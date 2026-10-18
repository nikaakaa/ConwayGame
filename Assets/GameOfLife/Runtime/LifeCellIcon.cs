using System.Collections.Generic;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.UIElements;

namespace GameOfLife
{
    /// <summary>
    /// 用与棋盘相同的方块绘制的图标。形状为字符行（'X' 为活细胞）或坐标集合；
    /// 颜色取 USS 自定义属性 --cell-color，出现时方块按固定伪随机顺序逐个“出生”。
    /// </summary>
    public sealed class LifeCellIcon : VisualElement
    {
        static readonly CustomStyleProperty<Color> CellColorProperty = new CustomStyleProperty<Color>("--cell-color");
        const long BirthMilliseconds = 120;

        readonly List<int2> cells = new List<int2>();
        int2 size;
        Color color = Color.white;
        float birth = 1f;
        IVisualElementScheduledItem birthAnimation;

        public LifeCellIcon()
        {
            AddToClassList("life-cell-icon");
            pickingMode = PickingMode.Ignore;
            generateVisualContent += Draw;
            RegisterCallback<CustomStyleResolvedEvent>(evt =>
            {
                if (evt.customStyle.TryGetValue(CellColorProperty, out Color value) && value != color)
                {
                    color = value;
                    MarkDirtyRepaint();
                }
            });
        }

        /// <summary>字符行形状，第一行在上。</summary>
        public void SetShape(string[] rows)
        {
            cells.Clear();
            int width = 0;
            for (int y = 0; y < rows.Length; y++)
            {
                width = math.max(width, rows[y].Length);
                for (int x = 0; x < rows[y].Length; x++)
                    if (rows[y][x] == 'X')
                        cells.Add(new int2(x, y));
            }
            size = new int2(width, rows.Length);
            Sort();
            MarkDirtyRepaint();
        }

        public void SetPattern(LifePattern pattern)
        {
            cells.Clear();
            using var bitmap = new LifePatternBitmap(pattern.Tiles);
            int2 bounds = bitmap.Maximum - bitmap.Minimum + 1;
            int scale = math.max(1, (math.cmax(bounds) + 31) / 32);
            size = (bounds + scale - 1) / scale;
            using var pixels = new NativeArray<Color32>(size.x * size.y, Allocator.TempJob);
            bitmap.Draw(pixels, size.x, bitmap.Minimum, bounds, scale);
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].a != 0)
                    cells.Add(new int2(i % size.x, size.y - 1 - i / size.x));
            Sort();
            MarkDirtyRepaint();
        }

        // 出生顺序只依赖坐标，同一图标每次出现顺序一致。
        void Sort() => cells.Sort((a, b) => Hash(a).CompareTo(Hash(b)));

        static uint Hash(int2 cell) => math.hash(cell);

        /// <summary>从空白开始逐个点亮方块。</summary>
        public void PlayBirth()
        {
            birthAnimation?.Pause();
            long start = 0;
            birth = 0f;
            MarkDirtyRepaint();
            birthAnimation = schedule.Execute(timer =>
            {
                start += timer.deltaTime;
                birth = math.saturate(start / (float)BirthMilliseconds);
                MarkDirtyRepaint();
                if (birth >= 1f)
                    birthAnimation.Pause();
            }).Every(16);
        }

        void Draw(MeshGenerationContext context)
        {
            if (cells.Count == 0 || size.x == 0)
                return;
            Rect box = contentRect;
            // 方块边长取整像素，保持清晰；大结构缩小到不足一像素时按小数绘制。
            float cell = math.min(box.width / size.x, box.height / size.y);
            if (cell >= 2f)
                cell = math.floor(cell);
            float gap = cell >= 4f ? math.max(1f, math.round(cell * 0.18f)) : 0f;
            float2 offset = math.round(new float2(box.x + (box.width - cell * size.x) * 0.5f,
                box.y + (box.height - cell * size.y) * 0.5f));

            Painter2D painter = context.painter2D;
            painter.fillColor = color;
            int visible = (int)math.ceil(cells.Count * birth);
            for (int i = 0; i < visible; i++)
            {
                float2 p = offset + (float2)cells[i] * cell;
                painter.BeginPath();
                painter.MoveTo(new Vector2(p.x, p.y));
                painter.LineTo(new Vector2(p.x + cell - gap, p.y));
                painter.LineTo(new Vector2(p.x + cell - gap, p.y + cell - gap));
                painter.LineTo(new Vector2(p.x, p.y + cell - gap));
                painter.ClosePath();
                painter.Fill();
            }
        }
    }
}
