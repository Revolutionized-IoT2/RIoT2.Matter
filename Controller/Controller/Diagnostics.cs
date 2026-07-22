using System;
using System.Linq;

namespace RIoT2.Matter.Controller;

/// <summary>
/// A lightweight, process-wide switch for the controller's verbose troubleshooting output. When
/// <see cref="Enabled"/> is false, essential operator output (startup, commissioning results) still
/// prints; only the noisy commissioning/transport/ACL/IM traces are suppressed. Resolve this once at
/// startup (see <see cref="Configure"/>) before the host starts so no early trace is missed.
/// </summary>
internal static class Diagnostics
{
    /// <summary>Whether verbose diagnostic tracing is emitted. Defaults to off.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// Resolves the diagnostics preference from (in priority order): a <c>--diagnostics</c> /
    /// <c>--no-diagnostics</c> command-line flag, then the <c>CONTROLLER_DIAGNOSTICS</c> environment
    /// variable (<c>1/true/on/yes</c>). Unlike the interactive sample, a web host is typically
    /// non-interactive, so there is no console prompt: absent any signal, diagnostics default to off.
    /// </summary>
    public static void Configure(string[] args)
    {
        if (args.Any(a => a.Equals("--no-diagnostics", StringComparison.OrdinalIgnoreCase)))
        {
            Enabled = false;
        }
        else if (args.Any(a => a.Equals("--diagnostics", StringComparison.OrdinalIgnoreCase) ||
                               a.Equals("-d", StringComparison.OrdinalIgnoreCase)))
        {
            Enabled = true;
        }
        else if (TryReadEnvironmentSwitch(out bool fromEnv))
        {
            Enabled = fromEnv;
        }

        Console.WriteLine($"Verbose diagnostics: {(Enabled ? "ON" : "OFF")}");
    }

    /// <summary>Writes a diagnostic line to stdout when diagnostics are enabled; otherwise a no-op.</summary>
    public static void Trace(string message)
    {
        if (Enabled)
        {
            Console.WriteLine(message);
        }
    }

    /// <summary>Writes a diagnostic line to stderr when diagnostics are enabled; otherwise a no-op.</summary>
    public static void TraceError(string message)
    {
        if (Enabled)
        {
            Console.Error.WriteLine(message);
        }
    }

    private static bool TryReadEnvironmentSwitch(out bool value)
    {
        string? raw = Environment.GetEnvironmentVariable("CONTROLLER_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = false;
            return false;
        }

        value = raw.Trim() is "1" or "true" or "TRUE" or "on" or "ON" or "yes" or "YES";
        return true;
    }
}