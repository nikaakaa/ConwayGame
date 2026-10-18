using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Unity.Mathematics;
using UnityEngine;

namespace GameOfLife
{
    /// <summary>
    /// 整个世界的来源：可读写的存档与只读的地图，载入时替换当前世界。两者同样处理：
    /// 列表只读名称与预生成的缩略图，不读世界文件；选中后才在后台读取解析完整 RLE。
    /// 存档在 persistentDataPath/Saves，按保存时间从新到旧排列，第一个即“继续”；每个存档是同名的
    /// .rle（世界）、.json（名称）与 .png（缩略图），三者都在保存时于后台写好。
    /// 地图来自 StreamingAssets/LifePatterns，名称与缩略图由 Tools/MapPreviews/generate_previews.py 离线生成。
    /// </summary>
    public sealed class LifeWorldStore
    {
        const string AnchorPrefix = "#C life-anchor ";
        const int PreviewPixels = 128;
        // 列表项本身的细胞：一个方块地球。只在缺少缩略图时显示。
        static readonly int2[] IconCells =
        {
            new int2(1, 0), new int2(2, 0), new int2(3, 0),
            new int2(0, -1), new int2(2, -1), new int2(4, -1),
            new int2(0, -2), new int2(1, -2), new int2(2, -2), new int2(3, -2), new int2(4, -2),
            new int2(0, -3), new int2(2, -3), new int2(4, -3),
            new int2(1, -4), new int2(2, -4), new int2(3, -4)
        };

        readonly string savesPath;
        readonly string mapsPath;
        readonly List<LifePattern> saves = new List<LifePattern>();
        readonly List<LifePattern> maps = new List<LifePattern>();
        readonly Dictionary<LifePattern, WorldFile> files = new Dictionary<LifePattern, WorldFile>();
        // 缩略图按文件路径缓存，重新打开列表不再读取；存档覆盖或删除时移除。
        readonly Dictionary<string, Texture2D> previewCache = new Dictionary<string, Texture2D>();

        sealed class WorldFile
        {
            public string Path;
            public bool IsSave;
            public Texture2D Preview;
        }

        [Serializable]
        sealed class MapIndex
        {
            public MapInfo[] maps;
        }

        [Serializable]
        sealed class MapInfo
        {
            public string file;
            public string name;
        }

        [Serializable]
        sealed class SaveInfo
        {
            public string name;
        }

        /// <summary>存档列表项。每项只是名称与缩略图，不是完整结构；载入用 <see cref="LoadAsync"/>。</summary>
        public IReadOnlyList<LifePattern> Saves => saves;

        /// <summary>地图列表项，按文件名排序；同样只是名称与缩略图。</summary>
        public IReadOnlyList<LifePattern> Maps => maps;

        public LifeWorldStore(string savesPath)
        {
            this.savesPath = savesPath;
            mapsPath = Path.Combine(Application.streamingAssetsPath, "LifePatterns");
            Directory.CreateDirectory(savesPath);
            ReloadSaves();
        }

        public bool IsSave(LifePattern entry) => files.TryGetValue(entry, out WorldFile file) && file.IsSave;

        /// <summary>预生成的缩略图；没有时返回 null，由列表按图标生成。</summary>
        public Texture2D Preview(LifePattern entry) => files.TryGetValue(entry, out WorldFile file) ? file.Preview : null;

        /// <summary>重新列出存档：只读目录、名称文件与缩略图，不读世界文件。</summary>
        public void ReloadSaves()
        {
            // 路径与名称不变的存档沿用原列表项，控制器持有的当前存档在刷新后仍然有效。
            var previous = new Dictionary<string, LifePattern>();
            foreach (LifePattern save in saves)
                previous[files[save].Path] = save;
            Forget(saves);
            var worlds = new List<FileInfo>(new DirectoryInfo(savesPath).GetFiles("*.rle"));
            worlds.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            foreach (FileInfo world in worlds)
            {
                string infoPath = Path.ChangeExtension(world.FullName, ".json");
                string name = File.Exists(infoPath)
                    ? JsonUtility.FromJson<SaveInfo>(File.ReadAllText(infoPath, Encoding.UTF8)).name
                    : Path.GetFileNameWithoutExtension(world.Name);
                previous.TryGetValue(world.FullName, out LifePattern old);
                saves.Add(Entry(name, world.FullName, true, old != null && old.Name == name ? old : null));
            }
        }

