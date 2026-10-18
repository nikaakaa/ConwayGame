using Unity.Entities;
using Unity.Mathematics;

namespace GameOfLife
{
    public struct LifeSettings : IComponentData
    {
        public float GenerationsPerSecond;
    }

    public struct LifeViewport : IComponentData
    {
        public int2 Origin;
        public int VisibleCells;
        public double FrameSeconds;

        public int2 PreloadOrigin => (Origin & ~(VisibleCells / 4 - 1)) - VisibleCells * 5 / 4;
        public int PreloadCells => VisibleCells * 7 / 2;
    }

    public struct LifeState : IComponentData
    {
        public bool IsRunning;
        public long Generation;
        public double Accumulator;
        public uint Revision;
    }

    public enum LifeCommandKind : byte
    {
        Play,
        Pause,
        Step,
        SetSpeed,
        Clear,
        CellStroke,
        PatternStroke,
        ClearArea
    }

    [InternalBufferCapacity(0)]
    public struct LifeCommand : IBufferElementData
    {
        public LifeCommandKind Kind;
        public float Speed;
        public int2 Position;
        public int2 End;
        public LifeShape Shape;
        public bool Alive;
        public bool SkipFirst;
    }
}
