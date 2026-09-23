using System.Buffers;
using RIoT2.Matter.Controller.InteractionModel;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Device;
using RIoT2.Matter.InteractionModel;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.Tlv;
using Xunit;
using ClientException = RIoT2.Matter.Controller.InteractionModel.InteractionModelException;

namespace RIoT2.Matter.Tests;

public sealed class MessagingTests
{
    [Fact]
    public async Task RejectedTimedRequestDoesNotSendAction()
    {
        using var exchanges = new ExchangeManager();
        var session = new MemorySession();
        session.Deliver = (p, _) => exchanges.OnMessageReceivedAsync(session, Message(
            p with { IsInitiator = false, ProtocolOpcode = (byte)InteractionModelOpcode.StatusResponse },
            new StatusResponseMessage { Status = InteractionModelStatusCode.Busy }.ToArray()));
        var client = new InteractionClient(exchanges, session);
        var failure = await Assert.ThrowsAsync<ClientException>(() => client.InvokeAsync(new ClusterCommand
        {
            Endpoint = new(1), Cluster = new(1), Command = new(1),
        }, true, 1000));
        Assert.Equal(InteractionModelStatusCode.Busy, failure.Status);
        Assert.Single(session.Sent);
    }

    [Fact]
    public async Task CancellationWhileWaitingForTimedStatusStopsBeforeAction()
    {
        using var exchanges = new ExchangeManager();
        var session = new MemorySession();
        using var cancellation = new CancellationTokenSource();
        session.Deliver = (_, _) =>
        {
            cancellation.Cancel();
            return ValueTask.CompletedTask;
        };
        var client = new InteractionClient(exchanges, session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.InvokeAsync(new ClusterCommand
        {
            Endpoint = new(1), Cluster = new(1), Command = new(1),
        }, true, 1000, cancellation.Token));
        Assert.Single(session.Sent);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TimedActionUsesSameExchangeAndEnforcesExpiry(bool write, bool expire)
    {
        var time = new TestClock();
        using var clientExchanges = new ExchangeManager();
        using var serverExchanges = new ExchangeManager(time);
        var node = new MatterNode();
        var cluster = new TimedCluster();
        node.AddEndpoint(new EndpointId(1)).AddCluster(cluster);
        serverExchanges.RegisterUnsolicitedHandler(MatterProtocolId.InteractionModel,
            new InteractionModelHandler(node, serverExchanges, time));
        var clientSession = new MemorySession();
        var serverSession = new MemorySession();
        clientSession.Deliver = (p, b) => serverExchanges.OnMessageReceivedAsync(serverSession, Message(p, b));
        serverSession.Deliver = (p, b) =>
        {
            if (expire && p.ProtocolOpcode == (byte)InteractionModelOpcode.StatusResponse) { time.Advance(); }
            return clientExchanges.OnMessageReceivedAsync(clientSession, Message(p, b));
        };
        var client = new InteractionClient(clientExchanges, clientSession);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        async Task Act()
        {
            if (write)
            {
                var statuses = await client.WriteAttributesAsync([
                    new AttributeDataIB { Path = new AttributePathIB { Endpoint = new(1), Cluster = cluster.Id, Attribute = new(0) }, Data = new byte[] { 0x09 } },
                ], true, 1000, timeout.Token);
                Assert.Equal(InteractionModelStatusCode.Success, Assert.Single(statuses).Status.Status);
            }
            else
            {
                var result = await client.InvokeAsync(new ClusterCommand
                {
                    Endpoint = new(1), Cluster = cluster.Id, Command = new(0),
                }, true, 1000, timeout.Token);
                Assert.Equal(InteractionModelStatusCode.Success, result.Status?.Status);
            }
        }
        if (expire) { await Assert.ThrowsAsync<ClientException>(Act); }
        else { await Act(); }
        Assert.Equal(expire ? 0 : 1, cluster.Actions);
        var sent = clientSession.Sent.Where(p => p.ProtocolId == MatterProtocolId.InteractionModel).ToArray();
        Assert.Equal(2, sent.Length);
        Assert.Equal(sent[0].ExchangeId, sent[1].ExchangeId);
    }

    [Fact]
    public async Task AnonymousPeersWithIdenticalCountersAndExchangeIdsAreIsolated()
    {
        using var sessions = new SessionManager();
        using var exchanges = new ExchangeManager();
        var handler = new ReplyHandler();
        exchanges.RegisterUnsolicitedHandler(MatterProtocolId.SecureChannel, handler);
        var dispatcher = new InboundMessageDispatcher(sessions, exchanges, MessageCounter.CreateRandom());
        var first = new RecordingTransport();
        var second = new RecordingTransport();
        var frame = Frame(counter: 1);
        await dispatcher.DispatchAsync(frame, first);
        await dispatcher.DispatchAsync(frame, second);
        Assert.Equal(2, handler.Received);
        Assert.Single(first.Frames);
        Assert.Single(second.Frames);
        await dispatcher.DispatchAsync(frame, first);
        Assert.Equal(2, handler.Received);
        await dispatcher.DispatchAsync(Frame(counter: 2), second);
        Assert.Equal(3, handler.Received);
        Assert.Single(first.Frames);
        Assert.Equal(2, second.Frames.Count);
    }

    [Fact]
    public async Task OutboundUnsecuredExchangeMatchesNewWrapperForSamePeer()
    {
        using var exchanges = new ExchangeManager();
        var transport = new RecordingTransport();
        var handler = new ReplyHandler();
        var outbound = exchanges.NewExchange(new UnsecuredMessageSession(transport), MatterProtocolId.SecureChannel, handler);
        await exchanges.OnMessageReceivedAsync(new UnsecuredMessageSession(transport),
            Message(new ProtocolHeader { ExchangeId = outbound.ExchangeId, IsInitiator = false,
                ProtocolId = MatterProtocolId.SecureChannel, ProtocolOpcode = 0x20 }, ReadOnlyMemory<byte>.Empty));
        Assert.Equal(1, handler.Received);
    }

    internal static MatterMessage Message(ProtocolHeader protocol, ReadOnlyMemory<byte> bytes) =>
        new(new MessageHeader { SessionId = 1, MessageCounter = 1 }, protocol, bytes);

    private static byte[] Frame(uint counter)
    {
        var buffer = new ArrayBufferWriter<byte>();
        MatterMessageCodec.Encode(buffer, new MessageHeader { SessionId = 0, MessageCounter = counter },
            new ProtocolHeader { IsInitiator = true, ExchangeId = 7,
                ProtocolId = MatterProtocolId.SecureChannel, ProtocolOpcode = 0x20 }, []);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class RecordingTransport : IMessageTransport
    {
        public List<byte[]> Frames { get; } = [];
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default)
        {
            Frames.Add(message.ToArray());
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ReplyHandler : IExchangeMessageHandler
    {
        public int Received;
        public async ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message,
            CancellationToken cancellationToken = default)
        {
            Received++;
            await exchange.SendAsync(0x21, new byte[] { 1 }, reliable: false, cancellationToken);
        }
        public void OnExchangeClosed(ExchangeContext exchange) { }
    }

    private sealed class TimedCluster : Cluster
    {
        public int Actions;
        public override ClusterId Id => new(0x1234);
        public override IReadOnlyCollection<AttributeId> AttributeIds => [new(0)];
        public override IReadOnlyCollection<CommandId> AcceptedCommandIds => [new(0)];
        public override bool AttributeRequiresTimedWrite(AttributeId attributeId) => true;
        public override bool CommandRequiresTimedInvoke(CommandId commandId) => true;
        protected override ValueTask<InteractionModelStatusCode> ReadAttributeCoreAsync(AttributeId id,
            TlvWriter writer, TlvTag tag, InteractionContext context, CancellationToken cancellationToken)
            => new(InteractionModelStatusCode.UnsupportedAttribute);
        protected override ValueTask<InteractionModelStatusCode> WriteAttributeCoreAsync(AttributeId id,
            ReadOnlyMemory<byte> value, InteractionContext context, CancellationToken cancellationToken)
        {
            Actions++;
            return new(InteractionModelStatusCode.Success);
        }
        protected override ValueTask<CommandResponse> InvokeCommandCoreAsync(CommandId id,
            ReadOnlyMemory<byte> fields, InteractionContext context, CancellationToken cancellationToken)
        {
            Actions++;
            return new(CommandResponse.FromStatus(InteractionModelStatusCode.Success));
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance() => _timestamp += 2000;
    }
}

internal sealed class MemorySession : IMessageSession
{
    private uint _counter;
    public Func<ProtocolHeader, ReadOnlyMemory<byte>, ValueTask>? Deliver;
    public List<ProtocolHeader> Sent { get; } = [];
    public ushort SessionId => 1;
    public ReliableMessageProtocolConfig RemoteMrpConfig => ReliableMessageProtocolConfig.Default;
    public bool IsPeerActive => true;
    public SessionSecurity Security => new() { IsSecure = true, FabricIndex = new(1), PeerNodeId = new(42) };
    public async ValueTask<EncodedMessage> SendAsync(ProtocolHeader protocol, ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        Sent.Add(protocol);
        var counter = ++_counter;
        if (Deliver is not null) { await Deliver(protocol, payload); }
        return new EncodedMessage(payload, counter);
    }
    public ValueTask RetransmitAsync(ReadOnlyMemory<byte> encodedMessage, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