        /// <summary>重新列出地图：只读目录、索引与缩略图，不读地图文件。缺少索引或缩略图时用文件名与统一图标。</summary>
        public void ReloadMaps()
        {
            Forget(maps);
            if (!Directory.Exists(mapsPath))
                return;
            var names = new Dictionary<string, string>();
            string indexPath = Path.Combine(mapsPath, "maps.json");
            if (File.Exists(indexPath))
                foreach (MapInfo info in JsonUtility.FromJson<MapIndex>(File.ReadAllText(indexPath, Encoding.UTF8)).maps)
                    names[info.file] = info.name;

            var paths = new List<string>(Directory.GetFiles(mapsPath, "*.rle"));
            paths.AddRange(Directory.GetFiles(mapsPath, "*.rle.gz"));
            paths.Sort(StringComparer.Ordinal);
            foreach (string filePath in paths)
            {
                if (!names.TryGetValue(Path.GetFileName(filePath), out string name))
                    name = Path.GetFileName(StemPath(filePath));
                maps.Add(Entry(name, filePath, false));
            }
        }

        void Forget(List<LifePattern> entries)
        {
            foreach (LifePattern entry in entries)
                files.Remove(entry);
            entries.Clear();
        }

        LifePattern Entry(string name, string filePath, bool isSave, LifePattern reuse = null)
        {
            LifePattern entry = reuse ?? new LifePattern(name, isSave ? "存档" : "地图", 0, int2.zero, IconCells, string.Empty);
            files.Add(entry, new WorldFile
            {
                Path = filePath,
                IsSave = isSave,
                Preview = LoadPreview(StemPath(filePath) + ".png")
            });
            return entry;
        }

