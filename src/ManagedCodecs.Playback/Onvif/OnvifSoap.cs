using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace ManagedCodecs.Playback.Onvif;

/// <summary>
/// The SOAP 1.2 messages ONVIF discovery and its device and media services take, and the parsing of their answers:
/// docs/decisions/2026-11-27-live-onvif.md. Answers come from the network, so they are parsed with DTDs prohibited, no
/// resolver and a size limit, and read by local name where devices are known to differ in prefixes.
/// </summary>
internal static class OnvifSoap
{
    public static readonly XNamespace Envelope12 = "http://www.w3.org/2003/05/soap-envelope";
    public static readonly XNamespace Discovery = "http://schemas.xmlsoap.org/ws/2005/04/discovery";
    public static readonly XNamespace Addressing = "http://schemas.xmlsoap.org/ws/2004/08/addressing";
    public static readonly XNamespace Device = "http://www.onvif.org/ver10/device/wsdl";
    public static readonly XNamespace NetworkWsdl = "http://www.onvif.org/ver10/network/wsdl";
    public static readonly XNamespace Media = "http://www.onvif.org/ver10/media/wsdl";
    public static readonly XNamespace Schema = "http://www.onvif.org/ver10/schema";
    public static readonly XNamespace Security = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
    public static readonly XNamespace Utility = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

    /// <summary>Where a probe is sent in WS-Discovery 2005/04's ad hoc mode.</summary>
    public const string DiscoveryTo = "urn:schemas-xmlsoap-org:ws:2005:04:discovery";

    public const string ProbeAction = "http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe";
    public const string ProbeMatchesAction = "http://schemas.xmlsoap.org/ws/2005/04/discovery/ProbeMatches";

    private const string PasswordDigest = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest";
    private const string Base64Binary = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary";

    // An answer larger than this is no camera's: a ProbeMatch is a few hundred bytes, GetProfiles a few tens of kilobytes.
    private const int MaxCharacters = 4 * 1024 * 1024;

