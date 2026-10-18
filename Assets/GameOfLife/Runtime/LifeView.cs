using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
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
        public int2 DisplayedOrigin => displayedOrigin;
        public int DisplayedCells => displayedCells;

        public const int TextureSize = 1024;
        Texture2D texture;
        EntityManager manager;
        Entity boardEntity;
        LifeWorld simulation;
        NativeList<LifeDrawTask> drawStack;
        uint renderedVersion;
        int2 displayedOrigin = new int2(-64, -64);
        int displayedCells = 128;
        bool windowChanged = true;
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
            texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
            {
                name = "Life board",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            BoardImage.texture = texture;
            BoardImage.uvRect = new Rect(0, 0, 1, 1);
        }

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
            if (!windowChanged && renderedVersion == frame.Version)
                return;
            displayedOrigin = new int2((int)frame.ValidBounds.MinX, (int)frame.ValidBounds.MinY);
            displayedCells = (int)(frame.ValidBounds.MaxX - frame.ValidBounds.MinX);
            if (hovering)
                hoverCell = displayedOrigin + (int2)math.floor(hoverNormalized * displayedCells);
            LifeShape preview = ghost;
            preview.X += hoverCell.x;
            preview.Y += hoverCell.y;
            new LifeRasterJob
            {
                Nodes = simulation.Nodes,
                Shape = frame.Shape,
                Pixels = texture.GetRawTextureData<Color32>(),
                Stack = drawStack,
                Origin = displayedOrigin,
                VisibleCells = displayedCells,
                Ghost = preview,
                ShowGhost = hovering && hasGhost,
                HasSelection = hasSelection,
                SelectionMin = selectionMin,
                SelectionMax = selectionMax
            }.Run();
            texture.Apply(false, false);
            renderedVersion = frame.Version;
            windowChanged = false;
        }

        public bool TryGetCell(Vector2 screenPosition, Camera eventCamera, out int2 cell)
        {
            bool inside = TryGetNormalized(screenPosition, eventCamera, out float2 normalized);
            cell = displayedOrigin + (int2)math.floor(normalized * displayedCells);
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
            windowChanged |= hovering;
        }

        public void SetHover(Vector2 position, Camera eventCamera)
        {
            bool visible = TryGetNormalized(position, eventCamera, out hoverNormalized);
            int2 cell = displayedOrigin + (int2)math.floor(hoverNormalized * displayedCells);
            if (visible == hovering && (!visible || math.all(cell == hoverCell)))
                return;
            hovering = visible;
            hoverCell = cell;
            windowChanged |= hasGhost;
        }

        public void ClearHover()
        {
            hovering = false;
            windowChanged |= hasGhost;
        }

        public void SetSelection(bool visible, int2 minimum, int2 maximum)
        {
            if (visible == hasSelection && (!visible || math.all(minimum == selectionMin) && math.all(maximum == selectionMax)))
                return;
            hasSelection = visible;
            selectionMin = minimum;
            selectionMax = maximum;
            windowChanged = true;
        }

        public void Pan(int2 offset)
        {
            if (math.all(offset == 0))
                return;
            Origin += offset;
            windowChanged = true;
        }

        public void ResetWindow()
        {
            VisibleCells = 128;
            Origin = new int2(-64, -64);
            windowChanged = true;
        }

        public void Zoom(Vector2 position, Camera eventCamera, bool zoomIn)
        {
            if (!TryGetNormalized(position, eventCamera, out float2 normalized))
                return;
            int nextSize = math.clamp(zoomIn ? VisibleCells / 2 : VisibleCells * 2, MinVisibleCells, MaxVisibleCells);
            if (nextSize == VisibleCells)
                return;
            int2 anchor = displayedOrigin + (int2)math.floor(normalized * displayedCells);
            Origin = anchor - (int2)math.floor(normalized * nextSize);
            VisibleCells = nextSize;
            windowChanged = true;
        }

        void OnDestroy()
        {
            drawStack.Dispose();
            Destroy(texture);
        }
    }
}