        static string StemPath(string filePath)
        {
            if (filePath.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                filePath = Path.ChangeExtension(filePath, null);
            return Path.ChangeExtension(filePath, null);
        }

        Texture2D LoadPreview(string pngPath)
        {
            if (previewCache.TryGetValue(pngPath, out Texture2D cached))
                return cached;
            if (!File.Exists(pngPath))
                return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                name = "World preview " + Path.GetFileNameWithoutExtension(pngPath),
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            if (!texture.LoadImage(File.ReadAllBytes(pngPath)))
            {
                UnityEngine.Object.Destroy(texture);
                return null;
            }
            previewCache.Add(pngPath, texture);
            return texture;
        }

        void DropPreview(string pngPath)
        {
            if (previewCache.Remove(pngPath, out Texture2D texture))
                UnityEngine.Object.Destroy(texture);
        }

        /// <summary>
        /// 在后台读取并解析列表项对应的完整世界，同时求出盖章锚点：存档回到保存时的位置，地图放在原点。
        /// 结果不缓存，避免巨构常驻内存。
        /// </summary>
        public Task<(LifePattern World, int2 Anchor)> LoadAsync(LifePattern entry)
        {
            WorldFile file = files[entry];
            return Task.Run(() =>
            {
                // 只用纯 C# 解析 RLE，不调用 Unity API，可在后台线程执行。
                Stream stream = File.OpenRead(file.Path);
                if (file.Path.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
                    stream = new GZipStream(stream, CompressionMode.Decompress);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                if (!LifePatternText.TryParse(text, entry.Name, out LifePattern world, out string error))
                    throw new FormatException("无法加载“" + file.Path + "”：" + error);
                int2 anchor = file.IsSave && TryReadTopLeft(text, out int2 topLeft)
                    ? topLeft - TopLeft(world.Tiles)
                    : int2.zero;
                return (world, anchor);
            });
        }

        /// <summary>
        /// 在后台写入新存档或覆盖已有存档：世界 RLE、名称与缩略图。cells 由调用方在主线程收集后交出，不再修改。
        /// 返回世界文件路径；完成后在主线程 <see cref="ReloadSaves"/> 并用 <see cref="FindSave"/> 取得列表项。
        /// </summary>
        public Task<string> SaveAsync(string name, int2[] cells, LifePattern overwrite = null)
        {
            string filePath = overwrite != null && files.TryGetValue(overwrite, out WorldFile old) && old.IsSave
                ? old.Path
                : Path.Combine(savesPath, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".rle");
            string pngPath = Path.ChangeExtension(filePath, ".png");
            DropPreview(pngPath);
            // JsonUtility 留在主线程调用，后台只写文件。
            string info = JsonUtility.ToJson(new SaveInfo { name = name });
            return Task.Run(() =>
            {
                int2 minimum = cells[0];
                int2 maximum = minimum;
                for (int i = 1; i < cells.Length; i++)
                {
                    minimum = math.min(minimum, cells[i]);
                    maximum = math.max(maximum, cells[i]);
                }
                // 记录世界左上角（最小 x、最大 y），载入时与解析后结构自身的左上角相减得到锚点。
                string text = AnchorPrefix + minimum.x.ToString(CultureInfo.InvariantCulture) + ","
                    + maximum.y.ToString(CultureInfo.InvariantCulture) + "\n" + LifePatternText.ToRle(name, cells);
                File.WriteAllText(filePath, text, new UTF8Encoding(false));
                File.WriteAllText(Path.ChangeExtension(filePath, ".json"), info, new UTF8Encoding(false));
                LifePreviewPng.Write(pngPath, cells, PreviewPixels);
                return filePath;
            });
        }

        public LifePattern FindSave(string filePath)
        {
            foreach (LifePattern save in saves)
                if (files[save].Path == filePath)
                    return save;
            return null;
        }

        public void DeleteSave(LifePattern entry)
        {
            if (!files.TryGetValue(entry, out WorldFile file) || !file.IsSave)
                return;
            File.Delete(file.Path);
            File.Delete(Path.ChangeExtension(file.Path, ".json"));
            string pngPath = Path.ChangeExtension(file.Path, ".png");
            File.Delete(pngPath);
            DropPreview(pngPath);
            files.Remove(entry);
            saves.Remove(entry);
        }

        /// <summary>改名只写名称文件，世界文件不动，“继续”的顺序不变。返回替换后的列表项。</summary>
        public LifePattern RenameSave(LifePattern entry, string name)
        {
            if (!files.TryGetValue(entry, out WorldFile file) || !file.IsSave || name == entry.Name)
                return entry;
            File.WriteAllText(Path.ChangeExtension(file.Path, ".json"),
                JsonUtility.ToJson(new SaveInfo { name = name }), new UTF8Encoding(false));
            var renamed = new LifePattern(name, entry.Category, entry.Period, entry.Displacement, entry.Tiles, entry.Description);
            saves[saves.IndexOf(entry)] = renamed;
            files.Remove(entry);
            files.Add(renamed, file);
            return renamed;
        }

        // 位置记录写在文件头；读到第一行非注释即停止，不扫描主体。
        static bool TryReadTopLeft(string text, out int2 topLeft)
        {
            using var reader = new StringReader(text);
            string line;
            while ((line = reader.ReadLine()) != null && line.StartsWith("#", StringComparison.Ordinal))
            {
                if (!line.StartsWith(AnchorPrefix, StringComparison.Ordinal))
                    continue;
                string[] parts = line.Substring(AnchorPrefix.Length).Split(',');
                if (parts.Length == 2
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y))
                {
                    topLeft = new int2(x, y);
                    return true;
                }
            }
            // 外部放入、没有位置记录的文件按原点摆放。
            topLeft = default;
            return false;
        }

        /// <summary>结构的左上角（最小 x、最大 y），y 向上。</summary>
        static int2 TopLeft(LifeTile[] tiles)
        {
            int minimumX = int.MaxValue;
            int maximumY = int.MinValue;
            for (int i = 0; i < tiles.Length; i++)
                for (int row = 0; row < 16; row++)
                {
                    uint bits = tiles[i].Bits.GetRow(row);
                    if (bits == 0)
                        continue;
                    minimumX = math.min(minimumX, tiles[i].Position.x + math.tzcnt(bits));
                    maximumY = math.max(maximumY, tiles[i].Position.y + row);
                }
            return new int2(minimumX, maximumY);
        }
    }
}
