namespace RIoT2.Matter.Credentials;

/// <summary>The outcome of an attestation certificate chain (DAC → PAI → PAA) verification.</summary>
public sealed record AttestationChainVerificationResult(bool IsSuccess, string? FailureReason)
{
    /// <summary>A successful chain verification.</summary>
    public static AttestationChainVerificationResult Success { get; } = new(true, null);

    /// <summary>A failure with a diagnostic reason (never a secret).</summary>
    public static AttestationChainVerificationResult Fail(string reason) => new(false, reason);
}