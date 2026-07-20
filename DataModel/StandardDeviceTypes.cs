namespace RIoT2.Matter.DataModel;

/// <summary>
/// Well-known device types from the Matter Device Library, each pairing a device type id with the
/// revision this library conforms to. Applied to <see cref="Device.Endpoint.DeviceTypes"/> and
/// projected by the Descriptor cluster's DeviceTypeList. See the Matter Device Library Specification.
/// </summary>
public static class StandardDeviceTypes
{
    /// <summary>Root Node (0x0016): the utility device type hosted on endpoint 0.</summary>
    public static readonly DeviceType RootNode = new(new DeviceTypeId(0x0016), Revision: 1);

    /// <summary>On/Off Light (0x0100): Identify + On/Off on a lighting endpoint.</summary>
    public static readonly DeviceType OnOffLight = new(new DeviceTypeId(0x0100), Revision: 2);

    /// <summary>Dimmable Light (0x0101): Identify + On/Off + Level Control on a lighting endpoint.</summary>
    public static readonly DeviceType DimmableLight = new(new DeviceTypeId(0x0101), Revision: 2);

    /// <summary>
    /// Control Bridge (0x0840): a controller endpoint that binds to and drives On/Off, Level Control,
    /// and Color Control on other nodes. Hosts Identify + Groups + Binding as servers and declares
    /// On/Off, Level Control, and Color Control as clients. See the Matter Device Library Specification.
    /// </summary>
    public static readonly DeviceType ControlBridge = new(new DeviceTypeId(0x0840), Revision: 2);

    /// <summary>
    /// Aggregator (0x000E): a utility endpoint that exposes one or more bridged devices to the fabric.
    /// Its PartsList enumerates the <see cref="BridgedNode"/> endpoints it aggregates; a commissioner
    /// reads and controls those bridged endpoints as if they were native. Hosts Descriptor (and
    /// optionally Actions). See the Matter Device Library Specification (Aggregator 0x000E) and the
    /// Core Specification, section 9.12 (Bridge for Non-Matter Devices).
    /// </summary>
    public static readonly DeviceType Aggregator = new(new DeviceTypeId(0x000E), Revision: 1);

    /// <summary>
    /// Bridged Node (0x0013): a device bridged into the fabric from a non-Matter ecosystem. Carried
    /// alongside one or more application device types (e.g. On/Off Light) on the same endpoint, it
    /// requires the Bridged Device Basic Information cluster (0x0039) so a commissioner can read the
    /// bridged device's identity and reachability. See the Matter Device Library Specification.
    /// </summary>
    public static readonly DeviceType BridgedNode = new(new DeviceTypeId(0x0013), Revision: 1);
}