using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using RIoT2.Matter.Crypto.Kat;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Hosting;
using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel.Case.Kat;

/// <summary>
/// Known-Answer Test (KAT) harness for CASE session resumption (Matter Core Specification §4.14.2.6).
/// It guards the resumption key schedule exposed by <see cref="ManagedCaseCryptoProvider"/> and the
/// <see cref="ICaseResumptionStore"/> semantics that the <see cref="CaseServer"/>/<see cref="CaseClient"/>
/// state machines depend on: MIC generation/verification, resumed-session-key agreement, the Type 2
/// (decline) discriminators, and store eviction/replacement/lookup.
/// <list type="bullet">
///   <item><description><c>Case_Resume_Mic</c>   = Sigma1/Sigma2 resume-MIC generate + verify + tamper</description></item>
///   <item><description><c>Case_Resume_Keys</c>  = resumed session-key agreement + salt sensitivity</description></item>
///   <item><description><c>Case_Resume_Store</c> = per-peer replacement, LRU eviction, resumptionID lookup</description></item>
///   <item><description><c>Case_Resume_Vector</c>= optional connectedhomeip wire-compatibility check</description></item>
/// </list>
/// </summary>
/// <remarks>
/// A full Sigma1→Sigma2_Resume→StatusReport round trip through the state machines needs commissioned
/// NOC/fabric fixtures; this harness instead pins the new resumption primitives and store behavior,
/// which is where the resumption-specific logic lives. Paste an authoritative vector below to promote
/// <c>Case_Resume_Vector</c> from SKIP to a wire-compatibility check.
/// </remarks>
public static class CaseResumptionKat
{
    // --- Authoritative resumption fixed vector (spec §4.14.2.6) ------------
    // Leave blank to SKIP; paste an authoritative connectedhomeip vector to pin the exact key schedule.
    private const string SpecSharedSecret = "";     // 32-byte hex ECDH shared secret
    private const string SpecInitiatorRandom = "";   // 32-byte hex initiator random
    private const string SpecResumptionId = "";      // 16-byte hex resumption id
    private const string SpecExpectedSigma1Mic = ""; // 16-byte hex initiatorResumeMIC
    private const string SpecExpectedSigma2Mic = ""; // 16-byte hex sigma2ResumeMIC
    private const string SpecExpectedI2RKey = "";     // 16-byte hex I2R session key

    /// <summary>Runs every CASE resumption KAT and returns an aggregate report.</summary>
    public static KatReport Run(TextWriter? log = null)
    {
        log ??= TextWriter.Null;
        var sw = Stopwatch.StartNew();

        var results = new List<KatResult>();
        results.AddRange(ResumeMicVectors());
        results.AddRange(ResumeKeyVectors());
        results.AddRange(ResumeStoreVectors());
        results.AddRange(ResumeSpecVector());

        sw.Stop();
        var report = new KatReport(results, sw.Elapsed);

        log.WriteLine("CASE session-resumption Known-Answer Tests");
        log.WriteLine("==========================================");
        foreach (KatResult r in results)
        {
            string tag = r.Status switch
            {
                KatStatus.Pass => "PASS",
                KatStatus.Fail => "FAIL",
                _ => "SKIP",
            };
            log.WriteLine($"[{tag}] {r.Primitive,-18} {r.Name}");
            if (r.Detail is not null && r.Status != KatStatus.Pass)
            {
                log.WriteLine($"         {r.Detail}");
            }
        }

        log.WriteLine("------------------------------------------");
        log.WriteLine($"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped " +
                      $"({report.Elapsed.TotalMilliseconds:F1} ms).");

        return report;
    }

    // ---------------------------------------------------------------------
    // Case_Resume_Mic — Sigma1/Sigma2 resume-MIC generate + verify + tamper
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> ResumeMicVectors()
    {
        var crypto = new ManagedCaseCryptoProvider();

        yield return Kat("Case_Resume_Mic", "Sigma1 MIC verifies + tamper rejected", () =>
        {
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            byte[] initiatorRandom = RandomNumberGenerator.GetBytes(32);
            byte[] resumptionId = crypto.GenerateResumptionId();

            byte[] mic = crypto.ComputeSigma1ResumeMic(secret, initiatorRandom, resumptionId);
            if (mic.Length != 16)
            {
                return "mic-not-16-bytes";
            }

            if (!crypto.VerifySigma1ResumeMic(secret, initiatorRandom, resumptionId, mic))
            {
                return "valid-mic-rejected";
            }

            // A wrong resumptionID must not verify.
            byte[] otherId = crypto.GenerateResumptionId();
            if (crypto.VerifySigma1ResumeMic(secret, initiatorRandom, otherId, mic))
            {
                return "wrong-resumption-id-accepted";
            }

            // A tampered MIC must not verify.
            mic[0] ^= 0x01;
            return crypto.VerifySigma1ResumeMic(secret, initiatorRandom, resumptionId, mic) ? "tampered-mic-accepted" : "ok";
        },
        "ok");

        yield return Kat("Case_Resume_Mic", "Sigma1 and Sigma2 MICs are domain-separated", () =>
        {
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            byte[] initiatorRandom = RandomNumberGenerator.GetBytes(32);
            byte[] resumptionId = crypto.GenerateResumptionId();

            byte[] mic1 = crypto.ComputeSigma1ResumeMic(secret, initiatorRandom, resumptionId);
            byte[] mic2 = crypto.ComputeSigma2ResumeMic(secret, initiatorRandom, resumptionId);

            // Different key/info domains must yield different tags for the same inputs, and a Sigma1 MIC
            // must not verify as a Sigma2 MIC.
            if (mic1.AsSpan().SequenceEqual(mic2))
            {
                return "sigma1-equals-sigma2";
            }

            return crypto.VerifySigma2ResumeMic(secret, initiatorRandom, resumptionId, mic1) ? "cross-domain-accepted" : "ok";
        },
        "ok");
    }

