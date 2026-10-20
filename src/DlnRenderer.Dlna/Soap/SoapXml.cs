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
}

/// <summary>A parsed SOAP control request.</summary>
public sealed class SoapRequest(XElement bodyElement)
{
    public string Action { get; } = bodyElement.Name.LocalName;

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

        if (!string.IsNullOrWhiteSpace(soapActionHeader))
        {
            var expected = soapActionHeader.Trim().Trim('"');
            var expectedAction = expected.Split('#').Last();
            if (!string.Equals(expectedAction, actionElement.Name.LocalName, StringComparison.Ordinal))
            {
                throw UpnpException.InvalidArgs("SOAPAction header does not match the body action");
            }
        }

        return new SoapRequest(actionElement);
    }

    public static string BuildResponse(string serviceType, string action, IEnumerable<KeyValuePair<string, string>> arguments)
    {
        var response = new XElement(XName.Get($"{action}Response", serviceType));
        foreach (var (name, value) in arguments)
        {
            response.Add(new XElement(XName.Get(name, serviceType), value));
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
