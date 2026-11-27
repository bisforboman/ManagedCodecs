#if LIVE
using System.Net;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Cli;

/// <summary>mcdec --onvif: the ONVIF cameras on the machine's networks, and their streams (docs/decisions/2026-11-27-live-onvif.md).</summary>
internal static class OnvifList
{
    public static int Run(TimeSpan wait, string? user, string? password)
    {
        var devices = OnvifDiscovery.FindAsync(wait).GetAwaiter().GetResult();
        Console.WriteLine($"{devices.Count} ONVIF device(s)");
        int failed = 0;
        foreach (var device in devices)
        {
            Console.WriteLine($"{device.Name ?? "(no name)"} | {device.Hardware ?? "(no hardware)"} | {device.Endpoint} | {string.Join(" ", device.Addresses)}");
            if (user is null)
            {
                continue;
            }

            try
            {
                foreach (var stream in device.GetStreamsAsync(new NetworkCredential(user, password ?? "")).GetAwaiter().GetResult())
                {
                    Console.WriteLine($"  {stream}");
                }
            }
            catch (Exception e) when (e is HttpRequestException or UnauthorizedAccessException or InvalidDataException)
            {
                Console.WriteLine($"  streams: {e.Message}");
                failed++;
            }
        }

        return failed == 0 ? 0 : 1;
    }
}
#endif
