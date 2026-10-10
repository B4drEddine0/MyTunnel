using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using MyTunnel.Contracts;

namespace MyTunnel.Gateway.Tunnels;

/// <summary>The tunnel connection was closed while a request was in flight.</summary>
public sealed class TunnelDisconnectedException() : Exception("The tunnel client disconnected.");

/// <summary>The CLI cancelled the request (for example because the local application failed mid-response).</summary>
public sealed class TunnelRequestCancelledException(string? reason) : Exception($"The tunnel client cancelled the request: {reason ?? "no reason given"}.");

/// <summary>
/// One registered CLI connection. Owns the receive loop and correlates public HTTP requests with the
/// responses that come back over the WebSocket. Any number of requests may be in flight; responses are
/// matched by request id and may arrive in any order.
/// </summary>
public sealed class TunnelSession
{
    private const string GatewayPong = "Hello from MyTunnel Gateway!";

    private readonly TunnelSocket _socket;
    private readonly GatewayOptions _options;
    private readonly GatewayMetrics _metrics;
    private readonly IUsageSink _usage;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
    private readonly SemaphoreSlim _concurrency;
    private readonly CancellationTokenSource _closed = new();
    private long _requestCount;

    public TunnelSession(
        string tunnelId,
        string subdomain,
        TunnelPrincipal principal,
        TunnelSocket socket,
        GatewayOptions options,
        GatewayMetrics metrics,
        IUsageSink usage,
        ILogger logger)
    {
        TunnelId = tunnelId;
        Subdomain = subdomain;
        Principal = principal;
        _socket = socket;
        _options = options;
        _metrics = metrics;
        _usage = usage;
        _logger = logger;
        _concurrency = new SemaphoreSlim(options.MaxConcurrentRequestsPerTunnel);
    }

    public string TunnelId { get; }

    public string Subdomain { get; }

    public TunnelPrincipal Principal { get; }

    public string UserId => Principal.UserId;

    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UtcNow;

    public long RequestCount => Interlocked.Read(ref _requestCount);

    public int InFlightRequests => _pending.Count;

