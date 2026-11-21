using Fusebox.Core.Live;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fusebox.App.Controls;

/// <summary>
/// A number that changes (§10.9, pref numberStyle). Right-aligned in a fixed-width box sized by an invisible template string, so the layout
/// never jumps. Empty before the first real sample (never a fake "0%"); the first real value flips in once. After that: fade (default,
/// old and new stacked, 6 steps on the shared clock, smoothstep, no dip to transparent), roll (old slides down, new slides in from above)
/// or instant. Always instant when hidden or in Low impact. Never a Storyboard per number.
/// </summary>
public sealed partial class ChangingText : Grid
{
    readonly TextBlock ghost = new() { Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock now = new() { HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock before = new() { HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0 };
    readonly TranslateTransform nowMove = new(), beforeMove = new();
    double version;
    bool ready;

    /// <summary>Card index: roll waits (index % 6) × 0.3 s so values don't all change at once.</summary>
    public int Index { get; set; }

    public ChangingText(double points, bool label = false)
    {
        foreach (var t in new[] { ghost, now, before })
        {
            if (label) Look.Label(t, points); else Look.Number(t, points);
            t.TextWrapping = TextWrapping.NoWrap;
        }
        now.RenderTransform = nowMove; before.RenderTransform = beforeMove;
        Children.Add(ghost); Children.Add(before); Children.Add(now);
    }

    public Brush Foreground { set { now.Foreground = value; before.Foreground = value; } }

    /// <summary>The widest text this number can show ("100%", "↓888 ↑888"); the box is as wide as it.</summary>
    public string Template { set => ghost.Text = value; }

    public string Text => now.Text;

    public void Set(string text)
    {
        if (text == now.Text) return;
        version++;
        string old = now.Text;
        bool first = old.Length == 0 && text.Length > 0 && !ready;
        if (text.Length > 0) ready = true;
        if (!Anim.Allowed || old.Length == 0 && !first || text.Length == 0)
        {
            Settle(text);
            return;
        }
        string style = first ? "roll" : Prefs.Str("numberStyle");
        if (style == "instant") { Settle(text); return; }
        double id = version, start = Anim.Now + (first ? 0.1 + Index * 0.3 / 3 : style == "roll" ? Index % 6 * 0.3 : 0);
        before.Text = old; now.Text = text;
        int step = -1;
        Anim.Run(t =>
        {
            if (id != version) return false;                       // a newer value took over
            if (t < start) { Apply(style, 0); return true; }
            int s = Math.Min((int)((t - start) / Motion.GridSeconds) + 1, Motion.FadeSteps);
            if (s == step) return true;
            step = s;
            Apply(style, Motion.FadeIn(s));
            if (s < Motion.FadeSteps) return true;
            Settle(text);
            return false;
        });
        Apply(style, 0);
    }

    void Apply(string style, double p)
    {
        now.Opacity = p; before.Opacity = 1 - p;
        if (style == "roll")
        {
            double h = Math.Max(now.ActualHeight, 12) * 0.6;
            nowMove.Y = -h * (1 - p); beforeMove.Y = h * p;       // new digits slide in from above, old ones out below
        }
    }

    void Settle(string text)
    {
        now.Text = text; before.Text = "";
        now.Opacity = 1; before.Opacity = 0;
        nowMove.Y = beforeMove.Y = 0;
    }
}
