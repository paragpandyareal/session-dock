using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentDock;

/// <summary>Turns a tool call into a short plain-English phrase, e.g. "Editing app.js".</summary>
public static class ToolDescriber
{
    public static string Describe(string? tool, JsonObject? input)
    {
        string? S(string key) => input.Str(key);
        string file = FileName(S("file_path") ?? S("notebook_path") ?? S("path"));

        switch (tool)
        {
            case null or "": return "Working";
            case "Edit" or "MultiEdit": return $"Editing {file}";
            case "Write": return $"Writing {file}";
            case "Read": return $"Reading {file}";
            case "NotebookEdit": return $"Editing notebook {file}";
            case "Bash" or "PowerShell":
                // Claude writes a short description for each command ("Run the test suite"); prefer it.
                if (S("description") is { Length: > 0 } desc) return OneLine(Capitalize(desc), 80);
                return $"Running {Command(S("command"))}";
            case "BashOutput": return "Checking command output";
            case "KillShell" or "KillBash" or "TaskStop": return "Stopping a background task";
            case "Grep": return $"Searching for \"{Shorten(S("pattern"), 40)}\"";
            case "Glob": return $"Finding files {Shorten(S("pattern"), 40)}";
            case "WebFetch": return $"Reading {Host(S("url"))}";
            case "WebSearch": return $"Searching the web for \"{Shorten(S("query"), 40)}\"";
            case "Task" or "Agent":
                var what = S("description");
                return string.IsNullOrWhiteSpace(what) ? "Running a subagent" : $"Subagent: {Shorten(what, 60)}";
            case "TodoWrite": return "Updating the to-do list";
            case "AskUserQuestion": return "Asking you a question";
            case "ExitPlanMode": return "Waiting for plan approval";
            case "EnterPlanMode": return "Planning";
            case "Skill": return $"Using skill {Shorten(S("skill") ?? S("command"), 40)}";
            case "ToolSearch": return "Loading tools";
            case "SlashCommand": return $"Running {Shorten(S("command"), 40)}";
        }

        if (tool.StartsWith("mcp__", StringComparison.Ordinal))
        {
            var parts = tool.Split("__", StringSplitOptions.RemoveEmptyEntries);
            string action = Humanize(parts.Length > 2 ? string.Join(" ", parts.Skip(2)) : parts.Last());
            string server = parts.Length > 1 ? parts[1] : "";
            bool opaque = server.Length > 24 || Guid.TryParse(server, out _);
            if (action.Equals("update plan", StringComparison.OrdinalIgnoreCase)) return "Updating the task list";
            return opaque || server.Length == 0 ? $"Using {action}" : $"Using {Humanize(server)}: {action}";
        }

        return $"Using {tool}";
    }

    /// <summary>Tools that block until the user answers.</summary>
    public static bool NeedsUser(string? tool) => tool is "AskUserQuestion" or "ExitPlanMode";

    public static string OneLine(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Shorten(Regex.Replace(text, @"\s+", " ").Trim(), max);
    }

    /// <summary>The raw command or input behind an activity, shown on hover.</summary>
    public static string Detail(string? tool, JsonObject? input) => tool switch
    {
        "Bash" or "PowerShell" => OneLine(input.Str("command"), 300),
        "WebFetch" => input.Str("url") ?? "",
        _ => input.Str("file_path") ?? input.Str("notebook_path") ?? "",
    };

    /// <summary>The leading words of a command, stopping before paths, variables or pipes so nothing private shows.</summary>
    static string Command(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "a command";
        var first = command.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? command;
        var words = first.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new System.Collections.Generic.List<string>();
        foreach (var w in words)
        {
            if (w == "--") continue;
            if (kept.Count == 3 || w.IndexOfAny(['\\', '/', ':', '$', '=', ';', '|', '&', '"', '\'', '(', '{', '>', '<']) >= 0) break;
            kept.Add(w);
        }
        if (kept.Count == 0) return "a command";
        string text = string.Join(' ', kept);
        return kept.Count < words.Length ? text + " …" : text;
    }

    static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    static string FileName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "a file";
        try
        {
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? path : name;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    static string Host(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : Shorten(url, 40);

    static string Humanize(string s) => s.Replace('_', ' ').Replace('-', ' ').Trim();

    static string Shorten(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }
}

public static class JsonExtensions
{
    public static string? Str(this JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static long? Long(this JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }
}
