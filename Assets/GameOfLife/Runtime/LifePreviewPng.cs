using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Unity.Mathematics;

namespace GameOfLife
{
    /// <summary>
    /// 把一组活细胞缩成至多 maxPixels 见方的 PNG：白色为有活细胞的区域，其余透明。
    /// 只用纯 C#，不调用 Unity API，保存世界时在后台线程生成；与 Tools/MapPreviews 的地图预览同一格式。
    /// </summary>
    public static class LifePreviewPng
    {
        static readonly uint[] CrcTable = BuildCrcTable();

        public static void Write(string path, IReadOnlyList<int2> cells, int maxPixels)
        {
            int2 minimum = cells[0];
            int2 maximum = minimum;
            for (int i = 1; i < cells.Count; i++)
            {
                minimum = math.min(minimum, cells[i]);
                maximum = math.max(maximum, cells[i]);
            }
            long width = (long)maximum.x - minimum.x + 1;
            long height = (long)maximum.y - minimum.y + 1;
            long scale = Math.Max(1, (Math.Max(width, height) + maxPixels - 1) / maxPixels);
            int columns = (int)((width + scale - 1) / scale);
            int rows = (int)((height + scale - 1) / scale);

            // 每行开头一个过滤字节（0 = 不过滤），随后每像素 RGBA；第一行是世界的最上方（最大 y）。
            int stride = columns * 4 + 1;
            var raw = new byte[stride * rows];
            for (int i = 0; i < cells.Count; i++)
            {
                int column = (int)(((long)cells[i].x - minimum.x) / scale);
                int row = (int)(((long)maximum.y - cells[i].y) / scale);
                int offset = row * stride + 1 + column * 4;
                raw[offset] = raw[offset + 1] = raw[offset + 2] = raw[offset + 3] = 255;
            }

            using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
            file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var header = new byte[13];
            WriteBigEndian(header, 0, (uint)columns);
            WriteBigEndian(header, 4, (uint)rows);
            header[8] = 8;
            header[9] = 6;
            WriteChunk(file, "IHDR", header);
            WriteChunk(file, "IDAT", Zlib(raw));
            WriteChunk(file, "IEND", Array.Empty<byte>());
        }

        // zlib 封装：两字节头、原始 deflate 数据、Adler-32 校验。
        static byte[] Zlib(byte[] data)
        {
            using var output = new MemoryStream();
            output.WriteByte(0x78);
            output.WriteByte(0x9C);
            using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, true))
                deflate.Write(data, 0, data.Length);
            uint a = 1;
            uint b = 0;
            foreach (byte value in data)
            {
                a = (a + value) % 65521;
                b = (b + a) % 65521;
            }
            var checksum = new byte[4];
            WriteBigEndian(checksum, 0, b << 16 | a);
            output.Write(checksum, 0, 4);
            return output.ToArray();
        }

        static void WriteChunk(Stream stream, string type, byte[] body)
        {
            var bytes = new byte[8 + body.Length + 4];
            WriteBigEndian(bytes, 0, (uint)body.Length);
            for (int i = 0; i < 4; i++)
                bytes[4 + i] = (byte)type[i];
            Buffer.BlockCopy(body, 0, bytes, 8, body.Length);
            uint crc = 0xFFFFFFFFu;
            for (int i = 4; i < 8 + body.Length; i++)
                crc = CrcTable[(crc ^ bytes[i]) & 0xFF] ^ (crc >> 8);
            WriteBigEndian(bytes, 8 + body.Length, crc ^ 0xFFFFFFFFu);
            stream.Write(bytes, 0, bytes.Length);
        }

        static void WriteBigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }
    }
}
