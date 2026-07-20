namespace RIoT2.Matter.DataModel;

/// <summary>
/// A device type applied to an endpoint: the device type <see cref="Id"/> from the Matter Device
/// Library and the <see cref="Revision"/> of that definition the endpoint conforms to. The Descriptor
/// cluster projects these as its DeviceTypeList (a DeviceTypeStruct on the wire). See the Matter Core
/// Specification, section 9.5 (Descriptor Cluster).
/// </summary>
public readonly record struct DeviceType(DeviceTypeId Id, ushort Revision);