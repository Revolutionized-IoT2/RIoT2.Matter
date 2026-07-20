using QRCoder;

namespace RIoT2.Matter.OnOffSample;

/// <summary>Renders a Matter <c>MT:</c> onboarding string as an ASCII QR for the terminal.</summary>
internal static class ConsoleQr
{
    public static string Render(string payload)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        return new AsciiQRCode(data).GetGraphic(1);
    }
}