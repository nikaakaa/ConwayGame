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
        public long populationSampleGeneration;
        public int populationSampleEpoch;
        public uint revision;
        public float speed;
        public int viewportOriginX;
        public int viewportOriginY;
        public int visibleCells;
        public double frameSeconds;
        public int policyStepExponent;
        public long policyStepGenerations;
        public double accumulator;
        public bool running;
        public int nodes;
        public int cacheEntries;
        public int regionCacheEntries;
        public int epochs;
        public bool pendingWork;
        public bool viewCurrent;
        public bool collecting;
        public bool advancing;
        public double playerLoopMs;
        public double simulationMs;
        public double regionPumpMs;
        public double collectionMs;
        public double rasterMs;
        public double overlayRasterMs;
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
        readonly EntityManager manager;
        readonly Entity board;
        readonly ProfilerRecorder[] recorders;
        readonly double seconds;
        readonly string path;
        readonly long initialGeneration;
        readonly long initialPopulation;
        readonly long initialPopulationSampleGeneration;
        readonly int initialPopulationSampleEpoch;
        readonly bool historyEnabledAtStart;
        readonly Stopwatch watch = Stopwatch.StartNew();
        long previous;
        int lastFrame = -1;
        int count;

        public Observation(string label, int seconds)
        {
            this.seconds = seconds;
            path = "Tools/Benchmarks/Results/life-frames-" + label + ".json";
            manager = World.DefaultGameObjectInjectionWorld.EntityManager;
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<LifeViewport>());
            board = query.GetSingletonEntity();
            initialGeneration = controller.State.Generation;
            var populationSample = system.Simulation.PopulationSample;
            initialPopulation = populationSample.Count;
            initialPopulationSampleGeneration = populationSample.Snapshot.Generation;
            initialPopulationSampleEpoch = populationSample.Snapshot.Epoch;
            historyEnabledAtStart = UnityEditorInternal.ProfilerDriver.enabled;
            var options = ProfilerRecorderOptions.Default | ProfilerRecorderOptions.CollectOnlyOnCurrentThread;
            string[] names = {
                "PlayerLoop", "Default World GameOfLife.LifeSimulationSystem",
                "LifeWorld:RegionPumpJob (Burst)", "LifeWorld:CollectionJob (Burst)",
                "LifeRasterJob (Burst)", "LifeOverlayRasterJob (Burst)", "Update.ScriptRunBehaviourUpdate",
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
            var populationSample = world.PopulationSample;
            var viewport = manager.GetComponentData<LifeViewport>(board);
            float speed = controller.Speed;
            int policyStepExponent = LifeSimulationPolicy.StepExponent(viewport.VisibleCells, speed);
            var allocation = recorders[11];
            frames[count++] = new Frame {
                frame = lastFrame, gapMs = (now - previous) * 1000d / Stopwatch.Frequency,
                generation = state.Generation, population = populationSample.Count,
                populationSampleGeneration = populationSample.Snapshot.Generation,
                populationSampleEpoch = populationSample.Snapshot.Epoch,
                revision = state.Revision, speed = speed, accumulator = state.Accumulator,
                viewportOriginX = viewport.Origin.x, viewportOriginY = viewport.Origin.y,
                visibleCells = viewport.VisibleCells, frameSeconds = viewport.FrameSeconds,
                policyStepExponent = policyStepExponent, policyStepGenerations = 1L << policyStepExponent,
                nodes = world.NodeCount, running = state.IsRunning,
                cacheEntries = world.CacheCount, collecting = world.IsCollecting,
                regionCacheEntries = world.RegionCacheCount, epochs = world.EpochCount,
                pendingWork = world.HasPendingWork, viewCurrent = world.IsViewCurrent,
                advancing = world.IsAdvancing, playerLoopMs = Milliseconds(0),
                simulationMs = Milliseconds(1), regionPumpMs = Milliseconds(2), collectionMs = Milliseconds(3),
                rasterMs = Milliseconds(4), overlayRasterMs = Milliseconds(5),
                behavioursMs = Milliseconds(6), lateBehavioursMs = Milliseconds(7),
                gcCollectMs = Milliseconds(8), uiRenderMs = Milliseconds(9), uiLayoutMs = Milliseconds(10),
                gcAllocationEvents = allocation.Count == 0 ? 0 : allocation.GetSample(allocation.Count - 1).Count
            };
            previous = now;
            if (watch.Elapsed.TotalSeconds >= seconds + WarmupSeconds || count == frames.Length)
                Finish(count > 1 ? "completed" : "noContinuousFrames");
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
                completedAtUtc = DateTime.UtcNow.ToString("O"), reason, markersValid, frameIntervalValid = count > 1,
                elapsedSeconds = watch.Elapsed.TotalSeconds, warmupSeconds = WarmupSeconds, speed = controller.Speed,
                population = system.Simulation.PopulationSample.Count,
                populationSampleGeneration = system.Simulation.PopulationSample.Snapshot.Generation,
                populationSampleEpoch = system.Simulation.PopulationSample.Snapshot.Epoch,
                sampleCount = count, frames = captured,
                initialGeneration, initialPopulation, historyEnabledAtStart,
                initialPopulationSampleGeneration, initialPopulationSampleEpoch,
                historyEnabledAtEnd = UnityEditorInternal.ProfilerDriver.enabled,
                markerSamples,
                scope = "目标 Editor 当前 Play 的被动主线程采样；population 仅为带来源 Epoch/Generation 的历史快照人口，不发起全图查询，也不表示当前代精确总人口。policyStepGenerations 由记录的视野与速度计算，是策略跨度，实际请求仍受信用限制；regionPumpMs 包含区域求值及 HashLife 内核。帧间隔包含 Editor 等待，Profiler 指标对应最近完成帧，可能与状态读数相差一帧。GC.Alloc 仅记录事件数，不表示字节；此工具不改变棋盘或速度。"
            };
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(result, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }
    }

    public static object Main(string label, int seconds) => new Observation(label, seconds).Start();
}
