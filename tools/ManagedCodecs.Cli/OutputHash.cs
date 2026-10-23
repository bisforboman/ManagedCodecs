using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ManagedCodecs.Aac;
using ManagedCodecs.Containers;
using ManagedCodecs.H264;
using ManagedCodecs.Mjpeg;

namespace ManagedCodecs.Cli;

/// <summary>
/// Decodes every stream of a file ManagedCodecs has a decoder for and hashes the output: each video frame packed
/// (<see cref="VideoFrame.WritePacked"/>) and each audio frame's samples as raw floats, in decoding order. `mcdec --hash`
/// and the WebAssembly check (tools/ManagedCodecs.Wasm) both print it, so a build for another runtime can be compared
/// with the native one, which the conformance tests compare with FFmpeg. SHA-256, not MD5: browser-wasm has no MD5.
/// </summary>
public static class OutputHash
{
    /// <summary>A line of the form "name: hash, V video and A audio frames".</summary>
    public static string Line(string name, byte[] file)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var demuxer = Demuxer.Open(new MemoryStream(file));
        var video = demuxer.Video;
        var audio = demuxer.Audio;
        IVideoDecoder? videoDecoder = video?.Codec switch
        {
            CodecIds.Mjpeg => new MjpegDecoder(video),
            CodecIds.H264 => new H264Decoder(video),
            _ => null,
        };
        IAudioDecoder? audioDecoder = audio?.Codec == CodecIds.Aac ? new AacDecoder(audio) : null;
        var packed = new MemoryStream();
        int videoFrames = 0;
        int audioFrames = 0;

        void AddVideo(IReadOnlyList<VideoFrame> frames)
        {
            foreach (var frame in frames)
            {
                packed.SetLength(0);
                frame.WritePacked(packed);
                sha.AppendData(packed.GetBuffer(), 0, (int)packed.Length);
                frame.Dispose();
                videoFrames++;
            }
        }

        foreach (var packet in demuxer.ReadPackets())
        {
            if (packet.StreamIndex == video?.StreamIndex && videoDecoder is not null)
            {
                AddVideo(videoDecoder.Decode(packet));
            }
            else if (packet.StreamIndex == audio?.StreamIndex && audioDecoder is not null)
            {
                AudioFrame decoded;
                try
                {
                    decoded = audioDecoder.Decode(packet);
                }
                catch (NotSupportedException)
                {
                    audioDecoder = null; // audio not decoded yet (HE-AAC): the rest of the file goes on without it
                    continue;
                }

                foreach (var channel in decoded.Samples)
                {
                    sha.AppendData(MemoryMarshal.AsBytes(channel.AsSpan()));
                }

                audioFrames++;
            }
        }

        if (videoDecoder is not null)
        {
            AddVideo(videoDecoder.Flush());
        }

        return $"{name}: {Convert.ToHexStringLower(sha.GetHashAndReset())}, {videoFrames} video and {audioFrames} audio frames";
    }
}
