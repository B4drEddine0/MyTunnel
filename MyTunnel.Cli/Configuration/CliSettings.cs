using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyTunnel.Cli.Configuration;

/// <summary>Persisted, non-secret CLI settings (config.json in the MyTunnel config directory).</summary>
public sealed class CliConfig
{
    public string? Gateway { get; set; }
}

/// <summary>Locations and persistence of CLI settings and the stored credential.</summary>
public sealed class CliSettings
{
    public const string DefaultGateway = "http://localhost:5018";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public CliSettings(string? directory = null)
    {
        Directory = directory
            ?? Environment.GetEnvironmentVariable("MYTUNNEL_CONFIG_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyTunnel");
        Credentials = OperatingSystem.IsWindows()
            ? new DpapiCredentialStore(Path.Combine(Directory, "credentials.bin"))
            : new FileCredentialStore(Path.Combine(Directory, "credentials"));
    }

    public string Directory { get; }

    public string ConfigPath => Path.Combine(Directory, "config.json");

    public ICredentialStore Credentials { get; }

    public CliConfig LoadConfig()
    {
        if (!File.Exists(ConfigPath))
        {
            return new CliConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CliConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new CliConfig();
        }
        catch (JsonException)
        {
            return new CliConfig();
        }
    }

    public void SaveConfig(CliConfig config)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, JsonOptions));
    }

    /// <summary>Resolution order: command-line option, MYTUNNEL_GATEWAY, config file, development default.</summary>
    public (string Value, string Source) ResolveGateway(string? optionValue)
    {
        if (!string.IsNullOrWhiteSpace(optionValue))
        {
            return (optionValue, "--gateway option");
        }

        if (Environment.GetEnvironmentVariable("MYTUNNEL_GATEWAY") is { Length: > 0 } fromEnv)
        {
            return (fromEnv, "MYTUNNEL_GATEWAY");
        }

        if (LoadConfig().Gateway is { Length: > 0 } fromConfig)
        {
            return (fromConfig, ConfigPath);
        }

        return (DefaultGateway, "default");
    }

    /// <summary>Resolution order: command-line option, MYTUNNEL_TOKEN, stored credential.</summary>
    public (string? Value, string Source) ResolveToken(string? optionValue)
    {
        if (!string.IsNullOrEmpty(optionValue))
        {
            return (optionValue, "--token option");
        }

        if (Environment.GetEnvironmentVariable("MYTUNNEL_TOKEN") is { Length: > 0 } fromEnv)
        {
            return (fromEnv, "MYTUNNEL_TOKEN");
        }

        return Credentials.Load() is { } stored ? (stored, Credentials.Description) : (null, "none");
    }
}

public interface ICredentialStore
{
    string Description { get; }

    string? Load();

    void Save(string token);

    bool Delete();
}

/// <summary>Encrypts the token with Windows DPAPI, so only the current Windows user can decrypt it.</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore(string path) : ICredentialStore
{
    private static readonly byte[] Entropy = "MyTunnel.Cli.Credential.v1"u8.ToArray();

    public string Description => $"{path} (DPAPI, current user)";

    public string? Load()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
    }

    public bool Delete()
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}

/// <summary>Non-Windows fallback: a file readable only by the owner (mode 0600).</summary>
public sealed class FileCredentialStore(string path) : ICredentialStore
{
    public string Description => $"{path} (owner-only file)";

    public string? Load() => File.Exists(path) ? File.ReadAllText(path).Trim() : null;

    public void Save(string token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var stream = new FileStream(path, options);
        if (!OperatingSystem.IsWindows())
        {
            // UnixCreateMode applies only to new files; tighten an existing file before writing the token.
            File.SetUnixFileMode(stream.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        stream.Write(Encoding.UTF8.GetBytes(token));
    }

    public bool Delete()
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
