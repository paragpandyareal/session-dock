using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace AgentDock;

public partial class App : Application
{
    Mutex? _singleInstance;
    EventServer? _server;
    TrayIcon? _tray;
    MainWindow? _window;
    SessionStore? _store;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Init();

        string? command = e.Args.FirstOrDefault(a => a is "--install" or "--uninstall" or "--install-hooks" or "--uninstall-hooks");
        if (command != null)
        {
            AttachConsole(-1);
            Console.WriteLine();
            int code;
            try
            {
                code = command switch
                {
                    "--install" => Setup.Install(Console.Out, launch: !e.Args.Contains("--no-launch"), out _),
                    "--uninstall" => Setup.Uninstall(Console.Out),
                    "--install-hooks" => HookInstaller.Install(Console.Out),
                    _ => HookInstaller.Uninstall(Console.Out),
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed: {ex.Message}");
                code = 1;
            }
            Shutdown(code);
            return;
        }

        // Double-clicking the downloaded exe offers to install it properly (once).
        if (OfferInstall())
        {
            Shutdown();
            return;
        }

        _singleInstance = new Mutex(true, SingleInstanceName, out bool isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write($"Unhandled: {args.Exception}");
            args.Handled = true;
        };

        HookInstaller.RefreshScript();
        var config = ConfigStore.LoadWithFreePort();
        _config = config;
        _store = new SessionStore(Dispatcher, config);
        int port = StartServer(config);

        _window = new MainWindow(_store, port, HookInstaller.IsInstalled());
        _window.ConnectRequested += () => ConnectHooks(askFirst: false);
        Setup.ListenForQuit(() => Dispatcher.BeginInvoke(ExitApp));
        var store = _store;
        _tray = new TrayIcon(TogglePanel, ExitApp, () => _window.IsVisible, OpenLastAlert,
        [
            new TrayToggle("Mini mode (critters only)", () => store.MiniMode, v => store.MiniMode = v),
            new TrayToggle("Compact cards", () => store.CompactMode, v => store.CompactMode = v),
            new TrayToggle("Calm mode (less motion)", () => store.CalmMode, v => store.CalmMode = v),
            new TrayToggle("Plain wording (for screen sharing)", () => store.PlainMode, v => store.PlainMode = v),
            // Only the installed copy owns the startup shortcut; a portable copy leaves it alone.
            .. (Setup.IsRunningFromInstallDir
                ? [new TrayToggle("Start with Windows", () => Setup.StartsWithWindows, v => Setup.StartsWithWindows = v)]
                : Array.Empty<TrayToggle>()),
        ]);
        _store.LongTaskFinished += (session, took) =>
        {
            _lastAlert = session;
            _tray.Notify($"{session.Project} is done", $"Finished after {(int)took.TotalMinutes}m {took.Seconds:00}s. {session.Activity}", warning: false);
        };
        _store.AttentionNeeded += (session, message) =>
        {
            _lastAlert = session;
            _tray.Notify($"{session.Project} needs you", message.Length > 0 ? message : "Claude Code is waiting for your input.");
        };
        _window.OpenFailed += session =>
            _tray.Notify("Couldn't open that session", $"No window found for {session.Project}. It may have been closed.");

        _window.Show();
        Log.Write($"Session Dock started, listening on 127.0.0.1:{port}");

        if (!HookInstaller.IsInstalled() && !config.SkipConnectOffer)
            Dispatcher.BeginInvoke(() => ConnectHooks(askFirst: true));
    }

    public const string SingleInstanceName = @"Local\AgentDock.SingleInstance";

    AppConfig? _config;

    /// <summary>
    /// When the release exe runs from somewhere other than its install folder (say, Downloads),
    /// offers a one-click install. Returns true if it installed and launched the installed copy.
    /// </summary>
    static bool OfferInstall()
    {
        if (!Setup.IsReleaseBuild || Setup.IsRunningFromInstallDir) return false;
        var config = ConfigStore.Load();
        if (config.SkipInstallOffer) return false;

        var answer = MessageBox.Show(
            "Install Session Dock?\n\n" +
            $"• Copies it to {Setup.InstallDir}\n" +
            "• Connects it to Claude Code (backs up ~/.claude/settings.json first)\n" +
            "• Adds it to the Start menu and starts it with Windows\n\n" +
            "No admin rights needed. Choose No to just run this copy.",
            "Session Dock", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
        {
            ConfigStore.Update(c => c.SkipInstallOffer = true);
            return false;
        }

        var output = new StringWriter();
        int code;
        bool launched = false;
        try
        {
            code = Setup.Install(output, launch: true, out launched);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed: {ex.Message}");
            code = 1;
        }
        // If only the hook step failed (code 1), the installed copy is running and offers to connect
        // itself, unless the user turned that offer off; every other failure needs a message here.
        if (code != 0 && (code != 1 || !launched || ConfigStore.Load().SkipConnectOffer))
            MessageBox.Show(output.ToString(), "Session Dock", MessageBoxButton.OK, MessageBoxImage.Warning);
        return launched;
    }

    void ConnectHooks(bool askFirst)
    {
        if (askFirst)
        {
            var answer = MessageBox.Show(
                "Connect Session Dock to Claude Code?\n\n" +
                "This adds Session Dock's hook to ~/.claude/settings.json, after saving a backup. " +
                "Nothing else in the file changes.",
                "Session Dock", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                if (_config != null) _config.SkipConnectOffer = true;
                ConfigStore.Update(c => c.SkipConnectOffer = true);
                return;
            }
        }

        var output = new StringWriter();
        int code;
        try
        {
            code = HookInstaller.Install(output);
        }
        catch (Exception ex)
        {
            output.WriteLine($"Failed: {ex.Message}");
            code = 1;
        }
        _window?.SetConnected(code == 0 && HookInstaller.IsInstalled());
        MessageBox.Show(
            code == 0 ? "Connected. Restart any Claude Code sessions that were already running so they show up." : output.ToString(),
            "Session Dock", MessageBoxButton.OK, code == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    int StartServer(AppConfig config)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _server = new EventServer(config.Port, config.Token,
                    ev => Dispatcher.BeginInvoke(() => _store!.Apply(ev)),
                    () => Dispatcher.Invoke(() => _store!.ToJson()));
                _server.Start();
                return config.Port;
            }
            catch (Exception ex) when (attempt < 3)
            {
                Log.Write($"Port {config.Port} unavailable ({ex.Message}), picking another");
                _server?.Dispose();
                config.Port = ConfigStore.FindFreePort();
                ConfigStore.Save(config);
            }
        }
    }

    void TogglePanel()
    {
        if (_window!.IsVisible) _window.HideDock();
        else ShowPanel();
    }

    void ShowPanel() => _window!.ShowDock();

    SessionInfo? _lastAlert;

    /// <summary>Clicking a "needs you" notification jumps straight to that session.</summary>
    void OpenLastAlert()
    {
        ShowPanel();
        if (_lastAlert != null) SessionFocus.Open(_lastAlert);
    }

    void ExitApp()
    {
        _window?.CloseDock();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _server?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int processId);
}
