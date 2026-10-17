using Unity.Mathematics;

namespace GameOfLife
{
    public sealed class LifePattern
    {
        public string Name { get; }
        public string Category { get; }
        public int Period { get; }
        public int2 Displacement { get; }
        public int2[] Cells { get; }
        public string Description { get; }

        public LifePattern(string name, string category, int period, int2 displacement, int2[] cells, string description)
        {
            Name = name;
            Category = category;
            Period = period;
            Displacement = displacement;
            Cells = cells;
            Description = description;
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
