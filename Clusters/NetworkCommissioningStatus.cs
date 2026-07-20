namespace RIoT2.Matter.Clusters;

/// <summary>
/// The result of a Network Commissioning operation, transmitted as <c>enum8</c>. Backs the
/// LastNetworkingStatus attribute and (once the Wi-Fi/Thread features land) the
/// NetworkConfigResponse/ConnectNetworkResponse commands. Values match the Matter Core Specification,
/// section 11.8.5.1 (NetworkCommissioningStatusEnum) and the upstream connectedhomeip enumeration.
/// </summary>
public enum NetworkCommissioningStatus : byte
{
    /// <summary>The operation succeeded.</summary>
    Success = 0,

    /// <summary>A supplied value was outside the valid range.</summary>
    OutOfRange = 1,

    /// <summary>Adding the network would exceed MaxNetworks.</summary>
    BoundsExceeded = 2,

    /// <summary>The referenced NetworkID was not found in the Networks list.</summary>
    NetworkIdNotFound = 3,

    /// <summary>The supplied NetworkID duplicates an existing entry.</summary>
    DuplicateNetworkId = 4,

    /// <summary>The requested network could not be found in range.</summary>
    NetworkNotFound = 5,

    /// <summary>The configuration is not compatible with the regulatory domain.</summary>
    RegulatoryError = 6,

    /// <summary>Authentication to the network failed.</summary>
    AuthFailure = 7,

    /// <summary>The requested security type is not supported.</summary>
    UnsupportedSecurity = 8,

    /// <summary>The connection failed for an unspecified reason.</summary>
    OtherConnectionFailure = 9,

    /// <summary>IPv6 address assignment failed.</summary>
    IPv6Failed = 10,

    /// <summary>Binding to the IP address failed.</summary>
    IPBindFailed = 11,

    /// <summary>An unknown error occurred.</summary>
    UnknownError = 12,
}