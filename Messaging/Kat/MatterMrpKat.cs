using System.Diagnostics;
using System.Globalization;
using RIoT2.Matter.Crypto.Kat;

namespace RIoT2.Matter.Messaging.Kat;

/// <summary>
/// Known-Answer Test (KAT) harness for the Message Reliability Protocol (MRP) retransmission
/// backoff algorithm (Matter Core Specification §4.12.2.1). It drives the real
/// <see cref="ReliableMessageProtocolConfig.GetRetransmissionTimeout"/> and pins the deterministic
/// (jitter-free) backoff intervals produced by the specification formula
/// <c>t = i · MRP_BACKOFF_MARGIN · MRP_BACKOFF_BASE^max(0, n ? MRP_BACKOFF_THRESHOLD) · (1 + jitter)</c>
/// for the default idle (SII = 500 ms) and active (SAI = 300 ms) intervals. These are the same
/// vectors exercised by connectedhomeip's MRP backoff tests, so the harness guards wire-compatible
/// timing rather than the platform.
/// <list type="bullet">
///   <item><description><c>Mrp_Constants</c> = the fixed MRP protocol constants (§4.12.2)</description></item>
///   <item><description><c>Mrp_Backoff</c>   = jitter-free backoff intervals for SII / SAI</description></item>
///   <item><description><c>Mrp_Jitter</c>    = jitter term stays within [0, MRP_BACKOFF_JITTER]</description></item>
/// </list>
/// </summary>
public static class MatterMrpKat
{
    // Tolerance for floating-point millisecond comparisons (the algorithm uses Math.Pow).
    private const double ToleranceMs = 1e-6;

    /// <summary>Runs every MRP KAT and returns an aggregate report.</summary>
    public static KatReport Run(TextWriter? log = null)
    {
        log ??= TextWriter.Null;
        var sw = Stopwatch.StartNew();

        var results = new List<KatResult>
        {
            ConstantsGolden(),
            IdleBackoffGolden(),
            ActiveBackoffGolden(),
            ThresholdHoldsBaseInterval(),
            JitterWithinBounds(),
            RejectsInvalidSendCount(),
        };

        sw.Stop();
        var report = new KatReport(results, sw.Elapsed);

        log.WriteLine("Matter MRP backoff Known-Answer Tests");
        log.WriteLine("=====================================");
        foreach (KatResult r in results)
        {
            string tag = r.Status switch
            {
                KatStatus.Pass => "PASS",
                KatStatus.Fail => "FAIL",
                _ => "SKIP",
            };
            log.WriteLine($"[{tag}] {r.Primitive,-14} {r.Name}");
            if (r.Detail is not null && r.Status != KatStatus.Pass)
                log.WriteLine($"         {r.Detail}");
        }
        log.WriteLine("-------------------------------------");
        log.WriteLine($"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped " +
                      $"({report.Elapsed.TotalMilliseconds:F1} ms).");

        return report;
    }

    // ---------------------------------------------------------------------
    // Mrp_Constants — the fixed protocol constants (§4.12.2)
    // ---------------------------------------------------------------------
    private static KatResult ConstantsGolden()
    {
        return Kat("Mrp_Constants", "protocol constants match §4.12.2", () =>
            $"{ReliableMessageProtocolConfig.MaxTransmissions}|" +
            $"{ReliableMessageProtocolConfig.BackoffThreshold}|" +
            $"{ReliableMessageProtocolConfig.BackoffBase.ToString(CultureInfo.InvariantCulture)}|" +
            $"{ReliableMessageProtocolConfig.BackoffJitter.ToString(CultureInfo.InvariantCulture)}|" +
            $"{ReliableMessageProtocolConfig.BackoffMargin.ToString(CultureInfo.InvariantCulture)}",
            // MRP_MAX_TRANSMISSIONS=5, MRP_BACKOFF_THRESHOLD=1, MRP_BACKOFF_BASE=1.6,
            // MRP_BACKOFF_JITTER=0.25, MRP_BACKOFF_MARGIN=1.1.
            "5|1|1.6|0.25|1.1");
    }

    // ---------------------------------------------------------------------
    // Mrp_Backoff — jitter-free intervals for the default SII (500 ms)
    // ---------------------------------------------------------------------
    private static KatResult IdleBackoffGolden()
    {
        // i = 500 ms, jitter = 0: t(n) = 500 * 1.1 * 1.6^max(0, n-1).
        double[] expected = [550.0, 880.0, 1408.0, 2252.8, 3604.48];
        return BackoffVector("Mrp_Backoff", "idle (SII=500ms) jitter-free vector", peerIsActive: false, expected);
    }

