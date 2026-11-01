#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Calendoro;

public sealed class WebhookSignatureException : CalendoroException
{
    public WebhookSignatureException() : base("invalid webhook signature") { }
}

/// <summary>Webhook signature verification, matching packages/webhooks.</summary>
public static class Webhooks
{
    public const long DefaultToleranceSeconds = 300;

    /// <summary>True when the Calendoro-Signature header signs the raw body with secret recently enough. False, never an exception, on bad input.</summary>
    public static bool VerifySignature(string? header, string secret, string body, long? nowSeconds = null, long toleranceSeconds = DefaultToleranceSeconds)
    {
        if (header is null) return false;
        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(','))
        {
            var i = part.IndexOf('=');
            if (i < 0) continue;
            var key = part[..i].Trim();
            var value = part[(i + 1)..].Trim();
            if (key == "t")
            {
                if (value.Length > 0 && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) timestamp = n;
            }
            else if (key == "v1")
            {
                signatures.Add(value);
            }
        }
        if (timestamp is null) return false;
        var now = nowSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(now - timestamp.Value) > toleranceSeconds) return false;
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp.Value}.{body}"));
        foreach (var s in signatures)
        {
            byte[] got;
            try
            {
                got = Convert.FromHexString(s);
            }
            catch (FormatException)
            {
                continue;
            }
            if (CryptographicOperations.FixedTimeEquals(got, expected)) return true;
        }
        return false;
    }

    /// <summary>Verifies a delivery and returns the parsed event. Pass the raw request body.</summary>
    public static JsonDocument ConstructEvent(string body, string? header, string secret, long? nowSeconds = null, long toleranceSeconds = DefaultToleranceSeconds)
    {
        if (!VerifySignature(header, secret, body, nowSeconds, toleranceSeconds)) throw new WebhookSignatureException();
        return JsonDocument.Parse(body);
    }
}
