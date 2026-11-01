using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace AgentDock;

/// <summary>Brings a session to the front: desktop sessions via deep link, CLI sessions by focusing their terminal.</summary>
public static class SessionFocus
{
    public static bool Open(SessionInfo session)
    {
        try
        {
            if (session.HostSessionId is { Length: > 0 } hostId && hostId.StartsWith("local_", StringComparison.Ordinal))
            {
                // hostId is validated against a strict local_<uuid> pattern before it is stored.
                Process.Start(new ProcessStartInfo($"claude://claude.ai/epitaxy/{Uri.EscapeDataString(hostId)}") { UseShellExecute = true });
                return true;
            }

            // Only follow a pid that is still a Claude Code process.
            if (session.ClaudePid > 0 && SessionStore.IsClaudeRunning(session.ClaudePid))
            {
                IntPtr terminal = ConsoleWindowOf(session.ClaudePid);
                // Windows Terminal hosts every tab in one window, so pick the session's tab first.
                if (terminal != IntPtr.Zero) SelectTerminalTab(session.ClaudePid, terminal);
                if (FocusWindow(terminal)) return true;
                foreach (int pid in Ancestors(session.ClaudePid))
                {
                    try
                    {
                        using var p = Process.GetProcessById(pid);
                        if (FocusWindow(p.MainWindowHandle)) return true;
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }

            if (session.Source == "Desktop")
            {
                var app = Process.GetProcessesByName("claude").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (app != null && FocusWindow(app.MainWindowHandle)) return true;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Could not open session {session.SessionId}: {ex.Message}");
        }
        return false;
    }

    /// <summary>The console window of a process. In Windows Terminal this is a pseudo window owned by the terminal window.</summary>
    static IntPtr ConsoleWindowOf(int pid)
    {
        IntPtr hwnd = IntPtr.Zero;
        FreeConsole();
        if (AttachConsole(pid))
        {
            hwnd = GetConsoleWindow();
            FreeConsole();
        }
        if (hwnd == IntPtr.Zero) return hwnd;
        IntPtr owner = GetAncestor(hwnd, GA_ROOTOWNER);
        return owner != IntPtr.Zero ? owner : hwnd;
    }

    /// <summary>
    /// Selects the terminal tab that hosts the given console process. Tabs only expose their titles
    /// (via UI Automation), so the console briefly gets a unique title to find its tab, then the
    /// original title is put back. Falls back to matching the current title when it is unique.
    /// </summary>
    static void SelectTerminalTab(int pid, IntPtr window)
    {
        string? original = ConsoleTitle(pid);
        if (original == null) return;
        try
        {
            var root = AutomationElement.FromHandle(window);
            AutomationElement? tab = null;
            // Claude Code animates its own title while working, so the marker can be overwritten; retry a few times.
            for (int attempt = 0; attempt < 3 && tab == null; attempt++)
            {
                string marker = "SessionDock-" + Guid.NewGuid().ToString("N")[..8];
                if (!SetConsoleTitleOf(pid, marker)) break;
                var timer = Stopwatch.StartNew();
                while (tab == null && timer.ElapsedMilliseconds < 400)
                {
                    tab = Tabs(root).FirstOrDefault(t => t.Current.Name.Contains(marker, StringComparison.Ordinal));
                    if (tab == null) Thread.Sleep(30);
                }
            }
            if (tab == null)
            {
                string wanted = WithoutSpinner(original);
                var matches = Tabs(root).Where(t => WithoutSpinner(t.Current.Name) == wanted).ToList();
                if (wanted.Length > 0 && matches.Count == 1) tab = matches[0];
            }
            if (tab != null && tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object pattern))
                ((SelectionItemPattern)pattern).Select();
        }
        catch (Exception ex)
        {
            Log.Write($"Could not select terminal tab: {ex.Message}");
        }
        finally
        {
            SetConsoleTitleOf(pid, original);
        }
    }

    static System.Collections.Generic.IEnumerable<AutomationElement> Tabs(AutomationElement root) =>
        root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>();

    /// <summary>Drops the leading status glyphs Claude Code animates in its title (e.g. "✳ ").</summary>
    static string WithoutSpinner(string title) => new string(title.SkipWhile(c => !char.IsLetterOrDigit(c)).ToArray()).Trim();

    static string? ConsoleTitle(int pid)
    {
        FreeConsole();
        if (!AttachConsole(pid)) return null;
        try
        {
            var buffer = new StringBuilder(1024);
            int length = GetConsoleTitle(buffer, buffer.Capacity);
            return buffer.ToString(0, Math.Min(length, buffer.Length));
        }
        finally
        {
            FreeConsole();
        }
    }

    static bool SetConsoleTitleOf(int pid, string title)
    {
        FreeConsole();
        if (!AttachConsole(pid)) return false;
        try
        {
            return SetConsoleTitle(title);
        }
        finally
        {
            FreeConsole();
        }
    }

    static bool FocusWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return false;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        else ShowWindow(hwnd, SW_SHOW);
        return SetForegroundWindow(hwnd);
    }

    static System.Collections.Generic.IEnumerable<int> Ancestors(int pid)
    {
        var parents = new System.Collections.Generic.Dictionary<int, int>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) yield break;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref entry))
                do parents[(int)entry.th32ProcessID] = (int)entry.th32ParentProcessID;
                while (Process32Next(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }

        int current = pid;
        for (int i = 0; i < 8 && parents.TryGetValue(current, out int parent) && parent > 4; i++)
        {
            yield return parent;
            current = parent;
        }
    }

    const uint GA_ROOTOWNER = 3;
    const int SW_RESTORE = 9, SW_SHOW = 5;
    const uint TH32CS_SNAPPROCESS = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);
    [DllImport("kernel32.dll")] static extern bool FreeConsole();
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetConsoleTitle(StringBuilder title, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool SetConsoleTitle(string title);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
