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

        if (e.Args.Any(a => a is "--install-hooks" or "--uninstall-hooks"))
        {
            AttachConsole(-1);
            Console.WriteLine();
            int code;
            try
            {
                code = e.Args.Contains("--install-hooks")
                    ? HookInstaller.Install(Console.Out)
                    : HookInstaller.Uninstall(Console.Out);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed: {ex.Message}");
                code = 1;
            }
            Shutdown(code);
            return;
        }

        _singleInstance = new Mutex(true, @"Local\AgentDock.SingleInstance", out bool isFirst);
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
        _store = new SessionStore(Dispatcher, config);
        int port = StartServer(config);

        _window = new MainWindow(_store, port, HookInstaller.IsInstalled());
        var store = _store;
        _tray = new TrayIcon(TogglePanel, ExitApp, () => _window.IsVisible, OpenLastAlert,
        [
            new TrayToggle("Mini mode (critters only)", () => store.MiniMode, v => store.MiniMode = v),
            new TrayToggle("Compact cards", () => store.CompactMode, v => store.CompactMode = v),
            new TrayToggle("Calm mode (less motion)", () => store.CalmMode, v => store.CalmMode = v),
            new TrayToggle("Plain wording (for screen sharing)", () => store.PlainMode, v => store.PlainMode = v),
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
        Log.Write($"Sessiondex started, listening on 127.0.0.1:{port}");
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
