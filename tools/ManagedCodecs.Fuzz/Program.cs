using System.Buffers.Binary;
using ManagedCodecs;
using ManagedCodecs.Containers;
using ManagedCodecs.Fuzz;
using SharpFuzz;

// fuzz <target>                  under afl-fuzz (tools/fuzz.sh): reads each input from stdin, SharpFuzz reports back.
// fuzz <target> <file>...        runs the target on files, letting any exception out: reproduces a finding.
// fuzz --seeds <samples> <dir>   writes a seed corpus per target from the samples, in <dir>/<target>/.
if (args.Length == 0 || (args[0] is not "--seeds" and not "--outcomes" && !Targets.Names.Contains(args[0])))
{
    Console.Error.WriteLine($"usage: fuzz <{string.Join('|', Targets.Names)}> [file...] | fuzz --seeds <samples dir> <out dir> | fuzz --outcomes <target> <file>...");
    return 2;
}

if (args[0] == "--outcomes")
{
    // fuzz --outcomes <target> <file>...: each file's outcome (Targets.Outcome), as tools/ManagedCodecs.Wasm prints it on
    // WebAssembly; tools/wasm-check.sh compares them.
    foreach (var file in args.Skip(2))
    {
        Console.WriteLine($"{file}: {Targets.Outcome(args[1], File.ReadAllBytes(file))}");
    }

    return 0;
}

if (args[0] == "--seeds")
{
    WriteSeeds(args[1], args[2]);
    return 0;
}

string target = args[0];
if (args.Length > 1)
{
    foreach (var file in args.Skip(1))
    {
        Console.WriteLine($"{file}: {(Targets.Run(target, File.ReadAllBytes(file)) ? "accepted" : "rejected")}");

        // As the fuzzing below runs it: H.264 a second time, concealing errors.
        if (target is "h264" or "file")
        {
            Console.WriteLine($"{file}, concealing errors: {(Targets.Run(target, File.ReadAllBytes(file), conceal: true) ? "accepted" : "rejected")}");
        }
    }

    return 0;
}

Fuzzer.OutOfProcess.Run(stream =>
{
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    Targets.Run(target, memory.ToArray());

    // H.264 once more concealing errors, which decodes on where the first run stopped.
    if (target is "h264" or "file")
    {
        Targets.Run(target, memory.ToArray(), conceal: true);
    }
});
return 0;

