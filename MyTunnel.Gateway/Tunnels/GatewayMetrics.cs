using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace MyTunnel.Gateway.Tunnels;

/// <summary>OpenTelemetry-compatible instruments; read them with <c>dotnet-counters monitor MyTunnel.Gateway</c> or an exporter.</summary>
public sealed class GatewayMetrics
{
    public const string MeterName = "MyTunnel.Gateway";

    private readonly Counter<long> _requests;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _registrations;

    public GatewayMetrics(IMeterFactory meterFactory, TunnelRegistry registry)
    {
        var meter = meterFactory.Create(MeterName);
        _requests = meter.CreateCounter<long>("mytunnel.requests", description: "Public requests handled, by outcome.");
        _requestDuration = meter.CreateHistogram<double>("mytunnel.request.duration", unit: "s", description: "Time to complete a tunnelled request.");
        _registrations = meter.CreateCounter<long>("mytunnel.registrations", description: "Tunnel registration attempts, by outcome.");
        meter.CreateObservableGauge("mytunnel.tunnels.active", () => registry.Count, description: "Currently connected tunnels.");
    }

    public void RequestCompleted(string outcome, int statusCode, TimeSpan duration)
    {
        var tags = new TagList { { "outcome", outcome }, { "status_code", statusCode } };
        _requests.Add(1, tags);
        _requestDuration.Record(duration.TotalSeconds, tags);
    }

    public void Registration(string outcome) => _registrations.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
}
