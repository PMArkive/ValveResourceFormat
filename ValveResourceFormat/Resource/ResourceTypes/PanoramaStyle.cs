using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ValveResourceFormat.Serialization;

namespace ValveResourceFormat.ResourceTypes
{
    public class PanoramaStyle : Panorama
    {
        private BinaryKV3 SourceMap;

        public override void Read(BinaryReader reader, Resource resource)
        {
            base.Read(reader, resource);

            SourceMap = resource.GetBlockByType(BlockType.SrMa) as BinaryKV3;
        }

        public override string ToString() => ToString(true);

        public string ToString(bool applySourceMapIfPresent)
        {
            if (applySourceMapIfPresent && SourceMap != default)
            {
                var sourceBytes = PanoramaSourceMapDecoder.Decode(Data, SourceMap.AsKeyValueCollection());
                return Encoding.UTF8.GetString(sourceBytes);
            }
            else
            {
                return base.ToString();
            }
        }
    }

    static class PanoramaSourceMapDecoder
    {
        public static byte[] Decode(byte[] data, IKeyValueCollection sourceMap)
        {
            var sourceLineByteIndices = sourceMap.GetIntegerArray("SourceLineByteIndexes");
            var sourceByteRangeToDestByteRange = ParseByteRanges(sourceMap);

            var output = new List<IEnumerable<byte>>();
            var index = 0;
            var breakIndex = 1;
            foreach (var ((sourceFrom, sourceTo), (destinationFrom, destinationTo)) in sourceByteRangeToDestByteRange)
            {
                var text = Encoding.UTF8.GetString(data.Skip(destinationFrom).Take(destinationTo - destinationFrom + 1).ToArray());

                // Prepend newlines if they are in front of this chunk according to sourceLineByteIndices
                while (breakIndex < sourceLineByteIndices.Length && sourceLineByteIndices[breakIndex] < sourceFrom)
                {
                    output.Add(Enumerable.Repeat(Encoding.UTF8.GetBytes("\n")[0], 1));
                    index = (int)sourceLineByteIndices[breakIndex] + 1;
                    breakIndex++;
                }

                // Prepend spaces until we catch up to the index we need to be at
                if (index < sourceFrom)
                {
                    output.Add(Enumerable.Repeat(Encoding.UTF8.GetBytes(" ")[0], sourceFrom - index));
                    index += sourceFrom - index;
                }

                var length = destinationTo - destinationFrom + 1;

                // Copy destination
                output.Add(data.Skip(destinationFrom).Take(length));
                index += length;
            }

            return output.SelectMany(_ => _).ToArray();
        }

        private static IEnumerable<((int sourceFrom, int sourceTo), (int destFrom, int destTo))> ParseByteRanges(IKeyValueCollection sourceMap)
        {
            foreach (var range in sourceMap.GetArray("SourceByteRangeToDestByteRange"))
            {
                var sourceRange = range.GetProperty<IKeyValueCollection>("0");
                var destinationRange = range.GetProperty<IKeyValueCollection>("1");

                var sourceFrom = sourceRange.GetInt32Property("0");
                var sourceTo = sourceRange.GetInt32Property("1");

                var destinationFrom = destinationRange.GetInt32Property("0");
                var destinationTo = destinationRange.GetInt32Property("1");

                yield return ((sourceFrom, sourceTo), (destinationFrom, destinationTo));
            }
        }
    }
}
