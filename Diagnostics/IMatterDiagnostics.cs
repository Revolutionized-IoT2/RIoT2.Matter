namespace RIoT2.Matter.Diagnostics;

/// <summary>
/// A minimal sink for the library's verbose troubleshooting output (dropped datagrams, ACL checks,
/// CASE/PASE handshake steps, Interaction Model opcodes, transport sends, mDNS announces). Keeping
/// this an abstraction rather than writing to <see cref="System.Console"/> directly lets the stack
/// stay UI-agnostic: samples route it to the console, hosted services to an <c>ILogger</c>.
/// </summary>
public interface IMatterDiagnostics
{
    /// <summary>Writes an informational diagnostic line (e.g. a commissioning-window transition).</summary>
    void Trace(string message);

    /// <summary>Writes an error/warning diagnostic line (e.g. a dropped inbound datagram).</summary>
    void TraceError(string message);
}