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

var results = new List<object>();
const int repetitions = 5;
const int budget = 128;
const double limitSeconds = 15;

GameOfLife.LifePattern Blocks()
{
    var bits = new GameOfLife.LifeBits();
    for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            if ((x & 3) < 2 && (y & 3) < 2)
                bits.Set(x, y, true);
    var tiles = new GameOfLife.LifeTile[128 * 128];
    for (int i = 0; i < tiles.Length; i++)
        tiles[i] = new GameOfLife.LifeTile {
            Position = new Unity.Mathematics.int2((i % 128) * 16 - 1024, (i / 128) * 16 - 1024),
            Bits = bits
        };
    return new GameOfLife.LifePattern("重复方块 1048576", "基准", 1,
        Unity.Mathematics.int2.zero, tiles, "");
}

GameOfLife.LifePattern Chaos()
{
    var random = new Unity.Mathematics.Random(0x12345678u);
    var tiles = new GameOfLife.LifeTile[16 * 16];
    for (int i = 0; i < tiles.Length; i++)
        tiles[i] = new GameOfLife.LifeTile {
            Position = new Unity.Mathematics.int2((i % 16) * 16 + 4096, (i / 16) * 16 + 4096),
            Bits = new GameOfLife.LifeBits {
                A = ((ulong)random.NextUInt() << 32) | random.NextUInt(),
                B = ((ulong)random.NextUInt() << 32) | random.NextUInt(),
                C = ((ulong)random.NextUInt() << 32) | random.NextUInt(),
                D = ((ulong)random.NextUInt() << 32) | random.NextUInt()
            }
        };
    return new GameOfLife.LifePattern("随机 256x256 seed12345678", "基准", 0,
        Unity.Mathematics.int2.zero, tiles, "");
}

void Put(GameOfLife.LifeWorld world, GameOfLife.LifeShape shape)
{
    world.Clear();
    world.BeginEdit();
    world.Stamp(shape, Unity.Mathematics.int2.zero);
    world.EndEdit();
}

var blocks = Blocks();
var chaos = Chaos();
var mixedTiles = new GameOfLife.LifeTile[blocks.Tiles.Length + chaos.Tiles.Length];
Array.Copy(blocks.Tiles, 0, mixedTiles, 0, blocks.Tiles.Length);
Array.Copy(chaos.Tiles, 0, mixedTiles, blocks.Tiles.Length, chaos.Tiles.Length);
var mixed = new GameOfLife.LifePattern("重复方块加随机区域", "基准", 0,
    Unity.Mathematics.int2.zero, mixedTiles, "");
var cases = new List<(string name, GameOfLife.LifePattern pattern, int exponent, long expectedPopulation)> {
    ("glider", GameOfLife.LifePatterns.Items[4], 12, 5),
    ("repeated-blocks", blocks, 16, blocks.Population),
    ("chaos", chaos, 3, -1),
    ("mixed", mixed, 5, -1)
};

using (var warmup = new GameOfLife.LifeWorld())
{
    var shape = warmup.BuildShape(GameOfLife.LifePatterns.Items[4].Tiles);
    Put(warmup, shape);
    warmup.BeginAdvance(3);
    while (!warmup.ContinueAdvance(budget)) { }
}

