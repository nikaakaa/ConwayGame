using System.Runtime.InteropServices;
using Unity.Mathematics;

namespace GameOfLife
{
    public struct LifeTile
    {
        public int2 Position;
        public LifeBits Bits;
    }

    public struct LifeShape
    {
        public int Root;
        public long X;
        public long Y;
    }

    [StructLayout(LayoutKind.Explicit, Size = 48)]
    public struct LifeNode
    {
        [FieldOffset(0)] public int Level;
        [FieldOffset(8)] public long Population;
        [FieldOffset(16)] public int4 Children;
        [FieldOffset(16)] public LifeBits Bits;
    }
}
