using RIoT2.Matter.InteractionModel;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>Thrown when an Interaction Model interaction fails, carrying the reported status where available.</summary>
public sealed class InteractionModelException : Exception
{
    public InteractionModelException(string message, InteractionModelStatusCode? status = null)
        : base(message) => Status = status;

    /// <summary>The Interaction Model status reported by the peer, when the failure was a status.</summary>
    public InteractionModelStatusCode? Status { get; }
}