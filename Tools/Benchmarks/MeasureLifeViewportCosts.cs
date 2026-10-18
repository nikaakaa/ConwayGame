bool historyEnabledAtStart = UnityEditorInternal.ProfilerDriver.enabled;
string progressPath = "Tools/Benchmarks/Results/viewport-hex-costs-progress.jsonl";
System.IO.File.WriteAllText(progressPath, string.Empty, new System.Text.UTF8Encoding(false));
string tilePath = "Tools/Benchmarks/Work/hex-22071-tiles.bin";
var sourceBytes = System.IO.File.ReadAllBytes(tilePath);
GameOfLife.LifeTile[] tiles;
using (var reader = new System.IO.BinaryReader(new System.IO.MemoryStream(sourceBytes)))
{
    tiles = new GameOfLife.LifeTile[reader.ReadInt32()];
    for (int i = 0; i < tiles.Length; i++)
        tiles[i] = new GameOfLife.LifeTile {
            Position = new Unity.Mathematics.int2(reader.ReadInt32(), reader.ReadInt32()),
            Bits = new GameOfLife.LifeBits {
                A = reader.ReadUInt64(), B = reader.ReadUInt64(), C = reader.ReadUInt64(), D = reader.ReadUInt64()
            }
        };
}
int mostPopulatedTile = 0;
for (int i = 1; i < tiles.Length; i++)
    if (tiles[i].Bits.Population > tiles[mostPopulatedTile].Bits.Population)
        mostPopulatedTile = i;
var nearCenter = tiles[mostPopulatedTile].Position + 8;
var expectedDocument = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(
    "Tools/Benchmarks/Results/hex-reference.json", System.Text.Encoding.UTF8));
var expected = expectedDocument["snapshots"].First(item => (long)item["generation"] == 22583);
var results = new System.Collections.Generic.List<object>();
var clock = System.Diagnostics.Stopwatch.StartNew();
const int generations = 512;
const int workBudget = 128;
const int allocationCapacity = 4096;

Unity.Profiling.ProfilerRecorder StartAllocationRecording() => Unity.Profiling.ProfilerRecorder.StartNew(
    Unity.Profiling.ProfilerCategory.Internal, "GC.Alloc", allocationCapacity,
    Unity.Profiling.ProfilerRecorderOptions.CollectOnlyOnCurrentThread);

(bool valid, int events, bool truncated) StopAllocationRecording(Unity.Profiling.ProfilerRecorder recorder)
{
    bool valid = recorder.Valid && recorder.IsRunning;
    recorder.Stop();
    return (valid, recorder.Count, recorder.WrappedAround || recorder.Count == recorder.Capacity);
}
using (var warm = StartAllocationRecording())
{
    GC.KeepAlive(new byte[16]);
    StopAllocationRecording(warm);
}
using var positive = StartAllocationRecording();
GC.KeepAlive(new byte[1048576]);
var positiveControl = StopAllocationRecording(positive);
using var empty = StartAllocationRecording();
var emptyControl = StopAllocationRecording(empty);
bool allocationCounterCalibrated = positiveControl.valid && positiveControl.events > 0 && !positiveControl.truncated
    && emptyControl.valid && emptyControl.events == 0 && !emptyControl.truncated;

(long population, string sum, string xor) Fingerprint(Unity.Mathematics.int2[] cells, bool clipped,
    Unity.Mathematics.int2 min, int size)
{
    ulong sum = 0, xor = 0;
    long population = 0;
    foreach (var cell in cells)
    {
        if (clipped && (Unity.Mathematics.math.any(cell < min) || Unity.Mathematics.math.any(cell >= min + size)))
            continue;
        unchecked
        {
            ulong key = ((ulong)(uint)cell.x << 32) | (uint)cell.y;
            key = (key ^ (key >> 30)) * 0xbf58476d1ce4e5b9UL;
            key = (key ^ (key >> 27)) * 0x94d049bb133111ebUL;
            key ^= key >> 31;
            sum += key; xor ^= key; population++;
        }
    }
    return (population, sum.ToString("x16"), xor.ToString("x16"));
}

