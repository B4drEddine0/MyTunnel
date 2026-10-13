using System.IO.Compression;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MyTunnel.Tests.Infrastructure;

/// <summary>A stand-in for the developer's local application, with endpoints that expose what it received.</summary>
public sealed class LocalApp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private LocalApp(WebApplication app, Uri origin)
    {
        _app = app;
        Origin = origin;
    }

    public Uri Origin { get; }

    public static byte[] Pattern(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    public static async Task<LocalApp> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();

        app.MapGet("/", () => "Hello from my local application!");

        app.Map("/echo/{**rest}", async (HttpContext context) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Json(new
            {
                method = context.Request.Method,
                path = context.Request.Path.Value,
                query = context.Request.QueryString.Value,
                host = context.Request.Host.Value,
                headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body,
            });
        });

        app.Map("/status/{code:int}", (int code) => Results.StatusCode(code));
        app.MapGet("/redirect", () => Results.Redirect("/target"));
        app.MapGet("/cookies", (HttpContext context) =>
        {
            context.Response.Headers.Append("Set-Cookie", "a=1; Path=/");
            context.Response.Headers.Append("Set-Cookie", "b=2; Path=/");
            return Results.Ok();
        });

        app.MapGet("/large", async (HttpContext context, int bytes) =>
        {
            context.Response.ContentType = "application/octet-stream";
            var data = Pattern(bytes);
            for (var offset = 0; offset < data.Length; offset += 50_000)
            {
                await context.Response.Body.WriteAsync(data.AsMemory(offset, Math.Min(50_000, data.Length - offset)));
            }
        });

        app.MapPost("/hash", async (HttpContext context) =>
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await context.Request.Body.ReadAsync(buffer)) > 0)
            {
                sha.AppendData(buffer, 0, read);
                total += read;
            }

            return Results.Json(new { length = total, sha256 = Convert.ToHexString(sha.GetHashAndReset()) });
        });

        app.MapGet("/slow", async (int ms, CancellationToken token) =>
        {
            await Task.Delay(ms, token);
            return "done";
        });

        app.MapGet("/gzip", (HttpContext context) =>
        {
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzip.Write("compressed payload"u8);
            }

            context.Response.Headers.ContentEncoding = "gzip";
            return Results.Bytes(compressed.ToArray(), "text/plain");
        });

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses.First();
        return new LocalApp(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