// Seeds: small whole files for "file"; for the codec targets, the first packets of a few samples in the target's
// framing (avcC and packets, a JPEG, AudioSpecificConfig and frames).
static void WriteSeeds(string samples, string output)
{
    foreach (var name in Targets.Names)
    {
        Directory.CreateDirectory(Path.Combine(output, name));
    }

    // samples/heaac holds the committed HE-AAC streams (the FDK encoder's), parametric stereo among them.
    string heaac = Path.Combine(samples, "heaac");
    var files = Directory.GetFiles(samples).Concat(Directory.Exists(heaac) ? Directory.GetFiles(heaac) : []);
    foreach (var path in files.Where(f => f.EndsWith(".avi") || f.EndsWith(".mp4") || f.EndsWith(".aac")).Order())
    {
        string stem = Path.GetFileNameWithoutExtension(path);

        // A worktree's samples are links (CLAUDE.md), whose own length is the target path's.
        var info = new FileInfo(path);
        if ((info.ResolveLinkTarget(true) ?? info) is FileInfo { Length: <= 64 * 1024 })
        {
            File.Copy(path, Path.Combine(output, "file", Path.GetFileName(path)), true);
            if (!path.EndsWith(".aac"))
            {
                File.Copy(path, Path.Combine(output, "thumbnail", Path.GetFileName(path)), true);
            }
        }

        try
        {
            using var demuxer = Demuxer.Open(path);
            var packets = demuxer.ReadPackets().Take(40).ToList();
            if (demuxer.Video is { } video)
            {
                var videoPackets = packets.Where(p => p.StreamIndex == video.StreamIndex).Take(4).ToList(); // 4: whole field pairs
                if (video.Codec == CodecIds.Mjpeg && videoPackets.Count > 0)
                {
                    File.WriteAllBytes(Path.Combine(output, "mjpeg", stem + ".jpg"), videoPackets[0].Data.ToArray());
                }
                else if (video.Codec == CodecIds.H264 && videoPackets.Count > 0)
                {
                    var seed = new MemoryStream();
                    var length = new byte[4];
                    BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)video.CodecPrivate.Length);
                    seed.Write(length, 0, 2);
                    seed.Write(video.CodecPrivate.Span);
                    foreach (var packet in videoPackets)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)packet.Data.Length);
                        seed.Write(length);
                        seed.Write(packet.Data.Span);
                    }

                    if (seed.Length <= 64 * 1024)
                    {
                        File.WriteAllBytes(Path.Combine(output, "h264", stem + ".bin"), seed.ToArray());
                    }
                }
            }

            if (demuxer.Audio is { Codec: CodecIds.Aac } audio && packets.Where(p => p.StreamIndex == audio.StreamIndex).Take(8).Select(p => p.Data.ToArray()).ToList() is [{ Length: > 0 }, ..] frames)
            {
                File.WriteAllBytes(Path.Combine(output, "aac", stem + ".bin"), AacSeed(audio.CodecPrivate.Span, frames));

                // ADTS: the first frames again as frames of 2 to 4 raw data blocks with CRCs, which no sample has.
                if (path.EndsWith(".aac") && JoinedFrames(packets.Take(12).Select(p => p.Data.ToArray()).ToList()) is { Count: > 0 } joined)
                {
                    File.WriteAllBytes(Path.Combine(output, "file", stem + "-blocks.aac"), [.. joined.SelectMany(f => f)]);
                    File.WriteAllBytes(Path.Combine(output, "aac", stem + "-blocks.bin"), AacSeed(audio.CodecPrivate.Span, joined.Take(4)));
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
        }
    }
}

// An aac target input: the config behind its 1-byte length, then each frame behind a 2-byte big-endian one.
static byte[] AacSeed(ReadOnlySpan<byte> config, IEnumerable<byte[]> frames)
{
    var seed = new List<byte> { (byte)config.Length };
    seed.AddRange(config);
    foreach (var frame in frames)
    {
        seed.Add((byte)(frame.Length >> 8));
        seed.Add((byte)frame.Length);
        seed.AddRange(frame);
    }

    return [.. seed];
}

// ADTS frames (one raw data block each, no CRC) joined 2, 3, 4, 2, ... at a time under one header with CRCs (of no
// value: the decoder does not check them): the header's after the positions of the blocks but the first, one after
// each block (ISO/IEC 14496-3 1.A.3.2.1).
static List<byte[]> JoinedFrames(List<byte[]> frames)
{
    var joined = new List<byte[]>();
    if (frames.Any(f => f.Length < 7 || (f[1] & 1) == 0 || (f[6] & 3) != 0))
    {
        return joined;
    }

    for (int i = 0, n = 2; i + n <= frames.Count; i += n, n = (n % 4) + 2)
    {
        var data = frames.Skip(i).Take(n).SelectMany(f => f[7..].Concat(new byte[] { 0x12, 0x34 })).ToList();
        int length = 7 + (2 * n) + data.Count;
        var header = frames[i][..7];
        header[1] &= 0xFE; // protection_absent 0
        header[3] = (byte)((header[3] & 0xFC) | (length >> 11));
        header[4] = (byte)(length >> 3);
        header[5] = (byte)((header[5] & 0x1F) | ((length & 7) << 5));
        header[6] = (byte)((header[6] & 0xFC) | (n - 1));
        joined.Add([.. header, .. Enumerable.Repeat((byte)0xAB, 2 * n), .. data]);
    }

    return joined;
}
