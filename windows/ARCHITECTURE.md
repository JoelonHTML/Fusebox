# Fusebox for Windows — architecture and contracts (v2 foundation)

Behaviour, numbers and wording come from the Mac (the spec FEATURES.md and `Sources/*.swift`). This file says where the shared pieces
live and how a page uses them. Section numbers like §3.2 refer to FEATURES.md.

## 1. Projects

| Project | What | Rule |
|---|---|---|
| `src/Fusebox.Core` (net10.0) | pure logic: prefs table, themes, formatting, layout, history, CSV, parsers | no Windows APIs; unit-test everything in `tests/Fusebox.Tests` |
| `src/Fusebox.Platform` | Windows readers (CPU/GPU/RAM/net/disk/battery, processes, checkup, network, docked) | `LibraryImport`, AOT-safe |
| `src/Fusebox.App` (WinUI 3) | windows, pages, services | x:Bind or code only (never `{Binding}`), classes `partial` |

## 2. Preferences — `Core/Settings/PrefTable.cs`, `PrefStore.cs`, `App/Prefs.cs`

- **Every key** (Mac §9.17 + Windows additions) is one line in `PrefTable.All`: key, default (bool, long for all integers, double,
  string; lists are comma strings) and scope: **Shared** (exported/imported), **Local** (this PC: never exported/imported, but reset by
  "Reset everything": pingHost, keepDays, autoUpdate, hotkey*, appLanguage, setupDone, page, settingsTab, fan/charge keys) or
  **Bookkeeping** (window sizes, updater state: never exported, imported or reset). New setting = one line there.
- App code: `Prefs.Bool("onTop")`, `Prefs.Int/Long/Double/Str/List(key)` (table default when unset or wrong type), `Prefs.Set(key, v)`,
  `Prefs.SetList`, `Prefs.Reset(key)`. A unit test fails when a literal key used with `Prefs.*` is not in the table.
- `Prefs.Changed(key)` fires only on a real change, **on the thread that called Set** — marshal to the UI yourself.
- Stored ids are the Mac's: themes `instrument|terminal|minimal|glas|blokken|liquid`, fonts `style|sfPro|rounded|mono|serif|montserrat|ubuntu`,
  `lowImpact` `off|on|auto` (the live state is `LowImpact.On`, not a pref). Windows: `menubarStyle` = tray icon style (`off` = no tray
  icon), `dockIcon` = taskbar button; `PrefStore.EnforceReachable()` never lets both be off. Hotkey = `hotkeyCode` (VK, −1 off) +
  `hotkeyMods` (MOD_ALT 1 | MOD_CONTROL 2 | MOD_SHIFT 4 | MOD_WIN 8), default Ctrl+Alt+F. `netHide` "" = MAC column visible.
- `Prefs.Store` (the `PrefStore`): `ApplyProfile("simple|gamer|nerd|laptop")` (resets every profile-touched key, then applies),
  `ResetAll()`, `Export()` (pretty sorted JSON, Shared keys only → "Fusebox-settings.json"), `ImportFile(path)` → applied count or null
  ("Fusebox can't read this file."). Import = trust boundary: < 100 000 bytes, JSON object, known Shared keys only, same type as the
  default (real bool; exact integer; finite double; string ≤ 200). `Profile.All` has id/title/blurb/values.
- v1 files are migrated once on load (`PrefStore.Migrate`): language→appLanguage, appearance→appearanceMode, glass→glas,
  blocks→blokken, segoe→sfPro, cascadia→mono, lowImpactMode→lowImpact, logDays→keepDays, tray=false→menubarStyle off,
  hotkey=false→hotkeyCode −1, windowRect→windowTopLeft, metric id bat→battery.

## 3. Theme — `Core/Look/Theme.cs`, `App/ThemeService.cs`, `App/Themes/Fusebox.xaml`, `App/Controls/Look.cs`

- Core: `Themes.All` (6 themes, exact Mac tokens), `Themes.Resolve(id, accent, textScale, font, scheme, lowImpact, systemAccent)` =
  §10.0 pipeline (accent overlay → scale 0.8–1.6 → font → light/dark conversion §10.3 → WCAG accent fix §10.4 → `.solid` §10.6).
  `Theme` helpers: `OnRow(secondary)`, `LabelColor` (accent only in Instrument), `Graph` (white on Color blocks), `Level(0/1/2)`,
  `Block(i)`, `RowPad`, `Face` (font), `Size(pt)` (× scale). `RowSpec.For(style)`: padding/radius/rowPad per row style.
- App: `ThemeService.Current` (resolved theme), `ThemeService.ThemeChanged` (UI thread), `ThemeService.Motion` (false in Low impact or
  with Windows animations off: **check it before any animation**), `ThemeService.Attach(window, themed)` once per window
  (themed: theme bg or Mica for Liquid Glass, RequestedTheme, caption colors; `themed: false` = system look for Settings/Setup).
  Never Acrylic behind live numbers.
