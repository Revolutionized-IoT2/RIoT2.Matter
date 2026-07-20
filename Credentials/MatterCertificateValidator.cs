using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Credentials;

/// <summary>
/// Validates the non-signature constraints on a decoded Matter certificate: its validity period and
/// the role-dependent BasicConstraints / KeyUsage / ExtendedKeyUsage extensions. The ECDSA signature
/// and chain linkage are checked separately by <see cref="MatterCertificateVerifier"/>. See the
/// Matter Core Specification, sections 6.5.10 and 6.5.11.
/// </summary>
public static class MatterCertificateValidator
{
    /// <summary>
    /// True when <paramref name="now"/> falls within the certificate's validity window. When the current
    /// time is not reliably known (a <paramref name="now"/> preceding the Matter epoch), the check is
    /// skipped and this returns true, since a Node without a trusted time source must not reject a
    /// certificate on validity alone (spec §6.5.10). A <see cref="MatterCertificate.NotAfter"/> of null
    /// encodes the "no well-defined expiration" sentinel.
    /// </summary>
    public static bool IsWithinValidityPeriod(MatterCertificate certificate, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        // A Node lacking a reliable clock (time before the Matter epoch) skips the validity check (spec §6.5.10).
        if (now < MatterEpoch.Epoch)
        {
            return true;
        }

        if (now < certificate.NotBefore)
        {
            return false; // not yet valid.
        }

        return certificate.NotAfter is not { } notAfter || now <= notAfter;
    }

    /// <summary>
    /// True when the certificate's BasicConstraints, KeyUsage, and ExtendedKeyUsage extensions match
    /// those mandated for <paramref name="role"/>. See the Matter Core Specification, section 6.5.11.
    /// </summary>
    public static bool SatisfiesRole(MatterCertificate certificate, MatterCertificateRole role)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        return role switch
        {
            MatterCertificateRole.Root => IsValidCertificateAuthority(certificate, isRoot: true),
            MatterCertificateRole.Intermediate => IsValidCertificateAuthority(certificate, isRoot: false),
            MatterCertificateRole.Node => IsValidNode(certificate),
            _ => false,
        };
    }

    /// <summary>Runs both the validity-period and role-constraint checks for <paramref name="role"/>.</summary>
    public static bool Validate(MatterCertificate certificate, MatterCertificateRole role, DateTimeOffset now)
        => IsWithinValidityPeriod(certificate, now) && SatisfiesRole(certificate, role);

    private static bool IsValidCertificateAuthority(MatterCertificate certificate, bool isRoot)
    {
        var extensions = certificate.Extensions;

        // basic-constraints: a CA certificate MUST assert is-ca (spec §6.5.11.1).
        if (!extensions.IsCertificateAuthority)
        {
            return false;
        }

        // An ICAC's path-length constraint, when present, MUST be 0 — it cannot issue further CAs.
        if (!isRoot && extensions.PathLengthConstraint is { } pathLength && pathLength != 0)
        {
            return false;
        }

        // key-usage: a CA signs certificates (and revocation lists) but is never a signing leaf, so
        // keyCertSign MUST be set and digitalSignature MUST NOT be (spec §6.5.11.2).
        if ((extensions.KeyUsage & MatterCertificateKeyUsage.KeyCertSign) == 0 ||
            (extensions.KeyUsage & MatterCertificateKeyUsage.DigitalSignature) != 0)
        {
            return false;
        }

        // A CA certificate MUST NOT carry an extended-key-usage extension (spec §6.5.11.3).
        return extensions.ExtendedKeyUsage is null || extensions.ExtendedKeyUsage.Count == 0;
    }

    private static bool IsValidNode(MatterCertificate certificate)
    {
        var extensions = certificate.Extensions;

        // basic-constraints: a NOC is a leaf, so is-ca MUST be false and no path length may be set (spec §6.5.11.1).
        if (extensions.IsCertificateAuthority || extensions.PathLengthConstraint is not null)
        {
            return false;
        }

        // key-usage: a NOC signs operational handshakes only, so digitalSignature MUST be set and
        // keyCertSign MUST NOT be (spec §6.5.11.2).
        if ((extensions.KeyUsage & MatterCertificateKeyUsage.DigitalSignature) == 0 ||
            (extensions.KeyUsage & MatterCertificateKeyUsage.KeyCertSign) != 0)
        {
            return false;
        }

        // extended-key-usage: a NOC MUST assert both server-auth and client-auth (spec §6.5.11.3).
        return extensions.ExtendedKeyUsage is { } extendedKeyUsage &&
               extendedKeyUsage.Contains(MatterExtendedKeyUsage.ServerAuth) &&
               extendedKeyUsage.Contains(MatterExtendedKeyUsage.ClientAuth);
    }
}