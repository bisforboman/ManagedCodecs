using System.Diagnostics;
using ManagedCodecs;
using ManagedCodecs.Aac;
using ManagedCodecs.Cli;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

// mcdec: decodes every stream of a file that ManagedCodecs has a decoder for and reports the time it took, in the
// shape of `ffmpeg -benchmark`, so both can be run on the same file:
//   dotnet run -c Release --project tools/ManagedCodecs.Cli -- samples/h264-bench-720p.mp4 --loops 10
//   ffmpeg -benchmark -threads 1 -stream_loop 9 -i samples/h264-bench-720p.mp4 -f null -
if (args.Length == 0 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("usage: mcdec <file> [--loops N] [--borrow-frames] | mcdec --hash <file>...");
    Console.WriteLine("  Demuxes and decodes the file's video and audio N times (default 1), like ffmpeg -stream_loop N-1.");
    Console.WriteLine("  --borrow-frames: H.264 frames point into the decoder's pictures instead of being copied out.");
    Console.WriteLine("  --hash: prints a SHA-256 of each file's decoded output instead (tools/wasm-check.sh compares them).");
    return args.Length == 0 ? 1 : 0;
}

if (args[0] == "--hash")
{
    foreach (var file in args.Skip(1))
    {
        Console.WriteLine(OutputHash.Line(file, File.ReadAllBytes(file)));
    }

    return 0;
}

string path = args[0];
int loops = 1;
int loopsAt = Array.IndexOf(args, "--loops");
if (loopsAt >= 0 && (loopsAt + 1 >= args.Length || !int.TryParse(args[loopsAt + 1], out loops) || loops < 1))
{
    Console.Error.WriteLine("--loops needs a positive number.");
    return 1;
}

bool borrowFrames = args.Contains("--borrow-frames");
var process = Process.GetCurrentProcess();
var wall = Stopwatch.StartNew();
var lastLoop = TimeSpan.Zero;
long videoFrames = 0;
long audioFrames = 0;
string description = "";

for (int loop = 0; loop < loops; loop++)
{
    var loopStart = wall.Elapsed;
    using var demuxer = Demuxer.Open(path);
    var video = demuxer.Video;
    var audio = demuxer.Audio;

    IVideoDecoder? videoDecoder = video?.Codec switch
    {
        CodecIds.Mjpeg => new MjpegDecoder(video),
        CodecIds.H264 => new H264Decoder(video) { BorrowFrames = borrowFrames },
        _ => null,
    };
    IAudioDecoder? audioDecoder = audio?.Codec == CodecIds.Aac ? new AacDecoder(audio) : null;

    if (loop == 0)
    {
        description = string.Join(", ", new[]
        {
            video is null ? null : $"video {video.Codec} {video.Width}x{video.Height}{(videoDecoder is null ? " (no decoder, skipped)" : "")}",
            audio is null ? null : $"audio {audio.Codec} {audio.SampleRate} Hz {audio.Channels} ch{(audioDecoder is null ? " (no decoder, skipped)" : "")}",
        }.Where(s => s is not null));
    }

    foreach (var packet in demuxer.ReadPackets())
    {
        if (packet.StreamIndex == video?.StreamIndex && videoDecoder is not null)
        {
            videoFrames += Release(videoDecoder.Decode(packet));
        }
        else if (packet.StreamIndex == audio?.StreamIndex && audioDecoder is not null)
        {
            audioDecoder.Decode(packet);
            audioFrames++;
        }
    }

    if (videoDecoder is not null)
    {
        videoFrames += Release(videoDecoder.Flush()); // frames held back for reordering
    }

    lastLoop = wall.Elapsed - loopStart;
}

process.Refresh();
Console.WriteLine($"{Path.GetFileName(path)}: {description}");
Console.WriteLine($"decoded {videoFrames / loops} video and {audioFrames / loops} audio frames per loop, {loops} loop(s)");

// Like ffmpeg's line: user and system CPU time and wall time of the whole process, startup and JIT included.
Console.WriteLine($"bench: utime={process.UserProcessorTime.TotalSeconds:F3}s stime={process.PrivilegedProcessorTime.TotalSeconds:F3}s rtime={wall.Elapsed.TotalSeconds:F3}s");
if (loops > 1)
{
    // The last loop runs on code the JIT has already optimized, so it shows the steady speed.
    long perLoop = (videoFrames + audioFrames) / loops;
    Console.WriteLine($"last loop: {lastLoop.TotalMilliseconds:F1} ms, {lastLoop.TotalMicroseconds / Math.Max(1, perLoop):F1} us per frame");
}

return 0;

// Disposes the frames, as a consumer done with them would (pooled planes go back to the decoder), and counts them.
static int Release(IReadOnlyList<VideoFrame> frames)
{
    foreach (var frame in frames)
    {
        frame.Dispose();
    }

    return frames.Count;
}
