using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace GameOfLife
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial class LifeSimulationSystem : SystemBase
    {
        const int AdvanceBudget = 128;
        const int EditBudget = 2048;
        const int CollectionBudget = 1024;
        const int FirstCollection = 262144;

        public LifeWorld Simulation { get; private set; }
        public LifeShape CellShape { get; private set; }
        public LifeSession Session { get; private set; }
        public Entity Board => board;

        Entity board;
        readonly Dictionary<LifePattern, LifeShape> patternShapes = new Dictionary<LifePattern, LifeShape>();
        readonly List<LifeShape> retainedPatterns = new List<LifeShape>();
        NativeList<LifeShape> collectionRoots;
        int nextCollection = FirstCollection;
        LifeSimulationPolicy policy = new LifeSimulationPolicy(0.001);
        bool explicitStep;
        bool strokeStarted;
        int2 strokePosition;
        int strokeError;

        protected override void OnCreate()
        {
            Simulation = new LifeWorld();
            collectionRoots = new NativeList<LifeShape>(64, Allocator.Persistent);
            LifeBits bit = default;
            bit.Set(0, 0, true);
            CellShape = Simulation.BuildShape(new[] { new LifeTile { Position = int2.zero, Bits = bit } });
            board = EntityManager.CreateEntity(typeof(LifeSettings), typeof(LifeState), typeof(LifeViewport), typeof(LifeCommand));
            EntityManager.SetName(board, "Conway Board");
            EntityManager.SetComponentData(board, new LifeSettings { GenerationsPerSecond = 10f });
            EntityManager.SetComponentData(board, new LifeState { Revision = 1 });
            EntityManager.SetComponentData(board, new LifeViewport
            {
                Origin = new int2(-64, -64), VisibleCells = 128, FrameSeconds = 1.0 / 60
            });
            Session = new LifeSession(this, EntityManager, board);
        }

        public void LoadPatterns(IReadOnlyList<LifePattern> patterns)
        {
            while (Simulation.IsCollecting)
                Simulation.ContinueCollection(CollectionBudget);
            patternShapes.Clear();
            retainedPatterns.Clear();
            retainedPatterns.Add(CellShape);
            for (int i = 0; i < patterns.Count; i++)
            {
                LifePattern pattern = patterns[i];
                LifeShape shape = Simulation.BuildShape(pattern.Tiles);
                patternShapes.Add(pattern, shape);
                retainedPatterns.Add(shape);
            }
        }

        public LifeShape PatternShape(LifePattern pattern) => patternShapes[pattern];

        protected override void OnUpdate()
        {
            Dependency.Complete();
            LifeState state = EntityManager.GetComponentData<LifeState>(board);
            LifeSettings settings = EntityManager.GetComponentData<LifeSettings>(board);
            LifeViewport viewport = EntityManager.GetComponentData<LifeViewport>(board);
            DynamicBuffer<LifeCommand> commands = EntityManager.GetBuffer<LifeCommand>(board);
            long frameBudget = (long)(policy.BeginFrame(viewport.FrameSeconds) * Stopwatch.Frequency);
            long deadline = Stopwatch.GetTimestamp() + frameBudget;
            Simulation.SetViewport(viewport.PreloadOrigin, viewport.PreloadCells);
            int stepExponent = LifeSimulationPolicy.StepExponent(viewport.VisibleCells, settings.GenerationsPerSecond);
            long step = 1L << stepExponent;
            if (state.IsRunning)
                state.Accumulator = Math.Min(state.Accumulator + World.Time.DeltaTime * settings.GenerationsPerSecond,
                    LifeSimulationPolicy.AccumulatorCapacity(step, settings.GenerationsPerSecond));

            if (!Simulation.IsCollecting && Simulation.NodeCount + Simulation.RegionCacheCount >= nextCollection)
            {
                collectionRoots.Clear();
                for (int i = 0; i < retainedPatterns.Count; i++)
                    collectionRoots.Add(retainedPatterns[i]);
                for (int i = 0; i < commands.Length; i++)
                    if (commands[i].Kind == LifeCommandKind.PatternStroke)
                        collectionRoots.Add(commands[i].Shape);
                Simulation.BeginCollection(collectionRoots.AsArray());
            }

            if (Simulation.IsCollecting)
            {
                long collectionDeadline = state.IsRunning || Simulation.HasPendingWork || commands.Length != 0
                    ? Stopwatch.GetTimestamp() + frameBudget / 4
                    : deadline;
                bool collected;
                do
                    collected = Simulation.ContinueCollection(CollectionBudget);
                while (!collected && Stopwatch.GetTimestamp() < collectionDeadline);
                if (collected)
                    nextCollection = Math.Max(FirstCollection, (Simulation.NodeCount + Simulation.RegionCacheCount) * 2);
            }

            if (Simulation.IsAdvancing)
            {
                if (explicitStep || commands.Length == 0)
                {
                    bool wasExplicitStep = explicitStep;
                    PumpView(ref state, deadline);
                    if (wasExplicitStep || Simulation.IsAdvancing)
                    {
                        EntityManager.SetComponentData(board, state);
                        return;
                    }
                }
                else
                    Simulation.CancelAdvance();
            }

            int consumed = 0;
            int editsRemaining = EditBudget;
            bool editing = false;
            while (consumed < commands.Length)
            {
                LifeCommand command = commands[consumed];
                if (command.Kind == LifeCommandKind.CellStroke || command.Kind == LifeCommandKind.PatternStroke)
                {
                    Pause(ref state);
                    if (!editing)
                    {
                        Simulation.BeginEdit();
                        editing = true;
                    }
                    if (!DrawStroke(command, ref editsRemaining, deadline))
                        break;
                    consumed++;
                    if (editsRemaining == 0 || Stopwatch.GetTimestamp() >= deadline)
                        break;
                    continue;
                }

                if (editing)
                {
                    Simulation.EndEdit();
                    editing = false;
                }
                consumed++;
                switch (command.Kind)
                {
                    case LifeCommandKind.Play:
                        state.IsRunning = true;
                        state.Accumulator = 0;
                        break;
                    case LifeCommandKind.Pause:
                        Pause(ref state);
                        break;
                    case LifeCommandKind.Step:
                        Pause(ref state);
                        explicitStep = true;
                        Simulation.BeginAdvance(0);
                        PumpView(ref state, deadline);
                        commands.RemoveRange(0, consumed);
                        EntityManager.SetComponentData(board, state);
                        return;
                    case LifeCommandKind.SetSpeed:
                        settings.GenerationsPerSecond = command.Speed;
                        state.Accumulator = 0;
                        EntityManager.SetComponentData(board, settings);
                        break;
                    case LifeCommandKind.Clear:
                        Pause(ref state);
                        Simulation.Clear();
                        state.Generation = Simulation.Snapshot.Generation;
                        break;
                    case LifeCommandKind.ClearArea:
                        Pause(ref state);
                        Simulation.ClearArea(command.Position, command.End);
                        break;
                }
            }
            if (editing)
                Simulation.EndEdit();
            if (consumed > 0)
                commands.RemoveRange(0, consumed);

            PumpView(ref state, deadline);
            stepExponent = LifeSimulationPolicy.StepExponent(viewport.VisibleCells, settings.GenerationsPerSecond);
            step = 1L << stepExponent;
            while (commands.Length == 0 && state.IsRunning && Simulation.IsViewCurrent && state.Accumulator >= step
                && Stopwatch.GetTimestamp() < deadline)
            {
                Simulation.BeginAdvance(stepExponent);
                PumpView(ref state, deadline);
                if (Simulation.IsAdvancing)
                    break;
            }
            while (Simulation.HasPendingWork && Stopwatch.GetTimestamp() < deadline)
            {
                Simulation.Pump(AdvanceBudget);
            }
            PublishState(ref state);
            EntityManager.SetComponentData(board, state);
        }

        void PumpView(ref LifeState state, long deadline)
        {
            while ((Simulation.IsAdvancing || !Simulation.IsViewCurrent) && Stopwatch.GetTimestamp() < deadline)
            {
                Simulation.Pump(AdvanceBudget);
            }
            PublishState(ref state);
        }

        void PublishState(ref LifeState state)
        {
            long generation = Simulation.Snapshot.Generation;
            if (!explicitStep)
                state.Accumulator -= generation - state.Generation;
            state.Generation = generation;
            state.Revision = Simulation.ViewFrame.Version;
            if (!Simulation.IsAdvancing)
                explicitStep = false;
        }

        static void Pause(ref LifeState state)
        {
            state.IsRunning = false;
            state.Accumulator = 0;
        }

        bool DrawStroke(LifeCommand command, ref int remaining, long deadline)
        {
            int initialRemaining = remaining;
            int2 distance = math.abs(command.End - command.Position);
            int2 direction = new int2(command.Position.x < command.End.x ? 1 : -1,
                command.Position.y < command.End.y ? 1 : -1);
            if (!strokeStarted)
            {
                strokeStarted = true;
                strokePosition = command.Position;
                strokeError = distance.x - distance.y;
                if (!command.SkipFirst)
                {
                    Paint(command, strokePosition);
                    remaining--;
                }
            }
            while (remaining > 0 && math.any(strokePosition != command.End)
                && (remaining == initialRemaining || Stopwatch.GetTimestamp() < deadline))
            {
                int doubled = strokeError * 2;
                if (doubled > -distance.y)
                {
                    strokeError -= distance.y;
                    strokePosition.x += direction.x;
                }
                if (doubled < distance.x)
                {
                    strokeError += distance.x;
                    strokePosition.y += direction.y;
                }
                Paint(command, strokePosition);
                remaining--;
            }
            bool complete = math.all(strokePosition == command.End);
            if (complete)
                strokeStarted = false;
            return complete;
        }

        void Paint(LifeCommand command, int2 position)
        {
            if (command.Kind == LifeCommandKind.PatternStroke)
                Simulation.Stamp(command.Shape, position);
            else
                Simulation.SetCell(position, command.Alive);
        }

        protected override void OnDestroy()
        {
            Session.Worlds.ReleasePreviews();
            collectionRoots.Dispose();
            Simulation.Dispose();
        }
    }
}
