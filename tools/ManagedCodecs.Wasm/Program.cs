using System.Diagnostics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Intrinsics;
using ManagedCodecs.Cli;

// The decoders on WebAssembly: prints the hash of each file's decoded output, as `mcdec --hash` does natively
// (tools/wasm-check.sh compares the two), and on stderr how long a decode takes here; with --thumbnails, each file's
// probe and thumbnail line instead, as `mcdec --thumbnail-hash` does.
//   node main.mjs [--thumbnails] [--loops N] <file>...   (from the AppBundle directory; files are read through Node)
int loops = 1;
var files = args.ToList();
bool thumbnails = files.Count > 0 && files[0] == "--thumbnails";
if (thumbnails)
{
    files.RemoveAt(0);
}

if (files.Count >= 2 && files[0] == "--loops")
{
    loops = int.Parse(files[1]);
    files.RemoveRange(0, 2);
}

Console.Error.WriteLine($"Vector128 accelerated: {Vector128.IsHardwareAccelerated}, Vector256: {Vector256.IsHardwareAccelerated}, processors: {Environment.ProcessorCount}");
foreach (var file in files)
{
    var data = Convert.FromBase64String(Host.ReadFile(file));
    var watch = Stopwatch.StartNew();
    string line = "";
    for (int loop = 0; loop < loops; loop++)
    {
        line = thumbnails ? ThumbnailOutput.Line(file, data) : OutputHash.Line(file, data);
    }

    Console.WriteLine(line);
    Console.Error.WriteLine($"{file}: {watch.Elapsed.TotalMilliseconds / loops:F1} ms a {(thumbnails ? "thumbnail" : "decode")}");
}

return 0;

// Emscripten's file system doesn't see the host's: Node reads the file (main.mjs), and it comes over as base64.
internal static partial class Host
{
    [JSImport("readFile", "main.mjs")]
    internal static partial string ReadFile(string path);
}
