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

string[] request = System.IO.File.ReadAllLines("Temp/LifeBenchmarks/request.txt", System.Text.Encoding.UTF8);
string input = System.IO.File.ReadAllText(request[0], System.Text.Encoding.UTF8);
int repetitions = int.Parse(request[1], System.Globalization.CultureInfo.InvariantCulture);
bool parsed = GameOfLife.LifePatternText.TryParse(input, "Life-in-Life", out var pattern, out string parseError);
if (!parsed)
    return new { parsed, parseError, gcAllocationCalibration };

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
            for (int i = 0; i < 4; i++)
                Visit(node.Children[i], x + (i & 1) * half, y + (i >> 1) * half);
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
                    sum += key;
                    xor ^= key;
                }
            }
        }
    }
    Visit(world.Shape.Root, world.Shape.X, world.Shape.Y);
    return (sum.ToString("x16"), xor.ToString("x16"));
}

var results = new List<object>();
int failures = 0;
const int requestedGenerations = 35328;
const int generationsPerSecond = requestedGenerations * 30;
const float initialDeltaTime = 1f / 30f;
for (int run = 0; run < repetitions; run++)
{
    using var world = new Unity.Entities.World("LifeSchedulerMeasurement");
    var system = world.GetOrCreateSystemManaged<GameOfLife.LifeSimulationSystem>();
    var manager = world.EntityManager;
    using var query = manager.CreateEntityQuery(typeof(GameOfLife.LifeState));
    var board = query.GetSingletonEntity();
    var watch = System.Diagnostics.Stopwatch.StartNew();
    system.LoadPatterns(new[] { pattern });
    system.Simulation.Clear();
    system.Simulation.BeginEdit();
    system.Simulation.Stamp(system.PatternShape(pattern), Unity.Mathematics.int2.zero);
    system.Simulation.EndEdit();
    manager.SetComponentData(board, new GameOfLife.LifeSettings { GenerationsPerSecond = generationsPerSecond });
    manager.SetComponentData(board, new GameOfLife.LifeState { Revision = 1 });
    manager.GetBuffer<GameOfLife.LifeCommand>(board).Add(new GameOfLife.LifeCommand { Kind = GameOfLife.LifeCommandKind.Play });
    world.SetTime(new Unity.Core.TimeData(0, 0));
    do
        system.Update();
    while (!manager.GetComponentData<GameOfLife.LifeState>(board).IsRunning && watch.Elapsed.TotalSeconds < 60);
    watch.Stop();
    double prepareMs = watch.Elapsed.TotalMilliseconds;
    world.SetTime(new Unity.Core.TimeData(initialDeltaTime, initialDeltaTime));
    int updates = 0;
    int peakNodes = system.Simulation.NodeCount;
    double maximumUpdateMs = 0;
    GameOfLife.LifeState state;
    using var gcRecorder = StartGcAllocationRecording();
    watch.Restart();
    do
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        system.Update();
        double updateMs = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        maximumUpdateMs = Math.Max(maximumUpdateMs, updateMs);
        peakNodes = Math.Max(peakNodes, system.Simulation.NodeCount);
        updates++;
        state = manager.GetComponentData<GameOfLife.LifeState>(board);
        world.SetTime(new Unity.Core.TimeData(initialDeltaTime, 0));
    } while (state.Generation < requestedGenerations && watch.Elapsed.TotalSeconds < 60);
    watch.Stop();
    var gcAllocations = ReadGcAllocationResult(gcRecorder);
    var fingerprint = Fingerprint(system.Simulation);
    bool referenceMatches = fingerprint.sum == request[2] && fingerprint.xor == request[3];
    bool complete = state.Generation == requestedGenerations && !system.Simulation.IsAdvancing;
    bool valid = complete && referenceMatches;
    if (!valid)
        failures++;
    results.Add(new {
        run, prepareMs, updates, maximumUpdateMs, milliseconds = watch.Elapsed.TotalMilliseconds,
        gcAllocations, peakNodes, nodes = system.Simulation.NodeCount,
        generation = state.Generation, population = system.Simulation.Population,
        fingerprint.sum, fingerprint.xor, referenceMatches, complete, valid
    });
}
var result = new {
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    unity = Application.unityVersion, inputPath = request[0], repetitions,
    requestedGenerations, generationsPerSecond, initialDeltaTime, gcAllocationCalibration, failures, results,
    scope = "Production System updates with synthetic time; excludes raster, UI, actual frame pacing and input preparation."
};
System.IO.Directory.CreateDirectory("Temp/LifeBenchmarks");
System.IO.File.WriteAllText("Temp/LifeBenchmarks/MeasureLifeScheduler-result.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
