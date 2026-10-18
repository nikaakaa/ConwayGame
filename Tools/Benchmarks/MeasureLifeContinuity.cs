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

string[] request = System.IO.File.ReadAllLines("Temp/LifeBenchmarks/continuity-request.txt", System.Text.Encoding.UTF8);
string inputPath = request[0];
string referencePath = request[1];
string input = System.IO.File.ReadAllText(inputPath, System.Text.Encoding.UTF8);
string referenceText = System.IO.File.ReadAllText(referencePath, System.Text.Encoding.UTF8);
var reference = Newtonsoft.Json.Linq.JObject.Parse(referenceText);
var snapshots = (Newtonsoft.Json.Linq.JArray)reference["snapshots"];
bool parsed = GameOfLife.LifePatternText.TryParse(input, "Life-in-Life 连续演化", out var pattern, out string parseError);
if (!parsed)
    return new { parsed, parseError, gcAllocationCalibration, complete = false, failures = 1 };

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
    return (request.GetAwaiter().GetResult(), exportWatch.Elapsed.TotalMilliseconds);
}

const int macroSteps = 8;
const long generationsPerMacro = 35328;
const int advanceBudget = 128;
const int collectionBudget = 4096;
const double limitSecondsPerMacro = 60;
int[] exponents = { 15, 11, 9 };
var results = new List<object>(macroSteps + 1);
int failures = 0;
long generation = 0;
int completedMacroSteps = 0;
int nextCollection = 262144;
using var world = new GameOfLife.LifeWorld();
var initial = world.BuildShape(pattern.Tiles);
using var retained = new Unity.Collections.NativeArray<GameOfLife.LifeShape>(new[] { initial }, Unity.Collections.Allocator.Persistent);
world.BeginEdit();
world.Stamp(initial, Unity.Mathematics.int2.zero);
world.EndEdit();
var viewportOrigin = pattern.Tiles[0].Position;
var viewportMax = viewportOrigin + 16;
foreach (var tile in pattern.Tiles)
{
    viewportOrigin = Unity.Mathematics.math.min(viewportOrigin, tile.Position);
    viewportMax = Unity.Mathematics.math.max(viewportMax, tile.Position + 16);
}
viewportOrigin -= (int)(macroSteps * generationsPerMacro);
viewportMax += (int)(macroSteps * generationsPerMacro);
int visibleCells = 16;
while (visibleCells < Unity.Mathematics.math.cmax(viewportMax - viewportOrigin))
    visibleCells *= 2;
world.SetViewport(viewportOrigin, visibleCells);
world.RefreshView();
while (!world.IsViewCurrent)
    world.Pump(advanceBudget);
var initialExport = ExportWholeWorld(world);
var initialFingerprint = Fingerprint(initialExport.cells);
long initialPopulation = initialExport.cells.LongLength;
long finalPopulation = initialPopulation;
var finalFingerprint = initialFingerprint;

void RecordSnapshot(int macroStep, bool complete, double? milliseconds, double exportMilliseconds, long population, object gcAllocations, int batches, int collections, int collectionBatches, int advanceBatchesDuringCollection, int peakNodes, (string sum, string xor) actual)
{
    var expected = snapshots[macroStep];
    long expectedGeneration = (long)expected["generation"];
    long expectedPopulation = (long)expected["population"];
    string expectedSum = (string)expected["sum"];
    string expectedXor = (string)expected["xor"];
    finalFingerprint = actual;
    finalPopulation = population;
    bool generationMatches = generation == expectedGeneration && generation == macroStep * generationsPerMacro
        && world.Snapshot.Generation == generation;
    bool populationMatches = population == expectedPopulation;
    bool fingerprintMatches = actual.sum == expectedSum && actual.xor == expectedXor;
    bool valid = complete && generationMatches && populationMatches && fingerprintMatches;
    if (!valid)
        failures++;
    results.Add(new
    {
        macroStep, complete, valid, generation, expectedGeneration,
        timedOut = !complete,
        population, expectedPopulation,
        actual.sum, actual.xor, expectedSum, expectedXor,
        generationMatches, populationMatches, fingerprintMatches,
        sameAsInitial = population == initialPopulation && actual == initialFingerprint,
        milliseconds, gcAllocations, batches, collections, collectionBatches, advanceBatchesDuringCollection, peakNodes,
        viewportMilliseconds = milliseconds, exportMilliseconds,
        exactCompletionMilliseconds = milliseconds.HasValue ? milliseconds.Value + exportMilliseconds : exportMilliseconds,
        nodes = world.NodeCount, cacheEntries = world.CacheCount,
        cacheHits = world.CacheHits, cacheMisses = world.CacheMisses
    });
}

