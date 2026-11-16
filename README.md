# Fusebox (beta)

A lightweight activity monitor for your Mac: live CPU, GPU, RAM, temperature, internet and SSD, which apps use the most, and advice.

> **Beta.** Fusebox is still in development. Expect rough edges; updates arrive automatically.

**Runs on:** a Mac with Apple silicon (M1 or newer) and macOS 26 or newer.

## Install

1. Unzip `Fusebox.zip` and drag **Fusebox** into your **Applications** folder.
2. Open it. The first time, macOS says the app can't be verified, because it doesn't come from the App Store.
3. Go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway** next to Fusebox. You only need to do this once.
4. A short setup opens: one question per step (style, what to show, menu bar, notifications…). You can change everything later in Settings.

## Update

Got a newer Fusebox? Just open it. It asks **Update Fusebox?** and replaces the old one in Applications. Your settings and history stay. (macOS may ask **Open Anyway** once again for the new version.)

## Use

- **Live:** drag the window larger for more columns, small graphs and details. Click a number to switch between % and real values (cores, GB).
- **Processes:** click a row for a plain-language explanation. Use ✕ to quit an app.
- **Checkup:** why your Mac is slow, health score, crashes, battery health, tips and a shareable report.
- **Network:** every device on your network (name, IP, maker, type, ms), router vs. internet speed and Wi-Fi signal. macOS asks once for Local Network access.
- **Fans** (MacBook Pro, Mac Studio, Mac mini): live fan speed, and in Settings › Fans your own fan curve. Control needs a small helper that asks for your password once; Fusebox never runs fans slower than macOS's minimum.
- **Settings (gear):** style, which measurements you see and in what order, graphs, menu bar and notifications.

Measurements are saved in `Documents/Fusebox` as CSV (one small file per day; open the folder via Settings → Privacy). Nothing goes to the internet, except a short connection to measure latency (ms). You choose the address for that yourself in Settings.
