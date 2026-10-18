using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AgentDock;

public enum AgentStatus { Working, Waiting, Finished, Idle, Ended }

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>One Claude Code session as shown on a card.</summary>
public sealed class SessionInfo : ObservableObject
{
    public SessionInfo(string sessionId, DateTime now, double hue)
    {
        SessionId = sessionId;
        StartedAt = now;
        LastUpdate = now;
        var critter = Critter.Create(sessionId, hue);
        CritterImage = critter.ToImage();
        CritterName = critter.Name;
    }

    /// <summary>When true, text appears instantly instead of typing out (calm mode).</summary>
    public static bool InstantText { get; set; }

    public string SessionId { get; }
    public System.Windows.Media.ImageSource CritterImage { get; }
    public string CritterName { get; }
    public string? HostSessionId { get; set; }
    public DateTime? TurnStartedAt { get; set; }
    public DateTime? WaitingSince { get; set; }

    int _toolCount;
    public int ToolCount
    {
        get => _toolCount;
        set
        {
            int before = Level;
            if (!Set(ref _toolCount, value)) return;
            if (Level != before) OnPropertyChanged(nameof(LevelText));
        }
    }
    int Level => Math.Min(99, 1 + _toolCount / 2);
    public string LevelText => $"Lv{Level}";

    string _branch = "";
    public string Branch
    {
        get => _branch;
        set { if (Set(ref _branch, value)) OnPropertyChanged(nameof(HasBranch)); }
    }
    public bool HasBranch => _branch.Length > 0;

    string _activityDetail = "";
    /// <summary>Raw command or path behind the activity, shown only on hover.</summary>
    public string ActivityDetail { get => _activityDetail; set => Set(ref _activityDetail, value); }

    string _battleText = "";
    public string BattleText
    {
        get => _battleText;
        set
        {
            if (!Set(ref _battleText, value)) return;
            TypeOut(value);
        }
    }

    string _typedBattleText = "";
    /// <summary>BattleText revealed letter by letter, like a game text box.</summary>
    public string TypedBattleText { get => _typedBattleText; private set => Set(ref _typedBattleText, value); }

    System.Windows.Threading.DispatcherTimer? _typer;

    void TypeOut(string text)
    {
        _typer?.Stop();
        if (InstantText)
        {
            TypedBattleText = text;
            return;
        }
        int shown = 0;
        TypedBattleText = "";
        _typer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(28) };
        _typer.Tick += (_, _) =>
        {
            shown = Math.Min(text.Length, shown + 1);
            TypedBattleText = text[..shown];
            if (shown >= text.Length) _typer!.Stop();
        };
        _typer.Start();
    }

    double _hp = 1;
    /// <summary>1 right after an update, draining to 0 when the session goes stale.</summary>
    public double Hp
    {
        get => _hp;
        private set
        {
            if (!Set(ref _hp, value)) return;
            HpLevel = value > 0.5 ? "High" : value > 0.2 ? "Mid" : "Low";
        }
    }

    string _hpLevel = "High";
    public string HpLevel { get => _hpLevel; private set => Set(ref _hpLevel, value); }

    // Bookkeeping used by SessionStore.
    public long LastEventTs { get; set; }
    public int ClaudePid { get; set; }
    public string? TranscriptPath { get; set; }
    public bool SourceFromTranscript { get; set; }
    public int TranscriptAttempts { get; set; }
    public bool StartTimeKnown { get; set; }
    public DateTime? EndedAt { get; set; }
    public string LastToolActivity { get; set; } = "";

    string _project = "Unknown project";
    public string Project { get => _project; set => Set(ref _project, value); }

    string _cwd = "";
    public string Cwd { get => _cwd; set => Set(ref _cwd, value); }

    string _source = "";
    public string Source
    {
        get => _source;
        set { if (Set(ref _source, value)) OnPropertyChanged(nameof(HasSource)); }
    }
    public bool HasSource => _source.Length > 0;

    AgentStatus _status = AgentStatus.Idle;
    public AgentStatus Status
    {
        get => _status;
        set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusChip));
            OnPropertyChanged(nameof(IsWaiting));
        }
    }

    public string StatusLabel => _status switch
    {
        AgentStatus.Working => "Working",
        AgentStatus.Waiting => "Waiting for you",
        AgentStatus.Finished => "Finished",
        AgentStatus.Idle => "Idle",
        _ => "Ended",
    };

    public string StatusChip => StatusLabel.ToUpperInvariant();

    public bool IsWaiting => _status == AgentStatus.Waiting;

    string _activity = "";
    public string Activity { get => _activity; set => Set(ref _activity, value); }

    string _lastPrompt = "";
    public string LastPrompt
    {
        get => _lastPrompt;
        set { if (Set(ref _lastPrompt, value)) OnPropertyChanged(nameof(HasPrompt)); }
    }
    public bool HasPrompt => _lastPrompt.Length > 0;

    bool _isStale;
    public bool IsStale { get => _isStale; set => Set(ref _isStale, value); }

    DateTime _startedAt;
    public DateTime StartedAt { get => _startedAt; set => Set(ref _startedAt, value); }

    DateTime _lastUpdate;
    public DateTime LastUpdate { get => _lastUpdate; set => Set(ref _lastUpdate, value); }

    string _runningText = "";
    public string RunningText { get => _runningText; private set => Set(ref _runningText, value); }

    string _updatedText = "";
    public string UpdatedText { get => _updatedText; private set => Set(ref _updatedText, value); }

    string _timeText = "";
    /// <summary>"Started 26m ago · Active 9s ago" (or "Waiting 2m" / "Ended 10s ago").</summary>
    public string TimeText { get => _timeText; private set => Set(ref _timeText, value); }

    public void RefreshTimes(DateTime now)
    {
        var end = EndedAt ?? now;
        RunningText = $"Running {FormatDuration(end - StartedAt)}";
        UpdatedText = $"Updated {FormatAgo(now - LastUpdate)}";
        string second = EndedAt is DateTime ended ? $"Ended {FormatAgo(now - ended)}"
            : Status == AgentStatus.Waiting && WaitingSince is DateTime w ? $"Waiting {FormatDuration(now - w)}"
            : $"Active {FormatAgo(now - LastUpdate)}";
        TimeText = $"Started {FormatAgo(now - StartedAt)} · {second}";
        Hp = EndedAt != null ? 0 : Math.Clamp(1 - (now - LastUpdate).TotalMinutes / 10.0, 0, 1);
    }

    static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{(int)t.TotalDays}d {t.Hours}h";
    }

    static string FormatAgo(TimeSpan t)
    {
        if (t.TotalSeconds < 5) return "just now";
        if (t.TotalMinutes < 1) return $"{(int)t.TotalSeconds}s ago";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m ago";
        if (t.TotalDays < 1) return $"{(int)t.TotalHours}h ago";
        return $"{(int)t.TotalDays}d ago";
    }
}
