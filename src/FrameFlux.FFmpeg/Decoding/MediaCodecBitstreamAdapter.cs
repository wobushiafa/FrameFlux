using System.Buffers.Binary;

namespace FrameFlux.FFmpeg;

internal sealed record MediaCodecInitializationData(
    byte[]? CodecSpecificData0,
    byte[]? CodecSpecificData1,
    int NalLengthSize);

internal static class MediaCodecBitstreamAdapter
{
    private static ReadOnlySpan<byte> StartCode => [0, 0, 0, 1];

    internal static MediaCodecInitializationData Parse(
        NativeVideoCodec codec,
        ReadOnlySpan<byte> extraData)
    {
        if (extraData.IsEmpty)
        {
            return new MediaCodecInitializationData(null, null, 0);
        }

        if (StartsWithAnnexB(extraData))
        {
            return new MediaCodecInitializationData(extraData.ToArray(), null, 0);
        }

        return codec switch
        {
            NativeVideoCodec.H264 => ParseAvcConfiguration(extraData),
            NativeVideoCodec.Hevc => ParseHevcConfiguration(extraData),
            _ => new MediaCodecInitializationData(extraData.ToArray(), null, 0)
        };
    }

    internal static byte[] NormalizePacket(
        ReadOnlySpan<byte> packet,
        int nalLengthSize)
    {
        byte[] destination = [];
        var length = NormalizePacket(packet, nalLengthSize, ref destination);
        return destination.AsSpan(0, length).ToArray();
    }

    internal static int NormalizePacket(
        ReadOnlySpan<byte> packet,
        int nalLengthSize,
        ref byte[] destination)
    {
        if (packet.IsEmpty || nalLengthSize is < 1 or > 4 || StartsWithAnnexB(packet))
        {
            EnsureCapacity(ref destination, packet.Length);
            packet.CopyTo(destination);
            return packet.Length;
        }

        var outputLength = 0;
        var offset = 0;
        while (offset < packet.Length)
        {
            if (packet.Length - offset < nalLengthSize)
            {
                throw new InvalidDataException("A MediaCodec packet contains a truncated NAL length.");
            }

            var nalLength = ReadNalLength(packet.Slice(offset, nalLengthSize));
            offset += nalLengthSize;
            if (nalLength <= 0 || nalLength > packet.Length - offset)
            {
                throw new InvalidDataException("A MediaCodec packet contains an invalid NAL unit length.");
            }

            outputLength = checked(outputLength + 4 + nalLength);
            offset += nalLength;
        }

        EnsureCapacity(ref destination, outputLength);
        offset = 0;
        var outputOffset = 0;
        while (offset < packet.Length)
        {
            var nalLength = ReadNalLength(packet.Slice(offset, nalLengthSize));
            offset += nalLengthSize;
            StartCode.CopyTo(destination.AsSpan(outputOffset));
            outputOffset += 4;
            packet.Slice(offset, nalLength).CopyTo(destination.AsSpan(outputOffset));
            outputOffset += nalLength;
            offset += nalLength;
        }

        return outputLength;
    }

    private static void EnsureCapacity(ref byte[] destination, int requiredLength)
    {
        if (destination.Length >= requiredLength) return;
        destination = GC.AllocateUninitializedArray<byte>(requiredLength);
    }

    private static MediaCodecInitializationData ParseAvcConfiguration(ReadOnlySpan<byte> data)
    {
        if (data.Length < 7 || data[0] != 1)
        {
            return new MediaCodecInitializationData(data.ToArray(), null, 0);
        }

        var nalLengthSize = (data[4] & 0x03) + 1;
        var offset = 6;
        var sps = ReadNalUnits(data, ref offset, data[5] & 0x1f);
        if (offset >= data.Length)
        {
            throw new InvalidDataException("The AVC decoder configuration is missing its PPS count.");
        }

        var ppsCount = data[offset++];
        var pps = ReadNalUnits(data, ref offset, ppsCount);
        return new MediaCodecInitializationData(
            JoinWithStartCodes(sps),
            JoinWithStartCodes(pps),
            nalLengthSize);
    }

    private static MediaCodecInitializationData ParseHevcConfiguration(ReadOnlySpan<byte> data)
    {
        if (data.Length < 23 || data[0] != 1)
        {
            return new MediaCodecInitializationData(data.ToArray(), null, 0);
        }

        var nalLengthSize = (data[21] & 0x03) + 1;
        var arrayCount = data[22];
        var offset = 23;
        var units = new List<byte[]>();
        for (var arrayIndex = 0; arrayIndex < arrayCount; arrayIndex++)
        {
            if (data.Length - offset < 3)
            {
                throw new InvalidDataException("The HEVC decoder configuration contains a truncated NAL array.");
            }

            offset++;
            var unitCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            offset += 2;
            units.AddRange(ReadNalUnits(data, ref offset, unitCount));
        }

        return new MediaCodecInitializationData(
            JoinWithStartCodes(units),
            null,
            nalLengthSize);
    }

    private static List<byte[]> ReadNalUnits(
        ReadOnlySpan<byte> data,
        ref int offset,
        int count)
    {
        var units = new List<byte[]>(count);
        for (var index = 0; index < count; index++)
        {
            if (data.Length - offset < 2)
            {
                throw new InvalidDataException("The codec configuration contains a truncated NAL length.");
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            offset += 2;
            if (length == 0 || length > data.Length - offset)
            {
                throw new InvalidDataException("The codec configuration contains an invalid NAL unit.");
            }

            units.Add(data.Slice(offset, length).ToArray());
            offset += length;
        }

        return units;
    }

    private static byte[]? JoinWithStartCodes(IReadOnlyCollection<byte[]> units)
    {
        if (units.Count == 0) return null;
        var result = new byte[units.Sum(static unit => unit.Length + 4)];
        var offset = 0;
        foreach (var unit in units)
        {
            StartCode.CopyTo(result.AsSpan(offset));
            offset += 4;
            unit.CopyTo(result, offset);
            offset += unit.Length;
        }

        return result;
    }

    private static int ReadNalLength(ReadOnlySpan<byte> bytes)
    {
        var value = 0;
        foreach (var current in bytes)
        {
            value = checked((value << 8) | current);
        }

        return value;
    }

    private static bool StartsWithAnnexB(ReadOnlySpan<byte> value) =>
        value.StartsWith(StartCode) ||
        value.Length >= 3 && value[0] == 0 && value[1] == 0 && value[2] == 1;
}
