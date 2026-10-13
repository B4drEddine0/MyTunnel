using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using MyTunnel.Contracts;

namespace MyTunnel.Cli.Tunnel;

/// <summary>
/// Keeps a tunnel registered with the gateway and serves forwarded requests from the local origin.
/// Reconnects with exponential backoff and jitter when the connection drops.
/// </summary>
public sealed class TunnelClient(TunnelClientOptions options, HttpMessageInvoker localHttp, ITunnelClientEvents events)
{
    private static readonly TimeSpan RegistrationTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan GracefulCloseTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Runs until <paramref name="cancellationToken"/> fires or the gateway rejects the registration.</summary>
    /// <exception cref="TunnelRejectedException">Registration was refused (bad token, unavailable subdomain, ...).</exception>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var subdomain = options.Subdomain;
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(subdomain, registration =>
                {
                    // Ask for the same name after a reconnect so the public URL stays stable.
                    subdomain = registration.Subdomain;
                    attempt = 0;
                }, cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                events.Disconnected("The gateway closed the connection.");
            }
            catch (TunnelRejectedException ex) when (ex.Code == ProtocolErrorCodes.SubdomainUnavailable && options.Subdomain is null && subdomain is not null)
            {
                // Our previously assigned random name was taken while we were away; take a new one.
                events.Warning($"Subdomain '{subdomain}' is no longer available; requesting a new one.");
                subdomain = null;
                continue;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or ProtocolException or IOException or TimeoutException or OperationCanceledException)
            {
                events.Disconnected(Describe(ex));
            }

            attempt++;
            var delay = GetReconnectDelay(attempt);
            events.Reconnecting(delay, attempt);
            try
            {
                await Task.Delay(delay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public TimeSpan GetReconnectDelay(int attempt)
    {
        var exponential = options.InitialReconnectDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt - 1, 16));
        var capped = Math.Min(exponential, options.MaxReconnectDelay.TotalMilliseconds);
        // "Equal jitter": half fixed, half random, so many clients do not reconnect in lockstep after a gateway restart.
        return TimeSpan.FromMilliseconds(capped / 2 + Random.Shared.NextDouble() * capped / 2);
    }

