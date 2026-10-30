using System.Buffers.Binary;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;

namespace ManagedCodecs.Tests.H264;

/// <summary>
/// Interlacing as FFmpeg reports it, where <see cref="H264ConformanceTests"/> cannot compare with ffprobe: each frame's
/// <see cref="VideoFrame.FieldOrder"/> (ffprobe prints only the stream's, its first picture's; the expected orders
/// here are those a build of FFmpeg's parser printed for every packet), the picture timing SEI held across a frame's
/// two field pictures (no sample's field pictures carry one), and the SEI's messages as FFmpeg reads them.
/// </summary>
public class H264InterlacingTests
{
    private const FieldOrder P = FieldOrder.Progressive;
    private const FieldOrder TT = FieldOrder.TT;
    private const FieldOrder BB = FieldOrder.BB;

    /// <summary>
    /// The parser's order by pic_struct where the SEI is there, whatever the frame's field order counts say: on MBAFF
    /// frames whose order counts say top field first (pic_struct 4, 0, 1, 2, 3, 5, 6, 7, 8, then with ct_type 0:1, 3:0,
    /// 4:2, 5:1, 1:0, 2:1, and 6), on progressive frames (1, 3, 0, 3, 4, 2, 4, 5, 6, 0, 3:1, 4, 7, 8, 3, 6:1, and again),
    /// and on bottom field first frames with pic_struct 3, which libx264 writes for them.
    /// </summary>
    [Theory]
    [InlineData("h264-mbaff-pic-struct.mp4", new[] { BB, P, P, P, TT, TT, BB, P, P, P, TT, BB, TT, P, P, BB })]
    [InlineData("h264-pic-struct.mp4", new[] { P, TT, P, TT, BB, P, BB, TT, BB, P, TT, BB, P, P, TT, BB, P, TT, P, TT })]
    [InlineData("h264-mbaff-bff.mp4", new[] { TT })]
    [InlineData("h264-mbaff-hrd.mp4", new[] { TT })]
    public void FieldOrder_IsByPicStruct(string sampleName, FieldOrder[] expected)
    {
        if (!ParameterSetRewriter.Exists(sampleName))
        {
            return;
        }

        var frames = Decode(sampleName, threads: 1, out var decoder);
        Assert.Equal(expected[0], decoder.FieldOrder);
        Assert.Equal(expected.Length == 1 ? Enumerable.Repeat(expected[0], frames.Count) : expected, frames.Select(f => f.FieldOrder));
        Assert.DoesNotContain(frames, f => f.IsCodedAsFields);
    }

    /// <summary>
    /// Without the SEI, the order of the field order counts: bottom field first (and so bb, though libx264's SEI said
    /// pic_struct 3) and top field first. A field pair's is the order of its fields (the second field after the first
    /// of the same frame_num), and a stream that starts with a field picture has none (its first packet's, a lone
    /// field's, which is what ffprobe prints for it).
    /// </summary>
    [Theory]
    [InlineData("h264-mbaff-bff-nosei.mp4", BB, BB)]
    [InlineData("h264-mbaff-nosei.mp4", TT, TT)]
    [InlineData("jm/jm-paff.mp4", TT, FieldOrder.Unknown)]
    [InlineData("jm/jm-paff-b.mp4", TT, FieldOrder.Unknown)]
    public void FieldOrder_IsByOrderCounts(string sampleName, FieldOrder frameOrder, FieldOrder streamOrder)
    {
        if (!ParameterSetRewriter.Exists(sampleName))
        {
            return;
        }

        var frames = Decode(sampleName, threads: 1, out var decoder);
        Assert.Equal(streamOrder, decoder.FieldOrder);
        Assert.All(frames, f => Assert.Equal(frameOrder, f.FieldOrder));
        Assert.All(frames, f => Assert.Equal(sampleName.Contains("paff", StringComparison.Ordinal), f.IsCodedAsFields));
    }

