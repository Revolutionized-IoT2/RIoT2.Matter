using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Hosting;
using RIoT2.Matter.Onboarding;
using RIoT2.Matter.SecureChannel.Pase;

namespace RIoT2.Matter.OnOffSample;

/// <summary>
/// Demonstrates the RIoT2.Matter stack by hosting an On/Off Light that can be commissioned to
/// Google Home. Prints the onboarding QR, then lets the operator toggle the light from the console
/// while any connected Matter controller sees the same state (and vice versa).
/// </summary>
internal static class Program
{
    // A 12-bit setup discriminator; must match the value advertised over DNS-SD.
    private const ushort Discriminator = 0x0F00;

    // Set while a console command is mutating OnOff so the OnOffChanged handler
    // can attribute the change to the console rather than a Matter controller.
    private static bool _consoleDrivenChange;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 1. Provision the onboarding secret ONCE. The passcode the user scans and the on-device
        //    SPAKE2+ verifier come from the same bundle, so they can never diverge.
        PaseProvisioning provisioning = PaseVerifierGenerator.Provision();

        // 2. Compose an On/Off Light node (root endpoint + commissioning stack + lighting endpoint).
        var options = new LightingDeviceOptions
        {
            Information = new DeviceInformation
            {
                VendorId = new VendorId(0xFFF2),      // CSA test vendor id
                ProductId = 0x8001,                   // matches the connectedhomeip FFF2-8001 test DAC/PAI/CD
                VendorName = "RIoT2",
                ProductName = "Demo On/Off Light",
                SoftwareVersion = 1,
                SoftwareVersionString = "1.0.0",
                SerialNumber = "RIOT2-ONOFF-0001",
            },
            Attestation = SampleAttestation.Load(),
            BasicCommissioningInfo = new BasicCommissioningInfo(
                FailSafeExpiryLengthSeconds: 60,
                MaxCumulativeFailsafeSeconds: 900),
            NetworkInterfaces =
            [
                new NetworkInterface { Name = "WiFi", IsOperational = true, Type = InterfaceType.WiFi },
            ],
            Profile = LightingProfile.OnOffLight,     // On/Off Light (0x0100), no Level Control
            NodeLabel = "RIoT2 Demo Light",
            InitialOnOff = false,
        };

        using var device = LightingDevice.Build(options);

        // 2a. Persist commissioned fabrics so this node keeps its identity across restarts. Attach right
        //     after Build, while the manager is still empty: Restore() re-seeds the fabric table (and, via
        //     the manager's Changed event, the ACL and group keys), then every subsequent change is written
        //     back to disk. This is fabric *identity* persistence only; CASE resumption is separate and is
        //     handled in-process by the host. The seal key MUST be reproducible and device-bound; in
        //     production derive it from a TPM/secure-element sealed value rather than the serial number.
        string fabricsPath = Path.Combine(AppContext.BaseDirectory, "fabrics.dat");
        string fabricKeyPassword = DeviceBoundSecret(options.Information.SerialNumber);

        using var persistence = FileFabricPersistence.Attach(
            device.Commissioning.Manager,
            path: fabricsPath,
            keyPassword: fabricKeyPassword);

        // 3. Build the onboarding payload + QR string from the SAME passcode used for the verifier.
        var payload = new SetupPayload
        {
            VendorId = options.Information.VendorId,
            ProductId = options.Information.ProductId,
            DiscoveryCapabilities = DiscoveryCapabilities.OnNetwork, // Ethernet/Wi-Fi on-network commissioning
            Discriminator = Discriminator,
            Passcode = provisioning.Passcode,
        };
        string qrPayload = QrCodePayload.Encode(payload); // e.g. "MT:Y.K90..."
        string manualCode = ManualPairingCode.Encode(payload); // 11-digit fallback for manual entry

        // 4. Describe how this node advertises itself for commissioning. The Discriminator MUST match the
        //    QR payload so a controller that scanned the code resolves this instance; the host manages the
        //    commissioning Mode from the window state, so it is left Disabled here.
        var commissionable = new CommissionableServiceInfo
        {
            InstanceId = StableInstanceId(options.Information.SerialNumber),
            Discriminator = Discriminator,
            Mode = CommissioningMode.Disabled,
            VendorId = options.Information.VendorId,
            ProductId = options.Information.ProductId,
            DeviceType = StandardDeviceTypes.OnOffLight.Id,
            DeviceName = options.NodeLabel,
        };

