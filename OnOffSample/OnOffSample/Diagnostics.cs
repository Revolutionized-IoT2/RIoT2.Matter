namespace RIoT2.Matter.OnOffSample;

/// <summary>
/// A lightweight, process-wide switch for the sample's verbose troubleshooting output. When
/// <see cref="Enabled"/> is false, the essential operator output (onboarding QR, state changes,
/// help) still prints; only the noisy commissioning/transport traces are suppressed. Flip this
/// once at startup (see <see cref="Configure"/>) before the host starts so no early trace is missed.
/// </summary>
internal static class Diagnostics
{
    /// <summary>Whether verbose diagnostic tracing is emitted. Defaults to off.</summary>
    public static bool Enabled { get; private set; }

    /// <summary>
    /// Resolves the diagnostics preference from (in priority order): a <c>--diagnostics</c> /
    /// <c>--no-diagnostics</c> command-line flag, the <c>ONOFF_DIAGNOSTICS</c> environment variable
    /// (<c>1/true/on</c>), or an interactive Y/N prompt when neither is supplied and the console is
    /// interactive. Non-interactive runs with no flag default to off.
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
        else if (!Console.IsInputRedirected)
        {
            Console.Write("Enable verbose console diagnostics? [y/N]: ");
            string? answer = Console.ReadLine();
            Enabled = answer is not null &&
                      (answer.Trim().StartsWith('y') || answer.Trim().StartsWith('Y'));
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
        string? raw = Environment.GetEnvironmentVariable("ONOFF_DIAGNOSTICS");
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = false;
            return false;
        }

        value = raw.Trim() is "1" or "true" or "TRUE" or "on" or "ON" or "yes" or "YES";
        return true;
    }
}