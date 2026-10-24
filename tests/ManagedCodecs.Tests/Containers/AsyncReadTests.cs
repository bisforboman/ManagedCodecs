using ManagedCodecs.Containers;

namespace ManagedCodecs.Tests.Containers;

/// <summary>
/// <see cref="IDemuxer.ReadPacketsAsync"/> of the three demuxers: every read of the file awaited (none synchronous once the
/// demuxer is open), the same packets as <see cref="IDemuxer.ReadPackets"/>, and malformed input and cancellation as the
/// synchronous API has them.
/// </summary>
public class AsyncReadTests
{
    public static TheoryData<string> Files()
    {
        var files = new TheoryData<string>();
        if (TestSamples.Directory is not { } directory)
        {
            return files;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order())
        {
            if (Path.GetExtension(file) is ".avi" or ".mp4" or ".aac")
            {
                files.Add(Path.GetRelativePath(directory, file));
            }
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task ReadsOnlyAsynchronously(string name)
    {
        await using var stream = new CountingStream(File.ReadAllBytes(TestSamples.PathOf(name)));
        using var demuxer = Demuxer.Open(stream, leaveOpen: true);
        stream.SyncReads = 0; // opening reads the headers synchronously
        int packets = 0;
        await foreach (var packet in demuxer.ReadPacketsAsync())
        {
            packets++;
        }

        Assert.True(packets > 0);
        Assert.Equal(0, stream.SyncReads);
        Assert.True(stream.AsyncReads >= packets, $"{stream.AsyncReads} asynchronous reads for {packets} packets.");
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task GivesThePacketsOfTheSynchronousApi(string name)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        var expected = demuxer.ReadPackets().ToList();
        int i = 0;
        await foreach (var packet in demuxer.ReadPacketsAsync())
        {
            Assert.True(i < expected.Count, "More packets than the synchronous API's.");
            var other = expected[i++];
            Assert.Equal(other with { Data = default }, packet with { Data = default });
            Assert.True(other.Data.Span.SequenceEqual(packet.Data.Span), $"Packet {i - 1}'s data differs.");
        }

        Assert.Equal(expected.Count, i);
    }

    [Theory]
    [InlineData("mjpeg-interlaced-420.avi")]
    [InlineData("aac-adts.aac")]
    [InlineData("h264-faststart.mp4")] // its moov comes first, so the cut leaves it whole
    public async Task EndsATruncatedFileInInvalidData(string name)
    {
        var bytes = File.ReadAllBytes(TestSamples.PathOf(name));
        using var demuxer = Demuxer.Open(new MemoryStream(bytes[..(bytes.Length * 3 / 4)]));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var packet in demuxer.ReadPacketsAsync())
            {
            }
        });
    }

    [Theory]
    [InlineData("mjpeg-interlaced-420.avi")]
    [InlineData("aac-adts.aac")]
    [InlineData("heaac/heaac-stereo-44k.mp4")]
    public async Task StopsWhenCancelled(string name)
    {
        using var demuxer = Demuxer.Open(TestSamples.PathOf(name));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var packet in demuxer.ReadPacketsAsync(cancellation.Token))
            {
            }
        });
    }

    // Counts reads by kind: the synchronous ones and the asynchronous ones (which go through ReadAsync(Memory), the
    // overload ReadExactlyAsync calls, and ReadAsync(byte[]), which the base class would otherwise send to Read).
    private sealed class CountingStream(byte[] data) : MemoryStream(data, writable: false)
    {
        // Set while an asynchronous read reads through the base class, whose Read calls come back to the overrides here.
        private bool _inAsyncRead;

        public int SyncReads { get; set; }

        public int AsyncReads { get; private set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            SyncReads += _inAsyncRead ? 0 : 1;
            return base.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            SyncReads += _inAsyncRead ? 0 : 1;
            return base.Read(buffer);
        }

        public override int ReadByte()
        {
            SyncReads += _inAsyncRead ? 0 : 1;
            return base.ReadByte();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            AsyncReads++;
            return ValueTask.FromResult(ReadAsAsync(() => base.Read(buffer.Span)));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            AsyncReads++;
            return Task.FromResult(ReadAsAsync(() => base.Read(buffer, offset, count)));
        }

        private int ReadAsAsync(Func<int> read)
        {
            _inAsyncRead = true;
            try
            {
                return read();
            }
            finally
            {
                _inAsyncRead = false;
            }
        }
    }
}
