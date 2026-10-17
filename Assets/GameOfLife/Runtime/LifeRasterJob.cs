using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GameOfLife
{
    public struct LifeDrawTask
    {
        public int Root;
        public long X;
        public long Y;
    }

    [BurstCompile]
    public struct LifeRasterJob : IJob
    {
        [ReadOnly] public NativeArray<LifeNode> Nodes;
        public NativeArray<Color32> Pixels;
        public NativeList<LifeDrawTask> Stack;
        public LifeShape Shape;
        public LifeShape Ghost;
        public int2 Origin;
        public int VisibleCells;
        public bool ShowGhost;
        public bool HasSelection;
        public int2 SelectionMin;
        public int2 SelectionMax;

        static readonly Color32 Black = new Color32(0, 0, 0, 255);
        static readonly Color32 White = new Color32(255, 255, 255, 255);
        static readonly Color32 SelectionFill = new Color32(22, 22, 22, 255);
        static readonly Color32 GhostColor = new Color32(78, 78, 78, 255);
        static readonly Color32 DashDim = new Color32(110, 110, 110, 255);
        const int TextureSize = LifeView.TextureSize;
        const int DashLength = 6;

        public void Execute()
        {
            for (int i = 0; i < Pixels.Length; i++)
                Pixels[i] = Black;
            long lowX = (long)SelectionMin.x - Origin.x;
            long lowY = (long)SelectionMin.y - Origin.y;
            long highX = (long)SelectionMax.x + 1 - Origin.x;
            long highY = (long)SelectionMax.y + 1 - Origin.y;
            int2 selectionBegin = new int2(PixelBegin(lowX), PixelBegin(lowY));
            int2 selectionEnd = new int2(PixelEnd(highX), PixelEnd(highY));
            if (HasSelection)
                Fill(selectionBegin, selectionEnd, SelectionFill);
            if (ShowGhost)
                DrawTree(Ghost, GhostColor);
            DrawTree(Shape, White);
            if (HasSelection)
                DrawDashedBorder(selectionBegin, selectionEnd,
                    new bool2(lowX >= 0 && lowX <= VisibleCells, lowY >= 0 && lowY <= VisibleCells),
                    new bool2(highX >= 0 && highX <= VisibleCells, highY >= 0 && highY <= VisibleCells));
        }

        void DrawTree(LifeShape shape, Color32 color)
        {
            Stack.Clear();
            Stack.Add(new LifeDrawTask { Root = shape.Root, X = shape.X, Y = shape.Y });
            while (Stack.Length != 0)
            {
                LifeDrawTask task = Stack[Stack.Length - 1];
                Stack.RemoveAt(Stack.Length - 1);
                LifeNode node = Nodes[task.Root];
                if (node.Population == 0)
                    continue;
                long size = 1L << node.Level;
                long x = task.X - Origin.x;
                long y = task.Y - Origin.y;
                if (x >= VisibleCells || y >= VisibleCells || x + size <= 0 || y + size <= 0)
                    continue;
                if (size <= VisibleCells / TextureSize)
                {
                    Fill(new int2(PixelBegin(x), PixelBegin(y)),
                        new int2(PixelEnd(x + size), PixelEnd(y + size)), color);
                    continue;
                }
                if (node.Level == 4)
                {
                    for (int row = 0; row < 16; row++)
                    {
                        if (y + row < 0 || y + row >= VisibleCells)
                            continue;
                        uint bits = node.Bits.GetRow(row);
                        while (bits != 0)
                        {
                            int column = math.tzcnt(bits);
                            bits &= bits - 1;
                            if (x + column >= 0 && x + column < VisibleCells)
                                Fill(new int2(PixelBegin(x + column), PixelBegin(y + row)),
                                    new int2(PixelEnd(x + column + 1), PixelEnd(y + row + 1)), color);
                        }
                    }
                    continue;
                }
                long half = size >> 1;
                Stack.Add(new LifeDrawTask { Root = node.Children.x, X = task.X, Y = task.Y });
                Stack.Add(new LifeDrawTask { Root = node.Children.y, X = task.X + half, Y = task.Y });
                Stack.Add(new LifeDrawTask { Root = node.Children.z, X = task.X, Y = task.Y + half });
                Stack.Add(new LifeDrawTask { Root = node.Children.w, X = task.X + half, Y = task.Y + half });
            }
        }

        int PixelBegin(long cell)
        {
            cell = cell < 0 ? 0 : cell > VisibleCells ? VisibleCells : cell;
            return (int)(cell * TextureSize / VisibleCells);
        }

        int PixelEnd(long cell)
        {
            cell = cell < 0 ? 0 : cell > VisibleCells ? VisibleCells : cell;
            return (int)((cell * TextureSize + VisibleCells - 1) / VisibleCells);
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
