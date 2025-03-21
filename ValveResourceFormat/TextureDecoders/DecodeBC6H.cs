// Copyright 2020 lewa_j [https://github.com/lewa-j]
// Reference: https://registry.khronos.org/DataFormat/specs/1.3/dataformat.1.3.html#bptc_bc6h
using System.Runtime.InteropServices;
using SkiaSharp;

namespace ValveResourceFormat.TextureDecoders
{
    internal class DecodeBC6H : CommonBPTC, ITextureDecoder
    {
        readonly int blockCountX;
        readonly int blockCountY;

        public DecodeBC6H(int width, int height)
        {
            blockCountX = (width + 3) / 4;
            blockCountY = (height + 3) / 4;
        }

        public void Decode(SKBitmap bitmap, Span<byte> input)
        {
            using var pixels = bitmap.PeekPixels();
            var rowBytes = bitmap.RowBytes;
            var bytesPerPixel = bitmap.BytesPerPixel;

            var data = pixels.GetPixelSpan<byte>();
            var dataHdr = MemoryMarshal.Cast<byte, float>(data);

            var endpoints = new ushort[4, 3];
            var endpointsUint = new uint[4, 3];
            var deltas = new short[3, 3];

            var blockOffset = 0;

            for (var j = 0; j < blockCountY; j++)
            {
                for (var i = 0; i < blockCountX; i++)
                {
                    DecompressBlock(j, i, input[blockOffset..], rowBytes, bytesPerPixel, data, dataHdr, endpoints, deltas);
                    blockOffset += 16;
                }
            }
        }

