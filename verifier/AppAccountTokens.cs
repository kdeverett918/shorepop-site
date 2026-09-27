using System.Security.Cryptography;
using System.Text;

namespace Shorepop.Verifier;

/// <summary>
/// StoreKit <c>appAccountToken</c> derivation shared with the client: RFC 4122 UUID version 5 (SHA-1) of the
/// name <c>"shorepop:ugs:" + playerId</c> (UTF-8) under the URL namespace <c>6ba7b811-9dad-11d1-80b4-00c04fd430c8</c>,
/// written lowercase and hyphenated. Example: player <c>abc123</c> -> <c>0f752aa9-33c5-54b1-b936-68d3b28f2043</c>.
/// </summary>
public static class AppAccountTokens
{
    public const string NamespaceUrl = "6ba7b811-9dad-11d1-80b4-00c04fd430c8";
    public const string NamePrefix = "shorepop:ugs:";

    public static string Derive(string playerId) => Uuid5(NamespaceUrl, NamePrefix + playerId);

    /// <summary>RFC 4122 section 4.3: namespace bytes in network order, then the name; version 5, variant 10xx.</summary>
    public static string Uuid5(string namespaceUuid, string name)
    {
        byte[] ns = Convert.FromHexString(namespaceUuid.Replace("-", ""));
        byte[] nameBytes = Encoding.UTF8.GetBytes(name);
        byte[] input = new byte[ns.Length + nameBytes.Length];
        ns.CopyTo(input, 0);
        nameBytes.CopyTo(input, ns.Length);
        byte[] hash = SHA1.HashData(input);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        string hex = Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..32]}";
    }

    /// <summary>Canonical lowercase hyphenated form, or null when absent or not a UUID.</summary>
    public static string? Normalize(string? token) =>
        !string.IsNullOrWhiteSpace(token) && Guid.TryParse(token.Trim(), out var guid) ? guid.ToString("D") : null;
}