int failures = 0;
foreach (var item in cases)
{
    for (int run = 0; run < repetitions; run++)
    {
        using var world = new GameOfLife.LifeWorld();
        var initial = world.BuildShape(item.pattern.Tiles);
        GameOfLife.LifeShape coldResult = default;
        long coldPopulation = 0;
        for (int cache = 0; cache < 2; cache++)
        {
            Put(world, initial);
            long hits = world.CacheHits;
            long misses = world.CacheMisses;
            var stopwatch = new System.Diagnostics.Stopwatch();
            var batchWatch = new System.Diagnostics.Stopwatch();
            double maxBatchMs = 0;
            int batches = 0;
            using var gcRecorder = StartGcAllocationRecording();
            stopwatch.Start();
            world.BeginAdvance(item.exponent);
            bool complete;
            do
            {
                batchWatch.Restart();
                complete = world.ContinueAdvance(budget);
                batchWatch.Stop();
                maxBatchMs = Math.Max(maxBatchMs, batchWatch.Elapsed.TotalMilliseconds);
                batches++;
            } while (!complete && stopwatch.Elapsed.TotalSeconds < limitSeconds);
            stopwatch.Stop();
            var gcAllocations = ReadGcAllocationResult(gcRecorder);

            bool sameAsCold = true;
            if (cache == 0)
            {
                coldResult = world.Shape;
                coldPopulation = world.Population;
            }
            else
                sameAsCold = world.Shape.Root == coldResult.Root && world.Shape.X == coldResult.X
                    && world.Shape.Y == coldResult.Y && world.Population == coldPopulation;

            bool expectedPopulation = item.expectedPopulation < 0 || world.Population == item.expectedPopulation;
            bool gliderPosition = item.name != "glider"
                || world.GetCell(new Unity.Mathematics.int2(1024, -1023))
                    && world.GetCell(new Unity.Mathematics.int2(1025, -1024))
                    && world.GetCell(new Unity.Mathematics.int2(1023, -1025))
                    && world.GetCell(new Unity.Mathematics.int2(1024, -1025))
                    && world.GetCell(new Unity.Mathematics.int2(1025, -1025));
            bool valid = complete && sameAsCold && expectedPopulation && gliderPosition;
            if (!valid)
                failures++;
            results.Add(new {
                operation = "advance", name = item.name, run, cache = cache == 0 ? "cold" : "warm",
                initialPopulation = item.pattern.Population, actualPopulation = world.Population,
                requestedGenerations = 1L << item.exponent,
                milliseconds = stopwatch.Elapsed.TotalMilliseconds, maxBatchMs, batches, budget,
                gcAllocations, nodes = world.NodeCount, cacheEntries = world.CacheCount,
                cacheHits = world.CacheHits - hits, cacheMisses = world.CacheMisses - misses,
                complete, sameAsCold, expectedPopulation, gliderPosition, valid
            });
            if (!complete)
                world.CancelAdvance();
        }
    }
}

long sequentialPopulation = -1;
for (int run = 0; run < repetitions; run++)
{
    using var world = new GameOfLife.LifeWorld();
    Put(world, world.BuildShape(chaos.Tiles));
    var stopwatch = new System.Diagnostics.Stopwatch();
    var batchWatch = new System.Diagnostics.Stopwatch();
    double maxBatchMs = 0;
    long generations = 0;
    int batches = 0;
    bool complete = true;
    using var gcRecorder = StartGcAllocationRecording();
    stopwatch.Start();
    for (int phase = 0; phase < 16; phase++)
    {
        world.BeginAdvance(3);
        bool finished;
        do
        {
            batchWatch.Restart();
            finished = world.ContinueAdvance(budget);
            batchWatch.Stop();
            maxBatchMs = Math.Max(maxBatchMs, batchWatch.Elapsed.TotalMilliseconds);
            batches++;
        } while (!finished && stopwatch.Elapsed.TotalSeconds < limitSeconds);
        if (!finished)
        {
            complete = false;
            break;
        }
        generations += 8;
    }
    stopwatch.Stop();
    var gcAllocations = ReadGcAllocationResult(gcRecorder);
    if (run == 0)
        sequentialPopulation = world.Population;
    bool valid = complete && generations == 128 && world.Population == sequentialPopulation;
    if (!valid)
        failures++;
    results.Add(new {
        operation = "sequential", name = "chaos-128-generations", run,
        milliseconds = stopwatch.Elapsed.TotalMilliseconds, gcAllocations,
        actualPopulation = world.Population, generations, maxBatchMs, batches,
        nodes = world.NodeCount, cacheHits = world.CacheHits, cacheMisses = world.CacheMisses, valid
    });
}

