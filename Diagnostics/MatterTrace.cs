namespace RIoT2.Matter.Diagnostics;

/// <summary>
/// The library's single, process-wide gate for verbose troubleshooting output. Every internal
/// diagnostic call site routes through here, so a host can silence all of it with one switch.
/// Disabled by default, so a consumer that never opts in produces no trace and pays no formatting
/// cost. Enable it once at startup (before the host starts) so no early trace is missed.
/// </summary>
public static class MatterTrace
{
    // A sink is set only while diagnostics are enabled; null means "off" so every call is a cheap no-op.
    private static volatile IMatterDiagnostics? _sink;

    /// <summary>Whether verbose diagnostics are currently emitted.</summary>
    public static bool Enabled => _sink is not null;

    /// <summary>
    /// Turns verbose diagnostics ON, routing every internal trace to <paramref name="sink"/>. Passing
    /// a sink again just replaces the target; call <see cref="Disable"/> to turn it back off.
    /// </summary>
    public static void Enable(IMatterDiagnostics sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    /// <summary>Turns verbose diagnostics OFF; every subsequent trace call becomes a no-op.</summary>
    public static void Disable() => _sink = null;

    /// <summary>
    /// Writes an informational diagnostic line when enabled; otherwise a no-op. The message is a
    /// deferred <see cref="Func{String}"/> so no string is formatted while diagnostics are off - this
    /// keeps the hot paths (per-datagram, per-ACL-check) allocation-free when disabled.
    /// </summary>
    public static void Write(Func<string> message)
    {
        IMatterDiagnostics? sink = _sink;
        if (sink is not null)
        {
            sink.Trace(message());
        }
    }

    /// <summary>Writes an error/warning diagnostic line when enabled; otherwise a no-op.</summary>
    public static void WriteError(Func<string> message)
    {
        IMatterDiagnostics? sink = _sink;
        if (sink is not null)
        {
            sink.TraceError(message());
        }
    }
}