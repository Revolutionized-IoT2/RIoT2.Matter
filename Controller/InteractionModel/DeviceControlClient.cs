using System.Buffers;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Tlv;
using InteractionModelException = RIoT2.Matter.Controller.InteractionModel.InteractionModelException;

namespace RIoT2.Matter.Controller.InteractionModel;

/// <summary>
/// Typed helpers for the common control clusters (On/Off 0x0006, Level Control 0x0008) over an
/// <see cref="IInteractionClient"/> bound to a node's operational session. Command/attribute ids and
/// field layouts follow the Matter Application Cluster Specification, sections 1.5 (On/Off) and 1.6
/// (Level Control). This is the controller-side counterpart to the device clusters in
/// <c>RIoT2.Matter.Clusters</c>.
/// </summary>
public sealed class DeviceControlClient
{
    private static readonly ClusterId OnOff = new(0x0006);
    private static readonly ClusterId LevelControl = new(0x0008);
    private static readonly ClusterId ColorControl = new(0x0300);
    private static readonly ClusterId Descriptor = new(0x001D);

    private const uint OnOffAttributeId = 0x0000;        // On/Off.OnOff (bool)
    private const uint CurrentLevelAttributeId = 0x0000; // LevelControl.CurrentLevel (uint8, nullable)
    private const uint CurrentHueAttributeId = 0x0000;              // ColorControl.CurrentHue (uint8)
    private const uint CurrentSaturationAttributeId = 0x0001;       // ColorControl.CurrentSaturation (uint8)
    private const uint ColorTemperatureMiredsAttributeId = 0x0007;  // ColorControl.ColorTemperatureMireds (uint16)
    private const uint PartsListAttributeId = 0x0003;               // Descriptor.PartsList (list of endpoint-no)
    private const uint ServerListAttributeId = 0x0001;              // Descriptor.ServerList (list of cluster-id)

    private readonly IInteractionClient _client;

    public DeviceControlClient(IInteractionClient client)
        => _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>On/Off.Off (command 0x00): turns the endpoint off.</summary>
    public Task OffAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
        => InvokeNoFieldsAsync(endpoint, OnOff, new CommandId(0x00), cancellationToken);

    /// <summary>On/Off.On (command 0x01): turns the endpoint on.</summary>
    public Task OnAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
        => InvokeNoFieldsAsync(endpoint, OnOff, new CommandId(0x01), cancellationToken);

    /// <summary>On/Off.Toggle (command 0x02): toggles the endpoint's on/off state.</summary>
    public Task ToggleAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
        => InvokeNoFieldsAsync(endpoint, OnOff, new CommandId(0x02), cancellationToken);