    // ---------------------------------------------------------------------
    // Case_Resume_Keys — resumed session-key agreement + salt sensitivity
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> ResumeKeyVectors()
    {
        var crypto = new ManagedCaseCryptoProvider();

        yield return Kat("Case_Resume_Keys", "initiator and responder derive identical keys", () =>
        {
            // Both roles feed the same (sharedSecret, initiatorRandom, newResumptionId) into the schedule,
            // mirroring what CaseServer.TryBuildSigma2Resume and CaseClient.HandleSigma2ResumeAsync do.
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            byte[] initiatorRandom = RandomNumberGenerator.GetBytes(32);
            byte[] newResumptionId = crypto.GenerateResumptionId();

            CaseSessionKeys responder = crypto.DeriveResumedSessionKeys(secret, initiatorRandom, newResumptionId);
            CaseSessionKeys initiator = crypto.DeriveResumedSessionKeys(secret, initiatorRandom, newResumptionId);

            if (responder.I2RKey.Length != 16 || responder.R2IKey.Length != 16 || responder.AttestationChallenge.Length != 16)
            {
                return "unexpected-key-length";
            }

            bool agree = responder.I2RKey.AsSpan().SequenceEqual(initiator.I2RKey) &&
                         responder.R2IKey.AsSpan().SequenceEqual(initiator.R2IKey) &&
                         responder.AttestationChallenge.AsSpan().SequenceEqual(initiator.AttestationChallenge);
            if (!agree)
            {
                return "role-disagreement";
            }

            // The two directional keys must differ from each other.
            return responder.I2RKey.AsSpan().SequenceEqual(responder.R2IKey) ? "i2r-equals-r2i" : "ok";
        },
        "ok");

        yield return Kat("Case_Resume_Keys", "keys change with the fresh resumptionID", () =>
        {
            byte[] secret = RandomNumberGenerator.GetBytes(32);
            byte[] initiatorRandom = RandomNumberGenerator.GetBytes(32);

            CaseSessionKeys a = crypto.DeriveResumedSessionKeys(secret, initiatorRandom, crypto.GenerateResumptionId());
            CaseSessionKeys b = crypto.DeriveResumedSessionKeys(secret, initiatorRandom, crypto.GenerateResumptionId());

            // A rotated resumptionID salts the schedule, so a resumed session gets fresh keys.
            return a.I2RKey.AsSpan().SequenceEqual(b.I2RKey) ? "keys-not-rotated" : "ok";
        },
        "ok");
    }

