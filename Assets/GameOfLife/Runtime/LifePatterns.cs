using System.Collections.Generic;
using Unity.Mathematics;

namespace GameOfLife
{
    public sealed class LifePattern
    {
        public string Name { get; }
        public string Category { get; }
        public int Period { get; }
        public int2 Displacement { get; }
        public LifeTile[] Tiles { get; }
        public long Population { get; }
        public string Description { get; }

        public LifePattern(string name, string category, int period, int2 displacement, int2[] cells, string description)
            : this(name, category, period, displacement, LifePatternBuilder.Pack(cells), description)
        {
        }

        public LifePattern(string name, string category, int period, int2 displacement, LifeTile[] tiles, string description)
        {
            Name = name;
            Category = category;
            Period = period;
            Displacement = displacement;
            Tiles = tiles;
            Description = description;
            long population = 0;
            for (int i = 0; i < tiles.Length; i++)
                population += tiles[i].Bits.Population;
            Population = population;
        }
    }

    internal sealed class LifePatternBuilder
    {
        readonly Dictionary<int2, LifeBits> tiles = new Dictionary<int2, LifeBits>();
        int2 minimum = new int2(int.MaxValue, int.MaxValue);
        int2 maximum = new int2(int.MinValue, int.MinValue);

        public bool IsEmpty => tiles.Count == 0;

        public static int2 Center(int2 minimum, int2 maximum)
        {
            return new int2(
                (int)((long)minimum.x + ((long)maximum.x - minimum.x + 1) / 2),
                (int)((long)minimum.y + ((long)maximum.y - minimum.y + 1) / 2));
        }

        public static LifeTile[] Pack(IReadOnlyList<int2> cells)
        {
            var builder = new LifePatternBuilder();
            for (int i = 0; i < cells.Count; i++)
                builder.AddRun(cells[i].x, cells[i].y, 1);
            return builder.Build(false);
        }

        public void AddRun(int x, int y, int count)
        {
            long end = (long)x + count;
            minimum = math.min(minimum, new int2(x, y));
            maximum = math.max(maximum, new int2((int)(end - 1), y));
            for (long cursor = x; cursor < end;)
            {
                int column = (int)cursor;
                int offset = column & 15;
                int length = math.min(16 - offset, (int)(end - cursor));
                ushort row = (ushort)(((1u << length) - 1u) << offset);
                OrRow(tiles, new int2(column & ~15, y & ~15), y & 15, row);
                cursor += length;
            }
        }

        public LifeTile[] Build(bool relative)
        {
            Dictionary<int2, LifeBits> result = tiles;
            int2 origin = relative ? Center(minimum, maximum) : int2.zero;
            if (math.any(origin != int2.zero))
            {
                result = new Dictionary<int2, LifeBits>(tiles.Count);
                foreach (KeyValuePair<int2, LifeBits> tile in tiles)
                    for (int y = 0; y < 16; y++)
                    {
                        ushort row = tile.Value.GetRow(y);
                        if (row == 0)
                            continue;
                        long x = (long)tile.Key.x - origin.x;
                        int targetY = (int)((long)tile.Key.y + y - origin.y);
                        long tileX = x & ~15L;
                        uint shifted = (uint)row << (int)(x & 15);
                        ushort left = (ushort)shifted;
                        ushort right = (ushort)(shifted >> 16);
                        if (left != 0)
                            OrRow(result, new int2((int)tileX, targetY & ~15), targetY & 15, left);
                        if (right != 0)
                            OrRow(result, new int2((int)(tileX + 16), targetY & ~15), targetY & 15, right);
                    }
            }

            var packed = new LifeTile[result.Count];
            int index = 0;
            foreach (KeyValuePair<int2, LifeBits> tile in result)
                packed[index++] = new LifeTile { Position = tile.Key, Bits = tile.Value };
            return packed;
        }

        static void OrRow(Dictionary<int2, LifeBits> target, int2 position, int y, ushort row)
        {
            target.TryGetValue(position, out LifeBits bits);
            ulong mask = (ulong)row << ((y & 3) * 16);
            switch (y >> 2)
            {
                case 0: bits.A |= mask; break;
                case 1: bits.B |= mask; break;
                case 2: bits.C |= mask; break;
                case 3: bits.D |= mask; break;
            }
            target[position] = bits;
        }
    }

