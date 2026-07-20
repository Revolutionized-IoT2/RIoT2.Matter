using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Controller.Commissioning;

/// <summary>The successful outcome of commissioning: the fabric-scoped identity assigned to the node.</summary>
public sealed record CommissioningResult
{
    /// <summary>The operational Node ID assigned to the commissioned node on the controller's fabric.</summary>
    public required NodeId NodeId { get; init; }

    /// <summary>The Fabric ID the node was commissioned onto.</summary>
    public required FabricId FabricId { get; init; }
}

/// <summary>Raised when commissioning fails; carries the stage that failed for diagnostics.</summary>
public sealed class CommissioningException : Exception
{
    public CommissioningException(CommissioningStage stage, string message, Exception? innerException = null)
        : base(message, innerException) => Stage = stage;

    /// <summary>The stage at which commissioning failed.</summary>
    public CommissioningStage Stage { get; }
}