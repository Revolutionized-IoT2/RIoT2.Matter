namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Holds the node's current <see cref="AdvertisedRecordSet"/>. The advertiser lifecycle replaces the set
/// atomically whenever the advertising inputs change; the query responder reads the current set per
/// query. Because each set is immutable, readers always observe a self-consistent snapshot with no lock.
/// </summary>
public sealed class AdvertisedRecordStore
{
    private volatile AdvertisedRecordSet _current = AdvertisedRecordSet.Empty;

    /// <summary>The current owned record set. Never null; starts empty.</summary>
    public AdvertisedRecordSet Current => _current;

    /// <summary>Atomically replaces the current record set.</summary>
    public void Update(AdvertisedRecordSet recordSet)
    {
        ArgumentNullException.ThrowIfNull(recordSet);
        _current = recordSet;
    }

    /// <summary>Rebuilds and atomically installs the record set from an advertising-input snapshot.</summary>
    public void Update(MatterAdvertisingInputs inputs) => Update(AdvertisedRecordSet.FromInputs(inputs));
}