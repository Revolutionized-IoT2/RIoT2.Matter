using System.Buffers;
using RIoT2.Matter.Tlv;

namespace RIoT2.Matter.UserDirectedCommissioning;

/// <summary>The error/status conveyed by a <see cref="CommissionerDeclarationMessage"/>. See the Matter Core Specification, section 5.3.4.2.</summary>
public enum CommissionerDeclarationError : ushort
{
    /// <summary>No error; the declaration is informational.</summary>
    NoError = 0,

    /// <summary>The commissioner failed to discover the commissionable node.</summary>
    CommissionableDiscoveryFailed = 1,

    /// <summary>The commissioner failed to establish a PASE connection to the commissionee.</summary>
    PaseConnectionFailed = 2,

    /// <summary>PASE authentication failed (an incorrect passcode).</summary>
    PaseAuthFailed = 3,

    /// <summary>A general connection failure occurred.</summary>
    ConnectionFailed = 4,

    /// <summary>The user cancelled the User-Directed Commissioning flow.</summary>
    UdcCancelled = 5,

    /// <summary>The commissioner does not support the commissioner-generated-passcode flow.</summary>
    UdcCommissionerPasscodeNotSupported = 6,

    /// <summary>The received IdentificationDeclaration was malformed or invalid.</summary>
    InvalidIdentificationDeclaration = 7,
}

/// <summary>
/// A CommissionerDeclaration (<see cref="UserDirectedCommissioningOpcode.CommissionerDeclaration"/>): sent
/// by a commissioner back to a commissionee to report status or advertise commissioner-passcode
/// capabilities. All fields are optional; unset booleans are false and an absent error code is
/// <see cref="CommissionerDeclarationError.NoError"/>. See the Matter Core Specification, section 5.3.4.2.
/// </summary>
public readonly record struct CommissionerDeclarationMessage
{
    /// <summary>The error/status code (field 1); omitted on the wire when <see cref="CommissionerDeclarationError.NoError"/>.</summary>
    public CommissionerDeclarationError ErrorCode { get; init; }

    /// <summary>The commissioner needs the commissionee's onboarding passcode to proceed (field 2).</summary>
    public bool NeedsPasscode { get; init; }

    /// <summary>No matching applications were found for the commissionee's target-app list (field 3).</summary>
    public bool NoAppsFound { get; init; }

    /// <summary>The commissioner has displayed its passcode-entry dialog (field 4).</summary>
    public bool PasscodeDialogDisplayed { get; init; }

    /// <summary>The commissioner supports and has generated a commissioner passcode (field 5).</summary>
    public bool CommissionerPasscode { get; init; }

    /// <summary>The commissioner has displayed the onboarding QR code (field 6).</summary>
    public bool QrCodeDisplayed { get; init; }

    /// <summary>Serializes this CommissionerDeclaration into a newly allocated TLV array.</summary>
    public byte[] ToArray()
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);

        writer.StartStructure(TlvTag.Anonymous);

        // Each field is emitted only when meaningful (non-default), matching connectedhomeip.
        if (ErrorCode != CommissionerDeclarationError.NoError) { writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), (ushort)ErrorCode); }
        if (NeedsPasscode) { writer.WriteBoolean(TlvTag.ContextSpecific(2), true); }
        if (NoAppsFound) { writer.WriteBoolean(TlvTag.ContextSpecific(3), true); }
        if (PasscodeDialogDisplayed) { writer.WriteBoolean(TlvTag.ContextSpecific(4), true); }
        if (CommissionerPasscode) { writer.WriteBoolean(TlvTag.ContextSpecific(5), true); }
        if (QrCodeDisplayed) { writer.WriteBoolean(TlvTag.ContextSpecific(6), true); }

        writer.EndContainer();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Attempts to parse a CommissionerDeclaration from <paramref name="payload"/>.</summary>
    public static bool TryParse(ReadOnlySpan<byte> payload, out CommissionerDeclarationMessage message)
    {
        var errorCode = CommissionerDeclarationError.NoError;
        var needsPasscode = false;
        var noAppsFound = false;
        var passcodeDialogDisplayed = false;
        var commissionerPasscode = false;
        var qrCodeDisplayed = false;

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
                case 1: errorCode = (CommissionerDeclarationError)reader.GetUnsignedInteger(); break;
                case 2: needsPasscode = reader.GetBoolean(); break;
                case 3: noAppsFound = reader.GetBoolean(); break;
                case 4: passcodeDialogDisplayed = reader.GetBoolean(); break;
                case 5: commissionerPasscode = reader.GetBoolean(); break;
                case 6: qrCodeDisplayed = reader.GetBoolean(); break;
                default: TlvCopier.Skip(ref reader); break;
            }
        }

        message = new CommissionerDeclarationMessage
        {
            ErrorCode = errorCode,
            NeedsPasscode = needsPasscode,
            NoAppsFound = noAppsFound,
            PasscodeDialogDisplayed = passcodeDialogDisplayed,
            CommissionerPasscode = commissionerPasscode,
            QrCodeDisplayed = qrCodeDisplayed,
        };
        return true;
    }
}