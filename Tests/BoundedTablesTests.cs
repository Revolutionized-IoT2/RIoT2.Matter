using System.Buffers;
using System.Security.Cryptography;
using RIoT2.Matter.Clusters;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.SecureChannel.Case;
using RIoT2.Matter.Tlv;
using Xunit;

namespace RIoT2.Matter.Tests;

/// <summary>
/// The peer and session tables are bounded (backlog item 8, I43): anyone who can send a datagram can
/// otherwise grow them without limit.
/// </summary>
public sealed class BoundedTablesTests
{
    [Fact]
    public void HandshakesBeyondTheBoundAreRefusedUntilOneIsReleased()
    {
        using var sessions = new SessionManager(maxPendingHandshakes: 2);
        var first = sessions.AllocateSessionId();
        sessions.AllocateSessionId();
        Assert.Throws<SessionTableFullException>(() => sessions.AllocateSessionId());

        sessions.ReleaseSessionId(first);
        sessions.AllocateSessionId();
        Assert.Equal(2, sessions.PendingHandshakes);
    }

    [Fact]
    public void AFullSessionTableEvictsTheLeastRecentlyUsedSession()
    {
        var clock = new ManualClock();
        using var sessions = new SessionManager(clock, maxSessions: 2);
        var a = sessions.RegisterSecureSession(Session(sessions.AllocateSessionId(), clock));
        clock.Advance();
        var b = sessions.RegisterSecureSession(Session(sessions.AllocateSessionId(), clock));
        clock.Advance();
        a.NotifyActivity(); // b is now the least recently used
        var c = sessions.RegisterSecureSession(Session(sessions.AllocateSessionId(), clock));

        Assert.Equal(2, sessions.Count);
        Assert.True(sessions.TryGetSecureSession(a.Session.LocalSessionId, out _));
        Assert.False(sessions.TryGetSecureSession(b.Session.LocalSessionId, out _));
        Assert.True(sessions.TryGetSecureSession(c.Session.LocalSessionId, out _));
        Assert.Equal(0, sessions.PendingHandshakes);
    }

    [Fact]
    public async Task UnsecuredPeersAreBoundedAndTheLeastRecentlySeenIsForgotten()
    {
        using var sessions = new SessionManager();
        using var exchanges = new ExchangeManager();
        var dispatcher = new InboundMessageDispatcher(sessions, exchanges, MessageCounter.CreateRandom(), maxUnsecuredPeers: 4);
        var peers = Enumerable.Range(0, 10).Select(_ => new NullTransport()).ToArray();
        foreach (var peer in peers)
        {
            await dispatcher.DispatchAsync(Frame(counter: 1), peer);
        }

        Assert.Equal(4, dispatcher.UnsecuredPeerCount);

        // A remembered peer's retransmission is still recognised as a duplicate...
        var handler = new CountingHandler();
        exchanges.RegisterUnsolicitedHandler(MatterProtocolId.SecureChannel, handler);
        await dispatcher.DispatchAsync(Frame(counter: 1), peers[^1]);
        Assert.Equal(0, handler.Received);

        // ...while a forgotten one starts over, as after a restart.
        await dispatcher.DispatchAsync(Frame(counter: 1), peers[0]);
        Assert.Equal(1, handler.Received);
        Assert.Equal(4, dispatcher.UnsecuredPeerCount);
    }

    [Fact]
    public async Task AnAbandonedCaseHandshakeReleasesItsSessionId()
    {
        using var sessions = new SessionManager();
        var (server, sigma1) = CaseResponder(sessions);
        using var exchanges = new ExchangeManager();
        var transport = new RecordingTransport();
        var exchange = exchanges.NewExchange(new UnsecuredMessageSession(transport), MatterProtocolId.SecureChannel, new CountingHandler());

        await server.OnMessageAsync(exchange, SecureChannelOpcode.CaseSigma1, sigma1);
        Assert.Equal(1, sessions.PendingHandshakes);
        Assert.Equal((byte)SecureChannelOpcode.CaseSigma2, Opcode(transport.Frames.Single()));

        server.OnExchangeClosed(exchange); // the initiator never sends Sigma3
        Assert.Equal(0, sessions.PendingHandshakes);
    }

    [Fact]
    public async Task ACaseHandshakeIsAnsweredBusyWhenTheSessionTableIsFull()
    {
        using var sessions = new SessionManager(maxPendingHandshakes: 1);
        sessions.AllocateSessionId();
        var (server, sigma1) = CaseResponder(sessions);
        using var exchanges = new ExchangeManager();
        var transport = new RecordingTransport();
        var exchange = exchanges.NewExchange(new UnsecuredMessageSession(transport), MatterProtocolId.SecureChannel, new CountingHandler());

        await server.OnMessageAsync(exchange, SecureChannelOpcode.CaseSigma1, sigma1);

        Assert.Equal((byte)SecureChannelOpcode.StatusReport, Opcode(transport.Frames.Single()));
        Assert.Equal(1, sessions.PendingHandshakes);
    }

