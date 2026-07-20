namespace RIoT2.Matter.Controller.Onboarding;

/// <summary>
/// The operational-network credentials a commissioner provisions onto a Wi-Fi or Thread node during
/// the Network Commissioning stage (spec §11.8). Ethernet/on-network nodes are already on the network
/// and need no credentials, so this is optional on <see cref="CommissioningParameters"/>. Exactly one
/// of <see cref="WiFi"/> or <see cref="Thread"/> should be set.
/// </summary>
public sealed record NetworkCredentials
{
    /// <summary>The Wi-Fi credentials to provision, or <see langword="null"/> for a Thread/Ethernet node.</summary>
    public WiFiNetworkCredentials? WiFi { get; init; }

    /// <summary>The Thread credentials to provision, or <see langword="null"/> for a Wi-Fi/Ethernet node.</summary>
    public ThreadNetworkCredentials? Thread { get; init; }

    /// <summary>Redacts the secret material so credentials are never logged in plaintext.</summary>
    public override string ToString() =>
        $"{nameof(NetworkCredentials)} {{ WiFi = {(WiFi is null ? "null" : "(redacted)")}, " +
        $"Thread = {(Thread is null ? "null" : "(redacted)")} }}";
}

/// <summary>
/// Wi-Fi credentials for <c>NetworkCommissioning.AddOrUpdateWiFiNetwork</c> (spec §11.8.7.3). The
/// <see cref="Ssid"/> is used both as the network SSID and, per spec, as the NetworkID for the
/// subsequent <c>ConnectNetwork</c>.
/// </summary>
public sealed record WiFiNetworkCredentials
{
    /// <summary>The Wi-Fi SSID octets (1..32 octets); also serves as the NetworkID for ConnectNetwork.</summary>
    public required byte[] Ssid { get; init; }

    /// <summary>The Wi-Fi credentials (passphrase or PSK) octets (0..64 octets).</summary>
    public required byte[] Credentials { get; init; }

    /// <summary>Redacts the credentials so they are never logged in plaintext.</summary>
    public override string ToString() =>
        $"{nameof(WiFiNetworkCredentials)} {{ Ssid = ({Ssid.Length} octets), Credentials = (redacted) }}";
}

/// <summary>
/// Thread credentials for <c>NetworkCommissioning.AddOrUpdateThreadNetwork</c> (spec §11.8.7.4). The
/// <see cref="OperationalDataset"/> is the Thread Operational Dataset TLV; its Extended PAN ID is used
/// as the NetworkID for the subsequent <c>ConnectNetwork</c>.
/// </summary>
public sealed record ThreadNetworkCredentials
{
    /// <summary>The Thread Operational Dataset TLV octets (1..254 octets).</summary>
    public required byte[] OperationalDataset { get; init; }

    /// <summary>
    /// The Extended PAN ID (8 octets) used as the NetworkID for ConnectNetwork. Must match the
    /// Extended PAN ID encoded in <see cref="OperationalDataset"/>.
    /// </summary>
    public required byte[] ExtendedPanId { get; init; }

    /// <summary>Redacts the dataset so it is never logged in plaintext.</summary>
    public override string ToString() =>
        $"{nameof(ThreadNetworkCredentials)} {{ OperationalDataset = (redacted), ExtendedPanId = ({ExtendedPanId.Length} octets) }}";
}