    public static class LifePatterns
    {
        public static readonly LifePattern[] Items =
        {
            new LifePattern("Block", "稳定", 1, new int2(0, 0), new[]
            {
                new int2(-1, 0), new int2(0, 0),
                new int2(-1, -1), new int2(0, -1)
            }, "4 个活细胞组成方块，每代保持原样。"),

            new LifePattern("Beehive", "稳定", 1, new int2(0, 0), new[]
            {
                new int2(-1, 1), new int2(0, 1),
                new int2(-2, 0), new int2(1, 0),
                new int2(-1, -1), new int2(0, -1)
            }, "6 个活细胞组成蜂巢，每代保持原样。"),

            new LifePattern("Blinker", "震荡", 2, new int2(0, 0), new[]
            {
                new int2(-1, 0), new int2(0, 0), new int2(1, 0)
            }, "3 个活细胞从横线开始，每 2 代恢复原状。"),

            new LifePattern("Toad", "震荡", 2, new int2(0, 0), new[]
            {
                new int2(-1, 0), new int2(0, 0), new int2(1, 0),
                new int2(-2, -1), new int2(-1, -1), new int2(0, -1)
            }, "6 个活细胞组成错位的两排，每 2 代恢复原状。"),

            new LifePattern("Glider", "飞船", 4, new int2(1, -1), new[]
            {
                new int2(0, 1),
                new int2(1, 0),
                new int2(-1, -1), new int2(0, -1), new int2(1, -1)
            }, "5 个活细胞；每 4 代形状重复，向右、向下各移动 1 格。"),

            new LifePattern("Lightweight Spaceship", "飞船", 4, new int2(-2, 0), new[]
            {
                new int2(-1, 1), new int2(2, 1),
                new int2(-2, 0),
                new int2(-2, -1), new int2(2, -1),
                new int2(-2, -2), new int2(-1, -2), new int2(0, -2), new int2(1, -2)
            }, "9 个活细胞；每 4 代形状重复，向左移动 2 格。"),

            new LifePattern("Pulsar", "震荡", 3, new int2(0, 0), new[]
            {
                new int2(-4, 6), new int2(-3, 6), new int2(-2, 6), new int2(2, 6), new int2(3, 6), new int2(4, 6),
                new int2(-6, 4), new int2(-1, 4), new int2(1, 4), new int2(6, 4),
                new int2(-6, 3), new int2(-1, 3), new int2(1, 3), new int2(6, 3),
                new int2(-6, 2), new int2(-1, 2), new int2(1, 2), new int2(6, 2),
                new int2(-4, 1), new int2(-3, 1), new int2(-2, 1), new int2(2, 1), new int2(3, 1), new int2(4, 1),
                new int2(-4, -1), new int2(-3, -1), new int2(-2, -1), new int2(2, -1), new int2(3, -1), new int2(4, -1),
                new int2(-6, -2), new int2(-1, -2), new int2(1, -2), new int2(6, -2),
                new int2(-6, -3), new int2(-1, -3), new int2(1, -3), new int2(6, -3),
                new int2(-6, -4), new int2(-1, -4), new int2(1, -4), new int2(6, -4),
                new int2(-4, -6), new int2(-3, -6), new int2(-2, -6), new int2(2, -6), new int2(3, -6), new int2(4, -6)
            }, "从 48 个活细胞的对称相位开始，每 3 代原地恢复原状。"),

            new LifePattern("Pentadecathlon", "震荡", 15, new int2(0, 0), new[]
            {
                new int2(-3, 1), new int2(2, 1),
                new int2(-5, 0), new int2(-4, 0), new int2(-2, 0), new int2(-1, 0),
                new int2(0, 0), new int2(1, 0), new int2(3, 0), new int2(4, 0),
                new int2(-3, -1), new int2(2, -1)
            }, "从 12 个活细胞的相位开始，每 15 代原地恢复原状。")
        };
    }
}
