var reference = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(
    "Tools/Benchmarks/Results/viewport-reference.json", System.Text.Encoding.UTF8));
var results = new System.Collections.Generic.List<object>();
var failures = new System.Collections.Generic.List<object>();
var clock = System.Diagnostics.Stopwatch.StartNew();
const int workBudget = 128;

Unity.Mathematics.int2 Position(Newtonsoft.Json.Linq.JToken value) =>
    new Unity.Mathematics.int2((int)value[0], (int)value[1]);

GameOfLife.LifeTile[] Tiles(Newtonsoft.Json.Linq.JArray cells)
{
    var tiles = new System.Collections.Generic.Dictionary<Unity.Mathematics.int2, GameOfLife.LifeBits>();
    foreach (var item in cells)
    {
        var position = Position(item);
        var key = position & ~15;
        tiles.TryGetValue(key, out var bits);
        bits.Set(position.x & 15, position.y & 15, true);
        tiles[key] = bits;
    }
    return tiles.Select(item => new GameOfLife.LifeTile { Position = item.Key, Bits = item.Value }).ToArray();
}

(long population, string sum, string xor) Fingerprint(
    Unity.Mathematics.int2[] cells, Newtonsoft.Json.Linq.JToken region)
{
    bool all = (string)region["kind"] == "all";
    var min = all ? default : Position(region["min"]);
    var max = all ? default : Position(region["maxExclusive"]);
    long population = 0;
    ulong sum = 0, xor = 0;
    foreach (var cell in cells)
    {
        if (!all && (Unity.Mathematics.math.any(cell < min) || Unity.Mathematics.math.any(cell >= max)))
            continue;
        unchecked
        {
            ulong key = ((ulong)(uint)cell.x << 32) | (uint)cell.y;
            key = (key ^ (key >> 30)) * 0xbf58476d1ce4e5b9UL;
            key = (key ^ (key >> 27)) * 0x94d049bb133111ebUL;
            key ^= key >> 31;
            sum += key;
            xor ^= key;
            population++;
        }
    }
    return (population, sum.ToString("x16"), xor.ToString("x16"));
}

Unity.Mathematics.int2[] ViewCells(GameOfLife.LifeWorld world)
{
    var cells = new System.Collections.Generic.List<Unity.Mathematics.int2>();
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
                if (px >= bounds.MinX && py >= bounds.MinY && px < bounds.MaxX && py < bounds.MaxY)
                    cells.Add(new Unity.Mathematics.int2((int)px, (int)py));
            }
        }
    }
    Visit(frame.Shape.Root, frame.Shape.X, frame.Shape.Y);
    return cells.ToArray();
}

void SetRegion(GameOfLife.LifeWorld world, Newtonsoft.Json.Linq.JToken region)
{
    var min = Position(region["min"]);
    var extent = Position(region["maxExclusive"]) - min;
    int side = 8;
    while (side < Unity.Mathematics.math.cmax(extent))
        side <<= 1;
    world.SetViewport(min, side);
}

bool Matches((long population, string sum, string xor) actual, Newtonsoft.Json.Linq.JToken expected) =>
    actual.population == (long)expected["population"] && actual.sum == (string)expected["sum"]
        && actual.xor == (string)expected["xor"];

