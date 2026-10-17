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
var initialFingerprint = Fingerprint(world);
long initialPopulation = world.Population;
var finalFingerprint = initialFingerprint;

void RecordSnapshot(int macroStep, bool complete, double? milliseconds, object gcAllocations, int batches, int collections, int collectionBatches, int advanceBatchesDuringCollection, int peakNodes, (string sum, string xor) actual)
{
    var expected = snapshots[macroStep];
    long expectedGeneration = (long)expected["generation"];
    long expectedPopulation = (long)expected["population"];
    string expectedSum = (string)expected["sum"];
    string expectedXor = (string)expected["xor"];
    finalFingerprint = actual;
    bool generationMatches = generation == expectedGeneration && generation == macroStep * generationsPerMacro;
    bool populationMatches = world.Population == expectedPopulation;
    bool fingerprintMatches = actual.sum == expectedSum && actual.xor == expectedXor;
    bool valid = complete && generationMatches && populationMatches && fingerprintMatches;
    if (!valid)
        failures++;
    results.Add(new
    {
        macroStep, complete, valid, generation, expectedGeneration,
        timedOut = !complete,
        population = world.Population, expectedPopulation,
        actual.sum, actual.xor, expectedSum, expectedXor,
        generationMatches, populationMatches, fingerprintMatches,
        sameAsInitial = world.Population == initialPopulation && actual == initialFingerprint,
        milliseconds, gcAllocations, batches, collections, collectionBatches, advanceBatchesDuringCollection, peakNodes,
        nodes = world.NodeCount, cacheEntries = world.CacheCount,
        cacheHits = world.CacheHits, cacheMisses = world.CacheMisses
    });
}

RecordSnapshot(0, true, null, null, 0, 0, 0, 0, world.NodeCount, initialFingerprint);
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
            if (!world.IsCollecting && world.NodeCount >= nextCollection)
            {
                world.BeginCollection(retained);
                collections++;
            }
            if (world.IsCollecting)
            {
                bool collected = world.ContinueCollection(collectionBudget);
                collectionBatches++;
                if (collected)
                    nextCollection = Math.Max(262144, world.NodeCount * 2);
            }
            if (world.IsCollecting)
                advanceBatchesDuringCollection++;
            finished = world.ContinueAdvance(advanceBudget);
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
    RecordSnapshot(macroStep, complete, watch.Elapsed.TotalMilliseconds, gcAllocations, batches, collections, collectionBatches, advanceBatchesDuringCollection, peakNodes, Fingerprint(world));
    if (!complete)
        break;
}

bool allComplete = completedMacroSteps == macroSteps && generation == macroSteps * generationsPerMacro && !world.IsAdvancing;
bool returnsToInitial = allComplete && world.Population == initialPopulation && finalFingerprint == initialFingerprint;
var result = new
{
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    unity = Application.unityVersion,
    burstEnabled = Unity.Burst.BurstCompiler.IsEnabled,
    inputPath, referencePath, parsed,
    initialPopulation, tileCount = pattern.Tiles.Length,
    macroSteps, generationsPerMacro, advanceBudget, collectionBudget, limitSecondsPerMacro,
    interleaveCollectionAndAdvance = true,
    completedMacroSteps, generation, complete = allComplete, valid = allComplete && failures == 0,
    gcAllocationCalibration, failures, returnsToInitial, results,
    scope = "同一 LifeWorld 只摆放一次，连续推进 8 个宏步；每步核对独立参考的全部细胞指纹、人口与累计代数。GC.Alloc 事件计数只包含主线程推进和回收，排除输入读取、解析、建树、初始摆放、指纹遍历和结果记录，不表示分配字节；本工具不验证实际帧率，也不把 8 步称为已完成底层周期。"
};
System.IO.Directory.CreateDirectory("Temp/LifeBenchmarks");
System.IO.File.WriteAllText("Temp/LifeBenchmarks/MeasureLifeContinuity-result.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
