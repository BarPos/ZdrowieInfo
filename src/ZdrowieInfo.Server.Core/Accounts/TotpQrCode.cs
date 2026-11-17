using System.Text.Encodings.Web;
using QRCoder;

namespace ZdrowieInfo.Server.Accounts;

/// <summary>Kod QR dla aplikacji uwierzytelniającej, renderowany po stronie serwera jako SVG (bez JavaScriptu).</summary>
public static class TotpQrCode
{
    public const string Issuer = "ZdrowieInfo";

    public static string BuildUri(string userName, string unformattedKey) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"otpauth://totp/{UrlEncoder.Default.Encode(Issuer)}:{UrlEncoder.Default.Encode(userName)}?secret={unformattedKey}&issuer={UrlEncoder.Default.Encode(Issuer)}&digits=6");

    public static string ToSvg(string otpauthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(6);
    }
}
