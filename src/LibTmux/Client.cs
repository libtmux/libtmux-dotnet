namespace LibTmux;

/// <summary>Represents an immutable client handle and snapshot.</summary>
/// <remarks>
/// Thread-safe: the handle is immutable once constructed and may be shared
/// freely, including as a singleton. Every mutating call answers a new handle
/// rather than changing this one.
/// </remarks>
public sealed partial class Client
{
}
