using Unity.Entities;
using Unity.Mathematics;

namespace GameOfLife
{
    public struct LifeSettings : IComponentData
    {
        public float GenerationsPerSecond;
    }

    public struct LifeState : IComponentData
    {
        public bool IsRunning;
        public long Generation;
        public float Accumulator;
        public uint Revision;
    }

    [InternalBufferCapacity(0)]
    public struct AliveCell : IBufferElementData
    {
        public int2 Position;
    }

    public enum LifeCommandKind : byte
    {
        Play,
        Pause,
        Step,
        SetSpeed,
        Clear,
        SetCell
    }

    [InternalBufferCapacity(0)]
    public struct LifeCommand : IBufferElementData
    {
        public LifeCommandKind Kind;
        public float Speed;
        public int2 Position;
        public bool Alive;
    }
}
