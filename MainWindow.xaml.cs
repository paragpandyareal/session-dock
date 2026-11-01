using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AgentDock;

public partial class MainWindow : Window
{
    const double PanelWidth = 320;
    const double MiniWidth = 64;

    readonly SessionStore _store;
    readonly AppBar _appBar;
    bool _closingForReal;

    public MainWindow(SessionStore store, int port, bool hooksInstalled)
    {
        InitializeComponent();
        _store = store;
        DataContext = store;
        _appBar = new AppBar(this, store.MiniMode ? MiniWidth : PanelWidth);
        Width = store.MiniMode ? MiniWidth : PanelWidth;
        ApplyMiniMode();
        store.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionStore.MiniMode)) ApplyMiniMode();
        };

        LogoImage.Source = Critter.Create("agent-dock").ToImage();
        EmptyImage.Source = Critter.Create("sleepy").ToImage();
        // Only surface technical details when something needs fixing.
        PortText.Text = hooksInstalled ? "Connected to Claude Code" : "Not connected: hooks not installed";
        PortText.ToolTip = hooksInstalled
            ? $"Receiving hook events on 127.0.0.1:{port}\nHooks registered in {HookInstaller.SettingsPath}"
            : "Run: SessionDock.exe --install-hooks";
        if (!hooksInstalled) StatusDot.Fill = (System.Windows.Media.Brush)FindResource("WaitingBrush");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: keeps the panel out of Alt+Tab.
        SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW);
        _appBar.Attach();
        _appBar.Register();
    }

    public void ShowDock()
    {
        Show();
        _appBar.Register();
    }

    public void HideDock()
    {
        _appBar.Unregister();
        Hide();
    }

    public void CloseDock()
    {
        _closingForReal = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 hides to the tray; Exit from the tray menu really closes.
        if (!_closingForReal)
        {
            e.Cancel = true;
            HideDock();
            return;
        }
        _appBar.Unregister();
        base.OnClosing(e);
    }

    void Hide_Click(object sender, RoutedEventArgs e) => HideDock();

    void Collapse_Click(object sender, RoutedEventArgs e) => _store.MiniMode = true;

    void Expand_Click(object sender, RoutedEventArgs e) => _store.MiniMode = false;

    /// <summary>Swaps between the full panel and the slim critter strip, resizing the docked area.</summary>
    void ApplyMiniMode()
    {
        bool mini = _store.MiniMode;
        MiniView.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        FullView.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        _appBar.SetWidth(mini ? MiniWidth : PanelWidth);
    }

    /// <summary>New activity text fades and slides up into place.</summary>
    void ActivityText_TargetUpdated(object? sender, System.Windows.Data.DataTransferEventArgs e)
    {
        if (sender is not FrameworkElement text || _store.CalmMode) return;
        try
        {
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            text.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(300)));
            var move = Thawed<System.Windows.Media.TranslateTransform>(text);
            move?.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(5, 0, TimeSpan.FromMilliseconds(350)) { EasingFunction = ease });
        }
        catch (Exception ex)
        {
            Log.Write($"Activity animation failed: {ex.Message}");
        }
    }

    /// <summary>Transforms declared in a template are frozen; swap in an animatable copy.</summary>
    static T? Thawed<T>(UIElement element) where T : System.Windows.Media.Transform
    {
        if (element.RenderTransform is not T transform) return null;
        if (!transform.IsFrozen) return transform;
        var copy = (T)transform.Clone();
        element.RenderTransform = copy;
        return copy;
    }

    /// <summary>Level up: the label springs and "LV UP!" floats away. Skipped for the first value.</summary>
    void LevelLabel_TargetUpdated(object? sender, System.Windows.Data.DataTransferEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBlock label) return;
        string? previous = label.Tag as string;
        label.Tag = label.Text;
        if (previous == null || previous == label.Text || _store.CalmMode) return;
        try
        {
            PlayLevelUp(label);
        }
        catch (Exception ex)
        {
            Log.Write($"Level-up animation failed: {ex.Message}");
        }
    }

    static void PlayLevelUp(System.Windows.Controls.TextBlock label)
    {
        var spring = new System.Windows.Media.Animation.ElasticEase { Oscillations = 2, Springiness = 4 };
        if (Thawed<System.Windows.Media.ScaleTransform>(label) is { } scale)
        {
            var pop = new System.Windows.Media.Animation.DoubleAnimation(1.6, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = spring };
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, pop);
        }
        if (label.Parent is System.Windows.Controls.Panel panel &&
            panel.Children.OfType<System.Windows.Controls.TextBlock>().FirstOrDefault(t => t.Name == "LevelUp") is { } burst)
        {
            var fade = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.Zero));
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(600)));
            fade.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(0, TimeSpan.FromMilliseconds(1100)));
            burst.BeginAnimation(OpacityProperty, fade);
            if (Thawed<System.Windows.Media.TranslateTransform>(burst) is { } rise)
                rise.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                    new System.Windows.Media.Animation.DoubleAnimation(-4, -26, TimeSpan.FromMilliseconds(1100))
                    { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
        }
    }

    /// <summary>Same display options as the tray menu, opened from the footer.</summary>
    void Options_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Top };
        menu.Items.Add(Option("Compact cards", _store.CompactMode, v => _store.CompactMode = v));
        menu.Items.Add(Option("Calm mode (less motion)", _store.CalmMode, v => _store.CalmMode = v));
        menu.Items.Add(Option("Plain wording (for screen sharing)", _store.PlainMode, v => _store.PlainMode = v));
        menu.IsOpen = true;
    }

    static System.Windows.Controls.MenuItem Option(string label, bool on, Action<bool> set)
    {
        var item = new System.Windows.Controls.MenuItem { Header = label, IsCheckable = true, IsChecked = on };
        item.Click += (_, _) => set(item.IsChecked);
        return item;
    }

    /// <summary>Raised when a session can't be brought to the front.</summary>
    public event Action<SessionInfo>? OpenFailed;

    void Card_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionInfo session } && !SessionFocus.Open(session))
            OpenFailed?.Invoke(session);
    }

    void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SessionInfo session })
            _store.Remove(session);
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
