namespace RIoT2.Matter.Controller.Commissioning.Attestation;

/// <summary>
/// Verifies a node's device attestation during commissioning: the DAC→PAI→PAA chain against a
/// trusted-PAA store, the attestation signature over (elements ‖ challenge), the nonce echo, and the
/// Certification Declaration. See the Matter Core Specification, section 6.2.3.
/// </summary>
public interface IDeviceAttestationVerifier
{
    /// <summary>Returns success, or a failure describing why the device must not be commissioned.</summary>
    AttestationVerificationResult Verify(AttestationInformation attestation);
}

/// <summary>The outcome of device-attestation verification.</summary>
public sealed record AttestationVerificationResult(bool IsSuccess, string? FailureReason)
{
    /// <summary>A successful verification.</summary>
    public static AttestationVerificationResult Success { get; } = new(true, null);

    /// <summary>A failure with a diagnostic reason (never a secret).</summary>
    public static AttestationVerificationResult Fail(string reason) => new(false, reason);
}