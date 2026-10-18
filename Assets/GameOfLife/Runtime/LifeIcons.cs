namespace GameOfLife
{
    /// <summary>
    /// 界面所有方块图标的唯一来源，5×5（滑翔机 3×3），X 为活细胞。
    /// 一个形状只表示一种操作，主界面、结构库和开始界面共用；新增图标先确认与已有形状不相像，
    /// 并让活细胞数保持在 11–17 个左右，排在一起时疏密一致。
    /// </summary>
    public static class LifeIcons
    {
        public static readonly string[][] GliderPhases =
        {
            new[] { ".X.", "..X", "XXX" }, new[] { "X.X", ".XX", ".X." },
            new[] { "..X", "X.X", ".XX" }, new[] { "X..", ".XX", "XX." }
        };

        // 演化
        public static readonly string[] Time = { ".XXX.", "X.X.X", "X.XXX", "X...X", ".XXX." };
        public static readonly string[] Play = { "X....", "XXX..", "XXXXX", "XXX..", "X...." };
        public static readonly string[] Pause = { "XX.XX", "XX.XX", "XX.XX", "XX.XX", "XX.XX" };
        public static readonly string[] Step = { "X...X", "XX..X", "XXX.X", "XX..X", "X...X" };
        public static readonly string[] Slower = { ".X..X", "XX.XX", "XXXXX", "XX.XX", ".X..X" };
        public static readonly string[] Faster = { "X..X.", "XX.XX", "XXXXX", "XX.XX", "X..X." };
        public static readonly string[] Clear = { "XX.XX", ".XXX.", "..X..", ".XXX.", "XX.XX" };
        public static readonly string[] Reset = { "..X..", ".XXX.", "XX.XX", ".XXX.", "..X.." };

        // 工具
        public static readonly string[] Pencil = { "...XX", "..XXX", ".XXX.", "XXX..", "XX..." };
        public static readonly string[] Select = { "XX.XX", "X...X", ".....", "X...X", "XX.XX" };

        // 结构与世界
        public static readonly string[] Copy = { "XXX..", "X.XXX", "XXX.X", "..X.X", "..XXX" };
        public static readonly string[] Delete = { "XXXXX", ".X.X.", ".X.X.", ".X.X.", ".XXX." };
        public static readonly string[] Use = { "...XX", "...XX", ".X.XX", "XXXXX", ".X..." };
        public static readonly string[] Store = { "..X..", "..X..", "X.X.X", ".XXX.", "XXXXX" };
        public static readonly string[] Paste = { ".XXX.", "XX.XX", "X...X", "X...X", "XXXXX" };
        public static readonly string[] Folder = { "XX...", "XXXXX", "X...X", "X...X", "XXXXX" };
        public static readonly string[] Search = { "XXX..", "X.X..", "XXXX.", "...XX", "....X" };
        public static readonly string[] Library = { "X.X..", "X.X.X", "X.X.X", "X.X.X", "XXXXX" };

        // 世界
        public static readonly string[] Menu = { "XXXXX", ".....", "XXXXX", ".....", "XXXXX" };
        public static readonly string[] Save = { "XXXX.", "X..XX", "X...X", "X.X.X", "XXXXX" };
        public static readonly string[] Home = { "..X..", ".XXX.", "XXXXX", ".X.X.", ".XXX." };
        public static readonly string[] Help = { ".XXX.", "XX.XX", "..XX.", ".....", "..XX." };
        public static readonly string[] Quit = { "..X..", "X.X.X", "X.X.X", "X...X", ".XXX." };
    }
}