- **Brush keys** (mutated in place, so `{ThemeResource Key}` in XAML or `ThemeService.Brush("Key")` in code stay live):
  `FuseboxBg, FuseboxText, FuseboxSecondary, FuseboxAccent, FuseboxWarn, FuseboxDanger, FuseboxGood, FuseboxLine, FuseboxCardFill,
  FuseboxOnRow, FuseboxOnRowSecondary, FuseboxLabel, FuseboxGraph, FuseboxGraphFill (15 %), FuseboxGraphSecond (50 %),
  FuseboxAccentSoft (accent 15 %: selected tab, chips), FuseboxHover (secondary 12 %), FuseboxBlock0…4, FuseboxGlassFill, FuseboxGlassRim`.
  Text on a themed row uses `FuseboxOnRow`/`FuseboxOnRowSecondary` (white on Color blocks). v1 names (`FuseboxCardBackground`,
  `FuseboxCardBorder`, `FuseboxGraphStroke`, `FuseboxWarning`, `FuseboxBad`) still work; style `FuseboxCard` = generic radius-8 card.
- **Typography**: `<TextBlock c:Look.Number="30"/>` / `Look.Number(tb, 30)` = theme font, theme number weight, × textScale, tabular
  digits; `c:Look.Label="13"` = Medium. Re-applied on ThemeChanged automatically. Fonts (PLAN §7.8): Segoe UI Variable, Nunito
  (bundled, rounded), Cascadia Mono → Consolas, Georgia, Montserrat/Ubuntu (bundled from `Resources/Fonts`); `ThemeService.Family(face)`.
- **Row style**: `<c:ThemedRow Index="0">…</c:ThemedRow>` (lines / plain / cards 12 / blocks 10 / glass 14, Mac radii; follows theme
  changes) or `ThemedRow.Apply(border, index)` on your own Border (call again on ThemeChanged). Spacing between rows is the page's job.

## 4. Formatting and layout — `Core/Format.cs`, `Core/Look/Layout.cs`

- `Fmt`: `Pct` (never "−0%"), `One`, `Bytes` (decimal, "640 KB"/"12.3 MB"), `Rate`, `Short(bps, bits)`, `RatePair(a, b, bits)` → (a, b,
  unit), `Deg`, `Duration(min)`, `BatteryTime(min)` ("2:35", "14h"), `Level(v, warn, danger)`, `Rpm`. Always en-US digits.
- `MemoryPressure.FromRamPercent` (1/2/4), `StatusSentence.Of(pressure, temp, cpu30, warnCpu, warnTemp)` → (translated text with "PC",
  level 0/1/2); `App.Live.Status` gives it for the current values.
- `LiveLayout.Compute(width, height, n, theme.RowPad, showAdvice)` → (Tier XS…XL, Cols, Rows, Side), exact §3.2; re-render only when it changes.
- `Placement`: `Fit` (§2.3), `TopLeftVisible`, `GrowTo`, `Settings` (§9.1), `Flyout` (tray), all in Windows y-down coordinates.

## 5. Data — `App/LiveHub.cs`, `Core/Metric.cs`, `Core/CsvLog.cs`, `Core/Peaks.cs`, `Core/Processes/AppsLog.cs`

- `App.Live` (one clock, 2 s / 5 s Low impact, measuring on a worker thread). UI-thread events: `Updated` (only while watched: main
  window on screen or flyout open, plus the first 5 ticks), `Sampled` (always; trivially cheap work only), `MinuteTick` (≥ 59 s apart,
  watched or not: alerts, peak notifications, self log). Properties: `Last` (LiveSample), `History`, `Peaks`, `Apps` (last minute's
  app groups, CPU averaged over the minute), `Hardware`, `Status`, `Cpu30`, `StepSeconds`, `ShortMinutes`.
- History (`SampleHistory`): `Recent` (last 3600 s, cap 1800), `Since(seconds, step)` (half-step margin), `Today` (minute averages,
  seeded from today's CSV at start, + running minute), `Range(TimeRange.Short|Hour|Today, shortMinutes, step)`. Graph values:
  `App.Live.Values(Metric.Cpu, TimeRange.Short)`; stats `Metrics.Stats(values)`. Titles `TimeRanges.Title`.
- `Metric` = Mac ids `cpu,gpu,ram,net,ssd,io,battery` (`Metrics.Parse/Join/Id/Label/Title/Value/Scale`). SSD = used %, I/O = R+W bytes/s.
- CSVs in `Documents\Fusebox` with the Mac's file names and Dutch columns: `meterkast-YYYY-MM-DD.csv` (`CsvLog.Line`, every sample,
  flushed ≥ 60 s; no CPU temp/fans on Windows → empty; swap 0), `apps-…csv` (`AppsLog.Lines`, per minute: top 10 CPU ∪ top 10 memory
  ∪ apps with a window; energy 0), `peaks-…csv` (`PeakWatch.CsvLine`, header `tijd,eind,soort,hoogte,app,app_waarde`),
  `battery-health.csv` (once a day). Retention `CsvLog.Prune(keepDays)` by filename date at start (battery-health never);
  `CsvLog.DeleteAll()`. Today's files with an old header are remapped by name (`CsvLog.MigrateHeader`).
