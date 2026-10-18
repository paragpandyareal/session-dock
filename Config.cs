using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace AgentDock;

public sealed class AppConfig
{
    public int Port { get; set; }
    /// <summary>Random per-install secret the hook must send, so web pages and other apps can't post events.</summary>
    public string Token { get; set; } = "";
    public bool CompactMode { get; set; }
    /// <summary>Null means "follow the Windows animation setting".</summary>
    public bool? CalmMode { get; set; }
    public bool PlainMode { get; set; }
    public bool MiniMode { get; set; }
}

/// <summary>Stores the listener port in %LOCALAPPDATA%\AgentDock\config.json, where the hook script reads it.</summary>
public static class ConfigStore
{
    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AgentDock");

    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath), JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read config: {ex.Message}");
        }
        return new AppConfig();
    }

    public static void Save(AppConfig config)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(config, JsonOptions));
    }

    /// <summary>Loads the config, keeping the saved port if it is still free, otherwise picking a new free one.</summary>
    public static AppConfig LoadWithFreePort()
    {
        var config = Load();
        bool changed = !File.Exists(FilePath);
        if (config.Port is < 1024 or > 65535 || !IsPortFree(config.Port))
        {
            config.Port = FindFreePort();
            changed = true;
        }
        if (config.Token.Length < 32)
        {
            config.Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            changed = true;
        }
        if (changed) Save(config);
        return config;
    }

    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static bool IsPortFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
