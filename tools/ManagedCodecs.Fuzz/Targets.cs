using System.Buffers.Binary;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Fuzz;

/// <summary>
/// What the fuzzer (and the malformed-input tests, which link this file) feed bytes to. The contract for malformed
/// input: <see cref="InvalidDataException"/> or <see cref="NotSupportedException"/>, which <see cref="Run"/> lets
/// pass; any other exception escapes it as a finding, and so does a hang (the fuzzer's timeout).
/// </summary>
public static class Targets
{
    /// <summary>
    /// file: a container (AVI or MP4) demuxed and every stream decoded. h264: a 2-byte big-endian length, that many
    /// bytes of avcC, then packets of length-prefixed NAL units, each packet behind a 4-byte length. mjpeg: one JPEG
    /// image. aac: a 1-byte length, that many bytes of AudioSpecificConfig, then one raw AAC frame.
    /// </summary>
    public static readonly string[] Names = ["file", "h264", "mjpeg", "aac"];

    /// <summary>
    /// Runs <paramref name="target"/> on <paramref name="data"/>, H.264 with <paramref name="threads"/> threads
    /// (<see cref="H264Decoder.Threads"/>); true if the input was accepted.
    /// </summary>
    public static bool Run(string target, ReadOnlySpan<byte> data, int threads = 1)
    {
        try
        {
            switch (target)
            {
                case "file":
                    DecodeFile(data.ToArray(), threads);
                    break;
                case "h264":
                    DecodeH264(data, threads);
                    break;
                case "mjpeg":
                    new MjpegDecoder().Decode(new Packet(0, 0, data.ToArray(), true, 0, 0, 1)).ToList().ForEach(f => f.Dispose());
                    break;
                case "aac":
                    DecodeAac(data);
                    break;
                default:
                    throw new ArgumentException($"Unknown target '{target}'.", nameof(target));
            }

            return true;
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>Demuxes a container file and decodes every packet of the streams there are decoders for.</summary>
    public static void DecodeFile(byte[] file, int threads = 1)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var video = demuxer.Video;
        var audio = demuxer.Audio;
        IVideoDecoder? videoDecoder = video?.Codec switch
        {
            CodecIds.H264 => new H264Decoder(video) { Threads = threads },
            CodecIds.Mjpeg => new MjpegDecoder(video),
            _ => null,
        };
        IAudioDecoder? audioDecoder = audio?.Codec == CodecIds.Aac ? new AacDecoder(audio) : null;
        foreach (var packet in demuxer.ReadPackets())
        {
            if (videoDecoder is not null && packet.StreamIndex == video!.StreamIndex)
            {
                Release(videoDecoder.Decode(packet));
            }
            else if (audioDecoder is not null && packet.StreamIndex == audio!.StreamIndex)
            {
                audioDecoder.Decode(packet);
            }
        }

        if (videoDecoder is not null)
        {
            Release(videoDecoder.Flush());
        }
    }

    private static void DecodeH264(ReadOnlySpan<byte> data, int threads)
    {
        if (data.Length < 2)
        {
            return;
        }

        int configLength = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(data), data.Length - 2);
        var decoder = new H264Decoder(data.Slice(2, configLength)) { Threads = threads };
        var rest = data[(2 + configLength)..];
        long sequence = 0;
        while (rest.Length >= 4)
        {
            int length = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(rest), (uint)(rest.Length - 4));
            var packet = rest.Slice(4, length).ToArray();
            Release(decoder.Decode(new Packet(0, sequence, packet, sequence == 0, sequence, sequence, 1)));
            sequence++;
            rest = rest[(4 + length)..];
        }

        Release(decoder.Flush());
    }

    private static void DecodeAac(ReadOnlySpan<byte> data)
    {
        if (data.Length < 1)
        {
            return;
        }

        int configLength = Math.Min(data[0], data.Length - 1);
        var decoder = new AacDecoder(data.Slice(1, configLength));
        decoder.Decode(data[(1 + configLength)..]);
    }

    private static void Release(IReadOnlyList<VideoFrame> frames)
    {
        foreach (var frame in frames)
        {
            frame.Dispose();
        }
    }
}
