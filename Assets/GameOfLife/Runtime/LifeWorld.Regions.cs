using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GameOfLife
{
    public readonly struct LifeRegion : IEquatable<LifeRegion>
    {
        public readonly long MinX;
        public readonly long MinY;
        public readonly long MaxX;
        public readonly long MaxY;

        public LifeRegion(long minX, long minY, long maxX, long maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        public bool IsEmpty => MinX == MaxX || MinY == MaxY;
        public bool Equals(LifeRegion other) => MinX == other.MinX && MinY == other.MinY && MaxX == other.MaxX && MaxY == other.MaxY;
        public override bool Equals(object obj) => obj is LifeRegion other && Equals(other);
        public override int GetHashCode() => (int)math.hash(new uint4((uint)MinX ^ (uint)(MinX >> 32),
            (uint)MinY ^ (uint)(MinY >> 32), (uint)MaxX ^ (uint)(MaxX >> 32), (uint)MaxY ^ (uint)(MaxY >> 32)));
    }

    public readonly struct LifeSnapshot : IEquatable<LifeSnapshot>
    {
        public readonly int Epoch;
        public readonly long Generation;

        public LifeSnapshot(int epoch, long generation)
        {
            Epoch = epoch;
            Generation = generation;
        }

        public bool Equals(LifeSnapshot other) => Epoch == other.Epoch && Generation == other.Generation;
        public override bool Equals(object obj) => obj is LifeSnapshot other && Equals(other);
        public override int GetHashCode() => (int)math.hash(new uint3((uint)Epoch, (uint)Generation, (uint)(Generation >> 32)));
    }

    public readonly struct LifeViewFrame
    {
        public readonly LifeSnapshot Snapshot;
        public readonly LifeRegion ValidBounds;
        public readonly LifeShape Shape;
        public readonly uint Version;

        public LifeViewFrame(LifeSnapshot snapshot, LifeRegion validBounds, LifeShape shape, uint version)
        {
            Snapshot = snapshot;
            ValidBounds = validBounds;
            Shape = shape;
            Version = version;
        }
    }

    public readonly struct LifePopulationSample
    {
        public readonly LifeSnapshot Snapshot;
        public readonly long Count;

        public LifePopulationSample(LifeSnapshot snapshot, long count)
        {
            Snapshot = snapshot;
            Count = count;
        }
    }

    public sealed partial class LifeWorld
    {
        NativeArray<TimelineState> timeline;
        readonly Queue<ExportOperation> exports = new Queue<ExportOperation>();
        ExportOperation currentExport;

        public LifeSnapshot Snapshot => timeline[0].Published;
        public LifeViewFrame ViewFrame => timeline[0].Frame;
        public LifePopulationSample PopulationSample => timeline[0].Population;
        public int RegionCacheCount => timeline[0].RegionCount;
        public int EpochCount => timeline[0].EpochCount;
        public bool IsAdvancing => core.Requests[0].Active != 0 && core.Requests[0].Target.Generation > Snapshot.Generation;
        public bool HasPendingWork => core.Requests[0].Active != 0 || core.Requests[1].Active != 0;
        public bool IsViewCurrent => ViewFrame.Snapshot.Equals(Snapshot) && ViewFrame.ValidBounds.Equals(timeline[0].Viewport);

        void InitializeRegions()
        {
            timeline = new NativeArray<TimelineState>(1, Allocator.Persistent);
            core.Epochs = new NativeList<WorldEpoch>(16, Allocator.Persistent);
            core.EpochSlots = new NativeParallelHashMap<int, int>(16, Allocator.Persistent);
            core.FreeEpochs = new NativeList<int>(16, Allocator.Persistent);
            core.EpochMarkStack = new NativeList<int>(16, Allocator.Persistent);
            core.Regions = new NativeParallelHashMap<RegionKey, int>(256, Allocator.Persistent);
            core.RegionKeys = new NativeList<RegionKey>(256, Allocator.Persistent);
            core.NewRegionKeys = new NativeList<RegionKey>(64, Allocator.Persistent);
            core.Frontiers = new NativeParallelHashMap<SpatialKey, RegionFrontier>(256, Allocator.Persistent);
            core.FrontierKeys = new NativeList<SpatialKey>(256, Allocator.Persistent);
            core.NewFrontierKeys = new NativeList<SpatialKey>(64, Allocator.Persistent);
            core.ViewWork = new NativeList<RegionWork>(128, Allocator.Persistent);
            core.ExportWork = new NativeList<RegionWork>(128, Allocator.Persistent);
            core.ExportFrames = new NativeList<AdvanceFrame>(64, Allocator.Persistent);
            core.ExportWalk = new NativeList<LifeShape>(128, Allocator.Persistent);
            core.ExportCells = new NativeList<int2>(256, Allocator.Persistent);
            core.Requests = new NativeArray<RegionRequest>(2, Allocator.Persistent);
            core.FullNodes = new NativeList<int>(64, Allocator.Persistent);
            Core kernel = ReadCore();
            LifeShape empty = kernel.EmptyShape();
            int epoch = kernel.AddEpoch(new WorldEpoch { Kind = 0, Initial = empty, World = 0 });
            kernel.Time.Published = new LifeSnapshot(epoch, 0);
            kernel.Time.Viewport = new LifeRegion(-64, -64, 64, 64);
            kernel.Time.Frame = new LifeViewFrame(kernel.Time.Published, kernel.Time.Viewport, empty, 1);
            kernel.Time.Population = new LifePopulationSample(kernel.Time.Published, 0);
            WriteCore(kernel);
        }

        public void SetViewport(int2 origin, int visibleCells)
        {
            LifeRegion viewport = new LifeRegion(origin.x, origin.y, (long)origin.x + visibleCells, (long)origin.y + visibleCells);
            Core kernel = ReadCore();
            if (kernel.Time.Viewport.Equals(viewport))
                return;
            kernel.Time.Viewport = viewport;
            kernel.StartRegionRequest(0, kernel.Time.Published, viewport, false);
            WriteCore(kernel);
        }

        public void RefreshView()
        {
            Core kernel = ReadCore();
            kernel.StartRegionRequest(0, kernel.Time.Published, kernel.Time.Viewport, false);
            WriteCore(kernel);
        }

        public void BeginAdvance(int stepExponent)
        {
            Core kernel = ReadCore();
            LifeSnapshot target = new LifeSnapshot(kernel.Time.Published.Epoch, kernel.Time.Published.Generation + (1L << stepExponent));
            kernel.StartRegionRequest(0, target, kernel.Time.Viewport, false);
            WriteCore(kernel);
        }

        public void CancelAdvance()
        {
            Core kernel = ReadCore();
            kernel.CancelRegionRequest(0);
            WriteCore(kernel);
        }

        public void Pump(int workBudget)
        {
            new RegionPumpJob { Kernel = core, State = state, Timeline = timeline, WorkBudget = workBudget }.Run();
            RegionRequest request = core.Requests[1];
            if (request.Ready != 0)
            {
                int2[] cells = core.ExportCells.AsArray().ToArray();
                core.ExportCells.Clear();
                request.Ready = 0;
                core.Requests[1] = request;
                ExportOperation completed = currentExport;
                currentExport = null;
                StartNextExport();
                completed.Completion.SetResult(cells);
            }
        }

        public Task<int2[]> CollectCellsAsync(bool selected, int2 min, int2 max)
        {
            var operation = new ExportOperation
            {
                Snapshot = Snapshot,
                Selected = selected,
                Bounds = new LifeRegion(min.x, min.y, (long)max.x + 1, (long)max.y + 1),
                Completion = new TaskCompletionSource<int2[]>(TaskCreationOptions.RunContinuationsAsynchronously)
            };
            exports.Enqueue(operation);
            StartNextExport();
            return operation.Completion.Task;
        }

        void StartNextExport()
        {
            if (currentExport != null || exports.Count == 0)
                return;
            currentExport = exports.Dequeue();
            Core kernel = ReadCore();
            LifeRegion bounds = currentExport.Selected ? currentExport.Bounds : kernel.Support(currentExport.Snapshot);
            kernel.StartRegionRequest(1, currentExport.Snapshot, bounds, !currentExport.Selected);
            WriteCore(kernel);
        }

        void RetainRegionRoots(ref Core kernel)
        {
            kernel.RetainEpoch(kernel.Time.Published.Epoch);
            kernel.RetainEpoch(kernel.Time.Frame.Snapshot.Epoch);
            if (kernel.State.Editing != 0)
                kernel.RetainEpoch(kernel.Time.EditSource.Epoch);
            for (int i = 0; i < 2; i++)
            {
                RegionRequest request = kernel.Requests[i];
                if (request.Active != 0 || request.Ready != 0)
                    kernel.RetainEpoch(request.Target.Epoch);
            }
            if (currentExport != null)
                kernel.RetainEpoch(currentExport.Snapshot.Epoch);
            foreach (ExportOperation operation in exports)
                kernel.RetainEpoch(operation.Snapshot.Epoch);
            kernel.RetainRequestNodes(0);
            kernel.RetainRequestNodes(1);
            kernel.Mark(kernel.Time.Frame.Shape.Root);
            for (int i = 0; i < kernel.FullNodes.Length; i++)
                kernel.Mark(kernel.FullNodes[i]);
            kernel.BeginRegionPrune();
        }

        void DisposeRegions()
        {
            if (currentExport != null)
                currentExport.Completion.SetCanceled();
            foreach (ExportOperation operation in exports)
                operation.Completion.SetCanceled();
            core.Epochs.Dispose();
            core.EpochSlots.Dispose();
            core.FreeEpochs.Dispose();
            core.EpochMarkStack.Dispose();
            core.Regions.Dispose();
            core.RegionKeys.Dispose();
            core.NewRegionKeys.Dispose();
            core.Frontiers.Dispose();
            core.FrontierKeys.Dispose();
            core.NewFrontierKeys.Dispose();
            core.ViewWork.Dispose();
            core.ExportWork.Dispose();
            core.ExportFrames.Dispose();
            core.ExportWalk.Dispose();
            core.ExportCells.Dispose();
            core.Requests.Dispose();
            core.FullNodes.Dispose();
            timeline.Dispose();
        }

        sealed class ExportOperation
        {
            public LifeSnapshot Snapshot;
            public LifeRegion Bounds;
            public bool Selected;
            public TaskCompletionSource<int2[]> Completion;
        }

        struct TimelineState
        {
            public LifeSnapshot Published;
            public LifeSnapshot EditSource;
            public LifeViewFrame Frame;
            public LifePopulationSample Population;
            public LifeRegion Viewport;
            public int NextEpoch;
            public int World;
            public int RegionCount;
            public int EpochCount;
            public int NextWorker;
            public long RequestClock;
            public int PrunePhase;
            public int PruneRead;
            public int PruneWrite;
            public int FrontierLimit;
            public int RegionLimit;
            public int EpochLimit;
        }

        struct WorldEpoch
        {
            public int Id;
            public int Kind;
            public int Mark;
            public int World;
            public int MaxCachedLevel;
            public LifeSnapshot Source;
            public LifeShape Initial;
            public LifeShape Alive;
            public LifeShape Dead;
            public LifeRegion Support;
            public int HasSupport;
            public long BaseGeneration;
        }

        struct SpatialKey : IEquatable<SpatialKey>
        {
            public int Epoch;
            public long X;
            public long Y;
            public int Level;

            public bool Equals(SpatialKey other) => Epoch == other.Epoch && X == other.X && Y == other.Y && Level == other.Level;
            public override bool Equals(object obj) => obj is SpatialKey other && Equals(other);
            public override int GetHashCode() => (int)math.hash(new uint4((uint)Epoch,
                (uint)X ^ (uint)(X >> 32), (uint)Y ^ (uint)(Y >> 32), (uint)Level));
        }

        struct RegionKey : IEquatable<RegionKey>
        {
            public SpatialKey Space;
            public long Generation;

            public bool Equals(RegionKey other) => Space.Equals(other.Space) && Generation == other.Generation;
            public override bool Equals(object obj) => obj is RegionKey other && Equals(other);
            public override int GetHashCode() => (int)math.hash(new uint3((uint)Space.GetHashCode(), (uint)Generation, (uint)(Generation >> 32)));
        }

        struct RegionFrontier
        {
            public long Latest;
            public long LastRequest;
        }

        struct RegionRequest
        {
            public int Active;
            public int Ready;
            public LifeSnapshot Target;
            public LifeRegion Bounds;
            public long X;
            public long Y;
            public int Level;
            public int Phase;
            public int Child;
            public int4 Children;
            public int Pending;
            public int CenterActive;
            public int CenterPending;
            public int Full;
        }

        struct RegionWork
        {
            public int Kind;
            public RegionKey Key;
            public LifeRegion Needed;
            public int Phase;
            public int Child;
            public int4 Children;
            public int Root;
            public int Exponent;
        }

        [BurstCompile]
        struct RegionPumpJob : IJob
        {
            public Core Kernel;
            public NativeArray<WorldState> State;
            public NativeArray<TimelineState> Timeline;
            public int WorkBudget;

            public void Execute()
            {
                Kernel.State = State[0];
                Kernel.Time = Timeline[0];
                Kernel.PumpRegions(WorkBudget);
                State[0] = Kernel.State;
                Timeline[0] = Kernel.Time;
            }
        }

        partial struct Core
        {
            const long RetainedRegionRequests = 64;
            public TimelineState Time;
            public NativeList<WorldEpoch> Epochs;
            public NativeParallelHashMap<int, int> EpochSlots;
            public NativeList<int> FreeEpochs;
            public NativeList<int> EpochMarkStack;
            public NativeParallelHashMap<RegionKey, int> Regions;
            public NativeList<RegionKey> RegionKeys;
            public NativeList<RegionKey> NewRegionKeys;
            public NativeParallelHashMap<SpatialKey, RegionFrontier> Frontiers;
            public NativeList<SpatialKey> FrontierKeys;
            public NativeList<SpatialKey> NewFrontierKeys;
            public NativeList<RegionWork> ViewWork;
            public NativeList<RegionWork> ExportWork;
            public NativeList<AdvanceFrame> ExportFrames;
            public NativeList<LifeShape> ExportWalk;
            public NativeList<int2> ExportCells;
            public NativeArray<RegionRequest> Requests;
            public NativeList<int> FullNodes;

            public LifeShape EmptyShape() => new LifeShape { Root = Empty(5), X = -16, Y = -16 };

            WorldEpoch Epoch(int id) => Epochs[EpochSlots[id]];

            public int AddEpoch(WorldEpoch value)
            {
                int slot;
                if (FreeEpochs.Length == 0)
                {
                    slot = Epochs.Length;
                    Epochs.Add(default);
                }
                else
                {
                    slot = FreeEpochs[FreeEpochs.Length - 1];
                    FreeEpochs.RemoveAt(FreeEpochs.Length - 1);
                }
                value.Id = Time.NextEpoch++;
                value.Mark = 0;
                Epochs[slot] = value;
                EpochSlots.Add(value.Id, slot);
                Time.EpochCount++;
                if (State.CollectionPhase != 0)
                    RetainEpoch(value.Id);
                return value.Id;
            }

            public LifeRegion Support(LifeSnapshot snapshot)
            {
                WorldEpoch epoch = Epoch(snapshot.Epoch);
                if (epoch.HasSupport == 0)
                    return default;
                long elapsed = snapshot.Generation - epoch.BaseGeneration;
                return new LifeRegion(epoch.Support.MinX - elapsed, epoch.Support.MinY - elapsed,
                    epoch.Support.MaxX + elapsed, epoch.Support.MaxY + elapsed);
            }

            public void BeginRegionEdit()
            {
                CancelRegionRequest(0);
                WorldEpoch epoch = Epoch(Time.Published.Epoch);
                if (epoch.Kind == 1 && epoch.BaseGeneration == Time.Published.Generation)
                {
                    Time.EditSource = epoch.Source;
                    State.EditShape = epoch.Alive;
                    State.EditDead = epoch.Dead;
                }
                else
                {
                    Time.EditSource = Time.Published;
                    State.EditShape = EmptyShape();
                    State.EditDead = State.EditShape;
                }
                State.Editing = 1;
            }

            public void EndRegionEdit()
            {
                State.EditShape = Shrink(State.EditShape);
                State.EditDead = Shrink(State.EditDead);
                LifeRegion support = Support(Time.EditSource);
                bool hasSupport = !support.IsEmpty;
                if (Nodes[State.EditShape.Root].Population != 0)
                {
                    LifeRegion added = ShapeBounds(State.EditShape);
                    support = hasSupport ? Union(support, added) : added;
                    hasSupport = true;
                }
                WorldEpoch source = Epoch(Time.EditSource.Epoch);
                WorldEpoch changed = new WorldEpoch
                {
                    Kind = 1,
                    World = source.World,
                    Source = Time.EditSource,
                    BaseGeneration = Time.EditSource.Generation,
                    Alive = State.EditShape,
                    Dead = State.EditDead,
                    Support = support,
                    HasSupport = hasSupport ? 1 : 0
                };
                if (source.HasSupport == 0)
                {
                    changed.Kind = 0;
                    changed.Initial = State.EditShape;
                }
                int id = AddEpoch(changed);
                Time.Published = new LifeSnapshot(id, Time.EditSource.Generation);
                if (source.HasSupport == 0)
                    Time.Population = new LifePopulationSample(Time.Published, Nodes[State.EditShape.Root].Population);
                State.Editing = 0;
                Track(State.EditShape.Root);
                Track(State.EditDead.Root);
                StartRegionRequest(0, Time.Published, Time.Viewport, false);
            }

            public void ClearRegions()
            {
                CancelRegionRequest(0);
                EditedTiles.Clear();
                EditedDeadTiles.Clear();
                State.EditShape = EmptyShape();
                State.EditDead = State.EditShape;
                Time.World++;
                int id = AddEpoch(new WorldEpoch { Kind = 0, Initial = State.EditShape, World = Time.World });
                Time.Published = new LifeSnapshot(id, 0);
                Time.EditSource = Time.Published;
                Time.Population = new LifePopulationSample(Time.Published, 0);
                StartRegionRequest(0, Time.Published, Time.Viewport, false);
            }

            public void CancelRegionRequest(int owner)
            {
                if (owner == 0)
                {
                    ViewWork.Clear();
                    Frames.Clear();
                }
                else
                {
                    ExportWork.Clear();
                    ExportFrames.Clear();
                    ExportWalk.Clear();
                }
                Requests[owner] = default;
            }

            public void StartRegionRequest(int owner, LifeSnapshot snapshot, LifeRegion bounds, bool full)
            {
                CancelRegionRequest(owner);
                Time.RequestClock++;
                WorldEpoch epoch = Epoch(snapshot.Epoch);
                if (epoch.Kind == 0 && snapshot.Generation == epoch.BaseGeneration)
                {
                    if (owner == 0)
                        PublishFrame(snapshot, bounds, epoch.Initial);
                    else
                    {
                        Requests[1] = new RegionRequest { Active = 1, Target = snapshot, Bounds = bounds, Phase = 1,
                            Pending = -1, CenterPending = -1, Full = full ? 1 : 0 };
                        ExportWalk.Add(epoch.Initial);
                    }
                    return;
                }
                long span = math.max(bounds.MaxX - bounds.MinX, bounds.MaxY - bounds.MinY);
                int level = 5;
                while ((1L << level) < span)
                    level++;
                long side = 1L << level;
                Requests[owner] = new RegionRequest
                {
                    Active = 1,
                    Target = snapshot,
                    Bounds = bounds,
                    X = bounds.MinX & -side,
                    Y = bounds.MinY & -side,
                    Level = level + 1,
                    Children = new int4(Empty(level)),
                    Pending = -1,
                    CenterPending = -1,
                    Full = full ? 1 : 0
                };
            }

            public void PumpRegions(int workBudget)
            {
                NativeList<AdvanceFrame> viewFrames = Frames;
                for (int work = 0; work < workBudget; work++)
                {
                    int owner = Time.NextWorker;
                    if (Requests[owner].Active == 0)
                        owner ^= 1;
                    if (Requests[owner].Active == 0)
                        break;
                    Time.NextWorker = owner ^ 1;
                    Frames = owner == 0 ? viewFrames : ExportFrames;
                    NativeList<RegionWork> stack = owner == 0 ? ViewWork : ExportWork;
                    RegionRequest request = Requests[owner];
                    State.Advancing = request.CenterActive;
                    State.PendingResult = request.CenterPending;
                    if (State.Advancing != 0)
                    {
                        AdvanceOne();
                        if (Frames.Length == 0)
                            State.Advancing = 0;
                    }
                    else if (stack.Length != 0)
                        StepRegion(stack, ref request);
                    else
                        StepRequest(owner, stack, ref request);
                    request.CenterActive = State.Advancing;
                    request.CenterPending = State.PendingResult;
                    Requests[owner] = request;
                }
                Frames = viewFrames;
            }

            void StepRequest(int owner, NativeList<RegionWork> stack, ref RegionRequest request)
            {
                if (request.Phase == 1)
                {
                    StepExport(ref request);
                    return;
                }
                if (request.Pending != -1)
                {
                    request.Children[request.Child] = request.Pending;
                    request.Pending = -1;
                    request.Child++;
                }
                long side = 1L << (request.Level - 1);
                while (request.Child < 4)
                {
                    long x = request.X + ((request.Child & 1) != 0 ? side : 0);
                    long y = request.Y + ((request.Child & 2) != 0 ? side : 0);
                    if (Intersects(request.Bounds, x, y, side))
                    {
                        stack.Add(Sample(request.Target, x, y, request.Level - 1));
                        return;
                    }
                    request.Child++;
                }
                int root = InternBranch(request.Children);
                LifeShape shape = new LifeShape { Root = root, X = request.X, Y = request.Y };
                if (owner == 0)
                {
                    Time.Published = request.Target;
                    PublishFrame(request.Target, request.Bounds, shape);
                    request.Active = 0;
                }
                else
                {
                    ExportWalk.Add(shape);
                    request.Phase = 1;
                }
            }

            void PublishFrame(LifeSnapshot snapshot, LifeRegion bounds, LifeShape shape)
            {
                LifeViewFrame previous = Time.Frame;
                bool changed = previous.Shape.Root != shape.Root || previous.Shape.X != shape.X || previous.Shape.Y != shape.Y
                    || !previous.ValidBounds.Equals(bounds);
                Time.Frame = new LifeViewFrame(snapshot, bounds, shape, previous.Version + (changed ? 1u : 0u));
            }

            void StepExport(ref RegionRequest request)
            {
                if (ExportWalk.Length == 0)
                {
                    request.Active = 0;
                    request.Ready = 1;
                    if (request.Full != 0 && Epoch(request.Target.Epoch).World == Epoch(Time.Published.Epoch).World
                        && request.Target.Generation >= Time.Population.Snapshot.Generation)
                        Time.Population = new LifePopulationSample(request.Target, ExportCells.Length);
                    return;
                }
                LifeShape shape = ExportWalk[ExportWalk.Length - 1];
                ExportWalk.RemoveAt(ExportWalk.Length - 1);
                LifeNode node = Nodes[shape.Root];
                long side = 1L << node.Level;
                if (node.Population == 0 || !Intersects(request.Bounds, shape.X, shape.Y, side))
                    return;
                if (node.Level > 4)
                {
                    long half = side / 2;
                    for (int i = 0; i < 4; i++)
                        ExportWalk.Add(new LifeShape
                        {
                            Root = node.Children[i],
                            X = shape.X + ((i & 1) != 0 ? half : 0),
                            Y = shape.Y + ((i & 2) != 0 ? half : 0)
                        });
                    return;
                }
                for (int y = 0; y < 16; y++)
                {
                    uint row = node.Bits.GetRow(y);
                    while (row != 0)
                    {
                        int x = math.tzcnt(row);
                        row &= row - 1;
                        long worldX = shape.X + x;
                        long worldY = shape.Y + y;
                        if (worldX >= request.Bounds.MinX && worldX < request.Bounds.MaxX
                            && worldY >= request.Bounds.MinY && worldY < request.Bounds.MaxY)
                            ExportCells.Add(new int2((int)worldX, (int)worldY));
                    }
                }
            }

            static RegionWork Sample(LifeSnapshot snapshot, long x, long y, int level) => new RegionWork
            {
                Kind = 0,
                Key = new RegionKey { Space = new SpatialKey { Epoch = snapshot.Epoch, X = x, Y = y, Level = level }, Generation = snapshot.Generation }
            };

            static RegionWork Gather(LifeSnapshot snapshot, long x, long y, int level, LifeRegion needed) => new RegionWork
            {
                Kind = 1,
                Key = new RegionKey { Space = new SpatialKey { Epoch = snapshot.Epoch, X = x, Y = y, Level = level }, Generation = snapshot.Generation },
                Needed = needed
            };

            void StepRegion(NativeList<RegionWork> stack, ref RegionRequest request)
            {
                int index = stack.Length - 1;
                RegionWork frame = stack[index];
                if (frame.Kind == 1)
                {
                    StepGather(stack, ref request, frame, index);
                    return;
                }
                if (frame.Kind == 2)
                {
                    StepPatch(stack, ref request, frame, index);
                    return;
                }
                SpatialKey space = frame.Key.Space;
                WorldEpoch epoch = Epoch(space.Epoch);
                if (frame.Phase == 0)
                {
                    if (TryRegion(frame.Key, epoch.MaxCachedLevel, out int cached))
                    {
                        FinishRegion(stack, ref request, cached);
                        return;
                    }
                    LifeRegion support = Support(new LifeSnapshot(space.Epoch, frame.Key.Generation));
                    if (support.IsEmpty || !Intersects(support, space.X, space.Y, 1L << space.Level)
                        || !HasLiveSource(epoch, frame.Key.Generation, space))
                    {
                        CompleteSample(stack, ref request, frame.Key, Empty(space.Level));
                        return;
                    }
                    if (frame.Key.Generation == epoch.BaseGeneration)
                    {
                        if (epoch.Kind == 0)
                        {
                            if (TryExtract(epoch.Initial, space, out int root))
                            {
                                CompleteSample(stack, ref request, frame.Key, root);
                                return;
                            }
                            frame.Phase = 1;
                            frame.Children = default;
                            stack[index] = frame;
                            stack.Add(Sample(new LifeSnapshot(space.Epoch, frame.Key.Generation), space.X, space.Y, space.Level - 1));
                            return;
                        }
                        if (PatchUniform(epoch.Dead, space, out int dead) && Nodes[dead].Population != 0)
                        {
                            CompleteSample(stack, ref request, frame.Key, Empty(space.Level));
                            return;
                        }
                        if (PatchUniform(epoch.Alive, space, out int alive) && Nodes[alive].Population != 0)
                        {
                            CompleteSample(stack, ref request, frame.Key, alive);
                            return;
                        }
                        frame.Phase = 2;
                        stack[index] = frame;
                        stack.Add(Sample(epoch.Source, space.X, space.Y, space.Level));
                        return;
                    }
                    if (space.Level == 4)
                    {
                        DelegateParent(stack, frame, index);
                        return;
                    }
                    long elapsed = frame.Key.Generation - epoch.BaseGeneration;
                    int exponent = math.tzcnt((ulong)elapsed);
                    long amount = 1L << exponent;
                    if (amount > (1L << (space.Level - 1)))
                    {
                        DelegateParent(stack, frame, index);
                        return;
                    }
                    long sourceGeneration = frame.Key.Generation - amount;
                    frame.Exponent = exponent;
                    frame.Phase = 3;
                    stack[index] = frame;
                    long half = 1L << (space.Level - 1);
                    LifeRegion needed = new LifeRegion(space.X - amount, space.Y - amount,
                        space.X + half * 2 + amount, space.Y + half * 2 + amount);
                    stack.Add(Gather(new LifeSnapshot(space.Epoch, sourceGeneration), space.X - half, space.Y - half, space.Level + 1, needed));
                    return;
                }
                if (frame.Phase == 1)
                {
                    frame.Children[frame.Child] = request.Pending;
                    request.Pending = -1;
                    frame.Child++;
                    if (frame.Child == 4)
                    {
                        CompleteSample(stack, ref request, frame.Key, InternBranch(frame.Children));
                        return;
                    }
                    stack[index] = frame;
                    long half = 1L << (space.Level - 1);
                    stack.Add(Sample(new LifeSnapshot(space.Epoch, frame.Key.Generation),
                        space.X + ((frame.Child & 1) != 0 ? half : 0), space.Y + ((frame.Child & 2) != 0 ? half : 0), space.Level - 1));
                    return;
                }
                if (frame.Phase == 2)
                {
                    frame.Phase = 6;
                    stack[index] = frame;
                    RegionWork patch = frame;
                    patch.Kind = 2;
                    patch.Phase = 0;
                    patch.Root = request.Pending;
                    request.Pending = -1;
                    stack.Add(patch);
                    return;
                }
                if (frame.Phase == 3)
                {
                    int input = request.Pending;
                    request.Pending = -1;
                    frame.Phase = 4;
                    stack[index] = frame;
                    BeginCenter(input, frame.Exponent);
                    return;
                }
                if (frame.Phase == 4)
                {
                    int result = State.PendingResult;
                    State.PendingResult = -1;
                    CompleteSample(stack, ref request, frame.Key, result);
                    return;
                }
                if (frame.Phase == 5)
                {
                    long half = 1L << space.Level;
                    int quadrant = ((space.X & half) != 0 ? 1 : 0) | ((space.Y & half) != 0 ? 2 : 0);
                    int result = Nodes[request.Pending].Children[quadrant];
                    request.Pending = -1;
                    CompleteSample(stack, ref request, frame.Key, result);
                    return;
                }
                int patched = request.Pending;
                request.Pending = -1;
                CompleteSample(stack, ref request, frame.Key, patched);
            }

            void DelegateParent(NativeList<RegionWork> stack, RegionWork frame, int index)
            {
                frame.Phase = 5;
                stack[index] = frame;
                long side = 1L << (frame.Key.Space.Level + 1);
                stack.Add(Sample(new LifeSnapshot(frame.Key.Space.Epoch, frame.Key.Generation),
                    frame.Key.Space.X & -side, frame.Key.Space.Y & -side, frame.Key.Space.Level + 1));
            }

            void StepGather(NativeList<RegionWork> stack, ref RegionRequest request, RegionWork frame, int index)
            {
                SpatialKey space = frame.Key.Space;
                long side = 1L << space.Level;
                if (frame.Phase == 0)
                {
                    if (!Intersects(frame.Needed, space.X, space.Y, side))
                    {
                        FinishRegion(stack, ref request, Empty(space.Level));
                        return;
                    }
                    bool aligned = (space.X & (side - 1)) == 0 && (space.Y & (side - 1)) == 0;
                    if (space.Level == 4 || aligned)
                    {
                        frame.Phase = 2;
                        stack[index] = frame;
                        stack.Add(Sample(new LifeSnapshot(space.Epoch, frame.Key.Generation), space.X, space.Y, space.Level));
                        return;
                    }
                    frame.Phase = 1;
                    stack[index] = frame;
                    stack.Add(Gather(new LifeSnapshot(space.Epoch, frame.Key.Generation), space.X, space.Y, space.Level - 1, frame.Needed));
                    return;
                }
                if (frame.Phase == 2)
                {
                    int result = request.Pending;
                    request.Pending = -1;
                    FinishRegion(stack, ref request, result);
                    return;
                }
                frame.Children[frame.Child] = request.Pending;
                request.Pending = -1;
                frame.Child++;
                if (frame.Child == 4)
                {
                    FinishRegion(stack, ref request, InternBranch(frame.Children));
                    return;
                }
                stack[index] = frame;
                long half = side / 2;
                stack.Add(Gather(new LifeSnapshot(space.Epoch, frame.Key.Generation),
                    space.X + ((frame.Child & 1) != 0 ? half : 0), space.Y + ((frame.Child & 2) != 0 ? half : 0), space.Level - 1, frame.Needed));
            }

            void StepPatch(NativeList<RegionWork> stack, ref RegionRequest request, RegionWork frame, int index)
            {
                SpatialKey space = frame.Key.Space;
                WorldEpoch epoch = Epoch(space.Epoch);
                if (frame.Phase == 0)
                {
                    bool hasAlive = PatchIntersects(epoch.Alive, space);
                    bool hasDead = PatchIntersects(epoch.Dead, space);
                    if (!hasAlive && !hasDead)
                    {
                        FinishRegion(stack, ref request, frame.Root);
                        return;
                    }
                    if (PatchUniform(epoch.Dead, space, out int dead) && Nodes[dead].Population != 0)
                    {
                        FinishRegion(stack, ref request, Empty(space.Level));
                        return;
                    }
                    if (TryExtract(epoch.Alive, space, out int alive) && !hasDead
                        && (Nodes[frame.Root].Population == 0 || alive == frame.Root))
                    {
                        FinishRegion(stack, ref request, alive);
                        return;
                    }
                    if (space.Level == 4)
                    {
                        LifeBits original = Nodes[frame.Root].Bits;
                        LifeBits live = ReadSquare(epoch.Alive.Root, epoch.Alive.X, epoch.Alive.Y, space.X, space.Y);
                        LifeBits erase = ReadSquare(epoch.Dead.Root, epoch.Dead.X, epoch.Dead.Y, space.X, space.Y);
                        int result = InternLeaf(new LifeBits
                        {
                            A = (original.A & ~erase.A) | live.A,
                            B = (original.B & ~erase.B) | live.B,
                            C = (original.C & ~erase.C) | live.C,
                            D = (original.D & ~erase.D) | live.D
                        });
                        FinishRegion(stack, ref request, result);
                        return;
                    }
                    frame.Phase = 1;
                    stack[index] = frame;
                    RegionWork child = frame;
                    child.Phase = 0;
                    child.Child = 0;
                    child.Key.Space.Level--;
                    child.Root = Nodes[frame.Root].Children.x;
                    stack.Add(child);
                    return;
                }
                frame.Children[frame.Child] = request.Pending;
                request.Pending = -1;
                frame.Child++;
                if (frame.Child == 4)
                {
                    FinishRegion(stack, ref request, InternBranch(frame.Children));
                    return;
                }
                stack[index] = frame;
                long half = 1L << (space.Level - 1);
                RegionWork next = frame;
                next.Phase = 0;
                next.Child = 0;
                next.Key.Space.Level--;
                next.Key.Space.X += (frame.Child & 1) != 0 ? half : 0;
                next.Key.Space.Y += (frame.Child & 2) != 0 ? half : 0;
                next.Root = Nodes[frame.Root].Children[frame.Child];
                stack.Add(next);
            }

            void FinishRegion(NativeList<RegionWork> stack, ref RegionRequest request, int root)
            {
                Track(root);
                request.Pending = root;
                stack.RemoveAt(stack.Length - 1);
            }

            void CompleteSample(NativeList<RegionWork> stack, ref RegionRequest request, RegionKey key, int root)
            {
                if (Regions.TryAdd(key, root))
                {
                    Time.RegionCount++;
                    if (Time.PrunePhase == 1 || Time.PrunePhase == 2)
                        NewRegionKeys.Add(key);
                    else
                        RegionKeys.Add(key);
                }
                RegionFrontier frontier;
                if (!Frontiers.TryGetValue(key.Space, out frontier))
                {
                    frontier = new RegionFrontier { Latest = key.Generation };
                    if (Time.PrunePhase == 1)
                        NewFrontierKeys.Add(key.Space);
                    else
                        FrontierKeys.Add(key.Space);
                }
                else if (key.Generation > frontier.Latest)
                    frontier.Latest = key.Generation;
                frontier.LastRequest = Time.RequestClock;
                Frontiers[key.Space] = frontier;
                WorldEpoch epoch = Epoch(key.Space.Epoch);
                epoch.MaxCachedLevel = math.max(epoch.MaxCachedLevel, key.Space.Level);
                Epochs[EpochSlots[epoch.Id]] = epoch;
                FinishRegion(stack, ref request, root);
            }

            bool TryRegion(RegionKey key, int maximumLevel, out int root)
            {
                RegionKey probe = key;
                for (int level = key.Space.Level; level <= maximumLevel; level++)
                {
                    long side = 1L << level;
                    probe.Space.Level = level;
                    probe.Space.X = key.Space.X & -side;
                    probe.Space.Y = key.Space.Y & -side;
                    if (!Regions.TryGetValue(probe, out root) || !Frontiers.TryGetValue(probe.Space, out RegionFrontier frontier))
                        continue;
                    frontier.LastRequest = Time.RequestClock;
                    Frontiers[probe.Space] = frontier;
                    for (int childLevel = level - 1; childLevel >= key.Space.Level; childLevel--)
                    {
                        long half = 1L << childLevel;
                        int quadrant = ((key.Space.X & half) != 0 ? 1 : 0) | ((key.Space.Y & half) != 0 ? 2 : 0);
                        root = Nodes[root].Children[quadrant];
                    }
                    Track(root);
                    return true;
                }
                root = -1;
                return false;
            }

            bool TryExtract(LifeShape shape, SpatialKey square, out int result)
            {
                long side = 1L << square.Level;
                int id = shape.Root;
                long x = shape.X;
                long y = shape.Y;
                LifeNode node = Nodes[id];
                long sourceSide = 1L << node.Level;
                if (node.Population == 0 || x >= square.X + side || y >= square.Y + side || square.X >= x + sourceSide || square.Y >= y + sourceSide)
                {
                    result = Empty(square.Level);
                    return true;
                }
                while (node.Level > square.Level)
                {
                    long half = 1L << (node.Level - 1);
                    int quadrant = (square.X >= x + half ? 1 : 0) | (square.Y >= y + half ? 2 : 0);
                    long childX = x + ((quadrant & 1) != 0 ? half : 0);
                    long childY = y + ((quadrant & 2) != 0 ? half : 0);
                    if (square.X < childX || square.Y < childY || square.X + side > childX + half || square.Y + side > childY + half)
                        break;
                    id = node.Children[quadrant];
                    x = childX;
                    y = childY;
                    node = Nodes[id];
                    if (node.Population == 0)
                    {
                        result = Empty(square.Level);
                        return true;
                    }
                }
                if (node.Level == square.Level && x == square.X && y == square.Y)
                {
                    result = id;
                    return true;
                }
                if (square.Level == 4)
                {
                    result = InternLeaf(ReadSquare(shape.Root, shape.X, shape.Y, square.X, square.Y));
                    return true;
                }
                result = -1;
                return false;
            }

            bool PatchUniform(LifeShape patch, SpatialKey square, out int root)
            {
                if (!TryExtract(patch, square, out root))
                    return false;
                if (Nodes[root].Population == 0)
                    return true;
                int id = root;
                LifeNode node = Nodes[id];
                while (node.Level > 4)
                {
                    if (node.Children.x != node.Children.y || node.Children.x != node.Children.z || node.Children.x != node.Children.w)
                        return false;
                    id = node.Children.x;
                    node = Nodes[id];
                }
                LifeBits bits = node.Bits;
                return bits.A == ulong.MaxValue && bits.B == ulong.MaxValue && bits.C == ulong.MaxValue && bits.D == ulong.MaxValue;
            }

            bool PatchIntersects(LifeShape patch, SpatialKey square) => Nodes[patch.Root].Population != 0
                && Intersects(ShapeBounds(patch), square.X, square.Y, 1L << square.Level);

            bool HasLiveSource(WorldEpoch epoch, long generation, SpatialKey square)
            {
                long side = 1L << square.Level;
                LifeRegion cone = new LifeRegion(square.X, square.Y, square.X + side, square.Y + side);
                while (true)
                {
                    long elapsed = generation - epoch.BaseGeneration;
                    cone = new LifeRegion(cone.MinX - elapsed, cone.MinY - elapsed, cone.MaxX + elapsed, cone.MaxY + elapsed);
                    LifeShape source = epoch.Kind == 0 ? epoch.Initial : epoch.Alive;
                    if (HasLiveInRegion(source.Root, source.X, source.Y, cone))
                        return true;
                    if (epoch.Kind == 0)
                        return false;
                    generation = epoch.Source.Generation;
                    epoch = Epoch(epoch.Source.Epoch);
                }
            }

            bool HasLiveInRegion(int id, long x, long y, LifeRegion area)
            {
                LifeNode node = Nodes[id];
                long side = 1L << node.Level;
                if (node.Population == 0 || !Intersects(area, x, y, side))
                    return false;
                if (area.MinX <= x && area.MinY <= y && area.MaxX >= x + side && area.MaxY >= y + side)
                    return true;
                if (node.Level == 4)
                {
                    int left = (int)math.max(0L, area.MinX - x);
                    int right = (int)math.min(16L, area.MaxX - x);
                    int bottom = (int)math.max(0L, area.MinY - y);
                    int top = (int)math.min(16L, area.MaxY - y);
                    uint mask = ((1u << (right - left)) - 1) << left;
                    for (int row = bottom; row < top; row++)
                        if ((node.Bits.GetRow(row) & mask) != 0)
                            return true;
                    return false;
                }
                long half = side / 2;
                for (int child = 0; child < 4; child++)
                    if (HasLiveInRegion(node.Children[child], x + ((child & 1) != 0 ? half : 0),
                        y + ((child & 2) != 0 ? half : 0), area))
                        return true;
                return false;
            }

            static LifeRegion ShapeBounds(LifeShape shape, int level) => new LifeRegion(shape.X, shape.Y, shape.X + (1L << level), shape.Y + (1L << level));
            LifeRegion ShapeBounds(LifeShape shape) => ShapeBounds(shape, Nodes[shape.Root].Level);
            static bool Intersects(LifeRegion area, long x, long y, long side) => !area.IsEmpty && x < area.MaxX && y < area.MaxY && x + side > area.MinX && y + side > area.MinY;
            static LifeRegion Union(LifeRegion first, LifeRegion second) => new LifeRegion(math.min(first.MinX, second.MinX), math.min(first.MinY, second.MinY), math.max(first.MaxX, second.MaxX), math.max(first.MaxY, second.MaxY));

            public void RetainEpoch(int id)
            {
                int slot = EpochSlots[id];
                WorldEpoch epoch = Epochs[slot];
                if (epoch.Mark == State.CollectionEpoch)
                    return;
                epoch.Mark = State.CollectionEpoch;
                Epochs[slot] = epoch;
                EpochMarkStack.Add(id);
            }

            void MarkEpochOne()
            {
                int index = EpochMarkStack.Length - 1;
                int id = EpochMarkStack[index];
                EpochMarkStack.RemoveAt(index);
                WorldEpoch epoch = Epoch(id);
                if (epoch.Kind == 0)
                {
                    Mark(epoch.Initial.Root);
                    return;
                }
                Mark(epoch.Alive.Root);
                Mark(epoch.Dead.Root);
                RetainEpoch(epoch.Source.Epoch);
            }

            public void RetainRequestNodes(int owner)
            {
                RegionRequest request = Requests[owner];
                NativeList<RegionWork> work = owner == 0 ? ViewWork : ExportWork;
                NativeList<AdvanceFrame> centers = owner == 0 ? Frames : ExportFrames;
                if (request.Active != 0 || request.Ready != 0)
                {
                    for (int i = 0; i < 4; i++)
                        Mark(request.Children[i]);
                    if (request.Pending != -1)
                        Mark(request.Pending);
                    if (request.CenterPending != -1)
                        Mark(request.CenterPending);
                }
                for (int i = 0; i < work.Length; i++)
                {
                    RegionWork frame = work[i];
                    Mark(frame.Root);
                    for (int child = 0; child < 4; child++)
                        Mark(frame.Children[child]);
                }
                for (int i = 0; i < centers.Length; i++)
                {
                    AdvanceFrame frame = centers[i];
                    Mark(frame.Node);
                    for (int n = 0; n < frame.Inputs.Length; n++)
                        Mark(frame.Inputs[n]);
                    for (int n = 0; n < frame.Results.Length; n++)
                        Mark(frame.Results[n]);
                }
                if (owner == 1)
                    for (int i = 0; i < ExportWalk.Length; i++)
                        Mark(ExportWalk[i].Root);
            }

            public void BeginRegionPrune()
            {
                Time.PrunePhase = 1;
                Time.PruneRead = 0;
                Time.PruneWrite = 0;
                Time.FrontierLimit = FrontierKeys.Length;
                Time.RegionLimit = RegionKeys.Length;
                Time.EpochLimit = Epochs.Length;
            }

            public void PruneRegionOne()
            {
                if (Time.PrunePhase == 1)
                {
                    if (Time.PruneRead < Time.FrontierLimit)
                    {
                        SpatialKey key = FrontierKeys[Time.PruneRead++];
                        RegionFrontier frontier = Frontiers[key];
                        if (Epoch(key.Epoch).Mark == State.CollectionEpoch && Time.RequestClock - frontier.LastRequest <= RetainedRegionRequests)
                            FrontierKeys[Time.PruneWrite++] = key;
                        else
                            Frontiers.Remove(key);
                        return;
                    }
                    FrontierKeys.ResizeUninitialized(Time.PruneWrite);
                    FrontierKeys.AddRange(NewFrontierKeys.AsArray());
                    NewFrontierKeys.Clear();
                    Time.PruneRead = 0;
                    Time.PruneWrite = 0;
                    Time.PrunePhase = 2;
                    return;
                }
                if (Time.PrunePhase == 2)
                {
                    if (Time.PruneRead < Time.RegionLimit)
                    {
                        RegionKey key = RegionKeys[Time.PruneRead++];
                        long baseGeneration = Epoch(key.Space.Epoch).BaseGeneration;
                        if (Frontiers.TryGetValue(key.Space, out RegionFrontier frontier)
                            && IsTimeAncestor(key.Generation - baseGeneration, frontier.Latest - baseGeneration))
                        {
                            Mark(Regions[key]);
                            RegionKeys[Time.PruneWrite++] = key;
                        }
                        else
                        {
                            Regions.Remove(key);
                            Time.RegionCount--;
                        }
                        return;
                    }
                    RegionKeys.ResizeUninitialized(Time.PruneWrite);
                    RegionKeys.AddRange(NewRegionKeys.AsArray());
                    NewRegionKeys.Clear();
                    Time.PruneRead = 0;
                    Time.PrunePhase = 3;
                    return;
                }
                if (Time.PruneRead < Time.EpochLimit)
                {
                    int slot = Time.PruneRead++;
                    WorldEpoch epoch = Epochs[slot];
                    if (epoch.Kind != -1 && epoch.Mark != State.CollectionEpoch)
                    {
                        EpochSlots.Remove(epoch.Id);
                        Time.EpochCount--;
                        epoch.Kind = -1;
                        Epochs[slot] = epoch;
                        FreeEpochs.Add(slot);
                    }
                    return;
                }
                Time.PrunePhase = 0;
            }

            static bool IsTimeAncestor(long ancestor, long descendant) =>
                (descendant & -(ancestor & -ancestor)) == ancestor;
        }
    }
}
