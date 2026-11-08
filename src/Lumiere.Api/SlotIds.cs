using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lumiere.Api;

/// <summary>Signed slot ids (ADR-005): base64url(startUtc|serviceKey|expiresUtc) + "." + truncated HMAC-SHA256.</summary>
public sealed class SlotIds(byte[] key, TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);
    private const int MacBytes = 16;
    private const string Format = "yyyy-MM-ddTHH:mm:ssZ";

    public string Create(DateTimeOffset startUtc, string serviceKey)
    {
        var expires = clock.GetUtcNow() + Lifetime;
        var payload = Encoding.UTF8.GetBytes(
            $"{startUtc.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture)}|{serviceKey}|{expires.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture)}");
        return $"{Base64Url(payload)}.{Base64Url(Mac(payload))}";
    }

    /// <summary>Returns the slot when the id is authentic and unexpired; otherwise null.</summary>
    public (DateTimeOffset StartUtc, Service Service)? Verify(string? slotId)
    {
        try
        {
            var parts = (slotId ?? "").Trim().Split('.');
            if (parts.Length != 2) return null;
            var payload = FromBase64Url(parts[0]);
            if (!CryptographicOperations.FixedTimeEquals(Mac(payload), FromBase64Url(parts[1]))) return null;

            var fields = Encoding.UTF8.GetString(payload).Split('|');
            if (fields.Length != 3) return null;
            var start = DateTimeOffset.ParseExact(fields[0], Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var expires = DateTimeOffset.ParseExact(fields[2], Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            var service = Salon.FindService(fields[1]);
            if (service is null || clock.GetUtcNow() > expires) return null;
            return (start.ToUniversalTime(), service);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private byte[] Mac(byte[] payload) => HMACSHA256.HashData(key, payload)[..MacBytes];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string s)
    {
        var b = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '='));
    }
}
