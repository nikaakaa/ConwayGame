using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameOfLife;
using Unity.Entities;
using Unity.Profiling;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;

public static class MeasureLifeInteraction
{
    struct Sample
    {
        public int run;
        public string phase;
        public double gapMs;
        public double inputMs;
        public double rasterMs;
        public double overlayMs;
        public double boardUploadMs;
        public double overlayUploadMs;
        public double simulationMs;
        public double lateMs;
        public long generation;
        public bool running;
        public bool cameraCovered;
        public bool postInputCameraCovered;
        public int stepExponent;
        public int desiredX, desiredY, desiredSize;
        public int textureX, textureY, textureSize;
        public bool hitMatchesCamera;
        public bool viewCurrent;
        public uint version;
    }

    sealed class Replay
    {
        readonly LifeController controller = UnityEngine.Object.FindFirstObjectByType<LifeController>();
        readonly LifeSimulationSystem system = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<LifeSimulationSystem>();
        readonly Sample[] samples = new Sample[540];
        readonly bool oldBackground = Application.runInBackground;
        readonly bool oldHistory = UnityEditorInternal.ProfilerDriver.enabled;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly string label;
        readonly bool capture;
        readonly bool advanceDuringReplay;
        readonly bool overlay;
        readonly bool wasRunning;
        readonly bool oldShortcuts;
        readonly int originX, originY, size;
        readonly long sourceGeneration;
        readonly float speed;
        readonly PointerEventData pointer;
        readonly ProfilerRecorder[] recorders;
        Task<string> saving;
        LifeController.SaveTicket ticket;
        bool prepared;
        int warmup, count, lastFrame = -1;
        long previous;
        string savePath;
        int expectedX, expectedY, expectedSize, externalCameraChanges;

        public Replay(string label, bool capture, bool advanceDuringReplay)
        {
            this.label = label;
            this.capture = capture;
            this.advanceDuringReplay = advanceDuringReplay;
            overlay = typeof(LifeView).GetField("overlayTexture", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) != null;
            wasRunning = controller.State.IsRunning;
            oldShortcuts = controller.KeyboardShortcutsEnabled;
            originX = controller.View.Origin.x; originY = controller.View.Origin.y; size = controller.View.VisibleCells;
            expectedX = originX; expectedY = originY; expectedSize = size;
            sourceGeneration = system.Simulation.Snapshot.Generation;
            speed = controller.Speed;
            pointer = new PointerEventData(EventSystem.current) { button = PointerEventData.InputButton.Middle };
            pointer.pointerCurrentRaycast = new RaycastResult { gameObject = controller.View.BoardImage.gameObject };
            var handles = new System.Collections.Generic.List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            var descriptions = handles.Select(ProfilerRecorderHandle.GetDescription).ToArray();
            string[] names = { "LifeView.BoardRaster", "Default World GameOfLife.LifeSimulationSystem",
                "PreLateUpdate.ScriptRunBehaviourLateUpdate", "LifeView.OverlayRaster", "LifeView.BoardUpload", "LifeView.OverlayUpload" };
            recorders = names.Select(name => descriptions.First(item => item.Name == name))
                .Select(item => ProfilerRecorder.StartNew(item.Category, item.Name, 1,
                    ProfilerRecorderOptions.Default | ProfilerRecorderOptions.CollectOnlyOnCurrentThread)).ToArray();
        }

        public object Start()
        {
            Application.runInBackground = true;
            UnityEditorInternal.ProfilerDriver.enabled = false;
            controller.KeyboardShortcutsEnabled = false;
            if (wasRunning) controller.ToggleRunning();
            EditorApplication.update += Tick;
            AssemblyReloadEvents.beforeAssemblyReload += Reload;
            return new { label, capture, compiledOverlay = overlay, sourceGeneration, originX, originY, size };
        }

        Vector2 Position(float x, float y)
        {
            RectTransform rect = controller.View.BoardImage.rectTransform;
            return RectTransformUtility.WorldToScreenPoint(null,
                rect.TransformPoint(new Vector3(rect.rect.xMin + rect.rect.width * x, rect.rect.yMin + rect.rect.height * y)));
        }

        bool CameraCovered()
        {
            LifeView view = controller.View;
            LifeRegion bounds = view.TextureBounds;
            return bounds.MinX <= view.Origin.x && bounds.MinY <= view.Origin.y
                && bounds.MaxX >= (long)view.Origin.x + view.VisibleCells
                && bounds.MaxY >= (long)view.Origin.y + view.VisibleCells;
        }

