using System.Diagnostics;
using Fusebox.Core.Live;
using Microsoft.UI.Dispatching;

namespace Fusebox.App.Controls;

/// <summary>
/// The one shared animation clock (Mac: one timer for every graph and number, 1/15 s steps). A single DispatcherQueueTimer that only runs
/// while something is animating: glides, number fades, row intros, graph reveals, core bars. Nothing animates (<see cref="Allowed"/>
/// false) in Low impact, with Windows animations off, or while the window is hidden: the caller then jumps to the end state.
/// UI thread only.
/// </summary>
public static class Anim
{
    public const double StepSeconds = 1.0 / 15;
    static readonly Stopwatch clock = Stopwatch.StartNew();
    static readonly List<Func<double, bool>> steps = [];
    static DispatcherQueueTimer? timer;
    static double introStart = -100, pace = 1;
    static bool launched;

    /// <summary>Seconds on the shared clock.</summary>
    public static double Now => clock.Elapsed.TotalSeconds;

    /// <summary>True when an animation may run: motion on (not Low impact, Windows animations on) and a window of ours is on screen.</summary>
    public static bool Allowed => ThemeService.Motion && ((App.Main?.IsOnScreen ?? false) || (App.Tray?.FlyoutOpen ?? false));

    /// <summary>Runs <paramref name="step"/> (argument: <see cref="Now"/>) every 1/15 s until it returns false.</summary>
    public static void Run(Func<double, bool> step)
    {
        steps.Add(step);
        timer ??= CreateTimer();
        if (!timer.IsRunning) timer.Start();
    }

    static DispatcherQueueTimer CreateTimer()
    {
        var t = DispatcherQueue.GetForCurrentThread().CreateTimer();
        t.Interval = TimeSpan.FromSeconds(StepSeconds);
        t.IsRepeating = true;
        t.Tick += (_, _) =>
        {
            double now = Now;
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                bool keep;
                try { keep = steps[i](now); } catch { keep = false; }
                if (!keep) steps.RemoveAt(i);
            }
            if (steps.Count == 0) t.Stop();
        };
        return t;
    }

    /// <summary>Plays <paramref name="apply"/>(0…1) over <paramref name="seconds"/> after <paramref name="delay"/>, then calls <paramref name="done"/>.</summary>
    public static void Tween(double seconds, double delay, Action<double> apply, Action? done = null)
    {
        double start = Now + delay;
        Run(now =>
        {
            if (now < start) return true;
            double p = Math.Clamp((now - start) / Math.Max(seconds, 0.001), 0, 1);
            apply(p);
            if (p < 1) return true;
            done?.Invoke();
            return false;
        });
    }

    // ---- launch / tab-switch intro (§10.12: rows and graphs only within 1.2 s) ----

    /// <summary>Call when the Live page is shown by a tab switch or at launch.</summary>
    public static void BeginIntro()
    {
        pace = Motion.Pace(!launched);
        launched = true;
        introStart = Now;
    }

    public static bool IntroActive => Allowed && Motion.IntroActive(Now - introStart);
    public static double Pace => pace;
}
