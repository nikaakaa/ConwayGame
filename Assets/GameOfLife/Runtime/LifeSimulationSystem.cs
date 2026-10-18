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
        const int InitialAutomaticExponent = 5;
        static readonly long FrameBudgetTicks = Stopwatch.Frequency / 125;
        static readonly long AdvanceTargetTicks = Stopwatch.Frequency / 30;

        public LifeWorld Simulation { get; private set; }
        public LifeShape CellShape { get; private set; }

        Entity board;
        readonly Dictionary<LifePattern, LifeShape> patternShapes = new Dictionary<LifePattern, LifeShape>();
        readonly List<LifeShape> retainedPatterns = new List<LifeShape>();
        NativeList<LifeShape> collectionRoots;
        int nextCollection = FirstCollection;
        long advanceAmount;
        long advanceWorkTicks;
        int automaticExponent = InitialAutomaticExponent;
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
            LoadPatterns(LifePatterns.Items);
            Simulation.BeginEdit();
            Simulation.Stamp(PatternShape(LifePatterns.Items[0]), int2.zero);
            Simulation.EndEdit();

            board = EntityManager.CreateEntity(typeof(LifeSettings), typeof(LifeState), typeof(LifeCommand));
            EntityManager.SetName(board, "Conway Board");
            EntityManager.SetComponentData(board, new LifeSettings { GenerationsPerSecond = 10f });
            EntityManager.SetComponentData(board, new LifeState { Revision = 1 });
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
            long deadline = Stopwatch.GetTimestamp() + FrameBudgetTicks;
            LifeState state = EntityManager.GetComponentData<LifeState>(board);
            LifeSettings settings = EntityManager.GetComponentData<LifeSettings>(board);
            DynamicBuffer<LifeCommand> commands = EntityManager.GetBuffer<LifeCommand>(board);
            if (state.IsRunning)
                state.Accumulator = Math.Min(state.Accumulator + World.Time.DeltaTime * settings.GenerationsPerSecond,
                    settings.GenerationsPerSecond);

            if (!Simulation.IsCollecting && Simulation.NodeCount >= nextCollection)
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
                long collectionDeadline = state.IsRunning || Simulation.IsAdvancing || commands.Length != 0
                    ? Stopwatch.GetTimestamp() + FrameBudgetTicks / 4
                    : deadline;
                bool collected;
                do
                    collected = Simulation.ContinueCollection(CollectionBudget);
                while (!collected && Stopwatch.GetTimestamp() < collectionDeadline);
                if (collected)
                    nextCollection = Math.Max(FirstCollection, Simulation.NodeCount * 2);
            }

            if (Simulation.IsAdvancing)
            {
                if (explicitStep || commands.Length == 0)
                {
                    bool wasExplicitStep = explicitStep;
                    ContinueAdvance(ref state, deadline);
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
            LifeShape beforeEdit = default;
            while (consumed < commands.Length)
            {
                LifeCommand command = commands[consumed];
                if (command.Kind == LifeCommandKind.CellStroke || command.Kind == LifeCommandKind.PatternStroke)
                {
                    Pause(ref state);
                    if (!editing)
                    {
                        beforeEdit = Simulation.Shape;
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
                    FinishEdits(beforeEdit, ref state);
                    editing = false;
                }
                consumed++;
                switch (command.Kind)
                {
                    case LifeCommandKind.Play:
                        state.IsRunning = true;
                        state.Accumulator = 0;
                        automaticExponent = InitialAutomaticExponent;
                        break;
                    case LifeCommandKind.Pause:
                        Pause(ref state);
                        break;
                    case LifeCommandKind.Step:
                        Pause(ref state);
                        explicitStep = true;
                        advanceAmount = 1;
                        advanceWorkTicks = 0;
                        Simulation.BeginAdvance(0);
                        ContinueAdvance(ref state, deadline);
                        commands.RemoveRange(0, consumed);
                        EntityManager.SetComponentData(board, state);
                        return;
                    case LifeCommandKind.SetSpeed:
                        settings.GenerationsPerSecond = command.Speed;
                        state.Accumulator = 0;
                        automaticExponent = InitialAutomaticExponent;
                        EntityManager.SetComponentData(board, settings);
                        break;
                    case LifeCommandKind.Clear:
                        Pause(ref state);
                        beforeEdit = Simulation.Shape;
                        Simulation.Clear();
                        state.Generation = 0;
                        if (Changed(beforeEdit, Simulation.Shape))
                            state.Revision++;
                        break;
                    case LifeCommandKind.ClearArea:
                        Pause(ref state);
                        beforeEdit = Simulation.Shape;
                        Simulation.ClearArea(command.Position, command.End);
                        if (Changed(beforeEdit, Simulation.Shape))
                            state.Revision++;
                        break;
                }
            }
            if (editing)
                FinishEdits(beforeEdit, ref state);
            if (consumed > 0)
                commands.RemoveRange(0, consumed);

            while (commands.Length == 0 && state.IsRunning && state.Accumulator >= 1
                && Stopwatch.GetTimestamp() < deadline)
            {
                int exponent = 0;
                while (exponent < automaticExponent && (1L << (exponent + 1)) <= state.Accumulator)
                    exponent++;
                advanceAmount = 1L << exponent;
                advanceWorkTicks = 0;
                Simulation.BeginAdvance(exponent);
                ContinueAdvance(ref state, deadline);
                if (Simulation.IsAdvancing)
                    break;
            }
            EntityManager.SetComponentData(board, state);
        }

        void ContinueAdvance(ref LifeState state, long deadline)
        {
            LifeShape before = Simulation.Shape;
            long start = Stopwatch.GetTimestamp();
            bool complete;
            do
                complete = Simulation.ContinueAdvance(AdvanceBudget);
            while (!complete && Stopwatch.GetTimestamp() < deadline);
            advanceWorkTicks += Stopwatch.GetTimestamp() - start;
            if (!complete)
                return;
            state.Generation += advanceAmount;
            if (Changed(before, Simulation.Shape))
                state.Revision++;
            if (!explicitStep)
            {
                state.Accumulator -= advanceAmount;
                AdjustAutomaticExponent();
            }
            explicitStep = false;
        }

        void AdjustAutomaticExponent()
        {
            int exponent = math.tzcnt((ulong)advanceAmount);
            if (advanceWorkTicks > AdvanceTargetTicks * 2)
            {
                long estimatedTicks = advanceWorkTicks;
                while (exponent > 0 && estimatedTicks > AdvanceTargetTicks)
                {
                    exponent--;
                    estimatedTicks >>= 1;
                }
                automaticExponent = exponent;
            }
            else if (exponent == automaticExponent && advanceWorkTicks < AdvanceTargetTicks)
                automaticExponent = math.min(30, automaticExponent + 1);
        }

        static void Pause(ref LifeState state)
        {
            state.IsRunning = false;
            state.Accumulator = 0;
        }

        void FinishEdits(LifeShape before, ref LifeState state)
        {
            Simulation.EndEdit();
            if (Changed(before, Simulation.Shape))
                state.Revision++;
        }

        static bool Changed(LifeShape before, LifeShape after) =>
            before.Root != after.Root || before.X != after.X || before.Y != after.Y;

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
            collectionRoots.Dispose();
            Simulation.Dispose();
        }
    }
}
