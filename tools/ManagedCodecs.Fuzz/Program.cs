using System.Buffers.Binary;
using ManagedCodecs;
using ManagedCodecs.Containers;
using ManagedCodecs.Fuzz;
using SharpFuzz;

// fuzz <target>                  under afl-fuzz (tools/fuzz.sh): reads each input from stdin, SharpFuzz reports back.
// fuzz <target> <file>...        runs the target on files, letting any exception out: reproduces a finding.
// fuzz --seeds <samples> <dir>   writes a seed corpus per target from the samples, in <dir>/<target>/.
if (args.Length == 0 || (args[0] != "--seeds" && !Targets.Names.Contains(args[0])))
{
    Console.Error.WriteLine($"usage: fuzz <{string.Join('|', Targets.Names)}> [file...] | fuzz --seeds <samples dir> <out dir>");
    return 2;
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
    }

    return 0;
}

Fuzzer.OutOfProcess.Run(stream =>
{
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    Targets.Run(target, memory.ToArray());
});
return 0;

// Seeds: small whole files for "file"; for the codec targets, the first packets of a few samples in the target's
// framing (avcC and packets, a JPEG, AudioSpecificConfig and a frame).
static void WriteSeeds(string samples, string output)
{
    foreach (var name in Targets.Names)
    {
        Directory.CreateDirectory(Path.Combine(output, name));
    }

    foreach (var path in Directory.GetFiles(samples).Where(f => f.EndsWith(".avi") || f.EndsWith(".mp4")).Order())
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        if (new FileInfo(path).Length <= 64 * 1024)
        {
            File.Copy(path, Path.Combine(output, "file", Path.GetFileName(path)), true);
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

            if (demuxer.Audio is { Codec: CodecIds.Aac } audio && packets.FirstOrDefault(p => p.StreamIndex == audio.StreamIndex) is { Data.Length: > 0 } first)
            {
                byte[] seed = [(byte)audio.CodecPrivate.Length, .. audio.CodecPrivate.Span, .. first.Data.Span];
                File.WriteAllBytes(Path.Combine(output, "aac", stem + ".bin"), seed);
            }
        }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException)
        {
        }
    }
}
