System.IO.Directory.CreateDirectory("Tools/Benchmarks/Work");
string inputPath = "Assets/StreamingAssets/LifePatterns/hexadecimal.rle";
string input = System.IO.File.ReadAllText(inputPath, System.Text.Encoding.UTF8);
bool parsed = GameOfLife.LifePatternText.TryParse(input, "Hexadecimal", out var pattern, out string parseError);
if (!parsed)
    return new { parsed, parseError };
const int preparationGenerations = 22071;
const int workBudget = 128;
var watch = new System.Diagnostics.Stopwatch();

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
using (var writer = new System.IO.BinaryWriter(System.IO.File.Create("Tools/Benchmarks/Work/hex-22071-tiles.bin")))
{
    writer.Write(tileArray.Length);
    foreach (var tile in tileArray)
    {
        writer.Write(tile.Position.x); writer.Write(tile.Position.y);
        writer.Write(tile.Bits.A); writer.Write(tile.Bits.B); writer.Write(tile.Bits.C); writer.Write(tile.Bits.D);
    }
}

return new { prepareMs, population = initialExport.cells.LongLength, initialFingerprint.sum, initialFingerprint.xor, tiles = tileArray.Length };
