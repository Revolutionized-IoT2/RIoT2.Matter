using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using RIoT2.Matter.Crypto;
using RIoT2.Matter.Crypto.Kat;
using RIoT2.Matter.DataModel;
using RIoT2.Matter.Discovery.Dns;
using RIoT2.Matter.Discovery.Mdns;
using RIoT2.Matter.Messaging;
using RIoT2.Matter.UserDirectedCommissioning;

namespace RIoT2.Matter.Discovery.Kat;

/// <summary>
/// Known-Answer Test (KAT) harness for the mDNS / DNS-SD discovery and User-Directed Commissioning
/// wire formats (Matter Core Specification §4.3 and §5.3). It pins exact DNS bytes, verifies
/// advertise→resolve round-trips for all three Matter service types, exercises the hex-vs-decimal and
/// byte-order encodings that differ between operational and commissioning discovery, and round-trips the
/// UDC declarations. The harness drives the real implementation so it guards behavior rather than the
/// platform.
/// <list type="bullet">
///   <item><description><c>Dns_Wire</c>            = RFC 1035/6762/6763 message + name-compression codec</description></item>
///   <item><description><c>Mdns_Cfid</c>           = compressed fabric id derivation (§4.3.2.2)</description></item>
///   <item><description><c>Mdns_Operational</c>    = <c>_matter._tcp</c> advertise→resolve round-trip</description></item>
///   <item><description><c>Mdns_Commissionable</c> = <c>_matterc._udp</c> advertise→resolve round-trip</description></item>
///   <item><description><c>Mdns_Commissioner</c>   = <c>_matterd._udp</c> advertise→resolve round-trip</description></item>
///   <item><description><c>Udc_Wire</c>            = UDC Identification/Commissioner declaration round-trip</description></item>
/// </list>
/// </summary>
public static class MatterDiscoveryKat
{
    // --- Authoritative compressed-fabric-id fixed vector -------------------
    // Leave blank to SKIP; paste an authoritative connectedhomeip vector to turn the test into a
    // wire-compatibility check.
    private const string SpecCfidRootPublicKey = ""; // 65-byte uncompressed P-256 point (0x04 || X || Y), hex
    private const string SpecCfidFabricId = "";      // 16-hex Fabric ID
    private const string SpecCfidExpected = "";      // 16-hex expected compressed fabric id

    /// <summary>Runs every discovery KAT and returns an aggregate report.</summary>
    public static KatReport Run(TextWriter? log = null)
    {
        log ??= TextWriter.Null;
        var sw = Stopwatch.StartNew();

        var results = new List<KatResult>
        {
            DnsQuestionGolden(),
            DnsCompressionGolden(),
            DnsDecodeGolden(),
            DnsMalformedRejected(),
            DnsRecordSetRoundTrip(),
            OperationalRoundTrip(),
            CommissionableRoundTrip(),
            CommissionerRoundTrip(),
            UdcIdentificationRoundTrip(),
            UdcCommissionerRoundTrip(),
        };
        results.AddRange(CompressedFabricIdVectors());

        sw.Stop();
        var report = new KatReport(results, sw.Elapsed);

        log.WriteLine("Matter DNS-SD / mDNS discovery Known-Answer Tests");
        log.WriteLine("=================================================");
        foreach (KatResult r in results)
        {
            string tag = r.Status switch
            {
                KatStatus.Pass => "PASS",
                KatStatus.Fail => "FAIL",
                _ => "SKIP",
            };
            log.WriteLine($"[{tag}] {r.Primitive,-19} {r.Name}");
            if (r.Detail is not null && r.Status != KatStatus.Pass)
                log.WriteLine($"         {r.Detail}");
        }
        log.WriteLine("-------------------------------------------------");
        log.WriteLine($"{report.Passed} passed, {report.Failed} failed, {report.Skipped} skipped " +
                      $"({report.Elapsed.TotalMilliseconds:F1} ms).");

        return report;
    }

