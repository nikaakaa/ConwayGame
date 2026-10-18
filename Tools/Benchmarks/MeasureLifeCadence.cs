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

string inputPath = "Temp/LifeBenchmarks/hex-22071-tiles.bin";
var reference = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText("Temp/LifeBenchmarks/hex-reference-targets.json", System.Text.Encoding.UTF8));
var expected = ((Newtonsoft.Json.Linq.JArray)reference["snapshots"]).First(item => (long)item["generation"] == 22583);
int repetitions = 5;
GameOfLife.LifeTile[] tiles;
using (var reader = new System.IO.BinaryReader(System.IO.File.OpenRead(inputPath)))
{
    tiles = new GameOfLife.LifeTile[reader.ReadInt32()];
    for (int tile = 0; tile < tiles.Length; tile++)
        tiles[tile] = new GameOfLife.LifeTile {
            Position = new Unity.Mathematics.int2(reader.ReadInt32(), reader.ReadInt32()),
            Bits = new GameOfLife.LifeBits { A = reader.ReadUInt64(), B = reader.ReadUInt64(), C = reader.ReadUInt64(), D = reader.ReadUInt64() }
        };
}
var pattern = new GameOfLife.LifePattern("Hexadecimal @22071", "Performance", 0, Unity.Mathematics.int2.zero, tiles, "");

