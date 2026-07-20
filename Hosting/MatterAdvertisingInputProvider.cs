using RIoT2.Matter.Clusters;
using RIoT2.Matter.Discovery.Mdns;

namespace RIoT2.Matter.Hosting;

/// <summary>
/// The <see cref="IMatterAdvertisingInputProvider"/> that snapshots advertising inputs from the live
/// commissioning window and fabric table, signalling the advertiser to rebuild whenever either changes.
/// Commissionable (<c>_matterc._udp</c>) advertising follows the commissioning-window state; one
/// operational (<c>_matter._tcp</c>) service is projected per commissioned fabric.
/// </summary>
public sealed class MatterAdvertisingInputProvider : IMatterAdvertisingInputProvider, IDisposable
{
    private readonly AdministratorCommissioningController _window;
    private readonly OperationalCredentialsManager _fabrics;
    private readonly MatterHostInfo _host;
    private readonly CommissionableServiceInfo _commissionable;

    /// <summary>Snapshots advertising inputs from the live commissioning window and fabric table.</summary>
    public MatterAdvertisingInputProvider(
        AdministratorCommissioningController window,
        OperationalCredentialsManager fabrics,
        MatterHostInfo host,
        CommissionableServiceInfo commissionable)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _fabrics = fabrics ?? throw new ArgumentNullException(nameof(fabrics));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _commissionable = commissionable ?? throw new ArgumentNullException(nameof(commissionable));

        // A window open/close or a fabric add/remove changes what we advertise; forward both as one signal.
        _window.Changed += OnSourceChanged;
        _fabrics.Changed += OnSourceChanged;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public MatterAdvertisingInputs GetCurrent() => new()
    {
        Host = _host,
        OperationalServices = BuildOperationalServices(),
        Commissionable = BuildCommissionable(),
    };

    /// <inheritdoc />
    public void Dispose()
    {
        _window.Changed -= OnSourceChanged;
        _fabrics.Changed -= OnSourceChanged;
    }

    // Advertise commissionable only while a commissioning window is open, tagging the mode so controllers
    // see CM=1 (basic) or CM=2 (enhanced); otherwise the node is not discoverable for commissioning.
    private CommissionableServiceInfo? BuildCommissionable() => _window.Status switch
    {
        CommissioningWindowStatus.BasicWindowOpen => _commissionable with { Mode = CommissioningMode.Basic },
        CommissioningWindowStatus.EnhancedWindowOpen => _commissionable with { Mode = CommissioningMode.Enhanced },
        _ => null,
    };

    // One operational service per commissioned fabric, carrying only public identity (root key + ids).
    private IReadOnlyList<OperationalServiceInfo> BuildOperationalServices()
    {
        IReadOnlyList<FabricDescriptor> fabrics = _fabrics.Fabrics;
        if (fabrics.Count == 0)
        {
            return [];
        }

        var services = new List<OperationalServiceInfo>(fabrics.Count);
        foreach (FabricDescriptor fabric in fabrics)
        {
            services.Add(new OperationalServiceInfo
            {
                RootPublicKey = fabric.RootPublicKey,
                FabricId = fabric.FabricId,
                NodeId = fabric.NodeId,
            });
        }

        return services;
    }

    private void OnSourceChanged(object? sender, EventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}