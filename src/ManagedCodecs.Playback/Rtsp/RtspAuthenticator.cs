using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ManagedCodecs.Playback.Rtsp;

/// <summary>
/// The <c>Authorization</c> header for a camera's 401 challenge: Digest (RFC 2617, MD5, with or without
/// <c>qop=auth</c>), which most cameras ask for, else Basic. One per challenge; each request gets its own header
/// (Digest hashes the method and URL, and counts requests with <c>qop</c>).
/// </summary>
internal sealed class RtspAuthenticator
{
    private static readonly Regex Parameter = new(@"([A-Za-z\-]+)\s*=\s*(""(?:[^""\\]|\\.)*""|[^,\s]*)", RegexOptions.CultureInvariant);

    private readonly NetworkCredential _credential;
    private readonly bool _digest;
    private readonly string _realm = "";
    private readonly string _nonce = "";
    private readonly string? _opaque;
    private readonly bool _qop;
    private readonly string _cnonce;
    private int _count;

    private RtspAuthenticator(NetworkCredential credential, bool digest, Dictionary<string, string>? parameters)
    {
        _credential = credential;
        _digest = digest;
        var random = new byte[8];
        using (var generator = RandomNumberGenerator.Create())
        {
            generator.GetBytes(random);
        }

        _cnonce = Hex(random);
        if (parameters is not null)
        {
            _realm = parameters.TryGetValue("realm", out var realm) ? realm : "";
            _nonce = parameters.TryGetValue("nonce", out var nonce) ? nonce : "";
            _opaque = parameters.TryGetValue("opaque", out var opaque) ? opaque : null;
            _qop = parameters.TryGetValue("qop", out var qop) && qop.Split(',').Any(q => q.Trim().Equals("auth", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// The authenticator for a 401's <c>WWW-Authenticate</c> headers: Digest with MD5 when one offers it, else Basic;
    /// null when none is either (another algorithm, another scheme).
    /// </summary>
    public static RtspAuthenticator? From(IEnumerable<string> challenges, NetworkCredential credential)
    {
        RtspAuthenticator? basic = null;
        foreach (var challenge in challenges)
        {
            var trimmed = challenge.Trim();
            if (trimmed.StartsWith("Digest", StringComparison.OrdinalIgnoreCase))
            {
                var parameters = Parameters(trimmed.Substring("Digest".Length));
                if (!parameters.TryGetValue("algorithm", out var algorithm) || algorithm.Equals("MD5", StringComparison.OrdinalIgnoreCase))
                {
                    return new RtspAuthenticator(credential, digest: true, parameters);
                }
            }
            else if (trimmed.StartsWith("Basic", StringComparison.OrdinalIgnoreCase))
            {
                basic = new RtspAuthenticator(credential, digest: false, null);
            }
        }

        return basic;
    }

    /// <summary>The <c>Authorization</c> header's value for a request.</summary>
    public string Header(string method, string uri)
    {
        if (!_digest)
        {
            return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(_credential.UserName + ":" + _credential.Password));
        }

        var header = new StringBuilder();
        header.Append("Digest username=\"").Append(_credential.UserName)
            .Append("\", realm=\"").Append(_realm)
            .Append("\", nonce=\"").Append(_nonce)
            .Append("\", uri=\"").Append(uri).Append('"');
        if (_qop)
        {
            string count = (++_count).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            header.Append(", response=\"").Append(DigestResponse(_credential.UserName, _realm, _credential.Password, method, uri, _nonce, count, _cnonce))
                .Append("\", qop=auth, nc=").Append(count).Append(", cnonce=\"").Append(_cnonce).Append('"');
        }
        else
        {
            header.Append(", response=\"").Append(DigestResponse(_credential.UserName, _realm, _credential.Password, method, uri, _nonce, null, null)).Append('"');
        }

        if (_opaque is not null)
        {
            header.Append(", opaque=\"").Append(_opaque).Append('"');
        }

        return header.ToString();
    }

    /// <summary>
    /// RFC 2617's request-digest (3.2.2.1): MD5(HA1:nonce:HA2), or with <c>qop=auth</c> MD5(HA1:nonce:nc:cnonce:auth:HA2),
    /// where HA1 is MD5(user:realm:password) and HA2 MD5(method:uri), each in lower-case hex.
    /// </summary>
    internal static string DigestResponse(string user, string realm, string password, string method, string uri, string nonce, string? count, string? cnonce)
    {
        string ha1 = Md5(user + ":" + realm + ":" + password);
        string ha2 = Md5(method + ":" + uri);
        return count is null ? Md5(ha1 + ":" + nonce + ":" + ha2) : Md5(ha1 + ":" + nonce + ":" + count + ":" + cnonce + ":auth:" + ha2);
    }

    [SuppressMessage("Security", "CA5351:Do Not Use Broken Cryptographic Algorithms", Justification = "RFC 2617's Digest authentication is defined over MD5, and cameras accept nothing else: it answers the camera's challenge, it protects nothing of ours.")]
    private static string Md5(string text)
    {
        using var md5 = MD5.Create();
        return Hex(md5.ComputeHash(Encoding.UTF8.GetBytes(text)));
    }

    // Lower-case hex (Convert.ToHexString isn't in the Unity build's netstandard2.1).
    private static string Hex(byte[] bytes)
    {
        var hex = new StringBuilder(bytes.Length * 2);
        foreach (byte b in bytes)
        {
            hex.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        }

        return hex.ToString();
    }

    private static Dictionary<string, string> Parameters(string text)
    {
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Parameter.Matches(text))
        {
            string value = match.Groups[2].Value;
            if (value.Length >= 2 && value[0] == '"')
            {
                value = value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            }

            parameters[match.Groups[1].Value] = value;
        }

        return parameters;
    }
}
