// Copyright 2020 lewa_j [https://github.com/lewa-j]
// Reference: https://registry.khronos.org/DataFormat/specs/1.3/dataformat.1.3.html#bptc_bc6h
using System.Runtime.InteropServices;
using SkiaSharp;

using UShortColor = (ushort Red, ushort Green, ushort Blue);
using IntColor = (int Red, int Green, int Blue);

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
            var deltas = new ushort[3, 3];

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

        internal static void DecompressBlock(int x, int y, ReadOnlySpan<byte> @in, int rowBytes, int bytesPerPixel, Span<byte> data, Span<float> dataHdr, ushort[,] endpoints, ushort[,] deltas)
        {
            if (@in.Length < 16)
            {
                return;
            }

            var header = MemoryMarshal.Read<UInt128>(@in);

            static int GetValue(UInt128 header, int offset, int length)
            {
                return (int)((header >> offset) & ((1u << length) - 1));
            }

            static int Bit(UInt128 header, int offset)
            {
                return (int)((header >> offset) & 1);
            }

            ushort decvalue;

            if ((@in[0] & 0x2) == 0)
            {
                decvalue = (ushort)(header & 0x01); // 0, 1
            }
            else
            {
                decvalue = (byte)(header & 0x1F); // 2, 3, 6, 7, 10, 11, 14, 15, 18, 22, 26, 30
            }

            Array.Clear(endpoints, 0, endpoints.Length);
            Array.Clear(deltas, 0, deltas.Length);

            var region = 0; // one or two
            var mode = 0;

            ushort wBits = 0;

            UShortColor tBits = (0, 0, 0);
            (IntColor W, IntColor X, IntColor Y, IntColor Z) endpoints2;

            byte pb = 0;
            ulong ib = 0;

            if (decvalue == 0)
            {
                mode = 1;
                wBits = 10;
                tBits = (5, 5, 5);

                endpoints2.W.Red = GetValue(header, 5, wBits);
                endpoints2.W.Green = GetValue(header, 15, wBits);
                endpoints2.W.Blue = GetValue(header, 25, wBits);

                endpoints2.X.Red = GetValue(header, 35, tBits.Red);
                endpoints2.X.Green = GetValue(header, 45, tBits.Green);
                endpoints2.X.Blue = GetValue(header, 55, tBits.Blue);

                endpoints2.Y.Red = GetValue(header, 65, 5);
                endpoints2.Y.Green = GetValue(header, 41, 4) | (Bit(header, 2) << 4);

                deltas[0, 0] = (ushort)GetValue(header, 35, 5);
                deltas[0, 1] = (ushort)GetValue(header, 45, 5);
                deltas[0, 2] = (ushort)GetValue(header, 55, 5);
                deltas[1, 0] = (ushort)GetValue(header, 65, 5);
                deltas[1, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 2) << 4));
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 3) << 4));
                deltas[2, 0] = (ushort)GetValue(header, 71, 5);
                deltas[2, 1] = (ushort)(GetValue(header, 51, 4) | (Bit(header, 40) << 4));
                deltas[2, 2] = (ushort)(Bit(header, 50) | (Bit(header, 60) << 1) | (Bit(header, 70) << 2) | (Bit(header, 76) << 3) | (Bit(header, 4) << 4));
            }
            else if (decvalue == 1)
            {
                mode = 2;
                wBits = 7;
                tBits = (6, 6, 6);
                endpoints[0, 0] = (ushort)GetValue(header, 5, 7);
                endpoints[0, 1] = (ushort)GetValue(header, 15, 7);
                endpoints[0, 2] = (ushort)GetValue(header, 25, 7);
                deltas[0, 0] = (ushort)GetValue(header, 35, 6);
                deltas[0, 1] = (ushort)GetValue(header, 45, 6);
                deltas[0, 2] = (ushort)GetValue(header, 55, 6);
                deltas[1, 0] = (ushort)GetValue(header, 65, 6);
                deltas[1, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 24) << 4) | (Bit(header, 2) << 5));
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 14) << 4) | (Bit(header, 22) << 5));
                deltas[2, 0] = (ushort)GetValue(header, 71, 6);
                deltas[2, 1] = (ushort)(GetValue(header, 51, 4) | ((GetValue(header, 3, 2)) << 4));
                deltas[2, 2] = (ushort)((GetValue(header, 12, 2)) | (Bit(header, 23) << 2) | (Bit(header, 32) << 3) | (Bit(header, 34) << 4) | (Bit(header, 33) << 5));
            }
            else if (decvalue == 2)
            {
                mode = 3;
                wBits = 11;
                tBits = (5, 4, 4);
                endpoints[0, 0] = (ushort)(GetValue(header, 5, 10) | (Bit(header, 40) << 10));
                endpoints[0, 1] = (ushort)(GetValue(header, 15, 10) | (Bit(header, 49) << 10));
                endpoints[0, 2] = (ushort)(GetValue(header, 25, 10) | (Bit(header, 59) << 10));
                deltas[0, 0] = (ushort)GetValue(header, 35, 5);
                deltas[0, 1] = (ushort)GetValue(header, 45, 4);
                deltas[0, 2] = (ushort)GetValue(header, 55, 4);
                deltas[1, 0] = (ushort)GetValue(header, 65, 5);
                deltas[1, 1] = (ushort)GetValue(header, 41, 4);
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3));
                deltas[2, 0] = (ushort)GetValue(header, 71, 5);
                deltas[2, 1] = (ushort)GetValue(header, 51, 4);
                deltas[2, 2] = (ushort)(Bit(header, 50) | (Bit(header, 60) << 1) | (Bit(header, 70) << 2) | (Bit(header, 76) << 3));
            }
            else if (decvalue == 6)
            {
                mode = 3;
                wBits = 11;
                tBits = (4, 5, 4);
                endpoints[0, 0] = (ushort)(GetValue(header, 5, 10) | (Bit(header, 39) << 10));
                endpoints[0, 1] = (ushort)(GetValue(header, 15, 10) | (Bit(header, 50) << 10));
                endpoints[0, 2] = (ushort)(GetValue(header, 25, 10) | (Bit(header, 59) << 10));
                deltas[0, 0] = (ushort)GetValue(header, 35, 4);
                deltas[0, 1] = (ushort)GetValue(header, 45, 5);
                deltas[0, 2] = (ushort)GetValue(header, 55, 4);
                deltas[1, 0] = (ushort)GetValue(header, 65, 4);
                deltas[1, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 75) << 4));
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3));
                deltas[2, 0] = (ushort)GetValue(header, 71, 4);
                deltas[2, 1] = (ushort)(GetValue(header, 51, 4) | (Bit(header, 40) << 4));
                deltas[2, 2] = (ushort)(Bit(header, 69) | (Bit(header, 60) << 1) | (Bit(header, 70) << 2) | (Bit(header, 76) << 3));
            }
            else if (decvalue == 10)
            {
                mode = 5;
                wBits = 11;
                tBits = (4, 4, 5);
                endpoints[0, 0] = (ushort)(GetValue(header, 5, 10) | (Bit(header, 39) << 10));
                endpoints[0, 1] = (ushort)(GetValue(header, 15, 10) | (Bit(header, 49) << 10));
                endpoints[0, 2] = (ushort)(GetValue(header, 25, 10) | (Bit(header, 60) << 10));
                deltas[0, 0] = (ushort)GetValue(header, 35, 4);
                deltas[0, 1] = (ushort)GetValue(header, 45, 4);
                deltas[0, 2] = (ushort)GetValue(header, 55, 5);
                deltas[1, 0] = (ushort)GetValue(header, 65, 4);
                deltas[1, 1] = (ushort)GetValue(header, 41, 4);
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 40) << 4));
                deltas[2, 0] = (ushort)GetValue(header, 71, 4);
                deltas[2, 1] = (ushort)GetValue(header, 51, 4);
                deltas[2, 2] = (ushort)(Bit(header, 50) | (Bit(header, 69) << 1) | (Bit(header, 70) << 2) | (Bit(header, 76) << 3) | (Bit(header, 75) << 3));
            }
            else if (decvalue == 14)
            {
                mode = 6;
                wBits = 9;
                tBits = (5, 5, 5);
                endpoints[0, 0] = (ushort)GetValue(header, 5, 9);
                endpoints[0, 1] = (ushort)GetValue(header, 15, 9);
                endpoints[0, 2] = (ushort)GetValue(header, 25, 9);
                deltas[0, 0] = (ushort)GetValue(header, 35, 5);
                deltas[0, 1] = (ushort)GetValue(header, 45, 5);
                deltas[0, 2] = (ushort)GetValue(header, 55, 5);
                deltas[1, 0] = (ushort)GetValue(header, 65, 5);
                deltas[1, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 24) << 4));
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 14) << 4));
                deltas[2, 0] = (ushort)GetValue(header, 71, 5);
                deltas[2, 1] = (ushort)(GetValue(header, 51, 4) | (Bit(header, 40) << 4));
                deltas[2, 2] = (ushort)(Bit(header, 50) | (Bit(header, 60) << 1) | (Bit(header, 70) << 2) | (Bit(header, 76) << 3) | (Bit(header, 34) << 4));
            }
            else if (decvalue == 18)
            {
                mode = 7;
                wBits = 8;
                tBits = (6, 5, 5);
                endpoints[0, 0] = (ushort)GetValue(header, 5, 8);
                endpoints[0, 1] = (ushort)GetValue(header, 15, 8);
                endpoints[0, 2] = (ushort)GetValue(header, 25, 8);
                deltas[0, 0] = (ushort)GetValue(header, 35, 6);
                deltas[0, 1] = (ushort)GetValue(header, 45, 5);
                deltas[0, 2] = (ushort)GetValue(header, 55, 5);
                deltas[1, 0] = (ushort)GetValue(header, 65, 6);
                deltas[1, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 24) << 4));
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 14) << 4));
                deltas[2, 0] = (ushort)GetValue(header, 71, 6);
                deltas[2, 1] = (ushort)(GetValue(header, 51, 4) | (Bit(header, 13) << 4));
                deltas[2, 2] = (ushort)(Bit(header, 50) | (Bit(header, 60) << 1) | (Bit(header, 23) << 2) | (Bit(header, 33) << 3) | (Bit(header, 34) << 4));
            }
            else if (decvalue == 22)
            {
                mode = 8;
                wBits = 8;
                tBits = (5, 6, 5);
                endpoints[0, 0] = (ushort)GetValue(header, 5, 8);
                endpoints[0, 1] = (ushort)GetValue(header, 15, 8);
                endpoints[0, 2] = (ushort)GetValue(header, 25, 8);
                deltas[0, 0] = (ushort)GetValue(header, 35, 5);
                deltas[0, 1] = (ushort)GetValue(header, 45, 6);
                deltas[0, 2] = (ushort)GetValue(header, 55, 5);
                deltas[1, 0] = (ushort)GetValue(header, 65, 5);
                deltas[1, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 24) << 4) | (Bit(header, 23) << 5));
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 14) << 4));
                deltas[2, 0] = (ushort)GetValue(header, 71, 5);
                deltas[2, 1] = (ushort)(GetValue(header, 51, 4) | (Bit(header, 40) << 4) | (Bit(header, 33) << 5));
                deltas[2, 2] = (ushort)(Bit(header, 13) | (Bit(header, 60) << 1) | (Bit(header, 70) << 2) | (Bit(header, 76) << 3) | (Bit(header, 34) << 4));
            }
            else if (decvalue == 26)
            {
                mode = 9;
                wBits = 8;
                tBits = (5, 5, 6);
                endpoints[0, 0] = (ushort)GetValue(header, 5, 8);
                endpoints[0, 1] = (ushort)GetValue(header, 15, 8);
                endpoints[0, 2] = (ushort)GetValue(header, 25, 8);
                deltas[0, 0] = (ushort)GetValue(header, 35, 5);
                deltas[0, 1] = (ushort)GetValue(header, 45, 5);
                deltas[0, 2] = (ushort)GetValue(header, 55, 6);
                deltas[1, 0] = (ushort)GetValue(header, 65, 5);
                deltas[1, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 24) << 4));
                deltas[1, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 14) << 4) | (Bit(header, 23) << 5));
                deltas[2, 0] = (ushort)GetValue(header, 71, 5);
                deltas[2, 1] = (ushort)(GetValue(header, 51, 4) | (Bit(header, 40) << 4));
                deltas[2, 2] = (ushort)(Bit(header, 50) | (Bit(header, 13) << 1) | (Bit(header, 70) << 2) | (Bit(header, 76) << 3) | (Bit(header, 34) << 4) | (Bit(header, 33) << 5));
            }
            else if (decvalue == 30)
            {
                mode = 10;
                wBits = 6;
                tBits = (6, 6, 6);
                endpoints[0, 0] = (ushort)GetValue(header, 5, 6);
                endpoints[0, 1] = (ushort)GetValue(header, 15, 6);
                endpoints[0, 2] = (ushort)GetValue(header, 25, 6);
                endpoints[1, 0] = (ushort)GetValue(header, 35, 6);
                endpoints[1, 1] = (ushort)GetValue(header, 45, 6);
                endpoints[1, 2] = (ushort)GetValue(header, 55, 6);
                endpoints[2, 0] = (ushort)GetValue(header, 65, 6);
                endpoints[2, 1] = (ushort)(GetValue(header, 41, 4) | (Bit(header, 24) << 4) | (Bit(header, 21) << 5));
                endpoints[2, 2] = (ushort)(GetValue(header, 61, 3) | (Bit(header, 64) << 3) | (Bit(header, 14) << 4) | (Bit(header, 22) << 5));
                endpoints[3, 0] = (ushort)GetValue(header, 71, 6);
                endpoints[3, 1] = (ushort)(GetValue(header, 51, 4) | (Bit(header, 11) << 4) | (Bit(header, 31) << 5));
                endpoints[3, 2] = (ushort)(GetValue(header, 12, 2) | (Bit(header, 23) << 2) | (Bit(header, 32) << 3) | (Bit(header, 34) << 4) | (Bit(header, 33) << 5));
            }
            else if (decvalue == 3)
            {
                mode = 11;
                wBits = 10;
                tBits = (10, 10, 10);
                endpoints[0, 0] = (ushort)GetValue(header, 5, 10);
                endpoints[0, 1] = (ushort)GetValue(header, 15, 10);
                endpoints[0, 2] = (ushort)GetValue(header, 25, 10);
                endpoints[1, 0] = (ushort)GetValue(header, 35, 10);
                endpoints[1, 1] = (ushort)GetValue(header, 45, 10);
                endpoints[1, 2] = (ushort)(GetValue(header, 55, 9) | (Bit(header, 64) << 9));
            }
            else if (decvalue == 7)
            {
                mode = 12;
                wBits = 11;
                tBits = (9, 9, 9);
                endpoints[0, 0] = (ushort)(GetValue(header, 5, 10) | (Bit(header, 44) << 10));
                endpoints[0, 1] = (ushort)(GetValue(header, 15, 10) | (Bit(header, 54) << 10));
                endpoints[0, 2] = (ushort)(GetValue(header, 25, 10) | (Bit(header, 64) << 10));
                deltas[0, 0] = (ushort)GetValue(header, 35, 9);
                deltas[0, 1] = (ushort)GetValue(header, 45, 9);
                deltas[0, 2] = (ushort)GetValue(header, 55, 9);
            }
            else if (decvalue == 11)
            {
                mode = 13;
                wBits = 12;
                tBits = (8, 8, 8);
                endpoints[0, 0] = (ushort)(GetValue(header, 5, 10) | (Bit(header, 44) << 10) | (Bit(header, 43) << 11));
                endpoints[0, 1] = (ushort)(GetValue(header, 15, 10) | (Bit(header, 54) << 10) | (Bit(header, 53) << 11));
                endpoints[0, 2] = (ushort)(GetValue(header, 25, 10) | (Bit(header, 64) << 10) | (Bit(header, 63) << 11));
                deltas[0, 0] = (ushort)GetValue(header, 35, 8);
                deltas[0, 1] = (ushort)GetValue(header, 45, 8);
                deltas[0, 2] = (ushort)GetValue(header, 55, 8);
            }
            else if (decvalue == 15)
            {
                mode = 14;
                wBits = 16;
                tBits = (4, 4, 4);
                endpoints[0, 0] = (ushort)(GetValue(header, 5, 10) | (Bit(header, 44) << 10) | (Bit(header, 43) << 11) | (Bit(header, 42) << 12) | (Bit(header, 41) << 13) | (Bit(header, 40) << 14) | (Bit(header, 39) << 15));
                endpoints[0, 1] = (ushort)(GetValue(header, 15, 10) | (Bit(header, 54) << 10) | (Bit(header, 53) << 11) | (Bit(header, 52) << 12) | (Bit(header, 51) << 13) | (Bit(header, 50) << 14) | (Bit(header, 49) << 15));
                endpoints[0, 2] = (ushort)(GetValue(header, 25, 10) | (Bit(header, 64) << 10) | (Bit(header, 63) << 11) | (Bit(header, 62) << 12) | (Bit(header, 61) << 13) | (Bit(header, 60) << 14) | (Bit(header, 59) << 15));
                deltas[0, 0] = (ushort)GetValue(header, 35, 8);
                deltas[0, 1] = (ushort)GetValue(header, 45, 8);
                deltas[0, 2] = (ushort)GetValue(header, 55, 8);
            }

            var epm = (ushort)((1U << wBits) - 1);

            if (mode > 10)
            {
                pb = (byte)GetValue(header, 77, 5);
                ib = (ulong)GetValue(header, 82, 46);
            }
            else
            {
                ib = (ulong)GetValue(header, 65, 63);
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

            if (decvalue != 3 && decvalue != 30)
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
                    endpoints[s, e] = Unquantize(endpoints[s, e], wBits, epm);
                }
            }

            // Every BC6H block (16 bytes) corresponds to 16 output pixels

            for (var by = 0; by < 4; by++)
            {
                for (var bx = 0; bx < 4; bx++)
                {
                    var pixelDataOffset = (x * 4 + by) * rowBytes + (y * 4 + bx) * bytesPerPixel;
                    var io = by * 4 + bx;

                    var isAnchor = 0;
                    byte cweight = 0;
                    byte subset = 0;

                    if ((decvalue & 3) == 3)
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