    [Fact]
    public async Task ACaseHandshakeWithoutSigma3ExpiresAndReleasesItsSessionId()
    {
        var clock = new ManualClock();
        using var sessions = new SessionManager();
        var (server, sigma1) = CaseResponder(sessions, clock);
        using var exchanges = new ExchangeManager();
        var stalled = exchanges.NewExchange(new UnsecuredMessageSession(new RecordingTransport()), MatterProtocolId.SecureChannel, new CountingHandler());
        await server.OnMessageAsync(stalled, SecureChannelOpcode.CaseSigma1, sigma1);
        Assert.Equal(1, sessions.PendingHandshakes);

        clock.Advance(CaseServer.HandshakeTimeout);
        var next = exchanges.NewExchange(new UnsecuredMessageSession(new RecordingTransport()), MatterProtocolId.SecureChannel, new CountingHandler());
        await server.OnMessageAsync(next, SecureChannelOpcode.CaseSigma1, sigma1);

        Assert.True(stalled.IsClosed);
        Assert.False(next.IsClosed);
        Assert.Equal(1, sessions.PendingHandshakes);
    }

    [Fact]
    public async Task UnsecuredExchangesAreBoundedAndTheOldestIsClosed()
    {
        using var sessions = new SessionManager();
        using var exchanges = new ExchangeManager(maxUnsecuredResponders: 3);
        var handler = new CountingHandler(); // never answers, so its exchanges never close by themselves
        exchanges.RegisterUnsolicitedHandler(MatterProtocolId.SecureChannel, handler);
        var dispatcher = new InboundMessageDispatcher(sessions, exchanges, MessageCounter.CreateRandom());
        var peer = new NullTransport();
        for (ushort exchange = 1; exchange <= 20; exchange++)
        {
            await dispatcher.DispatchAsync(Frame(counter: exchange, exchangeId: exchange), peer);
        }

        Assert.Equal(20, handler.Received);
        Assert.Equal(3, exchanges.OpenUnsecuredResponders);
        Assert.Equal(17, handler.Closed);
    }

    private static (CaseServer Server, MatterMessage Sigma1) CaseResponder(SessionManager sessions, TimeProvider? clock = null)
    {
        var crypto = new ManagedCaseCryptoProvider();
        using var root = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var q = root.ExportParameters(false).Q;
        byte[] rootPublicKey = [0x04, .. q.X!, .. q.Y!];
        var fabric = new ResolvedFabric(
            new FabricIndex(1), new FabricId(1), new NodeId(2), rootPublicKey,
            RandomNumberGenerator.GetBytes(16), new byte[64], null, new EcdsaOperationalKey());
        var server = new CaseServer(crypto, new FabricStore(fabric), sessions.AllocateSessionId, releaseSessionId: sessions.ReleaseSessionId, timeProvider: clock);

        byte[] initiatorRandom = RandomNumberGenerator.GetBytes(32);
        byte[] destinationId = crypto.ComputeDestinationIdentifier(
            fabric.IdentityProtectionKey, initiatorRandom, fabric.RootPublicKey, fabric.FabricId, fabric.NodeId);
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var e = ephemeral.ExportParameters(false).Q;

        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), initiatorRandom);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(2), 0x1234);
        writer.WriteByteString(TlvTag.ContextSpecific(3), destinationId);
        writer.WriteByteString(TlvTag.ContextSpecific(4), [0x04, .. e.X!, .. e.Y!]);
        writer.EndContainer();

        var sigma1 = new MatterMessage(
            new MessageHeader { SessionId = 0, MessageCounter = 1 },
            new ProtocolHeader { IsInitiator = true, ExchangeId = 9, ProtocolId = MatterProtocolId.SecureChannel, ProtocolOpcode = (byte)SecureChannelOpcode.CaseSigma1 },
            buffer.WrittenMemory.ToArray());
        return (server, sigma1);
    }

    private static byte Opcode(byte[] frame) => MatterMessageCodec.Decode(frame).Protocol.ProtocolOpcode;

    private static SecureSession Session(ushort localSessionId, TimeProvider clock) => new(
        SecureSessionType.Case, SecureSessionRole.Responder, localSessionId, 1, new NodeId(1), new NodeId(2), new FabricIndex(1),
        new byte[16], new byte[16], new byte[16], ReliableMessageProtocolConfig.Default, clock);

    private static byte[] Frame(uint counter, ushort exchangeId = 7)
    {
        var buffer = new ArrayBufferWriter<byte>();
        MatterMessageCodec.Encode(buffer, new MessageHeader { SessionId = 0, MessageCounter = counter },
            new ProtocolHeader { IsInitiator = true, ExchangeId = exchangeId, ProtocolId = MatterProtocolId.SecureChannel, ProtocolOpcode = 0x20 }, []);
        return buffer.WrittenSpan.ToArray();
    }

    private sealed class FabricStore(ResolvedFabric fabric) : IFabricStore
    {
        public IReadOnlyList<ResolvedFabric> Fabrics { get; } = [fabric];
    }

    private sealed class NullTransport : IMessageTransport
    {
        public ValueTask SendAsync(ReadOnlyMemory<byte> message, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
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

    private sealed class CountingHandler : IExchangeMessageHandler
    {
        public int Received;
        public int Closed;

        public ValueTask OnMessageReceivedAsync(ExchangeContext exchange, MatterMessage message, CancellationToken cancellationToken = default)
        {
            Received++;
            return ValueTask.CompletedTask;
        }

        public void OnExchangeClosed(ExchangeContext exchange) => Closed++;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance() => _timestamp += 1000;
        public void Advance(TimeSpan by) => _timestamp += (long)by.TotalMilliseconds;
    }
}
