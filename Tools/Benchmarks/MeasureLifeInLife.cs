const int gcAllocationSampleCapacity = 4096;
Unity.Profiling.ProfilerRecorder StartGcAllocationRecording() =>
    Unity.Profiling.ProfilerRecorder.StartNew(
        Unity.Profiling.ProfilerCategory.Internal, "GC.Alloc", gcAllocationSampleCapacity,
        Unity.Profiling.ProfilerRecorderOptions.CollectOnlyOnCurrentThread);

(bool recorderValid, bool wasRunning, int? events, int? capacity, bool bufferSaturated, bool wrappedAround, bool measurementValid)
StopGcAllocationRecording(Unity.Profiling.ProfilerRecorder recorder)
{
    bool recorderValid = recorder.Valid;
    if (!recorderValid)
        return (false, false, null, null, false, false, false);
    bool wasRunning = recorder.IsRunning;
    recorder.Stop();
    int events = recorder.Count;
    int capacity = recorder.Capacity;
    bool wrappedAround = recorder.WrappedAround;
    bool bufferSaturated = events >= capacity;
    return (recorderValid, wasRunning, events, capacity, bufferSaturated, wrappedAround,
        wasRunning && !bufferSaturated && !wrappedAround);
}

using (var warmRecorder = StartGcAllocationRecording())
{
    GC.KeepAlive(new byte[16]);
    StopGcAllocationRecording(warmRecorder);
}
using var positiveRecorder = StartGcAllocationRecording();
var positiveControl = new byte[1048576];
GC.KeepAlive(positiveControl);
var positiveGc = StopGcAllocationRecording(positiveRecorder);
using var emptyRecorder = StartGcAllocationRecording();
var emptyGc = StopGcAllocationRecording(emptyRecorder);
var gcAllocationCalibration = new
{
    marker = "GC.Alloc",
    thread = "current main thread",
    options = "CollectOnlyOnCurrentThread; no frame aggregation; no wraparound",
    positiveControlBytes = 1048576,
    positive = new
    {
        positiveGc.recorderValid, positiveGc.wasRunning, positiveGc.events,
        positiveGc.capacity, positiveGc.bufferSaturated, positiveGc.wrappedAround
    },
    empty = new
    {
        emptyGc.recorderValid, emptyGc.wasRunning, emptyGc.events,
        emptyGc.capacity, emptyGc.bufferSaturated, emptyGc.wrappedAround
    },
    passed = positiveGc.measurementValid && positiveGc.events > 0
        && emptyGc.measurementValid && emptyGc.events == 0
};

object ReadGcAllocationResult(Unity.Profiling.ProfilerRecorder recorder)
{
    var measurement = StopGcAllocationRecording(recorder);
    return new
    {
        measurement.events, measurement.capacity, measurement.recorderValid,
        measurement.wasRunning, measurement.bufferSaturated, measurement.wrappedAround,
        measurementValid = gcAllocationCalibration.passed && measurement.measurementValid
    };
}

string[] request = System.IO.File.ReadAllLines("Tools/Benchmarks/Work/request.txt", System.Text.Encoding.UTF8);
string inputPath = request[0];
int repetitions = int.Parse(request[1], System.Globalization.CultureInfo.InvariantCulture);
string expectedSum = request[2];
string expectedXor = request[3];
string input = System.IO.File.ReadAllText(inputPath, System.Text.Encoding.UTF8);
var parseWatch = System.Diagnostics.Stopwatch.StartNew();
bool parsed = GameOfLife.LifePatternText.TryParse(input, "Life-in-Life", out var pattern, out string parseError);
parseWatch.Stop();
if (!parsed)
    return new { parsed, parseError, gcAllocationCalibration };

var results = new List<object>();
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
            sum += key;
            xor ^= key;
        }
    }
    return (sum.ToString("x16"), xor.ToString("x16"));
}

(Unity.Mathematics.int2 origin, int visibleCells) WholeSupportViewport(GameOfLife.LifeTile[] tiles, int generations)
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
    return (min, visibleCells);
}

