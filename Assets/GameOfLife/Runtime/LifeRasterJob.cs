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

    public enum LifeRasterStage
    {
        Clear,
        Draw,
        Complete
    }

    public struct LifeRasterProgress
    {
        public LifeRasterStage Stage;
        public int ClearCursor;
    }

    [BurstCompile]
    public struct LifeRasterJob : IJob
    {
        [ReadOnly] public NativeArray<LifeNode> Nodes;
        public NativeArray<Color32> Pixels;
        public NativeList<LifeDrawTask> Stack;
        public NativeArray<LifeRasterProgress> Progress;
        public int WorkBudget;
        public int2 Origin;
        public int VisibleCells;

        public void Execute()
        {
            LifeRasterProgress progress = Progress[0];
            var raster = new LifeRasterizer
            {
                Nodes = Nodes, Pixels = Pixels, Stack = Stack, Origin = Origin, VisibleCells = VisibleCells
            };
            for (int work = 0; work < WorkBudget && progress.Stage != LifeRasterStage.Complete; work++)
            {
                if (progress.Stage == LifeRasterStage.Clear)
                {
                    int end = math.min(progress.ClearCursor + 1024, Pixels.Length);
                    raster.Clear(progress.ClearCursor, end, new Color32(0, 0, 0, 255));
                    progress.ClearCursor = end;
                    if (end == Pixels.Length)
                        progress.Stage = LifeRasterStage.Draw;
                }
                else
                {
                    raster.DrawNext(new Color32(255, 255, 255, 255), false);
                    if (Stack.Length == 0)
                        progress.Stage = LifeRasterStage.Complete;
                }
            }
            Progress[0] = progress;
        }
    }

    [BurstCompile]
    public struct LifeOverlayRasterJob : IJob
    {
        [ReadOnly] public NativeArray<LifeNode> Nodes;
        [ReadOnly] public NativeArray<Color32> BoardPixels;
        public LifeRegion BoardBounds;
        public NativeArray<Color32> Pixels;
        public NativeList<LifeDrawTask> Stack;
        public LifeShape Ghost;
        public int2 Origin;
        public int VisibleCells;
        public bool ShowGhost;
        public bool HasSelection;
        public int2 SelectionMin;
        public int2 SelectionMax;

        static readonly Color32 SelectionFill = new Color32(22, 22, 22, 255);
        static readonly Color32 GhostColor = new Color32(78, 78, 78, 255);

        public void Execute()
        {
            var raster = new LifeRasterizer
            {
                Nodes = Nodes, BoardPixels = BoardPixels, BoardBounds = BoardBounds, Pixels = Pixels, Stack = Stack,
                Origin = Origin, VisibleCells = VisibleCells
            };
            raster.Clear(0, Pixels.Length, default);
            long lowX = (long)SelectionMin.x - Origin.x;
            long lowY = (long)SelectionMin.y - Origin.y;
            long highX = (long)SelectionMax.x + 1 - Origin.x;
            long highY = (long)SelectionMax.y + 1 - Origin.y;
            int2 selectionBegin = new int2(raster.PixelBegin(lowX), raster.PixelBegin(lowY));
            int2 selectionEnd = new int2(raster.PixelEnd(highX), raster.PixelEnd(highY));
            if (HasSelection)
                raster.Fill(selectionBegin, selectionEnd, SelectionFill, true);
            if (ShowGhost)
                raster.DrawTree(Ghost, GhostColor, true);
            if (HasSelection)
                raster.DrawDashedBorder(selectionBegin, selectionEnd,
                    new bool2(lowX >= 0 && lowX <= VisibleCells, lowY >= 0 && lowY <= VisibleCells),
                    new bool2(highX >= 0 && highX <= VisibleCells, highY >= 0 && highY <= VisibleCells));
        }
    }

    struct LifeRasterizer
    {
        public NativeArray<LifeNode> Nodes;
        public NativeArray<Color32> BoardPixels;
        public LifeRegion BoardBounds;
        public NativeArray<Color32> Pixels;
        public NativeList<LifeDrawTask> Stack;
        public int2 Origin;
        public int VisibleCells;

        const int TextureSize = LifeView.TextureSize;
        const int DashLength = 6;
        static readonly Color32 White = new Color32(255, 255, 255, 255);
        static readonly Color32 DashDim = new Color32(110, 110, 110, 255);

        public void Clear(int begin, int end, Color32 color)
        {
            for (int i = begin; i < end; i++)
                Pixels[i] = color;
        }

        public void DrawTree(LifeShape shape, Color32 color, bool preserveBoard)
        {
            Stack.Clear();
            Stack.Add(new LifeDrawTask { Root = shape.Root, X = shape.X, Y = shape.Y });
            while (Stack.Length != 0)
                DrawNext(color, preserveBoard);
        }

        public void DrawNext(Color32 color, bool preserveBoard)
        {
            LifeDrawTask task = Stack[Stack.Length - 1];
            Stack.RemoveAt(Stack.Length - 1);
            LifeNode node = Nodes[task.Root];
            if (node.Population == 0)
                return;
            long size = 1L << node.Level;
            long x = task.X - Origin.x;
            long y = task.Y - Origin.y;
            if (x >= VisibleCells || y >= VisibleCells || x + size <= 0 || y + size <= 0)
                return;
            if (size <= VisibleCells / TextureSize)
            {
                Fill(new int2(PixelBegin(x), PixelBegin(y)),
                    new int2(PixelEnd(x + size), PixelEnd(y + size)), color, preserveBoard);
                return;
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
                                new int2(PixelEnd(x + column + 1), PixelEnd(y + row + 1)), color, preserveBoard);
                    }
                }
                return;
            }
            long half = size >> 1;
            Stack.Add(new LifeDrawTask { Root = node.Children.x, X = task.X, Y = task.Y });
            Stack.Add(new LifeDrawTask { Root = node.Children.y, X = task.X + half, Y = task.Y });
            Stack.Add(new LifeDrawTask { Root = node.Children.z, X = task.X, Y = task.Y + half });
            Stack.Add(new LifeDrawTask { Root = node.Children.w, X = task.X + half, Y = task.Y + half });
        }

        public int PixelBegin(long cell)
        {
            cell = cell < 0 ? 0 : cell > VisibleCells ? VisibleCells : cell;
            return (int)(cell * TextureSize / VisibleCells);
        }

        public int PixelEnd(long cell)
        {
            cell = cell < 0 ? 0 : cell > VisibleCells ? VisibleCells : cell;
            return (int)((cell * TextureSize + VisibleCells - 1) / VisibleCells);
        }

        public void Fill(int2 begin, int2 end, Color32 color, bool preserveBoard)
        {
            for (int y = begin.y; y < end.y; y++)
                for (int x = begin.x; x < end.x; x++)
                {
                    int pixel = y * TextureSize + x;
                    if (!preserveBoard || !BoardIsWhite(x, y))
                        Pixels[pixel] = color;
                }
        }

        bool BoardIsWhite(int x, int y)
        {
            long side = BoardBounds.MaxX - BoardBounds.MinX;
            long relativeX = ((long)Origin.x - BoardBounds.MinX) * TextureSize + (long)x * VisibleCells + VisibleCells / 2;
            long relativeY = ((long)Origin.y - BoardBounds.MinY) * TextureSize + (long)y * VisibleCells + VisibleCells / 2;
            if (relativeX < 0 || relativeY < 0 || relativeX >= side * TextureSize || relativeY >= side * TextureSize)
                return false;
            int boardX = (int)(relativeX / side);
            int boardY = (int)(relativeY / side);
            return BoardPixels[boardY * TextureSize + boardX].r != 0;
        }

        public void DrawDashedBorder(int2 begin, int2 end, bool2 lowVisible, bool2 highVisible)
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
