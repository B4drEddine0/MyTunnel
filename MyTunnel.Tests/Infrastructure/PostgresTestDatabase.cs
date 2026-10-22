using System.Net.Http.Json;
using Npgsql;

namespace MyTunnel.Tests.Infrastructure;

/// <summary>
/// Runs only when MYTUNNEL_TEST_POSTGRES holds an admin connection string (for example
/// "Host=127.0.0.1;Port=5432;Username=postgres;Password=..."). Otherwise the test is reported as skipped.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrEmpty(PostgresTestDatabase.AdminConnectionString))
        {
            Skip = "Set MYTUNNEL_TEST_POSTGRES to run PostgreSQL integration tests.";
        }
    }
}

/// <summary>A uniquely named, throwaway database per test.</summary>
public sealed class PostgresTestDatabase : IAsyncDisposable
{
    private PostgresTestDatabase(string name, string connectionString)
    {
        Name = name;
        ConnectionString = connectionString;
    }

    public static string? AdminConnectionString => Environment.GetEnvironmentVariable("MYTUNNEL_TEST_POSTGRES");

    public string Name { get; }

    public string ConnectionString { get; }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var name = $"mytunnel_test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = name };
        return new PostgresTestDatabase(name, builder.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}

/// <summary>Gateway with the control plane enabled on a fresh database.</summary>
public sealed class ControlPlaneHost : IAsyncDisposable
{
    private ControlPlaneHost(PostgresTestDatabase database, GatewayHost gateway)
    {
        Database = database;
        Gateway = gateway;
    }

    public PostgresTestDatabase Database { get; }

    public GatewayHost Gateway { get; }

    public static async Task<ControlPlaneHost> StartAsync(IDictionary<string, string?>? overrides = null)
    {
        var database = await PostgresTestDatabase.CreateAsync();
        var settings = TestTokens.GatewaySettings(overrides);
        settings["ConnectionStrings:MyTunnel"] = database.ConnectionString;
        settings["Database:MigrateOnStartup"] = "true";
        settings.TryAdd("Gateway:AuthRequestsPerMinutePerIp", "1000");
        return new ControlPlaneHost(database, await GatewayHost.StartAsync(settings));
    }

    /// <summary>A browser-like API client: keeps cookies and sends the CSRF header.</summary>
    public ApiClient CreateClient() => new(Gateway.BaseUri);

    /// <summary>Registers and signs in a new user.</summary>
    public async Task<ApiClient> CreateUserAsync(string email, string password = "correct horse battery")
    {
        var client = CreateClient();
        using var response = await client.Http.PostAsJsonAsync("/api/auth/register", new { email, password });
        response.EnsureSuccessStatusCode();
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        await Gateway.DisposeAsync();
        await Database.DisposeAsync();
    }
}

public sealed class ApiClient : IDisposable
{
    public ApiClient(Uri baseUri)
    {
        Cookies = new System.Net.CookieContainer();
        Http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, UseCookies = true })
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(30),
        };
        Http.DefaultRequestHeaders.Add("X-MyTunnel-Csrf", "1");
    }

    public HttpClient Http { get; }

    public System.Net.CookieContainer Cookies { get; }

    public void Dispose() => Http.Dispose();
}
