string inputPath = "Assets/StreamingAssets/LifePatterns/hexadecimal.rle";
string input = System.IO.File.ReadAllText(inputPath, System.Text.Encoding.UTF8);
bool parsed = GameOfLife.LifePatternText.TryParse(input, "Hexadecimal", out var pattern, out string parseError);
if (!parsed)
    return new { parsed, parseError };
const int preparationGenerations = 22071;
const int measuredGenerations = 512;
const int workBudget = 128;
var watch = new System.Diagnostics.Stopwatch();
var clock = System.Diagnostics.Stopwatch.StartNew();
var results = new System.Collections.Generic.List<object>();
var outputs = new System.Collections.Generic.List<string>();

(string sum, string xor) Fingerprint(Unity.Mathematics.int2[] cells)
{
    ulong sum = 0, xor = 0;
    foreach (var cell in cells)
    {
        unchecked
        {
            ulong key = ((ulong)(uint)cell.x << 32) | (uint)cell.y;
            key = (key ^ (key >> 30)) * 0xbf58476d1ce4e5b9UL;
            key = (key ^ (key >> 27)) * 0x94d049bb133111ebUL;
            key ^= key >> 31;
            sum += key; xor ^= key;
        }
    }
    return (sum.ToString("x16"), xor.ToString("x16"));
}

(Unity.Mathematics.int2 origin, int visibleCells) ConfigureViewport(GameOfLife.LifeWorld world, GameOfLife.LifeTile[] tiles, int generations)
{
    var min = tiles[0].Position;
    var max = min + 16;
    foreach (var tile in tiles)
    {
        min = Unity.Mathematics.math.min(min, tile.Position);
        max = Unity.Mathematics.math.max(max, tile.Position + 16);
    }
    min -= generations;
    max += generations;
    int visibleCells = 16;
    while (visibleCells < Unity.Mathematics.math.cmax(max - min))
        visibleCells *= 2;
    world.SetViewport(min, visibleCells);
    world.RefreshView();
    while (!world.IsViewCurrent)
        world.Pump(workBudget);
    return (min, visibleCells);
}

(Unity.Mathematics.int2[] cells, double milliseconds) ExportWholeWorld(GameOfLife.LifeWorld world)
{
    var exportWatch = System.Diagnostics.Stopwatch.StartNew();
    var request = world.CollectCellsAsync(false, default, default);
    while (!request.IsCompleted && exportWatch.Elapsed.TotalSeconds < 120)
    {
        if (world.IsCollecting)
            world.ContinueCollection(1024);
        world.Pump(workBudget);
    }
    if (!request.IsCompleted)
        throw new System.TimeoutException("全图导出未在 120 秒内完成。");
    return (request.GetAwaiter().GetResult(), exportWatch.Elapsed.TotalMilliseconds);
}