    /// <summary>One connection: connect, register, serve requests until the connection ends.</summary>
    public async Task RunSessionAsync(string? subdomain, Action<RegisteredMessage>? onRegistered, CancellationToken cancellationToken)
    {
        var uri = TunnelClientOptions.GetTunnelWebSocketUri(options.GatewayUri);
        events.Connecting(uri);

        using var webSocket = new ClientWebSocket();
        webSocket.Options.KeepAliveInterval = options.KeepAliveInterval;
        webSocket.Options.KeepAliveTimeout = options.KeepAliveTimeout;
        webSocket.Options.SetRequestHeader("User-Agent", $"mytunnel-cli/{TunnelClientOptions.ClientVersion}");

        using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectCts.CancelAfter(options.ConnectTimeout);
            try
            {
                await webSocket.ConnectAsync(uri, connectCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out connecting to {uri.GetLeftPart(UriPartial.Authority)}.");
            }
        }

        using var socket = new TunnelSocket(webSocket);
        var connection = new Connection(socket, options, localHttp, events);

        // On Ctrl+C, close politely; the receive loop then sees the gateway's close frame and exits.
        await using var closeOnCancel = cancellationToken.Register(() =>
        {
            _ = socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client shutting down");
            _ = Task.Delay(GracefulCloseTimeout).ContinueWith(_ => socket.Abort(), TaskScheduler.Default);
        });

        try
        {
            await socket.SendAsync(
                new RegisterMessage(TunnelProtocol.CurrentVersion, options.AuthToken, subdomain, TunnelClientOptions.ClientVersion),
                cancellationToken);

            var registration = await WaitForRegistrationAsync(socket, cancellationToken);
            connection.Limits = registration;
            onRegistered?.Invoke(registration);
            events.Registered(registration);

            await connection.RunAsync();
        }
        catch (ProtocolException ex)
        {
            await TrySendAsync(socket, new ErrorMessage(ex.Code, ex.Message));
            await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, ex.Code);
            throw;
        }
        catch (WebSocketException) when (cancellationToken.IsCancellationRequested)
        {
            // Aborted during shutdown.
        }
        finally
        {
            await connection.StopAsync();
        }
    }

    private static async Task<RegisteredMessage> WaitForRegistrationAsync(TunnelSocket socket, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + RegistrationTimeout;
        while (true)
        {
            var frame = await socket.ReceiveAsync(cancellationToken).WaitAsync(deadline - DateTime.UtcNow, cancellationToken);
            switch (frame.Message)
            {
                case RegisteredMessage registered:
                    return registered;
                case RegistrationRejectedMessage rejected:
                    throw new TunnelRejectedException(rejected.Code, rejected.Message);
                case PingMessage:
                    await socket.SendAsync(new PongMessage(), cancellationToken);
                    continue;
                case PongMessage:
                    continue;
                case ErrorMessage error:
                    throw new ProtocolException(error.Code, error.Message);
            }

            if (frame.Kind == TunnelFrameKind.Closed)
            {
                throw new WebSocketException("The gateway closed the connection during registration.");
            }

            throw new ProtocolException(ProtocolErrorCodes.UnexpectedMessage, "Expected a registration response.");
        }
    }

    private static async Task TrySendAsync(TunnelSocket socket, TunnelMessage message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.SendAsync(message, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        ProtocolException protocol => $"Protocol error ({protocol.Code}): {protocol.Message}",
        WebSocketException { InnerException: { } inner } => inner.Message,
        _ => ex.Message,
    };

    /// <summary>State for one registered connection: the receive loop and in-flight request handlers.</summary>
    private sealed class Connection(TunnelSocket socket, TunnelClientOptions options, HttpMessageInvoker localHttp, ITunnelClientEvents events)
    {
        private readonly ConcurrentDictionary<Guid, PendingRequest> _pending = new();
        private readonly ConcurrentDictionary<Task, byte> _handlers = new();
        private readonly CancellationTokenSource _stopping = new();

        public RegisteredMessage? Limits { get; set; }

        public async Task RunAsync()
        {
            while (true)
            {
                // Not cancelled by the caller's token: shutdown closes the socket, which ends this receive.
                var frame = await socket.ReceiveAsync(CancellationToken.None);
                switch (frame.Kind)
                {
                    case TunnelFrameKind.Closed:
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing");
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

        public async Task StopAsync()
        {
            await _stopping.CancelAsync();
            foreach (var pending in _pending.Values)
            {
                pending.Cancel(new OperationCanceledException("Tunnel connection closed."));
            }

            await Task.WhenAll(_handlers.Keys);
        }

        private async Task OnMessageAsync(TunnelMessage message)
        {
            switch (message)
            {
                case TunnelHttpRequest request:
                    await OnRequestAsync(request);
                    break;
                case WindowUpdateMessage window:
                    if (_pending.TryGetValue(window.RequestId, out var forWindow))
                    {
                        forWindow.ResponseWindow.Grant(window.Bytes);
                    }

                    break;
                case CancelRequestMessage cancel:
                    if (_pending.TryGetValue(cancel.RequestId, out var cancelled))
                    {
                        cancelled.Cancel(new OperationCanceledException($"Cancelled by gateway: {cancel.Reason}"));
                    }

                    break;
                case PingMessage:
                    await socket.SendAsync(new PongMessage());
                    break;
                case PongMessage:
                    break;
                case ErrorMessage error:
                    events.Warning($"Gateway reported {error.Code}: {error.Message}");
                    break;
                default:
                    throw new ProtocolException(ProtocolErrorCodes.UnexpectedMessage, $"Unexpected message '{message.GetType().Name}'.");
            }
        }

        private async Task OnRequestAsync(TunnelHttpRequest request)
        {
            if (_pending.Count >= options.MaxConcurrentRequests)
            {
                await socket.SendAsync(new TunnelHttpResponse(request.RequestId, 503, [new HttpHeader("Retry-After", "1")], false));
                return;
            }

            var maxRequestBody = Limits?.MaxRequestBodyBytes ?? long.MaxValue;
            var requestBody = request.HasBody
                ? new BodyReceiver(maxRequestBody, grant => TrySendAsync(socket, new WindowUpdateMessage(request.RequestId, grant)))
                : null;
            var pending = new PendingRequest(requestBody, _stopping.Token);
            if (!_pending.TryAdd(request.RequestId, pending))
            {
                throw new ProtocolException(ProtocolErrorCodes.UnexpectedMessage, "Duplicate request id.");
            }

            var handler = Task.Run(() => HandleRequestAsync(request, pending));
            _handlers.TryAdd(handler, 0);
            _ = handler.ContinueWith(t => _handlers.TryRemove(t, out _), TaskScheduler.Default);
        }

        private async Task OnBodyFrameAsync(BodyFrame frame)
        {
            if (!_pending.TryGetValue(frame.RequestId, out var pending))
            {
                return;
            }

            if (pending.RequestBody is null)
            {
                throw new ProtocolException(ProtocolErrorCodes.UnexpectedMessage, "Body frame for a request without a body.");
            }

            switch (pending.RequestBody.Write(frame.Payload.Span, frame.EndOfStream))
            {
                case BodyWriteResult.FlowControlViolation:
                    throw new ProtocolException(ProtocolErrorCodes.FlowControlViolation, "Request body exceeded the send window.");
                case BodyWriteResult.LimitExceeded:
                    await TrySendAsync(socket, new CancelRequestMessage(frame.RequestId, "request_too_large"));
                    break;
            }
        }

        private async Task HandleRequestAsync(TunnelHttpRequest request, PendingRequest pending)
        {
            var started = Stopwatch.GetTimestamp();
            var path = request.Target.Split('?', 2)[0];
            var status = 0;
            var responseStarted = false;
            try
            {
                using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(pending.Token);
                headersTimeout.CancelAfter(options.LocalResponseTimeout);

                HttpResponseMessage response;
                try
                {
                    using var localRequest = LocalRequestBuilder.BuildRequest(options.LocalOrigin, request, pending.RequestBody?.Stream);
                    response = await localHttp.SendAsync(localRequest, headersTimeout.Token);
                }
                catch (HttpRequestException ex) when (!pending.Token.IsCancellationRequested)
                {
                    status = 502;
                    await SendErrorResponseAsync(request.RequestId, status, "local_unreachable",
                        $"Could not reach the local application at {options.LocalOrigin.GetLeftPart(UriPartial.Authority)} ({ex.HttpRequestError}). Is it running?");
                    return;
                }
                catch (OperationCanceledException) when (!pending.Token.IsCancellationRequested)
                {
                    status = 504;
                    await SendErrorResponseAsync(request.RequestId, status, "local_timeout",
                        $"The local application did not respond within {options.LocalResponseTimeout.TotalSeconds:0} seconds.");
                    return;
                }

                using (response)
                {
                    status = (int)response.StatusCode;
                    if (status < 200)
                    {
                        status = 502;
                        await SendErrorResponseAsync(request.RequestId, status, "unsupported_status", "The local application returned an unsupported informational status.");
                        return;
                    }

                    var hasBody = !request.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) && response.Content.Headers.ContentLength != 0;
                    await socket.SendAsync(
                        new TunnelHttpResponse(request.RequestId, status, LocalRequestBuilder.GetResponseHeaders(response), hasBody),
                        pending.Token);
                    responseStarted = true;

                    if (hasBody)
                    {
                        await using var body = await response.Content.ReadAsStreamAsync(pending.Token);
                        await BodySender.SendAsync(
                            socket, request.RequestId, body, pending.ResponseWindow,
                            Limits?.MaxResponseBodyBytes ?? long.MaxValue, pending.Token);
                    }
                }

                events.RequestCompleted(request.Method, path, status, Stopwatch.GetElapsedTime(started));
            }
            catch (BodyLimitExceededException)
            {
                events.Warning($"{request.Method} {path}: response exceeded the gateway's size limit and was cancelled.");
                await TrySendAsync(socket, new CancelRequestMessage(request.RequestId, "response_too_large"));
            }
            catch (Exception ex) when (pending.Token.IsCancellationRequested)
            {
                // Cancelled by the gateway (visitor left, timeout) or the connection closed. Nothing to send.
                _ = ex;
            }
            catch (Exception ex)
            {
                if (responseStarted)
                {
                    events.Warning($"{request.Method} {path}: failed while streaming the response ({ex.Message}).");
                    await TrySendAsync(socket, new CancelRequestMessage(request.RequestId, "local_error"));
                }
                else
                {
                    await SendErrorResponseAsync(request.RequestId, 502, "local_error", "The request to the local application failed.");
                }
            }
            finally
            {
                _pending.TryRemove(request.RequestId, out _);
                pending.Dispose();
            }
        }

        private async Task SendErrorResponseAsync(Guid requestId, int status, string code, string message)
        {
            events.Warning(message);
            var body = Encoding.UTF8.GetBytes($"MyTunnel: {message}\n");
            var headers = new List<HttpHeader>
            {
                new("Content-Type", "text/plain; charset=utf-8"),
                new("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("Cache-Control", "no-store"),
                new("X-MyTunnel-Error", code),
            };

            try
            {
                await socket.SendAsync(new TunnelHttpResponse(requestId, status, headers, HasBody: true));
                await socket.SendBodyAsync(requestId, body, endOfStream: true);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    private sealed class PendingRequest : IDisposable
    {
        private readonly CancellationTokenSource _cts;

        public PendingRequest(BodyReceiver? requestBody, CancellationToken connectionStopping)
        {
            RequestBody = requestBody;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(connectionStopping);
        }

        public BodyReceiver? RequestBody { get; }

        public SendWindow ResponseWindow { get; } = new(TunnelProtocol.InitialWindowBytes);

        public CancellationToken Token => _cts.Token;

        public void Cancel(Exception reason)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            ResponseWindow.Fail(reason);
            RequestBody?.Fail(reason);
        }

        public void Dispose()
        {
            RequestBody?.Fail(new OperationCanceledException("Request finished."));
            _cts.Dispose();
        }
    }
}
