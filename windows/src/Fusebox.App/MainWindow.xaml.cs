using System.Runtime.InteropServices;
using Fusebox.App.Pages;
using Fusebox.App.Settings;
using Fusebox.Core;
using Fusebox.Core.Look;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using static Fusebox.Core.Strings;

namespace Fusebox.App;

/// <summary>
/// The main window (FEATURES.md §2): tabs in the title bar (Live · Processes · Checkup · Network · Docked + gear → Settings window),
/// Ctrl+1…5 / Ctrl+, , two remembered sizes (liveSize, windowSize) with per-tab minimums, placement and fit, launch fade,
/// close → hide to the tray (keepRunning). Pages live in <see cref="Host"/>; <see cref="Open"/> is the one way to switch.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>
    /// A top-level tab. Tag = what <see cref="Open"/> takes; Id = the Mac page id stored in pref "page"; Min = §2.2 "grows to at least".
    /// Page agents: swap <c>Page</c> for your page type.
    /// </summary>
    public sealed record Tab(string Tag, string Id, string Title, string Glyph, Type Page, double MinWidth, double MinHeight);

    /// <summary>Tab order = Ctrl+1…5. Titles: Live/Checkup/Docked are never translated.</summary>
    public static readonly Tab[] Tabs =
    [
        new("Live", "live", "Live", "\uEC4A", typeof(LivePage), 0, 0),
        new("Processes", "programs", "Processes", "\uE71D", typeof(ProcessesPage), 460, 420),
        new("Checkup", "advice", "Checkup", "\uE9D2", typeof(CheckupPage), 420, 460),
        new("Network", "network", "Network", "\uE968", typeof(NetworkPage), 470, 440),
        new("Docked", "ports", "Docked", "\uE772", typeof(DockedPage), 420, 460),
    ];

    const double DefaultW = 300, DefaultH = 340, NamesFrom = 740, IconsBelow = 700;
    static MainWindow? instance;
    readonly nint hwnd;
    readonly List<Button> buttons = [];
    Button? gear;
    Tab current = Tabs[0];
    bool wide, resizing;

    public MainWindow()
    {
        InitializeComponent();
        instance = this;
        hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Fusebox.ico"));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        if (AppWindow.Presenter is OverlappedPresenter p) p.IsMaximizable = false;   // 2 caption buttons: 6 tabs fit in 316 DIPs (Mac minimum)
        unsafe { SettingsNative.SetWindowSubclass(hwnd, &WndProc, 2, 0); }
        BuildTabs();
        Keys();
        ThemeService.Attach(this);
        ThemeService.ThemeChanged += () => UpdateDrop(false);
        LowImpact.Changed += UpdateGearTip;
        Prefs.Changed += k => { if (k is "onTop" or "dockIcon" or "menubarStyle") DispatcherQueue.TryEnqueue(ApplyWindowPrefs); };
        ApplyWindowPrefs();
        Root.SizeChanged += (_, e) => { UpdateWide(e.NewSize.Width); UpdateCaption(); };
        Host.Navigated += OnNavigated;
        AppWindow.Closing += OnClosing;
        AppWindow.Changed += (_, e) => { if ((e.DidVisibilityChange || e.DidPresenterChange) && IsOnScreen) App.Live.Refresh(); };
        PlaceAtLaunch();
        Open("Live");
        FadeIn();
    }

    /// <summary>Visible and not minimized. App.Live.Watched combines this with the tray flyout.</summary>
    public bool IsOnScreen => AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    /// <summary>Page type now in the host (smoke test report).</summary>
    public string CurrentPage => Host.CurrentSourcePageType?.Name ?? "none";

    /// <summary>Tag of the selected tab.</summary>
    public string CurrentTab => current.Tag;

    /// <summary>
    /// Shows a tab by tag (Live, Processes, Checkup, Network, Docked; case-insensitive; Mac ids live/programs/advice/network/ports too)
    /// or "Settings" (opens the Settings window).
    /// <paramref name="parameter"/> is passed to the page's OnNavigatedTo (e.g. an app key for Processes). A sub-page (detail,
    /// app page) under the same tab goes back to the tab's page.
    /// </summary>
    public void Open(string tag, object? parameter = null)
    {
        if (tag.Equals("Settings", StringComparison.OrdinalIgnoreCase)) { SettingsWindow.Open(); return; }
        var tab = Tabs.FirstOrDefault(t => t.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase) || t.Id == tag) ?? Tabs[0];
        bool switched = tab != current;
        current = tab;
        if (Host.CurrentSourcePageType != tab.Page || parameter != null)
        {
            Host.Navigate(tab.Page, parameter, new SuppressNavigationTransitionInfo());
            Host.BackStack.Clear();
        }
        Prefs.Set("page", tab.Id);
        for (int i = 0; i < buttons.Count; i++) Select(buttons[i], Tabs[i] == tab);
        UpdateDrop(switched && ThemeService.Motion);
        if (switched) ResizeFor(tab);
    }

    // ---- tab row ----

    void BuildTabs()
    {
        for (int i = 0; i < Tabs.Length; i++)
        {
            var t = Tabs[i];
            var b = TabButton(t.Glyph, TitleOf(t));
            ToolTipService.SetToolTip(b, $"{TitleOf(t)}  Ctrl+{i + 1}");
            b.Click += (_, _) => Open(t.Tag);
            buttons.Add(b);
            TabStrip.Children.Add(b);
        }
        gear = TabButton("\uE713", L("Settings"));
        gear.Click += (_, _) => SettingsWindow.Open();
        TabStrip.Children.Add(gear);
        UpdateGearTip();
        TabStrip.SizeChanged += (_, _) => UpdateDrop(false);
    }

    /// <summary>Tab title in the UI language; Live, Checkup and Docked are names and never translated.</summary>
    public static string TitleOf(Tab t) => t.Tag switch { "Processes" => L("Processes"), "Network" => L("Network"), _ => t.Title };

    static Button TabButton(string glyph, string title)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
        var text = new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.Medium, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(icon);
        content.Children.Add(text);
        var b = new Button
        {
            Content = content, MinWidth = 32, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(8),
            Background = null, BorderThickness = new Thickness(0), Foreground = ThemeService.Brush("FuseboxSecondary"),
        };
        AutomationName(b, title);
        // Lightweight styling (set before the template applies): hover = secondary @ 12 %, text on hover = text color.
        var r = b.Resources;
        r["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        r["ButtonBackgroundPointerOver"] = ThemeService.Brush("FuseboxHover");
        r["ButtonBackgroundPressed"] = ThemeService.Brush("FuseboxHover");
        r["ButtonBorderBrush"] = r["ButtonBorderBrushPointerOver"] = r["ButtonBorderBrushPressed"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        r["ButtonForegroundPointerOver"] = ThemeService.Brush("FuseboxText");
        r["ButtonForegroundPressed"] = ThemeService.Brush("FuseboxText");
        return b;
    }

    static void AutomationName(UIElement e, string name) => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(e, name);

    static void Select(Button b, bool on) => b.Foreground = ThemeService.Brush(on ? "FuseboxAccent" : "FuseboxSecondary");

    void UpdateGearTip()
    {
        if (gear == null) return;
        ToolTipService.SetToolTip(gear, LowImpact.On ? L("Settings  Ctrl+,  ·  Low impact is on") : L("Settings  Ctrl+,"));
    }

    /// <summary>Names from 740 DIPs wide, icons again below 700 (no flicker while dragging).</summary>
    void UpdateWide(double width)
    {
        bool w = wide ? width >= IconsBelow : width >= NamesFrom;
        if (w == wide && buttons.Count > 0 && buttons[0].Padding.Left == (wide ? 8 : 0)) return;
        wide = w;
        foreach (var b in buttons.Append(gear!))
        {
            b.Padding = new Thickness(wide ? 8 : 0, 0, wide ? 8 : 0, 0);
            if (b.Content is StackPanel sp && sp.Children[1] is TextBlock t) t.Visibility = wide ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Moves the accent drop behind the selected tab; slides (one implicit transition, no per-frame code) unless Low impact.</summary>
    void UpdateDrop(bool animate)
    {
        int i = Array.IndexOf(Tabs, current);
        if (i < 0 || i >= buttons.Count) return;
        var b = buttons[i];
        if (b.ActualWidth <= 0) return;   // not laid out yet: TabStrip.SizeChanged calls again
        var at = b.TransformToVisual(TabHost).TransformPoint(new Windows.Foundation.Point(0, 0));
        Drop.TranslationTransition = animate ? new Vector3Transition { Duration = TimeSpan.FromMilliseconds(300) } : null;
        Drop.Width = b.ActualWidth;
        Drop.Height = b.ActualHeight;
        Drop.Translation = new System.Numerics.Vector3((float)at.X, (float)at.Y, 0);
    }

    void UpdateCaption()
    {
        double scale = Win32.Scale(hwnd);
        double inset = AppWindow.TitleBar.RightInset / scale;
        if (inset > 0 && Math.Abs(CaptionColumn.Width.Value - inset) > 0.5) CaptionColumn.Width = new GridLength(inset);
    }

    void Keys()
    {
        for (int i = 0; i < Tabs.Length; i++)
        {
            var tag = Tabs[i].Tag;
            Accel((VirtualKey)((int)VirtualKey.Number1 + i), () => Open(tag));
            Accel((VirtualKey)((int)VirtualKey.NumberPad1 + i), () => Open(tag));
        }
        Accel((VirtualKey)188 /* VK_OEM_COMMA */, SettingsWindow.Open);
    }

    void Accel(VirtualKey key, Action run)
    {
        var a = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
        a.Invoked += (_, e) => { e.Handled = true; run(); };
        Root.KeyboardAccelerators.Add(a);
        Root.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
    }

    // ---- page transition (§2.4): new page fades in and slides up 10 DIPs in 0.22 s; nothing in Low impact ----

    void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.Content is not UIElement page || !ThemeService.Motion) return;
        var move = new TranslateTransform { Y = 10 };
        page.RenderTransform = move;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var sb = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease };
        Storyboard.SetTarget(fade, page); Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation { From = 10, To = 0, Duration = TimeSpan.FromMilliseconds(220), EasingFunction = ease };
        Storyboard.SetTarget(slide, move); Storyboard.SetTargetProperty(slide, "Y");
        sb.Children.Add(fade); sb.Children.Add(slide);
        sb.Begin();
    }

    void FadeIn()
    {
        if (!ThemeService.Motion) return;
        Root.Opacity = 0;
        var sb = new Storyboard();
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, Root); Storyboard.SetTargetProperty(fade, "Opacity");
        sb.Children.Add(fade);
        sb.Completed += (_, _) => Root.Opacity = 1;
        Root.Loaded += (_, _) => sb.Begin();
    }

    // ---- sizes and placement (§2.2, §2.3). Sizes in DIPs ("w,h"), top-left in physical screen pixels ("x,y"). ----

    static (double W, double H)? ReadSize(string key)
    {
        var p = Prefs.Str(key).Split(',');
        return p.Length == 2 && double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)
               && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h) && w > 0 && h > 0
            ? (w, h) : null;
    }

    static string SizeKey(Tab t) => t == Tabs[0] ? "liveSize" : "windowSize";

    (double W, double H) SizeDip()
    {
        double s = Win32.Scale(hwnd);
        return (AppWindow.Size.Width / s, AppWindow.Size.Height / s);
    }

    Box WorkArea(Windows.Graphics.PointInt32? at = null)
    {
        var area = at is { } p ? DisplayArea.GetFromPoint(p, DisplayAreaFallback.Nearest) : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        var w = area.WorkArea;
        return new Box(w.X, w.Y, w.Width, w.Height);
    }

    void PlaceAtLaunch()
    {
        double s = Win32.Scale(hwnd);
        var (w, h) = ReadSize("liveSize") ?? (DefaultW, DefaultH);
        var tl = Prefs.Str("windowTopLeft").Split(',');
        Windows.Graphics.PointInt32? pos = null;
        if (tl.Length == 2 && int.TryParse(tl[0], out var x) && int.TryParse(tl[1], out var y))
        {
            // Ask Windows which screen holds the point instead of enumerating DisplayArea.FindAll(): enumerating that WinRT list
            // throws InvalidCastException under Native AOT (crashed every start that had a saved position, on a real PC).
            if (DisplayArea.GetFromPoint(new(x + 20, y + 20), DisplayAreaFallback.None) is { } da
                && Placement.TopLeftVisible(x, y, [new Box(da.WorkArea.X, da.WorkArea.Y, da.WorkArea.Width, da.WorkArea.Height)])) pos = new(x, y);
        }
        var work = WorkArea(pos);
        var start = pos ?? AppWindow.Position;
        // Pixels: at 100 % and above the DIP minimums of Fit are already met, so only the screen clamp and the shift matter.
        var fit = Placement.Fit(new Box(start.X, start.Y, Math.Max(w, Placement.MinWidth) * s, h * s), work);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32((int)fit.X, (int)fit.Y, (int)fit.W, (int)fit.H));
    }

    /// <summary>Tab switch: the group's saved size (or the current one), grown to the tab's minimum; top-left fixed; 0.2 s.</summary>
    void ResizeFor(Tab t)
    {
        if (resizing || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        var (w, h) = Placement.GrowTo(ReadSize(SizeKey(t)) ?? SizeDip(), (Math.Max(t.MinWidth, Placement.MinWidth), Math.Max(t.MinHeight, Placement.MinHeight)));
        double s = Win32.Scale(hwnd);
        var work = WorkArea();
        var pos = AppWindow.Position;
        var target = Placement.Fit(new Box(pos.X, pos.Y, w * s, h * s), work);
        var from = AppWindow.Size;
        if (Math.Abs(target.W - from.Width) < 2 && Math.Abs(target.H - from.Height) < 2) return;
        if (!ThemeService.Motion) { AppWindow.MoveAndResize(new((int)target.X, (int)target.Y, (int)target.W, (int)target.H)); return; }
        // 12 steps over 0.2 s, ease-in-out, top-left moves only when the fit had to shift the window.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        int step = 0;
        resizing = true;
        timer.Tick += (_, _) =>
        {
            double p = Math.Min(++step / 12.0, 1), e = p < 0.5 ? 2 * p * p : 1 - Math.Pow(-2 * p + 2, 2) / 2;
            int Lerp(double a, double b) => (int)Math.Round(a + (b - a) * e);
            AppWindow.MoveAndResize(new(Lerp(pos.X, target.X), Lerp(pos.Y, target.Y), Lerp(from.Width, target.W), Lerp(from.Height, target.H)));
            if (p >= 1) { timer.Stop(); resizing = false; }
        };
        timer.Start();
    }

    /// <summary>End of a user move/resize (WM_EXITSIZEMOVE): remember the top-left and this tab group's size.</summary>
    void SaveRect()
    {
        if (resizing || AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        var pos = AppWindow.Position;
        Prefs.Set("windowTopLeft", $"{pos.X},{pos.Y}");
        var (w, h) = SizeDip();
        Prefs.Set(SizeKey(current), FormattableString.Invariant($"{Math.Round(w)},{Math.Round(h)}"));
    }

    [UnmanagedCallersOnly]
    static unsafe nint WndProc(nint h, uint msg, nuint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (msg == Win32.WM_GETMINMAXINFO)
            {
                var r = SettingsNative.DefSubclassProc(h, msg, wParam, lParam);   // WinUI first, then our minimum (316 × 120 DIPs)
                double s = Win32.Scale(h);
                var mm = (Win32.MINMAXINFO*)lParam;
                mm->MinTrackSize.X = Math.Max(mm->MinTrackSize.X, (int)(Placement.MinWidth * s));
                mm->MinTrackSize.Y = Math.Max(mm->MinTrackSize.Y, (int)(Placement.MinHeight * s));
                return r;
            }
            if (msg == Win32.WM_EXITSIZEMOVE) instance?.SaveRect();
        }
        catch { }
        return SettingsNative.DefSubclassProc(h, msg, wParam, lParam);
    }

    // ---- window behaviour prefs ----

    void ApplyWindowPrefs()
    {
        Prefs.Store.EnforceReachable();
        if (AppWindow.Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = Prefs.Bool("onTop");
        AppWindow.IsShownInSwitchers = Prefs.Bool("dockIcon");   // taskbar button; never off together with the tray icon
    }

    /// <summary>Close = keep measuring in the background (keepRunning): hide to the tray, or minimize when there is no tray icon.</summary>
    void OnClosing(AppWindow sender, AppWindowClosingEventArgs e)
    {
        SaveRect();
        if (App.Quitting) return;
        if (!Prefs.Bool("keepRunning")) { App.Quit(); return; }
        e.Cancel = true;
        if (App.Tray is { Visible: true }) sender.Hide();
        else if (sender.Presenter is OverlappedPresenter p) p.Minimize();
    }
}
