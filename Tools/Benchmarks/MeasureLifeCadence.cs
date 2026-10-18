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

string inputPath = "Tools/Benchmarks/Work/hex-22071-tiles.bin";
var reference = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText("Tools/Benchmarks/Results/hex-reference.json", System.Text.Encoding.UTF8));
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

(string sum, string xor) Fingerprint(Unity.Mathematics.int2[] cells)
{
    ulong sum = 0, xor = 0;
    foreach (var cell in cells)
    {
        unchecked
        {
            ulong key = CellHash(cell.x, cell.y);
            sum += key;
            xor ^= key;
        }
    }
    return (sum.ToString("x16"), xor.ToString("x16"));
}

(Unity.Mathematics.int2[] cells, double milliseconds) ExportWholeWorld(GameOfLife.LifeWorld simulation)
{
    var exportWatch = System.Diagnostics.Stopwatch.StartNew();
    var request = simulation.CollectCellsAsync(false, default, default);
    while (!request.IsCompleted && exportWatch.Elapsed.TotalSeconds < 60)
    {
        if (simulation.IsCollecting)
            simulation.ContinueCollection(1024);
        simulation.Pump(128);
    }
    if (!request.IsCompleted)
        throw new TimeoutException("全图导出未在 60 秒内完成。");
    return (request.GetAwaiter().GetResult(), exportWatch.Elapsed.TotalMilliseconds);
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
    var viewportOrigin = pattern.Tiles[0].Position;
    var viewportMax = viewportOrigin + 16;
    foreach (var tile in pattern.Tiles)
    {
        viewportOrigin = Unity.Mathematics.math.min(viewportOrigin, tile.Position);
        viewportMax = Unity.Mathematics.math.max(viewportMax, tile.Position + 16);
    }
    var viewportCenter = viewportOrigin + (viewportMax - viewportOrigin) / 2;
    int visibleCells = 16;
    while (visibleCells < Unity.Mathematics.math.cmax(viewportMax - viewportOrigin) + requestedGenerations * 2)
        visibleCells *= 2;
    int targetDivisorExponent = Unity.Mathematics.math.tzcnt((uint)requestedGenerations);
    while (GameOfLife.LifeSimulationPolicy.StepExponent(visibleCells, generationsPerSecond) > targetDivisorExponent)
        visibleCells /= 2;
    viewportOrigin = viewportCenter - visibleCells / 2;
    int policyStepExponent = GameOfLife.LifeSimulationPolicy.StepExponent(visibleCells, generationsPerSecond);
    manager.SetComponentData(board, new GameOfLife.LifeViewport {
        Origin = viewportOrigin, VisibleCells = visibleCells, FrameSeconds = 1d / 60
    });
    system.Simulation.SetViewport(viewportOrigin, visibleCells);
    system.Simulation.RefreshView();
    while (!system.Simulation.IsViewCurrent)
        system.Simulation.Pump(128);
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
    var exported = ExportWholeWorld(system.Simulation);
    var fingerprint = Fingerprint(exported.cells);
    bool referenceMatches = fingerprint.sum == (string)expected["sum"] && fingerprint.xor == (string)expected["xor"];
    bool complete = state.Generation == requestedGenerations && !system.Simulation.IsAdvancing;
    bool valid = complete && referenceMatches;
    if (!valid)
        failures++;
    results.Add(new {
        run, prepareMs, updates, maximumUpdateMs, milliseconds = watch.Elapsed.TotalMilliseconds,
        viewportMilliseconds = watch.Elapsed.TotalMilliseconds, exportMilliseconds = exported.milliseconds,
        exactCompletionMilliseconds = watch.Elapsed.TotalMilliseconds + exported.milliseconds,
        viewportOriginX = viewportOrigin.x, viewportOriginY = viewportOrigin.y, visibleCells, frameSeconds = 1d / 60,
        policyStepExponent, policyStepGenerations = 1L << policyStepExponent,
        publicationCount, publicationTimesMs = publicationTimes.Take(publicationCount).ToArray(),
        publicationGenerations = publicationGenerations.Take(publicationCount).ToArray(),
        gcAllocations, peakNodes, nodes = system.Simulation.NodeCount,
        generation = state.Generation, population = exported.cells.LongLength,
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
    long populationBeforeInput = exported.cells.LongLength;
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
    cleanupWatch.Stop();
    ulong expectedSum = ulong.Parse(fingerprint.sum, System.Globalization.NumberStyles.HexNumber);
    ulong expectedXor = ulong.Parse(fingerprint.xor, System.Globalization.NumberStyles.HexNumber);
    for (int cell = 0; cell < brushLength; cell++)
    {
        var position = brushStart + new Unity.Mathematics.int2(cell, 0);
        ulong hash = CellHash(position.x, position.y);
        unchecked { expectedSum += hash; }
        expectedXor ^= hash;
    }
    var editedExport = ExportWholeWorld(system.Simulation);
    var editedFingerprint = Fingerprint(editedExport.cells);
    int brushCells = 0;
    foreach (var cell in editedExport.cells)
        if (cell.y == brushStart.y && cell.x >= brushStart.x && cell.x < brushStart.x + brushLength)
            brushCells++;
    bool cellsAlive = brushCells == brushLength;
    bool editedMatches = editedFingerprint.sum == expectedSum.ToString("x16")
        && editedFingerprint.xor == expectedXor.ToString("x16");
    bool inputValid = advancingAtSubmit && collectingAtSubmit && consumed && collectingAtConsumption
        && !afterInput.IsRunning && !system.Simulation.IsAdvancing && !system.Simulation.IsCollecting
        && beforeInput.Generation == requestedGenerations && afterInput.Generation == beforeInput.Generation
        && editedExport.cells.LongLength == populationBeforeInput + brushLength && cellsAlive && editedMatches;
    if (!inputValid)
        failures++;
    inputLatencyResults.Add(new {
        run, brushLength, advancingAtSubmit, collectingAtSubmit, collectingAtConsumption,
        consumed, inputUpdates, inputMilliseconds, maximumInputUpdateMs, inputGcAllocations,
        cleanupUpdates, generationBefore = beforeInput.Generation, generationAfter = afterInput.Generation,
        cleanupMilliseconds = cleanupWatch.Elapsed.TotalMilliseconds, exportMilliseconds = editedExport.milliseconds,
        exactCompletionMilliseconds = inputMilliseconds + cleanupWatch.Elapsed.TotalMilliseconds + editedExport.milliseconds,
        populationBeforeInput, populationBeforeInputGeneration = state.Generation,
        population = editedExport.cells.LongLength, cellsAlive, editedMatches,
        editedFingerprint.sum, editedFingerprint.xor, valid = inputValid
    });
}
var result = new {
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    unity = Application.unityVersion, inputPath, repetitions,
    requestedGenerations, inputSourceGeneration = 22071, generationsPerSecond, initialDeltaTime, gcAllocationCalibration, failures, results,
    inputLatencyResults,
    scope = "正式 System 的合成时间驱动，只注入目标代数的信用。窗口位于输入中心，尺寸从保守支持向下选，使策略跨度整除有限目标，避免零 DeltaTime 时尾部信用不足；窗口不保证覆盖全图。milliseconds、发布次数和 GC.Alloc 只计该视野推进；全图指纹与人口由随后显式导出取得，exportMilliseconds 单列补算和枚举，exactCompletionMilliseconds 合计。输入延迟只计命令消费，清理和导出验证分别计时；populationBeforeInput 来自目标代完成后的导出，若提交前代数已变化则有效性判定失败。排除绘图、UI、实际帧节奏、输入准备与指纹遍历，新口径不能直接与旧整图计时计算加速比。"
};
System.IO.File.WriteAllText("Tools/Benchmarks/Results/viewport-system.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
