// mcplay: plays an MP4 or AVI (H.264 or MJPEG, AAC audio) in a MonoGame window, decoded by ManagedCodecs. A sample for
// in-game video (issue #254, docs/decisions/2026-10-31-game-engines.md), not a media player.
using ManagedCodecs.MonoGame;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: mcplay <file.mp4|file.avi>");
    return 1;
}

using var game = new PlayerGame(args[0]);
game.Run();
return 0;