    // ---------------------------------------------------------------------
    // Case_Resume_Store — per-peer replacement, LRU eviction, resumptionID lookup
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> ResumeStoreVectors()
    {
        yield return Kat("Case_Resume_Store", "lookup by resumptionID and by peer", () =>
        {
            var store = new ManagedCaseResumptionStore();
            var peer = new OperationalPeer(new FabricIndex(1), new NodeId(0x1122334455667788));
            byte[] resumptionId = RandomNumberGenerator.GetBytes(16);
            var record = new CaseResumptionRecord(
                resumptionId, RandomNumberGenerator.GetBytes(32), peer, ReliableMessageProtocolConfig.Default);

            store.Save(record);

            if (!store.TryGetByResumptionId(resumptionId, out var byId) || !byId.ResumptionId.AsSpan().SequenceEqual(resumptionId))
            {
                return "lookup-by-id-failed";
            }

            if (!store.TryGetByPeer(peer, out var byPeer) || byPeer.Peer != peer)
            {
                return "lookup-by-peer-failed";
            }

            // An unknown id must miss.
            return store.TryGetByResumptionId(RandomNumberGenerator.GetBytes(16), out _) ? "unknown-id-hit" : "ok";
        },
        "ok");

        yield return Kat("Case_Resume_Store", "saving a peer twice keeps only the newest record", () =>
        {
            var store = new ManagedCaseResumptionStore();
            var peer = new OperationalPeer(new FabricIndex(1), new NodeId(0x42));

            byte[] oldId = RandomNumberGenerator.GetBytes(16);
            byte[] newId = RandomNumberGenerator.GetBytes(16);
            store.Save(new CaseResumptionRecord(oldId, RandomNumberGenerator.GetBytes(32), peer, ReliableMessageProtocolConfig.Default));
            store.Save(new CaseResumptionRecord(newId, RandomNumberGenerator.GetBytes(32), peer, ReliableMessageProtocolConfig.Default));

            if (store.TryGetByResumptionId(oldId, out _))
            {
                return "stale-record-retained";
            }

            return store.TryGetByPeer(peer, out var current) && current.ResumptionId.AsSpan().SequenceEqual(newId)
                ? "ok"
                : "newest-record-missing";
        },
        "ok");

        yield return Kat("Case_Resume_Store", "least-recently-used record is evicted at capacity", () =>
        {
            var store = new ManagedCaseResumptionStore(capacity: 2);
            var peerA = new OperationalPeer(new FabricIndex(1), new NodeId(0xA));
            var peerB = new OperationalPeer(new FabricIndex(1), new NodeId(0xB));
            var peerC = new OperationalPeer(new FabricIndex(1), new NodeId(0xC));

            byte[] idA = RandomNumberGenerator.GetBytes(16);
            byte[] idB = RandomNumberGenerator.GetBytes(16);
            byte[] idC = RandomNumberGenerator.GetBytes(16);
            store.Save(new CaseResumptionRecord(idA, RandomNumberGenerator.GetBytes(32), peerA, ReliableMessageProtocolConfig.Default));
            store.Save(new CaseResumptionRecord(idB, RandomNumberGenerator.GetBytes(32), peerB, ReliableMessageProtocolConfig.Default));

            // Touch A so B becomes least-recently-used, then insert C to force one eviction.
            store.TryGetByPeer(peerA, out _);
            store.Save(new CaseResumptionRecord(idC, RandomNumberGenerator.GetBytes(32), peerC, ReliableMessageProtocolConfig.Default));

            if (store.TryGetByResumptionId(idB, out _))
            {
                return "lru-record-not-evicted";
            }

            return store.TryGetByResumptionId(idA, out _) && store.TryGetByResumptionId(idC, out _)
                ? "ok"
                : "retained-records-missing";
        },
        "ok");

        yield return Kat("Case_Resume_Store", "removed record no longer resolves", () =>
        {
            var store = new ManagedCaseResumptionStore();
            var peer = new OperationalPeer(new FabricIndex(2), new NodeId(0x99));
            byte[] id = RandomNumberGenerator.GetBytes(16);
            store.Save(new CaseResumptionRecord(id, RandomNumberGenerator.GetBytes(32), peer, ReliableMessageProtocolConfig.Default));

            store.Remove(id);
            return store.TryGetByResumptionId(id, out _) || store.TryGetByPeer(peer, out _) ? "record-still-present" : "ok";
        },
        "ok");
    }

    // ---------------------------------------------------------------------
    // Case_Resume_Vector — optional connectedhomeip wire-compatibility check
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> ResumeSpecVector()
    {
        if (SpecSharedSecret.Length == 0 || SpecInitiatorRandom.Length == 0 || SpecResumptionId.Length == 0)
        {
            yield return new KatResult(
                "Case_Resume_Vector", "spec key schedule", KatStatus.Skip, "no authoritative vector configured");
            yield break;
        }

        var crypto = new ManagedCaseCryptoProvider();
        byte[] secret = FromHex(SpecSharedSecret);
        byte[] initiatorRandom = FromHex(SpecInitiatorRandom);
        byte[] resumptionId = FromHex(SpecResumptionId);

        if (SpecExpectedSigma1Mic.Length != 0)
        {
            yield return Kat("Case_Resume_Vector", "initiatorResumeMIC",
                () => Hex(crypto.ComputeSigma1ResumeMic(secret, initiatorRandom, resumptionId)),
                SpecExpectedSigma1Mic.ToLowerInvariant());
        }

        if (SpecExpectedSigma2Mic.Length != 0)
        {
            yield return Kat("Case_Resume_Vector", "sigma2ResumeMIC",
                () => Hex(crypto.ComputeSigma2ResumeMic(secret, initiatorRandom, resumptionId)),
                SpecExpectedSigma2Mic.ToLowerInvariant());
        }

        if (SpecExpectedI2RKey.Length != 0)
        {
            yield return Kat("Case_Resume_Vector", "resumed I2R key",
                () => Hex(crypto.DeriveResumedSessionKeys(secret, initiatorRandom, resumptionId).I2RKey),
                SpecExpectedI2RKey.ToLowerInvariant());
        }
    }

    // ---------------------------------------------------------------------
    // Harness plumbing (mirrors MatterCryptoKat / MatterDiscoveryKat)
    // ---------------------------------------------------------------------
    private static KatResult Kat(string primitive, string name, Func<string> actual, string expected)
    {
        try
        {
            string got = actual();
            bool ok = FixedTimeStringEquals(got, expected);
            return new KatResult(primitive, name, ok ? KatStatus.Pass : KatStatus.Fail,
                ok ? null : $"expected {expected}, got {got}");
        }
        catch (Exception ex)
        {
            return new KatResult(primitive, name, KatStatus.Fail, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool FixedTimeStringEquals(string a, string b) =>
        a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}