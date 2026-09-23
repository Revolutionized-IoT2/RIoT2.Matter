using System.Buffers;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
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
    private readonly IReadOnlyCollection<byte[]> _trustedCdSigners;

    /// <param name="trustedPaaCertificates">The DER PAA certificates the DAC/PAI chain must anchor to.</param>
    public DeviceAttestationVerifier(IReadOnlyCollection<byte[]> trustedPaaCertificates,
        IReadOnlyCollection<byte[]> trustedCertificationDeclarationSigners)
    {
        ArgumentNullException.ThrowIfNull(trustedPaaCertificates);
        ArgumentNullException.ThrowIfNull(trustedCertificationDeclarationSigners);
        _trustedPaaCertificates = trustedPaaCertificates.Select(c => c.ToArray()).ToArray();
        _trustedCdSigners = trustedCertificationDeclarationSigners.Select(c => c.ToArray()).ToArray();
    }

    public AttestationVerificationResult Verify(AttestationInformation attestation)
    {
        ArgumentNullException.ThrowIfNull(attestation);
        if (_trustedPaaCertificates.Count == 0 || _trustedCdSigners.Count == 0)
        {
            return AttestationVerificationResult.Fail(
                "Configure both trusted PAA certificates and trusted Certification Declaration signer certificates before commissioning.");
        }

        try
        {
            if (attestation.ExpectedVendorId is not { } vendor || attestation.ExpectedProductId is not { } product ||
                attestation.AttestationNonce.Length != 32 || attestation.AttestationChallenge.Length != 16 ||
                attestation.ProductAttestationIntermediateCertificate is not { Length: > 0 } pai ||
                !VerifyAttestationSignature(attestation))
            {
                return AttestationVerificationResult.Fail("Missing identity/chain material or invalid attestation signature.");
            }

            var elements = AttestationTlv.Structure(attestation.AttestationElements);
            if (!AttestationTlv.Bytes(elements, 2).AsSpan().SequenceEqual(attestation.AttestationNonce) ||
                AttestationTlv.UInt(elements, 3) > uint.MaxValue)
            {
                return AttestationVerificationResult.Fail("Invalid attestation nonce or timestamp.");
            }
            var cd = CertificationDeclarationVerifier.Verify(AttestationTlv.Bytes(elements, 1), _trustedCdSigners);
            if (AttestationTlv.UInt(cd, 1) != vendor || !CertificationDeclarationVerifier.ContainsProduct(cd, product))
            {
                return AttestationVerificationResult.Fail("Certification Declaration does not cover the device VID/PID.");
            }

            using var dac = X509CertificateLoader.LoadCertificate(attestation.DeviceAttestationCertificate);
            var originVendor = cd.ContainsKey(9) ? checked((ushort)AttestationTlv.UInt(cd, 9)) : vendor;
            var originProduct = cd.ContainsKey(10) ? checked((ushort)AttestationTlv.UInt(cd, 10)) : product;
            if (cd.ContainsKey(9) != cd.ContainsKey(10) ||
                AttestationCertificateChainVerifier.ReadDnInteger(dac.SubjectName, AttestationCertificateChainVerifier.VendorIdOid) != originVendor ||
                AttestationCertificateChainVerifier.ReadDnInteger(dac.SubjectName, AttestationCertificateChainVerifier.ProductIdOid) != originProduct)
            {
                return AttestationVerificationResult.Fail("DAC VID/PID does not match the Certification Declaration.");
            }
            foreach (var root in _trustedPaaCertificates)
            {
                using var paa = X509CertificateLoader.LoadCertificate(root);
                if (CertificationDeclarationVerifier.AllowsPaa(cd, paa) &&
                    new AttestationCertificateChainVerifier([root]).Verify(attestation.DeviceAttestationCertificate, pai).IsSuccess)
                {
                    return AttestationVerificationResult.Success;
                }
            }
            return AttestationVerificationResult.Fail("The DAC/PAI chain is not anchored in an authorized trusted PAA.");
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or InvalidOperationException
            or ArgumentException or OverflowException or FormatException or NotSupportedException)
        {
            return AttestationVerificationResult.Fail("Malformed or untrusted attestation material.");
        }
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

}