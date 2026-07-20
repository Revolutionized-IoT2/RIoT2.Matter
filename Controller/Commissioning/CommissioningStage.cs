namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>The ordered stages of the Matter commissioning flow, reported for progress. See the Matter Core Specification, section 5.5.</summary>
public enum CommissioningStage
{
    /// <summary>Establishing the PASE session over the setup passcode.</summary>
    EstablishingPase,

    /// <summary>Arming the fail-safe timer on the node.</summary>
    ArmingFailSafe,

    /// <summary>Requesting and verifying device attestation (DAC/PAI/CD).</summary>
    VerifyingAttestation,

    /// <summary>Requesting the node's CSR and issuing its operational certificate.</summary>
    IssuingOperationalCredentials,

    /// <summary>Installing the trusted root and NOC on the node.</summary>
    InstallingCredentials,

    /// <summary>Configuring the operational network (skipped when already on-network).</summary>
    ConfiguringNetwork,

    /// <summary>Discovering the node on its operational address and establishing CASE.</summary>
    EstablishingCase,

    /// <summary>Sending CommissioningComplete and committing the fabric.</summary>
    Completing,

    /// <summary>Commissioning finished successfully.</summary>
    Completed,
}