        internal static void DecompressBlock(int x, int y, ReadOnlySpan<byte> blockData, int rowBytes, int bytesPerPixel, Span<byte> data, Span<float> dataHdr, ushort[,] endpoints, short[,] deltas)
        {
            if (blockData.Length < 16)
            {
                return;
            }

            var block0 = MemoryMarshal.Read<ulong>(blockData);
            var block64 = MemoryMarshal.Read<ulong>(blockData[8..]);

            ulong Bit(int p)
            {
                return (byte)(p < 64 ? block0 >> p & 1 : block64 >> (p - 64) & 1);
            }

            var m = (byte)(block0 & 0x3);
            if (m >= 2)
            {
                m = (byte)(block0 & 0x1F);
            }

            Array.Clear(endpoints, 0, endpoints.Length);
            Array.Clear(deltas, 0, deltas.Length);

            var epb = 0;
            byte pb = 0;
            ulong ib = 0;

            if (m == 0)
            {
                epb = 10;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0x3FF);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0x3FF);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0x3FF);
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x1F, 5);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x1F, 5);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x1F, 5);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0x1F, 5);
                deltas[1, 1] = SignExtend((block0 >> 41 & 0xF) | (Bit(2) << 4), 5);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3) | (Bit(3) << 4), 5);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0x1F, 5);
                deltas[2, 1] = SignExtend((block0 >> 51 & 0xF) | (Bit(40) << 4), 5);
                deltas[2, 2] = SignExtend(Bit(50) | (Bit(60) << 1) | (Bit(70) << 2) | (Bit(76) << 3) | (Bit(4) << 4), 5);
            }
            else if (m == 1)
            {
                epb = 7;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0x7F);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0x7F);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0x7F);
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x3F, 6);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x3F, 6);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x3F, 6);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0x3F, 6);
                deltas[1, 1] = SignExtend((block0 >> 41 & 0xF) | (Bit(24) << 4) | (Bit(2) << 5), 6);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3) | (Bit(14) << 4) | (Bit(22) << 5), 6);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0x3F, 6);
                deltas[2, 1] = SignExtend((block0 >> 51 & 0xF) | ((block0 >> 3 & 0x3) << 4), 6);
                deltas[2, 2] = SignExtend((block0 >> 12 & 0x3) | (Bit(23) << 2) | (Bit(32) << 3) | (Bit(34) << 4) | (Bit(33) << 5), 6);
            }
            else if (m == 2)
            {
                epb = 11;
                endpoints[0, 0] = (ushort)((block0 >> 5 & 0x3FF) | (Bit(40) << 10));
                endpoints[0, 1] = (ushort)((block0 >> 15 & 0x3FF) | (Bit(49) << 10));
                endpoints[0, 2] = (ushort)((block0 >> 25 & 0x3FF) | (Bit(59) << 10));
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x1F, 5);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0xF, 4);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0xF, 4);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0x1F, 5);
                deltas[1, 1] = SignExtend(block0 >> 41 & 0xF, 4);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3), 4);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0x1F, 5);
                deltas[2, 1] = SignExtend(block0 >> 51 & 0xF, 4);
                deltas[2, 2] = SignExtend(Bit(50) | (Bit(60) << 1) | (Bit(70) << 2) | (Bit(76) << 3), 4);
            }
            else if (m == 6)
            {
                epb = 11;
                endpoints[0, 0] = (ushort)((block0 >> 5 & 0x3FF) | (Bit(39) << 10));
                endpoints[0, 1] = (ushort)((block0 >> 15 & 0x3FF) | (Bit(50) << 10));
                endpoints[0, 2] = (ushort)((block0 >> 25 & 0x3FF) | (Bit(59) << 10));
                deltas[0, 0] = SignExtend(block0 >> 35 & 0xF, 4);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x1F, 5);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0xF, 4);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0xF, 4);
                deltas[1, 1] = SignExtend((block0 >> 41 & 0xF) | (Bit(75) << 4), 5);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3), 4);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0xF, 4);
                deltas[2, 1] = SignExtend((block0 >> 51 & 0xF) | (Bit(40) << 4), 5);
                deltas[2, 2] = SignExtend(Bit(69) | (Bit(60) << 1) | (Bit(70) << 2) | (Bit(76) << 3), 4);
            }
            else if (m == 10)
            {
                epb = 11;
                endpoints[0, 0] = (ushort)((block0 >> 5 & 0x3FF) | (Bit(39) << 10));
                endpoints[0, 1] = (ushort)((block0 >> 15 & 0x3FF) | (Bit(49) << 10));
                endpoints[0, 2] = (ushort)((block0 >> 25 & 0x3FF) | (Bit(60) << 10));
                deltas[0, 0] = SignExtend(block0 >> 35 & 0xF, 4);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0xF, 4);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x1F, 5);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0xF, 4);
                deltas[1, 1] = SignExtend(block0 >> 41 & 0xF, 4);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3) | (Bit(40) << 4), 5);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0xF, 4);
                deltas[2, 1] = SignExtend(block0 >> 51 & 0xF, 4);
                deltas[2, 2] = SignExtend(Bit(50) | (Bit(69) << 1) | (Bit(70) << 2) | (Bit(76) << 3) | (Bit(75) << 3), 5);
            }
            else if (m == 14)
            {
                epb = 9;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0x1FF);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0x1FF);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0x1FF);
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x1F, 5);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x1F, 5);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x1F, 5);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0x1F, 5);
                deltas[1, 1] = SignExtend((block0 >> 41 & 0xF) | (Bit(24) << 4), 5);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3) | (Bit(14) << 4), 5);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0x1F, 5);
                deltas[2, 1] = SignExtend((block0 >> 51 & 0xF) | (Bit(40) << 4), 5);
                deltas[2, 2] = SignExtend(Bit(50) | (Bit(60) << 1) | (Bit(70) << 2) | (Bit(76) << 3) | (Bit(34) << 4), 5);
            }
            else if (m == 18)
            {
                epb = 8;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0xFF);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0xFF);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0xFF);
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x3F, 6);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x1F, 5);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x1F, 5);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0x3F, 6);
                deltas[1, 1] = SignExtend((block0 >> 41 & 0xF) | (Bit(24) << 4), 5);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3) | (Bit(14) << 4), 5);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0x3F, 6);
                deltas[2, 1] = SignExtend((block0 >> 51 & 0xF) | (Bit(13) << 4), 5);
                deltas[2, 2] = SignExtend(Bit(50) | (Bit(60) << 1) | (Bit(23) << 2) | (Bit(33) << 3) | (Bit(34) << 4), 5);
            }
            else if (m == 22)
            {
                epb = 8;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0xFF);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0xFF);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0xFF);
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x1F, 5);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x3F, 6);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x1F, 5);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0x1F, 5);
                deltas[1, 1] = SignExtend((block0 >> 41 & 0xF) | (Bit(24) << 4) | (Bit(23) << 5), 6);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3) | (Bit(14) << 4), 5);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0x1F, 5);
                deltas[2, 1] = SignExtend((block0 >> 51 & 0xF) | (Bit(40) << 4) | (Bit(33) << 5), 6);
                deltas[2, 2] = SignExtend(Bit(13) | (Bit(60) << 1) | (Bit(70) << 2) | (Bit(76) << 3) | (Bit(34) << 4), 5);
            }
            else if (m == 26)
            {
                epb = 8;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0xFF);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0xFF);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0xFF);
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x1F, 5);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x1F, 5);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x3F, 6);
                deltas[1, 0] = SignExtend(block64 >> 1 & 0x1F, 5);
                deltas[1, 1] = SignExtend((block0 >> 41 & 0xF) | (Bit(24) << 4), 5);
                deltas[1, 2] = SignExtend((block0 >> 61 & 0x7) | (Bit(64) << 3) | (Bit(14) << 4) | (Bit(23) << 5), 6);
                deltas[2, 0] = SignExtend(block64 >> 7 & 0x1F, 5);
                deltas[2, 1] = SignExtend((block0 >> 51 & 0xF) | (Bit(40) << 4), 5);
                deltas[2, 2] = SignExtend(Bit(50) | (Bit(13) << 1) | (Bit(70) << 2) | (Bit(76) << 3) | (Bit(34) << 4) | (Bit(33) << 5), 6);
            }
            else if (m == 30)
            {
                epb = 6;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0x3F);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0x3F);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0x3F);
                endpoints[1, 0] = (ushort)(block0 >> 35 & 0x3F);
                endpoints[1, 1] = (ushort)(block0 >> 45 & 0x3F);
                endpoints[1, 2] = (ushort)(block0 >> 55 & 0x3F);
                endpoints[2, 0] = (ushort)(block64 >> 1 & 0x3F);
                endpoints[2, 1] = (ushort)((block0 >> 41 & 0xF) | (Bit(24) << 4) | (Bit(21) << 5));
                endpoints[2, 2] = (ushort)((block0 >> 61 & 0x3) | (Bit(64) << 3) | (Bit(14) << 4) | (Bit(22) << 5));
                endpoints[3, 0] = (ushort)(block64 >> 7 & 0x3F);
                endpoints[3, 1] = (ushort)((block0 >> 51 & 0xF) | (Bit(11) << 4) | (Bit(31) << 5));
                endpoints[3, 2] = (ushort)((block0 >> 12 & 0x3) | (Bit(23) << 2) | (Bit(32) << 3) | (Bit(34) << 4) | (Bit(33) << 5));
            }
            else if (m == 3)
            {
                epb = 10;
                endpoints[0, 0] = (ushort)(block0 >> 5 & 0x3FF);
                endpoints[0, 1] = (ushort)(block0 >> 15 & 0x3FF);
                endpoints[0, 2] = (ushort)(block0 >> 25 & 0x3FF);
                endpoints[1, 0] = (ushort)(block0 >> 35 & 0x3FF);
                endpoints[1, 1] = (ushort)(block0 >> 45 & 0x3FF);
                endpoints[1, 2] = (ushort)((block0 >> 55 & 0x1FF) | ((block64 & 0x1) << 9));
            }
            else if (m == 7)
            {
                epb = 11;
                endpoints[0, 0] = (ushort)((block0 >> 5 & 0x3FF) | (Bit(44) << 10));
                endpoints[0, 1] = (ushort)((block0 >> 15 & 0x3FF) | (Bit(54) << 10));
                endpoints[0, 2] = (ushort)((block0 >> 25 & 0x3FF) | (Bit(64) << 10));
                deltas[0, 0] = SignExtend(block0 >> 35 & 0x1FF, 9);
                deltas[0, 1] = SignExtend(block0 >> 45 & 0x1FF, 9);
                deltas[0, 2] = SignExtend(block0 >> 55 & 0x1FF, 9);
            }
            else if (m == 11)
            {
                epb = 12;
                endpoints[0, 0] = (ushort)((block0 >> 5 & 0x3FF) | (Bit(44) << 10) | (Bit(43) << 11));
                endpoints[0, 1] = (ushort)((block0 >> 15 & 0x3FF) | (Bit(54) << 10) | (Bit(53) << 11));
                endpoints[0, 2] = (ushort)((block0 >> 25 & 0x3FF) | (Bit(64) << 10) | (Bit(63) << 11));
                deltas[0, 0] = SignExtend((block0 >> 35) & 0xFF, 8);
                deltas[0, 1] = SignExtend((block0 >> 45) & 0xFF, 8);
                deltas[0, 2] = SignExtend((block0 >> 55) & 0xFF, 8);
            }
            else if (m == 15)
            {
                epb = 16;
                endpoints[0, 0] = (ushort)((block0 >> 5 & 0x3FF) | (Bit(44) << 10) | (Bit(43) << 11) | (Bit(42) << 12) | (Bit(41) << 13) | (Bit(40) << 14) | (Bit(39) << 15));
                endpoints[0, 1] = (ushort)((block0 >> 15 & 0x3FF) | (Bit(54) << 10) | (Bit(53) << 11) | (Bit(52) << 12) | (Bit(51) << 13) | (Bit(50) << 14) | (Bit(49) << 15));
                endpoints[0, 2] = (ushort)((block0 >> 25 & 0x3FF) | (Bit(64) << 10) | (Bit(63) << 11) | (Bit(62) << 12) | (Bit(61) << 13) | (Bit(60) << 14) | (Bit(59) << 15));
                deltas[0, 0] = SignExtend((block0 >> 35) & 0xFF, 4);
                deltas[0, 1] = SignExtend((block0 >> 45) & 0xFF, 4);
                deltas[0, 2] = SignExtend((block0 >> 55) & 0xFF, 4);
            }

            var epm = (ushort)((1U << epb) - 1);

            if ((m & 3) == 0)
            {
                pb = (byte)(block64 >> 13 & 0x1F);
                ib = block64 >> 18;
            }
            else
            {
                ib = block64 >> 1;
            }

            static ushort Unquantize(ushort e, int epb, ushort epm)
            {
                if (epb >= 15)
                {
                    return e;
                }
                else if (e == 0)
                {
                    return 0;
                }
                else if (e == epm)
                {
                    return 0xFFFF;
                }

                return (ushort)(((e << 15) + 0x4000) >> (epb - 1));
            }

            if (m != 3 && m != 30)
            {
                for (var d = 0; d < 3; d++)
                {
                    for (var e = 0; e < 3; e++)
                    {
                        endpoints[d + 1, e] = (ushort)((endpoints[0, e] + deltas[d, e]) & epm);
                    }
                }
            }

            for (var s = 0; s < 4; s++)
            {
                for (var e = 0; e < 3; e++)
                {
                    endpoints[s, e] = Unquantize(endpoints[s, e], epb, epm);
                }
            }

            // Every BC6H block (16 bytes) corresponds to 16 output pixels

            for (var by = 0; by < 4; by++)
            {
                for (var bx = 0; bx < 4; bx++)
                {
                    var pixelDataOffset = (((x * 4) + by) * rowBytes) + (((y * 4) + bx) * bytesPerPixel);
                    var io = (by * 4) + bx;

                    var isAnchor = 0;
                    byte cweight = 0;
                    byte subset = 0;

                    if ((m & 3) == 3)
                    {
                        isAnchor = (io == 0) ? 1 : 0;
                        cweight = BPTCWeights4[ib & 0xFu >> isAnchor];
                        ib >>= 4 - isAnchor;
                    }
                    else
                    {
                        subset = (byte)(BPTCPartitionTable2[pb, io] * 2);
                        isAnchor = (io == 0 || io == BPTCAnchorIndices2[pb]) ? 1 : 0;
                        cweight = BPTCWeights3[ib & 0x7u >> isAnchor];
                        ib >>= 3 - isAnchor;
                    }

                    // Store LDR
                    if (bytesPerPixel == 4)
                    {
                        for (var e = 0; e < 3; e++)
                        {
                            var factor = BPTCInterpolateFactor(cweight, endpoints[subset, e], endpoints[subset + 1, e]);
                            //gamma correction and mul 4
                            factor = (ushort)Math.Min(0xFFFF, MathF.Pow(factor / (float)((1U << 16) - 1), 2.2f) * ((1U << 16) - 1) * 4);
                            data[pixelDataOffset + 2 - e] = (byte)(factor >> 8);
                        }

                        data[pixelDataOffset + 3] = byte.MaxValue;
                        continue;
                    }

                    // Store HDR
                    var pixelOffsetFloat = pixelDataOffset / sizeof(float);

                    for (var e = 0; e < 3; e++)
                    {
                        var factor = BPTCInterpolateFactor((uint)cweight, endpoints[subset, e], endpoints[subset + 1, e]);

                        dataHdr[pixelOffsetFloat + e] = factor;
                    }

                    dataHdr[pixelOffsetFloat + 3] = 1f;
                }
            }
        }

        private static short SignExtend(ulong v, int bits)
        {
            if (((v >> (bits - 1)) & 1) == 1)
            {
                v |= (uint)(-1L << bits);
            }

            return (short)v;
        }
    }
}
