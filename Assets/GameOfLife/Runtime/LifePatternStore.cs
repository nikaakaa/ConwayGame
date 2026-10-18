using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEngine;

namespace GameOfLife
{
    [Serializable]
    public sealed class LifePatternData
    {
        public string name;
        public int2[] cells;
    }

    /// <summary>
    /// 结构库：目录中的每个 JSON、RLE 或 Plaintext 文件就是一个结构，没有内置与自定义之分。
    /// 首次使用时把 LifePatterns 的八种图案写成普通文件；之后可以与其他结构一样改名和删除。
    /// 列表按文件名排序，新文件以时间戳开头，因此顺序即创建顺序。
    /// </summary>
    public sealed class LifePatternStore
    {
        const string SeedMarker = ".seeded";

        readonly string directoryPath;
        readonly List<LifePattern> patterns = new List<LifePattern>();
        readonly Dictionary<LifePattern, string> files = new Dictionary<LifePattern, string>();
        // 目录签名：文件名、修改时间与大小。解析大型结构代价很高，签名不变时不重新读取。
        string signature;

        public IReadOnlyList<LifePattern> Patterns => patterns;

        public LifePatternStore(string directoryPath)
        {
            this.directoryPath = directoryPath;
            Directory.CreateDirectory(directoryPath);
            if (!File.Exists(Path.Combine(directoryPath, SeedMarker)))
                Seed();
            Reload();
            // 目录被清空时重新写入初始图案，保证库中始终有可用结构。
            if (patterns.Count == 0)
            {
                Seed();
                Reload();
            }
        }

        void Seed()
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            for (int i = 0; i < LifePatterns.Items.Length; i++)
            {
                string filePath = Path.Combine(directoryPath, stamp + "-" + i.ToString("00") + ".rle");
                File.WriteAllText(filePath, LifePatternText.ToRle(LifePatterns.Items[i]), new UTF8Encoding(false));
            }
            File.WriteAllText(Path.Combine(directoryPath, SeedMarker), string.Empty);
        }

