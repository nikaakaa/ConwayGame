using System.Collections.Generic;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

namespace GameOfLife
{
    /// <summary>
    /// 结构的文本格式转换：导出 RLE，导入 RLE、Plaintext（.cells）或本工程的 JSON。
    /// RLE 与 Plaintext 的行自上而下，本工程 y 向上，转换时翻转 y。
    /// </summary>
    public static class LifePatternText
    {
        const int RleLineLength = 70;

        /// <summary>按包围盒中心把世界坐标转成相对坐标，与保存结构使用同一规则。</summary>
        public static int2[] ToRelative(IReadOnlyList<int2> cells)
        {
            int2 minimum = cells[0];
            int2 maximum = minimum;
            for (int i = 1; i < cells.Count; i++)
            {
                minimum = math.min(minimum, cells[i]);
                maximum = math.max(maximum, cells[i]);
            }

            int2 origin = minimum + (maximum - minimum + 1) / 2;
            var relative = new int2[cells.Count];
            for (int i = 0; i < relative.Length; i++)
                relative[i] = cells[i] - origin;
            return relative;
        }

        public static string ToRle(string name, IReadOnlyList<int2> cells)
        {
            int2 minimum = cells[0];
            int2 maximum = minimum;
            var alive = new HashSet<int2>();
            for (int i = 0; i < cells.Count; i++)
            {
                minimum = math.min(minimum, cells[i]);
                maximum = math.max(maximum, cells[i]);
                alive.Add(cells[i]);
            }

            var text = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(name))
                text.Append("#N ").Append(name.Trim()).Append('\n');
            text.Append("x = ").Append(maximum.x - minimum.x + 1)
                .Append(", y = ").Append(maximum.y - minimum.y + 1)
                .Append(", rule = B3/S23\n");

            var body = new StringBuilder();
            int pendingRows = 0;
            for (int y = maximum.y; y >= minimum.y; y--)
            {
                // 行尾的死细胞省略；空行累积为一个带计数的 $。
                int lastAlive = int.MinValue;
                for (int x = maximum.x; x >= minimum.x; x--)
                    if (alive.Contains(new int2(x, y)))
                    {
                        lastAlive = x;
                        break;
                    }
                if (lastAlive == int.MinValue)
                {
                    pendingRows++;
                    continue;
                }
                if (body.Length != 0)
                    AppendRun(body, pendingRows + 1, '$');
                pendingRows = 0;

                int x0 = minimum.x;
                while (x0 <= lastAlive)
                {
                    bool state = alive.Contains(new int2(x0, y));
                    int run = 1;
                    while (x0 + run <= lastAlive && alive.Contains(new int2(x0 + run, y)) == state)
                        run++;
                    AppendRun(body, run, state ? 'o' : 'b');
                    x0 += run;
                }
            }
            body.Append('!');

            // RLE 约定每行不超过 70 个字符，只在记号之间换行。
            int lineStart = 0;
            int tokenStart = 0;
            for (int i = 0; i < body.Length; i++)
            {
                if (char.IsDigit(body[i]))
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

        static void AppendRun(StringBuilder body, int count, char tag)
        {
            if (count > 1)
                body.Append(count);
            body.Append(tag);
        }

        /// <summary>识别 RLE、Plaintext 或 JSON，成功时返回相对坐标结构。</summary>
        public static bool TryParse(string text, string fallbackName, out LifePattern pattern)
        {
            pattern = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            string name = fallbackName;
            List<int2> cells;
            if (trimmed[0] == '{')
                cells = ParseJson(trimmed, ref name);
            else if (LooksLikeRle(trimmed))
                cells = ParseRle(trimmed, ref name);
            else
                cells = ParsePlaintext(trimmed, ref name);

            if (cells == null || cells.Count == 0)
                return false;
            pattern = new LifePattern(name, "自定义", 0, int2.zero, ToRelative(cells), "从剪贴板导入的结构");
            return true;
        }

        static bool LooksLikeRle(string text)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                    continue;
                if (line[0] == 'x' || line[0] == 'X')
                    return true;
                // 没有头部时，只要出现 RLE 专用记号也按 RLE 处理。
                return line.IndexOf('$') >= 0 || line.IndexOf('!') >= 0 && line.IndexOf('o') >= 0;
            }
            return false;
        }

        static List<int2> ParseJson(string text, ref string name)
        {
            LifePatternData data;
            try
            {
                data = JsonUtility.FromJson<LifePatternData>(text);
            }
            catch (System.ArgumentException)
            {
                return null;
            }
            if (data == null || data.cells == null)
                return null;
            if (!string.IsNullOrWhiteSpace(data.name))
                name = data.name.Trim();
            return new List<int2>(data.cells);
        }

        static List<int2> ParseRle(string text, ref string name)
        {
            var cells = new List<int2>();
            int x = 0;
            int y = 0;
            int count = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;
                if (line[0] == '#')
                {
                    if (line.Length > 2 && (line[1] == 'N' || line[1] == 'n'))
                        name = line.Substring(2).Trim();
                    continue;
                }
                if (line[0] == 'x' || line[0] == 'X')
                    continue;

                foreach (char c in line)
                {
                    if (c >= '0' && c <= '9')
                    {
                        count = count * 10 + (c - '0');
                        continue;
                    }
                    int run = count == 0 ? 1 : count;
                    count = 0;
                    if (c == '!')
                        return cells;
                    if (c == '$')
                    {
                        y -= run;
                        x = 0;
                    }
                    else if (c == 'b' || c == '.')
                        x += run;
                    else if (c == 'o' || c >= 'A' && c <= 'X')
                    {
                        for (int i = 0; i < run; i++)
                            cells.Add(new int2(x + i, y));
                        x += run;
                    }
                    else if (!char.IsWhiteSpace(c))
                        return null;
                }
            }
            return cells;
        }

        static List<int2> ParsePlaintext(string text, ref string name)
        {
            var cells = new List<int2>();
            int y = 0;
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.TrimEnd('\r', ' ', '\t');
                if (line.StartsWith("!"))
                {
                    if (line.StartsWith("!Name:"))
                        name = line.Substring(6).Trim();
                    continue;
                }
                for (int x = 0; x < line.Length; x++)
                {
                    char c = line[x];
                    if (c == 'O' || c == 'o' || c == '*')
                        cells.Add(new int2(x, y));
                    else if (c != '.' && c != ' ')
                        return null;
                }
                y--;
            }
            return cells;
        }
    }
}
