using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AgentDock;

/// <summary>
/// Docks a window to the right edge of the primary monitor as a Windows AppBar,
/// so maximised windows and the work area shrink to make room for it.
/// Works in physical pixels; the process is per-monitor DPI aware (see app.manifest).
/// </summary>
internal sealed class AppBar
{
    const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_ACTIVATE = 6, ABM_WINDOWPOSCHANGED = 9;
    const int ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2;
    const uint ABE_RIGHT = 2;
    const int WM_ACTIVATE = 0x0006, WM_WINDOWPOSCHANGED = 0x0047, WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint MONITOR_DEFAULTTOPRIMARY = 1;

    readonly Window _window;
    double _widthDip;
    IntPtr _hwnd;
    uint _callbackMessage;
    bool _registered;

    public AppBar(Window window, double widthDip)
    {
        _window = window;
        _widthDip = widthDip;
    }

    /// <summary>Call from OnSourceInitialized.</summary>
    public void Attach()
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _callbackMessage = RegisterWindowMessage("AgentDock.AppBarMessage");
        HwndSource.FromHwnd(_hwnd)!.AddHook(WndProc);
    }

    public void Register()
    {
        if (_registered || _hwnd == IntPtr.Zero) return;
        var abd = NewData();
        abd.uCallbackMessage = _callbackMessage;
        SHAppBarMessage(ABM_NEW, ref abd);
        _registered = true;
        SetPosition();
    }

    /// <summary>Changes the docked width; other windows resize to match.</summary>
    public void SetWidth(double widthDip)
    {
        if (Math.Abs(widthDip - _widthDip) < 0.5) return;
        _widthDip = widthDip;
        SetPosition();
    }

    public void Unregister()
    {
        if (!_registered) return;
        var abd = NewData();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }

    void SetPosition()
    {
        if (!_registered) return;

        IntPtr monitor = MonitorFromPoint(new POINT(), MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return;
        uint dpi = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX : 96;
        int width = (int)Math.Round(_widthDip * dpi / 96.0);

        var abd = NewData();
        abd.uEdge = ABE_RIGHT;
        abd.rc = new RECT
        {
            left = info.rcMonitor.right - width,
            top = info.rcMonitor.top,
            right = info.rcMonitor.right,
            bottom = info.rcMonitor.bottom,
        };
        SHAppBarMessage(ABM_QUERYPOS, ref abd);
        abd.rc.left = abd.rc.right - width;
        SHAppBarMessage(ABM_SETPOS, ref abd);

        SetWindowPos(_hwnd, IntPtr.Zero, abd.rc.left, abd.rc.top,
            abd.rc.right - abd.rc.left, abd.rc.bottom - abd.rc.top, SWP_NOACTIVATE);
    }

    void SetPositionLater() =>
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(SetPosition));

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_registered) return IntPtr.Zero;

        if (msg == (int)_callbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case ABN_POSCHANGED:
                    SetPositionLater();
                    break;
                case ABN_FULLSCREENAPP:
                    // Step out of the way of full-screen apps (games, videos, presentations).
                    _window.Topmost = lParam == IntPtr.Zero;
                    break;
            }
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_ACTIVATE:
            {
                var abd = NewData();
                SHAppBarMessage(ABM_ACTIVATE, ref abd);
                break;
            }
            case WM_WINDOWPOSCHANGED:
            {
                var abd = NewData();
                SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd);
                break;
            }
            case WM_DISPLAYCHANGE:
            case WM_DPICHANGED:
                SetPositionLater();
                break;
        }
        return IntPtr.Zero;
    }

    APPBARDATA NewData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    [StructLayout(LayoutKind.Sequential)]
    struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
