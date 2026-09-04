namespace FrameFlux.WebRtc;

/// <summary>
/// High performance zero-allocation ITU-T G.711 A-law (PCMA) and mu-law (PCMU) audio decoder.
/// Decompresses 8-bit logarithmic PCM samples into 16-bit linear PCM via precomputed lookup tables.
/// </summary>
public static class G711Decoder
{
    private static readonly short[] AlawTable = new short[256];
    private static readonly short[] UlawTable = new short[256];

    static G711Decoder()
    {
        for (var i = 0; i < 256; i++)
        {
            AlawTable[i] = DecodeAlawByte((byte)i);
            UlawTable[i] = DecodeUlawByte((byte)i);
        }
    }

    /// <summary>
    /// Decodes a block of G.711 A-law (PCMA) bytes to 16-bit linear signed PCM samples.
    /// </summary>
    public static void DecodeAlaw(ReadOnlySpan<byte> alaw, Span<short> pcm)
    {
        var count = Math.Min(alaw.Length, pcm.Length);
        for (var i = 0; i < count; i++)
        {
            pcm[i] = AlawTable[alaw[i]];
        }
    }

    /// <summary>
    /// Decodes a block of G.711 mu-law (PCMU) bytes to 16-bit linear signed PCM samples.
    /// </summary>
    public static void DecodeUlaw(ReadOnlySpan<byte> ulaw, Span<short> pcm)
    {
        var count = Math.Min(ulaw.Length, pcm.Length);
        for (var i = 0; i < count; i++)
        {
            pcm[i] = UlawTable[ulaw[i]];
        }
    }

    private static short DecodeAlawByte(byte alaw)
    {
        alaw ^= 0x55;
        var sign = alaw & 0x80;
        var exponent = (alaw >> 4) & 0x07;
        var mantissa = alaw & 0x0F;
        int sample;
        if (exponent == 0)
        {
            sample = (mantissa << 4) + 8;
        }
        else
        {
            sample = ((mantissa << 4) + 0x108) << (exponent - 1);
        }

        return (short)(sign != 0 ? sample : -sample);
    }

    private static short DecodeUlawByte(byte ulaw)
    {
        ulaw = (byte)~ulaw;
        var sign = ulaw & 0x80;
        var exponent = (ulaw >> 4) & 0x07;
        var mantissa = ulaw & 0x0F;
        var sample = ((mantissa << 3) + 0x84) << exponent;
        sample -= 0x84;
        return (short)(sign != 0 ? -sample : sample);
    }
}