ulong CellHash(long x, long y)
{
    unchecked
    {
        ulong key = ((ulong)(uint)x << 32) | (uint)y;
        key = (key ^ (key >> 30)) * 0xbf58476d1ce4e5b9UL;
        key = (key ^ (key >> 27)) * 0x94d049bb133111ebUL;
        return key ^ (key >> 31);
    }
}

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
                    ulong key = CellHash(x + column, y + rowIndex);
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
var inputLatencyResults = new List<object>();
int failures = 0;
const int requestedGenerations = 512;
const int generationsPerSecond = 20480;
const float initialDeltaTime = (float)requestedGenerations / generationsPerSecond;
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
    var publicationTimes = new double[requestedGenerations];
    var publicationGenerations = new long[requestedGenerations];
    int publicationCount = 0;
    long lastPublished = 0;
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
        if (state.Generation != lastPublished)
        {
            publicationTimes[publicationCount] = watch.Elapsed.TotalMilliseconds;
            publicationGenerations[publicationCount++] = state.Generation;
            lastPublished = state.Generation;
        }
        world.SetTime(new Unity.Core.TimeData(initialDeltaTime, 0));
    } while (state.Generation < requestedGenerations && watch.Elapsed.TotalSeconds < 60);
    watch.Stop();
    var gcAllocations = ReadGcAllocationResult(gcRecorder);
    var fingerprint = Fingerprint(system.Simulation);
    bool referenceMatches = fingerprint.sum == (string)expected["sum"] && fingerprint.xor == (string)expected["xor"];
    bool complete = state.Generation == requestedGenerations && !system.Simulation.IsAdvancing;
    bool valid = complete && referenceMatches;
    if (!valid)
        failures++;
    results.Add(new {
        run, prepareMs, updates, maximumUpdateMs, milliseconds = watch.Elapsed.TotalMilliseconds,
        publicationCount, publicationTimesMs = publicationTimes.Take(publicationCount).ToArray(),
        publicationGenerations = publicationGenerations.Take(publicationCount).ToArray(),
        gcAllocations, peakNodes, nodes = system.Simulation.NodeCount,
        generation = state.Generation, population = system.Simulation.Population,
        fingerprint.sum, fingerprint.xor, referenceMatches, complete, valid
    });

    world.SetTime(new Unity.Core.TimeData(initialDeltaTime * 2, initialDeltaTime));
    system.Update();
    world.SetTime(new Unity.Core.TimeData(initialDeltaTime * 2, 0));
    using var inputRoots = new Unity.Collections.NativeArray<GameOfLife.LifeShape>(
        new[] { system.CellShape, system.PatternShape(pattern) }, Unity.Collections.Allocator.Temp);
    if (!system.Simulation.IsCollecting)
        system.Simulation.BeginCollection(inputRoots);
    bool advancingAtSubmit = system.Simulation.IsAdvancing;
    bool collectingAtSubmit = system.Simulation.IsCollecting;
    var beforeInput = manager.GetComponentData<GameOfLife.LifeState>(board);
    long populationBeforeInput = system.Simulation.Population;
    const int brushLength = 64;
    var brushStart = new Unity.Mathematics.int2(1000000, 1000000);
    manager.GetBuffer<GameOfLife.LifeCommand>(board).Add(new GameOfLife.LifeCommand {
        Kind = GameOfLife.LifeCommandKind.CellStroke, Position = brushStart,
        End = brushStart + new Unity.Mathematics.int2(brushLength - 1, 0), Alive = true
    });
    int inputUpdates = 0;
    double maximumInputUpdateMs = 0;
    using var inputGcRecorder = StartGcAllocationRecording();
    watch.Restart();
    do
    {
        long updateStart = System.Diagnostics.Stopwatch.GetTimestamp();
        system.Update();
        maximumInputUpdateMs = Math.Max(maximumInputUpdateMs,
            (System.Diagnostics.Stopwatch.GetTimestamp() - updateStart) * 1000d / System.Diagnostics.Stopwatch.Frequency);
        inputUpdates++;
    }
    while (manager.GetBuffer<GameOfLife.LifeCommand>(board).Length != 0 && watch.Elapsed.TotalSeconds < 60);
    watch.Stop();
    double inputMilliseconds = watch.Elapsed.TotalMilliseconds;
    var inputGcAllocations = ReadGcAllocationResult(inputGcRecorder);
    bool consumed = manager.GetBuffer<GameOfLife.LifeCommand>(board).Length == 0;
    bool collectingAtConsumption = system.Simulation.IsCollecting;
    var afterInput = manager.GetComponentData<GameOfLife.LifeState>(board);
    int cleanupUpdates = 0;
    var cleanupWatch = System.Diagnostics.Stopwatch.StartNew();
    while (system.Simulation.IsCollecting && cleanupWatch.Elapsed.TotalSeconds < 60)
    {
        system.Update();
        cleanupUpdates++;
    }
    bool cellsAlive = true;
    ulong expectedSum = ulong.Parse(fingerprint.sum, System.Globalization.NumberStyles.HexNumber);
    ulong expectedXor = ulong.Parse(fingerprint.xor, System.Globalization.NumberStyles.HexNumber);
    for (int cell = 0; cell < brushLength; cell++)
    {
        var position = brushStart + new Unity.Mathematics.int2(cell, 0);
        cellsAlive &= system.Simulation.GetCell(position);
        ulong hash = CellHash(position.x, position.y);
        unchecked { expectedSum += hash; }
        expectedXor ^= hash;
    }
    var editedFingerprint = Fingerprint(system.Simulation);
    bool editedMatches = editedFingerprint.sum == expectedSum.ToString("x16")
        && editedFingerprint.xor == expectedXor.ToString("x16");
    bool inputValid = advancingAtSubmit && collectingAtSubmit && consumed && collectingAtConsumption
        && !afterInput.IsRunning && !system.Simulation.IsAdvancing && !system.Simulation.IsCollecting
        && beforeInput.Generation == requestedGenerations && afterInput.Generation == beforeInput.Generation
        && system.Simulation.Population == populationBeforeInput + brushLength && cellsAlive && editedMatches;
    if (!inputValid)
        failures++;
    inputLatencyResults.Add(new {
        run, brushLength, advancingAtSubmit, collectingAtSubmit, collectingAtConsumption,
        consumed, inputUpdates, inputMilliseconds, maximumInputUpdateMs, inputGcAllocations,
        cleanupUpdates, generationBefore = beforeInput.Generation, generationAfter = afterInput.Generation,
        populationBeforeInput, population = system.Simulation.Population, cellsAlive, editedMatches,
        editedFingerprint.sum, editedFingerprint.xor, valid = inputValid
    });
}
var result = new {
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    unity = Application.unityVersion, inputPath, repetitions,
    requestedGenerations, inputSourceGeneration = 22071, generationsPerSecond, initialDeltaTime, gcAllocationCalibration, failures, results,
    inputLatencyResults,
    scope = "Production System updates with synthetic time; excludes raster, UI, actual frame pacing and input preparation."
};
System.IO.Directory.CreateDirectory("Temp/LifeBenchmarks");
System.IO.File.WriteAllText("Temp/LifeBenchmarks/hex-scheduler-cadence.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
