using System.Diagnostics;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace RIoT2.Matter.Crypto.Kat;

/// <summary>
/// Known-Answer Test (KAT) harness for the Matter cryptographic primitives
/// (Matter Core Specification §3.6–3.8, §3.10). Every test drives the
/// <see cref="MatterCrypto"/> facade / <see cref="Spake2Plus"/> so the harness
/// guards the real implementation rather than the platform.
/// <list type="bullet">
///   <item><description><c>Crypto_Hash</c>  = SHA-256 (FIPS 180-4)</description></item>
///   <item><description><c>Crypto_HMAC</c>  = HMAC-SHA-256 (RFC 4231)</description></item>
///   <item><description><c>Crypto_HKDF</c>  = HKDF-SHA-256 (RFC 5869)</description></item>
///   <item><description><c>Crypto_PBKDF</c> = PBKDF2-HMAC-SHA-256 (RFC 7914)</description></item>
///   <item><description><c>Crypto_AEAD</c>  = AES-128-CCM (RFC 3610 + Matter params)</description></item>
///   <item><description><c>Crypto_ECDH/Sign/Verify</c> = NIST P-256 (functional)</description></item>
///   <item><description><c>Crypto_PAKE</c>  = SPAKE2+ over P-256 (functional + spec-vector hook)</description></item>
/// </list>
/// </summary>
public static class MatterCryptoKat
{
    // --- Matter Core Spec Crypto_PAKE fixed vectors -------------------------
    // Leave blank to SKIP; paste authoritative bytes from the spec appendix to
    // turn the test into a wire-compatibility check.
    private const string SpecContext = "";
    private const string SpecIdProver = "";
    private const string SpecIdVerifier = "";
    private const string SpecW0 = "";          // 32-byte hex scalar
    private const string SpecW1 = "";          // 32-byte hex scalar
    private const string SpecX = "";           // prover random scalar x (32-byte hex)
    private const string SpecY = "";           // verifier random scalar y (32-byte hex)
    private const string SpecExpectedX = "";   // expected X share (65-byte uncompressed hex)
    private const string SpecExpectedY = "";   // expected Y share (65-byte uncompressed hex)
    private const string SpecExpectedKe = "";  // expected shared secret Ke (hex)

    /// <summary>Runs every KAT and returns an aggregate report.</summary>
    public static KatReport Run(TextWriter? log = null)
    {
        log ??= TextWriter.Null;
        var sw = Stopwatch.StartNew();

        var results = new List<KatResult>();
        results.AddRange(Sha256Vectors());
        results.AddRange(HmacSha256Vectors());
        results.AddRange(HkdfSha256Vectors());
        results.AddRange(Pbkdf2Sha256Vectors());
        results.AddRange(AesCcmVectors());
        results.AddRange(P256Vectors());
        results.AddRange(Spake2PlusVectors());

        sw.Stop();
        var report = new KatReport(results, sw.Elapsed);

        log.WriteLine("Matter cryptographic Known-Answer Tests");
        log.WriteLine("=======================================");
        foreach (KatResult r in results)
        {
            string tag = r.Status switch
            {
                KatStatus.Pass => "PASS",
                KatStatus.Fail => "FAIL",
                _ => "SKIP"
            };
            log.WriteLine($"[{tag}] {r.Primitive,-13} {r.Name}");
            if (r.Detail is not null && r.Status != KatStatus.Pass)
                log.WriteLine($"         {r.Detail}");
        }
        log.WriteLine("---------------------------------------");
        log.WriteLine($"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped " +
                      $"({report.Elapsed.TotalMilliseconds:F1} ms).");

        return report;
    }

