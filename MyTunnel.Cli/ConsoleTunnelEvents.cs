using MyTunnel.Cli.Tunnel;
using MyTunnel.Contracts;

namespace MyTunnel.Cli;

/// <summary>Human-readable progress output for <c>mytunnel http</c>.</summary>
public sealed class ConsoleTunnelEvents(TextWriter output, Uri localOrigin) : ITunnelClientEvents
{
    private readonly object _gate = new();

    public void Connecting(Uri gatewayUri) => Write($"Connecting to {gatewayUri.GetLeftPart(UriPartial.Authority)} ...");

    public void Registered(RegisteredMessage registration)
    {
        lock (_gate)
        {
            output.WriteLine();
            output.WriteLine("Tunnel status    online");
            output.WriteLine($"Forwarding       {registration.PublicUrl} -> {localOrigin.GetLeftPart(UriPartial.Authority)}");
            output.WriteLine();
            output.WriteLine("Press Ctrl+C to stop.");
            output.WriteLine();
            output.WriteLine("HTTP requests");
            output.WriteLine("-------------");
        }
    }

    public void RequestCompleted(string method, string path, int statusCode, TimeSpan elapsed) =>
        Write($"{DateTime.Now:HH:mm:ss}  {method,-7} {Truncate(path, 60),-60} {statusCode}  {elapsed.TotalMilliseconds,6:0}ms");

    public void Disconnected(string reason) => Write($"Disconnected: {reason}");

    public void Reconnecting(TimeSpan delay, int attempt) => Write($"Reconnecting in {delay.TotalSeconds:0.0}s (attempt {attempt}) ...");

    public void Warning(string message) => Write($"Warning: {message}");

    private void Write(string line)
    {
        lock (_gate)
        {
            output.WriteLine(line);
        }
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..(length - 3)] + "...";
}
