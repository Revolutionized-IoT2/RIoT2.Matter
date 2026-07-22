namespace RIoT2.Matter.Diagnostics;

/// <summary>
/// An <see cref="IMatterDiagnostics"/> that discards every message. Used as the host's default so
/// callers that do not opt in incur no output and no per-call allocation.
/// </summary>
public sealed class NullMatterDiagnostics : IMatterDiagnostics
{
    /// <summary>The shared, stateless no-op instance.</summary>
    public static readonly NullMatterDiagnostics Instance = new();

    private NullMatterDiagnostics()
    {
    }

    /// <inheritdoc />
    public void Trace(string message)
    {
    }

    /// <inheritdoc />
    public void TraceError(string message)
    {
    }
}

/// <summary>
/// An <see cref="IMatterDiagnostics"/> that forwards a supplied pair of delegates, letting a caller
/// bridge an existing toggle/logger into the stack without declaring a new type. Both delegates are
/// required so neither trace channel silently disappears.
/// </summary>
public sealed class DelegateMatterDiagnostics(Action<string> trace, Action<string> traceError) : IMatterDiagnostics
{
    private readonly Action<string> _trace = trace ?? throw new ArgumentNullException(nameof(trace));
    private readonly Action<string> _traceError = traceError ?? throw new ArgumentNullException(nameof(traceError));

    /// <inheritdoc />
    public void Trace(string message) => _trace(message);

    /// <inheritdoc />
    public void TraceError(string message) => _traceError(message);
}