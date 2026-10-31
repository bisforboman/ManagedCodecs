using System.Buffers.Binary;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;
using ManagedCodecs.Thumbnails;

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
    /// image. aac: a 1-byte length, that many bytes of AudioSpecificConfig, then raw AAC frames, each behind a
    /// 2-byte big-endian length (several, so SBR's and parametric stereo's state carries from frame to frame).
    /// thumbnail: a container file probed and thumbnailed (ManagedCodecs.Thumbnails).
    /// </summary>
    public static readonly string[] Names = ["file", "h264", "mjpeg", "aac", "thumbnail"];

    /// <summary>
    /// What <see cref="Run"/> makes of <paramref name="data"/>, as one line: "accepted", "rejected", or the type and
    /// message of any other exception (a finding), both on one thread and, for the targets that decode H.264, concealing
    /// errors. tools/wasm-check.sh compares these lines on WebAssembly with the native build's for every finding kept in
    /// tests/ManagedCodecs.Tests/Fuzz/Findings: the error paths, which the samples there don't reach.
    /// </summary>
    public static string Outcome(string target, byte[] data)
    {
        string plain = OutcomeOf(target, data, conceal: false);
        return target is "h264" or "file" ? plain + ", concealing errors " + OutcomeOf(target, data, conceal: true) : plain;
    }

    private static string OutcomeOf(string target, byte[] data, bool conceal)
    {
        try
        {
            return Run(target, data, conceal: conceal) ? "accepted" : "rejected";
        }
        catch (Exception e)
        {
            return e.GetType().Name + ": " + e.Message;
        }
    }

    /// <summary>
    /// Runs <paramref name="target"/> on <paramref name="data"/>, H.264 with <paramref name="threads"/> threads
    /// (<see cref="H264Decoder.Threads"/>) and concealing errors if <paramref name="conceal"/>
    /// (<see cref="H264Decoder.ConcealErrors"/>); true if the input was accepted.
    /// </summary>
    public static bool Run(string target, ReadOnlySpan<byte> data, int threads = 1, bool conceal = false)
    {
        try
        {
            switch (target)
            {
                case "file":
                    DecodeFile(data.ToArray(), threads, conceal);
                    break;
                case "h264":
                    DecodeH264(data, threads, conceal);
                    break;
                case "mjpeg":
                    new MjpegDecoder().Decode(new Packet(0, 0, data.ToArray(), true, 0, 0, 1)).ToList().ForEach(f => f.Dispose());
                    break;
                case "aac":
                    DecodeAac(data);
                    break;
                case "thumbnail":
                    Thumbnail(data.ToArray());
                    break;
                default:
                    throw new ArgumentException($"Unknown target '{target}'.", nameof(target));
            }

            return true;
        }
        catch (Exception e)
        {
            // No exception filter: this file runs on WebAssembly too (tools/ManagedCodecs.Wasm/CLAUDE.md).
            if (e is not (InvalidDataException or NotSupportedException))
            {
                throw;
            }

            return false;
        }
    }

    /// <summary>
    /// Probes a container file and makes its thumbnail the way an upload service would: small, stepping past dark
    /// frames (more decoding, keyframe by keyframe), as a PNG file.
    /// </summary>
    public static void Thumbnail(byte[] file)
    {
        VideoProbe.Read(new MemoryStream(file));
        var options = new ThumbnailOptions { MaxWidth = 48, MaxHeight = 48, SkipDark = true };
        VideoThumbnail.Create(new MemoryStream(file), options).ToPng();
    }

    /// <summary>
    /// Demuxes a container file and decodes every packet of the streams there are decoders for. An MP4 file is read again
    /// one track at a time and by <see cref="Mp4Demuxer.ReadPacketInfo"/>, which must give the same packets.
    /// </summary>
    public static void DecodeFile(byte[] file, int threads = 1, bool conceal = false)
    {
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var video = demuxer.Video;
        var audio = demuxer.Audio;
        IVideoDecoder? videoDecoder = video?.Codec switch
        {
            CodecIds.H264 => new H264Decoder(video) { Threads = threads, ConcealErrors = conceal },
            CodecIds.Mjpeg => new MjpegDecoder(video),
            _ => null,
        };
        IAudioDecoder? audioDecoder = audio?.Codec == CodecIds.Aac ? new AacDecoder(audio) : null;
        var infos = new List<PacketInfo>();
        foreach (var packet in demuxer.ReadPackets())
        {
            infos.Add(Info(packet));
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

        if (demuxer is Mp4Demuxer mp4)
        {
            CheckSame(infos, mp4.ReadPacketInfo(), "ReadPacketInfo");
            foreach (var (tracks, stream) in new[] { (MediaTypes.Video, video?.StreamIndex), (MediaTypes.Audio, audio?.StreamIndex) })
            {
                if (stream is { } index)
                {
                    using var one = new Mp4Demuxer(new MemoryStream(file), tracks);
                    var expected = infos.Where(p => p.StreamIndex == index).ToList();
                    CheckSame(expected, one.ReadPackets().Select(Info), $"{tracks} only");
                    CheckSame(expected, one.ReadPacketInfo(), $"{tracks} only, ReadPacketInfo");
                }
            }
        }
    }

    private static PacketInfo Info(Packet p) => new(p.StreamIndex, p.Sequence, p.Data.Length, p.IsKeyframe, p.Dts, p.Pts, p.Duration, p.SkipSamples, p.Discard);

    private static void CheckSame(List<PacketInfo> expected, IEnumerable<PacketInfo> actual, string what)
    {
        var list = actual.ToList();
        int differs = Enumerable.Range(0, Math.Min(expected.Count, list.Count)).FirstOrDefault(i => expected[i] != list[i], -1);
        if (differs >= 0 || expected.Count != list.Count)
        {
            throw new InvalidOperationException($"{what}: packet {(differs >= 0 ? differs : Math.Min(expected.Count, list.Count))} differs from ReadPackets' ({expected.Count} packets, {list.Count} here).");
        }
    }

    private static void DecodeH264(ReadOnlySpan<byte> data, int threads, bool conceal)
    {
        if (data.Length < 2)
        {
            return;
        }

        int configLength = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(data), data.Length - 2);
        var decoder = new H264Decoder(data.Slice(2, configLength)) { Threads = threads, ConcealErrors = conceal };
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
        var rest = data[(1 + configLength)..];
        while (rest.Length >= 2)
        {
            int length = Math.Min(BinaryPrimitives.ReadUInt16BigEndian(rest), rest.Length - 2);
            decoder.Decode(rest.Slice(2, length));
            rest = rest[(2 + length)..];
        }
    }

    private static void Release(IReadOnlyList<VideoFrame> frames)
    {
        foreach (var frame in frames)
        {
            frame.Dispose();
        }
    }
}
