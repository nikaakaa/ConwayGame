using System;
using System.Collections.Generic;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace GameOfLife
{
    public sealed partial class LifeWorld : IDisposable
    {
        Core core;
        NativeArray<WorldState> state;

        public NativeArray<LifeNode> Nodes => core.Nodes.AsArray();
        public int NodeCount => state[0].NodeCount;
        public int CacheCount => state[0].CacheCount;
        public long CacheHits => state[0].CacheHits;
        public long CacheMisses => state[0].CacheMisses;
        public bool IsCollecting => state[0].CollectionPhase != 0;

        public LifeWorld()
        {
            core = new Core
            {
                Nodes = new NativeList<LifeNode>(256, Allocator.Persistent),
                Leaves = new NativeParallelHashMap<LifeBits, int>(128, Allocator.Persistent),
                Branches = new NativeParallelHashMap<int4, int>(256, Allocator.Persistent),
                Cache = new NativeParallelHashMap<AdvanceKey, int>(256, Allocator.Persistent),
                EmptyNodes = new NativeList<int>(64, Allocator.Persistent),
                FreeNodes = new NativeList<int>(128, Allocator.Persistent),
                RetiredNodes = new NativeList<int>(128, Allocator.Persistent),
                Marks = new NativeList<int>(256, Allocator.Persistent),
                MarkStack = new NativeList<int>(256, Allocator.Persistent),
                Frames = new NativeList<AdvanceFrame>(64, Allocator.Persistent),
                EditedTiles = new NativeParallelHashMap<int2, LifeBits>(128, Allocator.Persistent),
                EditedDeadTiles = new NativeParallelHashMap<int2, LifeBits>(128, Allocator.Persistent)
            };
            state = new NativeArray<WorldState>(1, Allocator.Persistent);
            int leaf = core.InternLeaf(default);
            for (int i = 0; i <= 4; i++)
                core.EmptyNodes.Add(leaf);
            core.State.EditShape = new LifeShape { Root = core.Empty(5), X = -16, Y = -16 };
            core.State.EditDead = core.State.EditShape;
            state[0] = core.State;
            InitializeRegions();
        }

        public LifeShape BuildShape(LifeTile[] tiles)
        {
            using (var input = new NativeArray<LifeTile>(tiles, Allocator.TempJob))
            using (var entries = new NativeArray<BuildEntry>(tiles.Length, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
            using (var output = new NativeArray<LifeShape>(1, Allocator.TempJob, NativeArrayOptions.UninitializedMemory))
            {
                new BuildJob
                {
                    Kernel = core,
                    State = state,
                    Tiles = input,
                    Entries = entries,
                    Output = output
                }.Run();
                return output[0];
            }
        }

        public void BeginEdit()
        {
            Core kernel = ReadCore();
            kernel.BeginRegionEdit();
            WriteCore(kernel);
        }

        public void SetCell(int2 cell, bool alive)
        {
            int2 tile = cell & ~15;
            if (!core.EditedTiles.TryGetValue(tile, out LifeBits liveBits))
                liveBits = core.ReadTile(state[0].EditShape, tile.x, tile.y);
            if (!core.EditedDeadTiles.TryGetValue(tile, out LifeBits deadBits))
                deadBits = core.ReadTile(state[0].EditDead, tile.x, tile.y);
            liveBits.Set(cell.x & 15, cell.y & 15, alive);
            deadBits.Set(cell.x & 15, cell.y & 15, !alive);
            core.EditedTiles[tile] = liveBits;
            core.EditedDeadTiles[tile] = deadBits;
        }

        public void Stamp(LifeShape shape, int2 anchor)
        {
            Core kernel = ReadCore();
            kernel.FlushEdits();
            shape.X += anchor.x;
            shape.Y += anchor.y;
            LifeShape edited = kernel.State.EditShape;
            kernel.Overlay(ref edited, shape);
            kernel.State.EditShape = edited;
            LifeShape dead = kernel.State.EditDead;
            dead.Root = kernel.Difference(dead.Root, dead.X, dead.Y, shape.Root, shape.X, shape.Y);
            kernel.State.EditDead = dead;
            WriteCore(kernel);
        }

        public void EndEdit()
        {
            Core kernel = ReadCore();
            kernel.FlushEdits();
            kernel.EndRegionEdit();
            WriteCore(kernel);
        }

        public void ClearArea(int2 min, int2 max)
        {
            Core kernel = ReadCore();
            bool commit = kernel.State.Editing == 0;
            if (commit)
                kernel.BeginRegionEdit();
            kernel.FlushEdits();
            LifeShape shape = kernel.State.EditShape;
            shape.Root = kernel.Erase(shape.Root, shape.X, shape.Y, min.x, min.y, (long)max.x + 1, (long)max.y + 1);
            shape = kernel.Shrink(shape);
            kernel.State.EditShape = shape;
            LifeShape dead = kernel.State.EditDead;
            kernel.FillRectangle(ref dead, min.x, min.y, (long)max.x + 1, (long)max.y + 1);
            kernel.State.EditDead = dead;
            kernel.Track(shape.Root);
            if (commit)
                kernel.EndRegionEdit();
            WriteCore(kernel);
        }

        public void Clear()
        {
            Core kernel = ReadCore();
            kernel.ClearRegions();
            WriteCore(kernel);
        }

        public void BeginCollection(IReadOnlyList<LifeShape> retainedShapes)
        {
            Core kernel = ReadCore();
            kernel.BeginCollection();
            RetainRegionRoots(ref kernel);
            for (int i = 0; i < retainedShapes.Count; i++)
                kernel.Mark(retainedShapes[i].Root);
            WriteCore(kernel);
        }

        public void BeginCollection(NativeArray<LifeShape> retainedShapes)
        {
            Core kernel = ReadCore();
            kernel.BeginCollection();
            RetainRegionRoots(ref kernel);
            for (int i = 0; i < retainedShapes.Length; i++)
                kernel.Mark(retainedShapes[i].Root);
            WriteCore(kernel);
        }

        public bool ContinueCollection(int workBudget)
        {
            new CollectionJob { Kernel = core, State = state, Timeline = timeline, WorkBudget = workBudget }.Run();
            return state[0].CollectionPhase == 0;
        }

        public void Dispose()
        {
            DisposeRegions();
            core.Nodes.Dispose();
            core.Leaves.Dispose();
            core.Branches.Dispose();
            core.Cache.Dispose();
            core.EmptyNodes.Dispose();
            core.FreeNodes.Dispose();
            core.RetiredNodes.Dispose();
            core.Marks.Dispose();
            core.MarkStack.Dispose();
            core.Frames.Dispose();
            core.EditedTiles.Dispose();
            core.EditedDeadTiles.Dispose();
            state.Dispose();
        }

        Core ReadCore()
        {
            Core kernel = core;
            kernel.State = state[0];
            kernel.Time = timeline[0];
            return kernel;
        }

        void WriteCore(Core kernel)
        {
            state[0] = kernel.State;
            timeline[0] = kernel.Time;
        }

        struct WorldState
        {
            public LifeShape EditShape;
            public LifeShape EditDead;
            public int Editing;
            public int Advancing;
            public int PendingResult;
            public int CollectionPhase;
            public int CollectionEpoch;
            public int SweepCursor;
            public int SweepLimit;
            public int NodeCount;
            public int CacheCount;
            public long CacheHits;
            public long CacheMisses;
        }

        struct AdvanceKey : IEquatable<AdvanceKey>
        {
            public int Node;
            public int Exponent;

            public bool Equals(AdvanceKey other) => Node == other.Node && Exponent == other.Exponent;
            public override bool Equals(object obj) => obj is AdvanceKey other && Equals(other);
            public override int GetHashCode() => (int)math.hash(new int2(Node, Exponent));
        }

        struct AdvanceFrame
        {
            public int Node;
            public int Exponent;
            public int Stage;
            public int Index;
            public FixedList64Bytes<int> Inputs;
            public FixedList64Bytes<int> Results;
        }

        struct BuildEntry : IComparable<BuildEntry>
        {
            public ulong Morton;
            public int Node;

            public int CompareTo(BuildEntry other) => Morton.CompareTo(other.Morton);
        }

        [BurstCompile]
        struct BuildJob : IJob
        {
            public Core Kernel;
            public NativeArray<WorldState> State;
            [ReadOnly] public NativeArray<LifeTile> Tiles;
            public NativeArray<BuildEntry> Entries;
            public NativeArray<LifeShape> Output;

            public void Execute()
            {
                Kernel.State = State[0];
                Output[0] = Kernel.BuildShape(Tiles, Entries);
                State[0] = Kernel.State;
            }
        }

        [BurstCompile]
        struct CollectionJob : IJob
        {
            public Core Kernel;
            public NativeArray<WorldState> State;
            public NativeArray<TimelineState> Timeline;
            public int WorkBudget;

            public void Execute()
            {
                Kernel.State = State[0];
                Kernel.Time = Timeline[0];
                Kernel.ContinueCollection(WorkBudget);
                State[0] = Kernel.State;
                Timeline[0] = Kernel.Time;
            }
        }

        partial struct Core
        {
            public WorldState State;
            public NativeList<LifeNode> Nodes;
            public NativeParallelHashMap<LifeBits, int> Leaves;
            public NativeParallelHashMap<int4, int> Branches;
            public NativeParallelHashMap<AdvanceKey, int> Cache;
            public NativeList<int> EmptyNodes;
            public NativeList<int> FreeNodes;
            public NativeList<int> RetiredNodes;
            public NativeList<int> Marks;
            public NativeList<int> MarkStack;
            public NativeList<AdvanceFrame> Frames;
            public NativeParallelHashMap<int2, LifeBits> EditedTiles;
            public NativeParallelHashMap<int2, LifeBits> EditedDeadTiles;

            public LifeShape BuildShape(NativeArray<LifeTile> tiles, NativeArray<BuildEntry> entries)
            {
                if (tiles.Length == 0)
                    return new LifeShape { Root = Empty(5), X = -16, Y = -16 };

                long minX = tiles[0].Position.x;
                long minY = tiles[0].Position.y;
                for (int i = 1; i < tiles.Length; i++)
                {
                    minX = math.min(minX, tiles[i].Position.x);
                    minY = math.min(minY, tiles[i].Position.y);
                }
                for (int i = 0; i < tiles.Length; i++)
                {
                    LifeTile tile = tiles[i];
                    uint x = (uint)(((long)tile.Position.x - minX) >> 4);
                    uint y = (uint)(((long)tile.Position.y - minY) >> 4);
                    entries[i] = new BuildEntry
                    {
                        Morton = Interleave(x) | (Interleave(y) << 1),
                        Node = InternLeaf(tile.Bits)
                    };
                }
                entries.Sort();

                int count = entries.Length;
                int level = 4;
                do
                {
                    int empty = Empty(level);
                    int read = 0;
                    int write = 0;
                    while (read < count)
                    {
                        ulong parent = entries[read].Morton >> 2;
                        int4 children = new int4(empty);
                        do
                        {
                            BuildEntry entry = entries[read++];
                            children[(int)(entry.Morton & 3)] = entry.Node;
                        }
                        while (read < count && (entries[read].Morton >> 2) == parent);
                        entries[write++] = new BuildEntry { Morton = parent, Node = InternBranch(children) };
                    }
                    count = write;
                    level++;
                }
                while (count > 1);

                return Shrink(new LifeShape { Root = entries[0].Node, X = minX, Y = minY });
            }

            static ulong Interleave(uint value)
            {
                ulong bits = value;
                bits = (bits | (bits << 16)) & 0x0000ffff0000ffffUL;
                bits = (bits | (bits << 8)) & 0x00ff00ff00ff00ffUL;
                bits = (bits | (bits << 4)) & 0x0f0f0f0f0f0f0f0fUL;
                bits = (bits | (bits << 2)) & 0x3333333333333333UL;
                return (bits | (bits << 1)) & 0x5555555555555555UL;
            }

            public int InternLeaf(LifeBits bits)
            {
                if (Leaves.TryGetValue(bits, out int id))
                {
                    Track(id);
                    return id;
                }
                id = Allocate(new LifeNode { Level = 4, Population = bits.Population, Bits = bits });
                Leaves.Add(bits, id);
                return id;
            }

            public int InternBranch(int4 children)
            {
                if (Branches.TryGetValue(children, out int id))
                {
                    Track(id);
                    return id;
                }
                id = Allocate(new LifeNode
                {
                    Level = Nodes[children.x].Level + 1,
                    Population = Nodes[children.x].Population + Nodes[children.y].Population + Nodes[children.z].Population + Nodes[children.w].Population,
                    Children = children
                });
                Branches.Add(children, id);
                return id;
            }

            int Allocate(LifeNode node)
            {
                bool collecting = State.CollectionPhase != 0;
                int mark = collecting ? State.CollectionEpoch : 0;
                int id;
                if (FreeNodes.Length != 0)
                {
                    id = FreeNodes[FreeNodes.Length - 1];
                    FreeNodes.RemoveAt(FreeNodes.Length - 1);
                    Nodes[id] = node;
                    Marks[id] = mark;
                }
                else
                {
                    id = Nodes.Length;
                    Nodes.Add(node);
                    Marks.Add(mark);
                }
                State.NodeCount++;
                if (collecting && node.Level > 4)
                    for (int child = 0; child < 4; child++)
                        Mark(node.Children[child]);
                return id;
            }

            public int Empty(int level)
            {
                while (EmptyNodes.Length <= level)
                {
                    int child = EmptyNodes[EmptyNodes.Length - 1];
                    EmptyNodes.Add(InternBranch(new int4(child)));
                }
                return EmptyNodes[level];
            }

            public void Track(int id)
            {
                if (State.CollectionPhase != 0)
                    Mark(id, true);
            }

            public void Mark(int id, bool acquired = false)
            {
                if (Marks[id] == State.CollectionEpoch)
                    return;
                if (acquired && State.CollectionPhase >= 2)
                {
                    LifeNode node = Nodes[id];
                    if (node.Level > 4)
                    {
                        // 弱驻留节点重新取得时，旧缓存可能引用本轮已清扫的结果。
                        RemoveCachedResults(id, ref node);
                        Nodes[id] = node;
                    }
                }
                Marks[id] = State.CollectionEpoch;
                MarkStack.Add(id);
            }

            void RemoveCachedResults(int id, ref LifeNode node)
            {
                ulong exponents = node.CachedExponents;
                while (exponents != 0)
                {
                    int exponent = math.tzcnt(exponents);
                    exponents &= exponents - 1;
                    Cache.Remove(new AdvanceKey { Node = id, Exponent = exponent });
                    State.CacheCount--;
                }
                node.CachedExponents = 0;
            }

            public LifeBits ReadTile(LifeShape shape, long x, long y)
            {
                int id = FindLeaf(shape.Root, x - shape.X, y - shape.Y);
                return id == -1 ? default : Nodes[id].Bits;
            }

            int FindLeaf(int root, long x, long y)
            {
                int level = Nodes[root].Level;
                long side = 1L << level;
                if (x < 0 || y < 0 || x >= side || y >= side)
                    return -1;
                while (level > 4)
                {
                    if (Nodes[root].Population == 0)
                        return EmptyNodes[4];
                    long half = 1L << (level - 1);
                    int quadrant = (x >= half ? 1 : 0) | (y >= half ? 2 : 0);
                    root = Nodes[root].Children[quadrant];
                    if (x >= half) x -= half;
                    if (y >= half) y -= half;
                    level--;
                }
                return root;
            }

            public void FlushEdits()
            {
                if (EditedTiles.IsEmpty)
                    return;
                LifeShape shape = State.EditShape;
                foreach (var pair in EditedTiles)
                {
                    if (pair.Value.Population != 0)
                        EnsureContains(ref shape, pair.Key.x, pair.Key.y, (long)pair.Key.x + 16, (long)pair.Key.y + 16);
                    long side = 1L << Nodes[shape.Root].Level;
                    if (pair.Key.x >= shape.X && pair.Key.y >= shape.Y && pair.Key.x < shape.X + side && pair.Key.y < shape.Y + side)
                        shape.Root = ReplaceTile(shape.Root, shape.X, shape.Y, pair.Key, pair.Value);
                }
                EditedTiles.Clear();
                State.EditShape = shape;
                shape = State.EditDead;
                foreach (var pair in EditedDeadTiles)
                {
                    if (pair.Value.Population != 0)
                        EnsureContains(ref shape, pair.Key.x, pair.Key.y, (long)pair.Key.x + 16, (long)pair.Key.y + 16);
                    long side = 1L << Nodes[shape.Root].Level;
                    if (pair.Key.x >= shape.X && pair.Key.y >= shape.Y && pair.Key.x < shape.X + side && pair.Key.y < shape.Y + side)
                        shape.Root = ReplaceTile(shape.Root, shape.X, shape.Y, pair.Key, pair.Value);
                }
                EditedDeadTiles.Clear();
                State.EditDead = shape;
            }

            int ReplaceTile(int id, long x, long y, int2 tile, LifeBits bits)
            {
                LifeNode node = Nodes[id];
                if (node.Level == 4)
                    return InternLeaf(bits);
                long half = 1L << (node.Level - 1);
                int quadrant = (tile.x >= x + half ? 1 : 0) | (tile.y >= y + half ? 2 : 0);
                int4 children = node.Children;
                children[quadrant] = ReplaceTile(children[quadrant], x + ((quadrant & 1) != 0 ? half : 0), y + ((quadrant & 2) != 0 ? half : 0), tile, bits);
                return InternBranch(children);
            }

            public void Overlay(ref LifeShape target, LifeShape source)
            {
                if (Nodes[source.Root].Population == 0)
                    return;
                if (Nodes[target.Root].Population == 0 && (source.X & 15) == 0 && (source.Y & 15) == 0)
                {
                    target = source;
                    return;
                }
                long side = 1L << Nodes[source.Root].Level;
                EnsureContains(ref target, source.X, source.Y, source.X + side, source.Y + side);
                target.Root = Merge(target.Root, target.X, target.Y, source.Root, source.X, source.Y);
            }

            void EnsureContains(ref LifeShape shape, long minX, long minY, long maxX, long maxY)
            {
                long side = 1L << Nodes[shape.Root].Level;
                while (minX < shape.X || minY < shape.Y || maxX > shape.X + side || maxY > shape.Y + side)
                {
                    shape = Expand(shape);
                    side *= 2;
                }
            }

            LifeShape Expand(LifeShape shape)
            {
                LifeNode node = Nodes[shape.Root];
                int empty = Empty(node.Level - 1);
                int sw = InternBranch(new int4(empty, empty, empty, node.Children.x));
                int se = InternBranch(new int4(empty, empty, node.Children.y, empty));
                int nw = InternBranch(new int4(empty, node.Children.z, empty, empty));
                int ne = InternBranch(new int4(node.Children.w, empty, empty, empty));
                long half = 1L << (node.Level - 1);
                return new LifeShape { Root = InternBranch(new int4(sw, se, nw, ne)), X = shape.X - half, Y = shape.Y - half };
            }

            int Merge(int target, long tx, long ty, int source, long sx, long sy)
                => Combine(target, tx, ty, source, sx, sy, false);

            public int Difference(int target, long tx, long ty, int source, long sx, long sy)
                => Combine(target, tx, ty, source, sx, sy, true);

            int Combine(int target, long tx, long ty, int source, long sx, long sy, bool subtract)
            {
                LifeNode targetNode = Nodes[target];
                LifeNode sourceNode = Nodes[source];
                if (subtract && targetNode.Population == 0)
                    return target;
                long targetSide = 1L << targetNode.Level;
                long sourceSide = 1L << sourceNode.Level;
                if (sourceNode.Population == 0 || tx >= sx + sourceSide || ty >= sy + sourceSide || sx >= tx + targetSide || sy >= ty + targetSide)
                    return target;

                while (sourceNode.Level > targetNode.Level)
                {
                    long half = sourceSide / 2;
                    int qx = tx >= sx + half ? 1 : 0;
                    int qy = ty >= sy + half ? 2 : 0;
                    long childX = sx + (qx != 0 ? half : 0);
                    long childY = sy + (qy != 0 ? half : 0);
                    if (tx < childX || ty < childY || tx + targetSide > childX + half || ty + targetSide > childY + half)
                        break;
                    source = sourceNode.Children[qx | qy];
                    sx = childX;
                    sy = childY;
                    sourceNode = Nodes[source];
                    sourceSide = half;
                }

                if (tx == sx && ty == sy && targetNode.Level == sourceNode.Level)
                {
                    if (target == source)
                        return subtract ? Empty(targetNode.Level) : source;
                    if (targetNode.Population == 0)
                        return subtract ? target : source;
                    if (targetNode.Level > 4)
                    {
                        int4 merged = default;
                        long half = targetSide / 2;
                        for (int i = 0; i < 4; i++)
                        {
                            long x = tx + ((i & 1) != 0 ? half : 0);
                            long y = ty + ((i & 2) != 0 ? half : 0);
                            merged[i] = Combine(targetNode.Children[i], x, y, sourceNode.Children[i], x, y, subtract);
                        }
                        return InternBranch(merged);
                    }
                }

                if (targetNode.Level == 4)
                {
                    LifeBits incoming = ReadSquare(source, sx, sy, tx, ty);
                    LifeBits current = targetNode.Bits;
                    return InternLeaf(subtract
                        ? new LifeBits { A = current.A & ~incoming.A, B = current.B & ~incoming.B, C = current.C & ~incoming.C, D = current.D & ~incoming.D }
                        : new LifeBits { A = current.A | incoming.A, B = current.B | incoming.B, C = current.C | incoming.C, D = current.D | incoming.D });
                }

                int4 children = targetNode.Children;
                long childSide = targetSide / 2;
                for (int i = 0; i < 4; i++)
                    children[i] = Combine(children[i], tx + ((i & 1) != 0 ? childSide : 0), ty + ((i & 2) != 0 ? childSide : 0), source, sx, sy, subtract);
                return InternBranch(children);
            }

            public void FillRectangle(ref LifeShape shape, long minX, long minY, long maxX, long maxY)
            {
                EnsureContains(ref shape, minX, minY, maxX, maxY);
                shape.Root = Fill(shape.Root, shape.X, shape.Y, minX, minY, maxX, maxY);
            }

            int Full(int level)
            {
                if (FullNodes.Length == 0)
                {
                    int leaf = InternLeaf(new LifeBits { A = ulong.MaxValue, B = ulong.MaxValue, C = ulong.MaxValue, D = ulong.MaxValue });
                    for (int i = 0; i <= 4; i++)
                        FullNodes.Add(leaf);
                }
                while (FullNodes.Length <= level)
                    FullNodes.Add(InternBranch(new int4(FullNodes[FullNodes.Length - 1])));
                return FullNodes[level];
            }

            int Fill(int id, long x, long y, long minX, long minY, long maxX, long maxY)
            {
                LifeNode node = Nodes[id];
                long side = 1L << node.Level;
                if (x >= maxX || y >= maxY || minX >= x + side || minY >= y + side)
                    return id;
                if (minX <= x && minY <= y && maxX >= x + side && maxY >= y + side)
                    return Full(node.Level);
                if (node.Level == 4)
                {
                    int left = (int)math.max(0L, minX - x);
                    int right = (int)math.min(16L, maxX - x);
                    int bottom = (int)math.max(0L, minY - y);
                    int top = (int)math.min(16L, maxY - y);
                    uint mask = ((1u << (right - left)) - 1) << left;
                    LifeBits bits = node.Bits;
                    for (int row = bottom; row < top; row++)
                        bits.SetRow(row, (ushort)(bits.GetRow(row) | mask));
                    return InternLeaf(bits);
                }
                long half = side / 2;
                int4 children = node.Children;
                for (int i = 0; i < 4; i++)
                    children[i] = Fill(children[i], x + ((i & 1) != 0 ? half : 0), y + ((i & 2) != 0 ? half : 0), minX, minY, maxX, maxY);
                return InternBranch(children);
            }

            LifeBits ReadSquare(int source, long sx, long sy, long x, long y)
            {
                long localX = x - sx;
                long localY = y - sy;
                long tileX = localX & ~15L;
                long tileY = localY & ~15L;
                int shiftX = (int)(localX - tileX);
                int shiftY = (int)(localY - tileY);
                LifeBits sw = ReadLocalTile(source, tileX, tileY);
                LifeBits se = ReadLocalTile(source, tileX + 16, tileY);
                LifeBits nw = ReadLocalTile(source, tileX, tileY + 16);
                LifeBits ne = ReadLocalTile(source, tileX + 16, tileY + 16);
                LifeBits result = default;
                for (int row = 0; row < 16; row++)
                {
                    int inputY = row + shiftY;
                    uint low = inputY < 16 ? sw.GetRow(inputY) : nw.GetRow(inputY - 16);
                    uint high = inputY < 16 ? se.GetRow(inputY) : ne.GetRow(inputY - 16);
                    result.SetRow(row, (ushort)((low | (high << 16)) >> shiftX));
                }
                return result;
            }

            LifeBits ReadLocalTile(int root, long x, long y)
            {
                int leaf = FindLeaf(root, x, y);
                return leaf == -1 ? default : Nodes[leaf].Bits;
            }

            public int Erase(int id, long x, long y, long minX, long minY, long maxX, long maxY)
            {
                LifeNode node = Nodes[id];
                long side = 1L << node.Level;
                if (node.Population == 0 || x >= maxX || y >= maxY || minX >= x + side || minY >= y + side)
                    return id;
                if (minX <= x && minY <= y && maxX >= x + side && maxY >= y + side)
                    return Empty(node.Level);
                if (node.Level == 4)
                {
                    int left = (int)(minX > x ? minX - x : 0);
                    int right = (int)(maxX < x + 16 ? maxX - x : 16);
                    int bottom = (int)(minY > y ? minY - y : 0);
                    int top = (int)(maxY < y + 16 ? maxY - y : 16);
                    uint mask = ((1u << (right - left)) - 1) << left;
                    LifeBits bits = node.Bits;
                    for (int row = bottom; row < top; row++)
                        bits.SetRow(row, (ushort)(bits.GetRow(row) & ~mask));
                    return InternLeaf(bits);
                }
                long half = side / 2;
                int4 children = node.Children;
                for (int i = 0; i < 4; i++)
                    children[i] = Erase(children[i], x + ((i & 1) != 0 ? half : 0), y + ((i & 2) != 0 ? half : 0), minX, minY, maxX, maxY);
                return InternBranch(children);
            }

            public LifeShape Shrink(LifeShape shape)
            {
                if (Nodes[shape.Root].Population == 0)
                    return new LifeShape { Root = Empty(5), X = -16, Y = -16 };
                while (Nodes[shape.Root].Level > 5)
                {
                    LifeNode node = Nodes[shape.Root];
                    int4 c = node.Children;
                    int4 inner = new int4(Nodes[c.x].Children.w, Nodes[c.y].Children.z, Nodes[c.z].Children.y, Nodes[c.w].Children.x);
                    long centerPopulation = Nodes[inner.x].Population + Nodes[inner.y].Population + Nodes[inner.z].Population + Nodes[inner.w].Population;
                    if (centerPopulation == node.Population)
                    {
                        long quarter = 1L << (node.Level - 2);
                        shape.Root = InternBranch(inner);
                        shape.X += quarter;
                        shape.Y += quarter;
                        continue;
                    }
                    int occupied = -1;
                    for (int i = 0; i < 4; i++)
                        if (Nodes[c[i]].Population == node.Population)
                            occupied = i;
                    if (occupied == -1)
                        break;
                    long half = 1L << (node.Level - 1);
                    shape.Root = c[occupied];
                    shape.X += (occupied & 1) != 0 ? half : 0;
                    shape.Y += (occupied & 2) != 0 ? half : 0;
                }
                return shape;
            }

            int Center(int id)
            {
                LifeNode node = Nodes[id];
                if (node.Level == 5)
                    return InternLeaf(ReadSquare(id, 0, 0, 8, 8));
                int4 c = node.Children;
                return InternBranch(new int4(Nodes[c.x].Children.w, Nodes[c.y].Children.z, Nodes[c.z].Children.y, Nodes[c.w].Children.x));
            }

            public void BeginCenter(int node, int exponent)
            {
                Frames.Clear();
                State.PendingResult = -1;
                State.Advancing = 1;
                Frames.Add(new AdvanceFrame { Node = node, Exponent = exponent });
                Track(node);
            }

            void AdvanceOne()
            {
                int frameIndex = Frames.Length - 1;
                AdvanceFrame frame = Frames[frameIndex];
                if (frame.Stage == 0)
                {
                    var key = new AdvanceKey { Node = frame.Node, Exponent = frame.Exponent };
                    LifeNode node = Nodes[frame.Node];
                    if ((node.CachedExponents & (1UL << frame.Exponent)) != 0)
                    {
                        State.CacheHits++;
                        Finish(Cache[key]);
                        return;
                    }
                    State.CacheMisses++;
                    if (node.Population == 0)
                    {
                        int empty = Empty(node.Level - 1);
                        StoreResult(key, empty);
                        Finish(empty);
                        return;
                    }
                    if (node.Level == 5)
                    {
                        int result = InternLeaf(EvolveLeaf(node.Children, frame.Exponent));
                        StoreResult(key, result);
                        Finish(result);
                        return;
                    }

                    MakeOverlaps(ref frame);
                    // 最大步长分两段推进；较短步长的第一轮只提取中心。
                    if (frame.Exponent == node.Level - 2)
                    {
                        frame.Stage = 1;
                        frame.Index = 0;
                        Frames[frameIndex] = frame;
                        Frames.Add(new AdvanceFrame { Node = frame.Inputs[0], Exponent = frame.Exponent - 1 });
                    }
                    else
                    {
                        for (int i = 0; i < 9; i++)
                            frame.Results.Add(Center(frame.Inputs[i]));
                        MakeSecondPass(ref frame);
                        Frames[frameIndex] = frame;
                        Frames.Add(new AdvanceFrame { Node = frame.Inputs[0], Exponent = frame.Exponent });
                    }
                    return;
                }

                frame.Results.Add(State.PendingResult);
                State.PendingResult = -1;
                frame.Index++;
                if (frame.Stage == 1)
                {
                    if (frame.Index < 9)
                    {
                        Frames[frameIndex] = frame;
                        Frames.Add(new AdvanceFrame { Node = frame.Inputs[frame.Index], Exponent = frame.Exponent - 1 });
                    }
                    else
                    {
                        MakeSecondPass(ref frame);
                        Frames[frameIndex] = frame;
                        Frames.Add(new AdvanceFrame { Node = frame.Inputs[0], Exponent = frame.Exponent - 1 });
                    }
                    return;
                }

                if (frame.Index < 4)
                {
                    int exponent = frame.Exponent == Nodes[frame.Node].Level - 2 ? frame.Exponent - 1 : frame.Exponent;
                    Frames[frameIndex] = frame;
                    Frames.Add(new AdvanceFrame { Node = frame.Inputs[frame.Index], Exponent = exponent });
                    return;
                }
                int output = InternBranch(new int4(frame.Results[0], frame.Results[1], frame.Results[2], frame.Results[3]));
                StoreResult(new AdvanceKey { Node = frame.Node, Exponent = frame.Exponent }, output);
                Finish(output);
            }

            void Finish(int result)
            {
                State.PendingResult = result;
                Track(result);
                Frames.RemoveAt(Frames.Length - 1);
            }

            void StoreResult(AdvanceKey key, int result)
            {
                Track(key.Node);
                Track(result);
                if (Cache.TryAdd(key, result))
                    State.CacheCount++;
                else
                    Cache[key] = result;
                LifeNode node = Nodes[key.Node];
                node.CachedExponents |= 1UL << key.Exponent;
                Nodes[key.Node] = node;
            }

            void MakeOverlaps(ref AdvanceFrame frame)
            {
                int4 children = Nodes[frame.Node].Children;
                int4 sw = Nodes[children.x].Children;
                int4 se = Nodes[children.y].Children;
                int4 nw = Nodes[children.z].Children;
                int4 ne = Nodes[children.w].Children;
                frame.Inputs.Add(children.x);
                frame.Inputs.Add(InternBranch(new int4(sw.y, se.x, sw.w, se.z)));
                frame.Inputs.Add(children.y);
                frame.Inputs.Add(InternBranch(new int4(sw.z, sw.w, nw.x, nw.y)));
                frame.Inputs.Add(InternBranch(new int4(sw.w, se.z, nw.y, ne.x)));
                frame.Inputs.Add(InternBranch(new int4(se.z, se.w, ne.x, ne.y)));
                frame.Inputs.Add(children.z);
                frame.Inputs.Add(InternBranch(new int4(nw.y, ne.x, nw.w, ne.z)));
                frame.Inputs.Add(children.w);
            }

            void MakeSecondPass(ref AdvanceFrame frame)
            {
                FixedList64Bytes<int> first = frame.Results;
                frame.Inputs.Clear();
                frame.Inputs.Add(InternBranch(new int4(first[0], first[1], first[3], first[4])));
                frame.Inputs.Add(InternBranch(new int4(first[1], first[2], first[4], first[5])));
                frame.Inputs.Add(InternBranch(new int4(first[3], first[4], first[6], first[7])));
                frame.Inputs.Add(InternBranch(new int4(first[4], first[5], first[7], first[8])));
                frame.Results.Clear();
                frame.Stage = 2;
                frame.Index = 0;
            }

            LifeBits EvolveLeaf(int4 children, int exponent)
            {
                LifeBits sw = Nodes[children.x].Bits;
                LifeBits se = Nodes[children.y].Bits;
                LifeBits nw = Nodes[children.z].Bits;
                LifeBits ne = Nodes[children.w].Bits;
                var rows = new FixedList512Bytes<uint> { Length = 32 };
                var next = new FixedList512Bytes<uint> { Length = 32 };
                for (int y = 0; y < 16; y++)
                {
                    rows[y] = (uint)sw.GetRow(y) | ((uint)se.GetRow(y) << 16);
                    rows[y + 16] = (uint)nw.GetRow(y) | ((uint)ne.GetRow(y) << 16);
                }
                int steps = 1 << exponent;
                for (int step = 0; step < steps; step++)
                {
                    if ((step & 1) == 0)
                        EvolveRows(ref rows, ref next);
                    else
                        EvolveRows(ref next, ref rows);
                }
                LifeBits result = default;
                for (int y = 0; y < 16; y++)
                    result.SetRow(y, (ushort)(((steps & 1) == 0 ? rows[y + 8] : next[y + 8]) >> 8));
                return result;
            }

            static void EvolveRows(ref FixedList512Bytes<uint> input, ref FixedList512Bytes<uint> output)
            {
                for (int y = 0; y < 32; y++)
                {
                    uint below = y == 0 ? 0 : input[y - 1];
                    uint current = input[y];
                    uint above = y == 31 ? 0 : input[y + 1];
                    uint ones = 0;
                    uint twos = 0;
                    uint fours = 0;
                    uint eights = 0;
                    AddBits(ref ones, ref twos, ref fours, ref eights, below << 1);
                    AddBits(ref ones, ref twos, ref fours, ref eights, below);
                    AddBits(ref ones, ref twos, ref fours, ref eights, below >> 1);
                    AddBits(ref ones, ref twos, ref fours, ref eights, current << 1);
                    AddBits(ref ones, ref twos, ref fours, ref eights, current >> 1);
                    AddBits(ref ones, ref twos, ref fours, ref eights, above << 1);
                    AddBits(ref ones, ref twos, ref fours, ref eights, above);
                    AddBits(ref ones, ref twos, ref fours, ref eights, above >> 1);
                    uint twoOrThree = twos & ~(fours | eights);
                    output[y] = twoOrThree & (ones | current);
                }
            }

            static void AddBits(ref uint ones, ref uint twos, ref uint fours, ref uint eights, uint value)
            {
                uint carry = ones & value;
                ones ^= value;
                uint nextCarry = twos & carry;
                twos ^= carry;
                carry = fours & nextCarry;
                fours ^= nextCarry;
                eights ^= carry;
            }

            public void BeginCollection()
            {
                State.CollectionEpoch++;
                State.CollectionPhase = 1;
                State.SweepCursor = 0;
                State.SweepLimit = Nodes.Length;
                MarkStack.Clear();
                EpochMarkStack.Clear();
                RetiredNodes.Clear();
                if (State.Editing != 0)
                {
                    Mark(State.EditShape.Root);
                    Mark(State.EditDead.Root);
                }
                for (int level = 4; level < EmptyNodes.Length; level++)
                    Mark(EmptyNodes[level]);
            }

            public void ContinueCollection(int workBudget)
            {
                for (int work = 0; work < workBudget && State.CollectionPhase != 0; work++)
                {
                    if (EpochMarkStack.Length != 0)
                    {
                        MarkEpochOne();
                        continue;
                    }
                    if (Time.PrunePhase != 0)
                    {
                        PruneRegionOne();
                        continue;
                    }
                    if (MarkStack.Length != 0)
                    {
                        int id = MarkStack[MarkStack.Length - 1];
                        MarkStack.RemoveAt(MarkStack.Length - 1);
                        LifeNode node = Nodes[id];
                        if (node.Level > 4)
                        {
                            for (int child = 0; child < 4; child++)
                                Mark(node.Children[child]);
                            ulong exponents = node.CachedExponents;
                            while (exponents != 0)
                            {
                                int exponent = math.tzcnt(exponents);
                                exponents &= exponents - 1;
                                Mark(Cache[new AdvanceKey { Node = id, Exponent = exponent }]);
                            }
                        }
                        continue;
                    }
                    if (State.CollectionPhase == 1)
                    {
                        State.CollectionPhase = 2;
                        continue;
                    }
                    if (State.CollectionPhase == 2)
                    {
                        if (State.SweepCursor < State.SweepLimit)
                        {
                            int id = State.SweepCursor++;
                            LifeNode node = Nodes[id];
                            if (node.Level >= 4 && Marks[id] != State.CollectionEpoch)
                            {
                                if (node.Level == 4)
                                    Leaves.Remove(node.Bits);
                                else
                                {
                                    Branches.Remove(node.Children);
                                    RemoveCachedResults(id, ref node);
                                }
                                Nodes[id] = new LifeNode { Level = -1 };
                                RetiredNodes.Add(id);
                                State.NodeCount--;
                            }
                        }
                        else
                            State.CollectionPhase = 3;
                        continue;
                    }
                    // 整轮清扫结束后才复用 ID，避免旧驻留键引用被重用的子节点。
                    FreeNodes.AddRange(RetiredNodes.AsArray());
                    RetiredNodes.Clear();
                    State.CollectionPhase = 0;
                }
            }
        }
    }
}
