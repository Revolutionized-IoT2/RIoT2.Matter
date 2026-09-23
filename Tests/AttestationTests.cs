using System.Buffers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using RIoT2.Matter.Controller.Commissioning.Attestation;
using RIoT2.Matter.Credentials;
using RIoT2.Matter.Tlv;
using Xunit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RIoT2.Matter.Controller.Hosting;

namespace RIoT2.Matter.Tests;

public sealed class AttestationTests
{
    [Fact]
    public void ProductionConfigurationBindsBothExplicitTrustCollections()
    {
        using var fixture = new AttestationFixture();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["MatterController:TrustedPaaCertificates:0"] = Convert.ToBase64String(fixture.Paa.RawData),
            ["MatterController:TrustedCertificationDeclarationSigners:0"] = Convert.ToBase64String(fixture.Cd.RawData),
        }).Build();
        var services = new ServiceCollection();
        services.Configure<MatterControllerOptions>(configuration.GetSection("MatterController"));
        services.AddMatterController();
        using var provider = services.BuildServiceProvider();
        Assert.True(provider.GetRequiredService<IDeviceAttestationVerifier>().Verify(fixture.Information()).IsSuccess);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTrustConfigurationIsClearlyRejected(bool hasPaa)
    {
        using var fixture = new AttestationFixture();
        var services = new ServiceCollection();
        services.AddMatterController(options =>
        {
            if (hasPaa) { options.TrustedPaaCertificates.Add(fixture.Paa.RawData); }
        });
        using var provider = services.BuildServiceProvider();
        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IDeviceAttestationVerifier>());
        Assert.Contains("TrustedCertificationDeclarationSigners", failure.Message);
        if (!hasPaa) { Assert.Contains("TrustedPaaCertificates", failure.Message); }
    }

    [Fact]
    public void TrustedChainAndDeclarationPass()
    {
        using var fixture = new AttestationFixture();
        Assert.True(fixture.Verifier().Verify(fixture.Information()).IsSuccess);
    }

    [Theory]
    [InlineData("paa")]
    [InlineData("cd")]
    [InlineData("pai")]
    [InlineData("nonce")]
    [InlineData("signature")]
    [InlineData("vendor")]
    [InlineData("product")]
    [InlineData("missing-identity")]
    [InlineData("truncated")]
    [InlineData("duplicate")]
    [InlineData("embedded-untrusted")]
    public void InvalidAttestationFailsClosed(string fault)
    {
        using var fixture = new AttestationFixture();
        using var other = new AttestationFixture();
        var info = fixture.Information();
        var verifier = fixture.Verifier();
        switch (fault)
        {
            case "paa": verifier = new DeviceAttestationVerifier([], [fixture.Cd.RawData]); break;
            case "cd": verifier = new DeviceAttestationVerifier([fixture.Paa.RawData], []); break;
            case "pai": info = info with { ProductAttestationIntermediateCertificate = other.Pai.RawData }; break;
            case "nonce": info = info with { AttestationNonce = new byte[32] }; break;
            case "signature": info = info with { AttestationSignature = new byte[64] }; break;
            case "vendor": info = info with { ExpectedVendorId = 2 }; break;
            case "product": info = info with { ExpectedProductId = 2 }; break;
            case "missing-identity": info = info with { ExpectedProductId = null }; break;
            case "truncated": info = fixture.Sign(info with { AttestationElements = info.AttestationElements[..^1] }); break;
            case "duplicate": info = fixture.Information(duplicateNonce: true); break;
            case "embedded-untrusted": info = fixture.Information(cd: other.Declaration(includeCertificate: true)); break;
        }
        Assert.False(verifier.Verify(info).IsSuccess);
    }

    [Fact]
    public void TrustedSignatureDoesNotOverrideWrongDeclarationIdentity()
    {
        using var fixture = new AttestationFixture();
        var info = fixture.Information(cd: fixture.Declaration(vendor: 3));
        Assert.False(fixture.Verifier().Verify(info).IsSuccess);
    }

    [Fact]
    public void UnauthorizedPaaIsRejected()
    {
        using var fixture = new AttestationFixture();
        var info = fixture.Information(cd: fixture.Declaration(authorizedPaa: new byte[20]));
        Assert.False(fixture.Verifier().Verify(info).IsSuccess);
    }
}

