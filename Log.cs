using System;
using System.IO;

namespace AgentDock;

/// <summary>Small append-only log at %LOCALAPPDATA%\AgentDock\agentdock.log, reset when it grows past 1 MB.</summary>
public static class Log
{
    static readonly object Gate = new();
    static string FilePath => Path.Combine(ConfigStore.Dir, "agentdock.log");

    public static void Init()
    {
        try
        {
            Directory.CreateDirectory(ConfigStore.Dir);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > 1_000_000) info.Delete();
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the app.
        }
    }
}
