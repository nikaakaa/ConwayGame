string[] request = System.IO.File.ReadAllLines("Temp/LifeBenchmarks/request.txt", System.Text.Encoding.UTF8);
string inputPath = request[0];
int repetitions = int.Parse(request[1], System.Globalization.CultureInfo.InvariantCulture);
string expectedSum = request[2];
string expectedXor = request[3];
string input = System.IO.File.ReadAllText(inputPath, System.Text.Encoding.UTF8);
var parseWatch = System.Diagnostics.Stopwatch.StartNew();
bool parsed = GameOfLife.LifePatternText.TryParse(input, "Life-in-Life", out var pattern, out string parseError);
parseWatch.Stop();
if (!parsed)
    return new { parsed, parseError };

var results = new List<object>();
(string sum, string xor) Fingerprint(GameOfLife.LifeWorld world)
{
    ulong sum = 0, xor = 0;
    var nodes = world.Nodes;
    var shape = world.Shape;
    var stack = new Stack<(int root, long x, long y)>();
    stack.Push((shape.Root, shape.X, shape.Y));
    while (stack.Count != 0)
    {
        var item = stack.Pop();
        var node = nodes[item.root];
        if (node.Population == 0)
            continue;
        if (node.Level == 4)
        {
            for (int y = 0; y < 16; y++)
            {
                uint row = node.Bits.GetRow(y);
                while (row != 0)
                {
                    int x = Unity.Mathematics.math.tzcnt(row);
                    row &= row - 1;
                    unchecked
                    {
                        ulong key = ((ulong)(uint)(item.x + x) << 32) | (uint)(item.y + y);
                        key = (key ^ (key >> 30)) * 0xbf58476d1ce4e5b9UL;
                        key = (key ^ (key >> 27)) * 0x94d049bb133111ebUL;
                        key ^= key >> 31;
                        sum += key;
                        xor ^= key;
                    }
                }
            }
            continue;
        }
        long half = 1L << (node.Level - 1);
        for (int i = 0; i < 4; i++)
            stack.Push((node.Children[i], item.x + ((i & 1) * half), item.y + ((i >> 1) * half)));
    }
    return (sum.ToString("x16"), xor.ToString("x16"));
}
int failures = 0;
const int budget = 128;
const double limitSeconds = 60;
int[] exponents = { 15, 11, 9 };
for (int run = 0; run < repetitions; run++)
{
    using var world = new GameOfLife.LifeWorld();
    using var roots = new Unity.Collections.NativeList<GameOfLife.LifeShape>(2, Unity.Collections.Allocator.Persistent);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    var initial = world.BuildShape(pattern.Tiles);
    watch.Stop();
    double buildMs = watch.Elapsed.TotalMilliseconds;
    roots.Add(initial);
    GameOfLife.LifeShape coldResult = default;
    long coldPopulation = 0;
    for (int cache = 0; cache < 2; cache++)
    {
        world.Clear();
        world.BeginEdit();
        world.Stamp(initial, Unity.Mathematics.int2.zero);
        world.EndEdit();
        int batches = 0;
        int collections = 0;
        int nextCollection = 262144;
        int peakNodes = world.NodeCount;
        double maxBatchMs = 0;
        double maxCollectionBatchMs = 0;
        long generations = 0;
        long hits = world.CacheHits;
        long misses = world.CacheMisses;
        var batchWatch = new System.Diagnostics.Stopwatch();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        watch.Restart();
        bool complete = true;
        foreach (int exponent in exponents)
        {
            world.BeginAdvance(exponent);
            bool finished;
            do
            {
                if (!world.IsCollecting && world.NodeCount >= nextCollection)
                {
                    world.BeginCollection(roots.AsArray());
                    collections++;
                }
                if (world.IsCollecting)
                {
                    batchWatch.Restart();
                    bool collected = world.ContinueCollection(4096);
                    batchWatch.Stop();
                    maxCollectionBatchMs = Math.Max(maxCollectionBatchMs, batchWatch.Elapsed.TotalMilliseconds);
                    if (collected)
                        nextCollection = Math.Max(262144, world.NodeCount * 2);
                    finished = false;
                }
                else
                {
                    batchWatch.Restart();
                    finished = world.ContinueAdvance(budget);
                    batchWatch.Stop();
                    maxBatchMs = Math.Max(maxBatchMs, batchWatch.Elapsed.TotalMilliseconds);
                    batches++;
                }
                peakNodes = Math.Max(peakNodes, world.NodeCount);
            } while (!finished && watch.Elapsed.TotalSeconds < limitSeconds);
            if (!finished)
            {
                complete = false;
                break;
            }
            generations += 1L << exponent;
        }
        watch.Stop();
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        bool sameAsCold = true;
        if (cache == 0)
        {
            coldResult = world.Shape;
            coldPopulation = world.Population;
            roots.Add(coldResult);
        }
        else
            sameAsCold = world.Shape.Root == coldResult.Root && world.Shape.X == coldResult.X
                && world.Shape.Y == coldResult.Y && world.Population == coldPopulation;
        var fingerprint = Fingerprint(world);
        bool referenceMatches = fingerprint.sum == expectedSum && fingerprint.xor == expectedXor;
        bool valid = complete && sameAsCold && generations == 35328 && referenceMatches;
        if (!valid)
            failures++;
        results.Add(new {
            run, cache = cache == 0 ? "cold" : "warm", initialPopulation = pattern.Population,
            actualPopulation = world.Population, generations, milliseconds = watch.Elapsed.TotalMilliseconds,
            buildMs, maxBatchMs, maxCollectionBatchMs, batches, collections, peakNodes,
            nodes = world.NodeCount, cacheEntries = world.CacheCount,
            cacheHits = world.CacheHits - hits, cacheMisses = world.CacheMisses - misses,
            allocatedBytes = allocated, fingerprint.sum, fingerprint.xor, referenceMatches, complete, sameAsCold, valid
        });
        if (!complete)
            world.CancelAdvance();
    }
}
return new {
    unity = Application.unityVersion, burstEnabled = Unity.Burst.BurstCompiler.IsEnabled,
    processor = SystemInfo.processorType, inputPath, parsed, parseMs = parseWatch.Elapsed.TotalMilliseconds,
    initialPopulation = pattern.Population, tileCount = pattern.Tiles.Length,
    repetitions, budget, limitSeconds, failures, results
};
