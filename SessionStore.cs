using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace AgentDock;

/// <summary>Turns hook events into session cards. All members run on the UI thread.</summary>
public sealed class SessionStore : ObservableObject
{
    static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);
    static readonly TimeSpan RemoveEndedAfter = TimeSpan.FromSeconds(60);

    static readonly TimeSpan NotifyFinishedAfter = TimeSpan.FromSeconds(30);
    const int MaxSessions = 64;
    static readonly System.Text.RegularExpressions.Regex DesktopSessionId =
        new(@"^local_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

    readonly Dispatcher _dispatcher;
    readonly DispatcherTimer _timer;
    readonly AppConfig _config;
    int _ticks;
    int _hueCounter;

    public SessionStore(Dispatcher dispatcher, AppConfig config)
    {
        _dispatcher = dispatcher;
        _config = config;
        _calmMode = config.CalmMode ?? !System.Windows.SystemParameters.ClientAreaAnimation;
        SessionInfo.InstantText = _calmMode;
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Sessions.CollectionChanged += (_, _) => RefreshSummary();
        Restore();
        RefreshSummary();
    }

    // Cards survive an app restart. Only bookkeeping is saved: no prompts, replies or tool details.
    static string StatePath => Path.Combine(ConfigStore.Dir, "sessions.json");
    bool _dirty;

    sealed record SavedSession(string SessionId, string Cwd, int ClaudePid, string? TranscriptPath, string Source,
        bool SourceFromTranscript, string? HostSessionId, DateTime StartedAt, bool StartTimeKnown, DateTime LastUpdate,
        AgentStatus Status, int ToolCount, long LastEventTs);

    void Restore()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var saved = JsonSerializer.Deserialize<SavedSession[]>(File.ReadAllText(StatePath)) ?? [];
            var now = DateTime.Now;
            foreach (var s in saved.Take(MaxSessions))
            {
                // Only bring back sessions whose Claude Code process is still running.
                if (string.IsNullOrEmpty(s.SessionId) || s.SessionId.Length > 100 || s.Status == AgentStatus.Ended
                    || s.ClaudePid <= 0 || !IsClaudeRunning(s.ClaudePid) || Sessions.Any(x => x.SessionId == s.SessionId))
                    continue;
                var session = new SessionInfo(s.SessionId, s.StartedAt, (_hueCounter++ * 137.508 + 20) % 360)
                {
                    ClaudePid = s.ClaudePid,
                    TranscriptPath = s.TranscriptPath,
                    Source = s.Source ?? "",
                    SourceFromTranscript = s.SourceFromTranscript,
                    HostSessionId = s.HostSessionId is { } host && DesktopSessionId.IsMatch(host) ? host : null,
                    StartTimeKnown = s.StartTimeKnown,
                    LastUpdate = s.LastUpdate > now ? now : s.LastUpdate,
                    ToolCount = s.ToolCount,
                    LastEventTs = s.LastEventTs,
                    Status = s.Status,
                    Activity = s.Status switch
                    {
                        AgentStatus.Working => "Working…",
                        AgentStatus.Waiting => "Waiting for you",
                        AgentStatus.Finished => "Finished, waiting for your next prompt",
                        _ => "Waiting for a prompt",
                    },
                };
                if (s.Cwd is { Length: > 0 and < 1024 } cwd)
                {
                    session.Cwd = cwd;
                    session.Project = ProjectName(cwd);
                    session.Branch = GitInfo.Branch(cwd);
                }
                if (s.Status == AgentStatus.Waiting) session.WaitingSince = session.LastUpdate;
                session.RefreshTimes(now);
                Sessions.Add(session);
            }
            Resort();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not restore sessions: {ex.Message}");
        }
    }

    void SaveState()
    {
        try
        {
            var saved = Sessions.Where(s => s.Status != AgentStatus.Ended).Select(s => new SavedSession(
                s.SessionId, s.Cwd, s.ClaudePid, s.TranscriptPath, s.Source, s.SourceFromTranscript, s.HostSessionId,
                s.StartedAt, s.StartTimeKnown, s.LastUpdate, s.Status, s.ToolCount, s.LastEventTs)).ToArray();
            Directory.CreateDirectory(ConfigStore.Dir);
            string temp = StatePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved));
            File.Move(temp, StatePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not save sessions: {ex.Message}");
        }
    }

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    /// <summary>Raised when a session moves into "Waiting for you".</summary>
    public event Action<SessionInfo, string>? AttentionNeeded;

    /// <summary>Raised when a turn that ran for a while finishes.</summary>
    public event Action<SessionInfo, TimeSpan>? LongTaskFinished;

    // Display preferences (saved to config.json).
    public bool CompactMode
    {
        get => _config.CompactMode;
        set { if (_config.CompactMode == value) return; _config.CompactMode = value; ConfigStore.Update(c => c.CompactMode = value); OnPropertyChanged(); }
    }

    bool _calmMode;
    /// <summary>No looping animations. Defaults to the Windows "Show animations" setting.</summary>
    public bool CalmMode
    {
        get => _calmMode;
        set
        {
            if (!Set(ref _calmMode, value)) return;
            SessionInfo.InstantText = value;
            _config.CalmMode = value;
            ConfigStore.Update(c => c.CalmMode = value);
        }
    }

    /// <summary>Dock pushed in to a slim strip of critters.</summary>
    public bool MiniMode
    {
        get => _config.MiniMode;
        set { if (_config.MiniMode == value) return; _config.MiniMode = value; ConfigStore.Update(c => c.MiniMode = value); OnPropertyChanged(); }
    }

    /// <summary>Plain wording instead of game phrases (good for screen sharing).</summary>
    public bool PlainMode
    {
        get => _config.PlainMode;
        set { if (_config.PlainMode == value) return; _config.PlainMode = value; ConfigStore.Update(c => c.PlainMode = value); OnPropertyChanged(); }
    }

    string _summary = "";
    public string Summary { get => _summary; private set => Set(ref _summary, value); }

    bool _isEmpty = true;
    public bool IsEmpty { get => _isEmpty; private set => Set(ref _isEmpty, value); }

    public void Apply(JsonObject ev)
    {
        string? id = ev.Str("session_id");
        string name = ev.Str("hook_event_name") ?? "";
        if (string.IsNullOrEmpty(id)) return;

        if (id.Length > 100) return;

        var now = DateTime.Now;
        var session = Sessions.FirstOrDefault(s => s.SessionId == id);
        bool isNew = session == null;
        if (session == null)
        {
            if (Sessions.Count >= MaxSessions) return; // keep memory bounded
            // Golden-angle hue steps keep critters on screen visibly different.
            session = new SessionInfo(id, now, (_hueCounter++ * 137.508 + 20) % 360);
            Sessions.Add(session);
        }

        long ts = ev.Long("sent_at") ?? DateTimeOffset.Now.ToUnixTimeMilliseconds();
        // Hooks run asynchronously, so events can arrive slightly out of order.
        // Older events still refresh details but never change the status.
        bool inOrder = ts >= session.LastEventTs;
        if (inOrder) session.LastEventTs = ts;

        var previous = session.Status;
        session.LastUpdate = now;
        session.IsStale = false;

        if (ev.Str("cwd") is { Length: > 0 and < 1024 } cwd)
        {
            session.Cwd = cwd;
            session.Project = ProjectName(cwd);
            session.Branch = GitInfo.Branch(cwd);
        }
        if (ev.Long("claude_pid") is long pid and > 0) session.ClaudePid = (int)pid;
        if (!session.SourceFromTranscript && SourceLabel(ev.Str("entrypoint")) is { Length: > 0 } src) session.Source = src;
        if (ev.Str("transcript_path") is { Length: > 0 } tp) session.TranscriptPath = tp;
        // Only trust the desktop session id from desktop sessions; a terminal opened from the app can inherit it.
        // The id becomes part of a claude:// link, so accept only the exact expected shape.
        if (ev.Str("host_session_id") is { Length: > 0 } host && ev.Str("entrypoint") == "claude-desktop"
            && DesktopSessionId.IsMatch(host))
            session.HostSessionId = host;
        if (name == "PreToolUse") session.ToolCount++;

        if (session.Status == AgentStatus.Ended && name != "SessionEnd")
        {
            session.EndedAt = null;
            session.Status = AgentStatus.Idle;
        }

        switch (name)
        {
            case "SessionStart":
            {
                string source = ev.Str("source") ?? "";
                if (isNew && source == "startup") session.StartTimeKnown = true;
                if (session.Status != AgentStatus.Working)
                {
                    session.Status = AgentStatus.Idle;
                    session.Activity = source switch
                    {
                        "resume" => "Resumed, waiting for a prompt",
                        "compact" => "Context compacted",
                        "clear" => "Cleared, waiting for a prompt",
                        _ => "Started, waiting for a prompt",
                    };
                }
                break;
            }
            case "UserPromptSubmit":
                session.LastPrompt = ToolDescriber.OneLine(ev.Str("prompt"), 240);
                if (inOrder)
                {
                    session.Status = AgentStatus.Working;
                    session.Activity = "Thinking…";
                    session.ActivityDetail = "";
                    session.LastToolActivity = "";
                    session.TurnStartedAt = now;
                }
                break;

            case "PreToolUse":
                if (inOrder)
                {
                    string? tool = ev.Str("tool_name");
                    session.Activity = ToolDescriber.Describe(tool, ev["tool_input"] as JsonObject);
                    session.ActivityDetail = ToolDescriber.Detail(tool, ev["tool_input"] as JsonObject);
                    session.LastToolActivity = session.Activity;
                    session.Status = ToolDescriber.NeedsUser(tool) ? AgentStatus.Waiting : AgentStatus.Working;
                }
                break;

            case "PostToolUse":
                // A tool finished, so any permission prompt was answered.
                if (inOrder && session.Status != AgentStatus.Working)
                {
                    session.Status = AgentStatus.Working;
                    if (previous == AgentStatus.Waiting) session.Activity = "Thinking…";
                }
                break;

            case "Notification":
            {
                string type = ev.Str("notification_type") ?? "";
                string message = ToolDescriber.OneLine(ev.Str("message"), 160);
                bool needsUser = type is "permission_prompt" or "elicitation_dialog" or "elicitation_url_dialog" or "agent_needs_input"
                    || (type.Length == 0 && message.Contains("permission", StringComparison.OrdinalIgnoreCase));
                if (needsUser && session.Status != AgentStatus.Finished)
                {
                    // Prefer the pending tool ("Running npm test") over the generic notification text.
                    if (type == "permission_prompt" && session.Status == AgentStatus.Working && session.LastToolActivity.Length > 0)
                        session.Activity = $"Permission needed · {session.LastToolActivity}";
                    else if (message.Length > 0)
                        session.Activity = message;
                    session.Status = AgentStatus.Waiting;
                }
                break;
            }
            case "Stop":
                if (inOrder)
                {
                    session.Status = AgentStatus.Finished;
                    string reply = ToolDescriber.OneLine(ev.Str("last_assistant_message"), 220);
                    session.Activity = reply.Length > 0 ? reply : "Finished, waiting for your next prompt";
                    session.ActivityDetail = "";
                    if (session.TurnStartedAt is DateTime turn && now - turn >= NotifyFinishedAfter)
                        LongTaskFinished?.Invoke(session, now - turn);
                    session.TurnStartedAt = null;
                }
                break;

            case "SubagentStop":
                if (inOrder && session.Status == AgentStatus.Working)
                {
                    string type = ev.Str("agent_type") ?? "";
                    session.Activity = type.Length > 0 ? $"Subagent finished ({type})" : "Subagent finished";
                }
                break;

            case "SessionEnd":
                session.Status = AgentStatus.Ended;
                session.EndedAt = now;
                session.Activity = ev.Str("reason") switch
                {
                    "clear" => "Session cleared",
                    "logout" => "Logged out",
                    "prompt_input_exit" => "Session closed",
                    "resume" => "Switched to another session",
                    _ => "Session ended",
                };
                break;
        }

        if (session.Status == AgentStatus.Waiting && previous != AgentStatus.Waiting)
        {
            bool permission = session.Activity.Contains("permission", StringComparison.OrdinalIgnoreCase);
            session.WaitingSince = now;
            session.BattleText = PlainMode
                ? (permission ? "Claude needs your approval to continue." : "Claude is waiting for your answer.")
                : (permission ? "A wild permission prompt appeared!" : $"{session.CritterName.ToUpperInvariant()} wants your answer!");
            AttentionNeeded?.Invoke(session, session.Activity);
        }
        if (session.Status != AgentStatus.Waiting) session.WaitingSince = null;

        session.RefreshTimes(now);
        MaybeReadTranscript(session);
        if (previous != session.Status || isNew) Resort();
        RefreshSummary();
        _dirty = true;
    }

    public void Remove(SessionInfo session)
    {
        Sessions.Remove(session);
        _dirty = true;
    }

    void Tick()
    {
        var now = DateTime.Now;
        bool checkProcesses = ++_ticks % 5 == 0;
        bool changed = false;

        foreach (var s in Sessions.ToList())
        {
            if (s.Status == AgentStatus.Ended)
            {
                if (now - s.EndedAt > RemoveEndedAfter)
                {
                    Sessions.Remove(s);
                    _dirty = true;
                    continue;
                }
            }
            else
            {
                if (!s.IsStale && now - s.LastUpdate > StaleAfter)
                {
                    s.IsStale = true;
                    if (s.Status == AgentStatus.Finished) s.Status = AgentStatus.Idle;
                    changed = true;
                }
                if (checkProcesses && s.ClaudePid > 0 && !IsClaudeRunning(s.ClaudePid))
                {
                    s.Status = AgentStatus.Ended;
                    s.EndedAt = now;
                    s.Activity = "Claude Code process exited";
                    changed = true;
                }
            }
            s.RefreshTimes(now);
        }

        if (changed)
        {
            Resort();
            RefreshSummary();
            _dirty = true;
        }
        if (_dirty)
        {
            _dirty = false;
            SaveState();
        }
    }

    internal static bool IsClaudeRunning(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            var name = p.ProcessName;
            return name.Contains("claude", StringComparison.OrdinalIgnoreCase)
                || name.Contains("node", StringComparison.OrdinalIgnoreCase)
                || name.Contains("bun", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch
        {
            return true; // Access denied etc.: assume alive.
        }
    }

    void Resort()
    {
        var ordered = Sessions.OrderBy(Rank).ThenByDescending(s => s.StartedAt).ToList();
        for (int i = 0; i < ordered.Count; i++)
        {
            int current = Sessions.IndexOf(ordered[i]);
            if (current != i) Sessions.Move(current, i);
        }
    }

    static int Rank(SessionInfo s)
    {
        int rank = s.Status switch
        {
            AgentStatus.Waiting => 0,
            AgentStatus.Working => 1,
            AgentStatus.Finished => 2,
            AgentStatus.Idle => 3,
            _ => 4,
        };
        return s.IsStale ? rank + 5 : rank;
    }

    void RefreshSummary()
    {
        IsEmpty = Sessions.Count == 0;
        int live = Sessions.Count(s => s.Status != AgentStatus.Ended);
        int working = Sessions.Count(s => s.Status == AgentStatus.Working && !s.IsStale);
        int waiting = Sessions.Count(s => s.Status == AgentStatus.Waiting);
        var text = live == 1 ? "1 session" : $"{live} sessions";
        if (working > 0) text += $" · {working} working";
        if (waiting > 0) text += waiting == 1 ? " · 1 needs you" : $" · {waiting} need you";
        Summary = text;
    }

    /// <summary>Reads the transcript header for the entrypoint (CLI vs desktop) and the real start time.</summary>
    void MaybeReadTranscript(SessionInfo session)
    {
        if (session.SourceFromTranscript || session.TranscriptAttempts >= 5) return;
        string? path = session.TranscriptPath;
        if (string.IsNullOrEmpty(path)) return;
        session.TranscriptAttempts++;

        Task.Run(() =>
        {
            var (entrypoint, started) = ReadTranscriptHeader(path);
            if (entrypoint == null && started == null) return;
            _dispatcher.BeginInvoke(() =>
            {
                if (SourceLabel(entrypoint) is { Length: > 0 } label)
                {
                    session.Source = label;
                    session.SourceFromTranscript = true;
                }
                if (!session.StartTimeKnown && started is DateTime start && start < session.StartedAt)
                {
                    session.StartedAt = start;
                    session.StartTimeKnown = true;
                    session.RefreshTimes(DateTime.Now);
                }
            });
        });
    }

    static (string? entrypoint, DateTime? started) ReadTranscriptHeader(string path)
    {
        string? entrypoint = null;
        DateTime? started = null;
        try
        {
            if (!File.Exists(path)) return (null, null);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            for (int i = 0; i < 200 && reader.ReadLine() is { } line; i++)
            {
                if (line.Length > 200_000) continue;
                try
                {
                    if (JsonNode.Parse(line) is not JsonObject obj) continue;
                    entrypoint ??= obj.Str("entrypoint");
                    if (started == null && obj.Str("timestamp") is { } stamp &&
                        DateTime.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
                        started = t.ToLocalTime();
                }
                catch (JsonException)
                {
                }
                if (entrypoint != null && started != null) break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not read transcript {path}: {ex.Message}");
        }
        return (entrypoint, started);
    }

    static string SourceLabel(string? entrypoint) => entrypoint switch
    {
        null or "" => "",
        "cli" => "CLI",
        "claude-desktop" => "Desktop",
        "claude-vscode" => "VS Code",
        _ when entrypoint.Contains("jetbrains", StringComparison.OrdinalIgnoreCase) => "JetBrains",
        _ when entrypoint.StartsWith("sdk", StringComparison.OrdinalIgnoreCase) => "SDK",
        _ => entrypoint,
    };

    static string ProjectName(string cwd)
    {
        try
        {
            var name = Path.GetFileName(cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? cwd : name;
        }
        catch (ArgumentException)
        {
            return cwd;
        }
    }

    public string ToJson() => JsonSerializer.Serialize(Sessions.Select(s => new
    {
        s.SessionId,
        s.Project,
        s.Source,
        Status = s.StatusLabel,
        s.Activity,
        s.LastPrompt,
        s.IsStale,
        s.TimeText,
        s.Branch,
        s.LevelText,
        s.CritterName,
        s.HostSessionId,
        s.ClaudePid,
    }), new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