foreach (var input in reference["cases"])
foreach (string mode in new[] { "exact", "deferred-view", "retained-export" })
{
    string caseId = (string)input["id"];
    var caseTimer = System.Diagnostics.Stopwatch.StartNew();
    using var world = new GameOfLife.LifeWorld();
    var roots = new System.Collections.Generic.List<GameOfLife.LifeShape>();
    var captures = new System.Collections.Generic.Dictionary<string,
        (GameOfLife.LifeSnapshot snapshot, System.Threading.Tasks.Task<Unity.Mathematics.int2[]> task)>();
    var regions = input["regions"].ToDictionary(item => (string)item["id"]);
    var near = input["regions"].First(item => (string)item["kind"] == "rectangle");
    var checkpoints = input["checkpoints"].ToDictionary(item => (int)item["operationIndex"]);
    int nextCollection = 262144;
    int collectionBatches = 0;
    int pumpBatches = 0;
    int completeRegions = 0;
    double maxPumpMs = 0;
    double maxBatchMs = 0;
    double viewMs = 0;
    double exportMs = 0;
    void Pump()
    {
        if (clock.Elapsed.TotalSeconds > 180)
            throw new System.TimeoutException("Viewport correctness suite exceeded 180 seconds while processing " + caseId + "/" + mode + ".");
        long batchStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        if (!world.IsCollecting && world.NodeCount + world.RegionCacheCount >= nextCollection)
            world.BeginCollection(roots);
        if (world.IsCollecting)
        {
            collectionBatches++;
            if (world.ContinueCollection(128))
                nextCollection = System.Math.Max(262144, (world.NodeCount + world.RegionCacheCount) * 2);
        }
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        world.Pump(workBudget);
        double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        maxPumpMs = System.Math.Max(maxPumpMs, elapsed);
        maxBatchMs = System.Math.Max(maxBatchMs,
            (System.Diagnostics.Stopwatch.GetTimestamp() - batchStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency);
        pumpBatches++;
    }
    void CompleteView()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (world.IsAdvancing || !world.IsViewCurrent)
            Pump();
        viewMs += timer.Elapsed.TotalMilliseconds;
    }
    Unity.Mathematics.int2[] CompleteExport(System.Threading.Tasks.Task<Unity.Mathematics.int2[]> task)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (!task.IsCompleted)
            Pump();
        exportMs += timer.Elapsed.TotalMilliseconds;
        return task.GetAwaiter().GetResult();
    }
    void CheckRegions(Unity.Mathematics.int2[] cells, Newtonsoft.Json.Linq.JToken checkpoint, GameOfLife.LifeSnapshot snapshot)
    {
        foreach (var expected in checkpoint["regions"])
        {
            string regionId = (string)expected["id"];
            var actual = Fingerprint(cells, regions[regionId]);
            bool valid = Matches(actual, expected) && snapshot.Generation == (long)checkpoint["generation"];
            results.Add(new { caseId, mode, checkpoint = (string)checkpoint["id"], region = regionId,
                actual.population, actual.sum, actual.xor, valid, generation = snapshot.Generation,
                epoch = snapshot.Epoch, observedGeneration = world.Snapshot.Generation });
            if (!valid)
                failures.Add(new { caseId, mode, checkpoint = (string)checkpoint["id"], region = regionId,
                    actual.population, actual.sum, actual.xor, expected });
            completeRegions++;
        }
    }
    void CheckView(Newtonsoft.Json.Linq.JToken checkpoint, Newtonsoft.Json.Linq.JToken region)
    {
        SetRegion(world, region);
        CompleteView();
        string regionId = (string)region["id"];
        var expected = checkpoint["regions"].First(item => (string)item["id"] == regionId);
        var actual = Fingerprint(ViewCells(world), region);
        bool valid = Matches(actual, expected) && world.Snapshot.Generation == (long)checkpoint["generation"]
            && world.ViewFrame.Snapshot.Generation == world.Snapshot.Generation
            && world.ViewFrame.Snapshot.Epoch == world.Snapshot.Epoch;
        results.Add(new { caseId, mode, checkpoint = (string)checkpoint["id"], view = regionId,
            actual.population, actual.sum, actual.xor, valid, generation = world.Snapshot.Generation,
            epoch = world.Snapshot.Epoch, regionCache = world.RegionCacheCount, nodes = world.NodeCount });
        if (!valid)
            failures.Add(new { caseId, mode, checkpoint = (string)checkpoint["id"], view = regionId,
                actual.population, actual.sum, actual.xor, expected });
    }
    var setupTimer = System.Diagnostics.Stopwatch.StartNew();
    var initialShape = world.BuildShape(Tiles((Newtonsoft.Json.Linq.JArray)input["initialCells"]));
    double initialBuildMs = setupTimer.Elapsed.TotalMilliseconds;
    roots.Add(initialShape);
    SetRegion(world, near);
    setupTimer.Restart();
    world.BeginEdit(); world.Stamp(initialShape, Unity.Mathematics.int2.zero); world.EndEdit();
    double initialStampMs = setupTimer.Elapsed.TotalMilliseconds;
    double editMs = 0;
    CheckView(checkpoints[-1], near);
    if (mode == "exact")
        CheckRegions(CompleteExport(world.CollectCellsAsync(false, default, default)), checkpoints[-1], world.Snapshot);
    var operations = (Newtonsoft.Json.Linq.JArray)input["operations"];
    for (int operationIndex = 0; operationIndex < operations.Count; operationIndex++)
    {
        var operation = operations[operationIndex];
        var checkpoint = checkpoints[operationIndex];
        int exportsPendingAtOperation = captures.Values.Count(item => !item.task.IsCompleted);
        long operationStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        switch ((string)operation["kind"])
        {
            case "advance":
                SetRegion(world, near);
                CompleteView();
                long remaining = (long)operation["generations"];
                while (remaining != 0)
                {
                    int exponent = 0;
                    while ((1L << (exponent + 1)) <= remaining)
                        exponent++;
                    world.BeginAdvance(exponent);
                    CompleteView();
                    remaining -= 1L << exponent;
                }
                break;
            case "cells":
                world.BeginEdit();
                foreach (var write in operation["writes"])
                    world.SetCell(new Unity.Mathematics.int2((int)write["x"], (int)write["y"]), (bool)write["alive"]);
                world.EndEdit();
                break;
            case "stamp":
                var shape = world.BuildShape(Tiles((Newtonsoft.Json.Linq.JArray)operation["cells"]));
                roots.Add(shape);
                world.BeginEdit(); world.Stamp(shape, Position(operation["anchor"])); world.EndEdit();
                break;
            case "clearArea":
                world.ClearArea(Position(operation["min"]), Position(operation["maxExclusive"]) - 1);
                break;
        }
        if ((string)operation["kind"] != "advance")
            editMs += (System.Diagnostics.Stopwatch.GetTimestamp() - operationStarted) * 1000d / System.Diagnostics.Stopwatch.Frequency;
        CheckView(checkpoint, near);
        if (mode == "exact")
        {
            CheckRegions(CompleteExport(world.CollectCellsAsync(false, default, default)), checkpoint, world.Snapshot);
            var selected = regions.Values.First(item => (string)item["kind"] == "rectangle");
            var selectedCells = CompleteExport(world.CollectCellsAsync(true, Position(selected["min"]), Position(selected["maxExclusive"]) - 1));
            var expected = checkpoint["regions"].First(item => (string)item["id"] == (string)selected["id"]);
            bool selectedValid = Matches(Fingerprint(selectedCells, selected), expected);
            if (!selectedValid)
                failures.Add(new { caseId, mode, checkpoint = (string)checkpoint["id"], selectedValid });
        }
        else
        {
            foreach (var query in input["queryPlan"].Where(item => (int)item["afterOperationIndex"] == operationIndex
                && item["capture"] == null && (string)item["region"] != "all"))
                CheckView(checkpoint, regions[(string)query["region"]]);
        }
        if (mode == "retained-export" && operation["capture"] != null)
            captures.Add((string)operation["capture"], (world.Snapshot, world.CollectCellsAsync(false, default, default)));
        if (operationIndex == 3 && !world.IsCollecting)
            world.BeginCollection(roots);
        results.Add(new { caseId, mode, operationIndex, exportsPendingAtOperation,
            nodes = world.NodeCount, epochs = world.EpochCount, regionCache = world.RegionCacheCount });
    }
    if (mode != "exact")
        CheckRegions(CompleteExport(world.CollectCellsAsync(false, default, default)), checkpoints[operations.Count - 1], world.Snapshot);
    foreach (var capture in input["retainedCaptures"])
    {
        if (mode != "retained-export")
            break;
        string id = (string)capture["id"];
        var pending = captures[id];
        CheckRegions(CompleteExport(pending.task), checkpoints[(int)capture["requestedAtOperationIndex"]], pending.snapshot);
    }
    results.Add(new { caseId, mode, summary = true, pumpBatches, collectionBatches, maxPumpMs, maxBatchMs,
        viewMs, exportMs, initialBuildMs, initialStampMs, editMs, caseMs = caseTimer.Elapsed.TotalMilliseconds,
        completeRegions, nodes = world.NodeCount, epochs = world.EpochCount,
        regionCache = world.RegionCacheCount, world.CacheHits, world.CacheMisses });
}

