using RIoT2.Matter.Clusters;
using RIoT2.Matter.Credentials;

namespace RIoT2.Matter.OnOffSample;

/// <summary>
/// The sample's DEMO device attestation material. This is a thin binding of the library's
/// <see cref="TestAttestationFactory"/> to the sample's identifiers; the certificate-minting logic
/// (PAI/DAC off the CHIP test PAA, plus a matching Certification Declaration) lives in the library so
/// there is a single implementation shared with the RIoT Orchestrator's Matter bridge.
/// </summary>
/// <remarks>
/// The chain roots at the CSA test PAA, so a production controller still rejects it unless this
/// VID/PID pair is registered as a developer project with that ecosystem. See the Matter Core
/// Specification, section 6.2 (Device Attestation).
/// </remarks>
internal static class SampleAttestation
{
    // Must match the VendorId/ProductId advertised in Program.cs (CSA test vendor 0xFFF2).
    private const int VendorId = 0xFFF2;
    private const int ProductId = 0x8001;
    private const int DeviceTypeId = 0x0100; // On/Off Light

    /// <summary>
    /// Loads the sample's DAC/PAI/CD + signer, minting and persisting them under <c>credentials/</c>
    /// on first run. Verification diagnostics are written to the console.
    /// </summary>
    public static DeviceAttestationCredentials Load()
        => TestAttestationFactory.Create(new TestAttestationOptions
        {
            VendorId = VendorId,
            ProductId = ProductId,
            DeviceTypeId = DeviceTypeId,
            OutputDirectory = "credentials",
            Diagnostics = message => Console.WriteLine($"[attestation] {message}"),
        });
}