    // ---------------------------------------------------------------------
    // Crypto_Hash — SHA-256 (FIPS 180-4)
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> Sha256Vectors()
    {
        yield return Kat("Crypto_Hash", "SHA-256(\"abc\")",
            () => Hex(MatterCrypto.Hash("abc"u8)),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");

        yield return Kat("Crypto_Hash", "SHA-256(\"\")",
            () => Hex(MatterCrypto.Hash([])),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    // ---------------------------------------------------------------------
    // Crypto_HMAC — HMAC-SHA-256 (RFC 4231)
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> HmacSha256Vectors()
    {
        yield return Kat("Crypto_HMAC", "RFC 4231 TC1",
            () => Hex(MatterCrypto.Hmac(FromHex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"), "Hi There"u8)),
            "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7");

        yield return Kat("Crypto_HMAC", "RFC 4231 TC2",
            () => Hex(MatterCrypto.Hmac("Jefe"u8, "what do ya want for nothing?"u8)),
            "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843");
    }

    // ---------------------------------------------------------------------
    // Crypto_HKDF — HKDF-SHA-256 (RFC 5869)
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> HkdfSha256Vectors()
    {
        yield return Kat("Crypto_HKDF", "RFC 5869 TC1",
            () => Hex(MatterCrypto.Hkdf(
                FromHex("0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b"),
                FromHex("000102030405060708090a0b0c"),
                FromHex("f0f1f2f3f4f5f6f7f8f9"),
                42)),
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865");
    }

    // ---------------------------------------------------------------------
    // Crypto_PBKDF — PBKDF2-HMAC-SHA-256 (RFC 7914 §11)
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> Pbkdf2Sha256Vectors()
    {
        yield return Kat("Crypto_PBKDF", "c=1",
            () => Hex(MatterCrypto.Pbkdf("password"u8, "salt"u8, 1, 32)),
            "120fb6cffcf8b32c43e7225256c4f837a86548c92ccc35480805987cb70be17b");

        yield return Kat("Crypto_PBKDF", "c=4096",
            () => Hex(MatterCrypto.Pbkdf("password"u8, "salt"u8, 4096, 32)),
            "c5e478d59288c841aa530db6845c4c8d962893a001ce4e11a4963873aa98134a");
    }

    // ---------------------------------------------------------------------
    // Crypto_AEAD — AES-128-CCM
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> AesCcmVectors()
    {
        yield return Kat("Crypto_AEAD", "RFC 3610 PV#1", () =>
        {
            byte[] pt = FromHex("08090a0b0c0d0e0f101112131415161718191a1b1c1d1e");
            byte[] ct = new byte[pt.Length];
            byte[] tag = new byte[8]; // RFC 3610 PV#1 uses an 8-byte MIC
            MatterCrypto.AeadEncrypt(
                FromHex("c0c1c2c3c4c5c6c7c8c9cacbcccdcecf"),
                FromHex("00000003020100a0a1a2a3a4a5"),
                pt, FromHex("0001020304050607"), ct, tag);
            return Hex(ct) + Hex(tag);
        },
        "588c979a61c663d2f066d0c2c0f989806d5f6b61dac38417e8d12cfdf926e0");

        yield return Kat("Crypto_AEAD", "Matter params round-trip", () =>
        {
            byte[] key = RandomNumberGenerator.GetBytes(MatterCrypto.SymmetricKeyLengthBytes);
            byte[] nonce = RandomNumberGenerator.GetBytes(MatterCrypto.AeadNonceLengthBytes);
            byte[] aad = "matter-aead"u8.ToArray();
            byte[] pt = RandomNumberGenerator.GetBytes(64);
            byte[] ct = new byte[pt.Length];
            byte[] tag = new byte[MatterCrypto.AeadMicLengthBytes];
            byte[] rt = new byte[pt.Length];

            MatterCrypto.AeadEncrypt(key, nonce, pt, aad, ct, tag);
            if (!MatterCrypto.AeadDecrypt(key, nonce, ct, tag, aad, rt) || !pt.AsSpan().SequenceEqual(rt))
                return "round-trip-failed";

            tag[0] ^= 0x01; // tamper => must be rejected
            return MatterCrypto.AeadDecrypt(key, nonce, ct, tag, aad, rt) ? "tamper-not-detected" : "ok";
        },
        "ok");
    }

    // ---------------------------------------------------------------------
    // Crypto_ECDH / Crypto_Sign / Crypto_Verify — NIST P-256 (functional)
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> P256Vectors()
    {
        yield return Kat("Crypto_ECDH", "P-256 agreement", () =>
        {
            using ECDiffieHellman a = MatterCrypto.CreateAgreementKey();
            using ECDiffieHellman b = MatterCrypto.CreateAgreementKey();
            byte[] ab = MatterCrypto.Ecdh(a, b.PublicKey);
            byte[] ba = MatterCrypto.Ecdh(b, a.PublicKey);
            return ab.AsSpan().SequenceEqual(ba) && ab.Length == 32 ? "ok" : "mismatch";
        },
        "ok");

        yield return Kat("Crypto_Sign", "P-256 ECDSA sign/verify", () =>
        {
            using ECDsa signer = MatterCrypto.CreateSigningKey();
            byte[] msg = "matter-attestation"u8.ToArray();
            byte[] sig = MatterCrypto.Sign(signer, msg);
            if (!MatterCrypto.Verify(signer, msg, sig)) return "valid-signature-rejected";

            sig[0] ^= 0x01;
            return MatterCrypto.Verify(signer, msg, sig) ? "tampered-signature-accepted" : "ok";
        },
        "ok");
    }

    // ---------------------------------------------------------------------
    // Crypto_PAKE — SPAKE2+ over P-256
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> Spake2PlusVectors()
    {
        // Fixed: the constant points M and N must decompress to on-curve points
        // and re-compress to the authoritative SEC1 encodings.
        yield return Kat("Crypto_PAKE", "M/N on curve",
            () => P256.IsOnCurve(Spake2Plus.PointM) && P256.IsOnCurve(Spake2Plus.PointN) ? "ok" : "off-curve",
            "ok");

        yield return Kat("Crypto_PAKE", "M constant codec",
            () => Hex(P256.EncodeCompressed(Spake2Plus.PointM)),
            "02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f");

        yield return Kat("Crypto_PAKE", "N constant codec",
            () => Hex(P256.EncodeCompressed(Spake2Plus.PointN)),
            "03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49");

        // Functional: prover and verifier must derive matching Ke/cA/cB, and a
        // wrong passcode MUST diverge (proves secret-binding, not just agreement).
        yield return Kat("Crypto_PAKE", "SPAKE2+ round-trip", () =>
        {
            byte[] context = "RIoT2-KAT SPAKE2+"u8.ToArray();
            byte[] idA = "commissioner"u8.ToArray();
            byte[] idB = "commissionee"u8.ToArray();
            byte[] salt = "SPAKE2P Key Salt"u8.ToArray();
            const int iterations = 1000;

            var (w0, w1) = Spake2Plus.DeriveW0W1("20202021"u8, salt, iterations);
            ECP l = Spake2Plus.ComputeL(w1);
            BigInteger x = Spake2Plus.RandomScalar();
            BigInteger y = Spake2Plus.RandomScalar();
            ECP shareX = Spake2Plus.ProverShare(x, w0);
            ECP shareY = Spake2Plus.VerifierShare(y, w0);

            var prover = Spake2Plus.ProverFinish(x, w0, w1, shareX, shareY, context, idA, idB);
            var verifier = Spake2Plus.VerifierFinish(y, w0, l, shareX, shareY, context, idA, idB);

            bool agree = prover.Ke.AsSpan().SequenceEqual(verifier.Ke)
                      && prover.Ca.AsSpan().SequenceEqual(verifier.Ca)
                      && prover.Cb.AsSpan().SequenceEqual(verifier.Cb);
            if (!agree) return "prover/verifier-disagreement";
            if (prover.Ke.Length != 16) return "unexpected-Ke-length";

            var (w0Bad, w1Bad) = Spake2Plus.DeriveW0W1("99999999"u8, salt, iterations);
            ECP lBad = Spake2Plus.ComputeL(w1Bad);
            ECP shareYBad = Spake2Plus.VerifierShare(y, w0Bad);
            var attacker = Spake2Plus.VerifierFinish(y, w0Bad, lBad, shareX, shareYBad, context, idA, idB);

            return prover.Ke.AsSpan().SequenceEqual(attacker.Ke) ? "wrong-secret-not-rejected" : "ok";
        },
        "ok");

        // Fixed spec vector (wire compatibility) — skipped until authoritative bytes are pasted above.
        yield return Spake2PlusSpecVectorOrSkip();
    }

    private static KatResult Spake2PlusSpecVectorOrSkip()
    {
        const string name = "SPAKE2+ spec fixed vector";
        if (SpecW0.Length == 0 || SpecExpectedKe.Length == 0)
            return new KatResult("Crypto_PAKE", name, KatStatus.Skip,
                "paste authoritative Crypto_PAKE vectors from the Matter Core Spec appendix into the Spec* " +
                "constants to enable wire-compatibility validation");

        return Kat("Crypto_PAKE", name, () =>
        {
            byte[] context = Encoding.ASCII.GetBytes(SpecContext);
            byte[] idA = Encoding.ASCII.GetBytes(SpecIdProver);
            byte[] idB = Encoding.ASCII.GetBytes(SpecIdVerifier);
            BigInteger w0 = ScalarFromHex(SpecW0);
            BigInteger w1 = ScalarFromHex(SpecW1);
            BigInteger x = ScalarFromHex(SpecX);
            BigInteger y = ScalarFromHex(SpecY);

            ECP l = Spake2Plus.ComputeL(w1);
            ECP shareX = Spake2Plus.ProverShare(x, w0);
            ECP shareY = Spake2Plus.VerifierShare(y, w0);

            if (!Hex(P256.EncodeUncompressed(shareX)).Equals(SpecExpectedX, StringComparison.OrdinalIgnoreCase))
                return "X-mismatch";
            if (!Hex(P256.EncodeUncompressed(shareY)).Equals(SpecExpectedY, StringComparison.OrdinalIgnoreCase))
                return "Y-mismatch";

            var prover = Spake2Plus.ProverFinish(x, w0, w1, shareX, shareY, context, idA, idB);
            var verifier = Spake2Plus.VerifierFinish(y, w0, l, shareX, shareY, context, idA, idB);
            if (!prover.Ke.AsSpan().SequenceEqual(verifier.Ke)) return "Ke-disagreement";

            return Hex(prover.Ke);
        },
        SpecExpectedKe.ToLowerInvariant());
    }

    // ---------------------------------------------------------------------
    // Harness plumbing
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

    private static BigInteger ScalarFromHex(string hex) =>
        new(Convert.FromHexString(hex), isUnsigned: true, isBigEndian: true);

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>Outcome of a single known-answer test.</summary>
public enum KatStatus
{
    Pass,
    Fail,
    Skip
}

/// <summary>Result of a single known-answer test.</summary>
public sealed record KatResult(string Primitive, string Name, KatStatus Status, string? Detail)
{
    public bool Passed => Status == KatStatus.Pass;
}

/// <summary>Aggregate outcome of a KAT run.</summary>
public sealed class KatReport(IReadOnlyList<KatResult> results, TimeSpan elapsed)
{
    public IReadOnlyList<KatResult> Results { get; } = results;
    public TimeSpan Elapsed { get; } = elapsed;

    public int Total => Results.Count;
    public int Passed => Results.Count(r => r.Status == KatStatus.Pass);
    public int Failed => Results.Count(r => r.Status == KatStatus.Fail);
    public int Skipped => Results.Count(r => r.Status == KatStatus.Skip);

    /// <summary><see langword="true"/> when nothing failed (skips are allowed).</summary>
    public bool AllPassed => Failed == 0;
}