int[] viewSizes = { 8, 128, 256, 4096, 32768, 67108864 };
float[] speeds = { 1, 10, 60, 2560, 20480, GameOfLife.LifeController.MaxSpeed };
var policyRows = new System.Collections.Generic.List<object>();
foreach (int size in viewSizes)
foreach (float speed in speeds)
{
    int exponent = GameOfLife.LifeSimulationPolicy.StepExponent(size, speed);
    policyRows.Add(new { size, speed, exponent, step = 1L << exponent });
}
var policy = new GameOfLife.LifeSimulationPolicy(0.001);
double initialBudget = policy.BudgetSeconds;
for (int i = 0; i < 30; i++) policy.BeginFrame(0.020);
double slowFrameBudget = policy.BudgetSeconds;
for (int i = 0; i < 60; i++) policy.BeginFrame(0.010);
double recoveredBudget = policy.BudgetSeconds;
var output = new { referenceToolSha256 = (string)reference["toolSha256"], failures = failures.Count,
    failureDetails = failures, elapsedSeconds = clock.Elapsed.TotalSeconds, results, policyRows,
    budget = new { initialBudget, slowFrameBudget, recoveredBudget },
    scope = "正式 LifeWorld 与独立参考的区域、全图、同代/跨代编辑和固定旧快照对照。deferred-view 在末尾精确导出前不提出任何全图需求。retained-export 的 viewMs 包含等待视野时推进的并行导出工作，exportMs 只计随后等待任务完成的剩余CPU时间，不能作为两个独立工作阶段比较。maxPumpMs只计world.Pump，maxBatchMs包含该次节点回收和world.Pump。CPU 计时不含真实帧等待、渲染和 UI；本工具不据此报告 FPS 或鼠标响应。" };
System.IO.File.WriteAllText("Tools/Benchmarks/Results/viewport-current.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(output, Newtonsoft.Json.Formatting.Indented), new System.Text.UTF8Encoding(false));
return new { path = "Tools/Benchmarks/Results/viewport-current.json", output.failures, output.elapsedSeconds,
    rows = results.Count };
