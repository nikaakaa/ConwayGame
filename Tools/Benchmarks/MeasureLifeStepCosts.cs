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

(string sum, string xor) Fingerprint(GameOfLife.LifeWorld world)
{
    ulong sum = 0, xor = 0;
    var nodes = world.Nodes;
    void Visit(int id, long x, long y)
    {
        var node = nodes[id];
        if (node.Population == 0)
            return;
        if (node.Level > 4)
        {
            long half = 1L << (node.Level - 1);
            for (int child = 0; child < 4; child++)
                Visit(node.Children[child], x + ((child & 1) != 0 ? half : 0), y + ((child & 2) != 0 ? half : 0));
            return;
        }
        for (int rowIndex = 0; rowIndex < 16; rowIndex++)
        {
            uint row = node.Bits.GetRow(rowIndex);
            while (row != 0)
            {
                int column = Unity.Mathematics.math.tzcnt(row);
                row &= row - 1;
                unchecked
                {
                    ulong key = ((ulong)(uint)(x + column) << 32) | (uint)(y + rowIndex);
                    key = (key ^ (key >> 30)) * 0xbf58476d1ce4e5b9UL;
                    key = (key ^ (key >> 27)) * 0x94d049bb133111ebUL;
                    key ^= key >> 31;
                    sum += key; xor ^= key;
                }
            }
        }
    }
    Visit(world.Shape.Root, world.Shape.X, world.Shape.Y);
    return (sum.ToString("x16"), xor.ToString("x16"));
}

using var preparation = new GameOfLife.LifeWorld();
var original = preparation.BuildShape(pattern.Tiles);
preparation.BeginEdit(); preparation.Stamp(original, Unity.Mathematics.int2.zero); preparation.EndEdit();
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
        if (!preparation.IsCollecting && preparation.NodeCount >= nextCollection)
            preparation.BeginCollection(retained);
        if (preparation.IsCollecting && preparation.ContinueCollection(1024))
            nextCollection = System.Math.Max(262144, preparation.NodeCount * 2);
        complete = preparation.ContinueAdvance(workBudget);
    }
    while (!complete && watch.Elapsed.TotalSeconds < 120);
    if (!complete)
        throw new System.TimeoutException("Hex preparation did not complete.");
}
double prepareMs = watch.Elapsed.TotalMilliseconds;
var initialFingerprint = Fingerprint(preparation);
var tiles = new System.Collections.Generic.List<GameOfLife.LifeTile>();
var sourceNodes = preparation.Nodes;
void CollectTiles(int id, long x, long y)
{
    var node = sourceNodes[id];
    if (node.Population == 0)
        return;
    if (node.Level == 4)
    {
        tiles.Add(new GameOfLife.LifeTile { Position = new Unity.Mathematics.int2((int)x, (int)y), Bits = node.Bits });
        return;
    }
    long half = 1L << (node.Level - 1);
    for (int child = 0; child < 4; child++)
        CollectTiles(node.Children[child], x + ((child & 1) != 0 ? half : 0), y + ((child & 2) != 0 ? half : 0));
}
CollectTiles(preparation.Shape.Root, preparation.Shape.X, preparation.Shape.Y);
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
                if (!world.IsCollecting && world.NodeCount >= threshold)
                    world.BeginCollection(roots);
                if (world.IsCollecting)
                {
                    collectionBatches++;
                    if (world.ContinueCollection(1024))
                        threshold = System.Math.Max(262144, world.NodeCount * 2);
                }
                complete = world.ContinueAdvance(workBudget);
            }
            while (!complete && watch.Elapsed.TotalSeconds < 30);
            if (!complete)
                throw new System.TimeoutException("Hex measured advance did not complete.");
            publications[publication] = watch.Elapsed.TotalMilliseconds;
        }
        var fingerprint = Fingerprint(world);
        outputs.Add(fingerprint.sum + fingerprint.xor + world.Population);
        results.Add(new { run, exponent, step, publications, collectionBatches,
            population = world.Population, fingerprint.sum, fingerprint.xor });
    }
var reference = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText("Temp/LifeBenchmarks/hex-reference-targets.json", System.Text.Encoding.UTF8));
var referenceSnapshots = (Newtonsoft.Json.Linq.JArray)reference["snapshots"];
var initialReference = referenceSnapshots.First(item => (long)item["generation"] == preparationGenerations);
var finalReference = referenceSnapshots.First(item => (long)item["generation"] == preparationGenerations + measuredGenerations);
bool initialReferenceMatches = preparation.Population == (long)initialReference["population"]
    && initialFingerprint.sum == (string)initialReference["sum"] && initialFingerprint.xor == (string)initialReference["xor"];
string finalReferenceValue = (string)finalReference["sum"] + (string)finalReference["xor"] + (long)finalReference["population"];
bool independentReferenceMatches = initialReferenceMatches && outputs.All(value => value == finalReferenceValue);
var result = new { inputPath, parsed, preparationGenerations, prepareMs, initialPopulation = preparation.Population,
    initialFingerprint.sum, initialFingerprint.xor, tileCount = tiles.Count, measuredGenerations,
    elapsedSeconds = clock.Elapsed.TotalSeconds, allOutputsMatch = outputs.All(value => value == outputs[0]), independentReferenceMatches, results,
    scope = "从正式显示器初态重建22071代，以同一棋盘和冷缓存轮换比较1/8/32/512代请求；每种完成同样512代并核对整个世界指纹。CPU区间含回收，不含重建输入、指纹遍历、绘图与UI，不是实际帧间隔。" };
System.IO.File.WriteAllText("Temp/LifeBenchmarks/hex-cadence-probe.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
return result;
