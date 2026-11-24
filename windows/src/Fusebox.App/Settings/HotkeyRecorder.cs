using Fusebox.Core.Settings;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Settings;

/// <summary>
/// The shortcut recorder of Settings › Appearance (§11.3): click the button, press the keys. Esc cancels, Delete turns the shortcut off,
/// a key needs Ctrl or Alt (Shift optional). While it listens the shortcut itself is paused. The rules are in <see cref="HotkeyLogic"/>.
/// A refused registration (another app owns the combination) shows "Another app already uses this shortcut." in <paramref name="conflict"/>.
/// </summary>
internal static class HotkeyRecorder
{
    public static Button Create(SettingsCard card, InfoBar conflict)
    {
        bool recording = false, swallowUp = false;
        var b = new Button { MinWidth = 132, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };

        void Show()
        {
            b.Content = recording ? L("Press keys…") : Prefs.Int("hotkeyCode") <= 0 ? L("Off") : Prefs.Str("hotkeyLabel");
            card.SetDescriptionText(recording ? L("Esc cancels, Delete turns it off. Use Ctrl or Alt.") : L("Works from any app."));
            conflict.IsOpen = !recording && Hotkey.Conflict;
        }

        void Stop() { if (!recording) return; recording = false; Hotkey.Suspend(false); Show(); }

        bool Down(VirtualKey k) => InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(CoreVirtualKeyStates.Down);

        b.Click += (_, _) =>
        {
            if (recording) { Stop(); return; }
            recording = true;
            Hotkey.Suspend(true);
            Show();
            b.Focus(FocusState.Keyboard);
        };
        b.LostFocus += (_, _) => Stop();
        b.Unloaded += (_, _) => Stop();
        b.KeyUp += (_, e) => { if (swallowUp || recording) { e.Handled = true; swallowUp = false; } };   // the Space/Enter that ended recording must not click again
        b.KeyDown += (_, e) =>
        {
            if (!recording) return;
            e.Handled = true;
            var (result, code, mods, label) = HotkeyLogic.Record((int)e.Key, Down(VirtualKey.Control), Down(VirtualKey.Menu), Down(VirtualKey.Shift));
            switch (result)
            {
                case RecordResult.Cancel: Stop(); break;
                case RecordResult.Off:
                    Prefs.Set("hotkeyCode", -1);
                    swallowUp = true;
                    Stop();
                    break;
                case RecordResult.Accept:
                    Prefs.Set("hotkeyCode", code);
                    Prefs.Set("hotkeyMods", mods);
                    Prefs.Set("hotkeyLabel", label);
                    swallowUp = true;
                    Stop();   // resumes the shortcut (registers the new one now) and reads whether Windows accepted it
                    break;
                // Ignore: a modifier on its own, keep listening. Reject: not a valid shortcut, keep listening (the Mac beeps).
            }
        };
        Show();
        return b;
    }
}