(long population, string sum, string xor) ViewFingerprint(GameOfLife.LifeWorld world)
{
    long population = 0;
    ulong sum = 0, xor = 0;
    var frame = world.ViewFrame;
    var bounds = frame.ValidBounds;
    var nodes = world.Nodes;
    void Visit(int id, long x, long y)
    {
        var node = nodes[id];
        long side = 1L << node.Level;
        if (node.Population == 0 || x >= bounds.MaxX || y >= bounds.MaxY
            || x + side <= bounds.MinX || y + side <= bounds.MinY)
            return;
        if (node.Level > 4)
        {
            long half = side >> 1;
            for (int child = 0; child < 4; child++)
                Visit(node.Children[child], x + ((child & 1) != 0 ? half : 0), y + ((child & 2) != 0 ? half : 0));
            return;
        }
        for (int row = 0; row < 16; row++)
        {
            uint bits = node.Bits.GetRow(row);
            while (bits != 0)
            {
                int column = Unity.Mathematics.math.tzcnt(bits);
                bits &= bits - 1;
                long px = x + column, py = y + row;
                if (px < bounds.MinX || py < bounds.MinY || px >= bounds.MaxX || py >= bounds.MaxY)
                    continue;
                unchecked
                {
                    ulong key = ((ulong)(uint)px << 32) | (uint)py;
                    key = (key ^ (key >> 30)) * 0xbf58476d1ce4e5b9UL;
                    key = (key ^ (key >> 27)) * 0x94d049bb133111ebUL;
                    key ^= key >> 31;
                    sum += key; xor ^= key; population++;
                }
            }
        }
    }
    Visit(frame.Shape.Root, frame.Shape.X, frame.Shape.Y);
    return (population, sum.ToString("x16"), xor.ToString("x16"));
}

