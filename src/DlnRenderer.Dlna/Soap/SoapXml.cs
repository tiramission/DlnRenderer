using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace DlnRenderer.Dlna.Soap;

public static class SoapNamespaces
{
    public static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    public static readonly XNamespace Encoding = "http://schemas.xmlsoap.org/soap/encoding/";
    public static readonly XNamespace Control = "urn:schemas-upnp-org:control-1-0";

    public const string AvTransportServiceType = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string ConnectionManagerServiceType = "urn:schemas-upnp-org:service:ConnectionManager:1";
    public const string RenderingControlServiceType = "urn:schemas-upnp-org:service:RenderingControl:1";
}

/// <summary>A parsed SOAP control request.</summary>
public sealed class SoapRequest(XElement bodyElement)
{
    public string Action { get; } = bodyElement.Name.LocalName;

    /// <summary>
    /// Action named by the <c>SOAPAction</c> header, when one was sent. Recorded for diagnostics
    /// only: the header is not authoritative and never causes a request to be rejected.
    /// </summary>
    public string? HeaderAction { get; init; }

    public XElement Body { get; } = bodyElement;

    /// <summary>Reads an argument by name, or null when absent.</summary>
    public string? Argument(string name)
        => Body.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
}

public static class SoapXml
{
    private static XmlReaderSettings SecureSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        MaxCharactersInDocument = 256 * 1024,
    };

    /// <summary>
    /// Parses the SOAP envelope and returns the first element inside Body. XXE and DTDs are
    /// rejected outright; malformed input throws <see cref="UpnpException"/> 402.
    /// </summary>
    public static SoapRequest Parse(string body, string? soapActionHeader)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw UpnpException.InvalidArgs("empty SOAP body");
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(body), SecureSettings());
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw UpnpException.InvalidArgs($"malformed SOAP body: {ex.Message}");
        }

        var envelope = document.Root;
        if (envelope is null || envelope.Name.LocalName != "Envelope")
        {
            throw UpnpException.InvalidArgs("missing SOAP envelope");
        }

        var bodyNode = envelope.Elements().FirstOrDefault(e => e.Name.LocalName == "Body");
        var actionElement = bodyNode?.Elements().FirstOrDefault();
        if (actionElement is null)
        {
            throw UpnpException.InvalidArgs("missing SOAP action element");
        }

        string? headerAction = null;
        if (!string.IsNullOrWhiteSpace(soapActionHeader))
        {
            // The header is advisory: the element inside Body is what identifies the action.
            // Several real control points quote it loosely (".../AVTransport:1#Play" with trailing
            // whitespace, a different service version, or an empty pair of quotes) and a device that
            // answers 402 for the discrepancy simply looks broken to them. The caller logs the
            // mismatch instead; refusing here would turn a cosmetic header into a failed cast.
            var expected = soapActionHeader.Trim().Trim('"');
            var eol = expected.IndexOfAny(['\r', '\n']);
            if (eol >= 0)
            {
                expected = expected[..eol].Trim();
            }

            headerAction = expected.Split('#').LastOrDefault();
        }

        return new SoapRequest(actionElement) { HeaderAction = headerAction };
    }

    public static string BuildResponse(string serviceType, string action, IEnumerable<KeyValuePair<string, string>> arguments)
    {
        // Shape matches the renderer Chinese casting apps are verified against:
        //   <u:PlayResponse xmlns:u="urn:...:AVTransport:1"><InstanceID ...>0</InstanceID></u:PlayResponse>
        // The action name is qualified through the u: prefix, while the out-arguments are
        // unqualified. Qualifying the arguments too (default xmlns on the response element) is
        // equally valid per the device architecture, but clients that collect arguments by tag name
        // - without resolving namespaces - then read an empty result and treat the call as failed.
        var response = new XElement(
            XName.Get($"{action}Response", serviceType),
            new XAttribute(XNamespace.Xmlns + "u", serviceType));

        foreach (var (name, value) in arguments)
        {
            response.Add(new XElement(name, value));
        }

        return BuildEnvelope(response);
    }

    public static string BuildFault(int errorCode, string description)
    {
        var fault = new XElement(SoapNamespaces.Soap + "Fault",
            new XElement("faultcode", "s:Client"),
            new XElement("faultstring", "UPnPError"),
            new XElement("detail",
                new XElement(SoapNamespaces.Control + "UPnPError",
                    new XElement(SoapNamespaces.Control + "errorCode", errorCode.ToString()),
                    new XElement(SoapNamespaces.Control + "errorDescription", description))));

        return BuildEnvelope(fault);
    }

    private static string BuildEnvelope(XElement bodyContent)
    {
        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(SoapNamespaces.Soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "s", SoapNamespaces.Soap),
                new XAttribute(SoapNamespaces.Soap + "encodingStyle", SoapNamespaces.Encoding),
                new XElement(SoapNamespaces.Soap + "Body", bodyContent)));

        // Serialize through a UTF-8 byte stream: an XmlWriter over a StringBuilder always
        // declares utf-16 (its writer encoding), which contradicts the utf-8 body we actually send
        // and makes strict parsers reject the reply.
        using var buffer = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            OmitXmlDeclaration = false,
            Encoding = new UTF8Encoding(false),
        };

        using (var writer = XmlWriter.Create(buffer, settings))
        {
            envelope.Save(writer);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
