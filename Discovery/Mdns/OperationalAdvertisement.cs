using RIoT2.Matter.DataModel;

namespace RIoT2.Matter.Discovery.Mdns;

/// <summary>
/// Builds operational (<c>_matter._tcp</c>) DNS-SD service instances from the advertising inputs. Each
/// commissioned fabric yields one instance whose label is <c>&lt;CompressedFabricId&gt;-&lt;NodeId&gt;</c>
/// (two 16-digit uppercase hex values joined by a hyphen) and which advertises the compressed-fabric-id
/// subtype <c>_I&lt;CompressedFabricId&gt;</c> for fabric-scoped browsing. See the Matter Core
/// Specification, section 4.3.1.1.
/// </summary>
public static class OperationalAdvertisement
{
    /// <summary>The compressed-fabric-id subtype prefix (<c>_I</c>).</summary>
    public const string CompressedFabricIdSubtypePrefix = "_I";

    /// <summary>Builds the operational service instance for a single fabric.</summary>
    public static DnsSdService Build(OperationalServiceInfo service, MatterHostInfo host)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(host);

        // Derive throws if the root public key is not a valid uncompressed P-256 point.
        CompressedFabricId compressedFabricId =
            CompressedFabricIdentifier.Derive(service.RootPublicKey.Span, service.FabricId);

        // Both identifiers are 16-digit, zero-padded, uppercase hex. The "X16" specifier is
        // culture-independent for integers; format from the raw .Value so the wire form does not depend
        // on the id types' ToString() (NodeId.ToString() prepends "0x", which is not part of the name).
        string compressedFabricHex = $"{compressedFabricId.Value:X16}";
        string instanceName = $"{compressedFabricHex}-{service.NodeId.Value:X16}";
        string subtype = CompressedFabricIdSubtypePrefix + compressedFabricHex;

        IReadOnlyList<string> txt = new DnsSdTxtRecordBuilder()
            .AddSessionParameters(host.Mrp, host.TcpSupported, host.LongIdleTimeIcd)
            .Build();

        return new DnsSdService
        {
            ServiceType = DnsSdServiceType.Operational,
            InstanceName = instanceName,
            HostName = host.HostName,
            Port = host.Port,
            Subtypes = [subtype],
            TxtEntries = txt,
            Addresses = host.Addresses,
        };
    }

    /// <summary>Builds one operational service instance per fabric in <paramref name="inputs"/>.</summary>
    public static IReadOnlyList<DnsSdService> BuildAll(MatterAdvertisingInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var services = new List<DnsSdService>(inputs.OperationalServices.Count);
        foreach (OperationalServiceInfo service in inputs.OperationalServices)
        {
            services.Add(Build(service, inputs.Host));
        }

        return services;
    }
}