    // ---------------------------------------------------------------------
    // Mrp_Backoff — jitter-free intervals for the default SAI (300 ms)
    // ---------------------------------------------------------------------
    private static KatResult ActiveBackoffGolden()
    {
        // i = 300 ms, jitter = 0: t(n) = 300 * 1.1 * 1.6^max(0, n-1).
        double[] expected = [330.0, 528.0, 844.8, 1351.68, 2162.688];
        return BackoffVector("Mrp_Backoff", "active (SAI=300ms) jitter-free vector", peerIsActive: true, expected);
    }

    private static KatResult BackoffVector(string primitive, string name, bool peerIsActive, double[] expectedMs)
    {
        var config = ReliableMessageProtocolConfig.Default;
        return Kat(primitive, name, () =>
        {
            for (int i = 0; i < expectedMs.Length; i++)
            {
                int sendCount = i + 1;
                double actual = config.GetRetransmissionTimeout(sendCount, peerIsActive, jitter: 0.0).TotalMilliseconds;
                if (Math.Abs(actual - expectedMs[i]) > ToleranceMs)
                    return $"n={sendCount}: expected {expectedMs[i]}ms, got {actual}ms";
            }
            return "ok";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Mrp_Backoff — the first MRP_BACKOFF_THRESHOLD retries use the base interval
    // ---------------------------------------------------------------------
    private static KatResult ThresholdHoldsBaseInterval()
    {
        // With MRP_BACKOFF_THRESHOLD = 1, the first transmission (n=1) must not apply exponential
        // backoff: exponent = max(0, 1 - 1) = 0, so t = i * MRP_BACKOFF_MARGIN only.
        var config = ReliableMessageProtocolConfig.Default;
        return Kat("Mrp_Backoff", "first transmission uses base interval (no backoff)", () =>
        {
            double idle = config.GetRetransmissionTimeout(1, peerIsActive: false, jitter: 0.0).TotalMilliseconds;
            double expected = config.IdleRetransmitTimeout.TotalMilliseconds * ReliableMessageProtocolConfig.BackoffMargin;
            return Math.Abs(idle - expected) <= ToleranceMs ? "ok" : $"expected {expected}ms, got {idle}ms";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Mrp_Jitter — the jitter term never exceeds MRP_BACKOFF_JITTER
    // ---------------------------------------------------------------------
    private static KatResult JitterWithinBounds()
    {
        var config = ReliableMessageProtocolConfig.Default;
        return Kat("Mrp_Jitter", "jitter=1 yields the (1 + MRP_BACKOFF_JITTER) upper bound", () =>
        {
            double min = config.GetRetransmissionTimeout(1, peerIsActive: false, jitter: 0.0).TotalMilliseconds;
            double max = config.GetRetransmissionTimeout(1, peerIsActive: false, jitter: 1.0).TotalMilliseconds;
            double expectedMax = min * (1.0 + ReliableMessageProtocolConfig.BackoffJitter);
            return Math.Abs(max - expectedMax) <= ToleranceMs ? "ok" : $"expected {expectedMax}ms, got {max}ms";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Mrp_Backoff — a send count below 1 is rejected
    // ---------------------------------------------------------------------
    private static KatResult RejectsInvalidSendCount()
    {
        var config = ReliableMessageProtocolConfig.Default;
        return Kat("Mrp_Backoff", "rejects sendCount < 1", () =>
        {
            try
            {
                config.GetRetransmissionTimeout(0, peerIsActive: false, jitter: 0.0);
                return "accepted-zero";
            }
            catch (ArgumentOutOfRangeException)
            {
                return "ok";
            }
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Harness plumbing (mirrors MatterCryptoKat / MatterDiscoveryKat)
    // ---------------------------------------------------------------------
    private static KatResult Kat(string primitive, string name, Func<string> actual, string expected)
    {
        try
        {
            string got = actual();
            bool ok = string.Equals(got, expected, StringComparison.Ordinal);
            return new KatResult(primitive, name, ok ? KatStatus.Pass : KatStatus.Fail,
                ok ? null : $"expected {expected}, got {got}");
        }
        catch (Exception ex)
        {
            return new KatResult(primitive, name, KatStatus.Fail, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }
}
