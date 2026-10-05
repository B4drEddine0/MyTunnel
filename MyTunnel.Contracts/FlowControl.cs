using System.Buffers;
using System.IO.Pipelines;

namespace MyTunnel.Contracts;

/// <summary>The body exceeded a configured size limit.</summary>
public sealed class BodyLimitExceededException(long limit)
    : IOException($"Body exceeds the {limit}-byte limit.")
{
    public long Limit { get; } = limit;
}

/// <summary>Credit the peer has granted us for sending one body stream.</summary>
public sealed class SendWindow(int initialBytes)
{
    private const long MaxAvailableBytes = 16L * 1024 * 1024;

    private readonly object _gate = new();
    private long _available = initialBytes;
    private TaskCompletionSource? _waiter;
    private Exception? _failure;

    /// <exception cref="ProtocolException">The peer granted an unreasonable amount of credit.</exception>
    public void Grant(int bytes)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            if (_available + bytes > MaxAvailableBytes)
            {
                throw new ProtocolException(ProtocolErrorCodes.FlowControlViolation, "Window update exceeds the maximum window.");
            }

            _available += bytes;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }

    /// <summary>Wakes the sender with <paramref name="reason"/>; used when the request is cancelled or the connection drops.</summary>
    public void Fail(Exception reason)
    {
        TaskCompletionSource? waiter;
        lock (_gate)
        {
            _failure ??= reason;
            waiter = _waiter;
            _waiter = null;
        }

        waiter?.TrySetResult();
    }

    /// <summary>Waits until at least one byte of credit is available and takes up to <paramref name="maxBytes"/>.</summary>
    public async ValueTask<int> AcquireAsync(int maxBytes, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (_gate)
            {
                if (_failure is not null)
                {
                    throw new OperationCanceledException("The body stream was cancelled.", _failure);
                }

                if (_available > 0)
                {
                    var granted = (int)Math.Min(maxBytes, _available);
                    _available -= granted;
                    return granted;
                }

                _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                wait = _waiter.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }
}

public enum BodyWriteResult
{
    Accepted,

    /// <summary>The stream was already finished or abandoned; the data was dropped.</summary>
    Ignored,

    LimitExceeded,
    FlowControlViolation,
}

/// <summary>
/// Receiving end of one body stream. The connection's receive loop pushes frames in with
/// <see cref="Write"/>; the consumer reads <see cref="Stream"/>. As the consumer reads, credit is
/// returned to the sender, so at most one window of unread data is ever buffered.
/// </summary>
public sealed class BodyReceiver
{
    private readonly Pipe _pipe = new(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0, useSynchronizationContext: false));
    private readonly object _gate = new();
    private readonly long _maxBytes;
    private readonly Func<int, Task> _sendWindowUpdate;
    private long _received;
    private long _credit = TunnelProtocol.InitialWindowBytes;
    private int _consumedSinceGrant;
    private volatile bool _completed;

    /// <param name="maxBytes">Total body size limit.</param>
    /// <param name="sendWindowUpdate">Sends a window update to the peer. Must not throw.</param>
    public BodyReceiver(long maxBytes, Func<int, Task> sendWindowUpdate)
    {
        _maxBytes = maxBytes;
        _sendWindowUpdate = sendWindowUpdate;
        Stream = new CreditReturningStream(this);
    }

    public Stream Stream { get; }

    public long BytesReceived => Interlocked.Read(ref _received);

    public BodyWriteResult Write(ReadOnlySpan<byte> data, bool endOfStream)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return BodyWriteResult.Ignored;
            }

            if (data.Length > Interlocked.Read(ref _credit))
            {
                CompleteWriter(new ProtocolException(ProtocolErrorCodes.FlowControlViolation, "Peer exceeded its send window."));
                return BodyWriteResult.FlowControlViolation;
            }

            Interlocked.Add(ref _credit, -data.Length);
            if (Interlocked.Add(ref _received, data.Length) > _maxBytes)
            {
                CompleteWriter(new BodyLimitExceededException(_maxBytes));
                return BodyWriteResult.LimitExceeded;
            }

            if (!data.IsEmpty)
            {
                data.CopyTo(_pipe.Writer.GetSpan(data.Length));
                _pipe.Writer.Advance(data.Length);
                // With pauseWriterThreshold = 0 the flush never waits; the window bounds the buffer instead.
                _pipe.Writer.FlushAsync().GetAwaiter().GetResult();
            }

            if (endOfStream)
            {
                CompleteWriter(null);
            }

            return BodyWriteResult.Accepted;
        }
    }

    /// <summary>Ends the stream with an error that the consumer will observe on its next read.</summary>
    public void Fail(Exception reason)
    {
        lock (_gate)
        {
            if (!_completed)
            {
                CompleteWriter(reason);
            }
        }
    }

    private void CompleteWriter(Exception? error)
    {
        _completed = true;
        _pipe.Writer.Complete(error);
    }

    private void OnConsumed(int bytes)
    {
        if (_completed || Interlocked.Add(ref _consumedSinceGrant, bytes) < TunnelProtocol.InitialWindowBytes / 2)
        {
            return;
        }

        var grant = Interlocked.Exchange(ref _consumedSinceGrant, 0);
        if (grant > 0)
        {
            Interlocked.Add(ref _credit, grant);
            _ = _sendWindowUpdate(grant);
        }
    }

    private sealed class CreditReturningStream(BodyReceiver owner) : Stream
    {
        private readonly Stream _inner = owner._pipe.Reader.AsStream();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken);
            if (read > 0)
            {
                owner.OnConsumed(read);
            }

            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Consumer is done; anything still arriving for this stream is discarded.
                owner.Fail(new OperationCanceledException("Body stream was abandoned by its consumer."));
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

public static class BodySender
{
    /// <summary>
    /// Streams <paramref name="source"/> as body frames, respecting the peer's window, and finishes with an
    /// end-of-stream frame. Returns the number of bytes sent.
    /// </summary>
    /// <exception cref="BodyLimitExceededException">The source produced more than <paramref name="maxBytes"/>.</exception>
    public static async Task<long> SendAsync(
        TunnelSocket socket, Guid requestId, Stream source, SendWindow window, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TunnelProtocol.MaxBodyChunkBytes);
        try
        {
            long total = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, TunnelProtocol.MaxBodyChunkBytes), cancellationToken);
                if (read == 0)
                {
                    await socket.SendBodyAsync(requestId, ReadOnlyMemory<byte>.Empty, endOfStream: true, cancellationToken);
                    return total;
                }

                total += read;
                if (total > maxBytes)
                {
                    throw new BodyLimitExceededException(maxBytes);
                }

                for (var offset = 0; offset < read;)
                {
                    var allowed = await window.AcquireAsync(read - offset, cancellationToken);
                    await socket.SendBodyAsync(requestId, buffer.AsMemory(offset, allowed), endOfStream: false, cancellationToken);
                    offset += allowed;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