    /// <summary>Reads On/Off.OnOff for the endpoint.</summary>
    public async Task<bool> ReadOnOffAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var data = await ReadAttributeAsync(endpoint, OnOff, new AttributeId(OnOffAttributeId), cancellationToken).ConfigureAwait(false);
        var reader = new TlvReader(data.Span);
        return reader.Read() && reader.GetBoolean();
    }

    /// <summary>
    /// Level Control.MoveToLevel (command 0x00): sets CurrentLevel to <paramref name="level"/> over
    /// <paramref name="transitionTimeTenths"/> tenths of a second. OptionsMask/OptionsOverride are 0.
    /// </summary>
    public Task MoveToLevelAsync(
        EndpointId endpoint, byte level, ushort transitionTimeTenths = 0, CancellationToken cancellationToken = default)
    {
        // MoveToLevel: Level [0 : uint8], TransitionTime [1 : uint16 nullable], OptionsMask [2], OptionsOverride [3].
        var fields = EncodeFields(w =>
        {
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), level);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(1), transitionTimeTenths);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(2), 0);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(3), 0);
        });
        return InvokeAsync(endpoint, LevelControl, new CommandId(0x00), fields, cancellationToken);
    }

    /// <summary>Reads Level Control.CurrentLevel for the endpoint, or null when the attribute is null.</summary>
    public async Task<byte?> ReadCurrentLevelAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var data = await ReadAttributeAsync(endpoint, LevelControl, new AttributeId(CurrentLevelAttributeId), cancellationToken).ConfigureAwait(false);
        var reader = new TlvReader(data.Span);
        if (!reader.Read() || reader.IsNull)
        {
            return null;
        }

        return (byte)reader.GetUnsignedInteger();
    }

    /// <summary>
    /// Color Control.MoveToHueAndSaturation (command 0x06): sets hue and saturation over
    /// <paramref name="transitionTimeTenths"/> tenths of a second. OptionsMask/OptionsOverride are 0.
    /// See the Matter Application Cluster Specification, section 3.2.11.
    /// </summary>
    public Task MoveToHueAndSaturationAsync(
        EndpointId endpoint, byte hue, byte saturation, ushort transitionTimeTenths = 0, CancellationToken cancellationToken = default)
    {
        // MoveToHueAndSaturation: Hue [0 : uint8], Saturation [1 : uint8], TransitionTime [2 : uint16],
        // OptionsMask [3], OptionsOverride [4].
        var fields = EncodeFields(w =>
        {
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), hue);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(1), saturation);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(2), transitionTimeTenths);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(3), 0);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(4), 0);
        });
        return InvokeAsync(endpoint, ColorControl, new CommandId(0x06), fields, cancellationToken);
    }

    /// <summary>
    /// Color Control.MoveToColorTemperature (command 0x0A): sets the color temperature (in mireds) over
    /// <paramref name="transitionTimeTenths"/> tenths of a second. OptionsMask/OptionsOverride are 0.
    /// See the Matter Application Cluster Specification, section 3.2.11.
    /// </summary>
    public Task MoveToColorTemperatureAsync(
        EndpointId endpoint, ushort colorTemperatureMireds, ushort transitionTimeTenths = 0, CancellationToken cancellationToken = default)
    {
        // MoveToColorTemperature: ColorTemperatureMireds [0 : uint16], TransitionTime [1 : uint16],
        // OptionsMask [2], OptionsOverride [3].
        var fields = EncodeFields(w =>
        {
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(0), colorTemperatureMireds);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(1), transitionTimeTenths);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(2), 0);
            w.WriteUnsignedInteger(TlvTag.ContextSpecific(3), 0);
        });
        return InvokeAsync(endpoint, ColorControl, new CommandId(0x0A), fields, cancellationToken);
    }

    /// <summary>Reads Color Control.CurrentHue (uint8) for the endpoint.</summary>
    public async Task<byte> ReadCurrentHueAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var data = await ReadAttributeAsync(endpoint, ColorControl, new AttributeId(CurrentHueAttributeId), cancellationToken).ConfigureAwait(false);
        var reader = new TlvReader(data.Span);
        return reader.Read() ? (byte)reader.GetUnsignedInteger() : (byte)0;
    }

    /// <summary>Reads Color Control.CurrentSaturation (uint8) for the endpoint.</summary>
    public async Task<byte> ReadCurrentSaturationAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var data = await ReadAttributeAsync(endpoint, ColorControl, new AttributeId(CurrentSaturationAttributeId), cancellationToken).ConfigureAwait(false);
        var reader = new TlvReader(data.Span);
        return reader.Read() ? (byte)reader.GetUnsignedInteger() : (byte)0;
    }

    /// <summary>Reads Color Control.ColorTemperatureMireds (uint16) for the endpoint.</summary>
    public async Task<ushort> ReadColorTemperatureMiredsAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var data = await ReadAttributeAsync(endpoint, ColorControl, new AttributeId(ColorTemperatureMiredsAttributeId), cancellationToken).ConfigureAwait(false);
        var reader = new TlvReader(data.Span);
        return reader.Read() ? (ushort)reader.GetUnsignedInteger() : (ushort)0;
    }

    /// <summary>
    /// Reads Descriptor.PartsList for <paramref name="endpoint"/>: the child endpoints beneath it.
    /// Reading endpoint 0 (root) yields every non-root endpoint on the node (spec §9.5).
    /// </summary>
    public async Task<IReadOnlyList<ushort>> ReadPartsListAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var data = await ReadAttributeAsync(endpoint, Descriptor, new AttributeId(PartsListAttributeId), cancellationToken).ConfigureAwait(false);
        return DecodeUnsignedArray(data.Span, static v => (ushort)v);
    }

    /// <summary>Reads Descriptor.ServerList for <paramref name="endpoint"/>: the server cluster ids it hosts.</summary>
    public async Task<IReadOnlyList<uint>> ReadServerListAsync(EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var data = await ReadAttributeAsync(endpoint, Descriptor, new AttributeId(ServerListAttributeId), cancellationToken).ConfigureAwait(false);
        return DecodeUnsignedArray(data.Span, static v => (uint)v);
    }

    /// <summary>
    /// Subscribes to On/Off, Level Control, and Color Control state on <paramref name="endpoint"/>,
    /// streaming each attribute report (starting with the priming report) until the returned
    /// subscription is disposed.
    /// </summary>
    public Task<ISubscription> SubscribeStateAsync(
        EndpointId endpoint,
        ushort minIntervalFloorSeconds,
        ushort maxIntervalCeilingSeconds,
        CancellationToken cancellationToken = default)
    {
        var paths = new[]
        {
            new AttributePathIB { Endpoint = endpoint, Cluster = OnOff, Attribute = new AttributeId(OnOffAttributeId) },
            new AttributePathIB { Endpoint = endpoint, Cluster = LevelControl, Attribute = new AttributeId(CurrentLevelAttributeId) },
            new AttributePathIB { Endpoint = endpoint, Cluster = ColorControl, Attribute = new AttributeId(CurrentHueAttributeId) },
            new AttributePathIB { Endpoint = endpoint, Cluster = ColorControl, Attribute = new AttributeId(CurrentSaturationAttributeId) },
            new AttributePathIB { Endpoint = endpoint, Cluster = ColorControl, Attribute = new AttributeId(ColorTemperatureMiredsAttributeId) },
        };

        return _client.SubscribeAsync(paths, minIntervalFloorSeconds, maxIntervalCeilingSeconds, cancellationToken);
    }

    private Task InvokeNoFieldsAsync(EndpointId endpoint, ClusterId cluster, CommandId command, CancellationToken cancellationToken)
        => InvokeAsync(endpoint, cluster, command, ReadOnlyMemory<byte>.Empty, cancellationToken);

    private async Task InvokeAsync(
        EndpointId endpoint, ClusterId cluster, CommandId command, ReadOnlyMemory<byte> fields, CancellationToken cancellationToken)
    {
        var result = await _client.InvokeAsync(
            new ClusterCommand { Endpoint = endpoint, Cluster = cluster, Command = command, Fields = fields },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new InteractionModelException(
                $"Command 0x{command.Value:X2} on cluster 0x{cluster.Value:X4} failed.", result.Status?.Status);
        }
    }

    private async Task<ReadOnlyMemory<byte>> ReadAttributeAsync(
        EndpointId endpoint, ClusterId cluster, AttributeId attribute, CancellationToken cancellationToken)
    {
        var path = new AttributePathIB { Endpoint = endpoint, Cluster = cluster, Attribute = attribute };
        var reports = await _client.ReadAttributesAsync([path], cancellationToken).ConfigureAwait(false);

        foreach (var report in reports)
        {
            if (report.AttributeData is { } data && !data.Data.IsEmpty)
            {
                return data.Data;
            }

            if (report.AttributeStatus is { } status)
            {
                throw new InteractionModelException(
                    $"Reading attribute 0x{attribute.Value:X4} on cluster 0x{cluster.Value:X4} failed.", status.Status.Status);
            }
        }

        throw new InteractionModelException($"No value returned for attribute 0x{attribute.Value:X4} on cluster 0x{cluster.Value:X4}.");
    }

    private static ReadOnlyMemory<byte> EncodeFields(Action<TlvWriter> build)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        build(writer);
        writer.EndContainer();
        return buffer.WrittenSpan.ToArray();
    }

    // Decodes a TLV array of unsigned integers (e.g. Descriptor list attributes) into a projected list.
    private static IReadOnlyList<T> DecodeUnsignedArray<T>(ReadOnlySpan<byte> data, Func<ulong, T> project)
    {
        var results = new List<T>();
        var reader = new TlvReader(data);
        var depth = 0;
        while (reader.Read())
        {
            if (reader.IsContainer) { depth++; continue; }
            if (reader.IsEndOfContainer) { depth--; continue; }
            if (depth == 1) { results.Add(project(reader.GetUnsignedInteger())); }
        }

        return results;
    }
}