        // 5. Start the host: transport, sessions, Secure Channel (PASE/CASE), Interaction Model, DNS-SD.
        //    The host owns an in-process CASE resumption store, so an already-commissioned controller can
        //    resume a prior session via Sigma2_Resume (spec §4.14.2.6) without any wiring here.
        //    A stable, serial-derived host id keeps the <id>.local host name constant across restarts.
        await using var host = new MatterNodeHost(
            device.Node,
            device.Commissioning,
            provisioning,
            commissionable,
            hostId: StableInstanceId($"host:{options.Information.SerialNumber}"));
        using var lifetime = new CancellationTokenSource();
        await host.StartAsync(lifetime.Token);

        PrintOnboarding(qrPayload, manualCode, provisioning.Passcode, Discriminator);

        // 6. Monitor: OnOffChanged fires for BOTH console-driven and controller-driven changes.
        //    Use a flag to distinguish the origin so each path is logged separately.
        device.OnOff.OnOffChanged += (_, _) =>
        {
            if (_consoleDrivenChange)
            {
                RenderState(device.OnOff.OnOff, "console");
            }
            else
            {
                RenderState(device.OnOff.OnOff, "controller");
            }
        };
        RenderState(device.OnOff.OnOff, "initial");

        // 7. Control from the console.
        PrintHelp();
        await RunConsoleLoopAsync(device, lifetime);
        return 0;
    }

    private static async Task RunConsoleLoopAsync(LightingDevice device, CancellationTokenSource lifetime)
    {
        while (!lifetime.IsCancellationRequested)
        {
            if (!Console.KeyAvailable)
            {
                await Task.Delay(50).ConfigureAwait(false);
                continue;
            }

            switch (char.ToLowerInvariant(Console.ReadKey(intercept: true).KeyChar))
            {
                case 't':
                    // Push a change into the model → notifies live subscriptions → raises OnOffChanged.
                    SetOnOffFromConsole(device, !device.OnOff.OnOff);
                    break;
                case 'o':
                    SetOnOffFromConsole(device, true);
                    break;
                case 'f':
                    SetOnOffFromConsole(device, false);
                    break;
                case 's':
                    RenderState(device.OnOff.OnOff, "query");
                    break;
                case 'h':
                    PrintHelp();
                    break;
                case 'q':
                    lifetime.Cancel();
                    break;
                case 'r':
                    // Reopen a fresh commissioning window so the node re-advertises _matterc._udp
                    // without restarting the process (the initial 900 s window has expired).
                    device.Commissioning.AdministratorCommissioning.OpenBasicWindow(
                        900, FabricIndex.NoFabric, adminVendor: null);
                    Console.WriteLine("Commissioning window reopened for 900 s.");
                    break;
            }
        }

        Console.WriteLine("Shutting down…");
    }

    private static void RenderState(bool on, string source) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] OnOff = {(on ? "ON " : "OFF")}  (changed by: {source})");

    private static void PrintOnboarding(string qrPayload, string manualCode, SetupPasscode passcode, ushort discriminator)
    {
        Console.WriteLine();
        Console.WriteLine("=== Commission this On/Off Light to Google Home ===");
        Console.WriteLine("Scan the QR below in the Google Home app (Add device → Matter):");
        Console.WriteLine();
        Console.WriteLine(ConsoleQr.Render(qrPayload));
        Console.WriteLine($"QR payload    : {qrPayload}");
        Console.WriteLine($"Manual code   : {ManualPairingCode.Format(manualCode)}");
        Console.WriteLine($"Setup passcode: {passcode.Value:00000000}");
        Console.WriteLine($"Discriminator : 0x{discriminator:X3} ({discriminator})");
        Console.WriteLine();
    }

    private static void PrintHelp() =>
        Console.WriteLine("Keys:  [t] toggle   [o] on   [f] off   [s] show state   [r] reopen pairing   [h] help   [q] quit");

    /// <summary>
    /// Derives a reproducible, device-bound key that seals the persisted fabric snapshot. This sample
    /// derives it deterministically from the serial number so restarts reproduce the same key; a real
    /// device MUST source this from a hardware-sealed secret (TPM/secure-element), never a public id.
    /// </summary>
    private static string DeviceBoundSecret(string serialNumber)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"fabric-seal:{serialNumber}"), hash);
        return Convert.ToBase64String(hash);
    }

    /// <summary>
    /// Derives a stable 64-bit commissionable instance id from a device-unique string (the serial
    /// number). Using a deterministic value means every restart re-publishes the SAME
    /// <c>_matterc._udp</c> instance, so stale advertisements are replaced instead of accumulating.
    /// </summary>
    private static ulong StableInstanceId(string serialNumber)
    {
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(serialNumber), hash);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(hash);
    }

    private static void SetOnOffFromConsole(LightingDevice device, bool value)
    {
        _consoleDrivenChange = true;
        try
        {
            device.OnOff.OnOff = value;
        }
        finally
        {
            _consoleDrivenChange = false;
        }
    }
}