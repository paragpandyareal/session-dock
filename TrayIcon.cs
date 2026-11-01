using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AgentDock;

/// <summary>A checkable tray menu option.</summary>
internal sealed record TrayToggle(string Label, Func<bool> Get, Action<bool> Set);

/// <summary>Notification-area icon with Show/Hide and Exit, plus Windows notifications.</summary>
internal sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon _icon;
    readonly Icon _image;

    public TrayIcon(Action toggle, Action exit, Func<bool> isPanelVisible, Action showPanel, TrayToggle[] options)
    {
        var toggleItem = new ToolStripMenuItem("Hide panel", null, (_, _) => toggle());
        var menu = new ContextMenuStrip();
        menu.Items.Add(toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        foreach (var option in options)
        {
            var item = new ToolStripMenuItem(option.Label) { CheckOnClick = true };
            item.Click += (_, _) => option.Set(item.Checked);
            menu.Opening += (_, _) => item.Checked = option.Get();
            menu.Items.Add(item);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit Session Dock", null, (_, _) => exit()));
        menu.Opening += (_, _) => toggleItem.Text = isPanelVisible() ? "Hide panel" : "Show panel";

        _image = CreateIcon();
        _icon = new NotifyIcon
        {
            Icon = _image,
            Text = "Session Dock",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) toggle(); };
        _icon.BalloonTipClicked += (_, _) => showPanel();
    }

    /// <summary>Shows a Windows notification (a toast on Windows 10/11).</summary>
    public void Notify(string title, string text, bool warning = true) =>
        _icon.ShowBalloonTip(8000, title, string.IsNullOrWhiteSpace(text) ? " " : text,
            warning ? ToolTipIcon.Warning : ToolTipIcon.Info);

    /// <summary>The dock's mascot critter on a round badge.</summary>
    static Icon CreateIcon()
    {
        var critter = Critter.Create("agent-dock");
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var badge = new SolidBrush(Color.FromArgb(43, 52, 102));
            g.FillEllipse(badge, 0, 0, 32, 32);
            g.SmoothingMode = SmoothingMode.None;
            const int scale = 2, offset = 4;
            for (int y = 0; y < Critter.Size; y++)
            for (int x = 0; x < Critter.Size; x++)
            {
                if (critter.Pixels[x, y] is not { } c) continue;
                using var px = new SolidBrush(Color.FromArgb(c.R, c.G, c.B));
                g.FillRectangle(px, offset + x * scale, offset + y * scale, scale, scale);
            }
        }
        IntPtr handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