    /// <summary>Runs the receive loop until the connection closes. Fails all in-flight requests on exit.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                var frame = await _socket.ReceiveAsync(cancellationToken);
                switch (frame.Kind)
                {
                    case TunnelFrameKind.Closed:
                        await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed by client");
                        return;
                    case TunnelFrameKind.Body:
                        await OnBodyFrameAsync(frame.Body);
                        break;
                    default:
                        await OnMessageAsync(frame.Message!);
                        break;
                }
            }
        }
        finally
        {
            _closed.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.Fail(new TunnelDisconnectedException());
            }
        }
    }

    /// <summary>Closes the connection; used when the session is replaced or the gateway shuts down.</summary>
    public Task CloseAsync(WebSocketCloseStatus status, string reason) => _socket.CloseAsync(status, reason);

    /// <summary>Drops the connection immediately; in-flight requests fail with 502.</summary>
    public void Abort() => _socket.Abort();

    private async Task OnMessageAsync(TunnelMessage message)
    {
        switch (message)
        {
            case PingMessage:
                await _socket.SendAsync(new PongMessage(GatewayPong));
                break;
            case PongMessage:
                break;
            case TunnelHttpResponse response:
                if (_pending.TryGetValue(response.RequestId, out var pending))
                {
                    if (!pending.TrySetResponse(response))
                    {
                        throw new ProtocolException(ProtocolErrorCodes.UnexpectedMessage, "Duplicate response for a request.");
                    }

                    if (!response.HasBody)
                    {
                        pending.ResponseBody.Write([], endOfStream: true);
                    }
                }

                break;
            case WindowUpdateMessage window:
                if (_pending.TryGetValue(window.RequestId, out var forWindow))
                {
                    forWindow.RequestWindow.Grant(window.Bytes);
                }

                break;
            case CancelRequestMessage cancel:
                if (_pending.TryGetValue(cancel.RequestId, out var cancelled))
                {
                    cancelled.Fail(new TunnelRequestCancelledException(cancel.Reason));
                }

                break;
            case ErrorMessage error:
                _logger.LogWarning("Tunnel {Subdomain} reported error {Code}: {Message}", Subdomain, error.Code, error.Message);
                break;
            default:
                throw new ProtocolException(ProtocolErrorCodes.UnexpectedMessage, $"Unexpected message '{message.GetType().Name}'.");
        }
    }

    private async Task OnBodyFrameAsync(BodyFrame frame)
    {
        // Frames for unknown ids belong to requests that already finished or were cancelled; drop them.
        if (!_pending.TryGetValue(frame.RequestId, out var pending))
        {
            return;
        }

        if (!pending.HasResponse)
        {
            throw new ProtocolException(ProtocolErrorCodes.UnexpectedMessage, "Response body arrived before response headers.");
        }

        switch (pending.ResponseBody.Write(frame.Payload.Span, frame.EndOfStream))
        {
            case BodyWriteResult.FlowControlViolation:
                throw new ProtocolException(ProtocolErrorCodes.FlowControlViolation, "Response body exceeded the send window.");
            case BodyWriteResult.LimitExceeded:
                await TrySendAsync(new CancelRequestMessage(frame.RequestId, "response_too_large"));
                break;
        }
    }

    /// <summary>Forwards one public HTTP request through the tunnel and writes the CLI's response.</summary>
    public async Task ForwardAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        var stats = new RequestStats();
        var outcome = await ForwardCoreAsync(context, stats);
        _metrics.RequestCompleted(outcome, context.Response.StatusCode, Stopwatch.GetElapsedTime(started));
        _usage.Record(Principal, Subdomain, context.Response.StatusCode, stats.BytesIn, stats.BytesOut);
        _logger.LogDebug(
            "{Subdomain} {Method} {Path} -> {StatusCode} ({Outcome}) in {ElapsedMs:0}ms",
            Subdomain, context.Request.Method, context.Request.Path, context.Response.StatusCode, outcome,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private async Task<string> ForwardCoreAsync(HttpContext context, RequestStats stats)
    {
        if (context.Request.ContentLength > _options.MaxRequestBodyBytes)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "The request body is too large.");
            return "request_too_large";
        }

        if (!_concurrency.Wait(0))
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status429TooManyRequests, "too_many_concurrent_requests", "This tunnel is handling too many requests. Try again shortly.");
            return "concurrency_limited";
        }

        Interlocked.Increment(ref _requestCount);
        var requestId = Guid.NewGuid();
        var pending = new PendingRequest(new BodyReceiver(
            _options.MaxResponseBodyBytes,
            grant => TrySendAsync(new WindowUpdateMessage(requestId, grant))));
        _pending[requestId] = pending;

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _closed.Token);
        var completed = false;
        Task bodyTask = Task.CompletedTask;
        try
        {
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } sizeFeature)
            {
                sizeFeature.MaxRequestBodySize = _options.MaxRequestBodyBytes;
            }

            var hasBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? context.Request.ContentLength > 0;
            await _socket.SendAsync(
                new TunnelHttpRequest(requestId, context.Request.Method, GetTarget(context), BuildRequestHeaders(context), hasBody),
                requestCts.Token);

            if (hasBody)
            {
                bodyTask = SendRequestBodyAsync(requestId, context.Request.Body, pending, stats, requestCts.Token);
            }

            TunnelHttpResponse response;
            using (var headersTimeout = new CancellationTokenSource(_options.ResponseHeadersTimeout))
            using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(requestCts.Token, headersTimeout.Token))
            {
                try
                {
                    response = await pending.Response.WaitAsync(waitCts.Token);
                }
                catch (OperationCanceledException) when (headersTimeout.IsCancellationRequested && !requestCts.IsCancellationRequested)
                {
                    await GatewayErrors.WriteAsync(context, StatusCodes.Status504GatewayTimeout, "tunnel_timeout", "The local application did not respond in time.");
                    return "timeout";
                }
            }

            context.Response.StatusCode = response.StatusCode;
            CopyResponseHeaders(response.Headers, context.Response);

            if (response.HasBody && !HttpMethods.IsHead(context.Request.Method))
            {
                await pending.ResponseBody.Stream.CopyToAsync(context.Response.Body, requestCts.Token);
            }

            completed = true;
            return "completed";
        }
        catch (BodyLimitExceededException) when (!context.Response.HasStarted)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large", "The request body is too large.");
            return "request_too_large";
        }
        catch (BodyLimitExceededException)
        {
            context.Abort();
            return "response_too_large";
        }
        catch (TunnelDisconnectedException)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status502BadGateway, "tunnel_disconnected", "The tunnel disconnected before the response completed.");
            return "tunnel_disconnected";
        }
        catch (TunnelRequestCancelledException)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status502BadGateway, "tunnel_request_failed", "The tunnel client could not complete the request.");
            return "client_cancelled";
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException && context.RequestAborted.IsCancellationRequested)
        {
            return "visitor_aborted";
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException && _closed.IsCancellationRequested)
        {
            await GatewayErrors.WriteAsync(context, StatusCodes.Status502BadGateway, "tunnel_disconnected", "The tunnel disconnected before the response completed.");
            return "tunnel_disconnected";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Subdomain} request {RequestId} failed", Subdomain, requestId);
            await GatewayErrors.WriteAsync(context, StatusCodes.Status502BadGateway, "tunnel_error", "The request could not be forwarded through the tunnel.");
            return "error";
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
            if (!completed)
            {
                await TrySendAsync(new CancelRequestMessage(requestId, "gateway_cancelled"));
            }

            // Stops an unfinished request-body upload (the app may answer without reading it all).
            pending.Fail(new OperationCanceledException("Request finished."));
            await requestCts.CancelAsync();
            await bodyTask;
            stats.BytesOut = pending.ResponseBody.BytesReceived;
            _concurrency.Release();
        }
    }

    private async Task SendRequestBodyAsync(Guid requestId, Stream body, PendingRequest pending, RequestStats stats, CancellationToken cancellationToken)
    {
        try
        {
            stats.BytesIn = await BodySender.SendAsync(_socket, requestId, body, pending.RequestWindow, _options.MaxRequestBodyBytes, cancellationToken);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            pending.Fail(new BodyLimitExceededException(_options.MaxRequestBodyBytes));
        }
        catch (Exception ex)
        {
            pending.Fail(ex);
        }
    }

    private async Task TrySendAsync(TunnelMessage message)
    {
        if (_closed.IsCancellationRequested)
        {
            return;
        }

        try
        {
            await _socket.SendAsync(message, _closed.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Connection is going away; the receive loop will clean up.
        }
    }

    private static string GetTarget(HttpContext context)
    {
        var raw = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        return raw is not null && HttpValidation.IsOriginFormTarget(raw) ? raw : context.Request.GetEncodedPathAndQuery();
    }

    private List<HttpHeader> BuildRequestHeaders(HttpContext context)
    {
        var request = context.Request;
        var connectionTokens = HttpValidation.ParseConnectionTokens(request.Headers.Connection.OfType<string>());
        var headers = new List<HttpHeader>(request.Headers.Count + 3);
        foreach (var (name, values) in request.Headers)
        {
            // Client-supplied forwarding headers are untrusted and replaced below; Host is rewritten by the CLI.
            if (HttpValidation.IsHopByHop(name, connectionTokens)
                || !HttpValidation.IsToken(name)
                || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values)
            {
                if (value is not null && HttpValidation.IsValidHeaderValue(value))
                {
                    headers.Add(new HttpHeader(name, value));
                }
            }
        }

        if (context.Connection.RemoteIpAddress is { } remoteIp)
        {
            headers.Add(new HttpHeader("X-Forwarded-For", remoteIp.ToString()));
        }

        headers.Add(new HttpHeader("X-Forwarded-Proto", _options.PublicScheme));
        headers.Add(new HttpHeader("X-Forwarded-Host", request.Host.Value ?? ""));
        return headers;
    }

    private static void CopyResponseHeaders(IReadOnlyList<HttpHeader> headers, HttpResponse response)
    {
        var connectionTokens = HttpValidation.ParseConnectionTokens(
            headers.Where(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase)).Select(h => h.Value));
        foreach (var header in headers)
        {
            if (!HttpValidation.IsHopByHop(header.Name, connectionTokens))
            {
                response.Headers.Append(header.Name, header.Value);
            }
        }
    }

    private sealed class RequestStats
    {
        public long BytesIn { get; set; }

        public long BytesOut { get; set; }
    }

    private sealed class PendingRequest(BodyReceiver responseBody)
    {
        private readonly TaskCompletionSource<TunnelHttpResponse> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<TunnelHttpResponse> Response => _response.Task;

        public bool HasResponse { get; private set; }

        public SendWindow RequestWindow { get; } = new(TunnelProtocol.InitialWindowBytes);

        public BodyReceiver ResponseBody { get; } = responseBody;

        /// <summary>Called only from the receive loop.</summary>
        public bool TrySetResponse(TunnelHttpResponse response)
        {
            if (HasResponse)
            {
                return false;
            }

            HasResponse = true;
            return _response.TrySetResult(response);
        }

        public void Fail(Exception reason)
        {
            _response.TrySetException(reason);
            RequestWindow.Fail(reason);
            ResponseBody.Fail(reason);
        }
    }
}
