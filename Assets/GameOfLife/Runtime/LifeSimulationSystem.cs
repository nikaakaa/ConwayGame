using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace GameOfLife
{
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    public partial struct LifeSimulationSystem : ISystem
    {
        private Entity _board;
        private NativeParallelHashSet<int2> _aliveLookup;
        private NativeParallelHashSet<int2> _candidateSet;
        private NativeList<int2> _candidates;
        private NativeParallelHashSet<int2> _nextAlive;

        public void OnCreate(ref SystemState systemState)
        {
            _aliveLookup = new NativeParallelHashSet<int2>(64, Allocator.Persistent);
            _candidateSet = new NativeParallelHashSet<int2>(64, Allocator.Persistent);
            _candidates = new NativeList<int2>(64, Allocator.Persistent);
            _nextAlive = new NativeParallelHashSet<int2>(64, Allocator.Persistent);

            var entityManager = systemState.EntityManager;
            _board = entityManager.CreateEntity(
                typeof(LifeSettings), typeof(LifeState), typeof(AliveCell), typeof(LifeCommand));
            entityManager.SetName(_board, "Conway Board");
            entityManager.SetComponentData(_board, new LifeSettings { GenerationsPerSecond = 10f });

            var cells = entityManager.GetBuffer<AliveCell>(_board);
            var initialCells = LifePatterns.Items[0].Cells;
            cells.EnsureCapacity(initialCells.Length);
            for (var i = 0; i < initialCells.Length; i++)
                cells.Add(new AliveCell { Position = initialCells[i] });
            entityManager.SetComponentData(_board, new LifeState { Revision = 1 });
        }

        public void OnUpdate(ref SystemState systemState)
        {
            systemState.Dependency.Complete();

            var entityManager = systemState.EntityManager;
            var state = entityManager.GetComponentData<LifeState>(_board);
            var settings = entityManager.GetComponentData<LifeSettings>(_board);
            var cells = entityManager.GetBuffer<AliveCell>(_board);
            var commands = entityManager.GetBuffer<LifeCommand>(_board);

            for (var i = 0; i < commands.Length; i++)
            {
                var command = commands[i];
                switch (command.Kind)
                {
                    case LifeCommandKind.Play:
                        state.IsRunning = true;
                        break;
                    case LifeCommandKind.Pause:
                        Pause(ref state);
                        break;
                    case LifeCommandKind.Step:
                        Pause(ref state);
                        Advance(ref systemState, ref state, cells);
                        break;
                    case LifeCommandKind.SetSpeed:
                        settings.GenerationsPerSecond = command.Speed;
                        state.Accumulator = 0f;
                        break;
                    case LifeCommandKind.Clear:
                        Pause(ref state);
                        cells.Clear();
                        state.Generation = 0;
                        state.Revision++;
                        break;
                    case LifeCommandKind.SetCell:
                        Pause(ref state);
                        SetCell(command.Position, command.Alive, ref state, cells);
                        break;
                }
            }

            commands.Clear();

            if (state.IsRunning)
            {
                state.Accumulator += SystemAPI.Time.DeltaTime;
                var interval = 1f / settings.GenerationsPerSecond;
                while (state.Accumulator >= interval)
                {
                    Advance(ref systemState, ref state, cells);
                    state.Accumulator -= interval;
                }
            }

            entityManager.SetComponentData(_board, settings);
            entityManager.SetComponentData(_board, state);
        }

        public void OnDestroy(ref SystemState systemState)
        {
            systemState.Dependency.Complete();
            _aliveLookup.Dispose();
            _candidateSet.Dispose();
            _candidates.Dispose();
            _nextAlive.Dispose();
        }

        private static void Pause(ref LifeState state)
        {
            state.IsRunning = false;
            state.Accumulator = 0f;
        }

        private static void SetCell(int2 position, bool alive, ref LifeState state, DynamicBuffer<AliveCell> cells)
        {
            var existingIndex = -1;
            for (var i = 0; i < cells.Length; i++)
            {
                if (math.all(cells[i].Position == position))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (alive && existingIndex == -1)
            {
                cells.Add(new AliveCell { Position = position });
                state.Revision++;
            }
            else if (!alive && existingIndex != -1)
            {
                cells.RemoveAtSwapBack(existingIndex);
                state.Revision++;
            }
        }

        private void Advance(ref SystemState systemState, ref LifeState state, DynamicBuffer<AliveCell> cells)
        {
            _aliveLookup.Clear();
            _candidateSet.Clear();
            _candidates.Clear();
            _nextAlive.Clear();

            var aliveCount = cells.Length;
            var candidateCapacity = aliveCount * 9;
            if (_aliveLookup.Capacity < aliveCount)
                _aliveLookup.Capacity = aliveCount;
            if (_candidateSet.Capacity < candidateCapacity)
                _candidateSet.Capacity = candidateCapacity;
            if (_candidates.Capacity < candidateCapacity)
                _candidates.Capacity = candidateCapacity;

            new CollectCandidatesJob
            {
                Cells = cells.AsNativeArray(),
                AliveLookup = _aliveLookup,
                CandidateSet = _candidateSet,
                Candidates = _candidates
            }.Run();

            if (_nextAlive.Capacity < _candidates.Length)
                _nextAlive.Capacity = _candidates.Length;

            systemState.Dependency = new EvolveJob
            {
                Candidates = _candidates.AsArray(),
                AliveLookup = _aliveLookup,
                NextAlive = _nextAlive.AsParallelWriter()
            }.Schedule(_candidates.Length, 64, systemState.Dependency);
            systemState.Dependency.Complete();

            cells.Clear();
            cells.EnsureCapacity(_nextAlive.Count());
            foreach (var position in _nextAlive)
                cells.Add(new AliveCell { Position = position });

            state.Generation++;
            state.Revision++;
        }

        [BurstCompile]
        private struct CollectCandidatesJob : IJob
        {
            [ReadOnly] public NativeArray<AliveCell> Cells;
            public NativeParallelHashSet<int2> AliveLookup;
            public NativeParallelHashSet<int2> CandidateSet;
            public NativeList<int2> Candidates;

            public void Execute()
            {
                for (var i = 0; i < Cells.Length; i++)
                {
                    var position = Cells[i].Position;
                    AliveLookup.Add(position);
                    for (var y = -1; y <= 1; y++)
                    {
                        for (var x = -1; x <= 1; x++)
                        {
                            var candidate = position + new int2(x, y);
                            if (CandidateSet.Add(candidate))
                                Candidates.AddNoResize(candidate);
                        }
                    }
                }
            }
        }

        [BurstCompile]
        private struct EvolveJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<int2> Candidates;
            [ReadOnly] public NativeParallelHashSet<int2> AliveLookup;
            public NativeParallelHashSet<int2>.ParallelWriter NextAlive;

            public void Execute(int index)
            {
                var position = Candidates[index];
                var neighbours = 0;
                for (var y = -1; y <= 1; y++)
                {
                    for (var x = -1; x <= 1; x++)
                    {
                        if ((x != 0 || y != 0) && AliveLookup.Contains(position + new int2(x, y)))
                            neighbours++;
                    }
                }

                if (neighbours == 3 || (neighbours == 2 && AliveLookup.Contains(position)))
                    NextAlive.Add(position);
            }
        }
    }
}