    /// <summary>
    /// Frames and field pairs mixed (JM's adaptive frame/field coding): frames whose order counts are equal are
    /// progressive to the parser (JM gives a frame's fields one order count), field pairs tt.
    /// </summary>
    [Fact]
    public void FieldOrder_FramesAndFieldPairs()
    {
        var frames = Decode("jm/jm-paff-adaptive.mp4", threads: 1, out var decoder);
        Assert.Equal(P, decoder.FieldOrder);
        Assert.Contains(frames, f => f.IsCodedAsFields);
        Assert.Contains(frames, f => !f.IsCodedAsFields);
        Assert.All(frames, f => Assert.Equal(f.IsCodedAsFields ? TT : P, f.FieldOrder));
    }

    /// <summary>
    /// The previous frame's flag, which pic_struct 3 and 4 keep on a progressive frame, starts over (as interlaced) at a
    /// new picture size, as in FFmpeg (h264_slice_header_init): h264-pic-struct's last frame is not interlaced, and
    /// h264-pic-struct-small's first frame (pic_struct 3, then 0, 3) is, as ffprobe gives for the two streams joined.
    /// </summary>
    [Fact]
    public void PreviousFlag_StartsOverAtANewSize()
    {
        if (!ParameterSetRewriter.Exists("h264-pic-struct.mp4") || !ParameterSetRewriter.Exists("h264-pic-struct-small.mp4"))
        {
            return;
        }

        using var first = Mp4Demuxer.Open(TestSamples.PathOf("h264-pic-struct.mp4"));
        using var second = Mp4Demuxer.Open(TestSamples.PathOf("h264-pic-struct-small.mp4"));
        var decoder = new H264Decoder(first.Video!);
        var frames = new List<VideoFrame>();
        foreach (var packet in first.ReadPackets().Concat(second.ReadPackets()))
        {
            frames.AddRange(decoder.Decode(packet));
        }

        frames.AddRange(decoder.Flush());
        Assert.Equal(23, frames.Count);
        Assert.Equal(
            [(false, true), (true, true), (false, false), (false, true)],
            frames.Skip(19).Select(f => (f.IsInterlaced, f.IsTopFieldFirst)));
    }

    /// <summary>
    /// jm-paff with pic_struct_present_flag in its SPS and a picture timing SEI in each first field's packet only:
    /// FFmpeg's decoder keeps the SEI until the frame's second field, where it sets the frame's flags (as ffprobe gives
    /// for this stream: the flags below for every frame). Forgetting it at the second field's packet would give every
    /// frame the field pair's own flags, interlaced and top field first.
    /// </summary>
    [Theory]
    [InlineData(0, -1, false)] // a frame: not interlaced, though coded as fields
    [InlineData(0, 1, true)] // ct_type 1 (interlaced) overrides pic_struct 0
    [InlineData(1, -1, true)] // a top field
    [InlineData(2, 0, false)] // a bottom field, but ct_type 0 (progressive)
    [InlineData(3, -1, true)] // top field, bottom field
    public void FirstFieldsSei_SetsTheFrame(int picStruct, int ctType, bool interlaced)
    {
        foreach (int threads in new[] { 1, 3 })
        {
            var frames = DecodeWithSei("jm/jm-paff.mp4", picStruct, ctType, threads);
            Assert.Equal(10, frames.Count);

            // The order counts say top field first, which decides top_field_first whatever pic_struct says.
            Assert.All(frames, f => Assert.Equal((interlaced, true), (f.IsInterlaced, f.IsTopFieldFirst)));
            Assert.All(frames, f => Assert.Equal(TT, f.FieldOrder));
        }
    }

