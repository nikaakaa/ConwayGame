using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

namespace GameOfLife
{
    public static class LifePatternText
    {
        const int RleLineLength = 70;

        struct RowBits
        {
            public int X;
            public int Y;
            public ushort Bits;
        }

        public static int2[] ToRelative(IReadOnlyList<int2> cells)
        {
            int2 minimum = cells[0];
            int2 maximum = minimum;
            for (int i = 1; i < cells.Count; i++)
            {
                minimum = math.min(minimum, cells[i]);
                maximum = math.max(maximum, cells[i]);
            }
            int2 origin = LifePatternBuilder.Center(minimum, maximum);
            var relative = new int2[cells.Count];
            for (int i = 0; i < relative.Length; i++)
                relative[i] = cells[i] - origin;
            return relative;
        }

        public static string ToRle(string name, IReadOnlyList<int2> cells)
        {
            return WriteRle(name, LifePatternBuilder.Pack(cells));
        }

        public static string ToRle(LifePattern pattern)
        {
            return WriteRle(pattern.Name, pattern.Tiles);
        }

        static string WriteRle(string name, LifeTile[] tiles)
        {
            var rows = new List<RowBits>();
            int minimumX = int.MaxValue;
            int maximumX = int.MinValue;
            for (int i = 0; i < tiles.Length; i++)
                for (int y = 0; y < 16; y++)
                {
                    ushort bits = tiles[i].Bits.GetRow(y);
                    if (bits == 0)
                        continue;
                    int x = tiles[i].Position.x;
                    minimumX = math.min(minimumX, x + math.tzcnt((uint)bits));
                    maximumX = math.max(maximumX, x + 31 - math.lzcnt((uint)bits));
                    rows.Add(new RowBits { X = x, Y = tiles[i].Position.y + y, Bits = bits });
                }
            rows.Sort((left, right) =>
            {
                int vertical = right.Y.CompareTo(left.Y);
                return vertical != 0 ? vertical : left.X.CompareTo(right.X);
            });

            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(name))
                text.Append("#N ").Append(name.Trim().Replace('\r', ' ').Replace('\n', ' ')).Append('\n');
            text.Append("x = ").Append((long)maximumX - minimumX + 1)
                .Append(", y = ").Append((long)rows[0].Y - rows[rows.Count - 1].Y + 1)
                .Append(", rule = B3/S23\n");

            var body = new StringBuilder();
            int previousY = rows[0].Y;
            int index = 0;
            while (index < rows.Count)
            {
                int y = rows[index].Y;
                if (index != 0)
                    AppendRun(body, (long)previousY - y, '$');
                long cursor = minimumX;
                long liveRun = 0;
                while (index < rows.Count && rows[index].Y == y)
                {
                    RowBits row = rows[index++];
                    uint bits = row.Bits;
                    while (bits != 0)
                    {
                        int first = math.tzcnt(bits);
                        int length = math.tzcnt(~(bits >> first));
                        long start = (long)row.X + first;
                        if (start != cursor)
                        {
                            if (liveRun != 0)
                                AppendRun(body, liveRun, 'o');
                            AppendRun(body, start - cursor, 'b');
                            liveRun = 0;
                        }
                        liveRun += length;
                        cursor = start + length;
                        bits &= ~(((1u << length) - 1u) << first);
                    }
                }
                AppendRun(body, liveRun, 'o');
                previousY = y;
            }
            body.Append('!');

            int lineStart = 0;
            int tokenStart = 0;
            for (int i = 0; i < body.Length; i++)
            {
                if (body[i] >= '0' && body[i] <= '9')
                    continue;
                if (i + 1 - lineStart > RleLineLength && tokenStart > lineStart)
                {
                    text.Append(body, lineStart, tokenStart - lineStart).Append('\n');
                    lineStart = tokenStart;
                }
                tokenStart = i + 1;
            }
            text.Append(body, lineStart, body.Length - lineStart).Append('\n');
            return text.ToString();
        }

        static void AppendRun(StringBuilder body, long count, char tag)
        {
            if (count > 1)
                body.Append(count);
            body.Append(tag);
        }

