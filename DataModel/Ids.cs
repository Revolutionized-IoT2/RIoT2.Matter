namespace RIoT2.Matter.DataModel;

/// <summary>Uniquely identifies a Matter node (device) within a fabric. 64-bit value.</summary>
public readonly record struct NodeId(ulong Value)
{
    public static readonly NodeId Unspecified = new(0);
    public override string ToString() => $"0x{Value:X16}";
}

/// <summary>Identifies an endpoint within a node. Endpoint 0 is the root endpoint.</summary>
public readonly record struct EndpointId(ushort Value)
{
    public static readonly EndpointId Root = new(0);
    public override string ToString() => Value.ToString();
}

/// <summary>Identifies a cluster. Upper 16 bits are the vendor/manufacturer prefix.</summary>
public readonly record struct ClusterId(uint Value)
{
    public override string ToString() => $"0x{Value:X8}";
}

/// <summary>Identifies an attribute within a cluster.</summary>
public readonly record struct AttributeId(uint Value)
{
    public override string ToString() => $"0x{Value:X8}";
}

/// <summary>Identifies a command within a cluster.</summary>
public readonly record struct CommandId(uint Value)
{
    public override string ToString() => $"0x{Value:X8}";
}

/// <summary>Identifies an event within a cluster.</summary>
public readonly record struct EventId(uint Value)
{
    public override string ToString() => $"0x{Value:X8}";
}

/// <summary>Identifies a device type as defined by the Matter Device Library.</summary>
public readonly record struct DeviceTypeId(uint Value)
{
    public override string ToString() => $"0x{Value:X8}";
}

/// <summary>Identifies a group of nodes. 16-bit value.</summary>
public readonly record struct GroupId(ushort Value)
{
    public override string ToString() => $"0x{Value:X4}";
}

/// <summary>A local index identifying a fabric the node is a member of.</summary>
public readonly record struct FabricIndex(byte Value)
{
    public static readonly FabricIndex NoFabric = new(0);
    public override string ToString() => Value.ToString();
}

/// <summary>
/// Identifies a fabric by its 64-bit Fabric ID (unique within the scope of a root CA). Distinct
/// from <see cref="FabricIndex"/>, which is a node-local index. See the Matter Core Specification,
/// section 2.5.1.
/// </summary>
public readonly record struct FabricId(ulong Value)
{
    public override string ToString() => $"0x{Value:X16}";
}

/// <summary>
/// The 64-bit Compressed Fabric Identifier, derived from the fabric root public key and Fabric ID
/// via HKDF and used for operational service discovery. See the Matter Core Specification,
/// section 4.3.2.2.
/// </summary>
public readonly record struct CompressedFabricId(ulong Value)
{
    public override string ToString() => $"{Value:X16}";
}

/// <summary>A CSA-assigned vendor identifier. 16-bit value.</summary>
public readonly record struct VendorId(ushort Value)
{
    public override string ToString() => $"0x{Value:X4}";
}