RecordSnapshot(0, true, null, initialExport.milliseconds, initialPopulation, null, 0, 0, 0, 0, world.NodeCount, initialFingerprint);
var watch = new System.Diagnostics.Stopwatch();
for (int macroStep = 1; macroStep <= macroSteps; macroStep++)
{
    bool complete = true;
    int batches = 0;
    int collections = 0;
    int collectionBatches = 0;
    int advanceBatchesDuringCollection = 0;
    int peakNodes = world.NodeCount;
    using var gcRecorder = StartGcAllocationRecording();
    watch.Restart();
    foreach (int exponent in exponents)
    {
        world.BeginAdvance(exponent);
        bool finished;
        do
        {
            if (!world.IsCollecting && world.NodeCount + world.RegionCacheCount >= nextCollection)
            {
                world.BeginCollection(retained);
                collections++;
            }
            if (world.IsCollecting)
            {
                bool collected = world.ContinueCollection(collectionBudget);
                collectionBatches++;
                if (collected)
                    nextCollection = Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
            }
            if (world.IsCollecting)
                advanceBatchesDuringCollection++;
            world.Pump(advanceBudget);
            finished = !world.IsAdvancing;
            batches++;
            peakNodes = Math.Max(peakNodes, world.NodeCount);
        }
        while (!finished && watch.Elapsed.TotalSeconds < limitSecondsPerMacro);
        if (!finished)
        {
            complete = false;
            world.CancelAdvance();
            break;
        }
        generation += 1L << exponent;
        if (watch.Elapsed.TotalSeconds >= limitSecondsPerMacro && generation < macroStep * generationsPerMacro)
        {
            complete = false;
            world.CancelAdvance();
            break;
        }
    }
    watch.Stop();
    var gcAllocations = ReadGcAllocationResult(gcRecorder);
    if (complete)
        completedMacroSteps++;
    var exported = ExportWholeWorld(world);
    RecordSnapshot(macroStep, complete, watch.Elapsed.TotalMilliseconds, exported.milliseconds, exported.cells.LongLength,
        gcAllocations, batches, collections, collectionBatches, advanceBatchesDuringCollection, peakNodes, Fingerprint(exported.cells));
    if (!complete)
        break;
}

bool allComplete = completedMacroSteps == macroSteps && generation == macroSteps * generationsPerMacro && !world.IsAdvancing;
bool returnsToInitial = allComplete && finalPopulation == initialPopulation && finalFingerprint == initialFingerprint;
var result = new
{
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    unity = Application.unityVersion,
    burstEnabled = Unity.Burst.BurstCompiler.IsEnabled,
    inputPath, referencePath, parsed,
    initialPopulation, tileCount = pattern.Tiles.Length,
    macroSteps, generationsPerMacro, advanceBudget, collectionBudget, limitSecondsPerMacro,
    viewportOriginX = viewportOrigin.x, viewportOriginY = viewportOrigin.y, visibleCells,
    interleaveCollectionAndAdvance = true,
    completedMacroSteps, generation, complete = allComplete, valid = allComplete && failures == 0,
    gcAllocationCalibration, failures, returnsToInitial, results,
    scope = "同一 LifeWorld 只摆放一次，固定视野覆盖初态向外扩展 282624 格的保守支持，连续推进 8 个宏步。每步显式全图导出后才核对独立参考的全部细胞指纹与人口；milliseconds/GC.Alloc 仅计视野推进和回收，exportMilliseconds 单列导出求值与枚举，exactCompletionMilliseconds 合计两阶段。指纹遍历、输入和记录排除在计时外。新 API 的视野完成不等于全图完成，新旧计时不能直接计算加速比；本工具不验证实际帧率，也不把 8 步称为已完成底层周期。"
};
System.IO.Directory.CreateDirectory("Temp/LifeBenchmarks");
System.IO.File.WriteAllText("Temp/LifeBenchmarks/MeasureLifeContinuity-result.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
