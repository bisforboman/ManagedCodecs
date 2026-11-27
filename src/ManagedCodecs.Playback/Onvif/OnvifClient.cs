using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace ManagedCodecs.Playback.Onvif;

/// <summary>
/// SOAP calls to one ONVIF device's services. Authentication (ONVIF Core 5.9.1): HTTP Digest through the handler's
/// credentials, which the Core makes the rule; a device that answers 400 or 401 with a <c>NotAuthorized</c> fault gets
/// the WS-Security UsernameToken instead (legacy devices), dated by the device's own clock (GetSystemDateAndTime, which
/// needs no authentication), and every later call does too.
/// </summary>
internal sealed class OnvifClient : IDisposable
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _http;
    private readonly NetworkCredential? _credential;
    private readonly Uri _deviceService;
    private bool _useToken;
    private TimeSpan? _clockOffset;

    public OnvifClient(Uri deviceService, NetworkCredential? credential, HttpMessageHandler? handler = null)
    {
        _deviceService = deviceService;
        _credential = credential;
        _http = new HttpClient(handler ?? new HttpClientHandler { Credentials = credential }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Whether the device took the UsernameToken (it refused, or didn't ask for, HTTP authentication).</summary>
    public bool UsesToken => _useToken;

    /// <summary>The device's clock minus this machine's, once it was asked for (for the UsernameToken).</summary>
    public TimeSpan? ClockOffset => _clockOffset;

    /// <inheritdoc/>
    public void Dispose() => _http.Dispose();

    /// <summary>Calls <paramref name="action"/> at <paramref name="service"/> and returns its answer's body element.</summary>
    /// <exception cref="UnauthorizedAccessException">The device refused the request's credentials, or asked for some and none were given.</exception>
    /// <exception cref="HttpRequestException">Another HTTP failure, or a SOAP fault.</exception>
    /// <exception cref="InvalidDataException">An answer that isn't a SOAP envelope.</exception>
    public async Task<XElement> CallAsync(Uri service, string action, XElement body, CancellationToken cancellationToken)
    {
        var (status, content) = await SendAsync(service, action, body, _useToken ? await TokenAsync(cancellationToken).ConfigureAwait(false) : null, cancellationToken).ConfigureAwait(false);
        if (!_useToken && _credential is not null && Refused(status, content))
        {
            // Not authorized through HTTP: a legacy device, which takes the UsernameToken (ONVIF Core 5.9.1).
            _useToken = true;
            (status, content) = await SendAsync(service, action, body, await TokenAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
        }

        if (Refused(status, content))
        {
            throw new UnauthorizedAccessException(_credential is null
                ? $"The camera asks for credentials ({action})."
                : $"The camera refused the credentials ({action}).");
        }

        if (content is null)
        {
            throw new InvalidDataException($"The camera's answer to {action} is not a SOAP envelope (HTTP {(int)status}).");
        }

        if (content.Name.LocalName == "Fault")
        {
            throw new HttpRequestException($"The camera answered {action} with a fault: {OnvifSoap.FaultReason(content)}");
        }

        if (status != HttpStatusCode.OK)
        {
            throw new HttpRequestException($"The camera answered {action} with HTTP {(int)status}.");
        }

        return content;
    }

    private static bool Refused(HttpStatusCode status, XElement? content) =>
        status == HttpStatusCode.Unauthorized
        || ((status == HttpStatusCode.BadRequest || status == HttpStatusCode.InternalServerError) && OnvifSoap.IsNotAuthorized(content));

    private async Task<(HttpStatusCode Status, XElement? Content)> SendAsync(Uri service, string action, XElement body, XElement? security, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CallTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, service)
        {
            Content = new StringContent(OnvifSoap.Request(body, security), Encoding.UTF8),
        };

        // SOAP 1.2 over HTTP: the action is a parameter of the media type (RFC 3902).
        var type = new MediaTypeHeaderValue("application/soap+xml") { CharSet = "utf-8" };
        type.Parameters.Add(new NameValueHeaderValue("action", "\"" + action + "\""));
        request.Content.Headers.ContentType = type;
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
#if NET
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
#else
            using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            var document = OnvifSoap.TryParse(stream);
            return (response.StatusCode, document is null ? null : OnvifSoap.BodyContent(document));
        }
        catch (OperationCanceledException)
        {
            // No exception filter (ExceptionFilterTests): the caller's cancellation goes on as it is.
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new HttpRequestException($"The camera didn't answer {action} within {CallTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>A fresh UsernameToken, dated by the device's clock.</summary>
    private async Task<XElement> TokenAsync(CancellationToken cancellationToken)
    {
        _clockOffset ??= await DeviceClockOffsetAsync(cancellationToken).ConfigureAwait(false);
        var nonce = new byte[16];
        using (var generator = RandomNumberGenerator.Create())
        {
            generator.GetBytes(nonce);
        }

        return OnvifSoap.UsernameToken(_credential!, DateTime.UtcNow + _clockOffset.Value, nonce);
    }

    /// <summary>
    /// The device's UTC clock minus this machine's (GetSystemDateAndTime, ONVIF Core 8.3.1, needs no authentication):
    /// a camera without NTP is often hours off and refuses a token dated outside its window. Zero when it doesn't say.
    /// </summary>
    private async Task<TimeSpan> DeviceClockOffsetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var (status, content) = await SendAsync(_deviceService, "http://www.onvif.org/ver10/device/wsdl/GetSystemDateAndTime", new XElement(OnvifSoap.Device + "GetSystemDateAndTime"), null, cancellationToken).ConfigureAwait(false);
            var utc = OnvifSoap.Find(content, "UTCDateTime");
            if (status != HttpStatusCode.OK || utc is null)
            {
                return TimeSpan.Zero;
            }

            var date = OnvifSoap.Find(utc, "Date");
            var time = OnvifSoap.Find(utc, "Time");
            int Part(XElement? parent, string name) => int.Parse(OnvifSoap.Find(parent, name)?.Value.Trim() ?? "", NumberStyles.None, CultureInfo.InvariantCulture);
            var device = new DateTime(Part(date, "Year"), Part(date, "Month"), Part(date, "Day"), Part(time, "Hour"), Part(time, "Minute"), Part(time, "Second"), DateTimeKind.Utc);
            return device - DateTime.UtcNow;
        }
        catch (FormatException)
        {
            return TimeSpan.Zero;
        }
        catch (OverflowException)
        {
            return TimeSpan.Zero;
        }
        catch (ArgumentOutOfRangeException)
        {
            return TimeSpan.Zero; // not a date
        }
        catch (HttpRequestException)
        {
            return TimeSpan.Zero;
        }
    }
}
