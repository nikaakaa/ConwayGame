using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using GameOfLife;
using Unity.Entities;
using Unity.Profiling;
using UnityEditor;
using UnityEngine;

public static class ObserveLifeFrames
{
    struct Frame
    {
        public int frame;
        public double gapMs;
        public long generation;
        public long population;
        public uint revision;
        public float speed;
        public double accumulator;
        public bool running;
        public int nodes;
        public int cacheEntries;
        public bool collecting;
        public bool advancing;
        public double playerLoopMs;
        public double simulationMs;
        public double advanceMs;
        public double collectionMs;
        public double rasterMs;
        public double behavioursMs;
        public double lateBehavioursMs;
        public double gcCollectMs;
        public double uiRenderMs;
        public double uiLayoutMs;
        public long gcAllocationEvents;
    }

    sealed class Observation
    {
        const double WarmupSeconds = 2;
        readonly Frame[] frames = new Frame[7200];
        readonly LifeController controller = UnityEngine.Object.FindFirstObjectByType<LifeController>();
        readonly LifeSimulationSystem system = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LifeSimulationSystem>();
        readonly ProfilerRecorder[] recorders;
        readonly double seconds;
        readonly string path;
        readonly long initialGeneration;
        readonly long initialPopulation;
        readonly bool historyEnabledAtStart;
        readonly Stopwatch watch = Stopwatch.StartNew();
        long previous;
        int lastFrame = -1;
        int count;

        public Observation(string label, int seconds)
        {
            this.seconds = seconds;
            path = "Temp/LifeBenchmarks/life-frames-" + label + ".json";
            initialGeneration = controller.State.Generation;
            initialPopulation = system.Simulation.Population;
            historyEnabledAtStart = UnityEditorInternal.ProfilerDriver.enabled;
            var options = ProfilerRecorderOptions.Default | ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
            string[] names = {
                "PlayerLoop", "Default World GameOfLife.LifeSimulationSystem",
                "LifeWorld:AdvanceJob (Burst)", "LifeWorld:CollectionJob (Burst)",
                "LifeRasterJob (Burst)", "Update.ScriptRunBehaviourUpdate",
                "PreLateUpdate.ScriptRunBehaviourLateUpdate", "GC.Collect",
                "LifeEditorPanel(Clone).Render", "LifeEditorPanel(Clone).ValidateLayout", "GC.Alloc"
            };
            var handles = new System.Collections.Generic.List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
            Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
            var descriptions = handles.Select(Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription).ToArray();
            recorders = new ProfilerRecorder[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                var description = descriptions.First(item => item.Name == names[i]);
                recorders[i] = ProfilerRecorder.StartNew(description.Category, description.Name, 1, options);
            }
        }

        public object Start()
        {
            previous = Stopwatch.GetTimestamp();
            EditorApplication.update += Sample;
            AssemblyReloadEvents.beforeAssemblyReload += Reload;
            return new { path, seconds, warmupSeconds = WarmupSeconds };
        }

        double Milliseconds(int index) => recorders[index].LastValue / 1000000d;

        void Sample()
        {
            if (!Application.isPlaying)
            {
                Finish("playStopped");
                return;
            }
            if (Time.frameCount == lastFrame)
                return;
            long now = Stopwatch.GetTimestamp();
            lastFrame = Time.frameCount;
            if (watch.Elapsed.TotalSeconds < WarmupSeconds)
            {
                previous = now;
                return;
            }
            var world = system.Simulation;
            var state = controller.State;
            var allocation = recorders[10];
            frames[count++] = new Frame {
                frame = lastFrame, gapMs = (now - previous) * 1000d / Stopwatch.Frequency,
                generation = state.Generation, population = world.Population,
                revision = state.Revision, speed = controller.Speed, accumulator = state.Accumulator,
                nodes = world.NodeCount, running = state.IsRunning,
                cacheEntries = world.CacheCount, collecting = world.IsCollecting,
                advancing = world.IsAdvancing, playerLoopMs = Milliseconds(0),
                simulationMs = Milliseconds(1), advanceMs = Milliseconds(2), collectionMs = Milliseconds(3),
                rasterMs = Milliseconds(4), behavioursMs = Milliseconds(5), lateBehavioursMs = Milliseconds(6),
                gcCollectMs = Milliseconds(7), uiRenderMs = Milliseconds(8), uiLayoutMs = Milliseconds(9),
                gcAllocationEvents = allocation.Count == 0 ? 0 : allocation.GetSample(allocation.Count - 1).Count
            };
            previous = now;
            if (watch.Elapsed.TotalSeconds >= seconds + WarmupSeconds || count == frames.Length)
                Finish("completed");
        }

        void Reload() => Finish("domainReload");

        void Finish(string reason)
        {
            EditorApplication.update -= Sample;
            AssemblyReloadEvents.beforeAssemblyReload -= Reload;
            bool markersValid = recorders.All(item => item.Valid);
            var markerSamples = recorders.Select(item => item.Count).ToArray();
            foreach (var recorder in recorders)
                recorder.Dispose();
            var captured = frames.Take(count).ToArray();
            var result = new {
                completedAtUtc = DateTime.UtcNow.ToString("O"), reason, markersValid,
                elapsedSeconds = watch.Elapsed.TotalSeconds, warmupSeconds = WarmupSeconds, speed = controller.Speed,
                population = system.Simulation.Population, sampleCount = count, frames = captured,
                initialGeneration, initialPopulation, historyEnabledAtStart,
                historyEnabledAtEnd = UnityEditorInternal.ProfilerDriver.enabled,
                markerSamples,
                scope = "目标 Editor 当前 Play 的被动主线程采样；帧间隔包含 Editor 等待，Profiler 指标对应最近完成帧，可能与状态读数相差一帧。GC.Alloc 仅记录事件数，不表示字节；此工具不改变棋盘或速度。"
            };
            Directory.CreateDirectory("Temp/LifeBenchmarks");
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }
    }

    public static object Main(string label, int seconds) => new Observation(label, seconds).Start();
}
