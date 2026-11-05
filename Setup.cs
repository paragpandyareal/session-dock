using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace AgentDock;

/// <summary>
/// Installs the app for the current user: copies the exe to %LOCALAPPDATA%\Programs\SessionDock,
/// connects the Claude Code hooks and adds Start menu and startup shortcuts. No admin rights and
/// no registry changes. <see cref="Uninstall"/> undoes all of it.
/// </summary>
public static class Setup
{
    public const string ExeName = "SessionDock.exe";

    /// <summary>Signalled to ask a running copy to quit cleanly (used before upgrading or uninstalling).</summary>
    const string QuitEventName = @"Local\AgentDock.Quit";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SessionDock");

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Session Dock.lnk");

    static string StartupShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Session Dock.lnk");

    static string CurrentExe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, ExeName);

    public static bool IsRunningFromInstallDir =>
        string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    /// <summary>True for the single-file release build, as opposed to a dev build from bin\.</summary>
#pragma warning disable IL3000 // Location being empty in a single-file app is exactly what this checks.
    public static bool IsReleaseBuild => string.IsNullOrEmpty(typeof(Setup).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>
    /// A startup shortcut (ours or hand-made) points at the installed exe. The tray only offers this
    /// toggle in the installed copy, so a portable copy never repoints or removes it.
    /// </summary>
    public static bool StartsWithWindows
    {
        get => StartupShortcutsToUs().Any();
        set
        {
            try
            {
                if (value) CreateShortcut(StartupShortcut, InstalledExe);
                else foreach (var lnk in StartupShortcutsToUs().ToList()) File.Delete(lnk);
            }
            catch (Exception ex)
            {
                Log.Write($"Could not change startup shortcut: {ex.Message}");
            }
        }
    }

    /// <summary>Exit code when the app could not be copied into place (hooks untouched).</summary>
    public const int CopyFailed = 2;

    /// <summary>Exit code when the app was installed but could not be started.</summary>
    public const int LaunchFailed = 3;

    /// <summary>
    /// Full install. Returns 0 on success, <see cref="CopyFailed"/> if the exe could not be put in place,
    /// or 1 if only connecting the hooks failed. Launches the installed copy when <paramref name="launch"/> is set.
    /// </summary>
    public static int Install(TextWriter output, bool launch, out bool launched)
    {
        launched = false;
        // An upgrade keeps the user's "Start with Windows" choice; a fresh install turns it on.
        bool upgrading = File.Exists(InstalledExe) || File.Exists(Path.Combine(InstallDir, "SessionDock.dll"));
        bool startWithWindows = !upgrading || StartupShortcutsToUs().Any();
        QuitRunningCopies();

        if (!IsRunningFromInstallDir)
        {
            try
            {
                Directory.CreateDirectory(InstallDir);
                CopyWithRetry(CurrentExe, InstalledExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                output.WriteLine($"Could not copy to {InstalledExe}: {ex.Message}");
                output.WriteLine("Quit Session Dock from the tray and try again.");
                return CopyFailed;
            }
            // A browser download carries Mark-of-the-Web; the installed copy shouldn't re-warn at every login.
            try { File.Delete(InstalledExe + ":Zone.Identifier"); } catch { }
            output.WriteLine($"Installed to {InstalledExe}");
            RemoveLegacyFiles();
        }

        try
        {
            CreateShortcut(StartMenuShortcut, InstalledExe);
            RemoveOtherStartupShortcuts();
            if (startWithWindows)
            {
                CreateShortcut(StartupShortcut, InstalledExe);
                output.WriteLine("Added to the Start menu and set to start with Windows.");
            }
            else
            {
                output.WriteLine("Added to the Start menu. Start with Windows stays off, as you had it.");
            }
        }
        catch (Exception ex)
        {
            output.WriteLine($"Could not create shortcuts ({ex.Message}). Session Dock still works; start it from {InstalledExe}.");
        }

        // The running dock was closed above, so whatever happens with the hooks, start it again.
        int hooks;
        try
        {
            hooks = HookInstaller.Install(output);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Could not connect to Claude Code: {ex.Message}");
            hooks = 1;
        }

        if (launch)
        {
            try
            {
                Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
                launched = true;
            }
            catch (Exception ex)
            {
                output.WriteLine($"Installed, but could not start it ({ex.Message}). Start it from the Start menu.");
                return LaunchFailed;
            }
        }
        return hooks;
    }

    /// <summary>Removes hooks, shortcuts, the installed app and its data folder.</summary>
    public static int Uninstall(TextWriter output)
    {
        QuitRunningCopies();
        int code = HookInstaller.Uninstall(output);

        try { if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut); }
        catch (Exception ex) { output.WriteLine($"Could not remove {StartMenuShortcut}: {ex.Message}"); }
        StartsWithWindows = false;
        output.WriteLine("Removed the Start menu and startup shortcuts.");

        if (code != 0)
        {
            // The hooks still point at the script in the data folder, so keep it until they're gone.
            output.WriteLine($"The hooks could not be removed, so {ConfigStore.Dir} was kept. Fix the problem above and run --uninstall again.");
            return code;
        }

        // This exe may be the one being deleted, so a short-lived cmd removes the folders once it exits.
        // Paths go through the environment so cmd never parses characters like & or % in them.
        // Retries for up to ~20 seconds in case this process is slow to exit.
        const string retry = "for /l %i in (1,1,10) do (ping 127.0.0.1 -n 3 >nul & if exist \"%SD_APP%\" rmdir /s /q \"%SD_APP%\" 2>nul & if exist \"%SD_DATA%\" rmdir /s /q \"%SD_DATA%\" 2>nul & if not exist \"%SD_APP%\" if not exist \"%SD_DATA%\" exit)";
        var cleanup = new ProcessStartInfo("cmd.exe", "/d /c " + retry)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        cleanup.Environment["SD_APP"] = InstallDir;
        cleanup.Environment["SD_DATA"] = ConfigStore.Dir;
        Process.Start(cleanup);
        output.WriteLine($"Deleting {InstallDir} and {ConfigStore.Dir}. Session Dock is uninstalled.");
        output.WriteLine($"Backups of your Claude Code settings stay in {Path.GetDirectoryName(HookInstaller.SettingsPath)} (settings.json.agentdock-backup-*).");
        return code;
    }

    /// <summary>Lets a running copy listen for <see cref="QuitRunningCopy"/>.</summary>
    public static void ListenForQuit(Action quit)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var thread = new Thread(() =>
        {
            signal.WaitOne();
            quit();
        }) { IsBackground = true, Name = "QuitListener" };
        thread.Start();
    }

    /// <summary>
    /// Asks every other running Session Dock to exit, so its exe can be replaced or deleted and the
    /// newly launched copy gets the single-instance lock. 1.1.x copies don't know the quit signal,
    /// so anything still running after a few seconds is closed the hard way.
    /// </summary>
    static void QuitRunningCopies()
    {
        var others = OtherCopies();
        if (others.Count == 0) return;

        if (EventWaitHandle.TryOpenExisting(QuitEventName, out var signal))
            using (signal) signal.Set();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        foreach (var p in others)
        {
            using (p)
            {
                try
                {
                    int wait = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (p.WaitForExit(wait)) continue;
                    Log.Write($"Session Dock (pid {p.Id}) did not quit when asked; closing it");
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex)
                {
                    Log.Write($"Could not stop pid {p.Id}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>Other running Session Dock processes, any version, identified by the exe's product name.</summary>
    static System.Collections.Generic.List<Process> OtherCopies()
    {
        var result = new System.Collections.Generic.List<Process>();
        int self = Environment.ProcessId;
        // 1.0 shipped as AgentDock.exe ("Sessiondex"); it uses the same single-instance lock.
        foreach (var p in Process.GetProcessesByName("SessionDock").Concat(Process.GetProcessesByName("AgentDock")))
        {
            bool ours = false;
            try
            {
                string? path = p.MainModule?.FileName;
                string? product = path == null ? null : FileVersionInfo.GetVersionInfo(path).ProductName;
                ours = p.Id != self && product is "Session Dock" or "Sessiondex";
            }
            catch
            {
                // Can't inspect it (exited, or another user's): leave it alone.
            }
            if (ours) result.Add(p);
            else p.Dispose();
        }
        return result;
    }

    /// <summary>
    /// Removes startup shortcuts made by hand for older versions (the 1.x READMEs said to), whether
    /// they point at the installed exe or at a 1.0 "Sessiondex" AgentDock.exe, so the app doesn't start twice.
    /// </summary>
    static void RemoveOtherStartupShortcuts()
    {
        foreach (var (lnk, target) in StartupShortcuts())
        {
            if (string.Equals(lnk, StartupShortcut, StringComparison.OrdinalIgnoreCase)) continue;
            if (PathEquals(target, InstalledExe) || IsSessiondex(target))
                try { File.Delete(lnk); } catch (Exception ex) { Log.Write($"Could not remove {lnk}: {ex.Message}"); }
        }
    }

    static bool IsSessiondex(string exe)
    {
        try { return File.Exists(exe) && FileVersionInfo.GetVersionInfo(exe).ProductName == "Sessiondex"; }
        catch { return false; }
    }

    /// <summary>Startup shortcuts that point at the installed exe.</summary>
    static System.Collections.Generic.IEnumerable<string> StartupShortcutsToUs() =>
        StartupShortcuts().Where(s => PathEquals(s.Target, InstalledExe)).Select(s => s.Lnk);

    static System.Collections.Generic.IEnumerable<(string Lnk, string Target)> StartupShortcuts()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (!Directory.Exists(dir)) yield break;
        foreach (var lnk in Directory.GetFiles(dir, "*.lnk"))
        {
            string? target = null;
            try { target = ShortcutTarget(lnk); } catch { }
            if (!string.IsNullOrEmpty(target)) yield return (lnk, target);
        }
    }

    static bool PathEquals(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    /// <summary>Releases up to 1.1.x were a folder of files; the single exe replaces all of them.</summary>
    static void RemoveLegacyFiles()
    {
        foreach (var name in new[] { "SessionDock.dll", "SessionDock.deps.json", "SessionDock.runtimeconfig.json", "SessionDock.pdb" })
        {
            try { File.Delete(Path.Combine(InstallDir, name)); }
            catch (Exception ex) { Log.Write($"Could not remove old {name}: {ex.Message}"); }
        }
        try
        {
            string hookDir = Path.Combine(InstallDir, "hook");
            if (Directory.Exists(hookDir)) Directory.Delete(hookDir, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not remove old hook folder: {ex.Message}");
        }
    }

    static void CopyWithRetry(string source, string target)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(300);
            }
        }
    }

    static void CreateShortcut(string path, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WithShortcut(path, link =>
        {
            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Session Dock: see every Claude Code session at a glance";
            link.Save();
            return 0;
        });
    }

    static string? ShortcutTarget(string path) => WithShortcut(path, link => (string?)link.TargetPath);

    /// <summary>Opens (or starts) a .lnk through WScript.Shell and releases the COM objects afterwards.</summary>
    static T WithShortcut<T>(string path, Func<dynamic, T> use)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic? link = null;
        try
        {
            link = shell.CreateShortcut(path);
            return use(link);
        }
        finally
        {
            if (link != null) System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
        }
    }
}
