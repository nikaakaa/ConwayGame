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

var expectedPopulations = new Dictionary<string, long>
{
    ["gunstar.rle"] = 736,
    ["p690-cordership-gun.rle"] = 2473,
    ["breeder1.rle"] = 4060,
    ["c5greyship.rle"] = 8056,
    ["primer.rle"] = 2953,
    ["turingmachine.rle"] = 36549,
    ["spacefiller.rle"] = 200,
    ["metapixel-galaxy.rle"] = 7408195
};
var previewType = typeof(GameOfLife.LifePatternLibrary).GetNestedType("Preview", System.Reflection.BindingFlags.NonPublic);
var constructor = previewType.GetConstructor(new[] { typeof(GameOfLife.LifePattern), typeof(int), typeof(bool) });
var textureProperty = previewType.GetProperty("Texture");
var seedField = previewType.GetField("seed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
using (var warm = (IDisposable)constructor.Invoke(new object[] { GameOfLife.LifePatterns.Items[0], 48, false })) { }
int VerifyPixels(GameOfLife.LifePattern pattern, Texture2D texture)
{
    var minimum = new Unity.Mathematics.int2(int.MaxValue);
    var maximum = new Unity.Mathematics.int2(int.MinValue);
    foreach (var tile in pattern.Tiles)
        for (int rowIndex = 0; rowIndex < 16; rowIndex++)
        {
            uint row = tile.Bits.GetRow(rowIndex);
            while (row != 0)
            {
                int x = Unity.Mathematics.math.tzcnt(row);
                row &= row - 1;
                var position = tile.Position + new Unity.Mathematics.int2(x, rowIndex);
                minimum = Unity.Mathematics.math.min(minimum, position);
                maximum = Unity.Mathematics.math.max(maximum, position);
            }
        }
    var size = maximum - minimum + 1;
    int margin = Unity.Mathematics.math.max(3, Unity.Mathematics.math.cmax(size) / 5);
    var canvas = Unity.Mathematics.math.max(size + 2 * margin, 12);
    var origin = minimum - (canvas - size) / 2;
    int scale = Unity.Mathematics.math.max(1, (Unity.Mathematics.math.cmax(canvas) + 47) / 48);
    var texel = (canvas + scale - 1) / scale;
    if (texture.width != texel.x || texture.height != texel.y)
        return -1;
    var expected = new bool[texel.x * texel.y];
    foreach (var tile in pattern.Tiles)
        for (int rowIndex = 0; rowIndex < 16; rowIndex++)
        {
            uint row = tile.Bits.GetRow(rowIndex);
            while (row != 0)
            {
                int x = Unity.Mathematics.math.tzcnt(row);
                row &= row - 1;
                var local = tile.Position + new Unity.Mathematics.int2(x, rowIndex) - origin;
                var pixel = local / scale;
                expected[pixel.y * texel.x + pixel.x] = true;
            }
        }
    var actual = texture.GetRawTextureData<Color32>();
    int differences = 0;
    for (int i = 0; i < expected.Length; i++)
        if (expected[i] != (actual[i].a != 0))
            differences++;
    return differences;
}
var records = new List<object>();
int failures = 0;
foreach (var source in expectedPopulations)
{
    string path = System.IO.Path.Combine(Application.streamingAssetsPath, "LifePatterns", source.Key);
    var watch = System.Diagnostics.Stopwatch.StartNew();
    string text = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
    bool parsed = GameOfLife.LifePatternText.TryParse(text, source.Key, out var pattern, out string error);
    watch.Stop();
    double loadMs = watch.Elapsed.TotalMilliseconds;
    if (!parsed)
    {
        failures++;
        records.Add(new { file = source.Key, parsed, error });
        continue;
    }
    using var world = new GameOfLife.LifeWorld();
    var published = world.Shape;
    var shape = world.BuildShape(pattern.Tiles);
    bool populationMatches = world.Nodes[shape.Root].Population == source.Value && pattern.Population == source.Value;
    bool publishedUnchanged = world.Shape.Root == published.Root && world.Shape.X == published.X && world.Shape.Y == published.Y;
    int repetitions = source.Key == "metapixel-galaxy.rle" ? 5 : 1;
    for (int run = 0; run < repetitions; run++)
    {
        object[] args = { pattern, 48, false };
        using var gcRecorder = StartGcAllocationRecording();
        watch.Restart();
        using var preview = (IDisposable)constructor.Invoke(args);
        watch.Stop();
        var gcAllocations = ReadGcAllocationResult(gcRecorder);
        var texture = (Texture2D)textureProperty.GetValue(preview);
        int differences = run == 0 ? VerifyPixels(pattern, texture) : 0;
        bool expanded = seedField.GetValue(preview) != null;
        bool valid = populationMatches && publishedUnchanged && differences == 0 && (pattern.Population <= 2000 || !expanded);
        if (!valid)
            failures++;
        records.Add(new {
            file = source.Key, pattern.Name, pattern.Population, tiles = pattern.Tiles.Length, run,
            loadMs, previewMs = watch.Elapsed.TotalMilliseconds, gcAllocations,
            texture.width, texture.height, expanded, differences, populationMatches, publishedUnchanged, valid
        });
    }
}
var result = new {
    completedAtUtc = DateTime.UtcNow.ToString("O"),
    gcAllocationCalibration, failures, records,
    scope = "Actual library Preview constructor through reflection; includes initial native bitmap copy and texture upload, excludes verification and layout. Large preview repeated five times. GC.Alloc records main-thread allocation events, not allocated bytes; initialization allocations do not fail the behavior checks."
};
System.IO.Directory.CreateDirectory("Temp/LifeBenchmarks");
System.IO.File.WriteAllText("Temp/LifeBenchmarks/MeasureLifeCatalog-result.json",
    Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented),
    new System.Text.UTF8Encoding(false));
return result;
