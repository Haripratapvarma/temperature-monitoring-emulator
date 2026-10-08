namespace TempLab.Protocol;

public enum ProtocolErrorKind
{
    InvalidLength,
    Oversized,
    Truncated,
    InvalidMessage,
}

public sealed class ProtocolException : Exception
{
    public ProtocolErrorKind Kind { get; }

    public ProtocolException(ProtocolErrorKind kind, string message, Exception? inner = null)
        : base(message, inner) => Kind = kind;
}
