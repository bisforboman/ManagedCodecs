using System.Text;

namespace ManagedCodecs.Playback;

/// <summary>
/// Splits a <c>multipart/x-mixed-replace</c> body (MJPEG over HTTP) into its parts: each part's headers, then its body
/// by its <c>Content-Length</c> or, without one, up to the next delimiter line. Tolerates what cameras send: line ends
/// of LF alone, a header's boundary written with its leading <c>--</c>, a header's boundary that differs from the one
/// the body uses (the first delimiter line's is taken), and no boundary in the header at all.
/// </summary>
internal sealed class MultipartReader
{
    // A line (delimiter or header) longer than this is not MJPEG over HTTP; a part longer than MaxPart isn't either.
    private const int MaxLine = 8 * 1024;
    private const int MaxPart = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private string? _boundary;
    private byte[] _delimiter = []; // "\n--" + boundary, to find a part's end without a Content-Length
    private byte[] _buffer = new byte[64 * 1024];
    private int _start;
    private int _end;
    private bool _eof;
    private bool _first = true;

    /// <param name="stream">The response body.</param>
    /// <param name="boundary">The <c>Content-Type</c> header's boundary, or null when it has none.</param>
    public MultipartReader(Stream stream, string? boundary)
    {
        _stream = stream;
        if (!string.IsNullOrEmpty(boundary))
        {
            SetBoundary(boundary!);
        }
    }

    /// <summary>The boundary the body's delimiters use once the first is read, else the header's.</summary>
    public string? Boundary => _boundary;

    /// <summary>The next part, or null at the closing delimiter or the end of the stream (a part cut off by the end is dropped).</summary>
    /// <exception cref="InvalidDataException">A line or a part too long to be MJPEG over HTTP.</exception>
    public async Task<MultipartPart?> ReadPartAsync(CancellationToken token)
    {
        // The delimiter line: the body's lines before it (a preamble, the CRLF after a part's Content-Length) are skipped.
        while (true)
        {
            string? line = await ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            line = line.TrimEnd();
            if (!line.StartsWith("--", StringComparison.Ordinal) || line.Length == 2)
            {
                continue;
            }

            if (_first)
            {
                // The first delimiter line names the boundary the body uses, whatever the header says (or doesn't):
                // the header's may carry its "--", or differ. Only the header's closing delimiter ends the body here.
                if (_boundary is not null && (line == "--" + _boundary + "--" || line == _boundary + "--"))
                {
                    return null;
                }

                SetBoundary(line.Substring(2));
                break;
            }

            if (line == "--" + _boundary + "--")
            {
                return null;
            }

            if (line == "--" + _boundary)
            {
                break;
            }
        }

        _first = false;
        string? contentType = null;
        int length = -1;
        while (true)
        {
            string? line = await ReadLineAsync(token).ConfigureAwait(false);
            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                break;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            string name = line.Substring(0, colon).Trim();
            string value = line.Substring(colon + 1).Trim();
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                contentType = value;
            }
            else if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parsed))
            {
                if (parsed > MaxPart)
                {
                    throw new InvalidDataException($"A part of {parsed} bytes, more than MJPEG over HTTP sends.");
                }

                length = parsed;
            }
        }

        byte[]? body = length >= 0
            ? await ReadExactlyAsync(length, token).ConfigureAwait(false)
            : await ReadToDelimiterAsync(token).ConfigureAwait(false);
        return body is null ? null : new MultipartPart(contentType, body);
    }

    private void SetBoundary(string boundary)
    {
        _boundary = boundary;
        _delimiter = Encoding.ASCII.GetBytes("\n--" + boundary);
    }

    // A line without its CR LF or LF, as ASCII, or null at the end of the stream.
    private async Task<string?> ReadLineAsync(CancellationToken token)
    {
        int scanned = 0;
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start + scanned, _end - _start - scanned);
            if (newline >= 0)
            {
                int length = newline - _start;
                if (length > 0 && _buffer[newline - 1] == '\r')
                {
                    length--;
                }

                string line = Encoding.ASCII.GetString(_buffer, _start, length);
                _start = newline + 1;
                return line;
            }

            scanned = _end - _start;
            if (scanned > MaxLine)
            {
                throw new InvalidDataException("A line longer than MJPEG over HTTP's headers.");
            }

            if (!await FillAsync(token).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    private async Task<byte[]?> ReadExactlyAsync(int length, CancellationToken token)
    {
        while (_end - _start < length)
        {
            if (!await FillAsync(token).ConfigureAwait(false))
            {
                return null;
            }
        }

        byte[] body = _buffer.AsSpan(_start, length).ToArray();
        _start += length;
        return body;
    }

    // The bytes up to the line end before the next delimiter, which stays to be read as the next part's.
    private async Task<byte[]?> ReadToDelimiterAsync(CancellationToken token)
    {
        int scanned = 0;
        while (true)
        {
            int found = _buffer.AsSpan(_start + scanned, _end - _start - scanned).IndexOf(_delimiter);
            if (found >= 0)
            {
                int newline = _start + scanned + found;
                int end = newline > _start && _buffer[newline - 1] == '\r' ? newline - 1 : newline;
                byte[] body = _buffer.AsSpan(_start, end - _start).ToArray();
                _start = newline + 1;
                return body;
            }

            // The delimiter may straddle what is read so far: search again from its length before the end.
            scanned = Math.Max(0, _end - _start - _delimiter.Length + 1);
            if (_end - _start > MaxPart)
            {
                throw new InvalidDataException("A part longer than MJPEG over HTTP sends.");
            }

            if (!await FillAsync(token).ConfigureAwait(false))
            {
                return null;
            }
        }
    }

    // Reads more into the buffer (moving what is left to its start, growing it when full); false at the end of the stream.
    private async Task<bool> FillAsync(CancellationToken token)
    {
        if (_eof)
        {
            return false;
        }

        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        int read = await _stream.ReadAsync(_buffer.AsMemory(_end), token).ConfigureAwait(false);
        if (read == 0)
        {
            _eof = true;
            return false;
        }

        _end += read;
        return true;
    }
}

/// <summary>A part of a multipart body: its <c>Content-Type</c> (null when it has none) and its bytes.</summary>
internal sealed class MultipartPart(string? contentType, byte[] body)
{
    public string? ContentType { get; } = contentType;

    public byte[] Body { get; } = body;

    /// <summary>A JPEG by its type, or by its first bytes (FF D8) when it has no type.</summary>
    public bool IsJpeg => ContentType is null
        ? Body.Length >= 2 && Body[0] == 0xFF && Body[1] == 0xD8
        : ContentType.StartsWith("image/jpeg", StringComparison.OrdinalIgnoreCase)
            || ContentType.StartsWith("image/jpg", StringComparison.OrdinalIgnoreCase)
            || ContentType.StartsWith("image/pjpeg", StringComparison.OrdinalIgnoreCase);
}
