using System.Text;
using ManagedCodecs.Playback;

namespace ManagedCodecs.Tests.Playback;

/// <summary>
/// MultipartReader on hand-built bodies: parts by Content-Length and by delimiter, the quirks cameras send (LF line
/// ends, a header's boundary with its "--", a header's boundary the body doesn't use, none at all), a body that ends
/// mid-part, and reads cut into single bytes.
/// </summary>
public class MultipartReaderTests
{
    private static readonly byte[][] Bodies =
    [
        [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9],
        [0xFF, 0xD8, (byte)'\r', (byte)'\n', (byte)'-', (byte)'-', 0xFF, 0xD9], // a line end and dashes inside
        [0xFF, 0xD8, 0xFF, 0xD9],
    ];

    public static TheoryData<bool, string> Layouts() => new()
    {
        { true, "\r\n" },
        { false, "\r\n" },
        { true, "\n" },
        { false, "\n" },
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task ReadsEachPart(bool length, string newline)
    {
        var parts = await ReadAll(Body("frame", length, newline), "frame");
        Assert.Equal(Bodies, parts.Select(p => p.Body));
        Assert.All(parts, p => Assert.Equal("image/jpeg", p.ContentType));
    }

    [Theory]
    [InlineData("--frame")] // the header's boundary written with the delimiter's dashes
    [InlineData("other")] // a header's boundary the body doesn't use
    [InlineData(null)] // none in the header
    public async Task TakesTheBoundaryFromTheFirstDelimiter(string? header)
    {
        foreach (bool length in new[] { true, false })
        {
            var reader = new MultipartReader(new MemoryStream(Body("frame", length, "\r\n")), header);
            var parts = await ReadAll(reader);
            Assert.Equal(Bodies, parts.Select(p => p.Body));
            Assert.Equal("frame", reader.Boundary);
        }
    }

    [Fact]
    public async Task SkipsAPreambleAndEndsAtTheClosingDelimiter()
    {
        var body = Encoding.ASCII.GetBytes("a preamble\r\n\r\n").Concat(Body("frame", true, "\r\n"))
            .Concat(LiveSamples.Part("frame", [0xFF, 0xD8])).ToArray();
        var parts = await ReadAll(body, "frame");
        Assert.Equal(Bodies, parts.Select(p => p.Body));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DropsAPartCutOffByTheEnd(bool length)
    {
        var body = Body("frame", length, "\r\n", closed: false);
        var cut = body.AsSpan(0, body.Length - 3).ToArray();
        var parts = await ReadAll(cut, "frame");
        Assert.Equal(Bodies[..^1], parts.Select(p => p.Body));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadsBytesArrivingOneAtATime(bool length)
    {
        var parts = await ReadAll(new MultipartReader(new TrickleStream(Body("frame", length, "\r\n")), "frame"));
        Assert.Equal(Bodies, parts.Select(p => p.Body));
    }

    [Fact]
    public void TellsAJpegByTypeOrBytes()
    {
        Assert.True(new MultipartPart("image/jpeg", []).IsJpeg);
        Assert.True(new MultipartPart("IMAGE/JPEG; charset=x", []).IsJpeg);
        Assert.False(new MultipartPart("text/plain", [0xFF, 0xD8]).IsJpeg);
        Assert.True(new MultipartPart(null, [0xFF, 0xD8, 0]).IsJpeg);
        Assert.False(new MultipartPart(null, [0x89, 0x50]).IsJpeg);
    }

    [Fact]
    public async Task RefusesALineLongerThanAnyHeader()
    {
        var body = Encoding.ASCII.GetBytes("--frame\r\n" + new string('x', 20000));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAll(body, "frame"));
    }

    // The parts, then the closing delimiter (without which a part with no Content-Length never ends), unless cut short.
    private static byte[] Body(string boundary, bool length, string newline, bool closed = true) =>
        Bodies.SelectMany(b => LiveSamples.Part(boundary, b, length: length, newline: newline))
            .Concat(Encoding.ASCII.GetBytes(closed ? $"--{boundary}--{newline}" : ""))
            .ToArray();

    private static Task<List<MultipartPart>> ReadAll(byte[] body, string? boundary) =>
        ReadAll(new MultipartReader(new MemoryStream(body), boundary));

    private static async Task<List<MultipartPart>> ReadAll(MultipartReader reader)
    {
        var parts = new List<MultipartPart>();
        while (await reader.ReadPartAsync(CancellationToken.None) is { } part)
        {
            parts.Add(part);
        }

        return parts;
    }

    // A stream that gives a byte a read, as a slow network may.
    private sealed class TrickleStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
