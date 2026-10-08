using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace MyTunnel.Gateway.Tunnels;

/// <summary>The account a tunnel connection acts for.</summary>
public sealed record TunnelPrincipal(string UserId, string? CredentialId = null);

public interface ITunnelAuthenticator
{
    /// <summary>Returns the principal for <paramref name="token"/>, or null when it is missing, unknown, expired or revoked.</summary>
    ValueTask<TunnelPrincipal?> AuthenticateAsync(string? token, CancellationToken cancellationToken);
}

/// <summary>Validates tokens against SHA-256 digests in configuration. Intended for development and tests.</summary>
public sealed class StaticTokenAuthenticator(IOptions<GatewayOptions> options) : ITunnelAuthenticator
{
    public const string AnonymousUserId = "anonymous";

    public ValueTask<TunnelPrincipal?> AuthenticateAsync(string? token, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrEmpty(token))
        {
            return ValueTask.FromResult(settings.AllowAnonymousTunnels ? new TunnelPrincipal(AnonymousUserId) : null);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        foreach (var entry in settings.StaticTokens)
        {
            if (TryParseHex(entry.TokenSha256, out var expected) && CryptographicOperations.FixedTimeEquals(digest, expected))
            {
                return ValueTask.FromResult<TunnelPrincipal?>(new TunnelPrincipal(entry.UserId));
            }
        }

        return ValueTask.FromResult<TunnelPrincipal?>(null);
    }

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        try
        {
            bytes = Convert.FromHexString(hex);
            return bytes.Length == SHA256.HashSizeInBytes;
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }
}
