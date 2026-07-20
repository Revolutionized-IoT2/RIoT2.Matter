namespace RIoT2.Matter.Onboarding;

/// <summary>
/// The transports over which a device can be discovered for commissioning, encoded as the 8-bit
/// discovery-capabilities (rendezvous information) bitmask of the onboarding payload. See the Matter
/// Core Specification, section 5.1.3.1.
/// </summary>
[Flags]
public enum DiscoveryCapabilities : byte
{
    /// <summary>No capability advertised; a sentinel default, not valid for a real commissionable device.</summary>
    None = 0,

    /// <summary>The device hosts a Soft Access Point for commissioning (bit 0).</summary>
    SoftAccessPoint = 1 << 0,

    /// <summary>The device is discoverable over Bluetooth Low Energy (bit 1).</summary>
    Ble = 1 << 1,

    /// <summary>The device is already on the IP network and discoverable via DNS-SD (bit 2).</summary>
    OnNetwork = 1 << 2,
}