int[] sizes = { 128, 4096, 65536 };
int failures = 0;
for (int run = 0; run < 5; run++)
for (int index = 0; index < sizes.Length; index++)
{
    bool historyEnabledAtRunStart = UnityEditorInternal.ProfilerDriver.enabled;
    int size = sizes[(run + index) % sizes.Length];
    var origin = size == 65536 ? new Unity.Mathematics.int2(-32768, -32768) : nearCenter - size / 2;
    int exponent = GameOfLife.LifeSimulationPolicy.StepExponent(size, 20480);
    int step = 1 << exponent;
    var setupTimer = System.Diagnostics.Stopwatch.StartNew();
    using var world = new GameOfLife.LifeWorld();
    var shape = world.BuildShape(tiles);
    double buildMs = setupTimer.Elapsed.TotalMilliseconds;
    System.IO.File.AppendAllText(progressPath,
        Newtonsoft.Json.JsonConvert.SerializeObject(new { run, size, phase = "built", buildMs }) + System.Environment.NewLine,
        new System.Text.UTF8Encoding(false));
    var roots = new System.Collections.Generic.List<GameOfLife.LifeShape> { shape };
    world.SetViewport(origin, size);
    setupTimer.Restart();
    world.BeginEdit(); world.Stamp(shape, Unity.Mathematics.int2.zero); world.EndEdit();
    double initialStampMs = setupTimer.Elapsed.TotalMilliseconds;
    setupTimer.Restart();
    while (!world.IsViewCurrent) world.Pump(workBudget);
    double initialViewportMs = setupTimer.Elapsed.TotalMilliseconds;
    var publications = new double[generations / step];
    int nextCollection = 262144;
    int pumpBatches = 0;
    int collectionBatches = 0;
    int postViewCollectionBatches = 0;
    int exportCollectionBatches = 0;
    int postExportCollectionBatches = 0;
    double maxBatchMs = 0;
    double maxCollectionBatchMs = 0;
    long cacheMissesBefore = world.CacheMisses;
    long cacheHitsBefore = world.CacheHits;
    var timer = new System.Diagnostics.Stopwatch();
    using var allocations = StartAllocationRecording();
    timer.Start();
    for (int publication = 0; publication < publications.Length; publication++)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        world.BeginAdvance(exponent);
        while (world.IsAdvancing || !world.IsViewCurrent)
        {
            if (clock.Elapsed.TotalSeconds > 600)
                throw new System.TimeoutException("Viewport cost suite exceeded ten minutes while advancing.");
            long batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (!world.IsCollecting && world.NodeCount + world.RegionCacheCount >= nextCollection)
                world.BeginCollection(roots);
            if (world.IsCollecting)
            {
                collectionBatches++;
                if (world.ContinueCollection(1024))
                    nextCollection = System.Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
                maxCollectionBatchMs = System.Math.Max(maxCollectionBatchMs,
                    (System.Diagnostics.Stopwatch.GetTimestamp() - batchStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency);
            }
            world.Pump(workBudget);
            maxBatchMs = System.Math.Max(maxBatchMs,
                (System.Diagnostics.Stopwatch.GetTimestamp() - batchStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency);
            pumpBatches++;
        }
        publications[publication] = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d
            / System.Diagnostics.Stopwatch.Frequency;
    }
    timer.Stop();
    double viewCpuMs = timer.Elapsed.TotalMilliseconds;
    bool collectingAtViewEnd = world.IsCollecting;
    int viewNodes = world.NodeCount;
    int viewRegions = world.RegionCacheCount;
    long cacheMisses = world.CacheMisses - cacheMissesBefore;
    long cacheHits = world.CacheHits - cacheHitsBefore;
    timer.Restart();
    while (world.IsCollecting)
    {
        if (clock.Elapsed.TotalSeconds > 600)
            throw new System.TimeoutException("Viewport cost suite exceeded ten minutes while completing post-view collection.");
        long batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        postViewCollectionBatches++;
        if (world.ContinueCollection(1024))
            nextCollection = System.Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
        maxCollectionBatchMs = System.Math.Max(maxCollectionBatchMs,
            (System.Diagnostics.Stopwatch.GetTimestamp() - batchStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency);
    }
    timer.Stop();
    double postViewCollectionMs = timer.Elapsed.TotalMilliseconds;
    double viewMaintenanceCpuMs = viewCpuMs + postViewCollectionMs;
    var allocationResult = StopAllocationRecording(allocations);
    timer.Restart();
    var export = world.CollectCellsAsync(false, default, default);
    while (!export.IsCompleted)
    {
        if (clock.Elapsed.TotalSeconds > 600)
            throw new System.TimeoutException("Viewport cost suite exceeded ten minutes while exporting.");
        long collectionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!world.IsCollecting && world.NodeCount + world.RegionCacheCount >= nextCollection)
            world.BeginCollection(roots);
        if (world.IsCollecting)
        {
            exportCollectionBatches++;
            if (world.ContinueCollection(1024))
                nextCollection = System.Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
            maxCollectionBatchMs = System.Math.Max(maxCollectionBatchMs,
                (System.Diagnostics.Stopwatch.GetTimestamp() - collectionStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency);
        }
        world.Pump(workBudget);
    }
    var cells = export.GetAwaiter().GetResult();
    timer.Stop();
    double exportCpuMs = timer.Elapsed.TotalMilliseconds;
    bool collectingAtExportEnd = world.IsCollecting;
    timer.Restart();
    while (world.IsCollecting)
    {
        if (clock.Elapsed.TotalSeconds > 600)
            throw new System.TimeoutException("Viewport cost suite exceeded ten minutes while completing post-export collection.");
        long batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        postExportCollectionBatches++;
        world.ContinueCollection(1024);
        maxCollectionBatchMs = System.Math.Max(maxCollectionBatchMs,
            (System.Diagnostics.Stopwatch.GetTimestamp() - batchStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency);
    }
    timer.Stop();
    double postExportCollectionMs = timer.Elapsed.TotalMilliseconds;
    var fingerprint = Fingerprint(cells, false, default, 0);
    var visibleFingerprint = Fingerprint(cells, true, origin, size);
    var viewFingerprint = ViewFingerprint(world);
    bool viewValid = viewFingerprint == visibleFingerprint;
    bool valid = world.Snapshot.Generation == generations && fingerprint.population == (long)expected["population"]
        && fingerprint.sum == (string)expected["sum"] && fingerprint.xor == (string)expected["xor"] && viewValid;
    if (!valid) failures++;
    bool historyEnabledAtRunEnd = UnityEditorInternal.ProfilerDriver.enabled;
    var result = new { run, size, originX = origin.x, originY = origin.y, exponent, step, publications,
        buildMs, initialStampMs, initialViewportMs,
        viewCpuMs, collectingAtViewEnd, postViewCollectionMs, viewMaintenanceCpuMs,
        exportCpuMs, collectingAtExportEnd, postExportCollectionMs,
        maxBatchMs, maxCollectionBatchMs, pumpBatches, collectionBatches, postViewCollectionBatches,
        exportCollectionBatches, postExportCollectionBatches, viewNodes, viewRegions,
        historyEnabledAtRunStart, historyEnabledAtRunEnd,
        cacheMisses, cacheHits, population = fingerprint.population, fingerprint.sum, fingerprint.xor,
        visiblePopulation = visibleFingerprint.population, visibleSum = visibleFingerprint.sum, visibleXor = visibleFingerprint.xor,
        nodesAfterExport = world.NodeCount, world.RegionCacheCount, allocationResult.events,
        allocationMeasurementValid = allocationCounterCalibrated && allocationResult.valid && !allocationResult.truncated,
        allocationResult.truncated, viewValid, valid };
    results.Add(result);
    System.IO.File.AppendAllText(progressPath,
        Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.None) + System.Environment.NewLine,
        new System.Text.UTF8Encoding(false));
}
string sourceSha256;
using (var sha = System.Security.Cryptography.SHA256.Create())
    sourceSha256 = System.BitConverter.ToString(sha.ComputeHash(sourceBytes)).Replace("-", "").ToLowerInvariant();
var output = new { tilePath, sourceSha256, inputGeneration = 22071, generations, failures, results,
    elapsedSeconds = clock.Elapsed.TotalSeconds, allocationCounterCalibrated, positiveControl, emptyControl,
    historyEnabledAtStart, historyEnabledAtEnd = UnityEditorInternal.ProfilerDriver.enabled,
    scope = "同一显示器第22071代输入，五轮轮换三个视野，按正式缩放策略分别使用1/32/512代请求，共完成512代。每轮新建世界并在计时前发布第0代视野，随后演化可复用本轮缓存，不作独立cold/warm比较。viewCpuMs为最后一次视野发布前的CPU时间；postViewCollectionMs单列已开始回收的尾部，viewMaintenanceCpuMs为两者之和。随后显式全图导出另计exportCpuMs，postExportCollectionMs单列导出后已有回收的尾部；尾部不发起新回收。maxBatchMs仅覆盖视野推进批次，maxCollectionBatchMs覆盖上述各阶段回收批次。最终对照独立22583代全图人口及双指纹。各视野工作范围不同，不作等工作量加速比。CPU循环不含真实帧等待、绘图或UI。分配记录视野推进及其回收尾部的主线程GC.Alloc事件，显式导出及其回收尾部分配不在此区间。history记录套件和每轮起止状态。" };
System.IO.File.WriteAllText("Tools/Benchmarks/Results/viewport-hex-costs.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(output, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
return new { path = "Tools/Benchmarks/Results/viewport-hex-costs.json", failures, output.elapsedSeconds,
    allocationCounterCalibrated, runs = results.Count };
