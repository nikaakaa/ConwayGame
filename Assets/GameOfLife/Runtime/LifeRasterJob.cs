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
        public int TextureSize;
        public int2 Origin;
        public int VisibleCells;

        public void Execute()
        {
            LifeRasterProgress progress = Progress[0];
            var raster = new LifeRasterizer
            {
                Nodes = Nodes, Pixels = Pixels, Stack = Stack, TextureSize = TextureSize,
                Origin = Origin, VisibleCells = VisibleCells
            };
            for (int work = 0; work < WorkBudget && progress.Stage != LifeRasterStage.Complete; work++)
            {
                if (progress.Stage == LifeRasterStage.Clear)
                {
                    int end = math.min(progress.ClearCursor + 1024, Pixels.Length);
                    raster.Clear(progress.ClearCursor, end, default);
                    progress.ClearCursor = end;
                    if (end == Pixels.Length)
                        progress.Stage = LifeRasterStage.Draw;
                }
                else
                {
                    raster.DrawNext(new Color32(255, 255, 255, 255));
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
                Nodes = Nodes, Pixels = Pixels, Stack = Stack, TextureSize = LifeView.TextureSize,
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
                raster.Fill(selectionBegin, selectionEnd, SelectionFill);
            if (ShowGhost)
                raster.DrawTree(Ghost, GhostColor);
        }
    }

    struct LifeRasterizer
    {
        public NativeArray<LifeNode> Nodes;
        public NativeArray<Color32> Pixels;
        public NativeList<LifeDrawTask> Stack;
        public int TextureSize;
        public int2 Origin;
        public int VisibleCells;

        public void Clear(int begin, int end, Color32 color)
        {
            for (int i = begin; i < end; i++)
                Pixels[i] = color;
        }

        public void DrawTree(LifeShape shape, Color32 color)
        {
            Stack.Clear();
            Stack.Add(new LifeDrawTask { Root = shape.Root, X = shape.X, Y = shape.Y });
            while (Stack.Length != 0)
                DrawNext(color);
        }

        public void DrawNext(Color32 color)
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
                    new int2(PixelEnd(x + size), PixelEnd(y + size)), color);
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
                                new int2(PixelEnd(x + column + 1), PixelEnd(y + row + 1)), color);
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

        public void Fill(int2 begin, int2 end, Color32 color)
        {
            for (int y = begin.y; y < end.y; y++)
                for (int x = begin.x; x < end.x; x++)
                    Pixels[y * TextureSize + x] = color;
        }
    }
}
