using System;
using Unity.Mathematics;

namespace GameOfLife
{
    public struct LifeBits : IEquatable<LifeBits>
    {
        public ulong A;
        public ulong B;
        public ulong C;
        public ulong D;

        public long Population => math.countbits(A) + math.countbits(B) + math.countbits(C) + math.countbits(D);

        public ushort GetRow(int y)
        {
            ulong word = y < 4 ? A : y < 8 ? B : y < 12 ? C : D;
            return (ushort)(word >> ((y & 3) * 16));
        }

        public void Set(int x, int y, bool alive)
        {
            uint mask = 1u << x;
            uint row = GetRow(y);
            SetRow(y, (ushort)(alive ? row | mask : row & ~mask));
        }

        internal void SetRow(int y, ushort row)
        {
            int shift = (y & 3) * 16;
            ulong mask = 0xffffUL << shift;
            ulong value = (ulong)row << shift;
            switch (y >> 2)
            {
                case 0: A = (A & ~mask) | value; break;
                case 1: B = (B & ~mask) | value; break;
                case 2: C = (C & ~mask) | value; break;
                case 3: D = (D & ~mask) | value; break;
            }
        }

        public bool Equals(LifeBits other) => A == other.A && B == other.B && C == other.C && D == other.D;

        public override bool Equals(object obj) => obj is LifeBits other && Equals(other);

        public override int GetHashCode()
        {
            uint first = math.hash(new uint4((uint)A, (uint)(A >> 32), (uint)B, (uint)(B >> 32)));
            uint second = math.hash(new uint4((uint)C, (uint)(C >> 32), (uint)D, (uint)(D >> 32)));
            return (int)math.hash(new uint2(first, second));
        }
    }
}