    // ---------------------------------------------------------------------
    // Dns_Wire — RFC 1035/6762/6763 codec (exact bytes)
    // ---------------------------------------------------------------------
    private static KatResult DnsQuestionGolden()
    {
        // Header (id=0, flags=0, qd=1) + "_matter._tcp.local" + QTYPE=PTR(12) + QCLASS=IN(1).
        return Kat("Dns_Wire", "encode PTR question _matter._tcp.local (QM)", () =>
        {
            var message = new DnsMessage
            {
                Questions = [new DnsQuestion { Name = DnsSdServiceType.Operational.ServiceName, Type = DnsRecordType.Ptr }],
            };
            return Hex(message.ToArray());
        },
        "000000000001000000000000075f6d6174746572045f746370056c6f63616c00000c0001");
    }

    private static KatResult DnsCompressionGolden()
    {
        // Two PTR questions sharing the "local" suffix: the second name's "local" label is a compression
        // pointer (0xC019) back to the first name's "local" at offset 25 (RFC 1035 §4.1.4).
        return Kat("Dns_Wire", "shared-suffix name compression", () =>
        {
            var message = new DnsMessage
            {
                Questions =
                [
                    new DnsQuestion { Name = DnsSdServiceType.Operational.ServiceName, Type = DnsRecordType.Ptr },
                    new DnsQuestion { Name = DnsSdServiceType.Commissionable.ServiceName, Type = DnsRecordType.Ptr },
                ],
            };
            return Hex(message.ToArray());
        },
        "000000000002000000000000075f6d6174746572045f746370056c6f63616c00000c0001085f6d617474657263045f756470c019000c0001");
    }

    private static KatResult DnsDecodeGolden()
    {
        return Kat("Dns_Wire", "decode PTR question golden", () =>
        {
            byte[] bytes = FromHex("000000000001000000000000075f6d6174746572045f746370056c6f63616c00000c0001");
            if (!DnsMessage.TryParse(bytes, out DnsMessage message)) return "parse-failed";
            if (message.Questions.Count != 1) return "wrong-question-count";

            DnsQuestion question = message.Questions[0];
            if (question.Type != DnsRecordType.Ptr) return "wrong-type";
            if (question.Name.ToString() != "_matter._tcp.local") return "wrong-name";
            if (question.UnicastResponse) return "unexpected-qu-bit";
            return "ok";
        }, "ok");
    }

    private static KatResult DnsMalformedRejected()
    {
        return Kat("Dns_Wire", "reject truncated and self-referential input", () =>
        {
            // A name that is a compression pointer to itself must not be accepted (loop guard).
            if (DnsMessage.TryParse(FromHex("000000000001000000000000c00c"), out _)) return "accepted-self-pointer";

            // A datagram shorter than the 12-byte header must not be accepted.
            if (DnsMessage.TryParse(FromHex("0000"), out _)) return "accepted-truncated";
            return "ok";
        }, "ok");
    }

