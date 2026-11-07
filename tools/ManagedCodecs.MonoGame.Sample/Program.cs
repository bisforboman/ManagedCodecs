// mcplay: plays an MP4 or AVI (H.264 or MJPEG, AAC audio) in a MonoGame window, decoded by ManagedCodecs. A sample for
// in-game video (issue #254, docs/decisions/2026-10-31-game-engines.md), not a media player.
using System.Diagnostics;
using System.Reflection;
using ManagedCodecs.H264;
using ManagedCodecs.MonoGameSample;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: mcplay <file.mp4|file.avi>");
    return 1;
}

// A Debug build compiles the decoders without optimisation, several times slower (issue #402): the title's times would
// mislead.
if (typeof(H264Decoder).Assembly.GetCustomAttribute<DebuggableAttribute>() is { IsJITOptimizerDisabled: true })
{
    Console.Error.WriteLine("mcplay: the decoders were built without optimisation (a Debug build), so they decode several times slower: build with -c Release to measure.");
}

using var game = new PlayerGame(args[0]);
game.Run();
return 0;
