using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UI;

namespace GameOfLife
{
    public sealed class LifeView : MonoBehaviour
    {
        public RawImage BoardImage;

        public const int MinVisibleCells = 8;
        public const int MaxVisibleCells = 16384;

        public int2 Origin { get; private set; } = new int2(-64, -64);
        public int VisibleCells { get; private set; } = 128;

        const int TextureSize = 1024;
        NativeArray<Color32> pixels;
        Texture2D texture;
        EntityManager manager;
        Entity boardEntity;
        uint renderedRevision;
        bool windowChanged = true;

        // 盖章预览（洋葱皮）与选区，只影响显示。
        NativeList<int2> ghost;
        int2[] ghostSource;
        bool hovering;
        int2 hoverCell;
        bool hasSelection;
        int2 selectionMin;
        int2 selectionMax;

        void Awake()
        {
            pixels = new NativeArray<Color32>(TextureSize * TextureSize, Allocator.Persistent);
            ghost = new NativeList<int2>(16, Allocator.Persistent);
            texture = new Texture2D(TextureSize, TextureSize, TextureFormat.RGBA32, false)
            {
                name = "Life board",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            BoardImage.texture = texture;
            BoardImage.uvRect = new Rect(0, 0, 1, 1);
        }

        public void Bind(EntityManager entityManager, Entity entity)
        {
            manager = entityManager;
            boardEntity = entity;
        }

        void LateUpdate()
        {
            LifeState state = manager.GetComponentData<LifeState>(boardEntity);
            if (!windowChanged && renderedRevision == state.Revision)
                return;

            new RenderPixelsJob
            {
                Cells = manager.GetBuffer<AliveCell>(boardEntity, true).AsNativeArray(),
                Pixels = pixels,
                Origin = Origin,
                VisibleCells = VisibleCells,
                Ghost = ghost.AsArray(),
                ShowGhost = hovering && ghostSource != null,
                HoverCell = hoverCell,
                HasSelection = hasSelection,
                SelectionMin = selectionMin,
                SelectionMax = selectionMax
            }.Run();
            texture.SetPixelData(pixels, 0);
            texture.Apply(false, false);
            renderedRevision = state.Revision;
            windowChanged = false;
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
            if (!bounds.Contains(local))
            {
                normalized = default;
                return false;
            }
            normalized = new float2((local.x - bounds.xMin) / bounds.width, (local.y - bounds.yMin) / bounds.height);
            return true;
        }

        /// <summary>设置跟随鼠标的半透明预览；传 null 关闭。</summary>
        public void SetGhost(int2[] cells)
        {
            if (cells == ghostSource)
                return;
            ghostSource = cells;
            ghost.Clear();
            if (cells != null)
                for (int i = 0; i < cells.Length; i++)
                    ghost.Add(cells[i]);
            windowChanged |= hovering;
        }

        public void SetHover(bool visible, int2 cell)
        {
            if (visible == hovering && (!visible || math.all(cell == hoverCell)))
                return;
            hovering = visible;
            hoverCell = cell;
            windowChanged |= ghostSource != null;
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
            int2 anchor = Origin + (int2)math.floor(normalized * VisibleCells);
            Origin = anchor - (int2)math.floor(normalized * nextSize);
            VisibleCells = nextSize;
            windowChanged = true;
        }

        void OnDestroy()
        {
            pixels.Dispose();
            ghost.Dispose();
            Destroy(texture);
        }

        [BurstCompile]
        struct RenderPixelsJob : IJob
        {
            [ReadOnly] public NativeArray<AliveCell> Cells;
            public NativeArray<Color32> Pixels;
            public int2 Origin;
            public int VisibleCells;
            [ReadOnly] public NativeArray<int2> Ghost;
            public bool ShowGhost;
            public int2 HoverCell;
            public bool HasSelection;
            public int2 SelectionMin;
            public int2 SelectionMax;

            static readonly Color32 Black = new Color32(0, 0, 0, 255);
            static readonly Color32 White = new Color32(255, 255, 255, 255);
            static readonly Color32 SelectionFill = new Color32(22, 22, 22, 255);
            static readonly Color32 GhostColor = new Color32(78, 78, 78, 255);
            static readonly Color32 DashDim = new Color32(110, 110, 110, 255);
            const int DashLength = 6;

            public void Execute()
            {
                for (int i = 0; i < Pixels.Length; i++)
                    Pixels[i] = Black;

                int2 selectionLow = SelectionMin - Origin;
                int2 selectionHigh = SelectionMax + 1 - Origin;
                int2 selectionBegin = default;
                int2 selectionEnd = default;
                if (HasSelection)
                {
                    selectionBegin = ToPixelBegin(selectionLow);
                    selectionEnd = ToPixelEnd(selectionHigh);
                    Fill(selectionBegin, selectionEnd, SelectionFill);
                }

                // 预览先画，已有活细胞随后覆盖为白色，与盖章不翻转已有细胞一致。
                if (ShowGhost)
                    for (int i = 0; i < Ghost.Length; i++)
                        FillCell(HoverCell + Ghost[i] - Origin, GhostColor);

                for (int i = 0; i < Cells.Length; i++)
                    FillCell(Cells[i].Position - Origin, White);

                // 选区延伸到窗口外时，只画真正落在窗口内的边。
                if (HasSelection)
                    DrawDashedBorder(selectionBegin, selectionEnd,
                        selectionLow >= 0 & selectionLow <= VisibleCells,
                        selectionHigh >= 0 & selectionHigh <= VisibleCells);
            }

            void FillCell(int2 cell, Color32 color)
            {
                if (math.all(cell >= 0) && math.all(cell < VisibleCells))
                    Fill(ToPixelBegin(cell), ToPixelEnd(cell + 1), color);
            }

            /// <summary>窗口相对格坐标转纹理像素：起点向下取整、终点向上取整，远景每格至少一像素。
            /// 先夹紧，避免远处选区乘法溢出。</summary>
            int2 ToPixelBegin(int2 cell)
            {
                cell = math.clamp(cell, 0, VisibleCells);
                return cell * TextureSize / VisibleCells;
            }

            int2 ToPixelEnd(int2 cell)
            {
                cell = math.clamp(cell, 0, VisibleCells);
                return (cell * TextureSize + VisibleCells - 1) / VisibleCells;
            }

            void Fill(int2 begin, int2 end, Color32 color)
            {
                for (int y = begin.y; y < end.y; y++)
                    for (int x = begin.x; x < end.x; x++)
                        Pixels[y * TextureSize + x] = color;
            }

            void DrawDashedBorder(int2 begin, int2 end, bool2 lowVisible, bool2 highVisible)
            {
                if (begin.x >= end.x || begin.y >= end.y)
                    return;
                int2 last = end - 1;
                for (int x = begin.x; x <= last.x; x++)
                {
                    if (lowVisible.y)
                        Dash(x, begin.y, x);
                    if (highVisible.y)
                        Dash(x, last.y, x);
                }
                for (int y = begin.y; y <= last.y; y++)
                {
                    if (lowVisible.x)
                        Dash(begin.x, y, y);
                    if (highVisible.x)
                        Dash(last.x, y, y);
                }
            }

            void Dash(int x, int y, int along)
            {
                Pixels[y * TextureSize + x] = (along / DashLength & 1) == 0 ? White : DashDim;
            }
        }
    }
}
