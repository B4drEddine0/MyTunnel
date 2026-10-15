using System.Net.Http.Json;
using MyTunnel.Cli.Configuration;
using MyTunnel.Cli.Tunnel;
using MyTunnel.Contracts;

namespace MyTunnel.Cli;

public static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int Rejected = 3;
}

/// <summary>Command dispatch for the mytunnel CLI.</summary>
public sealed class CliApp(TextWriter output, TextWriter error, CliSettings settings, Func<Uri, bool>? openBrowser = null)
{
    private readonly Func<Uri, bool> _openBrowser = openBrowser ?? DeviceLogin.OpenInDefaultBrowser;

    public const string HelpText = """
        MyTunnel - expose a local HTTP application through a public URL.

        Usage:
          mytunnel http <port> [options]   Start a tunnel to http://localhost:<port>
          mytunnel login                   Sign in through your browser and store a token
          mytunnel login --token <token>   Store an existing access token (--paste to type it hidden)
          mytunnel logout                  Remove the stored access token
          mytunnel status [--gateway <url>] Check gateway reachability and login state
          mytunnel config                  Show effective configuration
          mytunnel config set gateway <url>
          mytunnel version
          mytunnel --help

        Options for 'http':
          --subdomain <name>   Request a specific subdomain (3-63 chars: a-z, 0-9, -)
          --https              The local application uses https://localhost:<port>
          --gateway <url>      Gateway URL (default: MYTUNNEL_GATEWAY, config, http://localhost:5018)
          --token <token>      Access token (prefer 'mytunnel login' or MYTUNNEL_TOKEN)
          --timeout <seconds>  Local response timeout (default 60)

        Environment:
          MYTUNNEL_GATEWAY     Gateway URL
          MYTUNNEL_TOKEN       Access token (useful for CI)
          MYTUNNEL_CONFIG_DIR  Override the configuration directory
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
        {
            output.WriteLine(HelpText);
            return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Success;
        }

        CommandLine parsed;
        try
        {
            parsed = CommandLine.Parse(args[1..]);
        }
        catch (ArgumentException ex)
        {
            return Usage(ex.Message);
        }

        return args[0] switch
        {
            "http" => await HttpAsync(parsed, cancellationToken),
            "login" => await LoginAsync(parsed, cancellationToken),
            "logout" => Logout(),
            "status" => await StatusAsync(parsed, cancellationToken),
            "config" => Config(parsed),
            "version" or "--version" => Version(),
            _ => Usage($"Unknown command '{args[0]}'."),
        };
    }

    private async Task<int> HttpAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (command.Positionals.Count != 1 || !int.TryParse(command.Positionals[0], out var port) || port is < 1 or > 65535)
        {
            return Usage("Usage: mytunnel http <port>  (port must be 1-65535)");
        }

        if (!command.TryGetUnknown(out var unknown, "subdomain", "https", "gateway", "token", "timeout"))
        {
            return Usage($"Unknown option '--{unknown}'.");
        }

        var timeoutSeconds = 60;
        if (command.Get("timeout") is { } timeoutText && (!int.TryParse(timeoutText, out timeoutSeconds) || timeoutSeconds is < 1 or > 3600))
        {
            return Usage("--timeout must be between 1 and 3600 seconds.");
        }

        var subdomain = command.Get("subdomain")?.ToLowerInvariant();
        if (subdomain is not null && (subdomain.Length is < 3 or > 63 || !subdomain.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')))
        {
            return Usage("--subdomain must be 3-63 characters of a-z, 0-9 and '-'.");
        }

        var (gatewayText, _) = settings.ResolveGateway(command.Get("gateway"));
        if (!Uri.TryCreate(gatewayText, UriKind.Absolute, out var gatewayUri))
        {
            return Usage($"Invalid gateway URL '{gatewayText}'.");
        }

        if (TunnelClientOptions.ValidateGatewayUri(gatewayUri) is { } gatewayProblem)
        {
            return Usage(gatewayProblem);
        }

        var (token, _) = settings.ResolveToken(command.Get("token"));
        var localOrigin = new Uri($"{(command.Has("https") ? "https" : "http")}://localhost:{port}");

        output.WriteLine($"Local application: {localOrigin.GetLeftPart(UriPartial.Authority)}");
        output.WriteLine($"Gateway:           {gatewayUri.GetLeftPart(UriPartial.Authority)}");
        await CheckLocalApplicationAsync(localOrigin, cancellationToken);

        using var localHttp = LocalRequestBuilder.CreateLocalInvoker();
        var client = new TunnelClient(
            new TunnelClientOptions
            {
                GatewayUri = gatewayUri,
                LocalOrigin = localOrigin,
                AuthToken = token,
                Subdomain = subdomain,
                LocalResponseTimeout = TimeSpan.FromSeconds(timeoutSeconds),
            },
            localHttp,
            new ConsoleTunnelEvents(output, localOrigin));

        try
        {
            await client.RunAsync(cancellationToken);
            output.WriteLine("Tunnel stopped.");
            return ExitCodes.Success;
        }
        catch (TunnelRejectedException ex)
        {
            error.WriteLine($"The gateway rejected the tunnel ({ex.Code}): {ex.Message}");
            return ExitCodes.Rejected;
        }
    }

    private async Task CheckLocalApplicationAsync(Uri localOrigin, CancellationToken cancellationToken)
    {
        using var http = LocalRequestBuilder.CreateLocalInvoker();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, localOrigin);
            using var response = await http.SendAsync(request, timeout.Token);
            output.WriteLine($"Local application is reachable (HTTP {(int)response.StatusCode}).");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            output.WriteLine("Warning: the local application is not reachable yet. Requests will fail with 502 until it starts.");
        }
    }

    private async Task<int> LoginAsync(CommandLine command, CancellationToken cancellationToken)
    {
        if (!command.TryGetUnknown(out var unknown, "token", "paste", "gateway", "no-browser"))
        {
            return Usage($"Unknown option '--{unknown}'.");
        }

        var token = command.Get("token");
        if (command.Has("paste"))
        {
            output.Write("Paste your access token: ");
            token = ReadSecretLine();
        }

        if (command.Has("token") || command.Has("paste"))
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return Usage("No token provided.");
            }

            settings.Credentials.Save(token.Trim());
            output.WriteLine($"Access token saved to {settings.Credentials.Description}.");
            return ExitCodes.Success;
        }

        var (gatewayText, _) = settings.ResolveGateway(command.Get("gateway"));
        if (!Uri.TryCreate(gatewayText, UriKind.Absolute, out var gatewayUri))
        {
            return Usage($"Invalid gateway URL '{gatewayText}'.");
        }

        if (TunnelClientOptions.ValidateGatewayUri(gatewayUri) is { } gatewayProblem)
        {
            return Usage(gatewayProblem);
        }

        using var http = new HttpClient { BaseAddress = new Uri(gatewayUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
        try
        {
            var result = await new DeviceLogin(http, output, _openBrowser).RunAsync(!command.Has("no-browser"), cancellationToken);
            if (!result.Succeeded)
            {
                error.WriteLine(result.Message);
                return ExitCodes.Error;
            }

            settings.Credentials.Save(result.AccessToken!);
            output.WriteLine(result.Message);
            output.WriteLine($"Access token saved to {settings.Credentials.Description}.");
            return ExitCodes.Success;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            error.WriteLine("Login cancelled.");
            return ExitCodes.Error;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            error.WriteLine($"Could not reach the gateway at {gatewayUri.GetLeftPart(UriPartial.Authority)}: {ex.Message}");
            return ExitCodes.Error;
        }
    }

    private int Logout()
    {
        output.WriteLine(settings.Credentials.Delete() ? "Logged out; the stored token was removed." : "No stored token to remove.");
        output.WriteLine("To invalidate the token itself, revoke it in the dashboard.");
        return ExitCodes.Success;
    }

    private async Task<int> StatusAsync(CommandLine command, CancellationToken cancellationToken)
    {
        var (gatewayText, gatewaySource) = settings.ResolveGateway(command.Get("gateway"));
        var (token, tokenSource) = settings.ResolveToken(null);
        output.WriteLine($"Gateway:      {gatewayText}  ({gatewaySource})");
        output.WriteLine($"Credential:   {(token is null ? "not logged in" : $"present ({tokenSource})")}");

        if (!Uri.TryCreate(gatewayText, UriKind.Absolute, out var gatewayUri))
        {
            return Usage("Invalid gateway URL.");
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var response = await http.GetAsync(new Uri(gatewayUri, "health"), cancellationToken);
            output.WriteLine($"Gateway health: {(response.IsSuccessStatusCode ? "reachable" : $"HTTP {(int)response.StatusCode}")}");
            if (!response.IsSuccessStatusCode)
            {
                return ExitCodes.Error;
            }

            if (token is null || TunnelClientOptions.ValidateGatewayUri(gatewayUri) is not null)
            {
                return ExitCodes.Success;
            }

            // Ask the gateway whether the token is still valid (not revoked or expired).
            using var whoami = new HttpRequestMessage(HttpMethod.Get, new Uri(gatewayUri, "api/cli/whoami"));
            whoami.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var identity = await http.SendAsync(whoami, cancellationToken);
            switch (identity.StatusCode)
            {
                case System.Net.HttpStatusCode.OK:
                    var body = await identity.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken);
                    output.WriteLine($"Account:      {body.GetProperty("email").GetString()} (token '{body.GetProperty("tokenName").GetString()}')");
                    return ExitCodes.Success;
                case System.Net.HttpStatusCode.Unauthorized:
                    output.WriteLine("Account:      the stored token was rejected (revoked or expired). Run 'mytunnel login'.");
                    return ExitCodes.Error;
                default:
                    output.WriteLine("Account:      this gateway has no account system.");
                    return ExitCodes.Success;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            output.WriteLine($"Gateway health: unreachable ({ex.Message})");
            return ExitCodes.Error;
        }
    }

    private int Config(CommandLine command)
    {
        if (command.Positionals is ["set", "gateway", var url])
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return Usage($"Invalid gateway URL '{url}'.");
            }

            if (TunnelClientOptions.ValidateGatewayUri(uri) is { } problem)
            {
                return Usage(problem);
            }

            var config = settings.LoadConfig();
            config.Gateway = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
            settings.SaveConfig(config);
            output.WriteLine($"Gateway set to {config.Gateway}.");
            return ExitCodes.Success;
        }

        if (command.Positionals.Count != 0)
        {
            return Usage("Usage: mytunnel config | mytunnel config set gateway <url>");
        }

        var (gateway, gatewaySource) = settings.ResolveGateway(null);
        var (token, tokenSource) = settings.ResolveToken(null);
        output.WriteLine($"Config directory: {settings.Directory}");
        output.WriteLine($"Gateway:          {gateway}  ({gatewaySource})");
        output.WriteLine($"Credential:       {(token is null ? "none" : tokenSource)}");
        return ExitCodes.Success;
    }

    private int Version()
    {
        output.WriteLine($"mytunnel {TunnelClientOptions.ClientVersion} (protocol {TunnelProtocol.CurrentVersion})");
        return ExitCodes.Success;
    }

    private int Usage(string message)
    {
        error.WriteLine(message);
        error.WriteLine("Run 'mytunnel --help' for usage.");
        return ExitCodes.Usage;
    }

    private static string? ReadSecretLine()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var buffer = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return buffer.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
            }
        }
    }
}

/// <summary>Minimal parser: positionals plus <c>--name value</c> and <c>--flag</c> options.</summary>
public sealed class CommandLine
{
    private static readonly HashSet<string> Flags = ["https", "paste", "no-browser"];
    private readonly Dictionary<string, string?> _options = new(StringComparer.Ordinal);

    public List<string> Positionals { get; } = [];

    public static CommandLine Parse(string[] args)
    {
        var result = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                result.Positionals.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? value = null;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (!Flags.Contains(name))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Option '--{name}' requires a value.");
                }

                value = args[++i];
            }

            result._options[name] = value;
        }

        return result;
    }

    public string? Get(string name) => _options.GetValueOrDefault(name);

    public bool Has(string name) => _options.ContainsKey(name);

    public bool TryGetUnknown(out string unknown, params string[] known)
    {
        unknown = _options.Keys.FirstOrDefault(k => !known.Contains(k)) ?? "";
        return unknown.Length == 0;
    }
}
