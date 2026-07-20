using RIoT2.Matter.Messaging;

namespace RIoT2.Matter.SecureChannel;

/// <summary>Carries a StatusReport received on a Secure Channel exchange not owned by a handshake.</summary>
public sealed class SecureChannelStatusReportEventArgs : EventArgs
{
    public SecureChannelStatusReportEventArgs(ExchangeContext exchange, SecureChannelStatusReport report)
    {
        Exchange = exchange;
        Report = report;
    }

    /// <summary>The exchange the report arrived on.</summary>
    public ExchangeContext Exchange { get; }

    /// <summary>The parsed StatusReport.</summary>
    public SecureChannelStatusReport Report { get; }
}