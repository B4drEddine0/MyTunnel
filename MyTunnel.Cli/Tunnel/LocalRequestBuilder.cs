using System.Net;
using MyTunnel.Contracts;

namespace MyTunnel.Cli.Tunnel;

/// <summary>Translates tunnelled requests into requests for the local origin, and responses back.</summary>
public static class LocalRequestBuilder
{
    /// <summary>
    /// Builds the local URL by appending the origin-form target to the configured origin and then verifying
    /// the authority did not change, so a crafted target can never redirect the CLI to another host.
    /// </summary>
    public static Uri BuildUri(Uri localOrigin, string target)
    {
        if (!HttpValidation.IsOriginFormTarget(target)
            || !Uri.TryCreate(localOrigin.GetLeftPart(UriPartial.Authority) + target, UriKind.Absolute, out var uri)
            || uri.Scheme != localOrigin.Scheme
            || !uri.Host.Equals(localOrigin.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != localOrigin.Port
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ProtocolException(ProtocolErrorCodes.InvalidMessage, "Request target does not resolve to the local origin.");
        }

        return uri;
    }

    public static HttpRequestMessage BuildRequest(Uri localOrigin, TunnelHttpRequest request, Stream? body)
    {
        var message = new HttpRequestMessage(new HttpMethod(request.Method), BuildUri(localOrigin, request.Target))
        {
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        if (body is not null)
        {
            message.Content = new StreamContent(body, TunnelProtocol.MaxBodyChunkBytes);
        }

        var connectionTokens = HttpValidation.ParseConnectionTokens(
            request.Headers.Where(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
        foreach (var header in request.Headers)
        {
            if (HttpValidation.IsHopByHop(header.Name, connectionTokens)
                || header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                // The gateway already handled 100-continue with the visitor.
                || header.Name.Equals("Expect", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
            {
                message.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        // Development servers commonly reject unknown Host values; the public host is in X-Forwarded-Host.
        message.Headers.Host = localOrigin.IsDefaultPort ? localOrigin.Host : $"{localOrigin.Host}:{localOrigin.Port}";
        return message;
    }

    public static List<HttpHeader> GetResponseHeaders(HttpResponseMessage response)
    {
        var all = response.Headers.Concat(response.Content.Headers).ToList();
        var connectionTokens = HttpValidation.ParseConnectionTokens(
            all.Where(h => h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)).SelectMany(h => h.Value));

        var headers = new List<HttpHeader>();
        foreach (var (name, values) in all)
        {
            if (HttpValidation.IsHopByHop(name, connectionTokens) || !HttpValidation.IsToken(name))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (HttpValidation.IsValidHeaderValue(value))
                {
                    headers.Add(new HttpHeader(name, value));
                }
            }
        }

        return headers;
    }

    /// <summary>The HTTP client used for the local origin: no redirects, cookies, proxies or decompression, so responses pass through untouched.</summary>
    public static HttpMessageInvoker CreateLocalInvoker() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        ConnectCallback = ConnectAsync,
    });

    /// <summary>
    /// "localhost" resolves to ::1 and 127.0.0.1, and dev servers often listen on only one of them. Windows takes
    /// about two seconds to report a refused loopback connection, so try both families at once and keep the winner.
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        if (!endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return await ConnectToAsync(new DnsEndPoint(endpoint.Host, endpoint.Port), cancellationToken);
        }

        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var attempts = new List<Task<Stream>>
        {
            ConnectToAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), raceCts.Token),
            ConnectToAsync(new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port), raceCts.Token),
        };

        Exception? lastError = null;
        while (attempts.Count > 0)
        {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);
            if (finished.IsCompletedSuccessfully)
            {
                await raceCts.CancelAsync();
                foreach (var loser in attempts)
                {
                    _ = loser.ContinueWith(t => t.Result.Dispose(), TaskContinuationOptions.OnlyOnRanToCompletion);
                }

                return finished.Result;
            }

            lastError = finished.Exception?.InnerException ?? lastError;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw lastError ?? new HttpRequestException("Could not connect to localhost.");
    }

    private static async Task<Stream> ConnectToAsync(EndPoint endpoint, CancellationToken cancellationToken)
    {
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
