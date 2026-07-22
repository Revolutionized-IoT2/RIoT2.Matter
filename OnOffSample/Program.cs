using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.Diagnostics;
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

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 0. Decide up-front whether verbose troubleshooting traces are emitted. Done before the host
        //    starts so the first WindowOpened / dropped-datagram traces honor the choice.
        Diagnostics.Configure(args);

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
            Profile = LightingProfile.DimmableLight,  // Dimmable Light (0x0101): adds Level Control for brightness
            NodeLabel = "RIoT2 Demo Light",
            InitialOnOff = false,
            InitialLevel = 100,
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
            DeviceType = StandardDeviceTypes.DimmableLight.Id,
            DeviceName = options.NodeLabel,
        };

        // 5. Start the host: transport, sessions, Secure Channel (PASE/CASE), Interaction Model, DNS-SD.
        //    The host owns an in-process CASE resumption store, so an already-commissioned controller can
        //    resume a prior session via Sigma2_Resume (spec §4.14.2.6) without any wiring here.
        //    A stable, serial-derived host id keeps the <id>.local host name constant across restarts.
        //    Diagnostics are routed into the library only when enabled, so the host's verbose traces
        //    honor the same --diagnostics / ONOFF_DIAGNOSTICS choice as the sample's own output.
        IMatterDiagnostics? hostDiagnostics = Diagnostics.Enabled
            ? new DelegateMatterDiagnostics(Diagnostics.Trace, Diagnostics.TraceError)
            : null;

        await using var host = new MatterNodeHost(
            device.Node,
            device.Commissioning,
            provisioning,
            commissionable,
            hostId: StableInstanceId($"host:{options.Information.SerialNumber}"),
            diagnostics: hostDiagnostics);
        using var lifetime = new CancellationTokenSource();

        // Subscribe BEFORE starting the host: a factory-new node opens its basic commissioning window
        // inside StartAsync, so hooking the events afterwards would miss that first WindowOpened.
        TraceCommissioningStages(device.Commissioning);

        await host.StartAsync(lifetime.Token);

        // Report the window state that resulted from startup (a factory-new node should be BasicWindowOpen).
        Diagnostics.Trace($"[commissioning] startup window status: {device.Commissioning.AdministratorCommissioning.Status}");

        // Subscribes to the commissioning-support lifecycle so each stage of a pairing attempt is logged.
        // Reading these in order during a Google Home attempt isolates the failure point:
        //   • WindowOpened but no FabricAdded            → controller never completed PASE (reachability/IPv6) or attestation was rejected.
        //   • FabricAdded but FabricRemoved/FailSafeExpired before CommissioningCompleted → post-AddNOC failure (CASE, ACL, or attestation revalidation).
        //   • CommissioningCompleted                     → fully commissioned; the pairing succeeded.
        //   • WindowClosed before any FabricAdded         → the commissioning window timed out.
        TraceCommissioningStages(device.Commissioning);

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

        // Level -> console: fires for BOTH console-driven and controller-driven brightness changes.
        if (device.LevelControl is { } levelControl)
        {
            levelControl.CurrentLevelChanged += (_, _) =>
                RenderLevel(levelControl.CurrentLevel, levelControl.MinLevel, levelControl.MaxLevel);
        }

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
                case '+':
                    AdjustBrightness(device, delta: +25);
                    break;
                case '-':
                    AdjustBrightness(device, delta: -25);
                    break;
                case 'b':
                    PromptBrightness(device);
                    break;
                case 's':
                    RenderState(device.OnOff.OnOff, "query");
                    if (device.LevelControl is { } lc)
                    {
                        RenderLevel(lc.CurrentLevel, lc.MinLevel, lc.MaxLevel);
                    }
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
        Console.WriteLine("Keys:  [t] toggle   [o] on   [f] off   [+/-] brightness ±   [b] set brightness %   [s] show state   [r] reopen pairing   [h] help   [q] quit");

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

    // Nudge CurrentLevel by delta (in raw level units), clamped into the cluster's Min/Max bounds.
    private static void AdjustBrightness(LightingDevice device, int delta)
    {
        if (device.LevelControl is not { } level)
        {
            Console.WriteLine("Brightness is unavailable: build the node with LightingProfile.DimmableLight.");
            return;
        }

        int target = Math.Clamp(level.CurrentLevel + delta, level.MinLevel, level.MaxLevel);
        level.SetCurrentLevel((byte)target);
    }

    // Prompt for a 0–100 % brightness and map it onto the cluster's Min/Max level range.
    private static void PromptBrightness(LightingDevice device)
    {
        if (device.LevelControl is not { } level)
        {
            Console.WriteLine("Brightness is unavailable: build the node with LightingProfile.DimmableLight.");
            return;
        }

        Console.Write("Brightness % (0-100): ");
        string? input = Console.ReadLine();
        if (!int.TryParse(input, out int percent) || percent is < 0 or > 100)
        {
            Console.WriteLine("Ignored: enter a whole number from 0 to 100.");
            return;
        }

        int span = level.MaxLevel - level.MinLevel;
        int target = level.MinLevel + (int)Math.Round(span * (percent / 100.0));
        level.SetCurrentLevel((byte)Math.Clamp(target, level.MinLevel, level.MaxLevel));
    }

    private static void RenderLevel(byte level, byte minLevel, byte maxLevel)
    {
        int span = maxLevel - minLevel;
        int percent = span == 0 ? 100 : (int)Math.Round((level - minLevel) * 100.0 / span);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Level  = {level,3} ({percent}%)  [min {minLevel}, max {maxLevel}]");
    }

    // Subscribes to the commissioning-support lifecycle so each stage of a pairing attempt is logged.
    // Reading these in order during a Google Home attempt isolates the failure point:
    //   • WindowOpened but no FabricAdded            → controller never completed PASE (reachability/IPv6) or attestation was rejected.
    //   • FabricAdded but FabricRemoved/FailSafeExpired before CommissioningCompleted → post-AddNOC failure (CASE, ACL, or attestation revalidation).
    //   • CommissioningCompleted                     → fully commissioned; the pairing succeeded.
    //   • WindowClosed before any FabricAdded         → the commissioning window timed out.
    private static void TraceCommissioningStages(RIoT2.Matter.Clusters.CommissioningSupport commissioning)
    {
        commissioning.AdministratorCommissioning.WindowOpened += (_, e) =>
            Diagnostics.Trace($"[commissioning] window OPENED ({e.Status}); node is now commissionable (PASE accepted).");

        commissioning.AdministratorCommissioning.WindowClosed += (_, _) =>
            Diagnostics.Trace("[commissioning] window CLOSED (revoked or timed out); node no longer commissionable.");

        commissioning.Manager.FabricAdded += (_, e) =>
            Diagnostics.Trace($"[commissioning] AddNOC succeeded: fabric added (index {e.FabricIndex}). PASE + attestation passed.");

        commissioning.Manager.FabricRemoved += (_, e) =>
            Diagnostics.Trace($"[commissioning] fabric REMOVED (index {e.FabricIndex}); AddNOC was rolled back.");

        commissioning.StateMachine.CommissioningCompleted += (_, _) =>
            Diagnostics.Trace("[commissioning] CommissioningComplete received: pairing SUCCEEDED.");

        commissioning.StateMachine.FailSafeExpired += (_, _) =>
            Diagnostics.Trace("[commissioning] fail-safe EXPIRED: the controller aborted before CommissioningComplete (post-AddNOC failure).");
    }
}