namespace RIoT2.Matter.Onboarding;

/// <summary>
/// Renders a QR onboarding string (from <see cref="QrCodePayload.Encode"/>) into an image. Kept out of
/// the portable core so no image or native dependency is imposed; a host supplies the implementation.
/// </summary>
public interface IQrCodeRenderer
{
    /// <summary>Renders the payload string to image bytes in an implementation-defined format (e.g. PNG or SVG).</summary>
    byte[] Render(string qrPayload);
}