- Readers for Checkup (malformed lines skipped): `CsvLog.ReadSamples(csv)`, `CsvLog.ReadMinutes(csv)` (§5.4 minute aggregation:
  offline, wifiDrop …), `AppsLog.Parse(csv)` (header or fixed Mac order), `PeakWatch.Parse(csv)`, generic `CsvTable`.
- PDF reports (`Core/Reports`): `Pdf` = tiny PDF 1.4 writer (A4, Helvetica in cp1252, lines/rects/polylines; no package), `ReportPdf` = the
  light page kit (At a glance / Stands out boxes, tables, line and bar charts, page breaks, footer, scrubber on every text), `NetPdf` and
  `CheckupPdf` build the two reports from what is already known. Saved via the share sheet ("Save as PDF…") and Checkup's "Export PDF".
- Peaks (`PeakWatch`, §3.9): cpu/gpu ≥ 80, temp ≥ warnTemp, mem pressure ≥ 2 (1 sample), io ≥ max(50 MB/s, p95 of the hour). Cause =
  biggest app of the last minute (`LiveHub.Blame`); the Live agent can refine with a snapshot on `Peaks.Started`.

## 6. Window shell — `App/MainWindow.xaml(.cs)`, `App/App.xaml.cs`

- Tabs live in `MainWindow.Tabs` (tag, Mac id, title, glyph, page type, minimum size). **A page agent only swaps the page type there.**
- `App.Main.Open(tag, parameter?)` is the one way to switch (tags Live/Processes/Checkup/Network/Docked or Mac ids; "Settings" opens the
  Settings window); the parameter reaches `OnNavigatedTo`. Sub-pages (detail, app page): `Frame.Navigate` inside the host, back =
  `Frame.GoBack`; switching tabs clears the back stack. `App.ShowMain()`, `App.ShowMain(tag, param)`, `App.Quit()`.
- Page contract: subscribe to `App.Live.Updated` in `OnNavigatedTo`, unsubscribe in `OnNavigatedFrom`; heavy work on a worker
  thread; nothing per frame; `Loc.Apply(this)` once on Loaded for XAML text. Page background is the window's (theme bg).
- Behaviour: tabs in the title bar (drag region = empty part only), icons below 700 / names from 740 DIPs, accent drop slides
  (TranslationTransition, not in Low impact), Ctrl+1…5 and Ctrl+, , tooltips "Live  Ctrl+1". Two sizes: `liveSize` / `windowSize`
  ("w,h" DIPs, saved at the end of a user resize), `windowTopLeft` ("x,y" pixels); tab minimums grow only (0.2 s, top-left fixed);
  minimum 316×120; no maximize button (6 tabs + 2 caption buttons fit 316). Launch: Live, saved Live size and position if on a screen,
  0.18 s fade. Close: `keepRunning` → hide to the tray (or minimize without a tray icon), else quit. `onTop`, `dockIcon` applied live.
- Page transition: fade + 10 DIPs up in 0.22 s (skipped when `ThemeService.Motion` is false).

## 7. Settings window — `App/Settings/`

- `SettingsWindow.Open(tabId?)`: one window shaped like Windows 11 Settings: resizable (1000×700 default, minimum 640×500 DIPs, size in
  `settingsSize`), sidebar open from 860 DIPs (icons only below), placed right/left/centered of the main window (§9.1), system look
  (Mica), remembers `settingsTab`. Sidebar = `SettingsWindow.Tabs` (Mac ids profile, look, live, processes, checkup, network, docked, menubar =
  "Tray icon", alerts, battery (only with a battery), data, about; no fans).
- One Page per tab (`ProfileSettingsPage` … `AboutSettingsPage`, all built): a fixed title (28) above a scrolling `x:Name="Body"`
  StackPanel, content at most 1000 wide and centred, rows 4 apart.
  Shared pieces: `Parts` (theme/text-size/font/graph/tray-style tiles, accent picker, alert rows, taskbar card, share/import settings),
  `Previews` (live-looking samples of rows, tables, tray styles, the number-change demo, all radius 6; timers only while loaded), `Tile`
  (one tile style: radius 8, calm fill + subtle stroke, selected = 2 px accent ring; `Parts.Arrange`/`TileGrid` cap tile widths), `Kit`
  (`Pick` drop-down, stepper, `WhilePrefs(el, h)` / `WhileTheme(el, h)` = subscribe on Loaded, unsubscribe on Unloaded, already on the UI thread).
