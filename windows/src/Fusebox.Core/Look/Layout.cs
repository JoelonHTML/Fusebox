namespace Fusebox.Core.Look;

public enum Tier { XS, S, M, L, XL }

/// <summary>
/// The Live grid (FEATURES.md §3.2, Mac LiveLayout): tier by space per row, balanced columns, never scrolling.
/// Recompute on every resize; re-render only when the result changes (record equality).
/// </summary>
public readonly record struct LiveLayout(Tier Tier, int Cols, int Rows, bool Side)
{
    /// <param name="width">Space below the tab bar (DIPs).</param>
    /// <param name="n">Number of enabled metrics.</param>
    /// <param name="pad">Theme.RowPad (lines 15, plain 10, cards/blocks/glass 22).</param>
    /// <param name="advice">Pref showAdvice.</param>
    public static LiveLayout Compute(double width, double height, int n, double pad, bool advice)
    {
        bool xl = width >= 960 && height >= 610;
        double grid = width - 24;
        int maxCols = Math.Clamp((int)Math.Floor(grid / 250), 1, 4);
        int rows = Math.Max((n + maxCols - 1) / maxCols, 1);
        int cols = Math.Max((n + rows - 1) / rows, 1);
        double C(Tier t) => (height - (10 + (advice && t > Tier.XS ? 24 : 0))) / rows - pad;
        var tier = xl ? Tier.XL : C(Tier.L) >= 150 ? Tier.L : C(Tier.M) >= 70 ? Tier.M : C(Tier.S) >= 38 ? Tier.S : Tier.XS;
        bool side = tier >= Tier.L && (grid - 10 * (cols - 1)) / cols >= 440;
        return new(tier, cols, rows, side);
    }

    /// <summary>Minimum content height: rows × (17 + rowPad/2) + 10 (≈ XS).</summary>
    public static double MinHeight(int rows, double pad) => rows * (17 + pad / 2) + 10;
}

/// <summary>A rectangle in Windows screen coordinates (y grows downward). Units are whatever the caller uses (px or DIPs).</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool Contains(double px, double py, double tolerance = 1) =>
        px >= X - tolerance && px <= Right + tolerance && py >= Y - tolerance && py <= Bottom + tolerance;
}

/// <summary>Window placement rules (§2.3, §9.1) as pure functions.</summary>
public static class Placement
{
    public const double MinWidth = 316, MinHeight = 120, FitMinHeight = 160, SettingsGap = 12;

    /// <summary>
    /// Mac `fit`: clamp size to [316, visible width] × [160, visible height], then shift left/up until it fits.
    /// Only shrinks when larger than the whole screen; never squeezes a window that just sits low on the screen.
    /// </summary>
    public static Box Fit(Box w, Box visible)
    {
        double width = Math.Min(Math.Max(w.W, MinWidth), visible.W), height = Math.Min(Math.Max(w.H, FitMinHeight), visible.H);
        double x = Math.Max(Math.Min(w.X, visible.Right - width), visible.X);
        double y = Math.Max(Math.Min(w.Y, visible.Bottom - height), visible.Y);
        return new(x, y, width, height);
    }

    /// <summary>A saved top-left is used only when (x+20, y+20) lies inside a connected screen's work area.</summary>
    public static bool TopLeftVisible(double x, double y, IEnumerable<Box> workAreas) => workAreas.Any(a => a.Contains(x + 20, y + 20));

    /// <summary>Tab switch: each dimension = max(saved size of that group, tab minimum). Never shrinks what the user chose.</summary>
    public static (double W, double H) GrowTo((double W, double H) saved, (double W, double H) minimum) =>
        (Math.Max(saved.W, minimum.W), Math.Max(saved.H, minimum.H));

    /// <summary>
    /// Tray flyout next to its icon, like Quick Settings: on the taskbar's side of the work area, <paramref name="margin"/> away from the
    /// edges, centered on the icon along the taskbar and clamped into the work area.
    /// </summary>
    public static (double X, double Y) Flyout(Box icon, Box work, double w, double h, double margin = 12)
    {
        double cx = icon.X + icon.W / 2, cy = icon.Y + icon.H / 2;
        double x = Math.Clamp(cx - w / 2, work.X + margin, Math.Max(work.X + margin, work.Right - w - margin));
        double y = cy >= work.Y + work.H / 2 ? work.Bottom - h - margin : work.Y + margin;
        if (cx < work.X) x = work.X + margin;                                   // taskbar on the left
        else if (cx > work.Right) x = work.Right - w - margin;                 // taskbar on the right
        if (cx < work.X || cx > work.Right) y = Math.Clamp(cy - h / 2, work.Y + margin, Math.Max(work.Y + margin, work.Bottom - h - margin));
        return (x, Math.Max(y, work.Y));
    }

    /// <summary>
    /// Mac SettingsPlacement: right of the main window (gap 12), else left of it, else centered over it (clamped to the screen);
    /// tops aligned, moved up if it would run off the bottom.
    /// </summary>
    public static (double X, double Y) Settings(Box main, Box screen, double w, double h)
    {
        double x = main.Right + SettingsGap + w <= screen.Right ? main.Right + SettingsGap
                 : main.X - SettingsGap - w >= screen.X ? main.X - SettingsGap - w
                 : Math.Clamp(main.X + main.W / 2 - w / 2, screen.X, Math.Max(screen.X, screen.Right - w));
        double y = Math.Max(Math.Min(Math.Max(main.Y, screen.Y), screen.Bottom - h), screen.Y);
        return (x, y);
    }
}
