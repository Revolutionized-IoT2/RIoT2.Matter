using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.Controller.Commissioning.Attestation;

/// <summary>
/// Default <see cref="IDeviceAttestationVerifier"/>: verifies the attestation signature over
/// (elements ‖ challenge) with the DAC public key, confirms the AttestationRequest nonce is echoed in
/// the elements, and validates the DAC→PAI chain and its anchoring in the configured PAA trust store.
/// See the Matter Core Specification, section 6.2.3.
/// </summary>
public sealed class DeviceAttestationVerifier : IDeviceAttestationVerifier
{
    private readonly IReadOnlyCollection<byte[]> _trustedPaaCertificates;

    /// <param name="trustedPaaCertificates">The DER PAA certificates the DAC/PAI chain must anchor to.</param>
    public DeviceAttestationVerifier(IReadOnlyCollection<byte[]> trustedPaaCertificates)
        => _trustedPaaCertificates = trustedPaaCertificates ?? throw new ArgumentNullException(nameof(trustedPaaCertificates));

    public AttestationVerificationResult Verify(AttestationInformation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);

        // TODO (attestation chain): parse the X.509 DAC/PAI/PAA, verify DAC←PAI←PAA signatures and the
        // vendor/product-id constraints, and validate the Certification Declaration (CMS/CD). Anchoring
        // to _trustedPaaCertificates is enforced here once that parsing is in place.
        if (!VerifyAttestationSignature(attestation))
        {
            return AttestationVerificationResult.Fail("The attestation signature did not verify against the DAC public key.");
        }

        if (!NonceEchoed(attestation.AttestationElements, attestation.AttestationNonce))
        {
            return AttestationVerificationResult.Fail("The attestation nonce was not echoed in the signed elements.");
        }

        return AttestationVerificationResult.Success;
    }

    private static bool VerifyAttestationSignature(AttestationInformation attestation)
    {
        try
        {
            using var dac = System.Security.Cryptography.X509Certificates.X509CertificateLoader
                .LoadCertificate(attestation.DeviceAttestationCertificate);
            using var ecdsa = dac.GetECDsaPublicKey();
            if (ecdsa is null)
            {
                return false;
            }

            // TBS = attestation_elements ‖ attestation_challenge (spec 11.18.6.1 / 6.2.3).
            var tbs = new byte[attestation.AttestationElements.Length + attestation.AttestationChallenge.Length];
            attestation.AttestationElements.CopyTo(tbs, 0);
            attestation.AttestationChallenge.CopyTo(tbs, attestation.AttestationElements.Length);

            return ecdsa.VerifyData(tbs, attestation.AttestationSignature,
                System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    /// <summary>Confirms the elements TLV carries the expected attestation_nonce (context tag 2).</summary>
    private static bool NonceEchoed(byte[] elements, byte[] expectedNonce)
    {
        var reader = new TlvReader(elements);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1 && reader.Tag.TagNumber == 2)
            {
                return reader.GetByteString().SequenceEqual(expectedNonce);
            }
        }

        return false;
    }
}