        List<string> PatternPaths()
        {
            var paths = new List<string>();
            foreach (string filePath in Directory.EnumerateFiles(directoryPath))
            {
                string extension = Path.GetExtension(filePath);
                if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".rle", StringComparison.OrdinalIgnoreCase)
                    || extension.Equals(".cells", StringComparison.OrdinalIgnoreCase))
                    paths.Add(filePath);
            }
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        string Signature()
        {
            var text = new StringBuilder();
            foreach (string filePath in PatternPaths())
            {
                var info = new FileInfo(filePath);
                text.Append(info.Name).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('|').Append(info.Length).Append('\n');
            }
            return text.ToString();
        }

        /// <summary>目录在外部发生变化时才重新读取；返回是否重新读取。</summary>
        public bool ReloadIfChanged()
        {
            if (Signature() == signature)
                return false;
            Reload();
            return true;
        }

        public void Reload()
        {
            List<string> paths = PatternPaths();

            var loaded = new List<LifePattern>(paths.Count);
            var loadedFiles = new Dictionary<LifePattern, string>(paths.Count);
            foreach (string filePath in paths)
            {
                string text = File.ReadAllText(filePath, Encoding.UTF8);
                if (!LifePatternText.TryParse(text, Path.GetFileNameWithoutExtension(filePath), out LifePattern pattern, out string error))
                    throw new FormatException("无法加载结构文件“" + filePath + "”：" + error);
                loaded.Add(pattern);
                loadedFiles.Add(pattern, filePath);
            }
            patterns.Clear();
            patterns.AddRange(loaded);
            files.Clear();
            foreach (KeyValuePair<LifePattern, string> file in loadedFiles)
                files.Add(file.Key, file.Value);
            signature = Signature();
        }

        static string NewFileName(string extension)
            => DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + extension;

        public LifePattern Save(string name, IReadOnlyList<int2> cells)
        {
            var data = new LifePatternData
            {
                name = name,
                cells = LifePatternText.ToRelative(cells)
            };

            string filePath = Path.Combine(directoryPath, NewFileName(".json"));
            File.WriteAllText(filePath, JsonUtility.ToJson(data, true), new UTF8Encoding(false));

            var pattern = new LifePattern(data.name, "自定义", 0, int2.zero, data.cells, string.Empty);
            patterns.Add(pattern);
            files.Add(pattern, filePath);
            signature = Signature();
            return pattern;
        }

        /// <summary>导入结构；已有同名且细胞完全相同的结构时直接返回它。</summary>
        public LifePattern Import(LifePattern imported)
        {
            foreach (LifePattern pattern in patterns)
                if (pattern.Name == imported.Name && SameTiles(pattern, imported))
                    return pattern;
            string filePath = Path.Combine(directoryPath, NewFileName(".rle"));
            File.WriteAllText(filePath, LifePatternText.ToRle(imported), new UTF8Encoding(false));
            patterns.Add(imported);
            files.Add(imported, filePath);
            signature = Signature();
            return imported;
        }

        /// <summary>库中至少保留一个结构，供盖章与重置使用。</summary>
        public bool CanDelete(LifePattern pattern) => patterns.Count > 1 && files.ContainsKey(pattern);

        public void Delete(LifePattern pattern)
        {
            if (!CanDelete(pattern))
                return;
            File.Delete(files[pattern]);
            files.Remove(pattern);
            patterns.Remove(pattern);
            signature = Signature();
        }

        /// <summary>
        /// 改名只改文件中的名称字段并保留路径，列表位置不变。结构对象不可变，返回替换后的新对象。
        /// </summary>
        public LifePattern Rename(LifePattern pattern, string name)
        {
            if (!files.TryGetValue(pattern, out string filePath) || name == pattern.Name)
                return pattern;

            string extension = Path.GetExtension(filePath);
            string text = File.ReadAllText(filePath, Encoding.UTF8);
            if (extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                LifePatternData data = JsonUtility.FromJson<LifePatternData>(text);
                data.name = name;
                text = JsonUtility.ToJson(data, true);
            }
            else
                text = ReplaceHeader(text, extension.Equals(".cells", StringComparison.OrdinalIgnoreCase) ? "!Name:" : "#N", name);
            File.WriteAllText(filePath, text, new UTF8Encoding(false));

            var renamed = new LifePattern(name, pattern.Category, pattern.Period, pattern.Displacement, pattern.Tiles, pattern.Description);
            patterns[patterns.IndexOf(pattern)] = renamed;
            files.Remove(pattern);
            files.Add(renamed, filePath);
            signature = Signature();
            return renamed;
        }

        // 删除原有名称行，在首行写入新名称；其余内容原样保留。
        static string ReplaceHeader(string text, string prefix, string name)
        {
            var result = new StringBuilder(prefix).Append(' ').Append(name).Append('\n');
            foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    result.Append(line).Append('\n');
            return result.ToString().TrimEnd('\n') + "\n";
        }

        static bool SameTiles(LifePattern left, LifePattern right)
        {
            if (left.Population != right.Population || left.Tiles.Length != right.Tiles.Length)
                return false;
            var index = new Dictionary<int2, LifeBits>(left.Tiles.Length);
            for (int i = 0; i < left.Tiles.Length; i++)
                index.Add(left.Tiles[i].Position, left.Tiles[i].Bits);
            for (int i = 0; i < right.Tiles.Length; i++)
            {
                LifeTile tile = right.Tiles[i];
                if (!index.TryGetValue(tile.Position, out LifeBits bits)
                    || bits.A != tile.Bits.A || bits.B != tile.Bits.B
                    || bits.C != tile.Bits.C || bits.D != tile.Bits.D)
                    return false;
            }
            return true;
        }
    }
}