    /// <summary>
    /// SEI messages as FFmpeg reads them: payload types and sizes of more than one byte (255s), the last picture
    /// timing message of the NAL unit, none after a message that overruns the NAL unit, and none in the stop bit's byte.
    /// </summary>
    [Fact]
    public void FindPayload_ReadsMessagesAsFfmpeg()
    {
        byte[] Sei(params byte[] rbsp) => [6, .. rbsp];

        // A user data message of 300 bytes (size 255 + 45), then picture timing.
        byte[] userData = [.. new byte[] { 5, 255, 45 }, .. Enumerable.Repeat((byte)0x11, 300)];
        Assert.Equal([0x30], Find(Sei([.. userData, 1, 1, 0x30, 0x80])));

        // Type 256 + 1 is not picture timing; two picture timing messages give the last.
        Assert.Null(Find(Sei(255, 2, 1, 0x30, 0x80)));
        Assert.Equal([0x40, 0x41], Find(Sei(1, 1, 0x30, 1, 2, 0x40, 0x41, 0x80)));

        // A message overrunning the NAL unit ends the reading; the one before it stays.
        Assert.Equal([0x30], Find(Sei(1, 1, 0x30, 5, 9, 0, 0x80)));

        // Picture timing whose payload would take the stop bit's byte is cut short there, so it overruns.
        Assert.Null(Find(Sei(1, 2, 0x30, 0x80)));
    }

    /// <summary>
    /// pic_timing with HRD delays first and ct_type in a later clock timestamp: the timestamps before it are skipped
    /// by their own flags (a full one, and one with seconds, minutes and hours) and time_offset_length.
    /// </summary>
    [Fact]
    public void PictureTiming_SkipsDelaysAndTimestamps()
    {
        var w = new BitWriter();
        w.Bits(0x1234, 13); // cpb_removal_delay
        w.Bits(0x5, 7); // dpb_output_delay
        w.Bits(5, 4); // pic_struct 5: three clock timestamps
        w.Bit(1); // a full timestamp, ct_type 0
        w.Bits(0, 2);
        w.Bits(0, 1 + 5);
        w.Bit(1); // full_timestamp_flag
        w.Bits(0, 1 + 1 + 8);
        w.Bits(0x1FFFF, 17); // seconds, minutes, hours
        w.Bits(0x3FF, 10); // time_offset
        w.Bit(1); // seconds, minutes and hours by their flags, ct_type 2
        w.Bits(2, 2);
        w.Bits(0, 1 + 5);
        w.Bit(0); // full_timestamp_flag
        w.Bits(0, 1 + 1 + 8);
        w.Bit(1);
        w.Bits(0x3F, 6);
        w.Bit(1);
        w.Bits(0x3F, 6);
        w.Bit(1);
        w.Bits(0x1F, 5);
        w.Bits(0x3FF, 10);
        w.Bit(0); // no third timestamp
        w.Trailing();
        var timing = PictureTiming.Read(w.ToBytes(), new PictureTimingLayout(true, 13, 7, 10));
        Assert.Equal(new PictureTiming(5, 0b101), timing);

        // pic_struct 9 and above are dropped, as by FFmpeg.
        Assert.Null(PictureTiming.Read([0x90], new PictureTimingLayout(false, 24, 24, 24)));
    }

    private static byte[]? Find(byte[] nal) => PictureTiming.FindPayload(NalUnit.Parse(nal));

