using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GameOfLife
{
    public sealed class LifePatternBitmap : IDisposable
    {
        readonly NativeArray<LifeTile> tiles;

        public int2 Minimum { get; }
        public int2 Maximum { get; }

        public LifePatternBitmap(LifeTile[] source)
        {
            tiles = new NativeArray<LifeTile>(source, Allocator.TempJob);
            using var bounds = new NativeArray<int4>(1, Allocator.TempJob);
            new BoundsJob { Tiles = tiles, Bounds = bounds }.Run();
            Minimum = bounds[0].xy;
            Maximum = bounds[0].zw;
        }

        public void Draw(Texture2D texture, int2 origin, int2 canvas, int scale)
        {
            new DrawJob
            {
                Tiles = tiles,
                Pixels = texture.GetRawTextureData<Color32>(),
                Origin = origin,
                Canvas = canvas,
                Scale = scale,
                Width = texture.width
            }.Run();
            texture.Apply(false, false);
        }

        public void Dispose() => tiles.Dispose();

        [BurstCompile]
        struct BoundsJob : IJob
        {
            [ReadOnly] public NativeArray<LifeTile> Tiles;
            public NativeArray<int4> Bounds;

            public void Execute()
            {
                int2 minimum = new int2(int.MaxValue);
                int2 maximum = new int2(int.MinValue);
                for (int i = 0; i < Tiles.Length; i++)
                {
                    LifeTile tile = Tiles[i];
                    for (int row = 0; row < 16; row++)
                    {
                        uint bits = tile.Bits.GetRow(row);
                        if (bits == 0)
                            continue;
                        minimum = math.min(minimum, tile.Position + new int2(math.tzcnt(bits), row));
                        maximum = math.max(maximum, tile.Position + new int2(31 - math.lzcnt(bits), row));
                    }
                }
                Bounds[0] = new int4(minimum, maximum);
            }
        }

        [BurstCompile]
        struct DrawJob : IJob
        {
            [ReadOnly] public NativeArray<LifeTile> Tiles;
            public NativeArray<Color32> Pixels;
            public int2 Origin;
            public int2 Canvas;
            public int Scale;
            public int Width;

            public void Execute()
            {
                for (int i = 0; i < Pixels.Length; i++)
                    Pixels[i] = default;
                Color32 white = new Color32(255, 255, 255, 255);
                for (int i = 0; i < Tiles.Length; i++)
                {
                    LifeTile tile = Tiles[i];
                    int2 local = tile.Position - Origin;
                    int2 first = local / Scale;
                    int2 last = (local + 15) / Scale;
                    if (math.all(first == last))
                    {
                        Pixels[first.y * Width + first.x] = white;
                        continue;
                    }
                    for (int row = 0; row < 16; row++)
                    {
                        uint bits = tile.Bits.GetRow(row);
                        while (bits != 0)
                        {
                            int column = math.tzcnt(bits);
                            bits &= bits - 1;
                            int2 cell = local + new int2(column, row);
                            if (math.any(cell < 0) || math.any(cell >= Canvas))
                                continue;
                            int2 pixel = cell / Scale;
                            Pixels[pixel.y * Width + pixel.x] = white;
                        }
                    }
                }
            }
        }
    }
}