- **Shortcut**: `HotkeyRecorder.Create(card, conflictBar)` (Appearance) records Ctrl/Alt (+Shift) + key via `HotkeyLogic.Record` into
  `hotkeyCode`/`hotkeyMods`/`hotkeyLabel`; Esc cancels, Delete = off. `Hotkey.Suspend(true)` while it listens; `Hotkey.Conflict` = another
  app owns the combination.
- **Notifications** (`App/Notify.cs`): `Notify.Send(title, body, Act? click = null, params Act[] buttons)` with
  `Act(Label, Verb, params (Key, Value)[] Args)`; respects pref `notify`. Clicks come back as verb + args through
  `Notify.On(verb, handler)` (UI thread, last registration wins). Built in: `open` (tab, param) and `settings` (tab).
  `AlertRunner` (§11.2) evaluates the pure `Core/Alerts/AlertEngine` on `App.Live.MinuteTick` (kinds `AlertKinds.Ids`, prefs
  `alert.<id>.on/limit/min`, `alertCooldown`); a click opens the tab that explains it (`AlertRunner.TabFor`). `PeakAlert` = sustained-peak
  notification with Stop app / Show buttons.
- **Own use** (`App/SelfUsage.cs`, §11.5): one line per minute in `self-YYYY-MM-DD.csv` (`Core/SelfLog`, pruned with the other day files);
  `SelfUsage.ShareReport()` (Settings › About) writes the 7-day performance report to Downloads + clipboard, home folder as "~".
- **Checkup in the tray**: `CheckupData.GistAsync()` (today's report, cached ≤ 10 min, else loaded off the UI thread) and `GistNow()`
  (memory only, may be null); App sets `TrayFlyout.GistProvider` to return `GistNow()` and start one `GistAsync()` at a time.
- Building blocks: `<s:SettingsCard Glyph="&#xE713;" Header="…" Description="…"><ToggleSwitch/></s:SettingsCard>` (header/description
  are English keys, translated by the card; `SetDescriptionText` for built text), section header
  `<TextBlock Style="{StaticResource SettingsSectionHeader}" Text="…"/>`, title style `SettingsPageTitle`. In code: `SettingsUi.Card`,
  `Section`, `Toggle(key)`, `Choice(key, items)` / `ChoiceBool` / `LongChoice` (choices are drop-downs, like Windows), `Block(content)` (a
  card holding tiles or a preview), `Expand(headCard, items…)` + `Item(card)` (SettingsExpander on the WinUI Expander), `CheckList` /
  `HiddenList` (column and section choices as check boxes in an expander), `TranslateOnce(page)`. A ToggleSwitch in a card gets "On"/"Off"
  on its left (`Switch`). Destructive actions are normal buttons with a `Confirm` dialog; at most one accent button per page. `tools/strings-check.py` knows Card/Section/SettingsCard text.

## 8. Tray — `App/Tray.cs`, `App/TrayFlyout.xaml(.cs)`

- `Tray`: Shell_NotifyIcon; left click = flyout, right click = menu (status sentence, Open Fusebox, Settings…, Quit Fusebox);
  `IconRect()` = the icon's screen rectangle. The icon bitmap/tooltip follows `LiveHub.Sampled` (redraw only on change).
- `TrayFlyout`: 336 DIPs, padding 12, rows 10 apart, above the icon like Quick Settings (`Placement.Flyout`), themed (bg or Mica),
  tool window (no taskbar/Alt+Tab), closes on deactivate, height follows the content. Add rows in `Build()` with `AddRow(content,
  index)` (a ThemedRow), update them in `Render()` (runs on open and on `Live.Updated` while open). Now: status row, value lines,
  five quick tiles (Fusebox, Checkup, Processes, Network = find the problem via `netFindNow`, Settings).

## 9. CI smoke tour — `App.SmokeTour` + `.github/workflows/windows.yml`

With `FUSEBOX_SMOKE=1`: Live 20 s (idle CPU measured), then Processes, Checkup, Network, Docked and the Settings window 5 s each via
`Main.Open(tag)`; the workflow screenshots each step; `smoke-ok.txt` lists open time and longest UI stall per step. Keep tags in
`MainWindow.Tabs` and "Settings" working, or update both files.

## 10. Performance rules

One clock (`LiveHub`); nothing redraws while the window is hidden/minimized (`Updated` only while watched); no per-frame work (one
implicit transition or one short storyboard, never a CompositionAnimation per number); no Acrylic behind live numbers; in Low impact
(`LowImpact.On`) measure every 5 s and skip every animation (`ThemeService.Motion`).
