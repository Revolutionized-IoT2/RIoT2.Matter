using System.Buffers;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.UserDirectedCommissioning;

/// <summary>
/// An IdentificationDeclaration (<see cref="UserDirectedCommissioningOpcode.IdentificationDeclaration"/>):
/// sent by a commissionee to a discovered commissioner to announce itself and request commissioning. All
/// fields except the instance name are optional and omitted when unset. See the Matter Core Specification,
/// section 5.3.4.1.
/// </summary>
public readonly record struct IdentificationDeclarationMessage
{
    /// <summary>The commissionable instance name the commissionee is advertising via <c>_matterc._udp</c> (field 1).</summary>
    public required string InstanceName { get; init; }

    /// <summary>The commissionee's vendor id (field 2).</summary>
    public VendorId? VendorId { get; init; }

    /// <summary>The commissionee's product id (field 3).</summary>
    public ushort? ProductId { get; init; }

    /// <summary>A human-readable device name (field 4).</summary>
    public string? DeviceName { get; init; }

    /// <summary>The rotating device identifier bytes (field 5).</summary>
    public ReadOnlyMemory<byte>? RotatingId { get; init; }

    /// <summary>The pairing instruction text (field 6).</summary>
    public string? PairingInstruction { get; init; }

    /// <summary>The pairing hint bitmap (field 7).</summary>
    public uint? PairingHint { get; init; }

    /// <summary>Applications the commissionee requests the commissioner make available (field 8).</summary>
    public IReadOnlyList<TargetAppInfo>? TargetAppList { get; init; }

    /// <summary>The commissionee has no onboarding passcode and requests a commissioner-generated one (field 9).</summary>
    public bool NoPasscode { get; init; }

    /// <summary>Requests the commissioner display a Commissioner Declaration once its passcode dialog is shown (field 10).</summary>
    public bool CdUponPasscodeDialog { get; init; }

    /// <summary>The commissioner is to generate and display the setup passcode (field 11).</summary>
    public bool CommissionerPasscode { get; init; }

    /// <summary>The user has entered the commissioner-generated passcode on the commissionee (field 12).</summary>
    public bool CommissionerPasscodeReady { get; init; }

    /// <summary>Requests cancellation of an in-progress commissioner-passcode flow (field 13).</summary>
    public bool CancelPasscode { get; init; }

    /// <summary>Serializes this IdentificationDeclaration into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUtf8String(TlvTag.ContextSpecific(1), InstanceName);

        if (VendorId is { } vendorId) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), vendorId.Value); }
        if (ProductId is { } productId) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), productId); }
        if (DeviceName is { } deviceName) { writer.WriteUtf8String(TlvTag.ContextSpecific(4), deviceName); }
        if (RotatingId is { } rotatingId) { writer.WriteByteString(TlvTag.ContextSpecific(5), rotatingId.Span); }
        if (PairingInstruction is { } pairingInstruction) { writer.WriteUtf8String(TlvTag.ContextSpecific(6), pairingInstruction); }
        if (PairingHint is { } pairingHint) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(7), pairingHint); }

        if (TargetAppList is { Count: > 0 } targetApps)
        {
            writer.StartArray(TlvTag.ContextSpecific(8));
            foreach (var app in targetApps) { app.Encode(writer, TlvTag.Anonymous); }
            writer.EndContainer();
        }

        // Boolean flags are emitted only when set (their absence means false), matching connectedhomeip.
        if (NoPasscode) { writer.WriteBoolean(TlvTag.ContextSpecific(9), true); }
        if (CdUponPasscodeDialog) { writer.WriteBoolean(TlvTag.ContextSpecific(10), true); }
        if (CommissionerPasscode) { writer.WriteBoolean(TlvTag.ContextSpecific(11), true); }
        if (CommissionerPasscodeReady) { writer.WriteBoolean(TlvTag.ContextSpecific(12), true); }
        if (CancelPasscode) { writer.WriteBoolean(TlvTag.ContextSpecific(13), true); }

        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse an IdentificationDeclaration from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out IdentificationDeclarationMessage message)
    {
        string? instanceName = null;
        VendorId? vendorId = null;
        ushort? productId = null;
        string? deviceName = null;
        ReadOnlyMemory<byte>? rotatingId = null;
        string? pairingInstruction = null;
        uint? pairingHint = null;
        List<TargetAppInfo>? targetAppList = null;
        var noPasscode = false;
        var cdUponPasscodeDialog = false;
        var commissionerPasscode = false;
        var commissionerPasscodeReady = false;
        var cancelPasscode = false;

        var reader = new TlvReader(payload);
        if (!reader.Read() || !reader.IsContainer)
        {
            message = default;
            return false;
        }

        while (reader.Read() && !reader.IsEndOfContainer)
        {
            switch (reader.Tag.TagNumber)
            {
                case 1: instanceName = reader.GetUtf8String(); break;
                case 2: vendorId = new VendorId((ushort)reader.GetUnsignedInteger()); break;
                case 3: productId = (ushort)reader.GetUnsignedInteger(); break;
                case 4: deviceName = reader.GetUtf8String(); break;
                case 5: rotatingId = reader.GetByteString().ToArray(); break;
                case 6: pairingInstruction = reader.GetUtf8String(); break;
                case 7: pairingHint = (uint)reader.GetUnsignedInteger(); break;
                case 8:
                    targetAppList = [];
                    while (reader.Read() && !reader.IsEndOfContainer) { targetAppList.Add(TargetAppInfo.Decode(ref reader)); }
                    break;
                case 9: noPasscode = reader.GetBoolean(); break;
                case 10: cdUponPasscodeDialog = reader.GetBoolean(); break;
                case 11: commissionerPasscode = reader.GetBoolean(); break;
                case 12: commissionerPasscodeReady = reader.GetBoolean(); break;
                case 13: cancelPasscode = reader.GetBoolean(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        if (instanceName is null)
        {
            message = default;
            return false;
        }

        message = new IdentificationDeclarationMessage
        {
            InstanceName = instanceName,
            VendorId = vendorId,
            ProductId = productId,
            DeviceName = deviceName,
            RotatingId = rotatingId,
            PairingInstruction = pairingInstruction,
            PairingHint = pairingHint,
            TargetAppList = targetAppList,
            NoPasscode = noPasscode,
            CdUponPasscodeDialog = cdUponPasscodeDialog,
            CommissionerPasscode = commissionerPasscode,
            CommissionerPasscodeReady = commissionerPasscodeReady,
            CancelPasscode = cancelPasscode,
        };
        return true;
    }
}