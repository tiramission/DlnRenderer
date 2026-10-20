using System.Text;
using DlnRenderer.Dlna.Soap;

namespace DlnRenderer.Dlna.Tests.Support;

internal static class SoapBody
{
    public static string Build(string serviceType, string action, params (string Name, string Value)[] arguments)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        builder.Append("<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ");
        builder.Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">");
        builder.Append("<s:Body>");
        builder.Append($"<u:{action} xmlns:u=\"{serviceType}\">");
        foreach (var (name, value) in arguments)
        {
            builder.Append($"<{name}>{System.Security.SecurityElement.Escape(value)}</{name}>");
        }

        builder.Append($"</u:{action}>");
        builder.Append("</s:Body></s:Envelope>");
        return builder.ToString();
    }

    public static SoapRequest Parse(string serviceType, string action, params (string Name, string Value)[] arguments)
        => SoapXml.Parse(Build(serviceType, action, arguments), $"\"{serviceType}#{action}\"");

    public static SoapRequest AvTransport(string action, params (string Name, string Value)[] arguments)
        => Parse(SoapNamespaces.AvTransportServiceType, action, arguments);
}