    private static KatResult DnsRecordSetRoundTrip()
    {
        // The operational record set exercises PTR/SRV/TXT/AAAA with shared names, so this round-trip
        // covers both emitting and following compression pointers.
        return Kat("Dns_Wire", "operational record set round-trip", () =>
        {
            DnsSdService service = OperationalAdvertisement.Build(SampleOperational(), SampleHost());
            var message = new DnsMessage { Flags = DnsFlags.MulticastResponse, Answers = service.ToRecords() };

            byte[] encoded = message.ToArray();
            if (!DnsMessage.TryParse(encoded, out DnsMessage decoded)) return "decode-failed";
            return Hex(encoded) == Hex(decoded.ToArray()) ? "ok" : "round-trip-differs";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Mdns_Cfid — compressed fabric id derivation (§4.3.2.2)
    // ---------------------------------------------------------------------
    private static IEnumerable<KatResult> CompressedFabricIdVectors()
    {
        yield return Kat("Mdns_Cfid", "derivation is deterministic and salt-sensitive", () =>
        {
            byte[] rootPublicKey = P256Curve.Encode(P256Curve.G);
            CompressedFabricId a = CompressedFabricIdentifier.Derive(rootPublicKey, new FabricId(0x2906C1A000000001UL));
            CompressedFabricId b = CompressedFabricIdentifier.Derive(rootPublicKey, new FabricId(0x2906C1A000000001UL));
            if (a != b) return "non-deterministic";

            CompressedFabricId c = CompressedFabricIdentifier.Derive(rootPublicKey, new FabricId(0x2906C1A000000002UL));
            return a == c ? "salt-insensitive" : "ok";
        }, "ok");

        yield return Kat("Mdns_Cfid", "rejects non-uncompressed root key", () =>
        {
            try
            {
                CompressedFabricIdentifier.Derive(new byte[64], new FabricId(1));
                return "accepted-bad-length";
            }
            catch (ArgumentException)
            {
                // Expected: only a 65-byte 0x04-tagged point is valid.
            }

            try
            {
                var wrongPrefix = new byte[65];
                wrongPrefix[0] = 0x05;
                CompressedFabricIdentifier.Derive(wrongPrefix, new FabricId(1));
                return "accepted-bad-prefix";
            }
            catch (ArgumentException)
            {
                // Expected.
            }

            return "ok";
        }, "ok");

        yield return CompressedFabricIdSpecVectorOrSkip();
    }

    private static KatResult CompressedFabricIdSpecVectorOrSkip()
    {
        const string name = "compressed-fabric-id spec fixed vector";
        if (SpecCfidRootPublicKey.Length == 0 || SpecCfidExpected.Length == 0)
        {
            return new KatResult("Mdns_Cfid", name, KatStatus.Skip,
                "paste an authoritative connectedhomeip compressed-fabric-id vector into the SpecCfid* " +
                "constants to enable wire-compatibility validation");
        }

        return Kat("Mdns_Cfid", name, () =>
        {
            byte[] rootPublicKey = FromHex(SpecCfidRootPublicKey);
            CompressedFabricId cfid = CompressedFabricIdentifier.Derive(
                rootPublicKey, new FabricId(Convert.ToUInt64(SpecCfidFabricId, 16)));
            return $"{cfid.Value:X16}".ToLowerInvariant();
        },
        SpecCfidExpected.ToLowerInvariant());
    }

    // ---------------------------------------------------------------------
    // Mdns_Operational — _matter._tcp (hex naming)
    // ---------------------------------------------------------------------
    private static KatResult OperationalRoundTrip()
    {
        return Kat("Mdns_Operational", "advertise→resolve round-trip", () =>
        {
            MatterHostInfo host = SampleHost();
            OperationalServiceInfo info = SampleOperational();
            DnsSdService service = OperationalAdvertisement.Build(info, host);

            // Instance name is <CompressedFabricId>-<NodeId>: 16 + '-' + 16 uppercase hex, never "0x".
            if (service.InstanceName.Length != 33 || service.InstanceName[16] != '-') return "bad-instance-shape";
            if (service.InstanceName.Contains("0x", StringComparison.OrdinalIgnoreCase)) return "instance-contains-0x";

            CompressedFabricId cfid = CompressedFabricIdentifier.Derive(info.RootPublicKey.Span, info.FabricId);
            if (service.InstanceName != $"{cfid.Value:X16}-{info.NodeId.Value:X16}") return "instance-mismatch";
            if (!service.Subtypes.Contains($"_I{cfid.Value:X16}")) return "missing-_I-subtype";

            if (!DiscoveredOperationalNode.TryParse(ToDiscovered(service), out DiscoveredOperationalNode node)) return "parse-failed";
            if (node.NodeId != info.NodeId) return "nodeid-mismatch";
            if (node.CompressedFabricId != cfid) return "cfid-mismatch";
            if (node.SessionParameters.IdleRetransmitTimeout != TimeSpan.FromMilliseconds(500)) return "sii-mismatch";
            if (node.SessionParameters.ActiveRetransmitTimeout != TimeSpan.FromMilliseconds(300)) return "sai-mismatch";
            if (node.SessionParameters.ActiveThreshold != TimeSpan.FromMilliseconds(4000)) return "sat-mismatch";
            return "ok";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Mdns_Commissionable — _matterc._udp (decimal naming)
    // ---------------------------------------------------------------------
    private static KatResult CommissionableRoundTrip()
    {
        return Kat("Mdns_Commissionable", "advertise→resolve round-trip", () =>
        {
            MatterHostInfo host = SampleHost();
            var info = new CommissionableServiceInfo
            {
                InstanceId = 0xB75AFB458ECDA666UL,
                Discriminator = 3840,
                Mode = CommissioningMode.Basic,
                VendorId = new VendorId(0xFFF1),
                ProductId = 0x8000,
                DeviceType = new DeviceTypeId(10),
                DeviceName = "Living Room",
            };
            DnsSdService service = CommissionableAdvertisement.Build(info, host);

            if (service.InstanceName != "B75AFB458ECDA666") return "instance-mismatch";

            // Subtypes and TXT values are DECIMAL here (contrast with operational hex).
            foreach (string subtype in (string[])["_L3840", "_S15", "_V65521", "_T10", "_CM"])
                if (!service.Subtypes.Contains(subtype)) return $"missing-subtype-{subtype}";

            foreach (string entry in (string[])["D=3840", "VP=65521+32768", "CM=1", "DT=10", "DN=Living Room"])
                if (!service.TxtEntries.Contains(entry)) return $"missing-txt-{entry}";

            if (!DiscoveredCommissionableNode.TryParse(ToDiscovered(service), out DiscoveredCommissionableNode node)) return "parse-failed";
            if (node.Discriminator != 3840) return "discriminator-mismatch";
            if (node.VendorId != new VendorId(0xFFF1)) return "vendor-mismatch";
            if (node.ProductId != 0x8000) return "product-mismatch";
            if (node.Mode != CommissioningMode.Basic) return "mode-mismatch";
            if (node.DeviceType != new DeviceTypeId(10)) return "devicetype-mismatch";
            if (node.DeviceName != "Living Room") return "devicename-mismatch";
            return "ok";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Mdns_Commissioner — _matterd._udp (no discriminator / no CM)
    // ---------------------------------------------------------------------
    private static KatResult CommissionerRoundTrip()
    {
        return Kat("Mdns_Commissioner", "advertise→resolve round-trip", () =>
        {
            MatterHostInfo host = SampleHost();
            var info = new CommissionerServiceInfo
            {
                InstanceId = 0x1122334455667788UL,
                Port = 5560,
                VendorId = new VendorId(0xFFF1),
                ProductId = 0x8001,
                DeviceType = new DeviceTypeId(35),
                DeviceName = "Living Room TV",
            };
            DnsSdService service = CommissionerAdvertisement.Build(info, host);

            if (service.InstanceName != "1122334455667788") return "instance-mismatch";
            if (service.Port != 5560) return "port-not-udc"; // the UDC port, not the operational host port

            // A commissioner is not being commissioned: no discriminator, short-discriminator, or CM.
            foreach (string subtype in service.Subtypes)
                if (subtype.StartsWith("_L", StringComparison.Ordinal) ||
                    subtype.StartsWith("_S", StringComparison.Ordinal) ||
                    subtype == "_CM")
                    return $"unexpected-subtype-{subtype}";

            foreach (string entry in service.TxtEntries)
                if (entry.StartsWith("D=", StringComparison.Ordinal) || entry.StartsWith("CM=", StringComparison.Ordinal))
                    return $"unexpected-txt-{entry}";

            if (!service.Subtypes.Contains("_V65521")) return "missing-_V";
            if (!service.Subtypes.Contains("_T35")) return "missing-_T";
            if (!service.TxtEntries.Contains("VP=65521+32769")) return "missing-VP";

            if (!DiscoveredCommissionerNode.TryParse(ToDiscovered(service), out DiscoveredCommissionerNode node)) return "parse-failed";
            if (node.Port != 5560) return "resolved-port-mismatch";
            if (node.VendorId != new VendorId(0xFFF1)) return "vendor-mismatch";
            if (node.ProductId != 0x8001) return "product-mismatch";
            if (node.DeviceType != new DeviceTypeId(35)) return "devicetype-mismatch";
            if (node.DeviceName != "Living Room TV") return "devicename-mismatch";
            return "ok";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Udc_Wire — User-Directed Commissioning declarations (§5.3.4)
    // ---------------------------------------------------------------------
    private static KatResult UdcIdentificationRoundTrip()
    {
        return Kat("Udc_Wire", "IdentificationDeclaration round-trip", () =>
        {
            var message = new IdentificationDeclarationMessage
            {
                InstanceName = "B75AFB458ECDA666",
                VendorId = new VendorId(0xFFF1),
                ProductId = 0x8000,
                DeviceName = "Living Room",
                PairingHint = 0x21,
                NoPasscode = true,
                CommissionerPasscode = true,
                TargetAppList = [new TargetAppInfo { VendorId = new VendorId(0xFFF1), ProductId = 0x1234 }],
            };

            byte[] encoded = message.ToArray();
            if (!IdentificationDeclarationMessage.TryParse(encoded, out IdentificationDeclarationMessage parsed)) return "parse-failed";
            if (parsed.InstanceName != "B75AFB458ECDA666") return "instance-mismatch";
            if (parsed.VendorId != new VendorId(0xFFF1)) return "vendor-mismatch";
            if (parsed.ProductId != 0x8000) return "product-mismatch";
            if (parsed.DeviceName != "Living Room") return "devicename-mismatch";
            if (parsed.PairingHint != 0x21u) return "pairinghint-mismatch";
            if (!parsed.NoPasscode) return "nopasscode-lost";
            if (!parsed.CommissionerPasscode) return "commissionerpasscode-lost";
            if (parsed.CdUponPasscodeDialog) return "spurious-flag-set"; // an unset flag must decode false
            if (parsed.TargetAppList is not { Count: 1 } apps || apps[0].ProductId != 0x1234) return "targetapp-mismatch";

            return Hex(encoded) == Hex(parsed.ToArray()) ? "ok" : "reencode-differs";
        }, "ok");
    }

    private static KatResult UdcCommissionerRoundTrip()
    {
        return Kat("Udc_Wire", "CommissionerDeclaration round-trip", () =>
        {
            var message = new CommissionerDeclarationMessage
            {
                ErrorCode = CommissionerDeclarationError.PaseAuthFailed,
                NeedsPasscode = true,
                CommissionerPasscode = true,
            };

            byte[] encoded = message.ToArray();
            if (!CommissionerDeclarationMessage.TryParse(encoded, out CommissionerDeclarationMessage parsed)) return "parse-failed";
            if (parsed.ErrorCode != CommissionerDeclarationError.PaseAuthFailed) return "error-mismatch";
            if (!parsed.NeedsPasscode) return "needspasscode-lost";
            if (!parsed.CommissionerPasscode) return "commissionerpasscode-lost";
            if (parsed.NoAppsFound) return "spurious-flag-set";

            return Hex(encoded) == Hex(parsed.ToArray()) ? "ok" : "reencode-differs";
        }, "ok");
    }

    // ---------------------------------------------------------------------
    // Shared fixtures
    // ---------------------------------------------------------------------
    private static MatterHostInfo SampleHost() => new()
    {
        HostName = new DnsName("B75AFB458ECDA666", MdnsConstants.LocalDomain),
        Addresses = [IPAddress.Parse("fe80::2e0:4cff:fe68:12ab")],
        Port = 5540,
        Mrp = ReliableMessageProtocolConfig.Default,
    };

    private static OperationalServiceInfo SampleOperational() => new()
    {
        // A real on-curve point (the generator) satisfies the uncompressed-P-256 contract of the deriver.
        RootPublicKey = P256Curve.Encode(P256Curve.G),
        FabricId = new FabricId(0x2906C1A000000001UL),
        NodeId = new NodeId(0x8FC7772401CD0696UL),
    };

    private static DiscoveredService ToDiscovered(DnsSdService service) => new()
    {
        ServiceType = service.ServiceType,
        InstanceName = service.InstanceName,
        HostName = service.HostName,
        Port = service.Port,
        Addresses = service.Addresses,
        TxtEntries = service.TxtEntries,
    };

    // ---------------------------------------------------------------------
    // Harness plumbing (mirrors MatterCryptoKat)
    // ---------------------------------------------------------------------
    private static KatResult Kat(string primitive, string name, Func<string> actual, string expected)
    {
        try
        {
            string got = actual();
            bool ok = FixedTimeStringEquals(got, expected);
            return new KatResult(primitive, name, ok ? KatStatus.Pass : KatStatus.Fail,
                ok ? null : $"expected {expected}, got {got}");
        }
        catch (Exception ex)
        {
            return new KatResult(primitive, name, KatStatus.Fail, $"threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool FixedTimeStringEquals(string a, string b) =>
        a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));

    private static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
}