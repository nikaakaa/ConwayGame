GameOfLife.LifeTile[] tiles;
using (var reader = new System.IO.BinaryReader(System.IO.File.OpenRead("Tools/Benchmarks/Work/hex-22071-tiles.bin")))
{
    tiles = new GameOfLife.LifeTile[reader.ReadInt32()];
    for (int i = 0; i < tiles.Length; i++)
        tiles[i] = new GameOfLife.LifeTile {
            Position = new Unity.Mathematics.int2(reader.ReadInt32(), reader.ReadInt32()),
            Bits = new GameOfLife.LifeBits { A = reader.ReadUInt64(), B = reader.ReadUInt64(), C = reader.ReadUInt64(), D = reader.ReadUInt64() }
        };
}
var rows = new System.Collections.Generic.List<object>();
Unity.Mathematics.int2[] expectedCells = null;
int failures = 0;
bool historyEnabledAtStart = UnityEditorInternal.ProfilerDriver.enabled;
for (int run = 0; run < 2; run++)
{
    bool collect = run == 1;
    using var world = new GameOfLife.LifeWorld();
    var shape = world.BuildShape(tiles);
    var roots = new System.Collections.Generic.List<GameOfLife.LifeShape> { shape };
    world.SetViewport(new Unity.Mathematics.int2(-16408, -12216), 4096);
    world.BeginEdit(); world.Stamp(shape, Unity.Mathematics.int2.zero); world.EndEdit();
    var publications = new double[512];
    int nextCollection = 262144, batches = 0, collectionBatches = 0, completedRequests = 0;
    double maxBatchMs = 0;
    var timer = System.Diagnostics.Stopwatch.StartNew();
    for (int request = 0; request < publications.Length; request++)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        world.BeginAdvance(5);
        while ((world.IsAdvancing || !world.IsViewCurrent) && timer.Elapsed.TotalSeconds < 60)
        {
            long batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            if (collect && !world.IsCollecting && world.NodeCount + world.RegionCacheCount >= nextCollection)
                world.BeginCollection(roots);
            if (world.IsCollecting)
            {
                collectionBatches++;
                if (world.ContinueCollection(1024))
                    nextCollection = System.Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
            }
            world.Pump(128);
            maxBatchMs = System.Math.Max(maxBatchMs, (System.Diagnostics.Stopwatch.GetTimestamp() - batchStarted)
                * 1000d / System.Diagnostics.Stopwatch.Frequency);
            batches++;
        }
        publications[request] = (System.Diagnostics.Stopwatch.GetTimestamp() - started)
            * 1000d / System.Diagnostics.Stopwatch.Frequency;
        if (world.IsAdvancing || !world.IsViewCurrent)
            break;
        completedRequests++;
    }
    timer.Stop();
    double advanceMs = timer.Elapsed.TotalMilliseconds;
    int nodes = world.NodeCount, regions = world.RegionCacheCount;
    long hits = world.CacheHits, misses = world.CacheMisses;
    var exportTimer = System.Diagnostics.Stopwatch.StartNew();
    var export = world.CollectCellsAsync(false, default, default);
    while (!export.IsCompleted && exportTimer.Elapsed.TotalSeconds < 60)
    {
        if (world.IsCollecting) world.ContinueCollection(1024);
        world.Pump(128);
    }
    if (!export.IsCompleted) throw new System.TimeoutException("长跑的精确导出未在60秒内完成。");
    var cells = export.GetAwaiter().GetResult();
    double exportMs = exportTimer.Elapsed.TotalMilliseconds;
    System.Array.Sort(cells, (left, right) => left.x != right.x ? left.x.CompareTo(right.x) : left.y.CompareTo(right.y));
    bool coordinatesEqual = true;
    if (run == 0) expectedCells = cells;
    else coordinatesEqual = cells.Length == expectedCells.Length && cells.SequenceEqual(expectedCells);
    bool valid = completedRequests == 512 && world.Snapshot.Generation == 16384 && coordinatesEqual;
    if (!valid) failures++;
    rows.Add(new { collect, completedRequests, generation = world.Snapshot.Generation, advanceMs, exportMs,
        publications, batches, collectionBatches, maxBatchMs, nodes, regions, hits, misses,
        population = cells.LongLength, coordinatesEqual, valid });
}
var result = new { completedAtUtc = System.DateTime.UtcNow.ToString("O"), failures, rows,
    historyEnabledAtStart, historyEnabledAtEnd = UnityEditorInternal.ProfilerDriver.enabled,
    scope = "同一第22071代显示器输入、4096格窗口，分别诊断停用回收和正式回收，各512次32代请求至16384代。两个新世界只作一次顺序采样，不报告精确加速比；停用回收仅为诊断。导出后排序比较完整坐标，排序不计时；exportMs含精确导出和已在途回收，输入准备/绘图/UI排除。不记录GC.Alloc，不据此声称零分配或FPS。" };
System.IO.File.WriteAllText("Tools/Benchmarks/Results/viewport-long.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
return new { path = "Tools/Benchmarks/Results/viewport-long.json", failures };
