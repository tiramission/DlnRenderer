namespace DlnRenderer.Dlna.Soap;

/// <summary>UPnP error codes used by this renderer (AVTransport / ConnectionManager).</summary>
public static class UpnpErrorCode
{
    public const int InvalidAction = 401;
    public const int InvalidArgs = 402;
    public const int ActionFailed = 501;
    public const int ArgumentValueInvalid = 600;
    public const int ArgumentValueOutOfRange = 601;
    public const int OptionalActionNotImplemented = 602;
    public const int TransitionNotAvailable = 701;
    public const int NoContents = 702;
    public const int InvalidInstanceId = 718;
    public const int ResourceNotFound = 716;
    public const int TransportIsLocked = 705;
}

/// <summary>Maps a protocol failure onto a SOAP fault with a specific UPnP error code.</summary>
public sealed class UpnpException(int code, string description)
    : Exception($"UPnP {code}: {description}")
{
    public int Code { get; } = code;

    public string Description { get; } = description;

    public static UpnpException InvalidArgs(string detail) => new(UpnpErrorCode.InvalidArgs, detail);
}
