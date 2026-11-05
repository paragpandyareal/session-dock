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

    /// <summary>Writes the hook script (embedded in the exe) to %LOCALAPPDATA%\AgentDock if it is missing or out of date.</summary>
    public static void RefreshScript()
    {
        try
        {
            using var stream = typeof(HookInstaller).Assembly.GetManifestResourceStream(ScriptName);
            if (stream == null) return;
            Directory.CreateDirectory(ConfigStore.Dir);
            string text = new StreamReader(stream).ReadToEnd();
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
            output.WriteLine($"Could not write the hook script to {InstalledScriptPath}.");
            return 1;
        }

        string? node = FindNode();
        if (node == null)
        {
            output.WriteLine("Session Dock needs Node.js to hear from Claude Code, and it isn't installed.");
            output.WriteLine("Install it with: winget install OpenJS.NodeJS.LTS");
            output.WriteLine("Then click \"Not connected\" at the bottom of the dock to finish.");
            return 1;
        }

        // Make sure the hook has a port to read even before the app first runs. Never move an
        // existing port: the running dock may be listening on it.
        ConfigStore.EnsureExists();

        // Reinstalling or upgrading shouldn't rewrite settings.json (or pile up backups) when nothing changed.
        if (IsCurrent(node))
        {
            output.WriteLine($"Session Dock is already connected to Claude Code ({SettingsPath}). Nothing to change.");
            return 0;
        }

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
            groups.Add(new JsonObject { ["hooks"] = new JsonArray { Handler(name, node) } });
        }

        Save(root);
        output.WriteLine($"Session Dock hooks registered in {SettingsPath}");
        if (backup != null) output.WriteLine($"Backup of the previous settings: {backup}");
        output.WriteLine($"Hook command: \"{node}\" \"{InstalledScriptPath}\"");
        output.WriteLine("Restart running Claude Code sessions so they pick up the new hooks.");
        return 0;
    }

    static JsonObject Handler(string eventName, string node) => new()
    {
        ["type"] = "command",
        ["command"] = node,
        ["args"] = new JsonArray(InstalledScriptPath),
        ["timeout"] = 5,
        // Run in the background so Claude Code never waits on the dock.
        // SessionEnd stays synchronous so it is delivered before the process exits.
        ["async"] = eventName != "SessionEnd",
    };

    /// <summary>True when every event already has exactly one Session Dock handler, identical to what Install would write.</summary>
    static bool IsCurrent(string node)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            if (JsonNode.Parse(File.ReadAllText(SettingsPath), null, ReadOptions)?["hooks"] is not JsonObject hooks) return false;
            foreach (var name in Events)
            {
                if (hooks[name] is not JsonArray groups) return false;
                var ours = groups.Where(g => g?["hooks"] is JsonArray)
                                 .SelectMany(g => (JsonArray)g!["hooks"]!)
                                 .Where(IsOurHandler)
                                 .ToList();
                if (ours.Count != 1 || !JsonNode.DeepEquals(ours[0], Handler(name, node))) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
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