    private static List<VideoFrame> Decode(string sampleName, int threads, out H264Decoder decoder)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        decoder = new H264Decoder(video) { Threads = threads };
        var frames = new List<VideoFrame>();
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            frames.AddRange(decoder.Decode(packet));
        }

        frames.AddRange(decoder.Flush());
        return frames;
    }

    /// <summary>
    /// A field picture sample with a VUI of pic_struct_present_flag alone in its SPS (which has none) and a picture
    /// timing SEI of <paramref name="picStruct"/>, with a full clock timestamp of <paramref name="ctType"/> (none for
    /// -1), ahead of each top field (the first fields, every other packet), decoded.
    /// </summary>
    private static List<VideoFrame> DecodeWithSei(string sampleName, int picStruct, int ctType, int threads)
    {
        using var demuxer = Mp4Demuxer.Open(TestSamples.PathOf(sampleName));
        var video = demuxer.Video!;
        var original = video.CodecPrivate.ToArray();
        var config = AvcConfiguration.Parse(original);
        Assert.Single(config.Sps);
        Assert.Equal(4, config.LengthSize);
        var sps = SpsWithPicStruct(config.Sps[0]);
        byte[] avcC = [.. original.AsSpan(0, 5), 0xE1, (byte)(sps.Length >> 8), (byte)sps.Length, .. sps, .. original.AsSpan(8 + config.Sps[0].Length)];

        var sei = new BitWriter();
        sei.Bits(1, 8); // payloadType: pic_timing
        var timing = new BitWriter();
        timing.Bits(picStruct, 4);
        int timestamps = picStruct switch { <= 2 => 1, 3 or 4 or 7 => 2, _ => 3 };
        for (int i = 0; i < timestamps; i++)
        {
            timing.Bit(i == 0 && ctType >= 0 ? 1 : 0);
            if (i == 0 && ctType >= 0)
            {
                timing.Bits(ctType, 2);
                timing.Bits(0, 1 + 5); // nuit_field_based_flag, counting_type
                timing.Bit(1); // full_timestamp_flag
                timing.Bits(0, 1 + 1 + 8 + 17 + 24); // flags, n_frames, the time, time_offset (24 bits: no HRD)
            }
        }

        timing.Trailing(); // bit_equal_to_one and alignment
        var payload = timing.ToBytes();
        sei.Bits(payload.Length, 8);
        foreach (byte b in payload)
        {
            sei.Bits(b, 8);
        }

        sei.Trailing();
        var seiNal = sei.ToNal(0, 6);

        var decoder = new H264Decoder(avcC) { Threads = threads };
        var frames = new List<VideoFrame>();
        int index = 0;
        foreach (var packet in demuxer.ReadPackets().Where(p => p.StreamIndex == video.StreamIndex))
        {
            // A field a packet, the top field first; JM repeats its SPS there.
            var nals = NalUnit.SplitLengthPrefixed(packet.Data, 4).Select(n => packet.Data.Slice(n.Offset, n.Length).ToArray())
                .Select(nal => (nal[0] & 0x1F) == 7 ? SpsWithPicStruct(nal) : nal).ToList();
            if (index++ % 2 == 0)
            {
                nals.Insert(nals.FindIndex(nal => (nal[0] & 0x1F) is 1 or 5), seiNal);
            }

            var data = new List<byte>();
            foreach (var nal in nals)
            {
                var length = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(length, nal.Length);
                data.AddRange([.. length, .. nal]);
            }

            frames.AddRange(decoder.Decode([.. data], packet.Pts));
        }

        frames.AddRange(decoder.Flush());
        return frames;
    }

    /// <summary>
    /// An SPS without a VUI given one with only pic_struct_present_flag set: vui_parameters_present_flag is the last
    /// bit before the stop bit.
    /// </summary>
    private static byte[] SpsWithPicStruct(byte[] spsNal)
    {
        var nal = NalUnit.Parse(spsNal);
        var bits = new List<int>();
        for (int i = 0; i < nal.RbspLength * 8; i++)
        {
            bits.Add((nal.Rbsp[i / 8] >> (7 - (i % 8))) & 1);
        }

        int stop = bits.LastIndexOf(1);
        Assert.Equal(0, bits[stop - 1]); // vui_parameters_present_flag
        var w = new BitWriter();
        foreach (int bit in bits.Take(stop - 1))
        {
            w.Bit(bit);
        }

        w.Bit(1); // vui_parameters_present_flag
        w.Bits(0, 7); // no aspect ratio, overscan, video signal type, chroma location, timing, NAL or VCL HRD
        w.Bit(1); // pic_struct_present_flag
        w.Bit(0); // bitstream_restriction_flag
        w.Trailing();
        return w.ToNal(spsNal[0] >> 5, 7);
    }
}