internal sealed class AttestationFixture : IDisposable
{
    public const ushort Vendor = 0xFFF1;
    public const ushort Product = 0x8001;
    private readonly ECDsa _paaKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _paiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _dacKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _cdKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public X509Certificate2 Paa { get; }
    public X509Certificate2 Pai { get; }
    public X509Certificate2 Dac { get; }
    public X509Certificate2 Cd { get; }
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] _challenge = RandomNumberGenerator.GetBytes(16);

    public AttestationFixture()
    {
        var before = DateTimeOffset.UtcNow.AddDays(-1);
        var after = DateTimeOffset.UtcNow.AddDays(1);
        Paa = Request("PAA", _paaKey, ca: true, vendor: false, product: false).CreateSelfSigned(before, after);
        Pai = Request("PAI", _paiKey, ca: true, vendor: true, product: false)
            .Create(Paa.SubjectName, X509SignatureGenerator.CreateForECDsa(_paaKey), before, after, [1]);
        Dac = Request("DAC", _dacKey, ca: false, vendor: true, product: true)
            .Create(Pai.SubjectName, X509SignatureGenerator.CreateForECDsa(_paiKey), before, after, [2]);
        Cd = Request("CD", _cdKey, ca: false, vendor: false, product: false).CreateSelfSigned(before, after);
    }

    public DeviceAttestationVerifier Verifier() => new([Paa.RawData], [Cd.RawData]);

    public AttestationInformation Information(byte[]? cd = null, bool duplicateNonce = false)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteByteString(TlvTag.ContextSpecific(1), cd ?? Declaration());
        writer.WriteByteString(TlvTag.ContextSpecific(2), _nonce);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), 1);
        if (duplicateNonce) { writer.WriteByteString(TlvTag.ContextSpecific(2), _nonce); }
        writer.EndContainer();
        return Sign(new AttestationInformation
        {
            ExpectedVendorId = Vendor, ExpectedProductId = Product,
            DeviceAttestationCertificate = Dac.RawData,
            ProductAttestationIntermediateCertificate = Pai.RawData,
            AttestationNonce = _nonce, AttestationChallenge = _challenge,
            AttestationElements = buffer.WrittenSpan.ToArray(), AttestationSignature = [],
        });
    }

    public AttestationInformation Sign(AttestationInformation information) => information with
    {
        AttestationSignature = _dacKey.SignData(
            [.. information.AttestationElements, .. information.AttestationChallenge],
            HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation),
    };

    public byte[] Declaration(ushort vendor = Vendor, bool includeCertificate = false, byte[]? authorizedPaa = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new TlvWriter(buffer);
        writer.StartStructure(TlvTag.Anonymous);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(0), 1);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(1), vendor);
        writer.StartArray(TlvTag.ContextSpecific(2));
        writer.WriteUnsignedInteger(TlvTag.Anonymous, Product);
        writer.EndContainer();
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(3), 0x100);
        writer.WriteUtf8String(TlvTag.ContextSpecific(4), "ZIG20141ZB330001-24");
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(5), 0);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(6), 0);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(7), 1);
        writer.WriteUnsignedInteger(TlvTag.ContextSpecific(8), 0);
        if (authorizedPaa is not null)
        {
            writer.StartArray(TlvTag.ContextSpecific(11));
            writer.WriteByteString(TlvTag.Anonymous, authorizedPaa);
            writer.EndContainer();
        }
        writer.EndContainer();
        var cms = new SignedCms(new ContentInfo(buffer.WrittenSpan.ToArray()), detached: false);
        cms.ComputeSignature(new CmsSigner(SubjectIdentifierType.SubjectKeyIdentifier, Cd)
        {
            IncludeOption = includeCertificate ? X509IncludeOption.EndCertOnly : X509IncludeOption.None,
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"),
        });
        return cms.Encode();
    }

    private static CertificateRequest Request(string name, ECDsa key, bool ca, bool vendor, bool product)
    {
        var dn = new X500DistinguishedNameBuilder();
        dn.AddCommonName(name);
        if (vendor) { dn.Add(AttestationCertificateChainVerifier.VendorIdOid, Vendor.ToString("X4")); }
        if (product) { dn.Add(AttestationCertificateChainVerifier.ProductIdOid, Product.ToString("X4")); }
        var request = new CertificateRequest(dn.Build(), key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(ca, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(ca
            ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign : X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request;
    }

    public void Dispose()
    {
        Paa.Dispose(); Pai.Dispose(); Dac.Dispose(); Cd.Dispose();
        _paaKey.Dispose(); _paiKey.Dispose(); _dacKey.Dispose(); _cdKey.Dispose();
    }
}
