namespace RIoT2.Matter.Clusters;

/// <summary>
/// The optional-feature bitmap of the Network Commissioning cluster (global attribute FeatureMap):
/// exactly one interface type is exposed per cluster instance. Values match the Matter Core
/// Specification, section 11.8.4 (Feature Map). Only <see cref="EthernetNetworkInterface"/> is
/// implemented today; the Wi-Fi and Thread features, with their scan/add/connect commands, are deferred.
/// </summary>
[Flags]
public enum NetworkCommissioningFeature : uint
{
    /// <summary>No feature.</summary>
    None = 0,

    /// <summary>Wi-Fi related features (WI): SSID-based scan/add/connect. Deferred.</summary>
    WiFiNetworkInterface = 0x1,

    /// <summary>Thread related features (TH): dataset-based scan/add/connect. Deferred.</summary>
    ThreadNetworkInterface = 0x2,

    /// <summary>Ethernet networking (ET): the on-network, command-less path.</summary>
    EthernetNetworkInterface = 0x4,
}