using var preparation = new GameOfLife.LifeWorld();
var original = preparation.BuildShape(pattern.Tiles);
preparation.BeginEdit(); preparation.Stamp(original, Unity.Mathematics.int2.zero); preparation.EndEdit();
var preparationViewport = ConfigureViewport(preparation, pattern.Tiles, preparationGenerations);
var retained = new System.Collections.Generic.List<GameOfLife.LifeShape> { original };
int nextCollection = 262144;
watch.Restart();
for (int exponent = 30; exponent >= 0; exponent--)
{
    if ((preparationGenerations & (1 << exponent)) == 0)
        continue;
    preparation.BeginAdvance(exponent);
    bool complete;
    do
    {
        if (!preparation.IsCollecting && preparation.NodeCount + preparation.RegionCacheCount >= nextCollection)
            preparation.BeginCollection(retained);
        if (preparation.IsCollecting && preparation.ContinueCollection(1024))
            nextCollection = System.Math.Max(262144, (preparation.NodeCount + preparation.RegionCacheCount) * 2);
        preparation.Pump(workBudget);
        complete = !preparation.IsAdvancing;
    }
    while (!complete && watch.Elapsed.TotalSeconds < 120);
    if (!complete)
        throw new System.TimeoutException("Hex preparation did not complete.");
}
double prepareMs = watch.Elapsed.TotalMilliseconds;
var initialExport = ExportWholeWorld(preparation);
var initialFingerprint = Fingerprint(initialExport.cells);
var tiles = new System.Collections.Generic.List<GameOfLife.LifeTile>();
System.Array.Sort(initialExport.cells, (left, right) =>
{
    int x = (left.x & ~15).CompareTo(right.x & ~15);
    return x != 0 ? x : (left.y & ~15).CompareTo(right.y & ~15);
});
foreach (var cell in initialExport.cells)
{
    var tilePosition = cell & ~15;
    if (tiles.Count == 0 || Unity.Mathematics.math.any(tiles[tiles.Count - 1].Position != tilePosition))
        tiles.Add(new GameOfLife.LifeTile { Position = tilePosition });
    var tile = tiles[tiles.Count - 1];
    tile.Bits.Set(cell.x & 15, cell.y & 15, true);
    tiles[tiles.Count - 1] = tile;
}
var tileArray = tiles.ToArray();
using (var writer = new System.IO.BinaryWriter(System.IO.File.Create("Temp/LifeBenchmarks/hex-22071-tiles.bin")))
{
    writer.Write(tileArray.Length);
    foreach (var tile in tileArray)
    {
        writer.Write(tile.Position.x); writer.Write(tile.Position.y);
        writer.Write(tile.Bits.A); writer.Write(tile.Bits.B); writer.Write(tile.Bits.C); writer.Write(tile.Bits.D);
    }
}
int[] exponents = { 9, 0, 5, 3 };
for (int run = 0; run < 5; run++)
    for (int index = 0; index < exponents.Length; index++)
    {
        int exponent = exponents[(index + run) % exponents.Length];
        using var world = new GameOfLife.LifeWorld();
        var shape = world.BuildShape(tileArray);
        world.BeginEdit(); world.Stamp(shape, Unity.Mathematics.int2.zero); world.EndEdit();
        var viewport = ConfigureViewport(world, tileArray, measuredGenerations);
        var roots = new System.Collections.Generic.List<GameOfLife.LifeShape> { shape };
        int threshold = 262144;
        int step = 1 << exponent;
        var publications = new double[measuredGenerations / step];
        int collectionBatches = 0;
        for (int publication = 0; publication < publications.Length; publication++)
        {
            watch.Restart();
            world.BeginAdvance(exponent);
            bool complete;
            do
            {
                if (!world.IsCollecting && world.NodeCount + world.RegionCacheCount >= threshold)
                    world.BeginCollection(roots);
                if (world.IsCollecting)
                {
                    collectionBatches++;
                    if (world.ContinueCollection(1024))
                        threshold = System.Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
                }
                world.Pump(workBudget);
                complete = !world.IsAdvancing;
            }
            while (!complete && watch.Elapsed.TotalSeconds < 30);
            if (!complete)
                throw new System.TimeoutException("Hex measured advance did not complete.");
            publications[publication] = watch.Elapsed.TotalMilliseconds;
        }
        var exported = ExportWholeWorld(world);
        var fingerprint = Fingerprint(exported.cells);
        long population = exported.cells.LongLength;
        outputs.Add(fingerprint.sum + fingerprint.xor + population);
        results.Add(new { run, exponent, step, publications, collectionBatches,
            population, fingerprint.sum, fingerprint.xor, originX = viewport.origin.x, originY = viewport.origin.y, viewport.visibleCells,
            exportMilliseconds = exported.milliseconds });
    }
var reference = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText("Temp/LifeBenchmarks/hex-reference-targets.json", System.Text.Encoding.UTF8));
var referenceSnapshots = (Newtonsoft.Json.Linq.JArray)reference["snapshots"];
var initialReference = referenceSnapshots.First(item => (long)item["generation"] == preparationGenerations);
var finalReference = referenceSnapshots.First(item => (long)item["generation"] == preparationGenerations + measuredGenerations);
bool initialReferenceMatches = initialExport.cells.LongLength == (long)initialReference["population"]
    && initialFingerprint.sum == (string)initialReference["sum"] && initialFingerprint.xor == (string)initialReference["xor"];
string finalReferenceValue = (string)finalReference["sum"] + (string)finalReference["xor"] + (long)finalReference["population"];
bool independentReferenceMatches = initialReferenceMatches && outputs.All(value => value == finalReferenceValue);
var result = new { inputPath, parsed, preparationGenerations, prepareMs, initialPopulation = initialExport.cells.LongLength,
    preparationOriginX = preparationViewport.origin.x, preparationOriginY = preparationViewport.origin.y,
    preparationVisibleCells = preparationViewport.visibleCells, preparationExportMilliseconds = initialExport.milliseconds,
    initialFingerprint.sum, initialFingerprint.xor, tileCount = tiles.Count, measuredGenerations,
    elapsedSeconds = clock.Elapsed.TotalSeconds, allOutputsMatch = outputs.All(value => value == outputs[0]), independentReferenceMatches, results,
    scope = "从正式显示器初态重建22071代，以同一棋盘和冷缓存轮换比较1/8/32/512代请求。各固定视野覆盖输入向外扩展测量代数的保守支持；publications 只计视野推进与回收，每种完成相同512代。之后显式全图导出，人口与完整指纹来自导出数组，导出求值与枚举另计 exportMilliseconds；指纹、重建输入、绘图与UI排除。该口径不把视野完成当作全图完成，也不能直接与旧根推进耗时计算加速比，不是实际帧间隔。" };
System.IO.File.WriteAllText("Temp/LifeBenchmarks/hex-cadence-probe.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
return result;
