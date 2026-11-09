using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Lumiere.Api;

/// <summary>Phone numbers and names given by the caller (ADR-006 fallback: spoken, normalised).</summary>
public static partial class Caller
{
    /// <summary>E.164, default country +41; null when missing or invalid.</summary>
    public static string? NormalisePhone(string? input)
    {
        var s = Separators().Replace(input ?? "", "");
        if (s.StartsWith("00")) s = "+" + s[2..];
        else if (s.StartsWith('0')) s = "+41" + s[1..];
        if (s.StartsWith("+410")) s = "+41" + s[4..]; // "+41 (0)79 ..."
        if (!E164().IsMatch(s)) return null;
        if (s.StartsWith("+41") && !SwissNational().IsMatch(s[3..])) return null;
        return s;
    }

    /// <summary>
    /// Caller identity (ADR-014 / ADR-006 amendment): a valid call-metadata <c>phoneNumber</c> wins and
    /// overrides the spoken number; otherwise the spoken <c>caller_phone</c>; otherwise none.
    /// </summary>
    public static (string? Phone, string Source) Resolve(string? metadataPhone, string? spokenPhone) =>
        NormalisePhone(metadataPhone) is { } fromMetadata ? (fromMetadata, "metadata")
        : NormalisePhone(spokenPhone) is { } spoken ? (spoken, "spoken")
        : (null, "none");

    /// <summary>"+41 79 *** ** 12" — safe for logs.</summary>
    public static string MaskPhone(string? e164)
    {
        if (string.IsNullOrEmpty(e164) || e164.Length < 6) return "***";
        return e164.StartsWith("+41") && e164.Length == 12
            ? $"+41 {e164[3..5]} *** ** {e164[^2..]}"
            : $"{e164[..3]} *** {e164[^2..]}";
    }

    /// <summary>"+41 79 123 45 67" for Swiss numbers, E.164 otherwise — for the private staff chat only (ADR-007).</summary>
    public static string FormatPhone(string e164) =>
        e164.StartsWith("+41") && e164.Length == 12
            ? $"+41 {e164[3..5]} {e164[5..8]} {e164[8..10]} {e164[10..]}"
            : e164;

    public static string PhoneHash(string e164, byte[] key) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(e164))).ToLowerInvariant();

    /// <summary>Trimmed name of 1–50 letters, spaces, hyphens or apostrophes; null otherwise.</summary>
    public static string? NormaliseName(string? input)
    {
        var s = Spaces().Replace((input ?? "").Trim(), " ");
        return s.Length is >= 1 and <= 50 && Name().IsMatch(s) ? s : null;
    }

    [GeneratedRegex(@"[\s\-./()]")]
    private static partial Regex Separators();

    [GeneratedRegex(@"^\+[1-9]\d{7,14}$")]
    private static partial Regex E164();

    [GeneratedRegex(@"^[1-9]\d{8}$")]
    private static partial Regex SwissNational();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^\p{L}[\p{L} '’\-]*$")]
    private static partial Regex Name();
}
