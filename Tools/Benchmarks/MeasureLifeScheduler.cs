string[] request = System.IO.File.ReadAllLines("Temp/LifeBenchmarks/request.txt", System.Text.Encoding.UTF8);
string input = System.IO.File.ReadAllText(request[0], System.Text.Encoding.UTF8);
int repetitions = int.Parse(request[1], System.Globalization.CultureInfo.InvariantCulture);
bool parsed = GameOfLife.LifePatternText.TryParse(input, "Life-in-Life", out var pattern, out string parseError);
if (!parsed)
    return new { parsed, parseError };

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
    manager.SetComponentData(board, new GameOfLife.LifeSettings { GenerationsPerSecond = 35328 });
    manager.SetComponentData(board, new GameOfLife.LifeState { Revision = 1 });
    manager.GetBuffer<GameOfLife.LifeCommand>(board).Add(new GameOfLife.LifeCommand { Kind = GameOfLife.LifeCommandKind.Play });
    world.SetTime(new Unity.Core.TimeData(0, 0));
    do
        system.Update();
    while (!manager.GetComponentData<GameOfLife.LifeState>(board).IsRunning && watch.Elapsed.TotalSeconds < 60);
    watch.Stop();
    double prepareMs = watch.Elapsed.TotalMilliseconds;
    world.SetTime(new Unity.Core.TimeData(1, 1));
    int updates = 0;
    int peakNodes = system.Simulation.NodeCount;
    double maximumUpdateMs = 0;
    GameOfLife.LifeState state;
    long allocated = GC.GetAllocatedBytesForCurrentThread();
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
        world.SetTime(new Unity.Core.TimeData(1, 0));
    } while (state.Generation < 35328 && watch.Elapsed.TotalSeconds < 60);
    watch.Stop();
    allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
    var fingerprint = Fingerprint(system.Simulation);
    bool referenceMatches = fingerprint.sum == request[2] && fingerprint.xor == request[3];
    bool complete = state.Generation == 35328 && !system.Simulation.IsAdvancing;
    bool valid = complete && referenceMatches;
    if (!valid)
        failures++;
    results.Add(new {
        run, prepareMs, updates, maximumUpdateMs, milliseconds = watch.Elapsed.TotalMilliseconds,
        allocatedBytes = allocated, peakNodes, nodes = system.Simulation.NodeCount,
        generation = state.Generation, population = system.Simulation.Population,
        fingerprint.sum, fingerprint.xor, referenceMatches, complete, valid
    });
}
return new {
    unity = Application.unityVersion, inputPath = request[0], repetitions, failures, results,
    scope = "Production System updates with synthetic time; excludes raster, UI, actual frame pacing and input preparation."
};
