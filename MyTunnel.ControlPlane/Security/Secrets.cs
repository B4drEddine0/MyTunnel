using System.Security.Cryptography;
using System.Text;

namespace MyTunnel.ControlPlane.Security;

/// <summary>
/// Generation and verification of high-entropy secrets (access tokens, device codes).
/// They carry 256 bits of randomness, so a fast SHA-256 verifier is sufficient; passwords use PBKDF2 instead.
/// </summary>
public static class Secrets
{
    public const string AccessTokenPrefix = "mt_";
    private const string LowerAlphanumeric = "abcdefghijklmnopqrstuvwxyz0123456789";

    // No vowels (avoids accidental words) and no look-alike characters.
    private const string UserCodeAlphabet = "BCDFGHJKLMNPQRSTVWXZ";

    /// <summary>Creates "mt_&lt;10-char id&gt;_&lt;43-char secret&gt;". The id part is stored for lookup; the whole token only as a hash.</summary>
    public static (string Token, string Prefix, byte[] Hash) CreateAccessToken()
    {
        var prefix = AccessTokenPrefix + RandomNumberGenerator.GetString(LowerAlphanumeric, 10);
        var token = $"{prefix}_{CreateOpaque()}";
        return (token, prefix, Hash(token));
    }

    public static bool TryGetAccessTokenPrefix(string? token, out string prefix)
    {
        prefix = "";
        if (token is null || token.Length > 128 || !token.StartsWith(AccessTokenPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var separator = token.IndexOf('_', AccessTokenPrefix.Length);
        if (separator != AccessTokenPrefix.Length + 10)
        {
            return false;
        }

        prefix = token[..separator];
        return true;
    }

    public static string CreateOpaque(int bytes = 32) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static string CreateUserCode() => RandomNumberGenerator.GetString(UserCodeAlphabet, 8);

    /// <summary>Accepts "bcdf-ghjk", "BCDFGHJK" etc. and returns the canonical stored form.</summary>
    public static string NormalizeUserCode(string code) =>
        new string(code.Where(char.IsLetter).ToArray()).ToUpperInvariant();

    public static string FormatUserCode(string code) => code.Length == 8 ? $"{code[..4]}-{code[4..]}" : code;

    public static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    public static bool Matches(string value, byte[] expectedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(value), expectedHash);
}