        void Tick()
        {
            if (clock.Elapsed.TotalSeconds > 180) { Finish("timeout"); return; }
            if (Time.frameCount == lastFrame) return;
            lastFrame = Time.frameCount;
            if (!prepared)
            {
                if (controller.State.IsRunning || system.Simulation.IsAdvancing || !system.Simulation.IsViewCurrent) return;
                if (capture)
                {
                    if (saving == null) saving = controller.SaveWorldAsync(out ticket);
                    if (!saving.IsCompleted)
                    {
                        long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 250;
                        do system.Simulation.Pump(128);
                        while (!saving.IsCompleted && Stopwatch.GetTimestamp() < deadline);
                        return;
                    }
                    savePath = saving.GetAwaiter().GetResult();
                    controller.CompleteSave(ticket, savePath);
                    File.WriteAllText("Tools/Benchmarks/Work/interaction-restore.json",
                        Newtonsoft.Json.JsonConvert.SerializeObject(new {
                            savePath, originX, originY, size, speed, wasRunning, sourceGeneration, oldBackground, oldHistory
                        }, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
                }
                prepared = true;
            }
            var view = controller.View;
            if (warmup++ < 24)
            {
                expectedX = view.Origin.x; expectedY = view.Origin.y; expectedSize = view.VisibleCells;
                previous = Stopwatch.GetTimestamp();
                return;
            }
            if (view.Origin.x != expectedX || view.Origin.y != expectedY || view.VisibleCells != expectedSize)
                externalCameraChanges++;
            if (count == 0 && advanceDuringReplay) controller.ToggleRunning();
            int run = count / 108;
            int frame = count % 108;
            string phase = frame < 12 ? "idle" : frame < 36 ? "hover" : frame < 84 ? "drag" : "zoom";
            long now = Stopwatch.GetTimestamp();
            samples[count] = new Sample {
                run = run, phase = phase, gapMs = (now - previous) * 1000d / Stopwatch.Frequency,
                rasterMs = recorders[0].LastValue / 1000000d, simulationMs = recorders[1].LastValue / 1000000d,
                lateMs = recorders[2].LastValue / 1000000d, overlayMs = recorders[3].LastValue / 1000000d,
                generation = system.Simulation.Snapshot.Generation, running = controller.State.IsRunning,
                cameraCovered = CameraCovered(), stepExponent = LifeSimulationPolicy.StepExponent(view.VisibleCells, controller.Speed),
                boardUploadMs = recorders[4].LastValue / 1000000d, overlayUploadMs = recorders[5].LastValue / 1000000d,
                desiredX = view.Origin.x, desiredY = view.Origin.y, desiredSize = view.VisibleCells,
                textureX = (int)view.TextureBounds.MinX, textureY = (int)view.TextureBounds.MinY,
                textureSize = (int)(view.TextureBounds.MaxX - view.TextureBounds.MinX),
                viewCurrent = system.Simulation.IsViewCurrent, version = system.Simulation.ViewFrame.Version
            };
            long inputStart = Stopwatch.GetTimestamp();
            if (phase == "hover")
            {
                pointer.position = Position(0.3f + (frame - 12) / 60f, 0.5f);
                controller.OnPointerMove(pointer);
            }
            else if (phase == "drag")
            {
                int step = frame - 36;
                pointer.position = Position(0.5f, 0.5f);
                if (step == 0) controller.OnPointerDown(pointer);
                float distance = step < 24 ? step / 24f : (47 - step) / 23f;
                pointer.position = Position(0.5f + distance * 0.15f, 0.5f + distance * 0.07f);
                controller.OnDrag(pointer);
                if (step == 47) controller.OnPointerUp(pointer);
            }
            else if (phase == "zoom" && (frame - 84) % 6 == 0)
            {
                pointer.position = Position(0.5f, 0.5f);
                pointer.scrollDelta = new Vector2(0, (frame - 84) % 12 == 0 ? 1 : -1);
                controller.OnScroll(pointer);
            }
            samples[count].inputMs = (Stopwatch.GetTimestamp() - inputStart) * 1000d / Stopwatch.Frequency;
            view.TryGetCell(Position(0.5f, 0.5f), null, out var centerCell);
            samples[count].hitMatchesCamera = Unity.Mathematics.math.all(centerCell == view.Origin + view.VisibleCells / 2);
            samples[count].postInputCameraCovered = CameraCovered();
            expectedX = view.Origin.x; expectedY = view.Origin.y; expectedSize = view.VisibleCells;
            previous = now;
            if (++count == samples.Length) Finish("completed");
        }

        void Reload() => Finish("domainReload");

        void Finish(string reason)
        {
            EditorApplication.update -= Tick;
            AssemblyReloadEvents.beforeAssemblyReload -= Reload;
            var output = new {
                completedAtUtc = DateTime.UtcNow.ToString("O"), reason, compiledOverlay = overlay,
                sourceGeneration, originX, originY, size, speed, wasRunning, savePath,
                advanceDuringReplay,
                boardTextureSize = LifeView.BoardTextureSize,
                externalCameraChanges, uncontaminatedCameraReplay = externalCameraChanges == 0,
                count, elapsedSeconds = clock.Elapsed.TotalSeconds, samples = samples.Take(count).ToArray(),
                historyEnabledDuringReplay = UnityEditorInternal.ProfilerDriver.enabled,
                scope = "目标Game中正式OnPointerMove/OnDrag/OnScroll，五轮每轮12帧空闲、24帧预览、48帧拖动、24帧缩放。advanceDuringReplay决定预热后是否播放，帧数据与输入可能相差一帧。运行时临时开启后台运行、关闭Profiler历史录制，未清除已有历史；结束恢复。保存与预热不计入分段。重编译恢复后世界时间基底归零，视野求值阶段不能据此计算受控加速比；外部相机变化使固定输入回放无效。不测GC.Alloc或GPU完成。"
            };
            foreach (var recorder in recorders) recorder.Dispose();
            pointer.button = PointerEventData.InputButton.Middle;
            controller.OnPointerUp(pointer);
            controller.KeyboardShortcutsEnabled = oldShortcuts;
            if (wasRunning != controller.State.IsRunning) controller.ToggleRunning();
            Application.runInBackground = oldBackground;
            UnityEditorInternal.ProfilerDriver.enabled = oldHistory;
            File.WriteAllText("Tools/Benchmarks/Results/interaction-" + label + ".json",
                Newtonsoft.Json.JsonConvert.SerializeObject(output, Newtonsoft.Json.Formatting.Indented), new UTF8Encoding(false));
        }
    }

    public static object Main(string label, bool capture, bool advanceDuringReplay) => new Replay(label, capture, advanceDuringReplay).Start();
}
