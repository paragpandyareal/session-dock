using System;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentDock;

/// <summary>
/// Merges the Session Dock hook into ~/.claude/settings.json (user level), after a
/// timestamped backup. Only handlers that point at agent-dock-hook.js are ever
/// added, replaced or removed; everything else in the file is left as it is.
/// </summary>
public static class HookInstaller
{
    public const string ScriptName = "agent-dock-hook.js";

    static readonly string[] Events =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PostToolUse",
        "Notification", "Stop", "SubagentStop", "SessionEnd",
    ];

    public static string SettingsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    public static string InstalledScriptPath => Path.Combine(ConfigStore.Dir, ScriptName);

    static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Copies the bundled hook script to %LOCALAPPDATA%\AgentDock if it is missing or out of date.</summary>
    public static void RefreshScript()
    {
        try
        {
            string source = Path.Combine(AppContext.BaseDirectory, "hook", ScriptName);
            if (!File.Exists(source)) return;
            Directory.CreateDirectory(ConfigStore.Dir);
            string text = File.ReadAllText(source);
            if (!File.Exists(InstalledScriptPath) || File.ReadAllText(InstalledScriptPath) != text)
                File.WriteAllText(InstalledScriptPath, text);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not copy hook script: {ex.Message}");
        }
    }

    public static int Install(TextWriter output)
    {
        RefreshScript();
        if (!File.Exists(InstalledScriptPath))
        {
            output.WriteLine($"Hook script not found next to the app ({Path.Combine(AppContext.BaseDirectory, "hook", ScriptName)}).");
            return 1;
        }

        string? node = FindNode();
        if (node == null)
        {
            output.WriteLine("Could not find node.exe. Install Node.js (winget install OpenJS.NodeJS.LTS) and run this again.");
            return 1;
        }

        // Make sure the hook has a port to read even before the app first runs.
        ConfigStore.LoadWithFreePort();

        var root = LoadSettings(output, out string? backup);
        if (root == null) return 1;

        var hooks = root["hooks"] as JsonObject;
        if (hooks == null)
        {
            hooks = new JsonObject();
            root["hooks"] = hooks;
        }

        foreach (var name in Events)
        {
            if (hooks[name] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[name] = groups;
            }
            RemoveOurHandlers(groups);
            groups.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "command",
                        ["command"] = node,
                        ["args"] = new JsonArray(InstalledScriptPath),
                        ["timeout"] = 5,
                        // Run in the background so Claude Code never waits on the dock.
                        // SessionEnd stays synchronous so it is delivered before the process exits.
                        ["async"] = name != "SessionEnd",
                    },
                },
            });
        }

        Save(root);
        output.WriteLine($"Session Dock hooks registered in {SettingsPath}");
        if (backup != null) output.WriteLine($"Backup of the previous settings: {backup}");
        output.WriteLine($"Hook command: \"{node}\" \"{InstalledScriptPath}\"");
        output.WriteLine("Restart running Claude Code sessions so they pick up the new hooks.");
        return 0;
    }

    public static int Uninstall(TextWriter output)
    {
        if (!File.Exists(SettingsPath))
        {
            output.WriteLine("No user settings file, nothing to remove.");
            return 0;
        }
        var root = LoadSettings(output, out string? backup);
        if (root == null) return 1;

        if (root["hooks"] is JsonObject hooks)
        {
            foreach (var name in hooks.Select(p => p.Key).ToList())
            {
                if (hooks[name] is not JsonArray groups) continue;
                RemoveOurHandlers(groups);
                if (groups.Count == 0) hooks.Remove(name);
            }
            if (hooks.Count == 0) root.Remove("hooks");
        }

        Save(root);
        output.WriteLine($"Session Dock hooks removed from {SettingsPath}");
        if (backup != null) output.WriteLine($"Backup of the previous settings: {backup}");
        return 0;
    }

    /// <summary>True when every event has an Session Dock handler.</summary>
    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(SettingsPath), null, ReadOptions)?["hooks"] is not JsonObject hooks) return false;
            return Events.All(e => hooks[e] is JsonArray groups && groups.Any(g => IsOurs(g)));
        }
        catch
        {
            return false;
        }
    }

    static JsonObject? LoadSettings(TextWriter output, out string? backup)
    {
        backup = null;
        if (!File.Exists(SettingsPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            return new JsonObject();
        }

        string text = File.ReadAllText(SettingsPath);
        backup = $"{SettingsPath}.agentdock-backup-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Copy(SettingsPath, backup, overwrite: false);

        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            if (JsonNode.Parse(text, null, ReadOptions) is JsonObject obj) return obj;
            output.WriteLine($"{SettingsPath} is not a JSON object; leaving it untouched.");
        }
        catch (JsonException ex)
        {
            output.WriteLine($"{SettingsPath} is not valid JSON ({ex.Message}); leaving it untouched.");
        }
        return null;
    }

    static void Save(JsonObject root)
    {
        string temp = SettingsPath + ".agentdock-tmp";
        File.WriteAllText(temp, root.ToJsonString(WriteOptions) + Environment.NewLine);
        File.Move(temp, SettingsPath, overwrite: true);
    }

    /// <summary>Removes Session Dock handlers from each matcher group, and drops groups left empty.</summary>
    static void RemoveOurHandlers(JsonArray groups)
    {
        for (int i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i]?["hooks"] is not JsonArray handlers) continue;
            bool hadAny = handlers.Count > 0;
            for (int j = handlers.Count - 1; j >= 0; j--)
                if (IsOurHandler(handlers[j])) handlers.RemoveAt(j);
            if (hadAny && handlers.Count == 0) groups.RemoveAt(i);
        }
    }

    static bool IsOurs(JsonNode? group) =>
        group?["hooks"] is JsonArray handlers && handlers.Any(IsOurHandler);

    static bool IsOurHandler(JsonNode? handler)
    {
        if (handler is not JsonObject h) return false;
        if (h.Str("command")?.Contains(ScriptName, StringComparison.OrdinalIgnoreCase) == true) return true;
        return h["args"] is JsonArray args &&
               args.Any(a => a is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains(ScriptName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Absolute path to node.exe, since the desktop app runs hooks with a limited PATH.</summary>
    static string? FindNode()
    {
        // Prefer the standard install location (admin-writable only) over whatever PATH resolves first.
        string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        if (File.Exists(standard)) return standard;

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), "node.exe");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
            }
        }

        string[] fallbacks =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe"),
        ];
        return fallbacks.FirstOrDefault(File.Exists);
    }
}
