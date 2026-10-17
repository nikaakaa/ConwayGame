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

    public sealed class LifePatternStore
    {
        readonly string directoryPath;
        readonly List<LifePattern> patterns = new List<LifePattern>();

        public IReadOnlyList<LifePattern> Patterns => patterns;

        public LifePatternStore(string directoryPath)
        {
            this.directoryPath = directoryPath;
            Directory.CreateDirectory(directoryPath);
            Reload();
        }

        public void Reload()
        {
            var loaded = new List<LifePattern>(LifePatterns.Items);

            foreach (string filePath in Directory.EnumerateFiles(directoryPath))
            {
                string extension = Path.GetExtension(filePath);
                if (!extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".rle", StringComparison.OrdinalIgnoreCase)
                    && !extension.Equals(".cells", StringComparison.OrdinalIgnoreCase))
                    continue;
                string text = File.ReadAllText(filePath, Encoding.UTF8);
                if (!LifePatternText.TryParse(text, Path.GetFileNameWithoutExtension(filePath), out LifePattern pattern, out string error))
                    throw new FormatException("无法加载结构文件“" + filePath + "”：" + error);
                loaded.Add(pattern);
            }
            patterns.Clear();
            patterns.AddRange(loaded);
        }

        public LifePattern Save(string name, IReadOnlyList<int2> cells)
        {
            var data = new LifePatternData
            {
                name = name,
                cells = LifePatternText.ToRelative(cells)
            };

            string filePath = Path.Combine(directoryPath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(filePath, JsonUtility.ToJson(data, true), new UTF8Encoding(false));

            var pattern = new LifePattern(data.name, "自定义", 0, int2.zero, data.cells, "保存的自定义结构");
            patterns.Add(pattern);
            return pattern;
        }

        public LifePattern Import(LifePattern imported)
        {
            foreach (LifePattern pattern in patterns)
                if (pattern.Name == imported.Name && SameTiles(pattern, imported))
                    return pattern;
            string filePath = Path.Combine(directoryPath, Guid.NewGuid().ToString("N") + ".rle");
            File.WriteAllText(filePath, LifePatternText.ToRle(imported), new UTF8Encoding(false));
            patterns.Add(imported);
            return imported;
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