(Unity.Mathematics.int2[] cells, double milliseconds) ExportWholeWorld(GameOfLife.LifeWorld world)
{
    var exportWatch = System.Diagnostics.Stopwatch.StartNew();
    var request = world.CollectCellsAsync(false, default, default);
    while (!request.IsCompleted && exportWatch.Elapsed.TotalSeconds < 60)
    {
        if (world.IsCollecting)
            world.ContinueCollection(4096);
        world.Pump(128);
    }
    if (!request.IsCompleted)
        throw new TimeoutException("全图导出未在 60 秒内完成。");
    var cells = request.GetAwaiter().GetResult();
    return (cells, exportWatch.Elapsed.TotalMilliseconds);
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
    var viewport = WholeSupportViewport(pattern.Tiles, 35328);
    world.SetViewport(viewport.origin, viewport.visibleCells);
    (string sum, string xor) coldFingerprint = default;
    long coldPopulation = 0;
    for (int cache = 0; cache < 2; cache++)
    {
        world.Clear();
        world.BeginEdit();
        world.Stamp(initial, Unity.Mathematics.int2.zero);
        world.EndEdit();
        world.RefreshView();
        while (!world.IsViewCurrent)
            world.Pump(budget);
        int batches = 0;
        int collections = 0;
        int nextCollection = 262144;
        int peakNodes = world.NodeCount;
        double maxBatchMs = 0;
        double maxCollectionBatchMs = 0;
        double collectionMilliseconds = 0;
        int collectionBatches = 0;
        long generations = 0;
        long hits = world.CacheHits;
        long misses = world.CacheMisses;
        var batchWatch = new System.Diagnostics.Stopwatch();
        using var gcRecorder = StartGcAllocationRecording();
        watch.Restart();
        bool complete = true;
        foreach (int exponent in exponents)
        {
            world.BeginAdvance(exponent);
            bool finished;
            do
            {
                if (!world.IsCollecting && world.NodeCount + world.RegionCacheCount >= nextCollection)
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
                    collectionMilliseconds += batchWatch.Elapsed.TotalMilliseconds;
                    collectionBatches++;
                    if (collected)
                        nextCollection = Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
                    finished = false;
                }
                else
                {
                    batchWatch.Restart();
                    world.Pump(budget);
                    finished = !world.IsAdvancing;
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
        var gcAllocations = ReadGcAllocationResult(gcRecorder);
        long advanceCacheHits = world.CacheHits - hits;
        long advanceCacheMisses = world.CacheMisses - misses;
        var exported = ExportWholeWorld(world);
        var fingerprint = Fingerprint(exported.cells);
        long population = exported.cells.LongLength;
        bool sameAsCold = true;
        if (cache == 0)
        {
            coldFingerprint = fingerprint;
            coldPopulation = population;
            roots.Add(world.ViewFrame.Shape);
        }
        else
            sameAsCold = fingerprint == coldFingerprint && population == coldPopulation;
        bool referenceMatches = fingerprint.sum == expectedSum && fingerprint.xor == expectedXor;
        bool valid = complete && sameAsCold && generations == 35328 && referenceMatches;
        if (!valid)
            failures++;
        results.Add(new {
            run, cache = cache == 0 ? "cold" : "warm", initialPopulation = pattern.Population,
            actualPopulation = population, generations, milliseconds = watch.Elapsed.TotalMilliseconds,
            viewportMilliseconds = watch.Elapsed.TotalMilliseconds, exportMilliseconds = exported.milliseconds,
            exactCompletionMilliseconds = watch.Elapsed.TotalMilliseconds + exported.milliseconds,
            originX = viewport.origin.x, originY = viewport.origin.y, viewport.visibleCells,
            buildMs, maxBatchMs, maxCollectionBatchMs, collectionMilliseconds, collectionBatches, batches, collections, peakNodes,
            nodes = world.NodeCount, cacheEntries = world.CacheCount,
            cacheHits = advanceCacheHits, cacheMisses = advanceCacheMisses,
            gcAllocations, fingerprint.sum, fingerprint.xor, referenceMatches, complete, sameAsCold, valid
        });
        if (!complete)
            world.CancelAdvance();
    }
}
var result = new {
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    unity = Application.unityVersion, burstEnabled = Unity.Burst.BurstCompiler.IsEnabled,
    processor = SystemInfo.processorType, inputPath, parsed, parseMs = parseWatch.Elapsed.TotalMilliseconds,
    initialPopulation = pattern.Population, tileCount = pattern.Tiles.Length,
    repetitions, budget, limitSeconds, gcAllocationCalibration, failures, results,
    scope = "固定视野覆盖初态向外扩展 35328 格的保守影响范围，连续请求 32768/2048/512 代；milliseconds/GC.Alloc 只记录完成视野推进及回收。显式全图导出另计 exportMilliseconds，人口和指纹来自导出数组；exactCompletionMilliseconds 为两阶段之和，指纹遍历不计入。导出可能补算离屏，不能仅凭视野完成声称全图完成；新口径不能与旧整图根推进耗时直接计算加速比。"
};
System.IO.File.WriteAllText("Tools/Benchmarks/Results/viewport-galaxy.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