        public static bool TryParse(string text, string fallbackName, out LifePattern pattern, out string error)
        {
            pattern = null;
            if (string.IsNullOrWhiteSpace(text))
            {
                error = "没有可导入的结构文本";
                return false;
            }

            string trimmed = text.Trim();
            string name = fallbackName;
            var builder = new LifePatternBuilder();
            bool parsed;
            if (trimmed[0] == '{')
                parsed = ReadJson(trimmed, ref name, builder, out error);
            else if (LooksLikeRle(trimmed))
                parsed = ReadRle(trimmed, ref name, builder, out error);
            else
                parsed = ReadPlaintext(text, ref name, builder, out error);
            if (!parsed)
                return false;
            if (builder.IsEmpty)
            {
                error = "结构中没有活细胞";
                return false;
            }
            pattern = new LifePattern(name, "自定义", 0, int2.zero, builder.Build(true), "导入的自定义结构");
            error = string.Empty;
            return true;
        }

        static bool LooksLikeRle(string text)
        {
            using var reader = new StringReader(text);
            string raw;
            while ((raw = reader.ReadLine()) != null)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (line[0] == '!')
                    return false;
                if (line[0] == 'x' || line[0] == 'X')
                    return true;
                for (int i = 0; i < line.Length; i++)
                    if (line[i] == '$' || line[i] == '!' || line[i] == 'b' || line[i] >= '0' && line[i] <= '9')
                        return true;
            }
            return false;
        }

        static bool ReadJson(string text, ref string name, LifePatternBuilder builder, out string error)
        {
            LifePatternData data;
            try
            {
                data = JsonUtility.FromJson<LifePatternData>(text);
            }
            catch (ArgumentException exception)
            {
                error = "JSON 无法解析：" + exception.Message;
                return false;
            }
            if (data.cells == null)
            {
                error = "JSON 缺少 cells 坐标数组";
                return false;
            }
            if (!string.IsNullOrWhiteSpace(data.name))
                name = data.name.Trim();
            for (int i = 0; i < data.cells.Length; i++)
                builder.AddRun(data.cells[i].x, data.cells[i].y, 1);
            error = string.Empty;
            return true;
        }

        static bool ReadRle(string text, ref string name, LifePatternBuilder builder, out string error)
        {
            int width = 0;
            int height = 0;
            int x = 0;
            int row = 0;
            bool bodyStarted = false;
            int lineNumber = 0;
            using var reader = new StringReader(text);
            string raw;
            while ((raw = reader.ReadLine()) != null)
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line[0] == '#')
                {
                    if (line.Length > 2 && (line[1] == 'N' || line[1] == 'n'))
                    {
                        string title = line.Substring(2).Trim();
                        if (title.Length != 0)
                            name = title;
                    }
                    if (line.Length > 1 && line[1] == 'r' && !IsLifeRule(line.Substring(2).Trim()))
                    {
                        error = $"RLE 第 {lineNumber} 行指定了不支持的规则，仅支持 B3/S23";
                        return false;
                    }
                    continue;
                }
                if ((line[0] == 'x' || line[0] == 'X') && line.IndexOf('=') >= 0)
                {
                    if (bodyStarted || width != 0)
                    {
                        error = $"RLE 第 {lineNumber} 行的头部位置不正确或重复";
                        return false;
                    }
                    if (!ReadHeader(line, out width, out height, out error))
                        return false;
                    continue;
                }

                bodyStarted = true;
                int count = 0;
                bool hasCount = false;
                for (int i = 0; i < line.Length; i++)
                {
                    char c = line[i];
                    if (c >= '0' && c <= '9')
                    {
                        int digit = c - '0';
                        if (count > (int.MaxValue - digit) / 10)
                        {
                            error = $"RLE 第 {lineNumber} 行的游程次数超过整数坐标范围";
                            return false;
                        }
                        count = count * 10 + digit;
                        hasCount = true;
                        continue;
                    }
                    if (char.IsWhiteSpace(c))
                    {
                        if (!hasCount)
                            continue;
                        error = $"RLE 第 {lineNumber} 行的游程次数和标记之间不能插入空白";
                        return false;
                    }
                    if (c == '!')
                    {
                        if (!hasCount)
                        {
                            error = string.Empty;
                            return true;
                        }
                        error = $"RLE 第 {lineNumber} 行的结束标记不能带次数";
                        return false;
                    }

                    int run = hasCount ? count : 1;
                    count = 0;
                    hasCount = false;
                    if (run == 0)
                    {
                        error = $"RLE 第 {lineNumber} 行的游程次数必须大于零";
                        return false;
                    }
                    if (c == '$')
                    {
                        long next = (long)row + run;
                        if (next > int.MaxValue || height != 0 && next > height)
                        {
                            error = $"RLE 第 {lineNumber} 行的换行超出声明高度或整数坐标范围";
                            return false;
                        }
                        row = (int)next;
                        x = 0;
                        continue;
                    }

                    bool alive = c == 'o' || c == 'O';
                    if (!alive && c != 'b' && c != '.')
                    {
                        error = $"RLE 第 {lineNumber} 行含有不支持的标记“{c}”，仅支持二状态图案";
                        return false;
                    }
                    long end = (long)x + run;
                    if (end > int.MaxValue || width != 0 && (end > width || row >= height))
                    {
                        error = $"RLE 第 {lineNumber} 行的细胞超出声明尺寸或整数坐标范围";
                        return false;
                    }
                    if (alive)
                        builder.AddRun(x, -row, run);
                    x = (int)end;
                }
                if (hasCount)
                {
                    error = $"RLE 第 {lineNumber} 行的次数后缺少 b、o 或 $ 标记";
                    return false;
                }
            }
            error = "RLE 缺少结束标记 !";
            return false;
        }

        static bool ReadHeader(string line, out int width, out int height, out string error)
        {
            width = 0;
            height = 0;
            string rule = null;
            foreach (string field in line.Split(','))
            {
                int separator = field.IndexOf('=');
                if (separator <= 0)
                {
                    error = "RLE 头部必须使用 x = 宽度, y = 高度, rule = B3/S23";
                    return false;
                }
                string key = field.Substring(0, separator).Trim();
                string value = field.Substring(separator + 1).Trim();
                if (key.Equals("rule", StringComparison.OrdinalIgnoreCase))
                {
                    if (rule != null)
                    {
                        error = "RLE 头部重复指定 rule";
                        return false;
                    }
                    rule = value;
                    if (!IsLifeRule(rule))
                    {
                        error = "RLE 规则“" + rule + "”不受支持，仅支持 B3/S23";
                        return false;
                    }
                    continue;
                }
                if (!key.Equals("x", StringComparison.OrdinalIgnoreCase) && !key.Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    error = "RLE 头部含有未知字段“" + key + "”";
                    return false;
                }
                if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dimension) || dimension <= 0)
                {
                    error = "RLE 宽高必须是 1 至 2147483647 的整数";
                    return false;
                }
                if (key.Equals("x", StringComparison.OrdinalIgnoreCase))
                {
                    if (width != 0)
                    {
                        error = "RLE 头部重复指定 x";
                        return false;
                    }
                    width = dimension;
                }
                else
                {
                    if (height != 0)
                    {
                        error = "RLE 头部重复指定 y";
                        return false;
                    }
                    height = dimension;
                }
            }
            if (width == 0 || height == 0)
            {
                error = "RLE 头部缺少 x 或 y";
                return false;
            }
            error = string.Empty;
            return true;
        }

        static bool IsLifeRule(string rule)
        {
            return rule.Equals("B3/S23", StringComparison.OrdinalIgnoreCase)
                || rule.Equals("S23/B3", StringComparison.OrdinalIgnoreCase)
                || rule == "23/3"
                || rule.Equals("Life", StringComparison.OrdinalIgnoreCase);
        }

        static bool ReadPlaintext(string text, ref string name, LifePatternBuilder builder, out string error)
        {
            int y = 0;
            int lineNumber = 0;
            using var reader = new StringReader(text);
            string raw;
            while ((raw = reader.ReadLine()) != null)
            {
                lineNumber++;
                string line = raw.TrimEnd('\r', ' ', '\t');
                if (line.StartsWith("!", StringComparison.Ordinal))
                {
                    if (line.StartsWith("!Name:", StringComparison.OrdinalIgnoreCase))
                    {
                        string title = line.Substring(6).Trim();
                        if (title.Length != 0)
                            name = title;
                    }
                    continue;
                }
                int x = 0;
                while (x < line.Length)
                {
                    char c = line[x];
                    if (c == 'O' || c == 'o' || c == '*')
                    {
                        int start = x++;
                        while (x < line.Length && (line[x] == 'O' || line[x] == 'o' || line[x] == '*'))
                            x++;
                        builder.AddRun(start, y, x - start);
                    }
                    else if (c == '.' || c == ' ')
                        x++;
                    else
                    {
                        error = $"Plaintext 第 {lineNumber} 行含有无效字符“{c}”，活细胞使用 O，死亡使用 .";
                        return false;
                    }
                }
                y--;
            }
            error = string.Empty;
            return true;
        }
    }
}
