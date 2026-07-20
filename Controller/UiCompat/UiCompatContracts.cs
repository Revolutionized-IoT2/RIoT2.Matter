using System.Text.Json;
using System.Text.Json.Serialization;
using RIoT2.Matter.Controller.Onboarding;

namespace RIoT2.Matter.Controller.UiCompat;

// These records serialize to the exact JSON the UI's services/backend/types.ts expects.
// System.Text.Json camelCases by default in ASP.NET Core, so property names line up.

internal record UiDeviceSummary(
    string NodeId, string Name, string? VendorName, string? ProductName, string Reachability);

internal sealed record UiClusterInfo(
    long ClusterId, string? ClusterName, IReadOnlyDictionary<string, object?> Attributes);

internal sealed record UiEndpointInfo(
    int EndpointId, string? DeviceType, IReadOnlyList<UiClusterInfo> Clusters);

internal sealed record UiDeviceDetail(
    string NodeId, string Name, string? VendorName, string? ProductName, string Reachability,
    int? VendorId, int? ProductId, string? SerialNumber, string? SoftwareVersion,
    IReadOnlyList<UiEndpointInfo> Endpoints)
    : UiDeviceSummary(NodeId, Name, VendorName, ProductName, Reachability);

internal sealed record UiDiscoveredDevice(
    int Discriminator, int? VendorId, int? ProductId, string? DeviceName, string Transport, string InstanceName);

internal sealed record UiOnboardingPayload(string Kind, string? Value, string? PairingCode);

internal sealed record UiNetworkCredentials(string Kind, string? Ssid, string? Password, string? DatasetHex)
{
    /// <summary>
    /// Maps the UI's plain credentials onto the backend's byte-based NetworkCredentials. SSID and
    /// password are UTF-8 encoded. For Thread, the Extended PAN ID is extracted from the operational
    /// dataset TLV (Thread MeshCoP type 0x02) so it matches the dataset, as the backend requires.
    /// </summary>
    public NetworkCredentials ToCredentials() => Kind switch
    {
        "wifi" => new NetworkCredentials
        {
            WiFi = new WiFiNetworkCredentials
            {
                Ssid = System.Text.Encoding.UTF8.GetBytes(Ssid ?? string.Empty),
                Credentials = System.Text.Encoding.UTF8.GetBytes(Password ?? string.Empty),
            },
        },
        "thread" => BuildThread(Convert.FromHexString(DatasetHex ?? string.Empty)),
        _ => new NetworkCredentials(),
    };

    private static NetworkCredentials BuildThread(byte[] dataset) => new()
    {
        Thread = new ThreadNetworkCredentials
        {
            OperationalDataset = dataset,
            ExtendedPanId = ExtractExtendedPanId(dataset),
        },
    };

    /// <summary>
    /// Extracts the 8-octet Extended PAN ID from a Thread Operational Dataset TLV. Thread MeshCoP TLVs
    /// are [type:1][length:1][value:length]; the Extended PAN ID is type 0x02. Throws when absent so a
    /// malformed dataset fails fast rather than being sent with an empty/incorrect NetworkID.
    /// </summary>
    private static byte[] ExtractExtendedPanId(ReadOnlySpan<byte> dataset)
    {
        const byte ExtendedPanIdType = 0x02;
        var offset = 0;
        while (offset + 2 <= dataset.Length)
        {
            var type = dataset[offset];
            var length = dataset[offset + 1];
            var valueStart = offset + 2;
            if (valueStart + length > dataset.Length)
            {
                break;
            }

            if (type == ExtendedPanIdType && length == 8)
            {
                return dataset.Slice(valueStart, length).ToArray();
            }

            offset = valueStart + length;
        }

        throw new ArgumentException("The Thread operational dataset does not contain an 8-octet Extended PAN ID (TLV type 0x02).");
    }
}

internal sealed record UiCommissionRequest(
    UiOnboardingPayload Onboarding, UiNetworkCredentials? Network, string? InstanceName);

internal sealed record UiCommissioningResult(string NodeId, bool Succeeded, string? Error);

internal sealed record UiAttributePath(string NodeId, int EndpointId, long ClusterId, long AttributeId);

internal sealed record UiCommandPath(string NodeId, int EndpointId, long ClusterId, long CommandId);

internal sealed record UiWriteAttributeRequest(UiAttributePath Path, JsonElement? Value);

internal sealed record UiInvokeCommandRequest(UiCommandPath Path, JsonElement? Payload);

public sealed record UiBackendEvent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("nodeId")] string? NodeId,
    [property: JsonPropertyName("payload")] object? Payload,
    [property: JsonPropertyName("timestamp")] string Timestamp);