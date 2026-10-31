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
    Console.WriteLine("usage: mcdec <file> [--loops N] [--threads N] [--borrow-frames] [--zero-copy] [--conceal] | mcdec --hash [--loops N] <file>... | mcdec --dump <file> <prefix> | mcdec --thumbnail-hash <file>... | mcdec --thumbnail <file> <out.rgba>");
    Console.WriteLine("  Demuxes and decodes the file's video and audio N times (default 1), like ffmpeg -stream_loop N-1.");
    Console.WriteLine("  --threads N: decode up to N H.264 pictures, or HE-AAC channels, at once, each on a thread of its own (default 1).");
    Console.WriteLine("  --borrow-frames: H.264 frames point into the decoder's pictures instead of being copied out.");
    Console.WriteLine("  --zero-copy: MJPEG images are decoded straight into their frames' (MCU-padded) planes.");
    Console.WriteLine("  --conceal: H.264 conceals damaged and lost pictures (H264Decoder.ConcealErrors) instead of failing on them.");
    Console.WriteLine("  --hash: prints a SHA-256 of each file's decoded output instead (tools/wasm-check.sh compares them).");
    Console.WriteLine("  --thumbnail-hash <file>...: prints each file's VideoProbe facts and a small thumbnail's size, time and SHA-256 (tools/wasm-check.sh compares them).");
    Console.WriteLine("  --thumbnail: writes the file's thumbnail (480 x 480 at most) as raw RGBA and prints its size and time and the file's VideoProbe facts; tools/realworld.py compares them.");
    Console.WriteLine("  --dump: writes <prefix>.video.md5 (each frame's MD5, as ffmpeg -f framemd5) and <prefix>.audio.f32 (as -f f32le); tools/realworld.py compares them.");
    return args.Length == 0 ? 1 : 0;
}

if (args[0] == "--dump")
{
    if (args.Length != 3)
    {
        Console.Error.WriteLine("--dump needs a file and an output prefix.");
        return 1;
    }

    OutputDump.Write(args[1], args[2]);
    return 0;
}

#if THUMBNAILS
if (args[0] == "--thumbnail-hash")
{
    // --thumbnail-hash <file>...: each file's probe and thumbnail line (ThumbnailOutput), as tools/ManagedCodecs.Wasm
    // prints it on WebAssembly with --thumbnails; tools/wasm-check.sh compares them.
    foreach (var file in args.Skip(1))
    {
        Console.WriteLine(ThumbnailOutput.Line(file, File.ReadAllBytes(file)));
    }

    return 0;
}

if (args[0] == "--thumbnail")
{
    // --thumbnail <file> <out.rgba>: the file's thumbnail as raw RGBA, and its facts on stdout (tools/realworld.py).
    if (args.Length < 3)
    {
        Console.Error.WriteLine("--thumbnail needs a file and an output path.");
        return 2;
    }

    Console.WriteLine(ThumbnailOutput.Write(args[1], args[2]));
    return 0;
}

#endif
if (args[0] == "--hash")
{
    // --hash [--loops N] <file>...: N decodes of each file after reading it, their mean time on stderr, as
    // tools/ManagedCodecs.Wasm prints it on WebAssembly (README, WebAssembly).
    var files = args.Skip(1).ToList();
    int decodes = 1;
    if (files.Count >= 2 && files[0] == "--loops")
    {
        decodes = int.Parse(files[1]);
        files.RemoveRange(0, 2);
    }

    foreach (var file in files)
    {
        var data = File.ReadAllBytes(file);
        var watch = Stopwatch.StartNew();
        string line = "";
        for (int i = 0; i < decodes; i++)
        {
            line = OutputHash.Line(file, data);
        }

        Console.WriteLine(line);
        Console.Error.WriteLine($"{file}: {watch.Elapsed.TotalMilliseconds / decodes:F1} ms a decode");
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

int threads = 1;
int threadsAt = Array.IndexOf(args, "--threads");
if (threadsAt >= 0 && (threadsAt + 1 >= args.Length || !int.TryParse(args[threadsAt + 1], out threads) || threads < 1))
{
    Console.Error.WriteLine("--threads needs a positive number.");
    return 1;
}

bool borrowFrames = args.Contains("--borrow-frames");
bool conceal = args.Contains("--conceal");
bool zeroCopy = args.Contains("--zero-copy");
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
        CodecIds.Mjpeg => new MjpegDecoder(video) { ZeroCopy = zeroCopy },
        CodecIds.H264 => new H264Decoder(video) { BorrowFrames = borrowFrames, Threads = threads, ConcealErrors = conceal },
        _ => null,
    };
    IAudioDecoder? audioDecoder = audio?.Codec == CodecIds.Aac ? new AacDecoder(audio) { Threads = threads } : null;

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
            try
            {
                audioDecoder.Decode(packet);
                audioFrames++;
            }
            catch (NotSupportedException e)
            {
                Console.Error.WriteLine($"audio skipped: {e.Message}"); // such as HE-AAC, not decoded yet
                audioDecoder = null;
            }
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
