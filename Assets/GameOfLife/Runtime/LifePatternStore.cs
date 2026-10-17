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
            patterns.Clear();
            patterns.AddRange(LifePatterns.Items);

            foreach (string filePath in Directory.EnumerateFiles(directoryPath, "*.json"))
            {
                string json = File.ReadAllText(filePath, Encoding.UTF8);
                LifePatternData data = JsonUtility.FromJson<LifePatternData>(json);
                patterns.Add(ToPattern(data));
            }
        }

        /// <summary>按包围盒中心转成相对坐标后写入新 GUID 文件，并加入列表。</summary>
        public LifePattern Save(string name, IReadOnlyList<int2> cells)
        {
            var data = new LifePatternData
            {
                name = name,
                cells = LifePatternText.ToRelative(cells)
            };

            string filePath = Path.Combine(directoryPath, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(filePath, JsonUtility.ToJson(data, true), new UTF8Encoding(false));

            LifePattern pattern = ToPattern(data);
            patterns.Add(pattern);
            return pattern;
        }

        /// <summary>导入结构：已有同名且坐标完全相同的结构时直接返回它，否则保存为新文件。</summary>
        public LifePattern Import(LifePattern imported)
        {
            foreach (LifePattern pattern in patterns)
                if (pattern.Name == imported.Name && SameCells(pattern.Cells, imported.Cells))
                    return pattern;
            return Save(imported.Name, imported.Cells);
        }

        static bool SameCells(int2[] a, int2[] b)
        {
            if (a.Length != b.Length)
                return false;
            for (int i = 0; i < a.Length; i++)
                if (math.any(a[i] != b[i]))
                    return false;
            return true;
        }

        static LifePattern ToPattern(LifePatternData data)
        {
            return new LifePattern(data.name, "自定义", 0, int2.zero, data.cells, "保存的自定义结构");
        }
    }
}
