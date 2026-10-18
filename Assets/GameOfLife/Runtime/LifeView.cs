using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.UI;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace GameOfLife
{
    public sealed class LifeView : MonoBehaviour
    {
        public RawImage BoardImage;

        public const int MinVisibleCells = 8;
        public const int MaxVisibleCells = 67108864;

        public int2 Origin { get; private set; } = new int2(-64, -64);
        public int VisibleCells { get; private set; } = 128;
        public LifeRegion TextureBounds { get; private set; }

        public const int TextureSize = 1024;
        Texture2D texture;
        Texture2D backTexture;
        Texture2D overlayTexture;
        RawImage boardSurface;
        RawImage overlayImage;
        static readonly ProfilerMarker BoardRaster = new ProfilerMarker("LifeView.BoardRaster");
        static readonly ProfilerMarker BoardUpload = new ProfilerMarker("LifeView.BoardUpload");
        static readonly ProfilerMarker OverlayRaster = new ProfilerMarker("LifeView.OverlayRaster");
        static readonly ProfilerMarker OverlayUpload = new ProfilerMarker("LifeView.OverlayUpload");
        EntityManager manager;
        Entity boardEntity;
        LifeWorld simulation;
        NativeList<LifeDrawTask> drawStack;
        NativeList<LifeDrawTask> overlayStack;
        NativeArray<LifeRasterProgress> rasterProgress;
        LifeViewFrame rasterFrame;
        bool rasterActive;
        uint renderedVersion;
        bool overlayChanged = true;
        LifeShape ghost;
        bool hasGhost;
        bool hovering;
        float2 hoverNormalized;
        int2 hoverCell;
        bool hasSelection;
        int2 selectionMin;
        int2 selectionMax;
        long previousFrameTicks;

        void Awake()
        {
            drawStack = new NativeList<LifeDrawTask>(256, Allocator.Persistent);
            overlayStack = new NativeList<LifeDrawTask>(256, Allocator.Persistent);
            rasterProgress = new NativeArray<LifeRasterProgress>(1, Allocator.Persistent);
            texture = CreateTexture("Life board");
            backTexture = CreateTexture("Life board back buffer");
            overlayTexture = CreateTexture("Life overlays");
            BoardImage.texture = null;
            BoardImage.color = Color.black;
            boardSurface = CreateLayer("Life board surface", texture);
            boardSurface.transform.SetSiblingIndex(0);
            overlayImage = CreateLayer("Life overlays", overlayTexture);
            overlayImage.transform.SetSiblingIndex(1);
        }

        RawImage CreateLayer(string name, Texture2D layerTexture)
        {
            var layer = new GameObject(name, typeof(RectTransform), typeof(RawImage));
            layer.transform.SetParent(BoardImage.transform, false);
            var image = layer.GetComponent<RawImage>();
            image.texture = layerTexture;
            image.raycastTarget = false;
            image.enabled = false;
            RectTransform rect = image.rectTransform;
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return image;
        }

        static Texture2D CreateTexture(string name) => new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
        {
            name = name,
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        public void Bind(EntityManager entityManager, Entity entity, LifeWorld world)
        {
            manager = entityManager;
            boardEntity = entity;
            simulation = world;
            previousFrameTicks = Stopwatch.GetTimestamp();
            manager.SetComponentData(boardEntity, new LifeViewport
            {
                Origin = Origin,
                VisibleCells = VisibleCells,
                FrameSeconds = 1.0 / 60
            });
        }

        void LateUpdate()
        {
            long frameTicks = Stopwatch.GetTimestamp();
            manager.SetComponentData(boardEntity, new LifeViewport
            {
                Origin = Origin,
                VisibleCells = VisibleCells,
                FrameSeconds = (frameTicks - previousFrameTicks) / (double)Stopwatch.Frequency
            });
            previousFrameTicks = frameTicks;
            LifeViewFrame frame = simulation.ViewFrame;
            if (!rasterActive && renderedVersion != frame.Version)
            {
                rasterFrame = frame;
                rasterProgress[0] = default;
                drawStack.Clear();
                drawStack.Add(new LifeDrawTask { Root = frame.Shape.Root, X = frame.Shape.X, Y = frame.Shape.Y });
                simulation.SetRenderRoot(frame.Shape.Root);
                rasterActive = true;
            }
            if (rasterActive)
            {
                long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 1000;
                using (BoardRaster.Auto())
                {
                    do
                    {
                        new LifeRasterJob
                        {
                            Nodes = simulation.Nodes,
                            Pixels = backTexture.GetRawTextureData<Color32>(),
                            Stack = drawStack,
                            Progress = rasterProgress,
                            WorkBudget = 64,
                            Origin = new int2((int)rasterFrame.ValidBounds.MinX, (int)rasterFrame.ValidBounds.MinY),
                            VisibleCells = (int)(rasterFrame.ValidBounds.MaxX - rasterFrame.ValidBounds.MinX)
                        }.Run();
                    }
                    while (rasterProgress[0].Stage != LifeRasterStage.Complete && Stopwatch.GetTimestamp() < deadline);
                }
                if (rasterProgress[0].Stage == LifeRasterStage.Complete)
                {
                    using (BoardUpload.Auto())
                        backTexture.Apply(false, false);
                    Texture2D previous = texture;
                    texture = backTexture;
                    backTexture = previous;
                    boardSurface.texture = texture;
                    TextureBounds = rasterFrame.ValidBounds;
                    renderedVersion = rasterFrame.Version;
                    rasterActive = false;
                    simulation.SetRenderRoot(0);
                    PresentSurface();
                    overlayChanged = true;
                }
            }
            if (!overlayChanged)
                return;
            bool showOverlay = hovering && hasGhost || hasSelection;
            overlayImage.enabled = showOverlay;
            if (showOverlay)
            {
                LifeShape preview = ghost;
                preview.X += hoverCell.x;
                preview.Y += hoverCell.y;
                using (OverlayRaster.Auto())
                    new LifeOverlayRasterJob
                    {
                        Nodes = simulation.Nodes,
                        BoardPixels = texture.GetRawTextureData<Color32>(),
                        BoardBounds = TextureBounds,
                        Pixels = overlayTexture.GetRawTextureData<Color32>(),
                        Stack = overlayStack,
                        Origin = Origin,
                        VisibleCells = VisibleCells,
                        Ghost = preview,
                        ShowGhost = hovering && hasGhost,
                        HasSelection = hasSelection,
                        SelectionMin = selectionMin,
                        SelectionMax = selectionMax
                    }.Run();
                using (OverlayUpload.Auto())
                    overlayTexture.Apply(false, false);
            }
            overlayChanged = false;
        }

        public bool TryGetCell(Vector2 screenPosition, Camera eventCamera, out int2 cell)
        {
            bool inside = TryGetNormalized(screenPosition, eventCamera, out float2 normalized);
            cell = Origin + (int2)math.floor(normalized * VisibleCells);
            return inside;
        }

        bool TryGetNormalized(Vector2 screenPosition, Camera eventCamera, out float2 normalized)
        {
            RectTransform rect = BoardImage.rectTransform;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(rect, screenPosition, eventCamera, out Vector2 local);
            Rect bounds = rect.rect;
            normalized = new float2((local.x - bounds.xMin) / bounds.width, (local.y - bounds.yMin) / bounds.height);
            return bounds.Contains(local);
        }

        public void SetGhost(bool visible, LifeShape shape)
        {
            hasGhost = visible;
            ghost = shape;
            overlayChanged |= hovering;
        }

        public void SetHover(Vector2 position, Camera eventCamera)
        {
            bool visible = TryGetNormalized(position, eventCamera, out hoverNormalized);
            int2 cell = Origin + (int2)math.floor(hoverNormalized * VisibleCells);
            if (visible == hovering && (!visible || math.all(cell == hoverCell)))
                return;
            hovering = visible;
            hoverCell = cell;
            overlayChanged |= hasGhost;
        }

        public void ClearHover()
        {
            hovering = false;
            overlayChanged |= hasGhost;
        }

        public void SetSelection(bool visible, int2 minimum, int2 maximum)
        {
            if (visible == hasSelection && (!visible || math.all(minimum == selectionMin) && math.all(maximum == selectionMax)))
                return;
            hasSelection = visible;
            selectionMin = minimum;
            selectionMax = maximum;
            overlayChanged = true;
        }

        public void Pan(int2 offset)
        {
            if (math.all(offset == 0))
                return;
            Origin += offset;
            CameraChanged();
        }

        public void ResetWindow()
        {
            VisibleCells = 128;
            Origin = new int2(-64, -64);
            CameraChanged();
        }

        public void Zoom(Vector2 position, Camera eventCamera, bool zoomIn)
        {
            if (!TryGetNormalized(position, eventCamera, out float2 normalized))
                return;
            int nextSize = math.clamp(zoomIn ? VisibleCells / 2 : VisibleCells * 2, MinVisibleCells, MaxVisibleCells);
            if (nextSize == VisibleCells)
                return;
            int2 anchor = Origin + (int2)math.floor(normalized * VisibleCells);
            Origin = anchor - (int2)math.floor(normalized * nextSize);
            VisibleCells = nextSize;
            CameraChanged();
        }

        void CameraChanged()
        {
            if (hovering)
                hoverCell = Origin + (int2)math.floor(hoverNormalized * VisibleCells);
            overlayChanged = true;
            PresentSurface();
        }

        void PresentSurface()
        {
            long minX = math.max(TextureBounds.MinX, Origin.x);
            long minY = math.max(TextureBounds.MinY, Origin.y);
            long maxX = math.min(TextureBounds.MaxX, (long)Origin.x + VisibleCells);
            long maxY = math.min(TextureBounds.MaxY, (long)Origin.y + VisibleCells);
            bool intersects = minX < maxX && minY < maxY;
            boardSurface.enabled = intersects;
            if (!intersects)
                return;
            float textureCells = TextureBounds.MaxX - TextureBounds.MinX;
            boardSurface.uvRect = new Rect((minX - TextureBounds.MinX) / textureCells,
                (minY - TextureBounds.MinY) / textureCells, (maxX - minX) / textureCells, (maxY - minY) / textureCells);
            RectTransform rect = boardSurface.rectTransform;
            rect.anchorMin = new Vector2((minX - Origin.x) / (float)VisibleCells, (minY - Origin.y) / (float)VisibleCells);
            rect.anchorMax = new Vector2((maxX - Origin.x) / (float)VisibleCells, (maxY - Origin.y) / (float)VisibleCells);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        void OnDestroy()
        {
            drawStack.Dispose();
            overlayStack.Dispose();
            rasterProgress.Dispose();
            Destroy(texture);
            Destroy(backTexture);
            Destroy(overlayTexture);
        }
    }
}