using (var entityWorld = new Unity.Entities.World("Life input benchmark", Unity.Entities.WorldFlags.Game))
{
    var system = entityWorld.GetOrCreateSystemManaged<GameOfLife.LifeSimulationSystem>();
    var manager = entityWorld.EntityManager;
    using var query = manager.CreateEntityQuery(Unity.Entities.ComponentType.ReadOnly<GameOfLife.LifeState>());
    var board = query.GetSingletonEntity();
    var initial = system.Simulation.BuildShape(blocks.Tiles);
    var stopwatch = new System.Diagnostics.Stopwatch();
    for (int run = -1; run < repetitions; run++)
    {
        Put(system.Simulation, initial);
        manager.SetComponentData(board, new GameOfLife.LifeState { Revision = 1 });
        var commands = manager.GetBuffer<GameOfLife.LifeCommand>(board);
        commands.Clear();
        commands.Add(new GameOfLife.LifeCommand {
            Kind = GameOfLife.LifeCommandKind.CellStroke,
            Position = new Unity.Mathematics.int2(-512, -4096),
            End = new Unity.Mathematics.int2(511, -4096),
            Alive = true
        });
        using var gcRecorder = StartGcAllocationRecording();
        int frames = 0;
        stopwatch.Restart();
        do
        {
            system.Update();
            frames++;
        } while (manager.GetBuffer<GameOfLife.LifeCommand>(board, true).Length != 0
            && stopwatch.Elapsed.TotalSeconds < limitSeconds);
        stopwatch.Stop();
        var gcAllocations = ReadGcAllocationResult(gcRecorder);
        bool valid = system.Simulation.Population == blocks.Population + 1024
            && manager.GetBuffer<GameOfLife.LifeCommand>(board, true).Length == 0;
        if (!valid)
            failures++;
        if (run >= 0)
            results.Add(new {
                operation = "stroke", name = "1024-cells-on-1048576", run,
                milliseconds = stopwatch.Elapsed.TotalMilliseconds, gcAllocations,
                actualPopulation = system.Simulation.Population, frames, valid
            });
    }
}
using (var world = new GameOfLife.LifeWorld())
using (var stack = new Unity.Collections.NativeList<GameOfLife.LifeDrawTask>(256, Unity.Collections.Allocator.Persistent))
{
    Put(world, world.BuildShape(blocks.Tiles));
    var texture = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
    foreach (int visible in new[] { 128, 65536, 67108864 })
    {
        for (int run = -1; run < repetitions; run++)
        {
            var pixels = texture.GetRawTextureData<Color32>();
            var job = new GameOfLife.LifeRasterJob {
                Nodes = world.Nodes, Shape = world.Shape, Pixels = pixels, Stack = stack,
                Origin = new Unity.Mathematics.int2(-visible / 2, -visible / 2), VisibleCells = visible
            };
            var stopwatch = new System.Diagnostics.Stopwatch();
            using var gcRecorder = StartGcAllocationRecording();
            stopwatch.Start();
            Unity.Jobs.IJobExtensions.Run(job);
            stopwatch.Stop();
            var gcAllocations = ReadGcAllocationResult(gcRecorder);
            double rasterMs = stopwatch.Elapsed.TotalMilliseconds;
            stopwatch.Restart();
            texture.Apply(false, false);
            stopwatch.Stop();
            int whitePixels = 0;
            for (int i = 0; i < pixels.Length; i++)
                if (pixels[i].r == 255)
                    whitePixels++;
            bool valid = whitePixels > 0;
            if (!valid)
                failures++;
            if (run >= 0)
                results.Add(new {
                    operation = "raster", name = "1048576-cells", run, visible,
                    milliseconds = rasterMs, uploadCpuMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
                    gcAllocations, whitePixels, valid
                });
        }
    }
    UnityEngine.Object.DestroyImmediate(texture);
}
var result = new {
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    unity = Application.unityVersion, burstEnabled = Unity.Burst.BurstCompiler.IsEnabled,
    processor = SystemInfo.processorType, logicalProcessors = SystemInfo.processorCount,
    repetitions, budget, limitSeconds, gcAllocationCalibration, failures, results
};
System.IO.Directory.CreateDirectory("Temp/LifeBenchmarks");
System.IO.File.WriteAllText("Temp/LifeBenchmarks/MeasureLife-result.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