    /// <summary>A Probe for one type (<paramref name="type"/> in <paramref name="typeNamespace"/>), with its message ID.</summary>
    public static byte[] Probe(string messageId, XNamespace typeNamespace, string type)
    {
        var envelope = new XElement(
            Envelope12 + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Envelope12),
            new XAttribute(XNamespace.Xmlns + "a", Addressing),
            new XAttribute(XNamespace.Xmlns + "d", Discovery),
            new XAttribute(XNamespace.Xmlns + "t", typeNamespace),
            new XElement(
                Envelope12 + "Header",
                new XElement(Addressing + "Action", ProbeAction),
                new XElement(Addressing + "MessageID", messageId),
                new XElement(Addressing + "To", DiscoveryTo)),
            new XElement(Envelope12 + "Body", new XElement(Discovery + "Probe", new XElement(Discovery + "Types", "t:" + type))));
        return Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
    }

    /// <summary>A request envelope around <paramref name="body"/>, with a WS-Security header when one is given.</summary>
    public static string Request(XElement body, XElement? security)
    {
        var envelope = new XElement(
            Envelope12 + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Envelope12),
            new XAttribute(XNamespace.Xmlns + "tds", Device),
            new XAttribute(XNamespace.Xmlns + "trt", Media),
            new XAttribute(XNamespace.Xmlns + "tt", Schema),
            security is null ? new XElement(Envelope12 + "Header") : new XElement(Envelope12 + "Header", security),
            new XElement(Envelope12 + "Body", body));
        return envelope.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// The WS-Security UsernameToken (UsernameToken Profile 1.0, 3.1; ONVIF Core 5.9.5: nonce and created both):
    /// <c>Base64(SHA-1(nonce + created + password))</c>, the nonce hashed as its octets, <paramref name="created"/> in UTC.
    /// </summary>
    [SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms", Justification = "The UsernameToken Profile's PasswordDigest is defined over SHA-1, and the legacy ONVIF devices that take only this token accept nothing else: it answers the device, it protects nothing of ours.")]
    public static XElement UsernameToken(NetworkCredential credential, DateTime created, byte[] nonce)
    {
        string createdText = created.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        byte[] created8 = Encoding.UTF8.GetBytes(createdText);
        byte[] password = Encoding.UTF8.GetBytes(credential.Password ?? "");
        byte[] input = new byte[nonce.Length + created8.Length + password.Length];
        nonce.CopyTo(input, 0);
        created8.CopyTo(input, nonce.Length);
        password.CopyTo(input, nonce.Length + created8.Length);
        string digest;
        using (var sha1 = SHA1.Create())
        {
            digest = Convert.ToBase64String(sha1.ComputeHash(input));
        }

        return new XElement(
            Security + "Security",
            new XAttribute(XNamespace.Xmlns + "wsse", Security),
            new XAttribute(XNamespace.Xmlns + "wsu", Utility),
            new XAttribute(Envelope12 + "mustUnderstand", "1"),
            new XElement(
                Security + "UsernameToken",
                new XElement(Security + "Username", credential.UserName ?? ""),
                new XElement(Security + "Password", new XAttribute("Type", PasswordDigest), digest),
                new XElement(Security + "Nonce", new XAttribute("EncodingType", Base64Binary), Convert.ToBase64String(nonce)),
                new XElement(Utility + "Created", createdText)));
    }

    /// <summary>A document from the network, or null when it doesn't parse.</summary>
    public static XDocument? TryParse(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>A datagram from the network, or null when it doesn't parse.</summary>
    public static XDocument? TryParse(byte[] buffer, int count)
    {
        using var stream = new MemoryStream(buffer, 0, count, writable: false);
        return TryParse(stream);
    }

    /// <summary>The first element under <paramref name="parent"/>, at any depth, with this local name.</summary>
    public static XElement? Find(XContainer? parent, string localName) =>
        parent?.Descendants().FirstOrDefault(e => e.Name.LocalName == localName);

    /// <summary>The child elements of <paramref name="parent"/> with this local name.</summary>
    public static IEnumerable<XElement> Children(XContainer? parent, string localName) =>
        parent?.Elements().Where(e => e.Name.LocalName == localName) ?? [];

    /// <summary>The SOAP body's first element, or null without one.</summary>
    public static XElement? BodyContent(XDocument document) =>
        document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Body")?.Elements().FirstOrDefault();

    /// <summary>Whether <paramref name="element"/> is a SOAP fault saying the request wasn't authorized (ONVIF Core 5.9.1: <c>ter:NotAuthorized</c>).</summary>
    public static bool IsNotAuthorized(XElement? element) =>
        element is not null && element.Name.LocalName == "Fault"
        && element.Descendants().Any(e => e.Name.LocalName == "Value" && e.Value.Trim().EndsWith("NotAuthorized", StringComparison.Ordinal));

    /// <summary>A fault's reason text, or its code where it gives none.</summary>
    public static string FaultReason(XElement fault) =>
        Find(fault, "Text")?.Value.Trim() is { Length: > 0 } text ? text : Find(fault, "Value")?.Value.Trim() ?? "a SOAP fault";

    /// <summary>
    /// The qualified names of a whitespace-separated list of QNames (<c>d:Types</c>), their prefixes resolved in
    /// <paramref name="element"/>'s scope; names whose prefix isn't declared are left out.
    /// </summary>
    public static IEnumerable<XName> QNames(XElement element)
    {
        foreach (var item in element.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = item.IndexOf(':');
            var ns = colon < 0 ? element.GetDefaultNamespace() : element.GetNamespaceOfPrefix(item.Substring(0, colon));
            if (ns is not null)
            {
                yield return ns + item.Substring(colon + 1);
            }
        }
    }

    /// <summary>A whitespace-separated list (<c>d:Scopes</c>, <c>d:XAddrs</c>).</summary>
    public static string[] List(XElement? element) =>
        element?.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries) ?? [];
}
