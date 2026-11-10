using System.Buffers.Binary;
using System.Globalization;

namespace ManagedCodecs.Containers;

/// <summary>
/// A WAVEFORMATEX (an AVI audio stream's 'strf', a .wav file's 'fmt ' chunk): wFormatTag, nChannels, nSamplesPerSec,
/// nAvgBytesPerSec, nBlockAlign, wBitsPerSample, then cbSize and cbSize bytes more. The codec is named as FFmpeg names
/// it: format tag 0x55 MP3, 0xFF AAC, 0x11 at 4 bits IMA ADPCM, 0x02 at 4 bits Microsoft ADPCM; integer PCM (tag 1) and float PCM (tag 3) by
/// wBitsPerSample, 8-bit unsigned, the rest little-endian; WAVE_FORMAT_EXTENSIBLE (0xFFFE) by its sub-format's tag, the
/// first two bytes of its GUID. Any other format is its tag in hex ("0x0006" for A-law), and so is PCM at a depth FFmpeg names
/// otherwise (64-bit integers, 12 bits).
/// </summary>
/// <param name="Tag">wFormatTag, or for WAVE_FORMAT_EXTENSIBLE its sub-format's.</param>
/// <param name="CodecPrivate">
/// The bytes after cbSize, as FFmpeg's extradata: none for WAVE_FORMAT_EXTENSIBLE but what follows its 22 bytes (valid
/// bits, channel mask, sub-format); an AAC stream's AudioSpecificConfig, IMA ADPCM's wSamplesPerBlock, Microsoft ADPCM's wSamplesPerBlock and coefficients.
/// </param>
internal readonly record struct WaveFormat(
    int Tag,
    int Channels,
    int SampleRate,
    int AverageBytesPerSecond,
    int BlockAlign,
    int BitsPerSample,
    string Codec,
    ReadOnlyMemory<byte> CodecPrivate)
{
    private const int Extensible = 0xFFFE;

    /// <summary>Reads <paramref name="format"/>; false when it is shorter than WAVEFORMAT's 16 bytes.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> format, out WaveFormat waveFormat)
    {
        var span = format.Span;
        if (span.Length < 16)
        {
            waveFormat = default;
            return false;
        }

        int tag = BinaryPrimitives.ReadUInt16LittleEndian(span);
        int channels = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
        int sampleRate = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[4..]), int.MaxValue);
        int averageBytes = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(span[8..]), int.MaxValue);
        int blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
        int bits = BinaryPrimitives.ReadUInt16LittleEndian(span[14..]);
        int extra = span.Length >= 18 ? BinaryPrimitives.ReadUInt16LittleEndian(span[16..]) : 0;
        var codecPrivate = extra > 0 && 18 + extra <= span.Length ? format.Slice(18, extra) : ReadOnlyMemory<byte>.Empty;
        if (tag == Extensible && codecPrivate.Length >= 22)
        {
            // WAVEFORMATEXTENSIBLE: wValidBitsPerSample, dwChannelMask, then the sub-format GUID, whose first two bytes are
            // the format tag it stands for.
            tag = BinaryPrimitives.ReadUInt16LittleEndian(codecPrivate.Span[6..]);
            codecPrivate = codecPrivate[22..];
        }

        string codec = (tag, bits) switch
        {
            (0x55, _) => CodecIds.Mp3,
            (0xFF, _) => CodecIds.Aac,
            (1, 8) => CodecIds.PcmU8,
            (1, 16) => CodecIds.PcmS16Le,
            (1, 24) => CodecIds.PcmS24Le,
            (1, 32) => CodecIds.PcmS32Le,
            (3, 32) => CodecIds.PcmF32Le,
            (3, 64) => CodecIds.PcmF64Le,
            (0x11, 4) => CodecIds.AdpcmImaWav,
            (0x02, 4) => CodecIds.AdpcmMs,
            _ => "0x" + tag.ToString("X4", CultureInfo.InvariantCulture),
        };
        waveFormat = new WaveFormat(tag, channels, sampleRate, averageBytes, blockAlign, bits, codec, codecPrivate);
        return true;
    }
}
