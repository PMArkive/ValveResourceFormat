// Copyright 2020 lewa_j [https://github.com/lewa-j]
// Reference: https://registry.khronos.org/DataFormat/specs/1.3/dataformat.1.3.html#bptc_bc6h
using System.Runtime.InteropServices;
using SkiaSharp;

using IntColor = (int Red, int Green, int Blue);
using System.Diagnostics;
using System.Runtime.CompilerServices;

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

            var blockOffset = 0;

            for (var j = 0; j < blockCountY; j++)
            {
                for (var i = 0; i < blockCountX; i++)
                {
                    DecompressBlock(j, i, input[blockOffset..], rowBytes, bytesPerPixel, data, dataHdr);
                    blockOffset += 16;
                }
            }
        }

        internal static void DecompressBlock(int x, int y, ReadOnlySpan<byte> @in, int rowBytes, int bytesPerPixel, Span<byte> data, Span<float> dataHdr)
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

            static int Unquantitize(int quantitized, int precision)
            {
                if (precision >= 15)
                {
                    return quantitized;
                }

                if (quantitized == 0)
                {
                    return 0;
                }

                var precisionMax = (1 << precision) - 1;

                if (quantitized == precisionMax)
                {
                    return ushort.MaxValue;
                }

                var ushortRange = ushort.MaxValue + 1;
                return (quantitized * ushortRange + ushortRange / 2) >> precision;
            }

            static int FinishUnquantitize(int q)
            {
                return (q * 31) >> 6;
            }

            static int Lerp(int a, int b, int i, int denom)
            {
                Debug.Assert(denom is 3 or 7 or 15);
                Debug.Assert(i >= 0 && i <= denom);

                if (denom == 3)
                {
                    denom *= 5;
                    i *= 5;
                }

                var weights = denom == 15 ? BPTCWeights4 : BPTCWeights3;
                return (int)((a * weights[denom - i] + b * weights[i]) / (float)(1 << 6));
            }

            static void GeneratePaletteQuantitized(int region, int[,] endpoints, Span<IntColor> palette, int wBits)
            {
                int a, b, lerped;

                for (var i = 0; i < palette.Length; i++)
                {
                    a = Unquantitize(endpoints[region, 0], wBits);
                    b = Unquantitize(endpoints[region + 1, 0], wBits);

                    lerped = Lerp(a, b, i, palette.Length - 1);
                    palette[i].Red = FinishUnquantitize(lerped);

                    a = Unquantitize(endpoints[region, 1], wBits);
                    b = Unquantitize(endpoints[region + 1, 1], wBits);

                    lerped = Lerp(a, b, i, palette.Length - 1);
                    palette[i].Green = FinishUnquantitize(lerped);

                    a = Unquantitize(endpoints[region, 2], wBits);
                    b = Unquantitize(endpoints[region + 1, 2], wBits);

                    lerped = Lerp(a, b, i, palette.Length - 1);
                    palette[i].Blue = FinishUnquantitize(lerped);
                }
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

            var endpoints = new int[4, 3];
            var deltas = new ushort[3, 3];

            int region; // one or two
            var mode = 0;
            ushort shapeIndex;
            ulong indices;
            bool isTransformed;

            ushort wBits = 0; // number of bits for the root endpoint
            Span<byte> tBits = [0, 0, 0]; // number of bits used for the transformed endpoints

            if (decvalue == 0)
            {
                mode = 1;
                wBits = 10;
                tBits = [5, 5, 5];
                endpoints[0, 0] = GetValue(header, 5, wBits);
                endpoints[0, 1] = GetValue(header, 15, wBits);
                endpoints[0, 2] = GetValue(header, 25, wBits);
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
                tBits = [6, 6, 6];
                endpoints[0, 0] = GetValue(header, 5, 7);
                endpoints[0, 1] = GetValue(header, 15, 7);
                endpoints[0, 2] = GetValue(header, 25, 7);
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
                tBits = [5, 4, 4];
                endpoints[0, 0] = GetValue(header, 5, 10) | (Bit(header, 40) << 10);
                endpoints[0, 1] = GetValue(header, 15, 10) | (Bit(header, 49) << 10);
                endpoints[0, 2] = GetValue(header, 25, 10) | (Bit(header, 59) << 10);
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
                mode = 4;
                wBits = 11;
                tBits = [4, 5, 4];
                endpoints[0, 0] = GetValue(header, 5, 10) | (Bit(header, 39) << 10);
                endpoints[0, 1] = GetValue(header, 15, 10) | (Bit(header, 50) << 10);
                endpoints[0, 2] = GetValue(header, 25, 10) | (Bit(header, 59) << 10);
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
                tBits = [4, 4, 5];
                endpoints[0, 0] = GetValue(header, 5, 10) | (Bit(header, 39) << 10);
                endpoints[0, 1] = GetValue(header, 15, 10) | (Bit(header, 49) << 10);
                endpoints[0, 2] = GetValue(header, 25, 10) | (Bit(header, 60) << 10);
                deltas[0, 0] = (ushort)GetValue(header, 35, 4);
                deltas[0, 1] = (ushort)GetValue(header, 45, 4);
                deltas[0, 2] = (ushort)GetValue(header, 55, 5);
                deltas[1, 0] = (ushort)GetValue(header, 65, 4);
                deltas[1, 1] = (ushort)GetValue(header, 41, 4);
                deltas[1, 2] = (ushort)(GetValue(header, 61, 4) | (Bit(header, 40) << 4));
                deltas[2, 0] = (ushort)GetValue(header, 71, 4);
                deltas[2, 1] = (ushort)GetValue(header, 51, 4);
                deltas[2, 2] = (ushort)(Bit(header, 50) |
                                       (Bit(header, 13) << 1) |
                                       (Bit(header, 70) << 2) |
                                       (Bit(header, 76) << 3) |
                                       (Bit(header, 34) << 4) |
                                       (Bit(header, 33) << 5));
            }
            else if (decvalue == 14)
            {
                mode = 6;
                wBits = 9;
                tBits = [5, 5, 5];
                endpoints[0, 0] = GetValue(header, 5, 9);
                endpoints[0, 1] = GetValue(header, 15, 9);
                endpoints[0, 2] = GetValue(header, 25, 9);
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
                tBits = [6, 5, 5];
                endpoints[0, 0] = GetValue(header, 5, 8);
                endpoints[0, 1] = GetValue(header, 15, 8);
                endpoints[0, 2] = GetValue(header, 25, 8);
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
                tBits = [5, 6, 5];
                endpoints[0, 0] = GetValue(header, 5, 8);
                endpoints[0, 1] = GetValue(header, 15, 8);
                endpoints[0, 2] = GetValue(header, 25, 8);
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
                tBits = [5, 5, 6];
                endpoints[0, 0] = GetValue(header, 5, 8);
                endpoints[0, 1] = GetValue(header, 15, 8);
                endpoints[0, 2] = GetValue(header, 25, 8);
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
                tBits = [6, 6, 6];
                endpoints[0, 0] = GetValue(header, 5, 6);
                endpoints[0, 1] = GetValue(header, 15, 6);
                endpoints[0, 2] = GetValue(header, 25, 6);
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
                tBits = [10, 10, 10];
                endpoints[0, 0] = GetValue(header, 5, 10);
                endpoints[0, 1] = GetValue(header, 15, 10);
                endpoints[0, 2] = GetValue(header, 25, 10);
                endpoints[1, 0] = (ushort)GetValue(header, 35, 10);
                endpoints[1, 1] = (ushort)GetValue(header, 45, 10);
                endpoints[1, 2] = (ushort)GetValue(header, 55, 10);
            }
            else if (decvalue == 7)
            {
                mode = 12;
                wBits = 11;
                tBits = [9, 9, 9];
                endpoints[0, 0] = GetValue(header, 5, 10) | (Bit(header, 44) << 10);
                endpoints[0, 1] = GetValue(header, 15, 10) | (Bit(header, 54) << 10);
                endpoints[0, 2] = GetValue(header, 25, 10) | (Bit(header, 64) << 10);
                deltas[0, 0] = (ushort)GetValue(header, 35, 9);
                deltas[0, 1] = (ushort)GetValue(header, 45, 9);
                deltas[0, 2] = (ushort)GetValue(header, 55, 9);
            }
            else if (decvalue == 11)
            {
                mode = 13;
                wBits = 12;
                tBits = [8, 8, 8];
                endpoints[0, 0] = GetValue(header, 5, 10) | (Bit(header, 44) << 10) | (Bit(header, 43) << 11);
                endpoints[0, 1] = GetValue(header, 15, 10) | (Bit(header, 54) << 10) | (Bit(header, 53) << 11);
                endpoints[0, 2] = GetValue(header, 25, 10) | (Bit(header, 64) << 10) | (Bit(header, 63) << 11);
                deltas[0, 0] = (ushort)GetValue(header, 35, 8);
                deltas[0, 1] = (ushort)GetValue(header, 45, 8);
                deltas[0, 2] = (ushort)GetValue(header, 55, 8);
            }
            else if (decvalue == 15)
            {
                mode = 14;
                wBits = 16;
                tBits = [4, 4, 4];
                endpoints[0, 0] = GetValue(header, 5, 10) | (Bit(header, 44) << 10) | (Bit(header, 43) << 11) | (Bit(header, 42) << 12) | (Bit(header, 41) << 13) | (Bit(header, 40) << 14) | (Bit(header, 39) << 15);
                endpoints[0, 1] = GetValue(header, 15, 10) | (Bit(header, 54) << 10) | (Bit(header, 53) << 11) | (Bit(header, 52) << 12) | (Bit(header, 51) << 13) | (Bit(header, 50) << 14) | (Bit(header, 49) << 15);
                endpoints[0, 2] = GetValue(header, 25, 10) | (Bit(header, 64) << 10) | (Bit(header, 63) << 11) | (Bit(header, 62) << 12) | (Bit(header, 61) << 13) | (Bit(header, 60) << 14) | (Bit(header, 59) << 15);
                deltas[0, 0] = (ushort)GetValue(header, 35, 4);
                deltas[0, 1] = (ushort)GetValue(header, 45, 4);
                deltas[0, 2] = (ushort)GetValue(header, 55, 4);
            }

            var epm = (ushort)((1U << wBits) - 1);

            // bit locations to start saving color index values
            const int ONE_REGION_INDEX_OFFSET = 65;
            const int TWO_REGION_INDEX_OFFSET = 82;

            if (mode <= 10)
            {
                region = 1;
                shapeIndex = (byte)GetValue(header, 77, 5);
                indices = (ulong)GetValue(header, TWO_REGION_INDEX_OFFSET, 46);
                isTransformed = mode < 10;
            }
            else
            {
                region = 0;
                shapeIndex = 0;
                indices = (ulong)GetValue(header, ONE_REGION_INDEX_OFFSET, 63);
                isTransformed = mode > 11;
            }

            Span<IntColor> palette0 = stackalloc IntColor[16];
            Span<IntColor> palette1 = stackalloc IntColor[16];

            Span<byte> decodedIndices = stackalloc byte[16];
            var indexOffset = region == 0 ? ONE_REGION_INDEX_OFFSET : TWO_REGION_INDEX_OFFSET;

            for (var i = 0; i < decodedIndices.Length; i++)
            {
                var nbits = 3;
                if (i == 0) // isAnchor
                {
                    nbits = region == 0 ? 3 : 2;
                }
                else
                {
                    if (region == 0)
                    {
                        nbits = 4;
                    }
                    else
                    {
                        nbits = BPTCAnchorIndices2[shapeIndex] == i ? 2 : 3;
                    }
                }

                decodedIndices[i] = (byte)GetValue(header, indexOffset, nbits);
                indexOffset += nbits;
            }

            if (region == 0)
            {
                if (isTransformed)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var extended = SignExtend(deltas[0, i], tBits[i]);
                        endpoints[1, i] = (endpoints[0, i] + extended) & epm;
                    }
                }

                GeneratePaletteQuantitized(0, endpoints, palette0[..16], wBits);
            }
            else // region 1
            {
                if (isTransformed)
                {
                    for (var i = 0; i < 3; i++)
                    {
                        var extended1 = SignExtend(deltas[0, i], tBits[i]);
                        endpoints[1, i] = (endpoints[0, i] + extended1) & epm;

                        var extended2 = SignExtend(deltas[1, i], tBits[i]);
                        endpoints[2, i] = (endpoints[0, i] + extended2) & epm;

                        var extended3 = SignExtend(deltas[2, i], tBits[i]);
                        endpoints[3, i] = (endpoints[0, i] + extended3) & epm;
                    }
                }

                GeneratePaletteQuantitized(0, endpoints, palette0[..8], wBits);
                GeneratePaletteQuantitized(1, endpoints, palette1[..8], wBits);
            }

            // Every BC6H block (16 bytes) corresponds to 16 output pixels

            for (var by = 0; by < 4; by++)
            {
                for (var bx = 0; bx < 4; bx++)
                {
                    var pixelDataOffset = (x * 4 + by) * rowBytes + (y * 4 + bx) * bytesPerPixel;
                    var io = by * 4 + bx;

                    var paletteIndex = decodedIndices[io];
                    var subset = region == 0
                        ? 0
                        : BPTCPartitionTable2[shapeIndex, io] * 2;


                    // Store LDR
                    if (bytesPerPixel == 4)
                    {
                        // TODO
                        data[pixelDataOffset + 3] = byte.MaxValue;
                        continue;
                    }

                    // Store HDR
                    var pixelOffsetFloat = pixelDataOffset / sizeof(float);

                    var color = subset == 0 ? palette0[paletteIndex] : palette1[paletteIndex];

                    Debug.Assert(color.Red >= 0);
                    Debug.Assert(color.Green >= 0);
                    Debug.Assert(color.Blue >= 0);

                    // Int to Half
                    dataHdr[pixelOffsetFloat + 0] = (float)Unsafe.As<int, Half>(ref color.Red);
                    dataHdr[pixelOffsetFloat + 1] = (float)Unsafe.As<int, Half>(ref color.Green);
                    dataHdr[pixelOffsetFloat + 2] = (float)Unsafe.As<int, Half>(ref color.Blue);
                    dataHdr[pixelOffsetFloat + 3] = 1f;
                }
            }
        }

        private static int SignExtend(ushort v, int bits)
        {
            var extend = (int)v;

            if (((v >> (bits - 1)) & 1) == 1)
            {
                extend |= -1 << bits;
            }

            return extend;
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
