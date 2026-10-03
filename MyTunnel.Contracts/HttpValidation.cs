namespace MyTunnel.Contracts;

/// <summary>RFC 9110 syntax checks plus the hop-by-hop header rules both proxy hops apply.</summary>
public static class HttpValidation
{
    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
    };

    public static bool IsToken(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        foreach (var c in value)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                || "!#$%&'*+-.^_`|~".Contains(c);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Accepts only "/path?query" targets, so a request can never name a different host.</summary>
    public static bool IsOriginFormTarget(string? target)
    {
        if (string.IsNullOrEmpty(target) || target[0] != '/' || target.Length > TunnelProtocol.MaxTargetLength)
        {
            return false;
        }

        foreach (var c in target)
        {
            if (c <= ' ' || c >= 0x7f || c == '#')
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsValidHeaderValue(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (var c in value)
        {
            if ((c < ' ' && c != '\t') || c == 0x7f)
            {
                return false;
            }
        }

        return true;
    }

    public static void ValidateHeaders(IReadOnlyList<HttpHeader>? headers)
    {
        if (headers is null)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "'headers' is required.");
        }

        if (headers.Count > TunnelProtocol.MaxHeaderCount)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Too many headers.");
        }

        var total = 0;
        foreach (var header in headers)
        {
            if (header is null || !IsToken(header.Name) || !IsValidHeaderValue(header.Value))
            {
                throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Invalid header.");
            }

            total += header.Name.Length + header.Value.Length;
        }

        if (total > TunnelProtocol.MaxHeaderBytes)
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Headers are too large.");
        }
    }

    /// <summary>
    /// True for headers that describe a single connection and must not be forwarded: the fixed RFC 9110 list
    /// plus any header named in the message's own <c>Connection</c> header.
    /// </summary>
    public static bool IsHopByHop(string name, IReadOnlySet<string>? connectionTokens = null) =>
        HopByHopHeaders.Contains(name) || (connectionTokens?.Contains(name) ?? false);

    public static HashSet<string> ParseConnectionTokens(IEnumerable<string> connectionHeaderValues)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in connectionHeaderValues)
        {
            foreach (var token in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }
}
