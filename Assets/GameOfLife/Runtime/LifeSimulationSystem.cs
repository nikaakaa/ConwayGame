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
        const int CollectionBudget = 4096;
        const int FirstCollection = 262144;
        static readonly long FrameBudgetTicks = Stopwatch.Frequency / 500;

        public LifeWorld Simulation { get; private set; }
        public LifeShape CellShape { get; private set; }

        Entity board;
        readonly Dictionary<LifePattern, LifeShape> patternShapes = new Dictionary<LifePattern, LifeShape>();
        readonly List<LifeShape> retainedPatterns = new List<LifeShape>();
        NativeList<LifeShape> collectionRoots;
        int nextCollection = FirstCollection;
        long advanceAmount;
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
                bool collected;
                do
                    collected = Simulation.ContinueCollection(CollectionBudget);
                while (!collected && Stopwatch.GetTimestamp() < deadline);
                if (collected)
                    nextCollection = Math.Max(FirstCollection, Simulation.NodeCount * 2);
                EntityManager.SetComponentData(board, state);
                return;
            }

            if (Simulation.IsAdvancing)
            {
                if (explicitStep || commands.Length == 0)
                {
                    ContinueAdvance(ref state, deadline);
                    EntityManager.SetComponentData(board, state);
                    return;
                }
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
                        break;
                    case LifeCommandKind.Pause:
                        Pause(ref state);
                        break;
                    case LifeCommandKind.Step:
                        Pause(ref state);
                        explicitStep = true;
                        advanceAmount = 1;
                        Simulation.BeginAdvance(0);
                        ContinueAdvance(ref state, deadline);
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

            if (commands.Length == 0 && state.IsRunning && state.Accumulator >= 1)
            {
                int exponent = 0;
                while (exponent < 30 && (1L << (exponent + 1)) <= state.Accumulator)
                    exponent++;
                advanceAmount = 1L << exponent;
                explicitStep = false;
                Simulation.BeginAdvance(exponent);
                ContinueAdvance(ref state, deadline);
            }
            EntityManager.SetComponentData(board, state);
        }

        void ContinueAdvance(ref LifeState state, long deadline)
        {
            LifeShape before = Simulation.Shape;
            bool complete;
            do
                complete = Simulation.ContinueAdvance(AdvanceBudget);
            while (!complete && Stopwatch.GetTimestamp() < deadline);
            if (!complete)
                return;
            state.Generation += advanceAmount;
            if (Changed(before, Simulation.Shape))
                state.Revision++;
            if (!explicitStep)
                state.Accumulator -= advanceAmount;
            explicitStep = false;
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
            while (remaining > 0 && Stopwatch.GetTimestamp() < deadline && math.any(strokePosition != command.End))
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
