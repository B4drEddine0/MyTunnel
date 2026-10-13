using MyTunnel.Cli;
using MyTunnel.Cli.Configuration;

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C shuts down gracefully; a second one terminates immediately.
    if (!shutdown.IsCancellationRequested)
    {
        e.Cancel = true;
        Console.WriteLine("Stopping tunnel...");
        shutdown.Cancel();
    }
};

var app = new CliApp(Console.Out, Console.Error, new CliSettings());
return await app.RunAsync(args, shutdown.Token);
