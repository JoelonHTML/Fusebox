# Fusebox — Feature and UI specification (for the Windows port)

> Source of truth: `Sources/*.swift` of the macOS app (version 0.6 beta, SwiftUI, macOS 26+, Apple silicon).
> Audience: a developer rebuilding Fusebox in WinUI 3 / C# without access to a Mac.
> English is the source language: every UI string in this document is quoted exactly as it appears in the
> Swift code (`L("…")` / `Text("…")` literals). `Resources/en.lproj/Localizable.strings` is intentionally empty;
> the other languages (`nl`, `de`, `fr`, `es`) are `.strings` files keyed by the English text.
> Wording conventions per language: `docs/translation-glossary.md` (nl "je", de "du", fr "vous", es "tú"; path separator ` › `).
>
> Notes in this document marked **Windows:** are porting hints, not part of the macOS behaviour.
> Segoe Fluent Icons mappings are suggestions; where there is no exact glyph it says "(closest)".
> Where the code differs from the intended product rules (e.g. privacy), this spec says so in a **Note** box —
> follow the product rule unless the product owner decides otherwise.

## Table of contents

1. Overview
2. Main window and tab bar
3. Live tab
4. Processes tab (+ 4.x App page)
5. Checkup tab (+ 5.B section details)
6. Network tab
7. Docked tab
8. Menu bar item and panel
9. Settings window
10. Themes and visual system (+ Low impact mode)
11. Data, alerts, hotkey, languages, privacy, first-run setup, updater
12. Porting checklist and key strings

---

## 1. Overview

### 1.1 Purpose

Fusebox (beta) is a light, native system monitor. README tagline: **"Clear · Light · Live."** —
"A Mac activity monitor made for Apple Silicon: CPU, GPU, RAM, temperature, internet, SSD and ports, which apps use the most, and advice."

It answers four questions, one per tab group:

| Tab (id) | Title (never translated) | Question it answers |
|---|---|---|
| `live` | **Live** | How is the machine doing right now? (cards with value + small graph) |
| `programs` | Processes (translated) | Which app uses CPU / memory / energy / disk / network? |
| `advice` | **Checkup** | Looking back: is something wrong, why is it slow, what can I clean up? |
| `network` | Network (translated) | Internet quality, speed test, devices on my network |
| `ports` | **Docked** | What is plugged in: chargers, cables, docks, displays, USB/Thunderbolt devices |

Plus: a menu bar item (Windows: notification-area/tray icon + flyout), a Settings window, and a first-run setup window ("Set up Fusebox").

### 1.2 Design principles (non-negotiable for the port)

| Principle | What it means concretely |
|---|---|
| Very low own CPU/GPU | Sample every 2 s (5 s in Low impact). Graphs glide in small steps, not at display frame rate. A hidden/occluded window or sleeping screen does not redraw at all (`onScreen` environment flag, driven by window occlusion state). Hidden menu-bar items don't follow the meter. Only pages observe the meter, not the root view. Processes are only measured while the Processes tab (or something needing it) is visible (`meter.programsVisible`). |
| Clear | One idea per card; plain-language texts ("Your Mac is fine"), real units, advice with an action button. |
| No clutter | Every page has a narrow layout; secondary info appears only when there is room (tiers by width *and* height). Empty things are hidden (not shown as "0"). Options to hide sections per page (`…Hide` prefs). |
| Calm motion | Short ease-out / spring animations; intro animation only right after launch or a tab switch; none at all in Low impact. |
| Honest numbers | Never "−0%"; plausibility checks on counters; values clamped in the CSV. |
| Privacy | Stored settings are short fixed ids, never user-visible text. Settings import never touches local-only keys (ping host, history retention, auto-update…). Share/report texts are scrubbed. See §11.6 and the notes in §6/§7. |

### 1.3 Names that are never translated

`Fusebox`, `Live`, `Checkup`, `Docked`, `Low impact` (in tooltips), and the theme names (see §10). Everything else goes through the localization table.

### 1.4 Languages

5 languages: English (source), Nederlands, Deutsch, Français, Español, plus "System" (follow OS). Language names are shown in their own language. Details: §11.4.

### 1.5 Platform scope and macOS-only features

| Feature | macOS mechanism | Windows equivalent hint |
|---|---|---|
| Fan control | privileged helper `fusebox-fand` (root) via SMC | LibreHardwareMonitor / vendor WMI; often not possible on laptops → hide |
| Charge limit | same helper, SMC charging keys | vendor tools (Lenovo/ASUS/Dell WMI) or hide |
| Open at login | `SMAppService.mainApp` | `StartupTask` (MSIX) or `HKCU\…\Run` |
| Notifications | `UNUserNotificationCenter` with action buttons | `AppNotificationManager` (Windows App SDK) with buttons |
| Global hotkey | Carbon `RegisterEventHotKey` | `RegisterHotKey` |
| Dock icon on/off | activation policy `.regular` / `.accessory` | show/hide taskbar button (`ShowInTaskbar`-like via `AppWindow.IsShownInSwitchers`) |

---

## 2. Main window and tab bar

### 2.1 Window basics

| Property | Value |
|---|---|
| Title | "Fusebox" (title text hidden: `windowToolbarStyle(.unifiedCompact(showsTitle: false))`, `toolbar(removing: .title)`) |
| Default size | 300 × 340 pt (first launch, and Live size when nothing saved) |
| Absolute minimum | 316 × 120 pt (content `frame(minWidth: 316, minHeight: 120)`) — fits 6 tab buttons (32 pt + 2 pt spacing) + traffic lights, never an overflow "»" |
| Fit-to-screen minimum | 316 × 160 when the window is clamped to a screen |
| Background | theme `bg` color if the theme has one; otherwise system `.thinMaterial` (Windows: Mica / Acrylic) |
| Toolbar background | hidden (content runs up behind the title bar) |
| Drag | window can be dragged by its background (`windowBackgroundDragBehavior(.enabled)`) |
| Restoration | macOS state restoration disabled; Fusebox does its own placement |
| Launch | always opens at launch (even if it was closed last time) and always on the **Live** tab |
| Launch fade | window starts at alpha 0 and fades to 1 in 0.18 s ease-out |
| Full screen | tab buttons stay visible in full screen |

### 2.2 Per-tab minimum sizes and the two remembered sizes

There are exactly **two remembered sizes**: one for Live (`liveSize`) and one shared by all other tabs (`windowSize`). Both are saved only at the end of a live resize (not during dragging) and not in full screen.

| Tab | Grows window to at least (w × h) | Why |
|---|---|---|
| Live | none (`.zero`) | Live adapts to any size via tiers |
| Processes | 460 × 420 | from 460 the disk column fits |
| Checkup | 420 × 460 | |
| Network | 470 × 440 | |
| Docked | 420 × 460 | |

Switching tabs (`resize(to:)`):
1. Target size = saved size for that group (`liveSize` for Live, `windowSize` otherwise; if never saved, current frame size).
2. Each dimension = max(saved, tab minimum). Only grows to the minimum, never shrinks below what the user chose; the user may always drag smaller afterwards (each page has a narrow layout).
3. If different from the current size: animate frame to it, **0.2 s ease-in-ease-out**, keeping the **top-left corner fixed**.
4. Skipped in full screen or during a live resize.

### 2.3 Placement rules

| Rule | Detail |
|---|---|
| Remembered position | top-left point saved as `windowTopLeft` on every move (not while live-resizing) and at end of resize; not in full screen |
| Restore only if visible | saved top-left is used only if the point (x+20, y−20) lies inside the visible frame (±1 pt) of a currently connected screen; otherwise the OS default position |
| Fit algorithm (`fit`) | size = clamp(width to [316, screen.visibleWidth]), clamp(height to [160, screen.visibleHeight]); then **shift** (left/up) until it fits; only shrink when larger than the whole screen. Never shrink to a strip because the window sits low on the screen |
| Test vector | visible 1440×800, window left 1300 top 300 size 600×700 → left 840, top 700, size unchanged. Size 2000×1200 at top 790 → 1440×800, top 800 |
| Launch | placed at saved Live size + saved top-left, without animation, then fade-in |
| Migration | old keys `size.<page>` are migrated to `liveSize` once and removed |

**Windows:** use `AppWindow.MoveAndResize` with DisplayArea.WorkArea as "visible frame"; store top-left in DIPs; note Windows y grows downward (macOS stores bottom-left origin, top = maxY).

### 2.4 Tab bar (toolbar header)

Sits in the title bar, right of the traffic lights (primaryAction placement), as real toolbar items (so clicks are not swallowed as window drags). Order: Live, Processes, Checkup, Network, Docked, then a **Settings** gear button.

```
 narrow (< 740 pt wide; back to icons below 700 pt):
 ● ● ●   [◔] [▦] [▥] [⌖] [▭] [⚙]

 wide (≥ 740 pt):
 ● ● ●   [◔ Live] [▦ Processes] [▥ Checkup] [⌖ Network] [▭ Docked] [⚙ Settings]
          ^^^^^^^ active: accent text on accent@15% rounded "drop"
```

| Element | Spec |
|---|---|
| Container | `HStack(spacing: 2)`, horizontal padding 4; vertical padding 2 only for the glass row style |
| Liquid Glass | only when `theme.rowStyle == .glass` (Liquid theme, not in Low impact): `.glassEffect(.regular, in: .capsule)` around the whole bar. All other themes: no glass. **Windows:** acrylic capsule (corner radius = height/2) behind the buttons |
| Button size | min 32 × 28 pt; horizontal padding 8 when wide, 0 when narrow |
| Label | icon only (narrow) or icon + title (wide); font system **13 pt medium** |
| Hysteresis | switch to names at width ≥ **740**; back to icons only below **700** (no flicker while dragging). The bar is rebuilt on switch (`.id(wide)`) so names are never truncated |
| Colors | active: `theme.accent`; hovered: `theme.text`; others: `theme.secondary` |
| Active background ("drop") | RoundedRectangle radius **8**, continuous corners, fill `accent.opacity(0.15)`, shared between buttons via matched geometry (slides to the new tab) |
| Hover background | same shape, fill `secondary.opacity(0.12)` |
| Tooltip | "`<Title>  ⌘<n>`" (e.g. "Live  ⌘1"); Settings: "Settings  ⌘," or, in Low impact, "Settings  ⌘,  ·  Low impact is on" |
| Shortcuts | ⌘1…⌘5 select tabs in order; ⌘, opens Settings. **Windows:** Ctrl+1…5, Ctrl+, |
| Low impact badge | tiny `leaf.fill` (8 pt, `theme.good`, padding 3) at the top-right of the gear |
| Accessibility | each button has its own label (title) |

Icons:

| Tab | SF Symbol | Segoe Fluent suggestion |
|---|---|---|
| Live | `gauge.with.needle` | Speed High `` |
| Processes | `square.grid.2x2` | Apps / GridView `` |
| Checkup | `chart.bar.xaxis` | BarChart (closest) `` |
| Network | `network` | Network `` |
| Docked | `dock.rectangle` | Dock bottom / Devices `` (closest) |
| Settings | `gearshape` | Settings `` |

#### Tab switch animation ("pulling drop")

1. If the target is to the right: anchor `leading`, else `trailing` (the side you leave stays fixed).
2. `stretch` → 1.35 with **easeOut 0.12 s** (drop scales x by `stretch`, y by `2 − stretch` = 0.65: stretches wide and flat).
3. Simultaneously `page = new` with **spring(response 0.38, dampingFraction 0.7)** → matched geometry slides the drop.
4. After 0.12 s: `stretch` → 1 with **spring(response 0.3, dampingFraction 0.55)** (snaps back with a small wobble).
5. Page content: old page out / new page in with **opacity + offset y 10 pt** (new page fades in and slides up 10 pt), container animation **easeOut 0.22 s**.
6. The page intro animations (rows, graph lines) replay on the new page, at **pace 0.4** (≈2.5× faster than at launch). Intro window: 1.2 s after launch/switch.
7. Low impact: all of this is disabled (`disablesAnimations`), no intro.

**Windows:** drop = a Border with CornerRadius 8 animated via Composition (`Vector3KeyFrameAnimation` on Scale + Offset, spring via `SpringVector3NaturalMotionAnimation` with DampingRatio ≈ 0.7, Period ≈ 0.38 s / 2π-ish). Page swap = `EntranceThemeTransition` with FromVerticalOffset 10, or a custom 220 ms fade+translate.

### 2.5 Always on top, hotkey, Dock icon, keep running

| Feature | Pref (default) | Behaviour |
|---|---|---|
| Always on top | `onTop` (false) | window level `.floating`; joins all Spaces and can sit next to full-screen apps (`canJoinAllSpaces`, `fullScreenAuxiliary`). Off restores the original behaviour. **Windows:** `OverlappedPresenter.IsAlwaysOnTop = true` |
| Global hotkey | `hotkeyCode` 3 / `hotkeyMods` 6400 / label "⌃⌥⌘F" | shows/hides Fusebox from anywhere; details §11.3 |
| Dock icon | `dockIcon` (true) | off = accessory app (no Dock icon, no app menu). **Rule: never both Dock icon off and menu bar style "off"** — if both, Dock icon is forced back on (the app would be unreachable) |
| Keep running | `keepRunning` (true) | closing the window does not quit; measuring and logging continue; only ⌘Q quits. Off = quitting after last window closes. **Windows:** close → hide to tray |
| Open at login | (system state, `SMAppService`) | toggle in Settings; re-registered once after the Meterkast→Fusebox rename |
| Notifications in foreground | — | banners + sound are shown even when Fusebox is frontmost |

### 2.6 Windows

| Window | id | Notes |
|---|---|---|
| Main | `main` | as above |
| Settings | SwiftUI `Settings` scene | §9 |
| Set up Fusebox | `setup` | size = content size; shown at launch when `setupDone == false`; follows appearance mode |
| Menu bar extras | up to 3 | §8 |

---
## 3. Live tab

Source: `Overzicht.swift` (page, layout tiers, card, graph, number animation), `Metrics.swift` (what each metric shows), `Detail.swift` (click-through detail screen), `Pieken.swift` (peaks), formatting helpers in `Meten.swift` / `Metrics.swift`, card shapes in `Theme.swift` (`ThemedRow`, `Spark`).

Page id `live`, tab title **"Live"** (never translated), tab icon SF Symbol `gauge.with.needle` → Segoe Fluent `Speed High` (U+EC4A). The app always opens on Live (see §2). Live has **its own remembered window size** (`liveSize`, default 300 × 340 pt) and **no minimum size of its own** (`Page.minSize = .zero`; the window-wide minimum 316 × 120 applies).

Live has **no explanatory text, no period buttons, no footer**: only labels, numbers and graphs. The graph period is always the "short" range (`shortMinutes`, default 5, clamped 1…60) across the full card width.

### 3.1 Page structure

```
┌ page (padding: 12 left/right, 0 top, 10 bottom) ───────────────┐
│ [advice line]   ← only if showAdvice && tier > XS; min 20 high, │
│                    4 pt below it                                │
│ ┌ grid: rows × cols of metric cards ────────────────────────┐  │
│ │ row spacing ("gap"): 0 for lines/plain row styles,         │  │
│ │   otherwise 3 (XS) or 6 (S+)                               │  │
│ │ column spacing: 10                                          │  │
│ └────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
```

* Rows share the available height equally (`maxHeight: .infinity` per row): there is never empty space at the bottom; cards stretch.
* Content minimum height: `rows × (17 + rowPad/2) + 10` (≈ the XS height).
* Cells are ordered row-major in the user's metric order (`metrics` pref, default `cpu,gpu,ram,net,ssd,battery`). `fan` is silently dropped on Macs without fans.
* In tiers XS/S, a missing cell at the end of the last row is filled with an invisible 1-pt spacer so numbers stay in columns. In M+ there is no filler: the last card(s) in a short row become wider.
* Each card keeps its identity per metric (`.id(metric)`), so when the layout changes a card never briefly shows another metric's value.
* Click anywhere on a card → detail screen for that metric (§3.8). Tooltip on every card: **"Click for details"**.

#### Advice line (optional, pref `showAdvice`, default **off**)

`HStack(spacing 6)`: a 7 × 7 pt filled circle in the status colour + one line of text, label font 12 pt, `theme.secondary`, single line. Text and colour from `Meter.status` (evaluated in this order, first match wins):

| Condition | Text | Dot colour |
|---|---|---|
| memory pressure ≥ 4 (critical) | "Memory is full — close a few apps" | red |
| hottest CPU temp ≥ warnTemp + 10 | "Your Mac is very hot — it's slowing itself down" | red |
| average CPU over last 30 s ≥ warnCPU | "Your Mac is busy" | orange |
| memory pressure == 2 | "Memory is filling up" | orange |
| temp ≥ warnTemp | "Your Mac is getting warm" | orange |
| otherwise | "All calm" | green |

(System red/orange/green, not theme colours.)

### 3.2 Layout tiers (`LiveLayout`)

Recomputed on every pixel of resize, but the view only re-renders when tier/cols/rows/side actually change.

Inputs: `size` = space below the tab bar; `n` = number of enabled metrics; `pad` = `theme.rowPad`; `advice` = showAdvice.

`rowPad` per row style: lines **15**, plain **10**, cards / blocks / glass **22**.

Algorithm (exact):

```
xl      = width >= 960 && height >= 610
grid    = width - 24
maxCols = clamp(floor(grid / 250), 1, 4)
rows    = max(ceil(n / maxCols), 1)
cols    = max(ceil(n / rows), 1)          // balanced, no surplus empty cells
c(t)    = (height - (10 + (advice && t > XS ? 24 : 0))) / rows - pad   // space per row
tier    = xl ? XL : c(L) >= 150 ? L : c(M) >= 70 ? M : c(S) >= 38 ? S : XS
side    = tier >= L && (grid - 10*(cols-1)) / cols >= 440
```

Examples: 6 metrics at 300 pt wide → 1 column × 6 rows. 774 × 400, 5 metrics → 3 cols × 2 rows. 1000 × 700, 6 metrics → XL, 3 × 2.

| Tier | When | What a card shows |
|---|---|---|
| **XS** | per-row space < 38 | One compact line: label 11 pt, number 15 pt (12 pt for NET/I/O), unit 11 pt. No graph. Vertical padding reduced by `inner/4` (negative padding: `inner = rowPad − (lines/plain ? 0 : 6)`). Peak dot possible. |
| **S** | ≥ 38 | One line, header min height 36 pt; label 13 pt; full-size number. No graph. Content vertically centred in the stretched row. Peak dot possible. |
| **M** | ≥ 70 | Header line + sparkline graph filling the rest (min 16 pt high). Hover on the graph shows time + value (no cause). Content aligned top. |
| **L** | ≥ 150 | Header + **facts** (up to 4 lines) above the graph, + peaks hover with cause and clickable peaks, + per-core bars always shown on CPU, + "Last peak" fact. |
| **XL** | width ≥ 960 and height ≥ 610 | As L, facts up to **9** lines, fact font 13.5 pt (L: 12.5), facts grid spacing 8 (L: 6). |

`side` (wide card ≥ 440 pt in L/XL) is computed and passed, but the current card always stacks facts above the graph (facts lay out in adaptive columns min 230 pt wide, 28 pt apart, so a wide card gets 2–3 columns of facts).

ASCII sketches:

```
XS / S — narrow default window (300 × 340), 6 metrics, 1 column
┌───────────────────────────────┐
│ CPU  42°  ▮▮▯▮            23% │   label · side text · (core bars if perCore) · number
│───────────────────────────────│   (lines style: 1-pt divider under each row)
│ GPU  38°                   4% │
│ RAM                     12.4GB│   ("ram" is absolute by default → value + unit)
│ NET 📶 18 ms      ↓8 ↑120 KB/s│
│ SSD                    412 GB │   ("ssd" absolute by default → free GB)
│ BAT 🔋 2:35               81% │
└───────────────────────────────┘

M — e.g. 300 × 700, 1 column
┌───────────────────────────────┐
│ CPU  42°                  23% │
│ ╱╲__╱‾‾╲___╱╲___ (sparkline) │
├───────────────────────────────┤
│ GPU …                          │
└───────────────────────────────┘

L / XL — e.g. 1000 × 700, 3 × 2 cards
┌──────────────────┐ ┌──────────────────┐ ┌──────────────────┐
│ CPU 42° ▮▮▯▮▮▮ 23%│ │ GPU 38°        4% │ │ RAM      12.4 GB │
│ In use  1.8 of 8  │ │ Temperature  38°  │ │ In use 12.4 of 16│
│ Last peak 14:32·X │ │ Low·avg·high 1·3·9%│ │ Pressure  normal │
│ Hottest core  42° │ │                   │ │ Swap      0.8 GB │
│ Xcode         61% │ │ 14:31  12%        │ │ Safari    2.1 GB │
│ ╱╲__╱‾‾╲___╱╲___ │ │ ___╱╲____________ │ │ ‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾‾ │
└──────────────────┘ └──────────────────┘ └──────────────────┘
(20-pt band above each graph holds the hover label "14:31  12%  Xcode 9%")
```

### 3.3 Metric card (`MetricView`) — appearance

Card container = `ThemedRow` with index `i` (index picks the colour in the Color blocks theme):

| Row style (theme) | Container |
|---|---|
| `lines` (Instrument, Minimal light) | vertical padding 7, 1-pt divider in `theme.line` along the bottom; no background |
| `plain` (Terminal) | vertical padding 5, nothing else |
| `cards` (Glass; also Liquid Glass in Low impact) | padding 10 h / 8 v, fill `theme.card`, corner radius 12, 1-pt stroke `theme.line` |
| `blocks` (Color blocks) | padding 10 h / 8 v, fill `blocks[index % 5]`, radius 10; all text white |
| `glass` (Liquid Glass) | padding 10 h / 8 v, continuous radius 14; fill = vertical linear gradient — dark: white 11 % → white 5 %; light: white 62 % → white 38 %; 1-pt inner border gradient — dark: white 28 % → white 6 %; light: white 95 % → black 6 %. **No real blur** on cards (only the tab bar is real glass). |

Card content: `VStack(alignment: leading, spacing: 2)`.

**Header line** — `HStack(alignment: firstTextBaseline, spacing: 6)`, min height 36 pt (except XS):

1. **Label** (`abbr`: CPU, GPU, RAM, NET, SSD, I/O, BAT, FAN) — label font (medium weight, theme design) 13 pt (XS 11). Colour: `theme.accent` in the Instrument theme, otherwise `onRow(secondary)`. Never truncated (`fixedSize`).
2. **Peak dot** (XS/S only, see §3.9).
3. **Thermal icon** (CPU only, when macOS reports `thermalState` > nominal): SF Symbol `thermometer.high` (Segoe: no exact glyph — use Fluent System Icons "Temperature"), 13 pt (XS 11) medium. Colour: critical (3) → `danger`, serious (2) → `warn`, fair (1) → `onRow(secondary)`. Tooltip: ≥2 "Your Mac is slowing itself down to cool off", 1 "Your Mac is warm". Updated by OS notification (no polling). Windows: no direct equivalent; could map to CPU throttling (`PROCESSOR_POWER_INFORMATION.CurrentMhz < MaxMhz`) or omit.
4. **Side icon** (NET: `wifi` (Segoe Wifi U+E701) when on Wi-Fi, else `cable.connector.horizontal` (Segoe Ethernet U+E839); BAT: `battery.100percent.bolt` (Segoe BatteryCharging9 U+E83E) while charging, else `battery.{0,25,50,75,100}percent` by level: ≥88→100, ≥63→75, ≥38→50, ≥13→25, else 0 (Segoe Battery0…Battery10 U+E850…E83F)). 13 pt (XS 11) medium; colour = side level colour or `onRow(secondary)`.
5. **Side text** — small text next to the label (temperature, ms, time left, fan mode), number font 13 pt (XS 11), colour by `sideLevel` (warn/danger) else `onRow(secondary)`. Animated with `ChangingText`.
6. **Per-core bars** (CPU, §3.6) — shown when tier ≥ L, or in any tier when pref `perCore` (default off) is on.
7. **Blocks-only warning glyph**: in the Color blocks theme, when `level > 0` a white `exclamationmark.triangle.fill` (Segoe Warning U+E7BA), caption size — because colours can't signal warnings on a coloured block.
8. Spacer (min 4).
9. **Value** — number font (theme weight, monospaced digits) at `valueSize`: **30 pt** for CPU/GPU/RAM, **24 pt** for NET/SSD/I/O/BAT/FAN; XS: 15 pt (12 pt for NET/I/O). Right-aligned in a fixed-width box sized by an invisible `template` string ("100%", or "88.8", "8888", "↓888 ↑888", "R 888 W 888", "8,888") so the layout never jumps (7 % → 17 %). If it doesn't fit, it shrinks to 50 % (`minimumScaleFactor 0.5`) rather than overflowing. Colour: level 2 → `danger`, level 1 → `warn`, else `onRow()` (blocks: always white). Layout priority 1.
10. **Unit** — fixed column right of the number (number font 14 pt, XS 11, `onRow(secondary)`), only when non-empty ("GB", "/8", "KB/s", "rpm").

**Below header** (M+): optional stats line (label 11 pt; unused on Live), then **facts** (L/XL) + **graph**, or just the graph (M).

**Facts block** (L/XL): `VStack(spacing 10) { facts; graph }`, facts = adaptive grid (min column 230, spacing 28, row spacing 6 / XL 8). Each fact line `HStack(spacing 6)`: optional app icon (size+3 square, i.e. 15.5/16.5 pt), label (label font 12.5/13.5, `onRow(secondary)`, 1 line), spacer ≥8, value (number font same size, `onRow()`, never truncated). A fact that names an app is a plain button with link cursor, tooltip "Open ‹app›", and click → switches to the **Processes** tab and opens that app's page (overrides the card tap). L shows first 4 facts, XL first 9 (most important first).

Colours: `onRow()` = `theme.text` (blocks: white); `onRow(true)` = `theme.secondary` (blocks: white 80 %).

### 3.4 Metrics — values, units, facts, thresholds, data sources

Formatting helpers (all use **en_US digits with a dot**, regardless of UI language):

| Helper | Rule | Examples |
|---|---|---|
| `pct(v)` | `Int(v.rounded()) + "%"`; integer rounding means **"0%" never "−0%"** | 23%, 0% |
| `one(v)` | exactly 1 decimal, en_US | 12.4 |
| `bytes(b)` | decimal (1000): `<1e6` → "N KB" (integer, truncated), `<1e9` → "x.x MB", else "x.x GB" | 640 KB, 12.3 MB, 1.2 GB |
| `rate(bps)` | `bytes(bps) + "/s"` | 640 KB/s |
| `short(bps, bits)` | bytes mode = rate(); bits mode: ×8, `<1 000 000` → "N kbit/s" else "x.x Mbit/s" | 5.1 Mbit/s |
| `ratePair(a,b,bits)` | one shared unit chosen by the larger value (×8 in bits mode): `<1e6` → KB/s or kbit/s, `<1e9` → MB/s or Mbit/s, else GB/s or Gbit/s; values integer, but **one decimal when unit is MB/GB (or Mbit/Gbit) and value < 10**; negatives clamp to 0 | ("8", "120", "KB/s"), ("1.2", "34", "MB/s") |
| `deg(t)` | `Int(t.rounded()) + "°"`, "" when unknown | 42° |
| `duration(min)` | ≥1440 → "Nd Nh"; ≥60 → "Nh Nm"; else "Nm" | 2h 35m, 1d 3h, 48m |
| `lvl(v, warn, danger)` | 2 if v ≥ danger, 1 if v ≥ warn, else 0 | |
| fan rpm | integer with en_US thousands comma | 1,850 |

Per-metric card content (`Metric.data` / `Metric.facts`):

| Metric (id · label · full title) | Value / unit | Side text | Level (value colour) | Graph | Facts (L/XL, in order) |
|---|---|---|---|---|---|
| `cpu` · CPU · "Processor (CPU)" | percent `23%`; **absolute** (if `cpu` in pref `absolute`): busy cores `one(cpu/100 × cores)` + unit `/8` (template "88.8") | hottest core temp `42°`, colour by `lvl(temp, warnTemp, warnTemp+10)` | `lvl(cpu, warnCPU, warnCPU+10)`; default warnCPU 85 → warn ≥85, danger ≥95 | per `cpuGraph`: `usage` (default, 0–100 %), `temp` (temperature line, scale 20…110 °C), `both` (usage + thin temp line mapped 20–110 °C onto 0–100 %). Temp is measured every 10 s; gaps filled with last known value. | "In use" = "1.8 of 8 cores"; "Hottest core" `42°`; *"Last peak" `14:32 · Xcode` (inserted at position 2, today's last peak)*; top 3 apps by CPU (icon, name, `61%`, clickable); "Low · avg · high" `12 · 23 · 78%` |
| `gpu` · GPU · "Graphics chip (GPU)" | `pct(gpu)` | GPU temp | `lvl(gpu, 90, 98)` (fixed thresholds) | per `gpuGraph` (same 3 modes) | "Temperature" `38°`; Last peak; "Low · avg · high" |
| `ram` · RAM · "Memory (RAM)" | percent; **absolute by default** (`absolute` default `ram,ssd`): `one(usedGB)` + "GB" | — | pressure: critical (≥4) → 2, high (2) → 1, else 0 | used GB over time, scale = installed RAM | "In use" = "12.4 of 16 GB"; "Pressure" critical/high/normal; "Swap" `0.8 GB`; Last peak; top 3 apps by memory (`2.1 GB`); "Low · avg · high" `11.9 · 12.2 · 12.8 GB` |
| `net` · NET · "Internet (NET)" | `↓8 ↑120` + unit `KB/s` (one unit, `ratePair`; bits if `netBits`) — template "↓888 ↑888" | `18 ms` only if pref `netPing` (default off) and ping host set and first ping answered ("– ms" if no answer); then `· 286 Mbit` if pref `netSpeed` (default off) and a speed test exists (latest download result from Network tab). Joined with " · ". Side colour `lvl(ping, warnPing, warnPing+10)` (default 100 ms). | 0 | two lines: download (main, filled) + upload (second, thin); auto scale (`top = nil` → max of both, min 1) | "Response time" `18 ms` / "no answer" (if host set); top app by network (only with `netPerApp`): "↓1.2 ↑0.1 MB/s"; "Downloaded" (bytes in period); "Uploaded"; "Peak" "↓x ↑y"; "Wi-Fi link" `286 Mbit/s`; "Wi-Fi signal" e.g. "Good · −52 dBm · ch 36" |
| `ssd` · SSD · "Free storage (SSD)" | percent **used**; **absolute by default**: free GB integer + "GB" (template "8888") | — | used ≥95 % → 2, ≥90 % → 1 | used GB over time, scale = disk size | "Free" `412 GB`; "Written today" (bytes, minute log of today); "Health" "Wear 3% · 41 TB written in total" (if known); "Size" `994 GB` |
| `io` · I/O · "Storage activity (read/write)" | `R 12 W 3` + one unit (`ratePair`, bits setting applies) | — | 0 | read (main) + write (second), auto scale | "Read" / "Written" (bytes in period); "Swap"; "Peak" "R x W y" |
| `battery` · BAT · "Battery" | `pct(battery)`; "–" if no battery | time from macOS: `2:35` (h:mm) or **`14h` when ≥ 600 min** (so it doesn't read as a clock time); only if pref `batteryTime` (default on). While charging it's time-to-full. | not charging and ≤10 % → 2, ≤20 % → 1 | battery % over time | charging: "Charging" "full in 1h 12m" (or "–"), charge-limit fact, "Charging at" `45 W`; on AC not charging: charge-limit fact, or "Charge limit" "holding at 80%" if < 99 %; on battery: "On battery" "2h 35m left" (+ " (learned)"), "Using" `9 W`; on AC also "Power adapter" `96 W` / "connected"; Last peak; "Low · avg · high"; last line (L/XL) = lowest Bluetooth device battery (e.g. "Magic Mouse  12%") |
| `fan` · FAN · "Fans (FAN)" (only on Macs with fans) | average rpm `1,850` + "rpm"; "–" if none | `auto`, or `curve` / `full` when Fusebox forces the fans (`fanMode` max → "full") | 0 | rpm, scale = highest fan max | "Fan 1" `1850 rpm`, "Fan 2"… (only if >1 fan); "Low · avg · high" (" rpm") |

Battery watt rules: watts = amperage (mA, signed) × voltage (mV) / 1e6. **When macOS reports amperage 0, no watts are shown at all** (instead of "0 W"); displayed as `abs(W)` rounded. Time to full: from macOS; if macOS has no estimate while charging, Fusebox computes it from the % rise over the last 5–15 min (needs >0.05 %/min). On battery with pref `learnedBattery` (default on) and ≥2 h of learned history, minutes come from the user's own drain rate (blended up to 50 % with the last 15 min) and get the suffix " (learned)".

Charge-limit fact (only if `chargeLimitOn`): "Charge limit" "80% · holding" / "Charging" "paused · battery warm" / "Charge limit" "80%".

Wi-Fi signal text: quality ≥ −55 "Excellent", ≥ −67 "Good", ≥ −75 "Fair", else "Weak"; format "Good · −52 dBm · ch 36" (uses a real minus sign; channel part only if known). Never shows SSID/BSSID/MAC. Cached max once per 10 s.

Data sources (sampling every **2 s**, or **5 s** in Low impact):

| Metric | macOS source | Windows equivalent (hint) |
|---|---|---|
| CPU total | `host_statistics(HOST_CPU_LOAD_INFO)` tick deltas | PDH `\Processor(_Total)\% Processor Time` or `GetSystemTimes` |
| CPU per core | `host_processor_info(PROCESSOR_CPU_LOAD_INFO)`; efficiency cores = `hw.perflevel1.logicalcpu` | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)`; E/P cores via `GetSystemCpuSetInformation` (EfficiencyClass) |
| CPU/GPU temp | SMC keys via IOKit (`AppleSMC`), every 10 s (30 s low impact), max of sensors | WMI `MSAcpi_ThermalZoneTemperature` (limited) or LibreHardwareMonitor |
| GPU | IOKit `IOAccelerator` → `PerformanceStatistics["Device Utilization %"]` | PDH `\GPU Engine(*)\Utilization Percentage` |
| RAM | `host_statistics64(HOST_VM_INFO64)`: (internal − purgeable) + wired + compressed pages; pressure `kern.memorystatus_vm_pressure_level` (1/2/4); swap `vm.swapusage` | `GlobalMemoryStatusEx`; pressure ≈ memory load % / commit; swap = pagefile usage |
| NET | `getifaddrs` byte counters on `en*` interfaces, delta/second, implausible jumps (>10 Gbit/s) dropped | `GetIfTable2` (physical adapters) |
| Ping | TCP connect time to `pingHost` (default 1.1.1.1), every 10 s while visible / 30 s hidden / 60 s low impact | `System.Net.NetworkInformation.Ping` or TCP connect timing |
| SSD | `volumeAvailableCapacityForImportantUsage` of `/`, once per minute, GB = 1e9 | `GetDiskFreeSpaceEx("C:\")` |
| I/O | IOKit `IOBlockStorageDriver` statistics (bytes read/written, delta) | PDH `\PhysicalDisk(_Total)\Disk Read/Write Bytes/sec` |
| Battery | `IOPSCopyPowerSourcesInfo` (capacity, charging, time to empty/full, AC), adapter watts, `AppleSmartBattery` Amperage × Voltage | `GetSystemPowerStatus`, WMI `BatteryStatus` (ChargeRate/DischargeRate mW) |
| Fan | SMC fan keys | LibreHardwareMonitor / vendor WMI (often unavailable → hide FAN) |
| Wi-Fi link/signal | CoreWLAN `transmitRate`, `rssiValue`, channel | `WlanQueryInterface` (wlan_intf_opcode_current_connection: rx/tx rate, signal quality) |
| Thermal state | `ProcessInfo.thermalState` notifications | none direct |

### 3.5 Graph (`SparkView` / `SparkNSView`)

Shown in tier ≥ M when pref `graphs` ≠ `never` (`auto` default = "Automatic (when the window is larger)", `always`, `never` → "Never"). (On Live, `auto` and `always` behave the same: the tier decides.)

Drawing (Core Animation layers, not SwiftUI, for CPU cost):

* Colour: `theme.accent` (Color blocks: white).
* **Area**: filled to the bottom with accent at **15 %** opacity.
* **Main line**: stroke accent, **1.5 pt**, round caps and joins.
* **Second line** (upload, write, temperature in "both" mode): stroke accent at **50 %**, **1 pt**, no fill.
* Scale: `top` (100 for %, installed RAM, disk size, fan max, 110 for temp) or, when nil, the max of both series (min 1). `bottom` 0 (20 for temp graphs).
* Shape: straight segments with lightly rounded corners — each interior vertex is replaced by a quadratic curve that starts/ends 20 % (`roundness 0.2`) along the adjacent segments, control point = the vertex. Not a full spline.
* Right-aligned: newest point on the right edge; points equally spaced over the width.
* Thinning: if there are more than 2 points per pt of width, the series is averaged down to `width` points (and it doesn't glide).
* No axes/labels on Live cards (axis label option exists but is nil).
* Graph min height 16 pt, fills the remaining card height. When peaks are enabled for the card (L/XL), a fixed **20-pt band** is reserved above the graph for the hover label.
* Clicks pass through the graph to the card (except on peaks in large cards).

Gliding after each new sample:
* Small graphs (tier < L) glide; large graphs (L/XL and the detail screen) jump each sample unless pref `smoothBig` (default **on**) is on — then they glide too.
* Glide duration **1.9 s** (slightly less than the 2-s sample interval), linear progress. On a "slide" (oldest point dropped, new added) the line moves left by one step; the new point starts just off the right edge, the old ones slide out to the left (2 trailing "lead" points kept so the left edge stays covered). On a "grow" (series still filling, e.g. after launch) the line stretches. Anything else → redraw instantly.
* Rendering steps are throttled: one redraw per **½ physical pixel** of movement, at most **15 per second**, min interval 1/15 s, max 0.5 s; if the scale changes during the glide, 15/s. All graphs share **one timer** (tolerance 20 ms), stopped when nothing glides.
* No glide (instant jump) when: Reduce Motion is on, the window is hidden/occluded, or Low impact is on.
* Windows hint: use a single `DispatchTimer`/`CompositionTarget` throttle shared by all graphs, draw with Win2D or a `Path` geometry; do not animate at display refresh rate.

Reveal (intro) animation: on launch/tab switch only (not on resize), the graph is clipped to a rectangle that grows from the right edge leftward (`drawn` 0→1), timing curve cubic-bezier(0.22, 1, 0.36, 1), duration `0.8 × pace`, delay `(0.05 + order × 0.25) × pace` (graphs appear one by one). If there is no line yet at launch (<2 samples) it waits and reveals once the second sample arrives. `pace` = 1 at launch, **0.4** after a tab switch; intro only plays within **1.2 s** of launch/tab switch and never in Low impact.

### 3.6 Per-core bars (`CoreBars`)

* One bar per logical core; efficiency cores first, then a gap before performance cores (extra leading padding 4 pt in the card header, 8 pt in the detail screen).
* Header version: fixed bar width **6 pt (L/XL)** or **4 pt** (smaller), spacing 2, height 18 (L/XL) / 14 (M/S) / 10 (XS), 4 pt left padding, baseline-aligned 1 pt above the label baseline. Tooltip "Per core: 4 efficiency + 4 performance".
* Detail version (`wide`): bars fill the width, spacing 3, height 54.
* Each bar: track = rounded rect (radius 1) accent 15 %; fill = accent, scaled vertically from the bottom by value/100 (clamped 0…100). Blocks theme: white.
* Animation: each new sample the bars move in **6 steps** on a global 1/16-s tick grid (`stepTick`), smoothstep easing `p²(3−2p)`. When the window is not on screen: jump.

### 3.7 Number animation (`ChangingText`) — pref `numberStyle`

Options: `roll` "Roll", `fade` "Fade" (**default**), `instant` "Instant".

* Until the first real sample arrives (`ready = false`) the text is empty (never a fake "0%").
* First real value: always a one-time "flip" — numeric content transition counting down (new digits slide in from above), `.snappy(duration 0.35)`, delay `0.1 + stagger/3`; flip mode ends after 0.6 s.
* **Roll**: numeric text transition (countsDown), snappy 0.35 s, each card delayed by `stagger = (index % 6) × 0.3 s`.
* **Fade**: old and new number stacked right-aligned in the same spot; cross-fade in **6 steps** on the shared 1/16-s grid with smoothstep (≈0.375 s); all numbers fade simultaneously (no stagger) so they share redraws; no dip to transparent.
* **Instant**: plain swap.
* Window hidden / Low impact: always instant.

Row intro (`IntroIn`): within 1.2 s of launch/tab switch, each card starts at opacity 0, offset y +14 pt and springs in: `spring(duration 0.35 × pace, bounce 0.2)`, delay `(0.03 + index × 0.04) × pace`. Disabled in Low impact.

Page switch transition (from §2): new page fades in and slides up 10 pt, easeOut 0.22 s.

### 3.8 Detail screen (`MetricDetail`) — click a card

Replaces the grid inside the same window (same size). Transition: opacity + scale from 0.97, `.snappy(duration 0.3)`. Back: chevron button or **Esc** (cancel shortcut), same animation.

```
┌──────────────────────────────────────┐
│ ‹  Processor (CPU)                    │  header (falls back to "CPU" if title doesn't fit)
│ 23%                              42°  │  big number 44 pt (26 for NET/I/O), side 18 pt
│ 1.8 of 8 cores busy                   │  other form, 14 pt
│ [ 5 min | 1 hour | Today ]            │  segmented picker
│ ┌──────────────────────────────────┐ │
│ │ (hover band 20 pt)               │ │  graph card, height max(110, 34% of view height)
│ │ ╱╲__╱‾‾╲___╱╲____                │ │
│ └──────────────────────────────────┘ │
│ ┌ Min ─┐ ┌ Average ┐ ┌ Max ─┐         │  3 stat cards, spacing 8
│ └──────┘ └─────────┘ └──────┘         │
│ ┌ Peaks ─────────────────────────────┐│
│ │ 14:32  8 s  CPU 94%     Xcode 61%  ││
│ └────────────────────────────────────┘│
│ [metric-specific cards…]              │
└──────────────────────────────────────┘
```

Layout: `ScrollView` → `VStack(alignment leading, spacing 12)`, padding 14 h / 14 bottom; clipped so it never scrolls under the transparent toolbar.

* **Header**: `HStack(spacing 8)`: back button (SF `chevron.left` → Segoe ChevronLeft U+E76B), 13 pt semibold, 26 × 26 hit area, borderless, tooltip "Back to Live (Esc)"; title = full metric title, or abbreviation if it doesn't fit on one line; label font 15 pt semibold, `theme.text`.
* **Headline**: `HStack(firstTextBaseline, spacing 6)`: main number (number font 44 pt, or 26 if `valueSize ≤ 16`, `onRow()`, numeric-text transition `.snappy`, shrink to 50 %); unit (18 pt, secondary); spacer; side text (18 pt, secondary). Second line (14 pt, secondary) = the other representation:
  * CPU: "23%" / "1.8 of 8 cores busy"
  * RAM: "12.4" + "GB" / "78% of 16 GB"
  * SSD: "412" + "GB free" / "58% used of 994 GB"
  * others: the card value/unit and its `detail` text (GPU: "temperature 38°"; NET: "internet response time" or "response time check is off"; I/O: "reading and writing to the SSD"; BAT: "full in 1h 12m" / "2h 35m left" / "charging" / "on power adapter" / "no battery"; FAN: "average of 2 fans" / "no fans").
* **Range picker**: segmented: "5 min" (actually "‹shortMinutes› min"), "1 hour", "Today". Default short range; when opened via a peak, chosen automatically: peak younger than short range − 10 s → short, < 3590 s → 1 hour, else Today. Data: short = last N minutes at 2-s resolution, 1 hour = all of `recent`, Today = per-minute averages since midnight + current partial minute.
* **Graph card** (`DetailCard` = theme row with padding 10): SparkView without gliding (re-drawn instantly when the range changes), `fill 1`; 20-pt hover band; full peak hover (§3.9). Selected peak highlighted as a vertical band: accent 22 %, width ≥4 pt, spanning the peak's start…end samples. Fewer than 2 points → centred "Collecting data…" (label 12, secondary). Height `max(110, viewHeight × 0.34)`.
* **Stats**: three equal cards "Min", "Average", "Max" (label 11 secondary over number 16 pt, shrink to 60 %), formatted per metric (% / "x.x GB" / rate / rpm / ° when the graph shows temperature); "–" when empty.
* **Peaks card** (for metrics that have peak kinds: CPU, GPU, RAM, I/O): title "Peaks" (label 11 secondary); "No peaks in this period" (12, secondary) when empty; up to 12 rows newest first, each a button: `HStack(spacing 8)`: time `HH:mm` (number 12), length (number 12 secondary: "8 s" — min "2 s" — or "5 min" from 120 s), value (CPU card prefixes the kind: "CPU 94%" / "Heat 97°"), spacer, cause "Xcode 61%" or "–" (label 12 secondary, middle-truncated). Row padding 2 v / 4 h; selected row background accent 18 %, radius 4. Click toggles selection (highlight in graph). Tooltip "Show in the graph".
* **Metric-specific cards** (`Facts` card = rows `HStack`: label 12 secondary … value number 13, middle-truncated, row spacing 7; "Top apps" card = title label 11 secondary + up to 5 rows of 16-pt icon, name label 12, value number 12 secondary; empty → "Available after the first minute"):

| Metric | Cards (in order) |
|---|---|
| CPU | Per-core card: "Per core · 4 efficiency + 4 performance" (or "Per core · 8 cores"), wide CoreBars 54 pt, per-core % under each bar (number 9). Facts: "Hottest core", "Load average (1 · 5 · 15 min)" ("2.1 · 1.8 · 1.6"), "Running since restart" ("3 d 4 h" when ≥1 day, else "4 h 12 min"), "Processes" (count of PIDs). Top apps "Busiest apps (last minute)" by CPU % |
| GPU | Facts "Load", "Temperature", "Processor temperature". Top apps "Most power-hungry apps (last minute)" "x.x W" |
| RAM | Facts "In use" "12.4 GB · 78%", "Free" "3.6 GB · 22%", "Installed" "16 GB", "Memory pressure" Normal/High/Critical, "Swap (memory on the SSD)". Top apps "Biggest apps (last minute)" "x.x GB" |
| NET | Facts "Download now", "Upload now", "Response time", "Downloaded (‹period›)", "Uploaded (‹period›)" (period = lowercase range title, e.g. "downloaded (1 hour)"), "Fastest download", "Fastest upload", "Wi-Fi link" |
| SSD | Usage bar card: "412 GB used · 582 GB free" + capsule 10 pt high, track accent 15 %, fill accent. Facts "Size", "Used" "x GB · y%", "Free", "Reading now", "Writing now", "Swap" |
| I/O | Facts "Reading now", "Writing now", "Read (‹period›)", "Written (‹period›)", "Fastest read", "Fastest write". Top apps "Busiest on the SSD (last minute)" rate |
| BAT | Facts "Charge", "Power" (Charging / On battery), "Full in"/"Time left" (+ " (learned from your use)"), "Health", "Charge cycles" "312 of ~1000", "Capacity" "91% of new" (health read once on open, in background). Top apps "Most power-hungry apps (last minute)" |
| FAN | Facts per fan "Fan 1" "1850 rpm (1200–5800)", "Control" "Fusebox" / "macOS (automatic)", "Processor temperature" |

### 3.9 Peaks

**Detection (`PeakTracker`, fed every sample):**

| Kind | Title | Metric card | Value | Threshold |
|---|---|---|---|---|
| `cpu` | "CPU" | CPU | CPU % | ≥ 80 % |
| `gpu` | "GPU" | GPU | GPU % | ≥ 80 % |
| `temp` | "Heat" | CPU | hottest core °C | ≥ warnTemp (default 95; 95 if unset) |
| `mem` | "Memory" | RAM | memory pressure level | ≥ 2 (high); shown as "high"/"critical" |
| `io` | "Disk" | I/O | read + write B/s | ≥ max(50 MB/s, 95th percentile of the last hour), recomputed every 30 samples |

* A peak needs **≥ 2 consecutive samples** above the threshold (1 for memory). A single sample below does not end it (gap 1); the 2nd sample below closes it. Lone 1-sample spikes are discarded.
* Peak records: start, end (= last above-sample + interval), top value and when.
* Cause: on the first sample above the threshold (max once per 30 s per kind) Fusebox takes an **app snapshot** (two process measurements 1 s apart). Cause = biggest app in that dimension (CPU → CPU %, Heat → CPU %, GPU → GPU %, Memory → GB, Disk → disk B/s) from the snapshot within 20 s of the peak's top moment; fallback the per-minute app measurement covering that time (next one within 70 s), else nearest minute within 90 s. "macOS itself" is never a cause. Cause text "Xcode 61%".
* Kept in memory: today + last hour; snapshots last hour. Closed peaks appended to `peaks-YYYY-MM-DD.csv` with header `tijd,eind,soort,hoogte,app,app_waarde` (start, end, kind id, top with 1 decimal, app name with commas → spaces, app value). Parsed by column name.
* Text helpers: "Peak 14:32 · 94% · Xcode"; length "8 s" / "5 min"; Checkup summaries "Xcode caused 2 of 4 CPU peaks today", "Xcode caused the GPU peak today", "Xcode caused all 3 CPU peaks today", "1 Heat peak yesterday", "6 CPU peaks today" (a name only when one app caused ≥2 or it was the only peak).

**On Live cards:**

* **Peak dot** (XS/S only): if the metric had a peak in the last 5 min, a 6 × 6 pt circle in `theme.warn` (blocks: white) next to the label, 3 pt padding hit area, centred slightly above the baseline. Tooltip = "Peak 14:32 · 94% · Xcode". Click → detail screen with that peak selected.
* **Hover on graph** (`PeakHover`, M and up): AppKit tracking area; on mouse move a **1-pt vertical line** in accent 55 % from below the 20-pt band to the bottom at the nearest sample, and a text label left-aligned in the band (no background card): time `HH:mm` (secondary, regular) + two spaces + value (text colour, **semibold**) + in L/XL/detail ("full") two spaces + cause (accent, semibold). Font = theme font at 12 pt (13 in full mode). NET value "↓x ↑y", I/O "R x W y". Updates live with new samples; hides on mouse exit.
* **Peak click** (full mode only: L/XL cards and detail): over a peak (±5 pt tolerance) the cursor becomes a pointing hand; click opens a transient popover anchored below the click point (and the card tap is suppressed). M-tier graphs don't capture clicks.
* L/XL tiers add the "Last peak" fact (§3.4). Peaks on cards appear only in L/XL (plus the dot in XS/S); M stays calm (hover only).

**Peak popover** (`PeakPopover`, width 380, padding 14, spacing 10):
* Title (label 14 semibold): "CPU peak 14:32 · 94% · 8 s".
* Subtitle (12, secondary): "Apps at that moment" or "Apps that minute (no snapshot at the moment itself)"; or "No app measurement near this moment".
* 2 × 2 grid (h spacing 18, v 10) of columns "CPU", "MEMORY", "DISK", "GPU" (uppercased label 11 semibold secondary), each 165 pt wide, top 3 apps: name (label 12, middle-truncated) … value (number 12, accent); "–" if none.

**Peak timeline** (used on Checkup, documented here because it lives in `Pieken.swift`): 16-pt canvas — centre line 1 pt `onRow(secondary)` 40 %, tick marks 1 × 8 pt (4 ticks = 0/6/12/18/24 h for one day, else one per day), each peak a rounded rect (radius 3, 10 pt high, min 5 pt wide) at 85 % opacity in its kind colour: CPU accent, GPU good, Heat warn, Memory danger, Disk secondary (blocks: white). Axis labels (number 9): "0:00 … 12:00 … 24:00" for one day, or weekday abbreviation (`EEE`, UI locale) … "today". Legend: 6-pt dots + kind titles (label 10), only kinds present.

### 3.10 Sustained-peak notification (`PeakAlert`)

* Prefs: `alert.peak.on` default **true**, `alert.peak.min` default **2** minutes; master switch `notify` (default true); per-app cooldown `alertCooldown` default **30** minutes.
* Checked once per minute (with the minute app measurement). For every running peak whose cause app is still running (not "macOS itself"): notify when `peak.seconds ≥ minutes × 60 − 10` (10 s slack) and that app wasn't notified within the cooldown.
* Title by kind: "‹App› is keeping your CPU busy" / "… your GPU busy" / "‹App› is heating up your Mac" / "‹App› is filling your memory" / "‹App› is keeping your disk busy".
* Body: "CPU 92% for 1 minute" / "CPU 92% for 3 minutes" (kind title, formatted top value, whole minutes).
* Actions: **"Stop app"** (destructive; graceful quit like ⌘Q, only offered when the app can be stopped — system processes never) and **"Show"** (foreground). Two categories: with both buttons, or "Show" only.
* "Show" or clicking the notification: switch to the Processes tab, open that app's page, unhide/activate the main window. Notifications also show as banners with sound while Fusebox is frontmost.
* Windows hint: Toast notification with two buttons (`ToastButton` with activation args `action=stop|show&app=…`).

### 3.11 Empty / special states

| Situation | Shown |
|---|---|
| Before the first real sample | numbers empty (no "0%"), graphs wait, then reveal |
| Fewer than 2 samples in detail graph | "Collecting data…" |
| No battery | BAT value "–", detail "no battery", no icon/time |
| No fans | FAN not offered at all; if forced into the list, dropped |
| Ping host empty | no ms on NET, no response-time fact; detail "response time check is off" |
| No speed test yet | no Mbit next to NET even with `netSpeed` on |
| Top apps before the first minute | "Available after the first minute" (detail) |
| No peaks | "No peaks in this period" |
| Temperature unknown | side text empty; facts show "–" |

### 3.12 Relevant prefs (defaults)

`metrics` "cpu,gpu,ram,net,ssd,battery" · `graphs` auto · `shortMinutes` 5 · `absolute` "ram,ssd" · `showAdvice` false · `netBits` false · `perCore` false · `batteryTime` true · `learnedBattery` true · `netSpeed` false · `netPing` false · `cpuGraph` usage · `gpuGraph` usage · `warnCPU` 85 · `warnTemp` 95 · `warnPing` 100 · `numberStyle` fade · `smoothBig` true · `pingHost` "1.1.1.1" · `netPerApp` false · `alert.peak.on` true · `alert.peak.min` 2 · `alertCooldown` 30.

---

## 4. Processes tab

Source: `Programmas.swift` (view), `Apps.swift` (measuring and grouping), `AppPagina.swift` (app page). Tab id `programs`, title **"Processes"**, tab icon SF `square.grid.2x2` (Segoe Fluent: `AllApps` E71D). The tab's minimum window size is **460 × 420**: switching to it grows the window to at least that size (see §2). The user can still drag the window smaller, and the page has a layout for every width.

### 4.1 Purpose and principles

- The list shows **one row per app, not per process**. An app and all its helper processes are added together (Chrome plus every "Chrome Helper", Safari plus its WebContent processes). Background daemons and macOS parts get plain-language names and a one-line explanation.
- Values are a **share of the whole Mac**: CPU 100% means all cores are fully busy (unlike Activity Monitor, where one full core is 100%).
- Measurements run **only while the tab is visible** and the window is on screen. When nobody is looking, nothing is measured.
- The page has no right-click context menu and no collapsible groups. The only disclosure is the **expandable row**: at most one row is open at a time.

### 4.2 Layout sketches

Narrow window (below 460 pt wide: Name, CPU and RAM columns only):

```
┌──────────────────────────────────────────────┐
│ CPU 23% · RAM 12.3 GB · 412 items            │  ← label 11, secondary
│ [All][Apps][Background][macOS]               │  ← filter chips (wrap to own row
│ [🔍 Search apps                    ⊗]        │     when the search field doesn't fit)
│                       Name      CPU▼    RAM  │  ← column header, label 10
│ ┌──────────────────────────────────────────┐ │
│ │[ic] Safari                  12%   1.2 GB ⊗│ │  ← row (icon 18, name 12)
│ │     ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬  ▬▬▬            │ │     CPU bar 3 pt under the %
│ ├──────────────────────────────────────────┤ │
│ │[ic] Spotlight (search)       4%   210 MB ⊗│ │
│ │     Indexes your files so searching is fast│ │  ← explanation, label 10
│ │  (i) Details   📁 Show in Finder  ⊗ Stop …│ │  ← expanded row (one at a time)
│ │  ? What is this? Indexes your files …     │ │
│ │  ▣ Spotlight is using 4% of your processor…│ │
│ │  ✓ Normal                                  │ │
│ ├──────────────────────────────────────────┤ │
│ │[] macOS itself               6%   3.1 GB  │ │
│ └──────────────────────────────────────────┘ │
│              Show all (187)                  │  ← accent link, label 11
└──────────────────────────────────────────────┘
```

Wide window (820 pt or more, `netPerApp` on):

```
┌──────────────────────────────────────────────────────────────────────────────────────────┐
│ CPU 23% · RAM 12.3 GB · 412 items                                                         │
│ [All][Apps][Background][macOS]  [🔍 Search apps                                        ]  │
│        Name                    CPU▼   GPU    RAM   Network    Disk  Energy Threads Proc. Running │
│ [ic] Safari                   12%     3%   1.2 GB ↓ 1.2 MB/s 640 KB/s 1.3 W  412    9   2h 13m ⊗│
│      ▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬▬ (90 pt)                ↑ 40 KB/s                                    │
└──────────────────────────────────────────────────────────────────────────────────────────┘
```

Split mode (1100 pt or more): the list on the left, a fixed **320 pt** detail pane on the right, with 12 pt between them.

```
┌──────────────────────────────────────────────────────────────┬──────────────────────┐
│ header · chips · search · columns · rows (no expanding)      │ [ic28] Safari        │
│ ▓ selected row: accent 12% background, radius 6              │ (i) Details 📁 ⊗ Stop│
│                                                              │ ▣ Safari is using …  │
└──────────────────────────────────────────────────────────────┴──────────────────────┘
```

### 4.3 Page structure, top to bottom

The outer container is an `HStack(alignment: .top, spacing: 12)` with padding of **12 pt** left and right and **10 pt** at the bottom. There is no top padding: the toolbar sits directly above. The left column is a `VStack(spacing: 8)`.

| # | Element | Appearance | Behavior |
|---|---|---|---|
| 1 | Summary line | Text "`CPU {pct} · RAM {x.x} GB · {n} items`", `theme.label(11)`, colored `theme.secondary`, leading-aligned, full width | CPU and RAM are the whole-Mac values from the latest sample. `n` is the number of rows (groups) before filtering |
| 2 | Filter chips + search | One row (`HStack spacing 8`: chips, then a search field at least 110 pt wide). If that doesn't fit, it switches (ViewThatFits) to a `VStack(alignment: .leading, spacing: 6)` with the chips above the search field | See §4.4 and §4.5 |
| 3 | Column header | Padding of **10 pt** left and right, except **0 pt** when the theme row style is `lines` or `plain` (so it lines up with the row contents) | Sortable, see §4.6 |
| 4 | ScrollView with a lazy list | Row spacing **0** for the `lines` and `plain` row styles, **6 pt** for `cards`, `blocks` and `glass` | Rows (§4.7), search sections, empty state, "Show all" link |
| 5 | Split detail pane (≥1100 only) | 320 pt wide, see §4.10 | |

Keyboard: **⌘F** focuses the search field (a hidden button carries the shortcut).

### 4.4 Filter chips (`procFilter`)

| Chip text | Stored id | Shows |
|---|---|---|
| "All" | `all` (default) | every group |
| "Apps" | `apps` | kind `app` (regular apps with a Dock icon) |
| "Background" | `background` | kind `background` (helpers, agents, command-line processes, menu bar apps) |
| "macOS" (never translated) | `macos` | kind `macos` (system parts and the "macOS itself" remainder row) |

- The chips sit in an `HStack(spacing: 2)` at its natural size (`fixedSize`).
- Each chip shows its text in `theme.label(11)` with padding of 7 pt left and right and 3 pt top and bottom.
- **Selected chip:** foreground `theme.accent`, background `theme.accent` at 15% opacity, rounded rectangle with radius **6**.
- **Unselected chip:** foreground `theme.secondary`, no background. There is no hover state and no animation; the change is instant.
- The starting filter is read from the setting `procFilter` each time the tab is opened (Settings › Processes). Choices made on the page are not saved.

### 4.5 Search field

- Layout: `HStack(spacing: 4)` with SF `magnifyingglass` (Segoe `Search` E721) in `theme.secondary`, then a plain text field with the placeholder **"Search apps"** in `theme.text`.
- While the field has text, a clear button appears: SF `xmark.circle.fill` (Segoe `Clear` E894, or a filled circle with `Cancel` E711) in secondary, tooltip **"Clear search"**.
- Font `theme.label(12)`. Padding 7 pt left and right, 3 pt top and bottom. Background `theme.line` at **50%** opacity, radius **6**. There is no border and no focus ring styling beyond the system's.
- **Matching:** case-insensitive "contains", on the group **name OR its explanation**.
- **While a search is active:**
  - The small-items filter (§4.8) is off: everything that matches is shown.
  - The list is split into two sections with headers (`theme.label(11).semibold`, secondary, leading-aligned, 4 pt top padding):
    - **"Running"**: only shown if there are running matches; these are the running groups.
    - **"Apps"**: **installed apps that are not running**, up to **30** results. Apps whose name starts with the search text come first, then apps that merely contain it. Apps that are already running (matched by name) are excluded.
  - Installed apps are scanned once, in the background, on the first keystroke. The scan covers `/Applications`, `~/Applications` and `/System/Applications`, plus one level of subfolders, and the result is sorted by name. (Windows: Start Menu `.lnk` files from `%ProgramData%` and `%AppData%\Microsoft\Windows\Start Menu\Programs`, plus AppX packages via `PackageManager`.)
- **Installed-app row (`InstalledRow`):**
  - Contents: `HStack(spacing: 8)` with the app icon (18 × 18), the name (`label(12)`, `onRow()`, one line, truncated at the end), a spacer, **"Not running"** (`label(10)`, `onRow(true)`), and SF `chevron.right` (10 pt, semibold, `onRow(true)`; Segoe `ChevronRight` E76C).
  - Wrapped in `themedRow`. Tooltip: "Usage over time and info about {name}".
  - Clicking it opens the **app page** (§4.11).

### 4.6 Columns, sorting and width bands

The visible columns depend on the **list width** (the whole page width) and on the setting `procHide`. `procHide` is a comma list of column ids that are switched off; the possible ids are `disk`, `energy`, `gpu`, `threads` and `running`. Name, CPU and RAM can never be hidden.

| Width band | From width | Columns added |
|---|---|---|
| narrow | < 460 | Name, CPU (58 pt), RAM (62 pt) |
| disk | ≥ 460 | + Disk (70 pt) |
| energy | ≥ 560 | + Energy (50 pt), GPU (44 pt); + Network (74 pt) **if** `netPerApp` is on |
| procs | ≥ 700 | + Threads (54 pt), Proc. (40 pt); the CPU column widens to **90 pt** (longer bar) |
| running | ≥ 820 | + Running (62 pt) |
| split | ≥ 1100 | the list plus a 320 pt detail pane (all columns above still apply) |

Order inside a row and in the header (`HStack spacing 8`):

`[icon 18] Name (flexible) | CPU | GPU | RAM | Network | Disk | Energy | Threads | Proc. | Running | stop (18)`

Column header details:

| Header text | Width | Align | Sort id | Tooltip | Notes |
|---|---|---|---|---|---|
| "Name" | flexible | leading, 26 pt left inset (the icon is 18 pt plus 8 pt spacing) | `name` | "Sort by name" | |
| "CPU" | 58, or 90 from 700 pt | trailing | `cpu` | "Sort by CPU" | |
| "GPU" | 44 | trailing | `gpu` | "Share of the graphics chip's time, like Activity Monitor's % GPU" | |
| "RAM" | 62 | trailing | `ram` | "Sort by RAM" | |
| "Network" | 74 | trailing | `net` | "Download ↓ and upload ↑, per second" | |
| "Disk" | 70 | trailing | `disk` | "Reading + writing to the SSD, per second" | |
| "Energy" | 50 | trailing | `energy` | "Sort by energy" | |
| "Threads" | 54 | trailing | `threads` | "Sort by threads" | |
| "Proc." | 40 | trailing | none (not sortable) | | Plain text, `label(10)`, secondary |
| "Running" | 62 | trailing | `running` | "Sort by running" | "More" means running longer |
| (blank) | 18 | | | | spacer above the stop buttons |

- **Header look:** `theme.label(10)`. The active sort column is drawn in `theme.text` with an SF `chevron.up` or `chevron.down` (8 pt, bold; Segoe `ChevronUp` E70E / `ChevronDown` E70D) 2 pt after the title. Inactive columns are `theme.secondary` with no chevron.
- **Click rules:**
  - Clicking the active column flips the direction.
  - Clicking another column selects it: ascending for Name, descending for every other column.
- **Starting sort:** the setting `procSort` (default `cpu`), read each time the tab is opened. It is descending, except `name`, which starts ascending.
- **Sort keys:**

  | Column | Sorted by |
  |---|---|
  | name | localized standard compare |
  | cpu | cpu |
  | ram | memGB |
  | energy | energyW |
  | gpu | gpu |
  | disk | read + write |
  | net | in + out |
  | threads | thread count |
  | running | start date (an earlier start counts as larger) |

  The monitor already delivers rows sorted by **CPU descending, ties by memory descending**, which is the default order.

### 4.7 Process row (`ProgramRow`)

**Structure.** A `VStack(spacing: 0)` holds the summary line and, if the row is expanded, the details. Settings on the whole row:

- Base font `theme.number(12)` (monospaced digits, theme weight), color `theme.onRow()`.
- Selected background (split mode only): `theme.accent` at **12%**, radius **6**. It is applied *inside* the themed row padding, so it hugs the content.
- Then `.themedRow(theme, index: i)`. In the `blocks` style each row gets the next block color in the cycle. Then `.clipped()`, so the expand animation doesn't spill out.

**Themed row styles** (from `Theme.swift`, applied to every row):

| rowStyle (themes) | Row chrome |
|---|---|
| `lines` (Instrument, Minimal light) | 7 pt vertical padding, 1 pt bottom separator in `theme.line`, no horizontal padding, no background |
| `plain` (Terminal) | 5 pt vertical padding only |
| `cards` (Glass) | padding 10 pt left and right, 8 pt top and bottom; fill `theme.card`; radius 12; 1 pt stroke in `theme.line` |
| `blocks` (Color blocks) | padding 10 / 8; fill `blocks[index % 5]`; radius 10; all text white (`onRow`: white, secondary white at 80%) |
| `glass` (Liquid Glass) | padding 10 / 8; continuous radius 14. Fill is a top-to-bottom gradient: dark mode white 11% → 5%, light mode white 62% → 38%. 1 pt stroke gradient: dark white 28% → 6%, light white 95% → black 6%. No blur |

**Summary line** (`HStack spacing 8`):

| Part | Spec |
|---|---|
| Icon | 18 × 18. For an app bundle, the app icon at high interpolation. For non-bundle processes, an SF symbol: `gearshape` for system processes (Segoe `Settings` E713), `terminal` for other background processes (Segoe `CommandPrompt` E756). The "macOS itself" row uses `apple.logo` (on Windows, use a Windows/system glyph) |
| Name block | `VStack(leading, spacing 1)`: the name in `label(12)`, `onRow()`; the explanation (if any) in `label(10)`, `onRow(true)`. One line each, truncated at the end; takes the flexible width |
| CPU | `VStack(trailing, spacing 3)`: the text `pct(cpu)`, then a **bar**. The bar is a capsule 3 pt high: track in barColor at 20%, fill in barColor at width `min(cpu/100, 1)` of the column. **barColor:** white in `blocks`; `theme.warn` when cpu ≥ 50%; otherwise `theme.accent` |
| GPU | `pct(min(gpu,100))` if gpu ≥ 0.5, else "–" (en dash) |
| RAM | `mem(GB)`: "1.2 GB" when ≥ 1 GB (one decimal, always a dot); otherwise "340 MB" (rounded, with GB = 1024³ and MB = GB × 1024) |
| Network | if in + out ≥ 1000 B/s, two lines "↓ 1.2 MB/s" and "↑ 40 KB/s", otherwise "–". Font `number(10)`, can shrink to 80% |
| Disk | read + write ≥ 1000 B/s: `rate()` ("640 KB/s", "12.3 MB/s", "1.2 GB/s"; decimal units, KB truncated to an integer); otherwise "–". Can shrink to 80% |
| Energy | ≥ 0.05 W: "1.3 W", otherwise "–" |
| Threads | > 0: the count, otherwise "–" |
| Proc. | the number of processes in the group, or "–" when it has none (the macOS remainder row) |
| Running | `duration(minutes since the earliest start, at least 1)`: "5m", "2h 13m", "3d 4h". "–" if unknown |
| Stop | Only if `canStop`. SF `xmark.circle`, 13 pt (Segoe `Cancel` E711 inside a circle, or `ErrorBadge` EA39). Idle: color `onRow(true)`, **opacity 0.35**. While the pointer is over the row: color `theme.danger` (white in `blocks`), **opacity 1**. Tooltip and accessibility label "Stop {name}". Otherwise an empty 18 pt slot |

**Interaction.**

- **Hover:** there is no row highlight. Only the stop button changes, as described above.
- **Clicking the summary** (outside split mode) toggles the expansion. A row that opens closes any other open row. Animation: `.snappy(duration: 0.25)`. Use a critically damped spring of about 250 ms, or a CubicEase-out of 250 ms in WinUI.
- **The details' transition:** opacity combined with a move from the top edge.
- **In split mode**, a click only selects the row (it does not toggle), and the row never expands inline. If nothing is selected, the first row counts as selected.

### 4.8 Small items (`procSmall`) and "Show all"

| Value | Label (Settings) | Hidden rows |
|---|---|---|
| `all` | show everything | none |
| `idle` (default) | hide idle | cpu < **0.05%** AND memory < **20 MB** |
| `light` | hide light | cpu < **1%** AND memory < **200 MB** |

- A row is kept if either CPU or memory reaches its threshold.
- If anything is hidden, a link-style button at the end of the list reads **"Show all ({n})"**. Once everything is shown, it reads **"Hide small items"**.
- The link is plain text in `theme.label(11)` and `theme.accent`, with 8 pt vertical padding.
- This choice is not saved; reopening the tab starts from the setting again.
- During a search the filter is skipped entirely.

### 4.9 Expanded details (`ProgramDetails`)

The details are a `VStack(leading, spacing 5)` in font `theme.label(11)` and color `onRow(true)`, with padding left **26** (aligned under the name), top **6** and bottom **2**.

**1. Button row**, at the top so the buttons don't jump when the lines below change. `HStack(spacing 12)`, plain buttons with SF icons, `label(11)`, 4 pt bottom padding:

| Button | Icon (SF → Segoe) | Shown when | Action |
|---|---|---|---|
| "Details" | `info.circle` → `Info` E946 | always | opens the app page (§4.11). Tooltip: "Usage over time, energy and info about {name}" |
| "Show in Finder" | `folder` → `Folder` E8B7 | the group id is a file path | reveals the bundle or executable in Finder (Windows: `explorer /select,`) |
| "Stop {name}" | `xmark.circle` → `Cancel` E711 | `canStop` | asks for confirmation (§4.9.1). Colored `theme.danger` (white in `blocks`) |

**2. Explanation lines.** Each line is a label: an SF icon in a 14 pt wide frame, then text that wraps. The lines appear in this order, each only when its condition holds:

| Icon (SF → Segoe) | Condition | Exact text |
|---|---|---|
| `questionmark.circle` → `Help` E897 | has an explanation | "What is this? {explanation}." |
| `cpu` → `Processing` E9F5 (closest) | always | "{name} is using {pct} of your processor right now (≈ {x.x} of the {N} cores) and {mem} of memory ({pct} of your {N} GB)." |
| `lock` → `Lock` E72E | macOS remainder row (no pids) | "macOS doesn't let apps see exactly which system parts these are. That's normal: this covers everything only macOS itself is allowed to measure." |
| `square.stack.3d.up` → `Library` E8F1 (closest) | has pids | 1 pid: "It's a single process." More than one: "It consists of {n} parts (processes), including {a, b, c}." (up to 3 unique process names); if no names could be read: "It consists of {n} parts (processes)." |
| `bolt` → `LightningBolt` E945 | energy ≥ 0.05 W | "Uses about {x.x} W — {light/moderate/heavy}." (< 1 W light, < 5 W moderate, otherwise heavy) |
| `cube.transparent` → a 3D or graphics glyph (closest) | gpu ≥ 0.5% | "Uses the graphics chip {pct} of the time." |
| `internaldrive` → `HardDrive` EDA2 | disk ≥ 1000 B/s | "Reading {rate} and writing {rate} on the SSD." |
| `network` → `Globe` E774 / `Network` E968 | net ≥ 1000 B/s | "Downloading {rate} and uploading {rate} over the network." |
| `arrow.triangle.branch` → a branch/flow glyph (closest) | threads > 0 | "Runs {n} threads (tasks at the same time)." |
| `clock` → `Clock` E917 | start time known | "Running for {duration}." |
| verdict icon | always, last line, colored | see the table below |

**Verdict** (first match wins; colors become white in `blocks`):

| Rule | Text | Icon (SF → Segoe) | Color |
|---|---|---|---|
| cpu ≥ 50% | "Heavy: uses a lot of processor" | `exclamationmark.triangle` → `Warning` E7BA | warn |
| not an app and cpu ≥ 10% | "Heavy for a background task" | `exclamationmark.triangle` | warn |
| memory ≥ 2 GB or ≥ 15% of RAM | "Uses a lot of memory" | `memorychip` → a memory glyph (closest) | warn |
| otherwise | "Normal" | `checkmark.circle` → `Completed` E930 | good |

#### 4.9.1 Stop confirmation and failure

| Dialog | Title | Message | Buttons |
|---|---|---|---|
| Confirm | "Stop {name}?" | "Unsaved work in {name} may be lost." | "Stop" (destructive), "Cancel" |
| Failure | "{name} couldn't be stopped" | "macOS doesn't allow it, or the app isn't responding." | "OK" |

- **How "Stop" works:** for a regular app (an `NSRunningApplication` with a regular activation policy), a polite terminate, like ⌘Q. Otherwise every pid in the group gets SIGTERM.
- **`canStop`:** false for the macOS kind, for groups without a positive pid, and for Fusebox itself.
- **Windows:**
  - Stop: `CloseMainWindow` / WM_CLOSE to the main windows first. For windowless processes, `TerminateProcess`, or tell the user a service can't be stopped.
  - Force: `TerminateProcess`.
  - Never offer Stop for "Windows itself", for protected processes, or for Fusebox itself.

### 4.10 Split-mode detail pane (≥ 1100 pt)

- A ScrollView without scroll indicators. The content is a `VStack(leading, spacing 4)`, full width, wrapped in `themedRow(index 0)`, so it looks like a single card or row in the theme.
- **Header:** `HStack(spacing 8)` with the app icon at **28 × 28** and the name in `theme.label(14)`, `onRow()`, up to 2 lines.
- Below the header comes the same `ProgramDetails` block as §4.9 (buttons plus lines).

### 4.11 App page (`AppPage`)

**What opens it:**

1. "Details" in an expanded row or in the split pane.
2. A click on an installed-app search result.
3. A click on a top-app fact in a big Live card (`meter.openApp`): this switches to Processes and opens the page.
4. "Show" in a sustained-peak notification (`PeakAlert.pendingPage`); this works even if Processes was already open.

**How it opens and closes:**

- The page **replaces the list inside the Processes tab**; no new window opens.
- Opening and closing animate with `.snappy(duration: 0.3)`. The page appears with opacity combined with scale from **0.97**, and the list fades.
- **Back:** the chevron button, or **Esc** (cancel action).

**Running detection:** a running group with the same id, or else a running group of kind app with the same name. This handles apps that run from another path, such as Safari.

**Layout:** a ScrollView (clipped) containing a `VStack(leading, spacing 12)` with padding **14 pt** left and right and **14 pt** at the bottom.

```
┌─────────────────────────────────────────────┐
│ ‹  [icon36]  Safari                          │  name label 15 semibold
│              Version 26.1 (21622) · Running for 2h 5m │  label 11 secondary
│      ↗ Bring to front  ⊗ Quit  ⚡ Force quit  📁 Show in Finder │ label 11 accent
│ Now                                           │  section title label 12 semibold secondary
│ ┌CPU──┐┌Memory┐┌Energy┐┌GPU──┐               │  adaptive grid, min 110, spacing 8
│ │ 12% ││1.2 GB││1.3 W ││ 3%  │               │  value number 16
│ └─────┘└──────┘└──────┘└─────┘               │
│ ┌CPU        12%┐┌Memory    1.2 GB┐            │  two spark cards, height 90
│ │ ~~~~~~~~~~~~ ││ ~~~~~~~~~~~~~~ │            │
│ └──────────────┘└────────────────┘            │
│ ┌ Processes 9 / Threads 412 / Disk read · write … ┐ │  Facts card
│ History                                       │
│ [ Today | 7 days | 30 days ]                  │  segmented control
│ ┌Average CPU┐┌Peak CPU┐┌Average memory┐┌Time open┐┌Average GPU┐ │
│ ┌ When you use it · by hour  ▁▂▅█▃▁ …  ┐     │  24 bars
│ │ By day of the week          ▃▅▂▁▆▇▂  │     │  7 bars (7 and 30 days only)
│ ┌ ⚡ About 1.2 Wh today (≈ 0.8 W while open) ┐ │
│ │ ◔ About 3% of your Mac's energy today     │ │
│ │ ▭ Small effect on your battery            │ │
│ footnote (label 10 secondary)                 │
│ About                                         │
│ ┌ Bundle ID … Opens at login ┐               │  Facts card
└─────────────────────────────────────────────┘
```

#### 4.11.1 Header

- **First row**, `HStack(spacing 10)`:
  - Back button: SF `chevron.left` (Segoe `ChevronLeft` E76B), 13 pt semibold, in a 26 × 26 hit area, borderless style. Tooltip: **"Back to Processes (Esc)"**.
  - Icon: 36 × 36. The running app's icon, or the file icon of the path.
  - Title block, `VStack(spacing 2)`:
    - The name in `theme.label(15).semibold`, `theme.text`, one line.
    - A status line in `label(11)`, secondary. It reads `"Version {v} · "` (only if the version is known) followed by either "Running for {duration}", "Running", or "Not running".
- **Action row**: indented 36 pt on the left, `HStack(spacing 12)`, plain buttons, `label(11)`, color `theme.accent`:

| Button | SF → Segoe | When | Action |
|---|---|---|---|
| "Open" / "Bring to front" | `arrow.up.forward.app` → `OpenInNewWindow` E8A7 | the path ends in `.app` | launches the app, or activates it if running ("Bring to front") |
| "Quit" | `xmark.circle` → `Cancel` E711 | running and `canStop` | confirmation: title "Quit {name}?", buttons "Quit" (destructive) / "Cancel" |
| "Force quit" | `bolt.horizontal.circle` → `Blocked` E733 (closest) | running and `canStop` | colored danger; confirmation: title "Force quit {name}?", buttons "Force Quit" (destructive) / "Cancel". Kills without saving |
| "Show in Finder" | `folder` → `Folder` E8B7 | a path is known | reveals the file |

Both confirmations share the message "Unsaved work in {name} may be lost.". On failure: "{name} couldn't be stopped" / "macOS doesn't allow it, or the app isn't responding." / "OK".

#### 4.11.2 "Now" (only while running)

- **Section title:** `theme.label(12).semibold`, secondary, 4 pt top padding.
- **Stat cards:** an adaptive grid with a minimum column width of 110 and spacing 8. Each card is a `DetailCard`: padding 10, full width, inside `themedRow`. The label is in `label(11)`, `onRow(true)`; the value in `number(16)`, `onRow()`, one line, can shrink to 60%.
  - "CPU": `pct`
  - "Memory": `mem`
  - "Energy": "x.x W", or "–" below 0.05 W
  - "GPU": `pct`, or "–" below 0.5%
- **Two spark cards** side by side (spacing 8, height **90**), "CPU" and "Memory":
  - Each card has a header line with the title (`label(11)`, `onRow(true)`) on the left and the current value (`number(11)`, `onRow()`) on the right.
  - Below that, a sparkline that auto-scales to its own maximum, uses the full card height, and jumps per sample instead of gliding.
  - The line is the theme accent, white in `blocks`.
  - The data is the last **300** samples (10 minutes at 2 s), collected only while the page is open.
  - Until there are 2 points, the card shows **"Collecting…"** in `label(11)`, secondary, centered.
- **Facts card:** rows with spacing 7. Labels in `label(12)`, `onRow(true)`; values in `number(13)`, `onRow()`, truncated in the middle.
  - "Processes": the count, or "–"
  - "Threads"
  - "Disk read · write": "{rate} · {rate}"
  - "Network ↓ · ↑": "{rate} · {rate}", only when `netPerApp` is on

#### 4.11.3 "History"

- **Segmented control:** "Today" (1), "7 days" (7), "30 days" (30). Stored in the setting `appPeriod` (default 1). Changing it reloads the history.
- **Loading:** a card with "Reading the logs…" (`label(12)`, secondary).
- **No data:** a card with "Not seen {today | in the last N days} — Fusebox logs open apps once a minute." in `label(12)`, `onRow(true)`.
- **With data:**
  - **Stat grid:** "Average CPU", "Peak CPU", "Average memory", "Time open" (as a duration), and "Average GPU" (only if the period has GPU data).
  - **Usage card**, `VStack(spacing 6)`:
    - "When you use it · by hour": 24 bars. The axis labels read "0", "6", "12" and "18". Tooltip per bar: "`{h}:00 – {h+1}:00 · {duration}`".
    - If the period is longer than 1 day, "By day of the week" (4 pt top padding): 7 bars, Monday first, labelled with the very short weekday symbols. Tooltip: "`{Weekday} · {duration}`".
    - **Bars:** `HStack(alignment: .bottom, spacing 2)`. Each bar is a rounded rectangle with radius 2, height `max(2, 44 × v / max)`. Fill is accent (white in `blocks`); a bar with value 0 uses the same color at 15%. The label under each bar is `label(9)`, `onRow(true)`, 3 pt below the bar. Total height is 60.
  - **Energy card** (`label(12)`, `onRow()`, label icons 14 pt wide):
    - SF `bolt`: "About {x.x} Wh {when} (≈ {x.x} W while open)". If below 0.1 Wh: "Hardly any energy {when}".
    - SF `chart.pie` (Segoe `PieSingle` EB05): "About {pct} of your Mac's energy {when}", only if the share is known.
    - SF `battery.75percent` (Segoe `Battery7` E857): battery words based on the average watts while open:

      | Average W | Text |
      |---|---|
      | < 0.5 | "Barely affects your battery" |
      | < 2 | "Small effect on your battery" |
      | < 6 | "Noticeable effect on your battery" |
      | otherwise | "Big drain on your battery" |

    - `{when}` is "today" or "in the last N days".
  - **Footnote** (`label(10)`, secondary). Newer log columns (GPU, energy share, full open time) only exist since a certain version:
    - No new columns in the period: "GPU, energy share and full open time: collecting from now on."
    - First new data later than 1 h after the period start: "GPU, energy share and full open time: collecting since {today HH:mm | d MMM}. Before that, only minutes when {name} was among the busiest apps count."
- **Data source:** the per-minute apps CSV logs (§4.13). Averages are taken over logged minutes. Energy in Wh is the sum of W per minute divided by 60. The share is this app's energy divided by the summed energy of all processes.

#### 4.11.4 "About" (only when a file path is known)

- While loading, a card shows "Looking it up…". The data is loaded in the background and cached per path for the session.
- The Facts card rows:

| Label | Value | Windows equivalent |
|---|---|---|
| "Bundle ID" | `CFBundleIdentifier` | AppX PackageFamilyName, or omit |
| "Version" | "1.2 (345)": the build number is added only if it differs | `FileVersionInfo.ProductVersion` |
| "Architecture" | "Universal (Apple silicon + Intel)", "Apple silicon", "Intel (runs via Rosetta)". While actually running under Rosetta: "Intel (running via Rosetta now)" | the PE machine type: "x64", "Arm64", "x64 (runs via emulation)" |
| "Size on disk" | allocated size of the bundle folder, decimal ("1.2 GB") | sum of the install folder |
| "Location" | parent folder, with the home folder shown as `~` | same |
| "Signed by" | "Apple" (certificates ending in "Software Signing"), "Mac App Store", the team name with prefixes such as "Developer ID Application: " removed, or "Not signed by a developer" | Authenticode signer via `WinVerifyTrust` |
| "Sandboxed" | "Yes" / "No" | AppContainer: Yes/No |
| "Installed" | Spotlight date added (abbreviated date) | `InstallDate` under the Uninstall registry key |
| "Last opened" | Spotlight last-used date | Prefetch or UserAssist if available, else "–" |
| "Opens at login" | "Yes (launch agent)" if any LaunchAgent or LaunchDaemon plist mentions the path or bundle id; otherwise "Unknown" | Windows *can* read the Run keys, the Startup folder and scheduled tasks, so show "Yes" or "No" |

- For non-`.app` paths, Bundle ID, Version, Architecture and Sandboxed are omitted.
- Tooltip on the card: "“Opens at login”: Fusebox can see launch agents, but not the Login Items list in System Settings."

### 4.12 Grouping and measuring (`AppMonitor`)

**Cadence:**

- One measurement per meter tick (**2 s**) while the Processes tab is shown and the window is visible.
- In **Low impact** mode, every **10 s** instead.
- A measurement also runs immediately when the tab opens, so the list is never empty for 2 s.
- A separate per-minute monitor (`minuteApps`) always runs. It feeds the log, Checkup, the peaks feature, the menu "heaviest" list, and Live top apps.

**Per process (macOS):**

| Value | How it is measured |
|---|---|
| CPU | (user + system time delta) ÷ elapsed time ÷ number of cores × 100 |
| Memory | `phys_footprint` |
| Energy | delta of `ri_energy_nj` ÷ elapsed time, giving W |
| Disk | delta of the bytes read and written |
| Threads | thread count |
| Start time | process start time |
| GPU | `accumulatedGPUTime` per client in the `IOAccelerator` registry |

If a counter is lower than last time (a reused pid), the process is treated as new and gets a delta of 0.

**Windows equivalents:**

| Value | Source |
|---|---|
| CPU | `GetProcessTimes` delta |
| Memory | `PROCESS_MEMORY_COUNTERS_EX.PrivateUsage` / private working set |
| Disk | `GetProcessIoCounters` |
| Threads | `NtQuerySystemInformation` / Toolhelp |
| GPU | PDH `\GPU Engine(pid_*)\Utilization Percentage`, summed per pid |
| Energy | Windows has no public per-process energy figure. Estimate it from CPU share × package power, or hide the column (`procHide` already supports `energy`) |

**Group key ("responsible app"):**

- The macOS responsible pid is resolved, and then its executable path. If the path contains `.app/`, the key is the path up to and including the first `.app`, so helpers inside the bundle join the app. Otherwise the key is the executable path, or `pid{N}`.
- Windows: walk the parent chain to the process that owns the top-level window. Alternatively, group by AppX package or by the install folder of the exe, so that browser children, Electron helpers and WebView2 fold into their app.

**Kinds:**

- An `.app` bundle is "regular" (kind **app**) unless its Info.plist has `LSUIElement`, or its path contains `/CoreServices/` or `/Library/Frameworks/`.
- A bundle under a system prefix (`/System/`, `/usr/`, `/sbin/`, `/bin/`, `/Library/Apple/`) that isn't regular becomes **macos**. Any other non-regular bundle becomes **background**.
- **Non-bundle processes** are named by the process name, with friendly names where known (table below). Their explanation is "Part of macOS" for system paths and "Background process" otherwise.

**Remainder row "macOS itself":**

- Processes that can't be read (other users, root) are skipped. Instead, one synthetic row is added:
  - id `macos`, name **"macOS itself"**
  - explanation "Drawing the screen, the kernel and system services only macOS is allowed to measure"
  - CPU = total CPU minus the measured sum; memory = used RAM minus the measured sum; energy 0; icon `apple.logo`
- Network traffic of pids not measured themselves is added to this row.
- Windows: use "Windows itself" with the same remainder logic. Typical contents are the System and Idle processes and protected services.

**Friendly names** (process-name prefix → display name, explanation). Show the explanation text as given; map the Windows analogues where they exist.

| Prefix | Name | Explanation |
|---|---|---|
| mds | Spotlight (search) | Indexes your files so searching is fast |
| mdworker | Spotlight (search) | Reads new files for Spotlight |
| corespotlightd | Spotlight (search) | Indexes content from apps |
| photoanalysisd | Photo analysis | Recognizes faces and objects in your photos |
| photolibraryd | Photo library | Manages your Photos library |
| mediaanalysisd | Media analysis | Analyzes photos and videos in the background |
| cloudd | iCloud | Syncs with iCloud |
| bird | iCloud Drive | Syncs your iCloud Drive files |
| fileproviderd | iCloud Drive | Keeps track of files in the cloud |
| backupd | Time Machine | Making a backup |
| softwareupdated | Software update | Checks for or installs updates |
| coreaudiod | Sound | Plays audio |
| bluetoothd | Bluetooth | Connects to Bluetooth devices |
| nsurlsessiond | Background downloads | Downloads for apps that aren't open |
| trustd | Security | Checks certificates |
| syspolicyd | Security | Checks whether apps are safe |
| XProtect | Virus scanner | Apple's built-in malware protection |
| WindowManager | Window management | Stage Manager and windows |
| sharingd | AirDrop and sharing | AirDrop, Handoff and sharing |
| locationd | Location | Determines your location for apps |
| Siri | Siri | Siri and suggestions |
| suggestd | Siri suggestions | Learns from your usage for suggestions |
| logd | Logging | Writes system messages to the log |
| spindump | Hang analysis | Investigates an app that froze |
| ReportCrash | Crash report | Handles an app that crashed |
| Finder | Finder | Files and desktop |
| Dock | Dock | The Dock and Mission Control |
| SystemUIServer | Menu bar | Icons in the menu bar |
| ControlCenter | Control Center | Control Center and menu bar |
| NotificationCenter | Notification Center | Notifications and widgets |

Suggested Windows set (same pattern):

| Process | Name | Explanation |
|---|---|---|
| SearchIndexer / SearchProtocolHost | Windows Search | Indexes your files so searching is fast |
| MsMpEng | Virus scanner (Microsoft Defender) | |
| OneDrive | OneDrive | Syncs your OneDrive files |
| TiWorker / TrustedInstaller / wuauserv | Windows Update | Checks for or installs updates |
| audiodg | Sound | |
| dwm | Drawing the screen | |
| explorer | File Explorer and taskbar | |
| svchost | Windows services | |

**Network per app (`netPerApp`, default off):**

- **macOS:** `nettop -P -L 1 -n -x -J bytes_in,bytes_out` runs in the background roughly every **≥ 3 s**, costing about 10 ms of CPU per run. The CSV is parsed from the right, so a comma inside a process name is safe. Rates are deltas divided by time; rates older than 10 s are dropped (150 s for the minute monitor). It is skipped entirely in Low impact mode.
- **Windows:** ETW `Microsoft-Windows-Kernel-Network` per pid, or `GetPerTcpConnectionEStats`.

**Searching installed apps:** see §4.5.

### 4.13 Apps log (per minute; it feeds History, Checkup and peaks)

- Written once a minute by the minute monitor, one file per day: `apps-YYYY-MM-DD.csv` in the log folder.
- Header (keep these exact column names for compatibility): `tijd,app,soort,cpu_pct,geheugen_gb,energie_w,gpu_pct,mac_energie_w`
- `soort` (kind) is one of `app`, `achtergrond` (background) or `macos`.
- Rows written: the top 10 by CPU, plus the top 10 by memory, plus every group of kind app (so that "Time open" is correct).
- Commas in names are replaced by spaces.
- `mac_energie_w` is the summed W of all groups that minute.
- Number formats: CPU `%.1f`, memory `%.2f`, energy `%.2f`, GPU `%.1f`.

### 4.14 Empty and edge states

- **No row matches** and there are no installed matches: **"Nothing found"** (`label(12)`, secondary, 20 pt vertical padding).
- **The list never starts empty**, because opening the tab triggers an immediate measurement.
- **GPU data unavailable** (other GPU or OS): every GPU value is 0, so it shows "–".

---


---

## 5. Checkup tab

Internal page id `advice` (enum case `Page.analysis`), title **"Checkup"** (never translated), tab icon SF Symbol `chart.bar.xaxis` (Segoe Fluent: `BarChartVertical` / `ReportDocument` U+E9F9). Minimum window size when switching to this tab: **420 × 460 pt** (window only grows, never shrinks, see §2).

Source files: `Analyse.swift` (page, report, score, tips, log, diagnosis, export, Pill/CardTitle/Headline/Capped building blocks), `Diagnose.swift` (Diag: leaks, runaways, macOS housekeeping, crash reports, battery health text, privacy scrubber), `Gezondheid.swift` (crash loops, cooling, `BackgroundSection`). Cards from `Opstart.swift`, `Accu.swift`, `Fans.swift` (`FanDust`) are documented in **§5.B**; here only their placement is given.

Purpose: look back at the CSV logs Fusebox writes every minute (today / yesterday / 7 days), give a **health score 1–10**, concrete **tips**, and a live **"Why is my Mac slow?"** diagnosis. All heavy work (reading files, `ps`, `tmutil`, crash reports, battery registry) happens off the main thread and only when the page opens, the period changes, ⟳ is pressed, or "Check now" is pressed. Nothing on this page runs on a timer.

---

### 5.1 Layout sketches

Narrow (window < 620 pt wide → 1 column):

```
┌───────────────────────────────────────────────┐
│ ● ● ●   [◉][▦][▮▮][⌗][▭]  [⚙]                 │ ← tab bar (§2)
├───────────────────────────────────────────────┤
│ [ Today | Yesterday | 7 days ]          ⟳    │ ← segmented picker (small) + reload
│ ┌───────────────────────────────────────────┐ │
│ │ 8 /10   Health                            │ │ ← score card (row style of theme)
│ │         Calm: not busy, memory and temp…  │ │
│ │         ↑ better than yesterday (6)       │ │
│ └───────────────────────────────────────────┘ │
│ ───────────────────────────────────────────── │ ← 1 pt theme.line
│ ◔  Performance                             ⌄ │ ← group header (expanded, chevron 90°)
│    1 tip · Xcode busiest · 1 busy moment · 3 peaks
│   TIPS                                        │
│   ┌ 💡 Xcode uses 3.2 GB on average   [Stop]┐ │
│   └ Close windows and documents you're…     ┘ │
│   ┌ 🔍 Why is my Mac slow?      (Check now) ┐ │
│   └ …diagnosis text…                        ┘ │
│   ┌ ▣ Top 5 CPU ─────┐  (side by side if it fits,
│   │ Xcode      42%   │   otherwise stacked)
│   │ ▬▬▬▬▬▬▬  1h 5m  │                       │
│   └──────────────────┘ ┌ ▣ Top 5 RAM ──────┐ │
│   ┌ 〰 Peaks ─────────────────────────────┐  │
│   │ 3  peaks today                        │  │
│   │ ─┼──■──┼────■■─┼─────┼─  (timeline)   │  │
│   │ 0:00        12:00          24:00      │  │
│   │ ● CPU  ● Heat                         │  │
│   └───────────────────────────────────────┘  │
│   ┌ 🔥 Busy moments ┐  ┌ ⚙⚙ What macOS is doing ┐
│ ───────────────────────────────────────────── │
│ 🔋 Battery & power                         › │ ← collapsed
│    lost 3% while asleep · 91% capacity · 212 cycles
│ ───────────────────────────────────────────── │
│ ⚠ Stability                                › │
│ ───────────────────────────────────────────── │
│ 🖴 Startup & storage                        › │
│ ───────────────────────────────────────────── │
│ ⟲ History & export                         › │
│                                               │
│      (Share report…) (Export CSV) (Export PDF)│ ← only if History group absent
└───────────────────────────────────────────────┘
```

Wide (window ≥ 620 pt → inside expanded groups content uses **2 equal columns**, 12 pt gap, top-aligned; each column is a VStack with 10 pt spacing):

```
│ ◔ Performance                                                     ⌄ │
│   [tips…full width]                                                 │
│   [Why is my Mac slow? …………… full width …………………… (Check again)]      │
│   [Top 5 CPU ……………………] [Top 5 RAM ……………………]   (ViewThatFits)        │
│   ┌ Peaks (left column) ─────────┐ ┌ Busy moments (right) ────────┐ │
│   │ …                            │ │ …                            │ │
│   └──────────────────────────────┘ ├ What macOS is doing ─────────┤ │
│                                    └──────────────────────────────┘ │
```

The page is a vertical `ScrollView` with **scroll indicators hidden** and content clipped (must not scroll under the transparent toolbar). Outer padding: **12 pt left/right, 12 pt bottom, 0 top**; root VStack spacing **12 pt**.

---

### 5.2 On-screen order (complete)

| # | Element | Shown when | `checkupHide` id | Notes |
|---|---|---|---|---|
| 1 | Period picker + reload button | always | – | §5.3 |
| 2 | Health score card | report loaded and `score` not hidden | `score` | §5.5 |
| 2b | "Why is my Mac slow?" card (temporary position) | only while the first report is still loading (`report == nil`) | – (always) | the button is usable immediately |
| 2c | Loading spinner | `report == nil` | – | small `ProgressView`, centered, 20 pt padding |
| 3 | Group **Performance** | any of `tops`,`busy`,`peaks`,`background` visible | – | default open |
| 3.1 | Tips of category `perf` | `tips` visible and ≥1 tip | `tips` | |
| 3.2 | "Why is my Mac slow?" | always | – | cannot be hidden |
| 3.3 | Top 5 CPU + Top 5 RAM | | `tops` | side by side if they fit, else stacked |
| 3.4 | Left col: Peaks | | `peaks` | |
| 3.5 | Right col: Busy moments | | `busy` | |
| 3.6 | Right col: What macOS is doing (`BackgroundSection`) | | `background` | |
| 4 | Group **Battery & power** (title "Power" on Macs without battery) | any of `battery`,`drain`,`sleep`,`awake`,`health` visible AND (Mac has battery OR `awake` visible) | – | battery-only ids (`drain`,`sleep`,`health`) are auto-hidden on Macs without battery |
| 4.1 | Tips of category `power` | | `tips` | |
| 4.2 | Left col: **Battery health** card (§5.B, `BatteryHealthSection`) — or, if `health` is hidden, the simple **Battery** card (§5.6.6) | | `health` / `battery` | Battery health already shows capacity+cycles+trend, so the old card shows only as fallback |
| 4.3 | Left col: **Battery drain** (§5.B, `DrainSection`) | battery Mac | `drain` | |
| 4.4 | Right col: **Sleep drain** (§5.B, `SleepDrainSection`) | battery Mac | `sleep` | |
| 4.5 | Right col: **Keeps awake** (§5.B, `AwakeSection`) | | `awake` | |
| 5 | Group **Stability** | `crashes` visible | `crashes` | |
| 5.1 | Tips of category `stability` | | `tips` | |
| 5.2 | Crashes card (§5.6.7) or "None" card | | | |
| 6 | Group **Startup & storage** | any of `startup`,`disk`,`ssdBench` visible | – | |
| 6.1 | Tips of category `storage` | | `tips` | |
| 6.2 | Left col: **Startup items** (§5.B, `StartupSection`) | | `startup` | |
| 6.3 | Right col: **Disk space** (§5.B, `DiskSection`) followed by **SSD health** (§5.B, `SSDHealthCard`) | | `disk` (controls both) | |
| 6.4 | Right col: **SSD speed test** (§5.B, `SSDSpeedCard`) | | `ssdBench` | |
| 7 | Group **History & export** | `runtime` or `log` visible | – | |
| 7.1 | Tips of category `history` (none exist today; category list never returns it) | | | |
| 7.2 | Left col: **Running time** (§5.6.5) | | `runtime` | |
| 7.3 | Right col: **Log** (§5.6.9) | | `log` | |
| 7.4 | Export bar | always inside this group | – | |
| 8 | Export bar (standalone, after all groups) | only if the History group is absent | – | §5.9 |

Order of ids in Settings › Checkup › Sections (chips, same as page order): `score` "Health score", `tips` "Tips", `tops` "Top apps", `peaks` "Peaks", `busy` "Busy moments", `background` "macOS background", `battery` "Battery", `health` "Battery health", `drain` "Battery drain", `sleep` "Sleep drain", `awake` "Keeps awake", `crashes` "Crashes", `startup` "Startup items", `disk` "Disk space", `ssdBench` "SSD speed test", `runtime` "Running time", `log` "Log". Footer text: *"\"Why is my Mac slow?\" is always there. A category with all its sections off disappears. Exported PDFs always contain everything."*

A group with none of its sections visible disappears entirely (header included).

Note on `index`: every card calls `themedRow(theme, index: n)`; the index only matters for the **Blocks** row style (it picks `theme.blocks[index % count]` as background color). Indices used: score 0, why 0, Top CPU 1, Top RAM 2, Running time 3, Busy 4, Peaks 4, tips 5+i, Battery 7, Crashes 8 / "None" crashes 8, Startup 9, Disk 10, SSD health 11, Background 11, Drain 12, SSD speed 12, Sleep 13, Awake 14, Battery health 15, Log 6+tipCount.

---

### 5.3 Period picker and reload

| Property | Value |
|---|---|
| Control | Segmented picker, `controlSize(.small)`, labels hidden, fills available width |
| Segments | "Today" (`today`), "Yesterday" (`yesterday`), "7 days" (`week`) |
| Persisted | `analysisPeriod` (last chosen period) |
| Reload | plain button with SF Symbol `arrow.clockwise` (Fluent `Refresh` U+E72C), color `theme.secondary`, tooltip "Reload"; while loading: opacity **0.4** and disabled |
| Row | HStack spacing 8 pt |

**Starting period** (`checkupStart`, default `"last"`): on page appear, if `checkupStart` is `today`/`yesterday`/`week`, the picker is forced to it; `"last"` keeps the last used `analysisPeriod`. Settings › Checkup › "When you open Checkup": segmented "Last used" / "Today" / "Yesterday" / "7 days".

**Period definitions**

| Period | Days read (0 = today) | Compared with ("previous") | `previousName` | `when` phrase |
|---|---|---|---|---|
| Today | [0] | [1] | "yesterday" | "today" |
| Yesterday | [1] | [2] | "the day before" | "yesterday" |
| 7 days | 6…0 | 13…7 | "the week before" | "in the last 7 days" |

**Caching / loading**: a static per-period cache keeps the last `Report`; when you come back to the tab or switch period, the cached report shows instantly, then a background (`utility` priority) reload replaces it. Per-day parse results are cached by file modification time, so normally only today's files are re-read. The today-report is also stored in `Analysis.today` for the menu bar panel (`CheckupGist`, §5.10).

---

### 5.4 Data source: CSV logs (how the analysis works)

Log directory = `meter.logDir` (see §11 data retention). Per day three files:

| File | Content | Columns used by Checkup (by header name; old logs without some columns still work) |
|---|---|---|
| `meterkast-YYYY-MM-DD.csv` | main log, one row per 2-s sample | `tijd` (first column, `yyyy-MM-dd HH:mm…`), `cpu_pct` (required), `ram_gb`, `geheugendruk` (memory pressure 1/2/4), `temp_c`, `schijf_vrij_gb`, `schijf_totaal_gb`, `ping_ms`, `wifi_dbm` (0 = Wi-Fi dropped, negative = signal, empty = cable/none), plus `ventilator_pct` (fan, used by FanDust) |
| `apps-YYYY-MM-DD.csv` | one row per app per minute | `tijd`,`app`,`soort` (kind: `app`/`macos`/…),`cpu_pct` (% of whole Mac),`geheugen_gb`,`energie_w`, optional `gpu_pct`,`mac_energie_w`. Without header: fixed order tijd,app,soort,cpu_pct,geheugen_gb,energie_w. App name `macOS zelf` with kind `macos` is renamed "macOS itself". |
| `peaks-YYYY-MM-DD.csv` | peaks (see Live §3 / Pieken) | parsed by `Peaks.parse` |

Malformed lines are skipped; `nan`/`inf` never crash.

**Minute aggregation (main log)**: samples grouped by the first 16 chars of the timestamp (`yyyy-MM-dd HH:mm`). Per minute: mean CPU, mean RAM GB, **highest** pressure, mean temperature (only non-empty), last disk free/total, mean ping; `offline = true` if the ping column exists but fewer than half of the samples in that minute have a ping; `wifiDrop = true` if any sample had `wifi_dbm == 0`; mean of negative Wi-Fi values.

**Apps aggregation**: per app sums of cpu/mem/energy and minute count; per-hour histogram (24 buckets); highest minute (peak CPU, peak memory); if the same name appears several times in one minute (e.g. Spotlight = mds + mdworker) values are added. Per "busy minute" (main CPU ≥ 85%) the non-macOS apps' CPU is recorded, to name the culprit.

**Constants**

| Constant | Value |
|---|---|
| busy CPU | **≥ 85 %** |
| hot | **≥ 95 °C** |
| slow ping (log event) | **≥ 200 ms** |
| weak Wi-Fi | **< −75 dBm** |
| disk almost full | free **< 10 %** of total (total from `schijf_totaal_gb`, else current volume capacity) |
| memory pressure "filling up" / "full" | pressure ≥ 2 / ≥ 4 |

**Report fields** computed per period: measured minutes; busy minutes; pressure minutes; hot minutes; Wi-Fi drop count (consecutive drop minutes = one drop); weak Wi-Fi minutes; last disk free/total; score + reason; previous period score; per-app averages (cpu/mem/energy per minute seen); top lists; busy moments; peaks; events; log files with sizes; leaks; runaways; housekeeping minutes; crash report counts; battery; boot date; dusty flag (§5.B FanDust).

**Top lists** (max 5 each): only "solid" apps, i.e. seen at least `min(5, max(measuredMinutes/20, 1))` minutes.
- Top CPU: by average CPU. Top RAM: by average memory. Top energy (report only): energy > 0, by average W.
- "Seen active longest": non-macOS apps by minutes seen (all apps, not only solid).

**Windows equivalent**: same CSV approach; per-minute app sampling via `Process`/PDH counters or `NtQuerySystemInformation`.

---

### 5.5 Health score card

**Score algorithm (1–10)**: start at 10, subtract (share = fraction of measured minutes):

| Deduction | Formula | Cap | Reason string (first letter capitalised when shown) |
|---|---|---|---|
| CPU busy (≥ 85 %) | share × 20 | 4 | "\(pct) of the time the CPU was busy (≥ 85%)" |
| Memory pressure ≥ 2 | share × 15 | 3 | "\(pct) of the time memory was (almost) full" |
| Hot ≥ 95 °C | share × 10 | 2 | "\(pct) of the time your Mac was hot (≥ 95 °C)" |
| Disk < 10 % free (last measurement) | 1 | 1 | "your disk is almost full" |

Score = `max(1, round(10 − Σ))`. Reason = the largest deduction if it is **≥ 0.25**, otherwise **"Calm: not busy, memory and temperature fine"**. No minutes → score `nil`, reason **"No measurements for this period yet"**. `pct` = integer percent, e.g. "12%".

**Layout** (`themedRow`, index 0): HStack, center-aligned, spacing 10 pt:

| Part | Font | Color |
|---|---|---|
| Score number ("–" if nil) | `theme.number(40)` (theme font family, theme weight, monospaced digits, × textScale) | ≥ 8 `theme.good`, 5–7 `theme.warn`, ≤ 4 `theme.danger`, nil `theme.secondary`; Blocks style: white |
| "/10" | `theme.number(14)`, baseline-aligned with the number, 2 pt gap | `onRow(true)` (secondary; white 80 % on blocks) |
| "Health" | `theme.label(14)` semibold | `onRow()` |
| reason | `theme.label(12)`, wraps | `onRow(true)` |
| comparison (only if both scores exist) | `theme.label(11)` | `onRow(true)` |

Comparison texts: "↑ better than \(previousName) (\(p))", "↓ worse than \(previousName) (\(p))", "= same as \(previousName)". Text VStack spacing 3 pt; trailing spacer.

---

### 5.6 Cards inside the groups

#### 5.6.0 Shared building blocks

**Row style (`themedRow`)** — every card is wrapped in the theme's row style:

| Row style (theme) | Padding | Background | Border |
|---|---|---|---|
| `lines` (Instrument, Minimal light) | vertical 7 | none | 1 pt `theme.line` bottom rule |
| `plain` (Terminal) | vertical 5 | none | none |
| `cards` | h 10, v 8 | `theme.card`, radius **12** | 1 pt `theme.line` stroke, radius 12 |
| `blocks` | h 10, v 8 | `theme.blocks[index % n]`, radius **10** | none; all text/icons white (secondary white 80 %) |
| `glass` (Liquid) | h 10, v 8 | continuous radius **14**, vertical gradient: dark `white 11 % → white 5 %`, light `white 62 % → white 38 %` | 1 pt gradient strokeBorder: dark `white 28 % → white 6 %`, light `white 95 % → black 6 %`. No blur (real glass only on the tab bar). |

**Tone** (`theme.tone(t, icon:)`): 0 = normal (icons `accent`, headlines `text`), 1 = attention (`warn`), 2 = serious (`danger`), 3 = good (`good`). Blocks style: always white.

**CardTitle**: HStack spacing 6. Icon: SF Symbol, `system 11 × scale` semibold, frame width 16, color `tone(tone)`. Title: `theme.label(12)` semibold, `onRow()`.

**Headline**: VStack spacing 1, bottom padding 2. Big: `theme.number(22)`, color `tone(tone, icon: false)`, 1 line, min scale 0.7. Small (optional): `theme.label(11)`, `onRow(true)`, wraps.

**Capped list**: shows first `limit` (5) rows; if there are more than limit + 1 items, a plain button "Show all (N)" / "Show less" toggles (font `label(11)` semibold, color `tone(0)` = accent, top padding 1). With exactly 6 items all 6 show (no button for a single hidden row). In PDF export: no limit.

**Item row** (name + detail): HStack first-baseline, spacing 6: name `label(12)` `onRow()` 1 line tail-truncated; spacer ≥ 4; detail `number(10)` `onRow(true)` 1 line, min scale 0.8.

**Line row** (label + value): label `label(11)` `onRow(true)`; value `number(11)` `onRow()`, min scale 0.8.

**Section header** (only "TIPS"): uppercased, `label(10)`, `theme.secondary`, top padding 4.

**Pill button** (theme-colored small button, replaces standard macOS buttons): label `system 11 semibold`, 1 line, color = given color; padding h 9, v 3; **Capsule stroke 1 pt at color × 0.6 opacity**, no fill; pressed → opacity 0.55. Hit area = capsule.

#### 5.6.1 Tips list

Header "TIPS" then one row per tip (`themedRow` index 5+i), HStack center, spacing 8:
- Icon: `exclamationmark.triangle.fill` (Fluent `Warning` U+E7BA) if serious, else `lightbulb` (Fluent `Lightbulb` U+EA80); `system 13 × scale` semibold, width 18, color `tone(1)` (warn) if serious else `tone(0)` (accent).
- Text: tip split into **head** (bold, `label(12)` semibold, `onRow()`) and **rest** (`label(11)`, `onRow(true)`): split at the first " — ", else at the first ". " (head keeps the period). Rest gets its first letter capitalised.
- Trailing action: Stop → Pill "Stop" in `danger` (white on blocks), tooltip "Stop \(name)"; Open → Pill with the action label in `onRow()`.
- **Serious** = id prefix in `loop`, `jetsam`, `disk`, `mem`, `hot`, `battery`, `leak`.

Tips are not shown as one list on the page: each group shows **its own tips at the top when expanded**, and the count ("1 tip"/"N tips") is prepended to that group's summary. Category by id prefix: `loop`,`crashed`,`stopped`,`jetsam`,`wifidrop`,`wifiweak` → Stability; `battery` → Battery & power; `disk`,`login`,`orphan` → Startup & storage; everything else (leak, runaway, app…, mem, hot, dust, uptime) → Performance.

**All tip rules (in generation order)**

| id | Condition | Text (exact) | Action |
|---|---|---|---|
| `leak<app>` | up to 2 leaks (largest first) | "\(app) keeps growing: +\(x.x) GB per hour for \(h) h (now \(x.x) GB) — restart it to free memory." | Stop, if stoppable |
| `runaway<app>` | up to 2 distinct apps with runaways (newest first) | "\(app) used a full processor core for \(mins) (\(span))." | Stop, if stoppable |
| `app<name>` | up to 3 non-macOS, non-leaking apps with avg mem > 2 GB, **or** seen ≥ 30 min with mem > 0.3 GB or cpu > 5 %; sorted by memory | mem > 2 GB browser (Google Chrome, Chrome, Safari, Brave Browser, Brave, Firefox, Microsoft Edge, Edge, Arc): "\(name) uses \(x.x) GB on average — close tabs you're not using; every tab costs memory"; other mem > 2 GB: "\(name) uses \(x.x) GB on average — close windows and documents you're not using"; otherwise only if stoppable: "\(name) has been running for \(duration) (avg. \(x.x)% CPU, \(x.x) GB) — quit it if you're not using it" | Stop, if stoppable |
| `loop<app>` | crash loop (≥ 5 crashes in 1 h), up to 2 | "\(app) keeps crashing and restarting (\(n)× in one hour) — that costs battery. Update or remove it." | – |
| `crashed<app>` | crashed ≥ 2 times in period (not a loop), up to 2 | "\(app) crashed \(n) times \(when). Update or reinstall it." | – |
| `stopped responding<app>` | hangs ≥ 2, up to 2 | "\(app) stopped responding \(n) times \(when). Update or reinstall it." | – |
| `jetsam` | macOS killed apps for memory ≥ 1× | 1×: "macOS closed apps once \(when) because memory ran out. Quit apps you're not using, or restart your Mac." n×: "macOS closed apps \(n) times \(when) because memory ran out. Quit apps you're not using, or restart your Mac." | – |
| `battery` | battery worn (capacity < 80 %, or a condition string, or health ≠ "Good") | "Your battery holds \(cap)% of its original charge" (or "Your battery may need attention") + optional " (macOS says: \(condition))" + ". A battery service gives you longer battery life again." | Open "Open Battery" → `x-apple.systempreferences:com.apple.Battery-Settings.extension` |
| `uptime` | days since boot ≥ `restartDays` | "Not restarted for \(d) days. A restart clears memory and finishes updates.\nHow to restart: click the Apple logo at the top left of the screen and choose Restart…" | – |
| `disk` | last disk free < 10 % | "Your disk is almost full (\(n) GB free) — clean up large files; macOS gets slow without free space" | "Open Storage" → `x-apple.systempreferences:com.apple.settings.Storage` |
| `wifidrop` | ≥ 3 Wi-Fi drops | "Wi-Fi dropped \(n)× \(when). Move closer to the router or restart it; a cable never drops." | – |
| `wifiweak` | weak Wi-Fi ≥ 15 min | "Weak Wi-Fi signal for \(duration) \(when) — move closer to the router, or add a mesh point where you work" | – |
| `mem` | pressure ≥ 2 for ≥ 30 min | "Memory was (almost) full for \(duration) — close apps you're not using, or restart your Mac" | – |
| `hot` | hot ≥ 15 min | "Your Mac was hot (≥ 95 °C) for \(duration) — put it on a hard, flat surface and keep the vents clear" | – |
| `dust` | FanDust (§5.B): minutes where mean fan > 60 % while CPU and GPU have both been < 20 % for ≥ 3 consecutive minutes; ≥ 60 such minutes summed over the last 7 days (Macs with fans only) | "Your fan spins hard while your Mac is barely busy — the vents may be dusty. Clean them or have them cleaned." | – |
| `login` | **always** (last) | "Check which apps open automatically at login — you can turn off what you don't need there" | "Open Login Items" → `x-apple.systempreferences:com.apple.LoginItems-Settings.extension` |
| `orphan…` | (category reserved; produced by §5.B startup section) | | |

"Stoppable" = the app is currently running, can be stopped (`canStop`) and is **not the frontmost app**. Pressing Stop opens the quit confirmation (§5.8); if the app is no longer running: note "\(name) is no longer running".

Restart tip threshold `restartDays` (Settings › Checkup › "Restart reminder" › "Suggest a restart after": 7 days / 14 days (default) / 30 days / Never = 0 → never). Caption: "A tip in Checkup: \"Not restarted for \(n) days. A restart clears memory and finishes updates.\"" or "Checkup never suggests a restart."

Formatting helpers used everywhere: `pct` = rounded integer + "%"; `one` = 1 decimal, always "." decimal point (en_US); `duration(minutes)` = "45m", "2h 5m", "1d 3h" (≥ 1440 min); `mins(m)` = "40 min" below 60, else `duration`; `span(a,b)` = "Tue 14:20–14:35" (weekday abbreviation in UI locale + HH:mm; if ≤ 60 s only "Tue 14:20"); `bytes` decimal: "640 KB", "12.3 MB", "1.2 GB".

#### 5.6.2 "Why is my Mac slow?" card

`themedRow` index 0, VStack spacing 6, fills width (`maxHeight: .infinity` so it matches a neighbour's height).

Header HStack: `magnifyingglass` (Fluent `Search` U+E721) `system 13 × scale` semibold, width 18, color `tone(0)`; title "Why is my Mac slow?" `label(15)` bold `onRow()`; spacer; Pill button **"Check now"** (before first check) / **"Check again"** (after), color `onRow()`; disabled while checking.

On press:
1. CPU = mean CPU over the last 30 samples (≈ 60 s) or latest CPU if none.
2. Off main thread: `housekeepingNow` (current macOS background jobs, §5.7.3).
3. Run `diagnose`, show text (`label(12)`, `onRow()`, wraps).
4. If no per-app data yet: extra line "Apps are measured every minute — the first measurement is coming soon." (`label(11)`, `onRow(true)`).
5. If a stoppable culprit app was found: Pill "Stop \(app)" in `danger` (white on blocks) → confirmation.
6. Any `note` (errors) appears below in `label(11)`, `warn` (white on blocks).

**Diagnosis rules** (`diagnose`; at most **two sentences** are shown, in this order; `own` = non-macOS apps):

| # | Condition | Sentence | Culprit app |
|---|---|---|---|
| 1 | CPU ≥ 85 % and top own app ≥ 10 % | "The CPU is at \(pct), mostly because of \(app) (\(pct))." | that app |
| 1b | CPU ≥ 85 %, no such app, a macOS job is busy | "The CPU is at \(pct), mostly because of macOS itself: \(job) — this passes by itself, no action needed." | – |
| 1c | CPU ≥ 85 %, otherwise | "The CPU is at \(pct), mostly because of macOS itself." | – |
| 2 | memory pressure ≥ 2 | "Memory is filling up" (≥ 4: "Memory is full") + optional " — \(app) is using \(x.x) GB" + "." | top memory app if ≥ 1 GB and no culprit yet |
| 3 | a runaway from the report ended < 5 min ago and isn't already the culprit | "\(app) has been using a full processor core for \(mins)." | that app if none yet |
| 4 | a leak from the report ended < 30 min ago and the app is running | "\(app) grew from \(x.x) to \(x.x) GB in \(h) h — quitting and reopening it frees that memory." | that app if none yet |
| 5 | temperature ≥ 95 °C | "Your Mac is hot (\(t) °C) and may be slowing itself down." | |
| 6 | disk < 10 % free | "Your disk is almost full (\(n) GB free)." | |
| 7 | something already found and a macOS job is busy | "\(job) — this passes by itself, no action needed." | |
| 8 | ping ≥ 100 ms | "Internet is responding slowly (\(ms) ms) — that's probably not your Mac." | |
| 9 | something found and uptime ≥ restartDays | "Your Mac hasn't been restarted for \(d) days — a restart clears memory." | |
| – | **nothing found** | "Nothing wrong: CPU \(pct), memory normal\(, 52 °C)." + optional " Biggest user: \(app)." + optional " \(job) — this passes by itself, no action needed." | none |

The Stop button only appears when the culprit `canStop`.

#### 5.6.3 Top 5 CPU / Top 5 RAM

Two cards, laid out with "fit": side by side (HStack spacing 8) if the width allows, else stacked (spacing 8). Each card min width **190 pt**, max infinite. Indices 1 and 2.

| Part | Spec |
|---|---|
| Title | CardTitle `cpu` "Top 5 CPU" / `memorychip` "Top 5 RAM" (Fluent: `Processing` U+E9F5 / `Memory` U+EEA0) |
| Empty | "No data yet" `label(11)` `onRow(true)` |
| Row | VStack spacing 2: line 1 = name (`label(12)`, 1 line, tail truncation) + spacer + value (`number(12)`), both `onRow()`; line 2 = bar + duration |
| Value | CPU: "42%"; RAM: "3.2 GB" |
| Bar | width **70 pt**, height **4 pt**, Capsule track `bar × 0.18` + Capsule fill `bar`, fill width = 70 × clamp(value / top value, 0.03…1); bar color = `theme.accent` (white on blocks) |
| Duration | `duration(minutes seen)` `label(10)` `onRow(true)`, 6 pt right of bar |
| Card VStack spacing | 5 |

#### 5.6.4 Peaks

CardTitle `waveform.path.ecg` (Fluent closest: U+E95E `Health`) "Peaks"; VStack spacing 5; default font `label(11)`; index 4.
- No peaks: Headline "None", small "No peaks recorded \(when) (CPU or GPU ≥ 80%, hot, memory pressure or heavy disk use)", tone 3 (good).
- Otherwise: Headline "\(n)" + "peak \(when)" / "peaks \(when)".
- **PeakTimeline** (2 pt vertical padding): Canvas height 16 pt. Horizontal axis line 1 pt at mid-height, color `onRow(true) × 0.4`. Ticks 1 × 8 pt, same color: 4 intervals (0/6/12/18/24 h) for one day, else one per day. Each peak = rounded rect radius 3, height 10, x = start position, width = max(duration share, 5 pt), color by kind × 0.85: CPU `accent`, GPU `good`, Heat `warn`, Memory `danger`, Disk `secondary` (blocks: white). Below: labels `number(9)` `onRow(true)`: one day "0:00 … 12:00 … 24:00"; multi-day "Mon … today" (start weekday abbreviation). Legend: per present kind a 6 pt circle + title ("CPU", "GPU", "Heat", "Memory", "Disk"), HStack spacing 10, `label(10)`.
- Summary sentences (one per kind present): "1 \(kind) peak \(when)", "\(n) \(kind) peaks \(when)", "\(app) caused \(k) of \(n) \(kind) peaks \(when)", "\(app) caused the \(kind) peak \(when)", "\(app) caused all \(n) \(kind) peaks \(when)" — an app is named only if it caused ≥ 2 or it is the only peak.
- List (Capped, newest first): span (`number(10)`, `onRow(true)`) + "\(Kind) \(value)" + " · \(cause)" (1 line, middle truncation). Value formats: CPU/GPU "92%", Heat "°C", Memory "high"/"critical", Disk rate.

#### 5.6.5 Running time

CardTitle `clock` (Fluent `Clock` U+E917) "Running time", tone 1 (warn) if uptime ≥ restartDays else 0. Index 3. VStack spacing 4.
- Headline big = `duration(uptime)` (uptime from `kern.boottime`, includes sleep; fallback system uptime), small "Mac on since Tue 18 Nov 09:12" (format `EEE d MMM HH:mm`), same tone.
- Line "Fusebox measuring since" → first measured minute of period or "–".
- Line "Measured this period" → `duration(minutes)`.
- If any: sub-header "Seen active longest" (`label(10)`, `onRow(true)`, top padding 3) + up to 5 item rows (name, duration).

#### 5.6.6 Battery (simple, only if "Battery health" is hidden)

CardTitle `battery.100percent` (Fluent `Battery10` U+E83F) "Battery", tone 1 if worn. Text = battery summary `label(11)`, e.g. **"Good · 31 of ~1000 cycles · 103% of original capacity"**. Parts: condition string, or "Good"/"Worn"/raw health; "\(c) of ~\(design) cycles" (design cycles default 1000); "\(p)% of original capacity" (NominalChargeCapacity / DesignCapacity, rounded). Worn = capacity < 80 % or condition present or health ≠ "Good". Index 7. Source: IOKit power sources + `AppleSmartBattery` registry (Windows: `Win32_Battery` / `BatteryStaticData`/`BatteryFullChargedCapacity` WMI).

#### 5.6.7 Crashes (Stability group)

If the period has crash data (`crashes`, `hangs`, `resource`, `jetsam`):
- CardTitle `exclamationmark.octagon` (Fluent `ErrorBadge` U+EA39) "Crashes", tone 2 (danger) if any crash/hang else 1.
- If crashes+hangs > 0: Headline "1 app"/"\(n) apps", small "crashed or stopped responding \(when)", tone 1.
- Capped item rows, most first (ties alphabetical):
  - "\(app)" → "crashed \(n)×" (+ ", \(k)× in one hour" if crash loop)
  - "\(app)" → "stopped responding \(n)×"
  - "Out of memory" → "macOS closed apps \(n)×"
  - up to 5 resource rows: "\(app) (disk writes)" / "(processor)" / "(wakeups)" → "flagged by macOS \(n)×"
- Index 8.

Otherwise a "None" card: CardTitle `checkmark.seal` (Fluent `Completed` U+E930) "Crashes" tone 3; Headline "None", small "No apps crashed or stopped responding \(when)", tone 3.

**Crash data source** (`Diag.crashes`): `~/Library/Logs/DiagnosticReports` and `/Library/Logs/DiagnosticReports` (skipped if unreadable, `Retired/` ignored), files modified since period start − 1 day, newest **300** files, only the first **4 KB** of each.
- `.ips`: line 1 is JSON. `JetsamEvent*` or `bug_type` 298 → out of memory; `bug_type` 309 or 109 → crash of `app_name`/`name`; `is_simulated == 1` ignored; timestamp from `timestamp`.
- `.diag`/`.hang`/`.spin`: text header lines `Command:`, `Process:` (spindump, "Name [pid]"), `Event:`, `Date/Time:`. Event contains hang/spin → hang; "disk writes"/"cpu"/"wakeups" → resource flag.
- Reports from test builds running in `/tmp`, `/private/tmp`, `/private/var/folders`, `/var/folders` (`procPath`) are ignored.
- Counted only if timestamp within the period (for "Yesterday": until today 00:00).
- **Crash loop**: ≥ **5** crashes of one app within any sliding **1-hour** window.
- Windows: Windows Error Reporting (`%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive`), Event Log Application 1000 (crash) / 1002 (hang), Resource-Exhaustion-Detector 2004 for low memory.

#### 5.6.8 Busy moments

CardTitle `flame` (no Fluent flame glyph; use U+E945 `LightningBolt` or a custom path) "Busy moments", tone 1 if any. Headline "None" (tone 3) or count (tone 1), small "periods of ≥ 5 min above 85% CPU \(when)". Item rows: "Mostly \(app)" (or "Busy") → "\(span) · \(pct)". Index 4.
Rule: runs of ≥ **5** consecutive minutes with CPU ≥ 85 % (gaps ≤ 90 s keep the run), newest first, max **5**; culprit = app with the highest summed CPU during the run; end = last minute + 60 s.

#### 5.6.9 Log (History group)

CardTitle `list.bullet.rectangle` (Fluent `BulletedList` U+E8FD) "Log". Index 6 + number of tips.
- Empty: "Nothing unusual happened" (`label(11)`, `onRow(true)`).
- Capped event rows: icon (width 14, color by level: 2 `danger`, 1 `warn`, 0 `secondary`; blocks white) + text (`label(12)`, wraps) + spacer + span (`number(10)`, `onRow(true)`).
- Divider 1 pt (`theme.line`; blocks white 30 %), vertical padding 3.
- File list: line rows "meterkast-2026-11-21.csv" → "1.2 MB", same for apps- and peaks- files of the period.
- Pill "Open log folder" (`onRow()`, top padding 2) — creates the folder if needed and opens it in Finder (Windows: Explorer).

**Events** (newest first, max 30): runs for heat/memory/internet join if interruptions ≤ 6 min (360 s).

| Event | Icon (SF → Fluent) | Text | Level |
|---|---|---|---|
| Busy run (≥ 5 min ≥ 85 %) | `flame` | "Busy: \(pct) CPU" + " · mostly \(app)" | 1 |
| Heat run (≥ 95 °C) | `thermometer.high` → no exact Fluent glyph; use a custom thermometer path or U+E9CA | "Hot: up to \(t) °C" | 2 |
| Memory run (pressure ≥ 2) | `memorychip` → U+EEA0 | "Memory full" (any minute ≥ 4, level 2) / "Memory filling up" (level 1) | 1/2 |
| Internet run (offline or ping ≥ 200 ms) | `wifi.exclamationmark` → U+EB63 `NetworkOffline`/Wi-Fi warning variant | "Internet down" (all minutes offline) / "Internet slow: avg. \(ms) ms" | 1 |
| Wi-Fi drop run | `wifi.slash` → U+EB5E `WifiError` | "Wi-Fi dropped" / "Wi-Fi dropped for \(mins)" | 1 |
| Weak Wi-Fi run (< −75 dBm) | `wifi.exclamationmark` | "Weak Wi-Fi: around \(dBm) dBm" | 1 |
| First disk-low minute | `internaldrive` → `HardDrive` U+EDA2 | "Disk almost full: \(n) GB free" | 2 |
| Leak | `memorychip` | "\(app) grew from \(x.x) to \(x.x) GB" | 1 |
| Runaway | `cpu` | "\(app) used a full processor core for \(mins)" | 1 |
| macOS housekeeping run (≥ 5 min, gaps ≤ 10 min) | `gearshape` → `Settings` U+E713 | "\(job) — passes by itself" | 0 |

#### 5.6.10 Cards from other files (see §5.B)

`BatteryHealthSection` (index 15), `DrainSection` (12), `SleepDrainSection` (13), `AwakeSection` (14), `StartupSection` (9), `DiskSection` (10), `SSDHealthCard` (11), `SSDSpeedCard` (12). They are only created when their group is expanded (their own background `.task`s — `pmset`, login item scan, disk scan — therefore do not run while collapsed).

---

### 5.7 Health detection rules (Diag / Gezondheid)

#### 5.7.1 Memory leak

Per app (non-macOS), from the apps log (per day; a leak across midnight is judged in two pieces):
1. Split the minute series at gaps **> 30 min** or a restart (memory suddenly **< 60 %** of the previous minute).
2. A piece qualifies if: ≥ **30** points, duration ≥ **3 h**, `from` = median of first 5 minutes > 0, `to` = median of last 5; `to ≥ 1.5 × from` and `to − from ≥ 1 GB`; drops (point < 85 % of running peak) ≤ 10 % of points; Pearson correlation time↔memory ≥ **0.7**.
3. Largest growth per app; over the period the largest per app, sorted by growth.
- `hours` = round(duration h), min 1. `perHour` = (to − from) / max(hours, 1).

#### 5.7.2 Runaway ("full processor core")

App with CPU ≥ `100 / cores` % (of the whole Mac = one full core) for ≥ **10** consecutive minutes (gaps ≤ 150 s). Newest first.

#### 5.7.3 macOS housekeeping ("passes by itself")

| Job text | Process names (ps prefix) | Names in apps log |
|---|---|---|
| "Spotlight is indexing" | mds, mdworker, corespotlightd | "Spotlight (search)" |
| "iCloud is syncing" | bird, cloudd, fileproviderd | "iCloud", "iCloud Drive" |
| "Photos is analysing your library" | photoanalysisd, mediaanalysisd | "Photo analysis", "Media analysis" |
| "Time Machine is making a backup" | backupd | "Time Machine" |
| "Software Update is checking or installing updates" | softwareupdated | "Software update" |
| "Your Mac is cooling down (kernel_task slows it on purpose)" | kernel_task | – (from thermal state) |

- "Busy" = ≥ **25 %** of one core.
- History: minutes per job; runs ≥ 5 min (gap ≤ 10 min) become log events.
- Now (`housekeepingNow`): apps from the last minute measurement ≥ 25 % of a core; if "macOS itself" > 30 %, root processes are checked via `ps -o %cpu=,comm= -p <pids>`; `tmutil status` contains "Running = 1" → Time Machine; thermal state serious/critical → cooling. Returned in table order.
- Windows: Windows Search indexer (`SearchIndexer.exe`), OneDrive sync, Windows Update (`TiWorker.exe`, `MoUsoCoreWorker.exe`), Defender scan (`MsMpEng.exe`), thermal throttling via `PowerGetActiveScheme`/WMI `MSAcpi_ThermalZoneTemperature`.

#### 5.7.4 BackgroundSection — "What macOS is doing"

CardTitle `gearshape.2` (Fluent `Settings` U+E713) "What macOS is doing"; VStack spacing 4; index 11.
- While computing: small spinner. Then: "Nothing special in the background right now." or the busy jobs joined with ". " + " — this passes by itself."
- If the period saw jobs: "Busy \(when)" (`label(11)`, `onRow(true)`, top padding 2), then rows job → `duration(minutes)` (`number(10)`), sorted most first.

#### 5.7.5 Uptime

Boot date from `kern.boottime` (Windows: `GetTickCount64` / `Win32_OperatingSystem.LastBootUpTime`; note Fast Startup).

---

### 5.8 Collapsible groups

| Property | Value |
|---|---|
| Persisted open set | `checkupOpen`, comma list, default `"perf"` (Performance open, others closed) |
| Header | full-width plain button, HStack spacing 10, vertical padding **10**, whole row clickable |
| Icon | SF Symbol `system 15 semibold`, width 22; color `warn` if group warns, else link color (`accent`; on Blocks style `text`, because accent would be too light on the page) |
| Title | `label(15)` bold `theme.text` |
| Summary | `label(11)`, 1 line tail-truncated, `warn` if group warns else `secondary`; parts joined " · " with tip count first |
| Chevron | `chevron.right` `system 12 semibold` `secondary`, rotated **90°** when open (Fluent `ChevronRight` U+E76C) |
| Separator | 1 pt `theme.line` rectangle at the **top** of each group |
| Content | VStack spacing 10, bottom padding 12; group's tips then content |
| Animation | toggling: `spring(duration 0.32, bounce 0.12)`; reduce motion: `easeOut 0.2`. Content insertion = opacity + offset y −14 → 0; removal = opacity only; reduce motion = opacity only. No blur/scale/shadow (cheap). Low impact mode disables all animation. |
| Accessibility | label "\(title), expanded" / "\(title), collapsed" |
| Collapsed | content is not built at all |

**Groups**

| id | Title | Icon (SF → Fluent) | Summary | Warn when |
|---|---|---|---|---|
| perf | "Performance" | `gauge.with.dots.needle.67percent` → `Speedometer`/U+EC4A | "\(app) busiest" (or "No app data yet") · "1 busy moment"/"\(n) busy moments" (if any) · "no peaks"/"1 peak"/"\(n) peaks" | any busy moment |
| power | "Battery & power" / "Power" | `battery.75percent` / `bolt` → U+E859 `Battery7` / U+E945 `LightningBolt` | "lost \(n)% while asleep" (from cached sleep drain) · "\(p)% capacity" · "\(n) cycles"; fallback "What uses energy and what keeps your Mac awake" | battery worn |
| stability | "Stability" | `exclamationmark.triangle` → U+E7BA | "1 app crashed"/"\(n) apps crashed" · "\(n) stopped responding" · "out of memory \(n)×"; fallback "No crashes or hangs \(when)" | any crash or hang |
| storage | "Startup & storage" | `internaldrive` → U+EDA2 | "\(n) items start at login" (from cached startup scan; excludes orphans) · "\(n) left behind" · "\(n) GB free"; fallback "Login items and disk space" | orphans > 0 or disk low |
| history | "History & export" | `clock.arrow.circlepath` → U+E81C `History` | "Measured \(duration) · " + "nothing unusual"/"1 event"/"\(n) events" | never |

Summaries only use already known data (report or caches); nothing is loaded for them.

**Quit confirmation** (from tips or "Stop \(app)"): alert "Quit \(name)?", message "Unsaved work may be lost.", buttons "Stop" (destructive) / "Cancel". On failure: note "\(name) couldn't be stopped"; on success the diagnosis is cleared.

---

### 5.9 Export bar, share report, PDF

Export bar: HStack spacing 8, right-aligned, top padding 4, all **Pill** buttons in `theme.secondary`: **"Share report…"**, **"Export CSV"**, **"Export PDF"**.

| Action | Behaviour |
|---|---|
| Share report… | sheet **540 × 560**, padding 16, VStack spacing 10: title "Share report" `label(14)`; note "Contains no user name, file names, computer name, serial number or network addresses." (`label(11)`, secondary); scroll box with the text in **monospaced 11 pt**, selectable, padding 8, background quaternary × 0.5, radius 8; buttons "Copy" (clipboard), "Save as .txt…" (default name `fusebox-report.txt`), spacer, "Close" (Esc). |
| Export CSV | save panel, name `fusebox-today.csv` / `fusebox-yesterday.csv` / `fusebox-7-days.csv`; all main logs of the period merged into the **current** column order (missing columns empty). Written off main thread. Error: "Saving failed: \(error)". |
| Export PDF | save panel `fusebox-<period>.pdf`; renders, always in **Minimal light** theme, light scheme, white background, width **595 pt** (A4 width), padding 32, VStack spacing 12: "Fusebox — \(period)" system 20 semibold, date (long date + short time, en_US) 11 pt secondary, then **all** report parts in order score, tops, runtime, battery, busy, peaks, crashes, tips, log — no limits, no buttons. Single tall page. Failure: "Creating the PDF failed". |

**Share text** (plain, English, ~60 lines; app names only):
```
Fusebox Checkup report — Today
Created Sat 21 Nov 2026 10:15

MAC
Model: Mac16,13
Chip: Apple M4
Memory: 16 GB
macOS: 26.2.0 (25C56)
On for: 3d 2h (since …)
Disk: 494 GB, 120 GB free

BATTERY
Good · 31 of ~1000 cycles · 103% of original capacity

HEALTH (today)
Score: 8/10 — Calm: …
Measured: … / Busy (CPU ≥ 85%): … / Memory (almost) full: … / Hot (≥ 95 °C): …

BUSY MOMENTS        - Tue 14:20–14:30 · 95% · mostly Xcode
TOP APPS            By CPU: … / By memory: … / By energy: …
FINDINGS            - X grew from 1.0 to 5.0 GB in 4 h / - Y used a full processor core for 30 min (span)
CRASHES AND REPORTS FROM MACOS   - OneDrive: crashed 3×
MACOS HOUSEKEEPING SEEN          - Spotlight is indexing: 8m
```
Sections without data are omitted. The whole text passes through the **privacy scrubber**: `/Users/<name>` → `~`; user name, full name, host name and computer name (≥ 4 chars, excluding generic words mac/macbook/imac/apple/macos/user/admin/home/local) → "…"; IPv4 → `x.x.x.x`; MAC addresses → `xx:xx:xx:xx:xx:xx`. Never include serial numbers.

---

### 5.10 CheckupGist (used by the menu bar rich panel, §8)

`Analysis.gist(meter)`: uses today's report from memory if < **10 min** old (filled by the Checkup page or a previous gist call), else loads today once in the background. Returns score, reason, and **one tip**: the first *serious* tip, else the first tip that isn't the always-present `login` tip; split into head/rest as in §5.6.1, plus `serious` flag (so the panel shows a warning triangle vs a lightbulb).

---

### 5.11 Empty and loading states (summary)

| Situation | Shown |
|---|---|
| First open, report loading | period picker, "Why is my Mac slow?" card, small centered spinner (20 pt padding) |
| Re-open / period switch | cached report immediately; reload button dimmed (0.4) while refreshing |
| No measurements in period | score "–" (secondary), reason "No measurements for this period yet"; tops "No data yet"; busy "None"; peaks "None…"; log "Nothing unusual happened" |
| No crashes | "Crashes / None / No apps crashed or stopped responding \(when)" (good tone) |
| Background jobs computing | small spinner in "What macOS is doing" |
| No per-app data yet | under the diagnosis: "Apps are measured every minute — the first measurement is coming soon." |

### 5.12 Light vs dark, narrow vs wide

- All colors come from theme tokens (§10); Minimal light is light-only, Instrument and Terminal dark-only, others follow the appearance mode (§10). Glass card gradients differ per scheme (table in §5.6.0).
- Narrow (< 620 pt): every `cols{}` block stacks vertically (left content first, then right). Wide (≥ 620 pt): two equal columns. Top 5 CPU/RAM independently use "fits horizontally" (each ≥ 190 pt + 8 pt gap).
- Text scales with `textScale` (all `theme.label`/`theme.number` sizes and icon sizes multiplied by `theme.scale`).

---

## 5.B Checkup sections (detail)

Sources: `Opstart.swift` (startup items, disk usage, SSD health, SSD speed), `Accu.swift` (battery and power cards, learned battery time), `Fans.swift` (fans, curve editor, helper, dust), `Laden.swift` (charge limit), `Helper/FanHelper.swift` (privileged helper `fusebox-fand`).

This part covers the cards themselves. The page frame (the collapsible groups, period picker, tips, report) is in 5.A. Recap of where each card sits (from `Analyse.swift`):

| Group (id, title, icon) | Left column | Right column |
|---|---|---|
| `power`: **"Battery & power"** (`battery.75percent`). On a Mac without a battery it is called **"Power"** (`bolt`) | `BatteryHealthSection` (if `health` is on; otherwise the older report "battery" card), then `DrainSection` | `SleepDrainSection`, `AwakeSection` |
| `storage`: **"Startup & storage"** (`internaldrive`) | `StartupSection` | `DiskSection`, then `SSDHealthCard` (both shown when `disk` is on), then `SSDSpeedCard` (if `ssdBench` is on) |

- **Columns.** The page has `columns == 2` when the window is wide. Then it shows two VStacks side by side: `HStack(alignment: .top, spacing: 12)`, each column `VStack(spacing: 10)` at `maxWidth: .infinity, alignment: .topLeading`. When narrow, everything stacks in one `VStack(spacing: 10)`: left content first, then right.
- **Lazy loading.** A collapsed group does not put its cards in the view tree at all. So nothing loads (no `pmset`, no plist reading, no disk scan) until the user expands the group. Each card's `.task` runs when it appears and is cancelled when the user leaves the page or collapses the group.
- **Which cards are shown.** Each card can be switched off with an id in the `checkupHide` pref: `drain`, `sleep`, `awake`, `health`, `battery`, `startup`, `disk`, `ssdBench`. The cards `drain`, `sleep` and `health` are always hidden on a Mac without a battery (`Power.hasBattery == false`). `awake` is still shown there.
- **Group header summaries** (computed from cache only; nothing is read to build them):
  - Power: `"lost N% while asleep"` (from the last sleep span with a loss > 0), `"N% capacity"`, `"N cycles"`, joined with `" · "`. Fallback: `"What uses energy and what keeps your Mac awake"`. The header turns warn-colored when `battery.worn`.
  - Storage: `"N items start at login"`, `"N left behind"` (only if > 0), `"N GB free"`. Fallback: `"Login items and disk space"`. Warn color when there are orphans or the disk is low.

### 5.B.0 Shared card building blocks (used by every card below)

Every card is a `VStack(alignment: .leading)` at full width (`maxWidth: .infinity, alignment: .leading`) with `.themedRow(theme, index: n)` applied. The index picks the block color in the "Color blocks" theme (`blocks[index % 5]`).

**Card indexes:** Startup = 9, Disk = 10, SSD health = 11, SSD speed = 12, Drain = 12, Sleep = 13, Awake = 14, Battery health = 15.

**ThemedRow** (Theme.swift), by row style:

| Row style (themes) | Look |
|---|---|
| `.lines` (Instrument, Minimal light) | Padding: vertical 7. 1 pt bottom hairline in `theme.line`. No background |
| `.plain` (Terminal) | Padding: vertical 5. No line, no background |
| `.cards` (Glass) | Padding: h 10 / v 8. Background `theme.card`, rounded rect r=12. 1 pt stroke in `theme.line` |
| `.blocks` (Color blocks) | Padding: h 10 / v 8. Solid fill `blocks[index % 5]`, r=10. **All text and icons are white** |
| `.glass` (Liquid Glass) | Padding: h 10 / v 8. Continuous rounded rect r=14. Fill is a top→bottom linear gradient: dark white 0.11→0.05, light white 0.62→0.38. Stroke border 1 pt, also a gradient: dark white 0.28→0.06, light white 0.95→black 0.06. This is **not** a real blur |

The block colors, in index order: `#E0573D`, `#2E7DD6`, `#1C9E75`, `#7A54C7`, `#D9991A`.

**Text colors on a row.** `theme.onRow()` is the theme text color, or white on blocks. `theme.onRow(true)` is the theme secondary color, or white at 0.8 opacity on blocks.

**Tone** (`theme.tone(t, icon:)`):

| t | Meaning | Color | On blocks |
|---|---|---|---|
| 0 | normal | icon: accent; heading: text color | white |
| 1 | watch out | `warn` | white |
| 2 | serious | `danger` | white |
| 3 | good | `good` | white |

**Fonts.** `theme.label(size)` is the theme font at weight medium, multiplied by `textScale`. `theme.number(size)` is the theme number weight with monospaced digits, multiplied by `textScale`. The font design comes from the theme: rounded / monospaced / default.

**`CardTitle(icon, title, tone)`**
- `HStack(spacing: 6)`.
- SF Symbol at `11 × scale` pt, semibold, colored `tone(tone)`, in a 16 pt wide frame.
- Title at `label(12)` semibold in `onRow()`.

**`Headline(big, small, tone)`**
- `VStack(spacing: 1)`, with bottom padding 2.
- big: `number(22)`, colored `tone(tone, icon: false)`. One line, shrinks to 0.7 scale before truncating.
- small: `label(11)` in `onRow(true)`. Wraps.

**`Pill` button style**
- Text at 11 pt semibold system font, one line, in the given color.
- Padding: h 9 / v 3.
- 1 pt capsule stroke at 0.6 opacity of the same color.
- Pressed: opacity 0.55. There is no hover state.
- Used for all "Show in Finder" and "Test speed"-style buttons.
- Windows: a small `HyperlinkButton` or a `Button` with a transparent background, `CornerRadius=999` and a 1 px border.

**`Capped(items, limit: 5)`**
- Shows the first 5 rows. A plain text button follows: `"Show all (N)"` / `"Show less"` at `label(11)` semibold in `tone(0)` (accent), with top padding 1.
- No "show all" appears if only one row would be hidden: it caps only when `count > limit + 1`.

**`PowerCard`** (frame of the 4 battery and power cards)
- `VStack(spacing: 5)`.
- Header `HStack`: CardTitle, Spacer, then an optional refresh button. The refresh button is a plain `arrow.clockwise` icon in `onRow(true)` with tooltip "Check again".
- Content font defaults to `label(11)`.

**Mini bar** (Disk, Drain)
- `Capsule`, height 4. Width is `50 × max(value / biggest, 0.04)` pt, so 2–50 pt.
- Fill: accent at 0.7 opacity (white at 0.7 on blocks).
- Not animated.

**Spinner.** While data loads, the card body shows a `ProgressView().controlSize(.small)`: a small indeterminate spinner. Windows: `ProgressRing` 16 px.

**SF Symbol → Segoe Fluent Icons mapping used in this section:**

| SF Symbol | Used for | Segoe Fluent Icons (glyph) |
|---|---|---|
| `power` | Startup card | `PowerButton` E7E8 |
| `internaldrive` | Disk / SSD health / storage group | `HardDrive` EDA2 |
| `speedometer` | SSD speed | `Speed High` EC4A |
| `exclamationmark.triangle.fill` | orphan rows | `Warning` E7BA (filled: `IncidentTriangle` E814) |
| `battery.25percent` | Drain card | `Battery2` E852 (or `MobBattery2` EBA2) |
| `moon.zzz` | Sleep card | `QuietHours` E708 |
| `cup.and.saucer` / `eye` | Awake card (`eye` when something keeps the screen on) | `Coffee`-like: use `Lightbulb` EA80 / `View` E890 |
| `heart.text.square` | Battery health | `Health` E95E |
| `arrow.clockwise` | refresh | `Refresh` E72C |
| `battery.75percent` / `bolt` | Power group | `Battery7` E857 / `LightningBolt` E945 |
| `fan` | Settings › Fans tab | no exact glyph; use `Fan`-like `E9CA` (`Frigid`) or a custom path |
| `battery.75percent` | Settings › Battery tab | `Battery7` E857 |

---

### 5.B.1 Startup & background items (`StartupSection`, card index 9)

**Where:** Checkup › "Startup & storage" › left column. **Visible:** always, unless `startup` is in `checkupHide`.

```
┌───────────────────────────────────────────────────────────┐
│ ⏻ Startup & background items              ( Login Items… )│
│ 7                                                         │
│ apps start at login                                       │
│ Apps from other makers that start with your Mac or wait   │
│ in the background. Turn off what you don't use in Login…  │
│                                                           │
│ Left behind by deleted apps            (warn color, bold) │
│ These try to start every time but their app is gone. …    │
│ ⚠ Teamviewer Service  needs your password to remove (Show in Finder) │
│                                                           │
│ Apps that start at login                         (bold)   │
│ ● Dropbox   running        0.4% CPU · 52 MB · 0.1 W (Show in Finder) │
│ ○ Zoom      you                                (Show in Finder) │
│ … Show all (9)                                            │
│ Background helpers                               (bold)   │
│ ● Google Keystone Agent  all users   <0.1% CPU · 12 MB    │
└───────────────────────────────────────────────────────────┘
```

#### Layout

`VStack(spacing: 5)`:

1. **Header row:** `CardTitle(icon: "power", title: "Startup & background items")`, then Spacer, then a Pill button **"Login Items…"**.
   - The title tone is 1 (warn) if any orphan exists, otherwise 0.
   - The button opens System Settings › General › Login Items (`x-apple.systempreferences:com.apple.LoginItems-Settings.extension`).
   - Windows: `ms-settings:startupapps`.
2. **Headline:** big = the number of distinct app names that start at login (non-orphan). small = `"app starts at login"` (exactly 1) or `"apps start at login"`.
3. **Explainer**, `label(11)`, `onRow(true)`, wrapping: "Apps from other makers that start with your Mac or wait in the background. Turn off what you don't use in Login Items."
4. **Orphans block** (only if there are any):
   - Heading "Left behind by deleted apps": `label(11)` semibold in `tone(1, icon:false)` (warn), top padding 4.
   - Text "These try to start every time but their app is gone. Safe to remove." in `onRow(true)`.
   - Then one row per orphan, `HStack(spacing: 6)` at `label(11)`:
     - `exclamationmark.triangle.fill` in `tone(1)`.
     - The name in `onRow()`, one line, middle truncation.
     - If the item is not in the user folder: "needs your password to remove" at `label(10)` in `onRow(true)`.
     - Spacer (min 4), then Pill **"Show in Finder"**, which reveals the plist.
   - Tooltip: the plist path + newline + `"Missing: <program path>"`.
5. **Group "Apps that start at login"**, then **group "Background helpers"**. Each group:
   - Heading at `label(11)` semibold, `onRow()`, top padding 4.
   - If empty: "None" in `onRow(true)`.
   - Otherwise a `Capped` list (5 rows, then "Show all (N)").
   - Row: `HStack(spacing: 6)` at `label(11)`:
     - **Status dot:** 6×6 circle. Running: `good` (white on blocks). Not running: `onRow(true)` at 0.35 opacity.
     - **Name:** `onRow()`, one line, middle truncation.
     - **State word** at `label(10)`, `onRow(true)`: `"running"` if running, else `"you"` if it is a per-user item, else `"all users"`.
     - Spacer (min 4).
     - **Usage text** (if measured) at `number(11)`. Colored `warn` when watt ≥ 1 or CPU ≥ 5%, otherwise `onRow(true)`. Tooltip "Average since it started (memory: now)".
     - Pill **"Show in Finder"**, only for per-user items (`~/Library/LaunchAgents`).
   - Row tooltip: the launchd label + newline + the program path.
6. **While loading:** a small spinner in place of items 4–5. The headline is absent.

#### Data (macOS)

- **Where it reads.** It reads every `*.plist` in three folders. No permissions needed.

  | Folder | per-user | agent |
  |---|---|---|
  | `~/Library/LaunchAgents` | yes | yes |
  | `/Library/LaunchAgents` | no | yes |
  | `/Library/LaunchDaemons` | no | no (daemon) |

- **Skipped items.** Items whose `Label` starts with `com.apple.`, items with `Disabled = true`, and items without a `Label`. SMAppService/sfltool login items are skipped (they need admin rights).
- **`atLogin`.** True only for agents with `RunAtLoad == true`, or with `KeepAlive` set (either the bool true, or a conditions dictionary). Daemons always count as "background".
- **`orphan`.** True when the program path is absolute, does not start with `/Volumes/`, and does not exist (a broken symlink also counts). Paths that are only a name (e.g. `node`) are never orphans.
- **Friendly name.** The outermost `*.app` folder in the program path, without `.app`. Otherwise the label: split on `.`, `-`, `_`, drop the first word, drop words that repeat the previous one case-insensitively, capitalise the first letter of each, join with spaces. `com.teamviewer.teamviewer_service` becomes "Teamviewer Service", `com.google.keystone.agent` becomes "Google Keystone Agent". If nothing is left, the raw label is used.
- **Running and usage.**
  - All PIDs are listed (`proc_listallpids` + `proc_pidpath`), mapped from path to PIDs. A plist's program matches either directly or after resolving symlinks.
  - For own processes, usage comes from `proc_pid_rusage` v6: (user+system CPU time) ÷ lifetime ÷ cores × 100 = **% of the whole Mac**; memory = `phys_footprint` (MB); watt = `energy_nj` ÷ 1e9 ÷ lifetime.
  - Root processes fall back to `ps -o time=,etime=,rss=`. That gives CPU and RSS memory, but no energy (`energyKnown = false`).
  - Several PIDs are summed.
- **Usage text format:**

  | Part | Rule |
  |---|---|
  | CPU | `"<0.1"` if < 0.1; one decimal if < 10; integer otherwise |
  | Memory | `"x.x GB"` if ≥ 1024 MB, else `"N MB"` |
  | Watt | `"<0.1"` if < 0.1, else one decimal |
  | Whole text | `"0.4% CPU · 52 MB · 0.1 W"`. Without energy: `"0.5% CPU · 40 MB"` (no watt part) |

- **Grouping within each list.** Items with the same friendly name become one row (e.g. 3× OneDrive):
  - The row uses the per-user one if present.
  - It counts as running if any of them runs.
  - Usages are summed.
  - Sort: by watt descending (unmeasured items count as −1), then by name.
- **Caching.** The scan runs once off the main thread (utility priority), the first time the card appears. It is cached in memory for the app's lifetime (a static), and the group header uses that cache. There is no refresh button; restarting the app refreshes.
- **Rule: read-only.** Fusebox never deletes or disables items. It only reveals them in Finder or opens Login Items.

**Windows equivalent:** `HKCU\…\Run`, `HKLM\…\Run`, the Startup folders, Task Scheduler logon tasks, and services (`Win32_Service` with StartMode Auto, non-Microsoft). Orphan = the target exe is missing. Usage: `Process.TotalProcessorTime` ÷ uptime ÷ cores, and `WorkingSet`/`PrivateMemorySize`. Energy is not available, so use the "no watt" text form. "Show in Finder" becomes "Show in Explorer" (`explorer /select,`) or "Open in Task Manager".

---

### 5.B.2 Disk usage: "What takes my disk space?" (`DiskSection`, card index 10)

**Where:** Checkup › Startup & storage › right column, top. **Visible:** unless `disk` is hidden.

```
┌───────────────────────────────────────────────────────────┐
│ 🖴 What takes my disk space?              ( Find large folders )│
│ 212 GB free                                               │
│ of 494 GB                                                 │
│ Finds the biggest folders in your home folder. Nothing is deleted. │
└───────────────────────────────────────────────────────────┘
after scanning:
│ Library › Developer [check first]   ▬▬▬▬▬▬▬▬  48.2 GB (Show in Finder) │
│   Xcode: build files, simulators and device support. Clear in …      │
│   Biggest inside: Xcode 30.1 GB · CoreSimulator 12.0 GB (check first)│
│ Movies [keep]                       ▬▬▬▬      22.0 GB (Show in Finder) │
│ …(max 10 rows)                                                       │
│ Not counted (macOS protects these): Desktop, Documents, …            │
```

#### Layout

`VStack(spacing: 5)`:

1. **Header:** `CardTitle(icon: "internaldrive", title: "What takes my disk space?")`. Tone 1 if free space < 10% of total, else 0.
   - Spacer, then a Pill button: **"Find large folders"** (never scanned) or **"Check again"** (a result exists).
   - The button is disabled while scanning. Disabled is the standard SwiftUI disabled look, about 0.5 opacity.
2. **Headline** (free and total are read once, when the card appears, from `/`):
   - big = `"N GB free"`, small = `"of N GB"`. Integers, decimal GB (1e9).
   - Tone 1 when low (< 10% free).
3. **One of three states:**
   - **Scanning:**
     - A determinate thin progress bar, `ProgressView(value: done, total: count).controlSize(.small)`, tinted accent (white on blocks).
     - Under it: `"Checking <folder title>…"` at `label(11)` in `onRow(true)`, one line, middle truncation.
   - **Result:** up to the **10** biggest folders. Each folder is a `VStack(spacing: 2)` with vertical padding 2.
     - **Top line**, `HStack(spacing: 6)` at `label(11)`:
       - the title in `onRow()`, one line, middle truncation;
       - an optional **verdict tag**;
       - Spacer (min 4);
       - the mini bar (width ∝ size / largest);
       - the size text at `number(11)`, `onRow()`, in a 58 pt wide right-aligned frame;
       - Pill **"Show in Finder"**.
     - **Explanation line** at `label(10)`, `onRow(true)`, wrapping. It is the advice text from the table below. If the folder has no advice and no big children, the fallback is "Your own folder. Check what's inside before deleting anything".
     - **"Biggest inside: "** line at `label(10)`, `onRow(true)`, max 2 lines. It lists up to 3 children over 100 MB: `"name 12.0 GB"`, plus `" (can go)"`-style verdict labels when a child name has advice, joined with `" · "`.
     - After the list, if anything was skipped: `"Not counted (macOS protects these): A, B, C. Sizes are too small without Full Disk Access."` at `label(11)`, `onRow(true)`.
   - **Never scanned:** "Finds the biggest folders in your home folder. Nothing is deleted." at `label(11)`, `onRow(true)`.

**Verdict tag.** Text at `label(9)` semibold. Padding h 5 / v 1. 1 pt capsule stroke at 0.6 opacity of the color.

| Verdict | Label | Color (blocks theme: white) |
|---|---|---|
| safe | "can go" | `good` |
| careful | "check first" | `warn` |
| keep | "keep" | `secondary` |
| unknown | "?" | `secondary` |

#### Full Disk Access flow

Pressing the button first checks Full Disk Access (FDA): Fusebox tries to open `/Library/Application Support/com.apple.TCC/TCC.db` for reading. This fails silently if FDA is missing.

Without FDA, an alert appears:

- Title: **"Give Fusebox Full Disk Access?"**
- Message: "Without it, macOS hides your Photos library, Mail, app containers and parts of the caches, so the sizes come out too small. In Privacy & Security › Full Disk Access, turn on Fusebox (or add it with +), then press Find large folders again. Fusebox only measures sizes; it never reads or deletes your files."
- Buttons:
  - **"Open Settings"**: opens `…PrivacySecurity.extension?Privacy_AllFiles`.
  - **"Scan without"**: scans anyway.
  - **"Cancel"**.

#### What is scanned

- Every direct child of the home folder except `Library`.
- Every direct child of `~/Library`.
- `~/Library/Application Support/MobileSync` (iOS backups), as its own row.
- **Never entered:** `Library/CloudStorage` (File Provider folders could trigger downloads).
- **Protected folders.** Without FDA these are not opened at all, not even deep inside another row. They are listed under "Not counted":
  `Desktop, Documents, Downloads, .Trash, Library/Mobile Documents, Library/Containers, Library/Group Containers, Library/Application Support/MobileSync, Music/Music, Movies/TV, Pictures/Photos Library.photoslibrary, Library/Photos, Library/Mail, Library/Messages, Library/Safari, Library/Calendars, Library/HomeKit`.
- **Titles.** The relative path, with `Library/` shown as `"Library › "`. Special titles:

  | Path | Title |
  |---|---|
  | `.Trash` | "Trash" |
  | `…MobileSync` | "iPhone & iPad backups" |
  | `Music/Music` | "Music library" |
  | `Movies/TV` | "TV library" |
  | `Photos Library.photoslibrary` | "Photos library" |
  | `Library/Photos` | "Photos data" |

- **Measurement.**
  - Allocated size on disk (`totalFileAllocatedSize`, falling back to `fileAllocatedSize`) is summed over a recursive enumeration.
  - Every other row's folder and every protected folder is skipped, so nothing is counted twice.
  - Per-child totals are collected in the same pass.
  - Each folder gets at most **15 s**. If it runs longer (macOS waiting on a permission prompt), the row counts as "not counted" and the scan moves on.
  - Cancellation is checked every 2,000 files.
  - Leaving the page cancels the scan. A half-finished result is discarded.
- **Result.** Top 10 by size; empty and unmeasured rows are dropped. It is kept in memory (static) while the app runs, so returning to the page shows the last result.
- **Size format, `bytes()`.** Decimal, always with a period: < 1e6 → `"640 KB"` (integer); < 1e9 → `"12.3 MB"`; otherwise `"1.2 GB"` (one decimal, en_US).

**Advice table** (looked up by relative path first, then by last path component):

| Key | Text | Verdict |
|---|---|---|
| Library/Developer | Xcode: build files, simulators and device support. Clear in Xcode or delete DerivedData; old simulators: Xcode › Settings › Components | careful |
| DerivedData | Xcode build files. Safe to delete; Xcode rebuilds them | safe |
| CoreSimulator | iPhone/iPad simulators from Xcode. Remove old ones in Xcode › Settings › Components | careful |
| iOS DeviceSupport | Debug files for every iPhone you connected to Xcode. Old versions can go | safe |
| Archives | Xcode app archives (builds you exported). Keep the ones you still need | careful |
| Library/Caches | Caches of your apps. Safe to delete; apps rebuild them (first start is a bit slower) | safe |
| Caches | Cache files. Safe to delete; they are rebuilt | safe |
| Library/Logs | Log files of apps and macOS. Safe to delete | safe |
| Library/Application Support | Data and settings of your apps. Keep it; look inside for an app you no longer use | keep |
| Library/Containers | Data of App Store apps. Keep it; delete an app to remove its data | keep |
| .Trash | Trash. Empty it to get the space back | safe |
| Downloads | Your downloads. Often old installers and zips that can go | careful |
| Movies / Music | Your videos. / Your music. Keep, or move to an external drive | keep |
| Pictures | Your photos. Keep, or move the Photos library to an external drive | keep |
| Desktop / Documents | Files on your desktop. Your own files / Your documents. Your own files | keep |
| node_modules | JavaScript packages. Safe to delete; `npm install` brings them back | safe |
| .npm | npm download cache. Safe to delete | safe |
| .gradle | Gradle cache (Android/Java builds). Safe to delete; it downloads again | safe |
| .android | Android SDK settings and emulators. Delete old emulators in Android Studio | careful |
| .m2 | Maven cache (Java). Safe to delete; it downloads again | safe |
| .cargo | Rust packages and tools. The registry cache can go | careful |
| .cache | Cache of command-line tools. Safe to delete | safe |
| .docker | Docker settings. The big data is in Docker itself: clean up there | careful |
| .claude | Claude Code: your sessions and history. Keep it unless you don't need old conversations | keep |
| .vscode | VS Code extensions. Remove unused extensions in VS Code | careful |
| Library/Mobile Documents | iCloud Drive files on this Mac. Remove downloads with "Remove Download" in Finder | careful |
| Library/Mail | Your Mail messages and attachments | keep |
| MobileSync / …/MobileSync | iPhone/iPad backups. Delete old backups in Finder › your iPhone › Manage Backups | careful |

**Windows equivalent:**
- Scan `%USERPROFILE%` children plus `AppData\Local`, `AppData\Roaming` and `AppData\LocalLow` children. Skip OneDrive placeholders (`FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`).
- Use the size on disk (`GetCompressedFileSize`).
- There is no FDA concept; skip folders that raise access-denied and list them as "not counted".
- Adapt the advice keys to Windows: `AppData\Local\Temp` (safe), `%LOCALAPPDATA%\Packages` (keep), `node_modules`, `.gradle`, `.nuget\packages` (safe).
- Free/total space: `DriveInfo("C:\\")`.

---

### 5.B.3 SSD health (`SSDHealthCard`, card index 11)

**Where:** directly under the Disk card, when `disk` is visible. Also feeds a "Health" fact on the Live SSD detail. **Visible only if** the internal SSD returns S.M.A.R.T. data. Otherwise the card renders nothing: a zero-height clear view that runs one read attempt.

```
┌───────────────────────────────────────────────┐
│ 🖴 SSD health                                  │  (icon good-green, or warn)
│ OK                                            │  (22 pt, good color)
│ Wear 3% · 41 TB written in total · 10,000 hours on │
│ [warn only] The SSD is wearing out or reports errors. Keep a recent backup. │
└───────────────────────────────────────────────┘
```

- `VStack(spacing: 4)`. `CardTitle(icon: "internaldrive", title: "SSD health")`, tone 1 if `warn`, else 3 (good).
- **Headline:**
  - big = **"OK"** or **"Check soon"** (warn), tone 3 or 1.
  - small = `info.text + " · N hours on"`. Hours use en_US grouping: "10,000".
- **Warn text**, `label(11)`, `onRow()`: "The SSD is wearing out or reports errors. Keep a recent backup."
- **`text`:** `"Wear N% · X written in total"`, where X is:

  | Total written | Format |
  |---|---|
  | < 1 TB | `"820 GB"` |
  | < 10 TB | `"8.0 TB"` (one decimal) |
  | otherwise | `"41 TB"` |

- **Warn rule:** percentage used ≥ 80, or the NVMe critical-warning byte ≠ 0, or available spare < the spare threshold, or media errors > 0.
- **Data (macOS):**
  - NVMe SMART/Health log page (512 bytes) via the IOKit `NVMeSMARTLib` plug-in, on `IOEmbeddedNVMeBlockDevice` / `IONVMeBlockDevice` with "NVMe SMART Capable" = true. No root needed.
  - Fields: byte 0 critical; 3 spare %; 4 spare threshold; 5 percentage used (can exceed 100); 48–63 data units written × 512,000 bytes; 128–143 power-on hours; 160–175 media errors (all little-endian).
  - Read in the background **at most once per hour**.
  - **Serial numbers are never read or shown.**
- **Windows:** `MSFT_StorageReliabilityCounter` via `Get-StorageReliabilityCounter` (Wear, PowerOnHours, ReadErrorsUncorrected), or `IOCTL_STORAGE_QUERY_PROPERTY` with `StorageDeviceProtocolSpecificProperty` (NVMe log page 2). The byte layout is identical. Admin may be needed.

### 5.B.4 SSD speed test (`SSDSpeedCard`, card index 12)

**Where:** Storage group, right column, last. **Visible:** unless `ssdBench` is hidden. It only runs when the user presses the button.

```
┌──────────────────────────────────────────────────┐
│ ⏲ SSD speed                         ( Test speed ) │
│ Writes and reads 1 GB in a temporary file (a few │
│ seconds), then deletes it.                       │
└──────────────────────────────────────────────────┘
running:   ━━━━━━━━━━──────────  Writing a test file…  [Stop]
result:    As fast as expected
           Read 3.1 GB/s · Write 2.8 GB/s · Nov 18, 2026 at 10:42
```

- **Header:** `CardTitle(icon: "speedometer", title: "SSD speed")`. Tone: no result → 0; fast → 3; slow → 1.
- **Button** (Pill): `"Test speed"` (never run) / `"Test again"` / `"Stop"` (while running; it cancels).
- **States:**
  - **Running:**
    - A determinate small progress bar (0…1), tinted accent (white on blocks). It updates in ≥ 2% steps.
    - Text "Writing a test file…" while progress < 0.5, then "Reading it back…".
  - **Result:**
    - `Headline` big = **"As fast as expected"** (tone 3) or **"Slower than usual"** (tone 1).
    - small = `"Read X · Write Y · <date>"`. The date is abbreviated date + short time in the UI locale.
    - If slow, an extra line in `onRow()`: "The disk may be almost full or busy. Try again when nothing else is running."
  - **Idle, no result:** "Writes and reads 1 GB in a temporary file (a few seconds), then deletes it." in `onRow(true)`.
  - **Note line** (warn color) on failure: "Needs at least 5 GB of free space." or "The test couldn't finish." Cancel shows nothing.
- **Speed format:** ≥ 1000 MB/s → `"3.1 GB/s"` (one decimal), else `"850 MB/s"`.
- **Fast rule:** read ≥ 1500 MB/s **and** write ≥ 1000 MB/s.
- **Method:**
  - Requires ≥ 5 GB free (important-usage capacity).
  - Fills an 8 MB buffer with random bytes (defeats compression).
  - Writes 128 × 8 MB = 1 GB to `<temp>/fusebox-speedtest-<pid>.tmp`, with `F_NOCACHE`, then `F_FULLFSYNC` (fallback `fsync`). Timed.
  - Reopens the file and reads it back, also `F_NOCACHE`. Timed.
  - MB/s = 1,073,741,824 ÷ 1e6 ÷ seconds. Write is timed including the flush.
  - The file is always deleted.
  - Runs off the main thread (user-initiated priority).
- **Storage:** the last result (read, write, date) is kept as JSON in pref `ssdBenchLast`, so it persists across launches.
- **Windows:** the same idea with `FileStream` and `FileOptions.WriteThrough | NoBuffering` (sector-aligned buffer), then `FlushFileBuffers`. Use `%TEMP%`. Thresholds for NVMe can stay; SATA SSDs reach about 500 MB/s, so consider "Slower than usual" only below roughly 400/300 for SATA.

---

### 5.B.5 Battery & power cards

All four use `PowerCard`. They read the power log through a shared `PowerLog` cache: `pmset -g log` is filtered with grep to `Sleep|Wake|DarkWake` and `Summary-` lines, and only the last 2 days are kept. It is read **at most once per hour**, because it costs about 5 s CPU. Dates in rows use the format `"EEE HH:mm"` in the UI locale, e.g. "Tue 23:10".

#### 5.B.5.1 "What drained my battery" (`DrainSection`, index 12) — battery Macs only

```
┌────────────────────────────────────────────────┐
│ ▭ What drained my battery                      │
│ Zoom 42%                                       │  (22 pt)
│ Since unplugged Tue 08:12 · 96% → 61%          │
│ Safari                       ▬▬▬▬▬       18%   │
│ Mail                         ▬▬           6%   │
│ …(up to 5 more rows)                           │
└────────────────────────────────────────────────┘
```

- **Icon:** `battery.25percent`. Tone is always 0. No refresh button.
- **Time window:**
  - The last stretch on battery: from the first AC→battery transition in the log, to the next battery→AC transition (or now).
  - The stretch is used if Fusebox is on battery now and the stretch is still open, or if the stretch ended today.
  - Otherwise the window is **today from midnight** ("on the charger").
- **Ranking:**
  - Reads the per-app minute CSVs `apps-YYYY-MM-DD.csv` for each day in the window (columns `tijd`, `app`, `energie_w`, optional `mac_energie_w`).
  - Per-app Wh = Σ W ÷ 60. Total = max(Σ of `mac_energie_w` per minute ÷ 60, Σ of app Wh).
  - Top **6** apps.
- **Headline** (when total > 0):
  - big = `"<top app> <share>%"`, share = app Wh ÷ total × 100, rounded.
  - small = the subtitle.
- **Subtitle:**
  - Not unplugged: **"Today · on the charger now"**.
  - Otherwise: `"Since unplugged <EEE HH:mm>"` + optionally `" until <EEE HH:mm>"` + optionally `" · 96% → 61%"`. The end % is the current % if still on battery, `?` if unknown.
- **Rows:** apps 2–6. The top app is already in the headline; when total is 0, all apps are listed.
  - `HStack(spacing: 6)`: name (`onRow()`, tail truncation), Spacer, mini bar (relative to the top app's Wh), and the share `"18%"` at `number(11)` in a 38 pt right-aligned frame (`"–"` if total is 0).
  - **Only the percentage share is shown, never Wh**, because macOS attributes only part of the energy to apps (the screen etc. are not attributed).
- **Empty:** "No app data for this time yet".
- **Windows:**
  - Battery/AC transitions: `powercfg /batteryreport /xml` or Event Log (Kernel-Power 105). The app's own per-minute log gives the same.
  - Per-app energy: Windows has the "Energy estimation" engine (SRUM, `powercfg /srumutil`), or fall back to CPU-seconds share and label it as a share.

#### 5.B.5.2 "Battery use while asleep" (`SleepDrainSection`, index 13) — battery Macs only

```
┌────────────────────────────────────────────────┐
│ ☾ Battery use while asleep                     │
│ −6%                                            │
│ during the last sleep on battery (8h 30m)      │
│ Tue 23:10 → Wed 07:40   8h 30m          −6%    │
│   Woken by lid opened · woke briefly 2×, mostly network access (Wi-Fi/Bluetooth) │
│ Mon 22:00 → Tue 06:30   8h 30m   on charger    │
└────────────────────────────────────────────────┘
```

- **Icon:** `moon.zzz`. Title tone 1 if any span is "heavy".
- **Sleep span:**
  - Starts at the first `Sleep` after a full wake and ends at the next full `Wake`. `DarkWake` events in between belong to the span and are counted per reason.
  - Spans shorter than **30 minutes** are dropped.
  - `charger` = AC was seen at any point (at sleep, at a dark wake, or at wake). Then the loss is unknown and shown as **"on charger"**.
  - Loss = charge at sleep − charge at wake.
- **Heavy:** loss ≥ 3% **and** loss × 60 > minutes, i.e. more than 1%/hour.
- **Headline** (the last span with a known loss):
  - big = `"−N%"` if > 0, else **`"0%"`**. Never "−0%". The minus is U+2212.
  - small = `"during the last sleep on battery (8h 30m)"`. Tone 1 if heavy.
- **Rows:** the last 4 spans, newest first. Each is a `VStack(spacing: 1)`:
  - Line 1, `HStack(spacing: 6)`: `"EEE HH:mm → EEE HH:mm"` (`onRow()`), the duration (`onRow(true)`), Spacer, then the loss or "on charger" at `number(11)`. The loss is warn-colored (`tone(1, icon:false)`) when heavy, else `onRow()`.
  - Line 2, `label(10)`, `onRow(true)`, wrapping: `"Woken by <reason>"` + `" · woke briefly N×"` + `", mostly <most frequent dark-wake reason>"`.
- **Empty:** "No sleep longer than 30 minutes in the last 2 days".
- **Duration format, `duration(min)`:** ≥ 1440 → `"1d 3h"`; ≥ 60 → `"8h 30m"`; else `"45m"`.
- **Wake reason → plain words.** The first rule that matches wins, on the lowercased "due to" text:

  | Match | Text |
  |---|---|
  | `usb-c_plug`, `acattach` | charger connected |
  | `lid` | lid opened |
  | `pwrbtn` | power button |
  | `keyboard` | keyboard |
  | `multi-touch` | trackpad |
  | `hid activity` | keyboard or trackpad |
  | `useractivity` | you |
  | `notification` | a notification |
  | `maintenance` | Power Nap / maintenance |
  | `sleepservice` | scheduled background task |
  | `wifibt` | network access (Wi-Fi/Bluetooth) |
  | `bluetooth` | Bluetooth |
  | `wlan` | network access (Wi-Fi) |
  | `usb` | USB device |
  | `rtc` | timer |
  | (empty) | unknown |
  | (anything else) | other |

- **Windows:**
  - `powercfg /sleepstudy` (Modern Standby) or System event log: Kernel-Power 42 (sleep), 107 / Power-Troubleshooter 1 (wake, with "Wake Source").
  - Battery % at each event comes from `powercfg /batteryreport` or the app's own log.

#### 5.B.5.3 "What keeps my Mac awake" (`AwakeSection`, index 14) — all Macs

```
┌────────────────────────────────────────────┬───┐
│ ☕ What keeps my Mac awake                    ↻ │
│ Zoom   keeping the screen on        1h 12m     │
│ Audio playing   preventing sleep        3m     │
│ WhatsApp   preventing sleep                    │
└────────────────────────────────────────────────┘
```

- **Icon:** `eye` if any hold is "keeping the screen on", else `cup.and.saucer`. Tone 0.
- Has the **refresh button** (↻, "Check again"), which re-reads the data.
- **Source:** `pmset -g assertions`. Reads the per-process lines and the follow-up `Created for PID:` lines (which attribute the hold to the real app).
- **Types kept:**

  | Assertion type | Text |
  |---|---|
  | PreventUserIdleDisplaySleep, NoDisplaySleepAssertion | "keeping the screen on" |
  | PreventUserIdleSystemSleep, NoIdleSleepAssertion | "preventing sleep" |
  | PreventSystemSleep | "preventing all sleep" |

- **Ignored processes:** powerd, WindowServer, kernel_task, hidd, loginwindow, sharingd, bluetoothd, and runningboardd (unless it carries a "Created for PID", which is then used).
- `coreaudiod` is shown as **"Audio playing"**.
- **App names:** from `NSRunningApplication.localizedName`, trimmed of whitespace and control characters (WhatsApp starts with an invisible direction mark). Otherwise the process name, or `"pid N"`.
- **Age:** `HH:MM:SS` → minutes → `duration()`. Under a minute, no age is shown.
- Duplicates (same app + text) are removed.
- **Row:** `HStack(spacing: 6)`: app (`onRow()`, tail truncation), the text at `label(10)` in `onRow(true)`, Spacer, then the age at `number(11)` in `onRow(true)`.
- **Empty:** "Nothing: your Mac can sleep normally".
- **Windows:** `powercfg /requests` (DISPLAY / SYSTEM / AWAYMODE / EXECUTION sections, which need admin), or `CallNtPowerInformation`. Map DISPLAY → "keeping the screen on" and SYSTEM → "preventing sleep". Audio shows as "[DRIVER] … audio stream" → "Audio playing".

#### 5.B.5.4 "Battery health" (`BatteryHealthSection`, index 15) — battery Macs only

```
┌──────────────────────────────────────────────┐
│ ♡ Battery health                             │
│ 92%                                          │
│ of original capacity · 312 cycles            │
│ ╲___________╱‾‾‾‾╲_______  (28 pt sparkline) │
│ Since 2026-10-01: 95% → 92% · +41 cycles     │
└──────────────────────────────────────────────┘
```

- **Icon:** `heart.text.square`. Tone 1 when `worn`.
- **Headline:**
  - big = `"92%"` (capacity). If the capacity is unknown, the condition string, otherwise the health string.
  - small = `"of original capacity"` and `"N cycles"`, joined with `" · "`. Tone 1 if worn.
- **Trend** (only with ≥ 2 daily rows):
  - **Sparkline:** `Spark(values, top: max+1, bottom: min−1, fill: 1, closed: false)`.
  - Stroked line only (no area fill), in accent (white on blocks), 1.5 pt wide, round caps and joins. Height 28 pt, full card width.
  - Line style: straight segments with slightly rounded corners (`Spark.roundness = 0.2`). It is right-aligned, thinned to at most 1 point per 2 pt of width, and static (no glide).
  - Under it: `"Since <first day>: 95% → 92% · +41 cycles"`. The cycles part appears only if both rows have cycle counts. Color `onRow(true)`.
- **Fewer than 2 rows:** "Recorded once a day; the trend shows after a few days".
- **`Diag.battery()` fields:**
  - `capacity` = round(NominalChargeCapacity ÷ DesignCapacity × 100). Read from AppleSmartBattery, at the top level or under `BatteryData`.
  - `cycles` = CycleCount; `designCycles` = DesignCycleCount9C, default 1000.
  - `health` = `kIOPSBatteryHealthKey`. If missing (Apple silicon), it is derived: < 80% → "Worn", else "Good".
  - `condition` = `kIOPSBatteryHealthConditionKey` (e.g. "Service Recommended"), if present.
- **Worn:** capacity < 80, or a condition is present, or health ≠ "Good".
- **Summary string** (used in the report/tips, not this card): `"Good · 31 of ~1000 cycles · 103% of original capacity"`.
- **Daily log:** `Documents/Fusebox/battery-health.csv`.
  - Header `datum,gezondheid_pct,cycli`. Column names are Dutch and fixed; keep them.
  - One row per date, written by the minute tick only when the date changed, or at launch. Skipped if that date already exists.
  - Example: `2026-11-18,102,31`.
- **Windows:** `powercfg /batteryreport /xml`: DesignCapacity, FullChargeCapacity, CycleCount. Or WMI `root\wmi` `BatteryStaticData.DesignedCapacity`, `BatteryFullChargedCapacity`, `BatteryCycleCount`. There is no "condition" string; derive "Worn" from < 80%.

#### 5.B.5.5 Learned battery time (`LearnedBattery`): feeds the Live BAT row, not a Checkup card

- **Pref:** `learnedBattery` (default **true**). Used only when on battery (not charging, not on AC) and a learned rate exists.
- **Rate:**
  - From the app's own minute logs `meterkast-YYYY-MM-DD.csv` (columns `tijd`, `batterij_pct`), last 7 days.
  - A segment is a run of consecutive minutes where each gap is ≤ 180 s and the % never rises by more than 0.05.
  - Flat starts and ends (< 0.05 change) are trimmed: full on the charger, or held by the charge limit.
  - A segment counts only if it lasts ≥ 10 min, drops ≥ 1%, and drops ≥ 1.5 %/h (slower is assumed to be the charger).
  - **Weight** = 0.5^(age in days ÷ 2) × (0.75 + 0.25·cos(hour difference × π/12)). Recent days count more, and so does the same time of day.
  - The rate is the weighted average %/h.
  - Used only if the total unweighted hours ≥ **2**.
  - Recomputed off the main thread **at most every 30 min**.
- **Minutes left** = pct ÷ rate × 60. It is blended with the observed rate since a sample at most 15 min ago that was on battery the whole time: only if that span is ≥ 5 min and the drop is ≥ 1%, with blend weight 0.5 × min(span ÷ 900 s, 1).
- **Display:**
  - The Live BAT row's side text (pref `batteryTime`, default true) shows `"H:MM"`, e.g. `"2:35"`, or **`"14h"` when ≥ 600 minutes (10 h)**, because "14:27" would read as a clock time.
  - The detail fact reads `"<duration> left (learned)"`.
- **When charging** without a macOS estimate, Fusebox computes time-to-full from how fast the % rose in recent minutes.
- **Battery watts** (`batteryW`) = Amperage × Voltage ÷ 1e6 (amperage is read as a signed Int64). It is **nil, so the watt facts are hidden, when amperage is 0.**
- **Live BAT row details** (for cross-reference with the Live section):
  - Level: 2 (danger) at ≤ 10%, 1 (warn) at ≤ 20%, only when not charging.
  - Side icon: `battery.100percent.bolt` while charging. Otherwise `battery.{100|75|50|25|0}percent`, using thresholds ≥ 88 / 63 / 38 / 13.
  - Detail facts:
    - Charging: `"full in …"` + `"Charging at N W"`.
    - On AC but not charging: the charge-limit fact, or `"holding at N%"` when < 99%.
    - On battery: `"… left"` + `"Using N W"`.
    - Plus `"Power adapter: N W"` or "connected".
- **Windows:** the same algorithm on the app's own log. `SystemInformation.PowerStatus.BatteryLifeRemaining` is the OS estimate. Watts: `BatteryStatus.ChargeRate` / `DischargeRate` (mW) from WMI `root\wmi`. Hide it when 0.

---

### 5.B.6 Fans

**Visible only when the Mac has fans:** `Fans.count` = SMC key `FNum` > 0, read once. On a fanless Mac (MacBook Air):
- there is no FAN Live metric;
- the Settings › Fans tab is absent (`SettingsTab.shown` filters it);
- dust is never detected.

The first time a fanned Mac runs, the FAN metric is auto-added to the Live list once (`fanMetricAdded`).

**Reading** (no root): per fan `i`, SMC keys:

| Key | Meaning |
|---|---|
| `F{i}Ac` | actual rpm |
| `F{i}Mn` | min |
| `F{i}Mx` | max |
| `F{i}Tg` | target |
| `F{i}Md` | 1 = forced, i.e. someone (the helper) is driving it |

`fanRPM` = the average rpm. `fanPct` = the average rpm ÷ max × 100. It is nil when any fan is forced, because then it says nothing about dust. It is logged as CSV column `ventilator_pct`.

**Live FAN row** (for reference): value = rpm with en_US grouping, e.g. `"2,400"`, unit `rpm`, template `"8,888"`. Graph top = the highest max rpm. Side text:
- `"auto"` when no fan is forced;
- `"curve"` or `"full"` when forced (by `fanMode`).

Detail: `"average of N fans"` when there is more than one fan.

#### 5.B.6.1 Settings › Fans (`FanSettings`)

Settings uses a standard grouped `Form` (`.formStyle(.grouped)`): native macOS grouped sections with rounded inset backgrounds, system fonts, `.caption` footers in secondary color. It does **not** use the app themes. Windows: a `SettingsCard`/`SettingsExpander` style page (Windows Community Toolkit), or a `StackPanel` of grouped cards with corner radius 8.

```
Fans now
┌───────────────────────────────────────────────────────────┐
│ Fan                          ╱‾‾╲__╱‾‾ (140×22)   2,400 rpm │
│ macOS range 1200–5700 rpm · set by Fusebox                │
└───────────────────────────────────────────────────────────┘
┌───────────────────────────────────────────────────────────┐
│ Control  [Automatic (macOS) | Custom curve | Full speed ]  │
│ Install the helper below, then Fusebox can set the fans.  │ (orange caption)
└───────────────────────────────────────────────────────────┘
 Fusebox never runs the fans slower than macOS's minimum. Above 95 °C the fans go to full speed automatically.
Fan speed by temperature (drag the points)          (only in Custom curve)
┌───────────────────────────────────────────────────────────┐
│ [curve editor 170 pt tall]                                │
│ 40 °C              70 °C              95 °C: full speed   │
│ Bottom = macOS's minimum speed, top = full speed.         │
│ [Quiet] [Balanced] [Cool]               Now 64 °C → 18%   │
└───────────────────────────────────────────────────────────┘
Fusebox helper   (HelperSection, see 5.B.6.3)
```

- **No fans:** a single section, "This Mac has no fans." in secondary. This can't normally be reached, since the tab is hidden.
- **Section "Fans now":** one `LabeledContent` row per fan.
  - Label line 1: "Fan" (or "Fan 1", "Fan 2" when there is more than one).
  - Label line 2, a secondary subtitle: `"macOS range 1200–5700 rpm"`, plus `" · set by Fusebox"` when forced.
  - Right side, `HStack(spacing: 10)`:
    - a sparkline of the last 150 samples' rpm (≈ 5 min at 2 s): stroke only, `Color.accentColor` 1.5 pt, 140×22, top = max rpm;
    - the rpm text, e.g. `"2,400 rpm"`, monospaced digits, in an 80 pt right-aligned frame.
  - If no readings yet: "Reading the fans…".
- **Control section:**
  - A segmented picker **"Control"** with **"Automatic (macOS)"** (`auto`), **"Custom curve"** (`curve`), **"Full speed"** (`max`). Pref `fanMode`, default `auto`.
  - If the mode is not auto and the helper is not installed: an orange caption, "Install the helper below, then Fusebox can set the fans."
  - Footer: "Fusebox never runs the fans slower than macOS's minimum. Above 95 °C the fans go to full speed automatically."
- **Curve section** (mode == curve only):
  - Header "Fan speed by temperature (drag the points)".
  - The `CurveEditor`.
  - A row of preset buttons, **"Quiet"**, **"Balanced"**, **"Cool"**: standard bordered buttons. The one equal to the current curve is disabled.
  - Then Spacer and, if a temperature is known, `"Now 64 °C → 18%"` (monospaced, secondary).

#### 5.B.6.2 Curve model and editor

- **Pref `fanCurve`:** the string `"°C:%,…"`. Default **Balanced** = `40:0,60:20,75:50,85:80,95:100`.

  | Preset | Value |
  |---|---|
  | Quiet | `40:0,65:10,80:40,90:75,95:100` |
  | Balanced | `40:0,60:20,75:50,85:80,95:100` |
  | Cool | `40:10,55:35,70:65,80:90,90:100` |

- **% meaning:** the position between macOS's min rpm (0%) and max rpm (100%), so rpm = min + (max − min) × clamp(%, 0…100) ÷ 100.
- **Validation** (trust boundary; the setting may come from an import):
  - 2–8 points;
  - each °C in 40…100;
  - % in 0…100;
  - °C strictly increasing;
  - each part exactly `t:p`.
  - Invalid → fall back to Balanced.
- **Interpolation:** linear between points, flat outside the ends. A non-finite temperature uses the last point's % (safe).
- **Input temperature:** the hotter of CPU temp and GPU temp.
- **Editor visuals** (system colors, not theme), 170 pt tall, full width:
  - **Grid:** 7 vertical lines (every 10 °C, 40–100) and 5 horizontal lines (0/25/50/75/100%), stroked `.quaternary` (very faint label color), 0.5 pt.
  - **95 °C marker:** a vertical dashed line in red at 0.5 opacity, 1 pt, dash [3,3].
  - **Curve:** a polyline from the left edge (at the first point's %) through all points to the right edge (at the last point's %). `Color.accentColor`, 2 pt. Sharp joins, no smoothing.
  - **Points:**
    - a 12×12 accent-colored filled circle with a 2 pt ring in the window background color, so it looks punched out;
    - the hit area is a 24×24 circle;
    - accessibility label `"Point N: T °C, P%"`.
  - **Current temperature:** a 10×10 orange dot on the curve at the current temperature (clamped to 40…100). Not interactive.
  - **Axis labels** (`.caption2`, secondary): "40 °C" left, "70 °C" center, "95 °C: full speed" right in red at 0.8 opacity.
  - Caption: "Bottom = macOS's minimum speed, top = full speed."
- **Dragging:**
  - The drag gesture has a minimum distance of 0.
  - The point follows the pointer live.
  - x is rounded to whole °C and clamped between its neighbors with ≥ 1 °C gap (or 40 / 100 at the ends).
  - y is rounded to whole % and clamped 0…100.
  - The pref is written on every change. No animation.
- **Windows:** a `Canvas` (Win2D) or a XAML `Polyline` + `Ellipse`s with `ManipulationDelta`. Use `SystemAccentColor`.

**Control loop** (`FanController`, every 2 s sample, off the main thread):

| Mode | Commands sent |
|---|---|
| auto | `"auto"` once, when switching into it |
| max | `set i <max rpm>` for each fan, every sample |
| curve | `set i <rpm>` for each fan, every sample. The rpm goes through the smoother. No temperature → send nothing (the helper falls back by itself) |

- Commands are skipped when there are no fans, the previous round is still busy, or the helper is not installed.
- When the app quits in curve/max mode, it sends `"auto"` synchronously.
- **`FanSmoother`** (anti-hunting):
  - If the target is > current + 150 rpm, jump up immediately.
  - If the target is < current − 150, step down by at most 100 rpm per sample.
  - Changes within ±150 are ignored.

#### 5.B.6.3 Fusebox helper (`HelperSection`): shown under Settings › Fans **and** Settings › Battery

- **Header:** "Fusebox helper".
- **Footer:** "A small background program that runs as administrator. It is the only part that can change fan speed or charging. If Fusebox stops talking to it, the fans go back to macOS after 30 s and charging back to normal after 10 minutes."
- **Not installed:**
  - a row **"Helper"** with the button **"Install…"**;
  - caption "Not installed. Installing asks for your password once." (secondary).
- **Installed:**
  - `LabeledContent("Helper", value: …)`:
    - "Installed";
    - "Update needed" when the helper reports `v=` lower than the expected version **3**;
    - "Installed, not responding" when the status call failed.
  - If outdated, an orange caption: "This Fusebox has a newer helper. Click Reinstall… to update it (asks for your password)."
  - Buttons: **"Reinstall…"** and **"Uninstall helper…"** (destructive role, red text).
  - Uninstalling sends `auto` and `charge auto`, removes the files, and sets `fanMode = auto` and `chargeLimitOn = false`.
- **Note line:** errors, in caption secondary: "Cancelled.", `"Didn't work: <message>"`, or "The helper is missing from this copy of Fusebox."
- **Status re-check:** about 1 s after every action, and when the card appears.
- **Install mechanics (macOS):**
  - One admin prompt (AppleScript `do shell script … with administrator privileges`).
  - Copies `Contents/Resources/fusebox-fand` to `/Library/PrivilegedHelperTools/local.fusebox.fand` (root:wheel 0755).
  - Writes the LaunchDaemon `/Library/LaunchDaemons/local.fusebox.fand.plist` (RunAtLoad, KeepAlive) and bootstraps it.
  - Socket: `/var/run/local.fusebox.fand.sock`, mode 0666. The peer must be root or the console user (`getpeereid`).
- **Protocol:** one ASCII line per connection, ≤ 32 characters, strict grammar. A 1 s socket timeout.

  | Command | Reply / effect |
  |---|---|
  | `status` | `ok fans=N forced=0|1 hot=0|1 [charge=CHTE|CH0B|none held=…] v=3` |
  | `auto` | fans back to macOS |
  | `set <fan> <rpm>` | rpm is clamped by the helper to [Mn, Mx]. Nonsense limits (0, reversed) → error |
  | `charge limit <50-100>` | hold charging |
  | `charge pause` | pause because hot |
  | `charge auto` | normal charging |

- **Helper-side safety** (it never trusts the app):
  - never below the macOS minimum;
  - the hottest sensor ≥ **95 °C** → all fans full until it drops below **90 °C**;
  - no `set` for **30 s** → auto;
  - no `charge` command for **10 min** → normal charging;
  - at start and on SIGTERM → auto and normal charging;
  - `charge limit L` is honored only if its own reading is ≥ L − 6%;
  - `charge pause` only if its own battery temperature is ≥ 35 °C and < 90 °C;
  - writes only whitelisted SMC charge keys: `CHTE` (4 bytes, 1 = inhibit), or `CH0B`+`CH0C` (1 byte, 0x02 = inhibit).
- **Windows equivalent:**
  - A Windows Service (LocalSystem), installed once with UAC, talking over a named pipe with an ACL for the interactive user. Same line protocol and same watchdog rules.
  - Fan control on Windows is vendor-specific. Use LibreHardwareMonitor's `Control` sensors where supported, otherwise hide fan control and show read-only fans.
  - Charge limit: vendor WMI (Lenovo Conservation mode, ASUS `ChargeMode`, Dell BIOS `PrimaryBattChargeCfg`, Surface Smart Charging). Hide the feature when none is found.

#### 5.B.6.4 Dust detection (`FanDust`)

- Runs as part of the Checkup analysis (5.A), **fanned Macs only**.
- Reads the last 7 days of `meterkast-*.csv`. Columns are looked up by name: `ventilator_pct`, `cpu_pct`, `gpu_pct`. Old logs without the fan column count 0.
- Per minute it averages CPU, GPU and fan. A minute is **suspicious** when:
  - CPU and GPU averages have both been < **20%** for ≥ **3 consecutive minutes**, so the fan still spinning down after heavy work does not count;
  - at least half the samples in the minute have a fan value;
  - the fan average is > **60%**.
- **Trigger:** ≥ **60** suspicious minutes in total over 7 days. Finished days are cached; today is re-read.
- **Output:** a Checkup tip (id `dust`, no action button): "Your fan spins hard while your Mac is barely busy — the vents may be dusty. Clean them or have them cleaned."
- Minutes when the helper forces the fans are excluded (`fanPct` is nil then).

---

### 5.B.7 Charge limit (Settings › Battery, `ChargeSettings`)

**Visible only on Macs with a battery** (the Settings › Battery tab is hidden otherwise). Default **off**. The concept is like AlDente.

```
Charge limit
┌───────────────────────────────────────────────────────────┐
│ Limit charging                                       [◯ ] │
│ Keeps the battery healthier when you're mostly plugged in. Needs the Fusebox helper (admin password once). │
│ Stop charging at          ────●──────── (200 pt)      80% │
│ Pause charging when the battery is hot               [◯ ] │
│ From 40 °C until it has cooled to 37 °C.                  │
└───────────────────────────────────────────────────────────┘
 Install the helper below, then Fusebox can hold the charge.   (orange)
 Charging starts again 5% below the limit. When Fusebox quits or you turn this off, charging goes back to normal.
 Now 78% · 31 °C · holding
Fusebox helper  (HelperSection, 5.B.6.3)
```

**Prefs** (all local to this Mac; never imported):

| Pref | Default | Range |
|---|---|---|
| `chargeLimitOn` | false | — |
| `chargeLimit` | 80 | 50–100 in steps of 5 |
| `chargeHeat` | false | — |

**Section "Charge limit":**
1. Toggle **"Limit charging"**, with subtitle "Keeps the battery healthier when you're mostly plugged in. Needs the Fusebox helper (admin password once)." Disabled when the Mac is not supported.
2. `LabeledContent("Stop charging at")`: a slider (50…100, step 5, 200 pt wide) and a `"80%"` label (monospaced, 44 pt, right-aligned). Disabled when the toggle is off.
3. Toggle **"Pause charging when the battery is hot"**, with subtitle "From 40 °C until it has cooled to 37 °C." Disabled when the limit is off.

**Footer** (caption, VStack spacing 4):
- **Not supported:**
  - in orange: "Not supported on this Mac: macOS doesn't let apps hold the charge here. Use macOS's own charge limit in System Settings › Battery.";
  - a button **"Open Battery Settings"** (`com.apple.Battery-Settings.extension`).
- **On but no helper**, in orange: "Install the helper below, then Fusebox can hold the charge."
- **Always**, in secondary: "Charging starts again 5% below the limit. When Fusebox quits or you turn this off, charging goes back to normal."
- **Live status**, monospaced secondary: `"Now 78%"` + `" · 31 °C"` (battery temperature, if known) + `" · holding"` / `" · paused (warm)"`.

Then the `HelperSection`.

**Support detection:**
- Before any helper exists: the SMC has `CHTE`, or both `CH0B` and `CH0C` → unknown/possible. Otherwise → unsupported. Per the code comment, macOS 27 closed these keys on many Macs.
- With the helper: the status field `charge=none` → unsupported. A helper reply of `err unsupported` also marks it unsupported.

**Decision** (every 2 s sample, main thread):
- **Hot pause:** if `chargeHeat` is on and the battery temperature is ≥ 40 °C (or ≥ 37 °C while already paused for heat) → `.hot`.
- **No battery reading, or limit == 100** → charge normally.
- **Limit hysteresis:** hold when battery % ≥ limit (if not already holding), or ≥ limit − 5 (while holding). Otherwise charge.
- **Commands:** `charge limit <L>` (L clamped 50–100) / `charge pause` / `charge auto`.
  - Sent on every change, always once at the first sample, and repeated **every 60 s** while holding or pausing (a heartbeat, so the helper's 10-minute timeout never fires).
  - On quit, `charge auto` is sent synchronously.
  - If the helper is unreachable, it retries on the next sample.

**Live BAT detail fact** (`ChargeLimit.fact`, shown when on and supported):

| State | Fact |
|---|---|
| holding | `"Charge limit: 80% · holding"` |
| hot | `"Charging: paused · battery warm"` |
| otherwise | `"Charge limit: 80%"` |

While charging toward the limit, the fact is inserted second.

**Battery temperature:** the hottest of SMC `TB0T`/`TB1T`/`TB2T`, in −20…120 °C.

**Windows:** see 5.B.6.3. With no vendor API, show the "Not supported" footer, with the button opening `ms-settings:batterysaver` (or the OEM app).

---

### 5.B.8 Narrow vs wide, motion, states summary

| Aspect | Behavior |
|---|---|
| Narrow window (1 column) | All cards stack: left-column cards first, then right. Rows keep one line: names truncate (middle for paths/startup names, tail for apps). Pills and numbers never truncate; the Spacer has min length 4 |
| Wide window (2 columns) | Two equal columns with 12 pt between them and 10 pt between cards. Cards are top-aligned, each to its own height |
| Animations | The cards themselves don't animate: no bar growth, no number tweening. The only motion is the parent group's expand: spring 0.32 s, bounce 0.12; content fades in with a −14 pt y offset; on removal, opacity only. With Reduce Motion: ease-out 0.2 s, opacity only. Low impact mode disables all of it |
| Loading | Small spinner per card. Each data source loads once per app session (startup, disk result, drain, sleep, awake), or once per hour (pmset log, SSD SMART), or once per 30 min (learned battery) |
| Hover | None on the cards. Tooltips on startup rows (label + path), orphan rows (path + "Missing: …"), usage text, and the refresh icon ("Check again") |
| Light/dark | Cards follow theme tokens only. Settings (Fans/Battery) follows the system appearance with system colors: accent, orange for warnings, red for the 95 °C marker and the destructive button |
| Privacy | No serial numbers (SSD), no user paths in shared reports (`Diag.scrub` replaces `/Users/<name>` with `~`, removes host names, IPs and MAC addresses) |

---

## 6. Network tab

Source files: `Netwerk.swift` (page, device table, scanner, OUI table, device store), `NetwerkInternet.swift` (internet panel, ping quality, live graph, "Find the problem", speed test, public IP), `NetwerkProbes.swift` (raw ICMP/TCP/UDP/mDNS/NetBIOS/SSDP/UPnP/WoL probes, IEEE OUI loader, `PingStats`), `Draadloos.swift` (Bluetooth + mobile-uplink rows; **these are shown on the Docked tab, not on Network**; documented in 6.12 for completeness).

Tab identity: page id `network`, title **"Network"**, SF Symbol `network` (Segoe Fluent: `` Network / `` "NetworkTower" alt). Keyboard shortcut **⌘4** (Windows: Ctrl+4). Minimum window size when switching to this tab: **470 × 440 pt** (the window grows to at least this; the user may drag it smaller afterwards, down to the global 316 × 120 minimum).

Design rule for the whole tab: **nothing on this page runs in the 2-second metering tick.** Scanning and pinging only happen while the tab is visible (`onAppear` → start, `onDisappear` → stop), or after an explicit click. Cloudflare (speed test, public IP) is contacted **only after a click**.

---

### 6.1 Page layout and width bands

Root: `VStack(spacing: 8)`, padding **12 pt left/right, 10 pt bottom**, 0 top (the toolbar/tab bar sits directly above). Top-aligned, fills the window.

Order, top to bottom:

1. **Internet panel** (`InternetPanel`, a themed card, row index 0)
2. **Toolbar** (filter, search, count, progress, range pill, bell, Scan/Stop)
3. **Blocked hint** (only when macOS Local Network permission blocks the scan)
4. **Column header** (sortable)
5. **Device list** (`ScrollView` → `LazyVStack`), or an empty-state line

Width bands (`NetWidth`, measured on the page view = full window content width):

| Band | Width (pt) | Columns shown | Toolbar differences | Internet panel |
|---|---|---|---|---|
| `narrow` | < 470 | Name, IP, ms | Filter as compact pop-up menu; no count text; no scan phase text; progress bar 36 pt; range pill icon-only ("Range") | no live graph |
| `vendor` | 470 – 599 | + Vendor | same as narrow | no live graph |
| `kind` | 600 – 759 | + Type | Filter as **segmented control**; count text visible; scan phase text visible; progress bar 70 pt; range pill with title | **live graph 170 × 46** shown at right |
| `mac` | ≥ 760 | + MAC | same as kind | graph shown |

Column visibility also obeys the setting `netHide` (comma list; ids `mac`, `vendor`, `kind`; default `""` = all shown). A column appears only if the window is wide enough **and** it is not hidden. Name, IP and ms can never be hidden.

#### ASCII sketches

Narrow (< 470 pt), e.g. 440 pt wide:

```
┌──────────────────────────────────────────────┐
│ [wifi] This Mac 192.168.1.23 on Wi-Fi ·      │  ← Internet panel (card)
│        signal good (-54 dBm)            [⌄]  │
│ Router 3 ms   Internet 14 ms                 │
│ [✓] All good: router 3 ms, internet 14 ms,…  │
│ ( 🩺 Find the problem )                       │
├──────────────────────────────────────────────┤
│ [All ▾] [🔍 Search      ]   ▬▬  (◎)  🔕 (↻ Scan)│ ← toolbar
│    Name                    IP ˄          ms  │ ← header
│ ┌──────────────────────────────────────────┐ │
│ │ 📶 Router  Router       192.168.1.1     2 │ │
│ │ 💻 MacBook This Mac     192.168.1.23    – │ │
│ │ 📱 Alex-iPhone [New]    192.168.1.40   18 │ │
│ │ 🔊 Living Room (50%)    192.168.1.52  off │ │  ← offline rows at 50 % opacity
│ └──────────────────────────────────────────┘ │
└──────────────────────────────────────────────┘
```

Medium (600–759 pt):

```
┌──────────────────────────────────────────────────────────────────────────┐
│ [wifi] This Mac 192.168.1.23 on Wi-Fi · signal good (-54 dBm)  ╭~~~~~~╮ [⌄]│
│ Router 3 ms   Internet 14 ms                                   │graph │    │
│ [✓] All good: router 3 ms, internet 14 ms, no packet loss      ╰~~~~~~╯    │
│ ( 🩺 Find the problem )                            internet — router ┄ · live│
├──────────────────────────────────────────────────────────────────────────┤
│ [All|Online|New|Offline] [🔍 Search ] 12 online · 1 new · 3 offline         │
│                          Pinging… ▬▬▬▬  (◎ This network) 🔕 (↻ Scan)        │
│    Name                       IP ˄           Vendor          Type       ms  │
│  rows…                                                                       │
└──────────────────────────────────────────────────────────────────────────┘
```

Wide (≥ 760 pt): same as medium plus a **MAC** column (monospaced) between IP and Vendor:

```
│    Name                IP ˄          MAC                Vendor        Type         ms │
│ 💻 Office-PC           192.168.1.30  3C:7C:3F:12:34:56  ASUS          Computer      4 │
```

Expanded row (any width):

```
│ 📱 Alex-iPhone [New]                    192.168.1.40           18 │
│      🏷 Name from mDNS · also: alex-iphone (DNS)                   │
│      🌐 IP address 192.168.1.40                                    │
│      ▥ Hardware address 6A:12:…                                    │
│      🔒 Private address: a phone or laptop that hides its real…    │
│      📱 Looks like: phone or tablet                                │
│      📡 Offers: (Apple device) (AirPlay)                           │
│      ⏱ Answers in 18 ms · no packet loss (3 pings)                 │
│      🕒 First seen 21 Nov 2026 at 09:12                            │
│      ↺ Last seen 21 Nov 2026 at 14:03                              │
│      (🚪 Check ports) (✏ Rename…) (🗑 Forget)                       │
```

---

### 6.2 Shared visual primitives on this page

#### Theme tokens used

All colours come from the active theme (see section 10). Tokens used here: `text`, `secondary`, `accent`, `warn`, `danger`, `good`, `line`, `rowStyle`, `scale` (text scale 0.8–1.6, multiplies every font size below), fonts `label(size)` (= theme font, **medium** weight, theme design) and `number(size)` (= theme font, theme weight, theme design, **monospaced digits**).

`onRow(secondary)`: on `blocks` (Color blocks) theme always **white** (secondary text = white 80 %); otherwise `text` / `secondary`.

Whenever this page uses `accent` for a pill or chip, it uses **white instead on the Color blocks theme** (because rows sit on coloured blocks). Same for `warn` / `good` verdict colours: white on Color blocks.

#### Themed row (`themedRow`, used for the internet panel and every device row)

| rowStyle (theme) | Padding | Background | Border / separator | Corner radius |
|---|---|---|---|---|
| `lines` (Instrument, Minimal light) | vertical 7, horizontal 0 | none | 1 pt bottom line in `line` | – |
| `plain` (Terminal) | vertical 5, horizontal 0 | none | none | – |
| `cards` (Glass; Liquid Glass in Low impact) | h 10, v 8 | `card` | 1 pt stroke `line` | 12 |
| `blocks` (Color blocks) | h 10, v 8 | `blocks[index % 5]` (cycling red/blue/green/purple/amber) | none | 10 |
| `glass` (Liquid Glass) | h 10, v 8 | vertical linear gradient: dark = white 11 % → 5 %; light = white 62 % → 38 % | 1 pt gradient stroke: dark = white 28 % → 6 %; light = white 95 % → black 6 % | 14 (continuous/squircle) |

The internet panel always uses index 0 (first block colour); device rows use index `i + 1` (so on Color blocks, consecutive rows cycle through the 5 block colours, starting with the second one).

"Tight" themes (`lines`, `plain`): list spacing **0** and column header has **no** horizontal inset. Other themes: list spacing **6 pt**, header inset **10 pt** each side (to align with the card padding).

#### `NetPill` (all buttons on this page)

A label button: SF Symbol + title.

| Property | Value |
|---|---|
| Font | `label(11)` |
| Foreground | `accent` (white on Color blocks) |
| Background | same colour at **15 % opacity**, rounded rect **radius 6** |
| Padding | horizontal 7, vertical 3 |
| Line limit | 1 |
| Button style | plain (no system bezel); whole pill is hit-target |

No hover effect is coded (plain style); a disabled pill uses the standard SwiftUI disabled dimming. WinUI: a `Button` with a custom template: `Border` CornerRadius 6, Background = accent @ 0.15, Padding 7,3, FontIcon + TextBlock 11 pt medium.

#### Chips (`chips(...)`, in expanded device rows)

Flow-wrapped (`NetFlow`, 4 pt spacing horizontally and vertically, wraps when the next chip would overflow). First item is plain text `"<Title>:"`. Each chip: `label(10)`, foreground `onRow()`, padding h 6 / v 2, background accent (white on blocks) **15 %**, **Capsule** shape. A chip may contain an underlined **"Open"** link (accent colour) that opens a URL in the browser (tooltip = the URL). WinUI: `ItemsRepeater` with a `WrapLayout`/`UniformGridLayout`-like flow, or a WinUI Community Toolkit `WrapPanel` (HorizontalSpacing 4, VerticalSpacing 4).

#### Detail line (`line(icon, text)`)

Label: icon in a **14 pt wide** frame, then wrapping text (never truncated, grows vertically). Font is inherited (`label(11)` in device details and the internet panel).

#### Key–value line (`kv`, internet panel details)

`HStack(alignment: firstTextBaseline, spacing 6)`: key in a **78 pt** fixed-width column (secondary colour, inherited), value in `onRow()` colour, **selectable text** (user can copy), wraps.

#### Section (`section(title)`, internet panel details)

`VStack(spacing: 3)`, title in `label(11)` `onRow()` colour, content below.

---

### 6.3 Internet panel (`InternetPanel`)

A single themed card at the top. Card content: `VStack(alignment: leading, spacing: 8)`. Default font `label(11)`, default foreground `onRow(true)` (secondary).

Top row: `HStack(alignment: .top, spacing: 10)`:
- Left column `VStack(spacing: 5)` with the summary lines (below).
- `Spacer`.
- **Live graph** (only in bands `kind`/`mac` and when connected), **170 × 46 pt**.
- **Disclosure chevron** button: SF Symbol `chevron.down` (Segoe `` ChevronDown), 11 pt **semibold**, in a 20 × 20 hit frame, colour `onRow()`. Rotates **180°** when open. Tooltip closed: "More details: connection, Wi-Fi, quality, speed test"; open: "Fewer details". Open state persists in `netPanelOpen` (default **false**, not in Prefs defaults; it's a UI-only key).
- Toggle animation: `.snappy(duration: 0.25)` (WinUI: ~250 ms ease-out; a spring with no overshoot). The details area transitions with **opacity + move from top edge**; the card is clipped so content slides out from under the top row.

#### 6.3.1 Summary lines (left column)

When connected (`NetConnection.current()` ≠ nil):

1. **Connection line** — icon `wifi` (Wi-Fi) or `cable.connector` (Ethernet/other) (Segoe: `` Wifi / `` Ethernet). Text: `"This Mac <IPv4 or –> on <Wi-Fi|Ethernet|ifname>"`, plus for Wi-Fi `" · signal <good|fair|weak> (<rssi> dBm)"`.
   - Signal word: RSSI ≥ −60 → **"good"**; ≥ −70 → **"fair"**; else **"weak"**.
2. **Stats row** — `HStack(spacing: 16)`: **Router** stat, and **Internet** stat (only if `pingHost` ≠ ""). Each stat: `HStack(firstTextBaseline, spacing 5)`: title (`onRow()`), value in **`number(15)`** (`onRow()`), `"ms"` (`onRow(true)`), and if loss > 0 `"<n>% lost"` in `warn` (white on blocks). Value `"…"` while not yet measured; otherwise `msText(avg)` (see formatting).
3. **Verdict line** (quick, plain-language judgement) — see 6.3.3.
4. **Find-the-problem line** — see 6.3.4.

When not connected: one line, icon `wifi.slash` (Segoe `` WifiWarning / ``), text **"Not connected to a network"**.

#### 6.3.2 Live graph

- Data: one reply-time sample per ~1 s for the router and for the internet host (tick: sleep 1 s + up to 1 s ping wait, so effectively ~1–2 s), last **60 points** (`graphPoints = 60`, "last two minutes" in the tooltip). A missed ping repeats the previous value (line stays flat; loss is shown in the numbers instead), first-ever miss = 0.
- Main series = internet (if `pingHost` set and data exists), else router. Second series = router (when main is internet).
- Rendering: shared `SparkView` (section 3 Live graphs): main line **1.5 pt**, round caps/joins, colour accent (white on blocks); area under main line filled with accent at **15 %** opacity; second line **1 pt** at **50 %** of the accent alpha (no fill). Curves slightly rounded (`Spark.roundness = 0.2`). Line is right-aligned and only covers `fill = (n−1)/59` of the width until 60 points exist (grows leftward from the right edge). `glides: false` here → it jumps per sample (no glide animation) unless the global setting "smoothBig" is on (default **true**, so it does glide). Y scale: 0 … max(max of both series, 5 ms) × 1.2.
- Intro: on first appearance after launch / tab switch the graph is revealed from the right (clip reveal); not in Low impact.
- Caption under the graph (right-aligned, `VStack(spacing: 2)`): `label(9)`, `onRow(true)`: **"internet — router ┄ · live"**, or **"router · live"** when only the router series exists.
- Tooltip: "Reply times over the last two minutes (max <n> ms)".

#### 6.3.3 Quick verdict (`InternetStats.verdict`)

Shown after the initial quality test; replaced (hidden) while a "Find the problem" check is running or after it has produced a finding (never two conflicting lines).

While testing: icon `hourglass`, "Testing the connection…".

Icon/colour by result: `ok == false` → `exclamationmark.triangle`, `warn` colour; `ok == true` → `checkmark.circle`, `good`; `nil` (attention) → `info.circle`, `onRow(true)`. On Color blocks always white.

Logic (inputs: wired?, RSSI, SNR, router PingStats, internet PingStats, DNS ms, internetCheck = pingHost ≠ ""):

| Condition (in order) | Text (exact) | ok |
|---|---|---|
| router got 0 **and** internet got 0 | "No answer from the router or the internet → check the connection" | false |
| internet got 0 | "The router answers but the internet doesn't → problem at your provider or modem" | false |
| router bad (loss ≥ 2 %, or avg > 30 ms, or jitter > 40 ms on Wi-Fi / > 20 ms wired) | weak Wi-Fi: "Wi-Fi signal weak and \<what\> → move closer to the router or use a cable"; wired: "\<What\> on a cable → check the cable or restart the router"; else "\<What\> → Wi-Fi interference or a busy router; restart it or use a cable". \<what\> = "\<n\>% packet loss to the router" / "slow replies from the router (\<n\> ms)" / "unsteady replies from the router (jitter \<n\> ms)" | false |
| internet bad (loss ≥ 2 %, avg > 100, jitter > 30) | "Your own network is fine, but the internet has \<what\> → most likely your provider" (\<what\> = "\<n\>% packet loss" / "unsteady replies (jitter \<n\> ms)" / "slow replies (\<n\> ms)") | false |
| weak Wi-Fi only (RSSI < −70 or SNR < 20) | "Wi-Fi signal is weak (\<rssi\> dBm) but the connection works for now; moving closer helps" | nil |
| + DNS > 150 ms (appended with ". ") | "Name lookups are slow (\<n\> ms) → try another DNS server" | nil unless above |
| nothing wrong | "All good: router \<n\> ms" (or "router doesn't answer pings") + ", internet \<n\> ms" (or " (internet check is off in Settings)") + ", no packet loss" | true |

#### 6.3.4 "Find the problem" (thorough check, ~6 s)

Pill button: icon `stethoscope` (Segoe `` Diagnostic / ``), title **"Find the problem"** (after a result: **"Check again"**). Tooltip: "Is it your Wi-Fi or the internet? About 6 s: pings your router and 1.1.1.1, times fresh DNS lookups and reads the Wi-Fi signal".

While running: small spinner + "Checking Wi-Fi, router, DNS and internet… (about 6 s)".

Method: 20 ICMP pings 0.25 s apart to router and **1.1.1.1** (fixed, independent of `pingHost`), 1 s linger; if 1.1.1.1 gets 0 replies → 5 TCP connects to 1.1.1.1:443 (1 s timeout) as substitute. In parallel 3 DNS lookups of random uncached names (`fb-<random>.apple.com`, `…cloudflare.com`, `…google.com`, NXDOMAIN counts as an answer, 3 s timeout each) → median ms. Wi-Fi RSSI/SNR/channel/band read once.

Result: one line (same icon/colour scheme as the verdict) + a numbers line below in `label(10)`, `onRow(true)`, indented **22 pt**: e.g. `router 3 ms, internet 15 ms, DNS 20 ms, Wi-Fi -55 dBm · SNR 35 dB · ch 36 (5 GHz)`; " (\<n\>% lost)" appended per target with loss; "no reply" for none; "router unknown" if no router.

Finding texts (first match wins; weak = RSSI < −72 or SNR < 20; \<local\> = "Wi-Fi" or "network" when wired):

| Condition | Text | ok |
|---|---|---|
| not connected | "Not connected to a network: turn on Wi-Fi or plug in a cable" | false |
| no router & internet dead | "No router found: reconnect to your network" | false |
| router dead & internet dead | weak → "Weak Wi-Fi signal (\<rssi\> dBm): move closer to the router or use a cable"; wired → "Router not responding: check the cable, then restart the router"; else "Router not responding: restart the router" | false |
| router answers but loss ≥ 10 % / avg > 50 / jitter > 30 | weak → weak text; wired → "The cable link to your router is unstable: check the cable, then restart the router"; else "Your Wi-Fi is unstable despite a good signal: interference or a busy router; restart the router" (+ " or switch to 5 GHz" on 2.4 GHz) | false |
| internet dead | "Your \<local\> is fine, but the internet doesn't answer: restart the modem, or your provider has an outage" | false |
| internet loss ≥ 5 % / avg > 100 / jitter > 30 | "Your \<local\> is fine; your internet provider \<loses data / is slow / is unsteady\> (router \<n\> ms, internet \<n\> ms)" | false |
| DNS no answer | "Name lookups (DNS) fail: try 1.1.1.1 as DNS server in Network settings" | false |
| DNS > 300 ms | "DNS is slow (\<n\> ms): try 1.1.1.1 as DNS server in Network settings" | false |
| weak signal only | "Everything works, but the Wi-Fi signal is weak (\<rssi\> dBm): moving closer to the router helps" | nil |
| else | "No problem found: \<local\>, router, DNS and internet are all fine" | true |

(A router that ignores pings while the internet works counts as OK.)

**Menu bar link**: the rich menu-bar panel has a quick button "Network" (icon `stethoscope`, tooltip "Find the problem") which sets `netFindNow = true` and opens the Network tab; the panel's `onAppear`/`onChange` consumes the flag and starts this check automatically.

#### 6.3.5 Expanded details

`LazyVGrid` with **adaptive columns, minimum 230 pt**, column spacing 16, row spacing 12, top-aligned, top padding 2. So: one column below ~476 pt card width, two columns when wider, three or four on very wide windows. Sections in order:

**Connection** (when connected)

| Key | Value |
|---|---|
| Type | "Wi-Fi (en0)" / "Ethernet (en5)" |
| IPv4 | dotted address or "–" |
| IPv6 | first global (2000::/3) address + " (+N)" if more; "none" |
| Router | IPv4 or "unknown" |
| DNS | up to 3 servers comma-separated, or "unknown" |
| IPv6 internet | "Yes" if a global IPv6 address and an IPv6 router exist, else "No" |

**Wi-Fi** (only on Wi-Fi with RSSI ≠ 0)

| Key | Value |
|---|---|
| Signal | "-54 dBm (good)" |
| Noise | "-92 dBm · SNR 38 dB" (only if both RSSI and noise < 0) |
| Link speed | "866 Mbit/s" (transmit rate, only if > 0) |
| Channel | "36 · 5 GHz · 80 MHz" (number · band · width; omitted entirely when macOS withholds channel info without Location permission) |
| Standard | "802.11a/b/g", "Wi-Fi 4 (802.11n)", "Wi-Fi 5 (802.11ac)", "Wi-Fi 6 (802.11ax)", "Wi-Fi 7 (802.11be)" |

Band values: "2.4 GHz", "5 GHz", "6 GHz". Width values: "20 MHz" … "320 MHz". **No SSID and no BSSID are shown anywhere** (SSID would require Location permission; BSSID is a MAC).

**Quality (10 pings)**

| Key | Value |
|---|---|
| Router | `statsText`: "3 ms (2–5) · jitter 1 · 0% lost"; "no answer" if 0 replies; "…" while testing; "–" if no router |
| Internet | same, + " (TCP)" when ICMP was blocked and TCP 443 was used; "off (Settings)" when `pingHost` is empty |
| DNS lookup | "12 ms" (time of `getaddrinfo("apple.com")`), "–"; hidden when internet check off |

Then pill **"Test again"** (icon `arrow.clockwise`; title "Testing…" and disabled while running).

Initial quality test on tab open: 10 ICMP pings 0.2 s apart to router and the resolved `pingHost` (default **1.1.1.1**), 1 s linger; if the host gets 0 ICMP replies → 10 TCP connects to host:443 (`viaTCP`, affects the live graph too). `pingHost = ""` means the internet part is off entirely (no internet traffic). Changing `pingHost` restarts the test.

**Speed test** — see 6.4. **Public IP** — see 6.5 (inside the Speed test section, after a 0.4-opacity divider with 2 pt vertical padding).

---

### 6.4 Speed test (`SpeedKind`, `SpeedMeter`, `SpeedMath`)

Server: **Cloudflare** — download `https://speed.cloudflare.com/__down?bytes=50000000` (50 MB chunks; Cloudflare refuses ≥ 100 MB per request), upload POST `https://speed.cloudflare.com/__up` with 10 MB zero-filled chunks (`application/octet-stream`), idle/loaded "ping" = `GET __down?bytes=0`. Each stream is its own ephemeral `URLSession` (own TCP connection; no HTTP/2 multiplexing, no cache). Chunks are requested back-to-back on the same stream until time, byte cap, or stop.

| | Quick | Full |
|---|---|---|
| Button | pill **"Quick"**, icon `hare` | pill **"Full"**, icon `gauge.with.dots.needle.67percent` |
| Tooltip | "About 10 s, 4 connections at once. The first 2 s, while the line speeds up, don't count." | "About 30 s, 6 connections: typical and top speed, delay while the line is busy (bufferbloat) and how steady it is." |
| Parallel streams | 4 | 6 |
| Seconds per direction | 5 | 15 |
| Warm-up seconds discarded | 2 | 3 |
| Byte cap per direction | 600 MB | 2 GB |
| Result speed | **mean** of per-second samples (after warm-up) | **median** of samples |
| Extras | – | top speed (max sample), loaded ping (median of pings every 400 ms during transfer after warm-up, worst direction), variation (coefficient of variation, worst direction) |

Sequence: idle delay = median of 5 empty requests (6 sent, the first opens the connection and is discarded) → download → upload. Sampling: every 250 ms check; every ≥ 1 s (or ≥ 0.3 s at the end) record Mbit/s = Δbytes × 8 / Δt / 1e6. Error if no samples or < 1 MB moved ("Speed test failed: \<error\>").

Under the buttons: caption `label(10)`: **"Quick uses about 90.0 MB, Full about 270.0 MB (via Cloudflare)"** — estimate = min(cap, last result's max(avg, peak) × 1.2 × secs / 8) per direction, summed; without history: "at most 1.2 GB" (Quick) / "at most 4.0 GB" (Full).

**Metered check**: before starting, `NWPathMonitor` checks `isExpensive || isConstrained` (cellular, iPhone hotspot, Low Data Mode). If metered, instead of running it shows (in `warn` colour): "This connection is metered (mobile data, iPhone hotspot or Low Data Mode). The \<quick/full\> test uses \<about X\> of data." with pills **"Run anyway"** (icon `speedometer`) and **"Not now"** (icon `xmark`). Windows: `NetworkInformation.GetInternetConnectionProfile().GetConnectionCost()` (`NetworkCostType != Unrestricted` or `ApproachingDataLimit/OverDataLimit/Roaming`).

While running (replaces the buttons): `HStack(spacing 6)`: small spinner, phase text (`onRow()`), pill **"Cancel"** (icon `xmark`); below a small linear `ProgressView(value:)` (download = 0…0.5, upload = 0.5…1, by elapsed time); below `kv("Now", "<live Mbit/s>")`.
Phase texts: "Measuring delay…", then "\<Quick|Full\> · \<Download|Upload\> · \<4|6\> connections · \<n\> s left".

Result (latest of the stored history, shown whenever history exists):

| Key | Value |
|---|---|
| Download | "412 Mbit/s" + " · top 480 Mbit/s" (Full) |
| Upload | same |
| Delay | "11 ms" + " · busy 38 ms" (Full) / "–" |
| Stability | (Full) `stabilityText`: CV < 0.10 → "steady (±4%)"; < 0.25 → "varies a little (±12%)"; else "unsteady (±31%)" |
| Tested | "21 Nov 2026 at 14:03 · Quick test" / "Full test" / "single connection" (legacy entries without kind) |

Then a bufferbloat line (Full only, when idle and loaded pings exist), same icon/colour scheme (ok true/nil/false), extra = loaded − idle:
- < 30 ms: "Delay barely rises while the line is busy (+N ms) → fine for calls and games" (good)
- < 100 ms: "Delay rises while the line is busy (+N ms) → calls may stutter during big downloads" (attention)
- else: "Delay rises a lot while the line is busy (+N ms, "bufferbloat") → calls and games suffer during downloads; a router with smart queueing (SQM) helps" (warn)

**History bars** (when ≥ 2 results): last up to **10** results, **oldest left**; `HStack(alignment: bottom, spacing 3)`, total height 26 pt, bottom-aligned. Each bar a **Capsule**, colour accent (white on blocks); newest at 100 % opacity, others **45 %**; width **9 pt for Full**, **5 pt for Quick**/legacy; height = max(3, 24 × down / max(down)). Tooltip per bar: "\<date\> · \<Quick test\>: ↓412 Mbit/s ↑38 Mbit/s". Trailing caption `label(9)`, 4 pt leading padding: **"last N · wide = Full"**.

Storage: `UserDefaults` key `speedHistory` (JSON array of `{date, down, up, ping, kind?, downPeak?, upPeak?, loadedPing?, variation?}`, Mbit/s and ms), newest first, max 10. Old entries without the optional fields must still decode.

Unit formatting `mbitText`: setting **`speedMB`** (default **false**): false → `"\(Int(round(mbit))) Mbit/s"` e.g. "412 Mbit/s"; true → one decimal MB/s `"51.5 MB/s"` (mbit / 8, always "." decimal separator, en_US). Set in Settings › Network › Speed test › Unit (segmented "Mbit/s | MB/s"; caption "Mbit/s shows 412 Mbit/s · MB/s shows 51.5 MB/s").

Windows equivalent: `HttpClient` with one `SocketsHttpHandler` per stream (`MaxConnectionsPerServer = 1`, `EnableMultipleHttp2Connections` irrelevant—force HTTP/1.1), count bytes via a custom stream/`ProgressMessageHandler`.

---

### 6.5 Public IP

Pill **"Show public IP"** (icon `globe`, Segoe `` Globe); tooltip "Asks speed.cloudflare.com which address and provider you appear as". While loading the pill title becomes "Asking Cloudflare…"; on failure "Couldn't reach Cloudflare" (the pill stays clickable to retry). Request: `GET https://speed.cloudflare.com/meta`, 8 s timeout, ephemeral session, response must be < 100 KB JSON with `clientIp` (< 64 chars).

Result replaces the pill (not persisted, gone after leaving the tab):

| Key | Value |
|---|---|
| Public IP | `clientIp` |
| Provider | `asOrganization` (if present) |
| Location | "\<city\>, \<country\> (Cloudflare \<colo\>)" e.g. "Utrecht, NL (Cloudflare AMS)" |

---

### 6.6 Toolbar

`HStack(spacing: 8)`, left to right:

| Element | Details |
|---|---|
| **Filter** | `All / Online / New / Offline`. Bands ≥ `kind`: segmented control (`.segmented`, fixed size, label hidden). Narrower: compact pop-up menu (`.menu`). Not persisted (resets to All each visit). |
| **Search field** | min 70, max 170 pt wide. `HStack(spacing 4)`: `magnifyingglass` (secondary), plain text field placeholder **"Search"** (`text` colour), when non-empty a clear button `xmark.circle.fill` (secondary, tooltip "Clear search"). Font `label(12)`, padding h 7 / v 3, background `line` at **50 %**, radius **6**. Tooltip "Filter on name, IP, MAC address or maker". Matches case-insensitively on all found names, current name, IP string, MAC string, vendor, model. |
| **Count text** (≥ `kind`) | `label(11)` secondary, 1 line: "12 online" + " · 1 new" + " · 3 offline" (parts omitted when 0). |
| Spacer (min 4) | |
| **Scan phase + progress** (only while scanning) | ≥ `kind`: phase text `label(10)` secondary ("Pinging…", "Trying quiet addresses…", "Looking up names…", "Asking devices…"). Linear time-based progress bar (`ProgressView(timerInterval: start…start+expected)`), **70 pt** wide (36 pt in narrow bands), no labels. |
| **Range pill** | ≥ `kind`: `NetPill(title: <range text> or "This network", icon: "scope")`; narrower: icon only (title "Range"). Tooltip: "Scan range: \<range or "this network (found automatically)"\>. Click to type a range." Opens the **range popover** (6.7) below (arrow at bottom edge of the pill). |
| **Bell** | Toggles `notifyNewDevice` (default **false**). Icon `bell.fill` in accent when on, `bell.slash` in secondary when off, 12 pt, frame 22 × 20. Turning on requests notification permission. Tooltips: on: "Notifying you when a new device shows up (while Fusebox scans). Click to turn off."; off: "Get a notification when a new device shows up on your network (while this page scans)". Segoe: `` Ringer / `` RingerSilent. |
| **Scan / Stop** | `NetPill` "Scan" (`arrow.clockwise`, Segoe `` Refresh) or "Stop" (`stop.fill`, Segoe `` Stop) while scanning. Tooltips: "Look for devices in the scan range" / "Stop scanning (keeps what was found so far)". |

---

### 6.7 Scan-range popover

Popover, **320 pt** wide, padding 12, `VStack(alignment: leading, spacing: 8)`, base font `label(12)`.

1. Title "Scan range" (`label(13)`).
2. Radio-style button **"This network (auto)"** with sub-line `label(10)` secondary "192.168.1.1 – 192.168.1.254" (first–last host). Icon `checkmark.circle.fill` when active (no custom range), else `circle`.
3. `HStack(spacing 6)`: rounded-border text field, placeholder **"192.168.1.0/24 or 192.168.1.10-80"** (Return submits if valid) + button **"Scan"** (disabled while input invalid/empty).
4. Help/validation line, `label(10)`, wraps:
   - empty: "A block (192.168.1.0/24), a range (192.168.1.10-192.168.1.80 or 192.168.1.10-80) or one address. Up to 4096 addresses." (secondary)
   - invalid: "Not a range. Try 192.168.1.0/24, 192.168.1.10-80 or a single address." (`danger`)
   - reversed: "The first address is higher than the last one." (`danger`)
   - too large: "That's \<n\> addresses; Fusebox scans at most 4096 at a time (a /20). Use a smaller block." (`danger`)
   - valid: "1 address" / "\<n\> addresses" (secondary), plus if any part lies outside the own subnet: ". Outside your own network: Fusebox can find devices and their names, but not hardware addresses or makers (routers don't pass those on)."
5. "Recent" header (`label(10)` secondary, 2 pt top padding) + up to **5** recent ranges as buttons (icon `checkmark.circle.fill` if active, else `clock.arrow.circlepath`). Stored newline-separated in `netRecentRanges`; a newly applied range moves to the top.

Applying (button, Return, recent item, or "This network") closes the popover and immediately rescans. The custom range is **not persisted** across visits (resets to "This network" when the page is reopened; only the recent list persists).

Range parsing: CIDR (`/0…/32`; network & broadcast excluded except /31, /32), `a.b.c.d-a.b.c.e`, short form `a.b.c.d-N` (last octet only, N < 256), or a single address; whitespace ignored; max 4096 addresses.

---

### 6.8 Column header and sorting

`HStack(spacing: 8)`; extra **14 pt trailing padding** on both header and list (room for the scrollbar, keeps columns aligned).

| Column | Header text | Width | Alignment | Notes |
|---|---|---|---|---|
| Name | "Name" | flexible (fills) | leading, header indented **26 pt** (icon 18 + spacing 8) | |
| IP | "IP" | **104** | leading | |
| MAC | "MAC" | **124** | leading | tooltip "Hardware address"; band `mac` only |
| Vendor | "Vendor" | **118** | leading | band ≥ `vendor` |
| Type | "Type" | **104** | leading | band ≥ `kind` |
| ms | "ms" | **42** | trailing | tooltip "Reply time in milliseconds" |

Header buttons: `label(10)`; active column in `text`, others `secondary`; active column shows `chevron.up` (ascending) / `chevron.down`, **8 pt bold**, 2 pt after the title. Click: same column toggles direction; new column → ascending. Default sort: **IP ascending** (not persisted).

Sort rule: **online devices always before offline ones**; within each group by the chosen column (name: localized natural compare; IP numeric; MAC string, unknown last; vendor natural; type by title; ms numeric, unknown = ∞).

---

### 6.9 Device row (`NetRow`)

Container: `VStack(spacing 0)`, font `number(12)`, colour `onRow()`, `themedRow(index: i+1)`, clipped. Summary row is tappable (whole row, `contentShape(.rect)`); offline rows draw the summary at **50 % opacity**.

Tap → expand/collapse, **one row open at a time**, animation `.snappy(duration: 0.25)`; details transition = opacity + move from top edge. No hover highlight is coded.

**Summary** `HStack(spacing 8)`:

1. Kind icon, 12 pt, 18 pt frame, `onRow(true)`.
2. Name block (`HStack(spacing 5)`, 1 line, tail truncation, fills width):
   - Name in `label(12)`.
   - Tag `label(10)` `onRow(true)`: **"This Mac"** (own Mac) or **"Router"** (default gateway).
   - **New badge**: text "New", `label(9)`, padding h 4 / v 1, foreground accent (white on blocks), background same at **18 %**, **Capsule**.
   - Offline: "last seen 5 min ago" (`label(10)`, `onRow(true)`); `agoText`: < 1 h → "\<max(1, min)\> min ago", < 24 h → "\<h\> h ago", else "\<d\> d ago".
3. IP (`number(12)`): dotted IPv4, "–" if unknown (offline device with no remembered IP).
4. MAC (band `mac`, not hidden): `"AA:BB:CC:DD:EE:FF"` uppercase colon form, **monospaced system font 11 × scale**, "–" if unknown. Tooltip for remote devices without MAC: remoteHelp (below).
5. Vendor (≥ `vendor`): `label(11)`, 1 line; "Private address" for randomized MACs; "–" unknown. Same remote tooltip.
6. Type (≥ `kind`): `label(11)`, kind title.
7. ms (trailing): online → `msText(ms)`; offline → **"off"**. Colour `warn` when the device lost any of its 3 latency pings (not on blocks). Tooltip: loss > 0 → "Some pings got no answer"; via TCP → "Measured with a TCP connection (no ping answer)".

`msText`: nil → "–"; < 1 → "<1"; else rounded integer.

**Details** (expanded; `VStack(spacing 5)`, `label(11)`, `onRow(true)`, padding leading **26**, top 6, bottom 2). Lines in order (icon → text):

| Icon (SF) | Text | When |
|---|---|---|
| `tag` | "Name from \<source\>" (or "Name from you") + " · also: name2 (src), name3 (src)" | names found |
| `tag` | "No name found; this is a guess from the maker" | no names |
| `network` | "IP address \<ip\>" (online) / "Last IP address \<ip\>" (offline) | ip known |
| `barcode` | "No hardware address or maker: this address is outside your own network, and routers don't pass those on" | remote w/o MAC (then the next two lines are skipped) |
| `barcode` | "Hardware address \<MAC\>" / "Hardware address unknown" | otherwise |
| `lock` | "Private address: a phone or laptop that hides its real address (Wi-Fi privacy). The maker can't be seen." | MAC is locally administered |
| `building.2` | "Made by \<vendor\>" / "Made by an unknown maker" | MAC not private |
| `info.circle` | "Model: \<model\>" e.g. "Model: MacBook Pro (MacBookPro18,3) · Sonos One by Sonos, Inc." | model known |
| \<kind icon\> | "Looks like: \<kind title lowercased\>" | always |
| `dot.radiowaves.left.and.right` | chips "Offers:" + sorted unique service titles | Bonjour services found |
| `door.left.hand.closed` | "No common ports open" | port check done, none open |
| `door.left.hand.open` | chips "Open ports:" each "\<name\> \<port\>" with optional "Open" link | port check found ports |
| `timer` | latency text (below) | online and not this Mac |
| `clock` | "First seen \<date, abbreviated + short time\>" | remembered |
| `clock.arrow.circlepath` | "Last seen \<…\>" | remembered |

Latency text: no ms → "Doesn't answer (many phones and TVs sleep or ignore pings; that's normal)"; via TCP → "Answers in \<n\> ms (via a TCP connection; it ignores pings)"; else "Answers in \<n\> ms · no packet loss (3 pings)" / "· \<k\> of 3 pings lost".

**Action pills** (`HStack(spacing 6)`, top padding 3):

| Pill | Icon | When | Behaviour |
|---|---|---|---|
| "Check ports" / "Checking…" / "Check again" | `door.left.hand.open` | online, not this Mac, IP known | TCP connect to 15 ports, 0.3 s timeout; disabled while checking. Tooltip "Try 15 common ports (web, file sharing, printing, …)". If the device had no ping time, the fastest TCP answer becomes its ms (via TCP). |
| "Wake on LAN" | `power` | MAC known, not this Mac, not private | sends magic packet (6×FF + 16×MAC = 102 bytes) via UDP port 9 to subnet broadcast **and** 255.255.255.255; feedback text `label(10)`: "Wake-up packet sent" / "Couldn't send the wake-up packet". Tooltip "Send a wake-up packet; works for computers and NAS that have Wake on LAN turned on" |
| "Rename…" | `pencil` | MAC known | opens rename alert |
| "Forget" | `trash` | MAC known, not this Mac | removes from remembered store immediately (no confirmation). Tooltip "Remove from the remembered devices" |

Ports checked (port → name; "Open" link URL): 22 SSH; 80 Web (`http://ip/`); 443 Web (secure) (`https://ip/`); 445 File sharing; 548 File sharing (AFP); 554 Camera stream; 631 Printing (`http://ip:631/`); 1883 MQTT; 3389 Remote desktop; 5000 NAS / web (`http://ip:5000/`); 5001 NAS (secure) (`https://ip:5001/`); 8080 Web (alt) (`http://ip:8080/`); 9100 Printer; 32400 Plex (`http://ip:32400/web`); 62078 iPhone sync. Unknown → "Port \<n\>".

**Rename alert** (system alert): title "Rename device", text field "Name" prefilled with the custom or current name, buttons **"Save"** and **"Cancel"** (cancel role), message "Leave empty to use the name Fusebox finds itself." Name trimmed, max 64 chars; empty clears the custom name. Stored keyed by MAC.

Service titles (Bonjour type → label): `_device-info`, `_companion-link` → "Apple device"; `_airplay` → "AirPlay"; `_raop` → "AirPlay audio"; `_googlecast` → "Chromecast"; `_sonos` → "Sonos"; `_spotify-connect` → "Spotify Connect"; `_ipp`, `_printer` → "Printing"; `_scanner` → "Scanning"; `_smb`, `_afpovertcp` → "File sharing"; `_workstation` → "Computer"; `_ssh`, `_sftp-ssh` → "Remote login (SSH)"; `_rfb` → "Screen sharing"; `_hap` (tcp/udp), `_homekit` → "HomeKit"; `_http` → "Web page"; `_matter` → "Matter"; `_meshcop._udp` → "Thread border router".

#### Device kinds

| Kind | Title | Noun (fallback names) | SF Symbol | Segoe Fluent suggestion |
|---|---|---|---|---|
| phone | Phone or tablet | phone | `iphone` | `` CellPhone |
| computer | Computer | computer | `laptopcomputer` | `` Laptop / `` PC |
| tv | TV / streaming | TV | `tv` | `` TVMonitor |
| speaker | Speaker | speaker | `hifispeaker` | `` Speakers |
| printer | Printer | printer | `printer` | `` Print |
| router | Router / Wi-Fi | router | `wifi.router` | `` Network / `` |
| nas | Storage (NAS) | NAS | `externaldrive.connected.to.line.below` | `` MapDrive / `` |
| smartHome | Smart home | smart device | `lightbulb` | `` Lightbulb |
| console | Game console | console | `gamecontroller` | `` Game |
| unknown | Unknown | device | `questionmark.circle` | `` Unknown / `` Help |

Other icons on this page: `scope` (Segoe ``/`` Target), `magnifyingglass` (`` Search), `xmark.circle.fill` (`` Cancel in a circle), `lock.shield` (`` Lock / `` Shield), `tag` (`` Tag), `network` (``), `barcode` (no exact Segoe glyph; use `` Fingerprint as the hardware-ID metaphor), `lock` (``), `building.2` (``/`` Company), `info.circle` (`` Info), `dot.radiowaves.left.and.right` (`` NetworkTower / ``), `door.left.hand.open/closed` (`` OpenInNewWindow / ``), `timer` (`` Stopwatch), `clock` (`` Clock), `clock.arrow.circlepath` (`` History), `power` (`` Power), `pencil` (`` Edit), `trash` (`` Delete), `hourglass` (use `` Stopwatch or a ProgressRing), `checkmark.circle` (`` Completed), `exclamationmark.triangle` (`` Warning), `stethoscope` (`` Diagnostic), `hare` (`` Speed-ish / `` LightningBolt), `gauge.with.dots.needle.67percent` (`` SpeedHigh), `speedometer` (``), `globe` (``), `cable.connector` (`` Ethernet), `wifi` (``), `wifi.slash` (``).

#### Empty states (single centred line in the list, `label(12)`, secondary, 20 pt vertical padding)

In priority order:

| Condition | Text |
|---|---|
| no network info and auto-scan on | "Not connected to a network" |
| no network info and auto-scan off | "Press Scan to look for devices" |
| search not empty | "Nothing matches “\<search\>”" (curly quotes) |
| scanning | "Looking for devices…" |
| filter ≠ All | "No \<online/new/offline\> devices" (filter title lowercased) |
| custom range | "Nothing answered in \<range\>" |
| else | "No devices found" |

#### Blocked hint (Local Network permission)

Shown between toolbar and header when the scan is blocked. `HStack(spacing 6)`, `label(11)`, colour **`warn`**: icon `lock.shield`; text "macOS is blocking the scan. Allow Fusebox under Privacy & Security › Local Network, then press Scan." (wraps); spacer; plain button **"Open"** in accent that opens System Settings › Privacy & Security › Local Network.

Detection: any of (a) ICMP sends all failed (`blocked`), (b) the ARP table is empty although local hosts replied to pings, (c) a Bonjour browser reports DNS-SD error −65570 (`kDNSServiceErr_PolicyDenied`).
Windows: no equivalent OS permission; instead detect a firewall/AV block (all sends fail) and show the same hint pointing to Windows Security › Firewall (string to be adapted).

---

### 6.10 Scanner (`NetScanner`) — method

Trigger: on tab appear if **`netAutoScan`** (default **true**; Settings › Network › Devices › "Scan when you open Network", caption "Off: Fusebox only looks for devices on your network when you press Scan."); or Scan button; or applying a range. Leaving the tab stops everything (Bonjour browsers, tasks). Each scan starts fresh (found maps cleared; remembered store kept).

Targets: own subnet from the primary interface (`State:/Network/Global/IPv4` → `PrimaryInterface`, `Router`; `getifaddrs` → IP, mask, own MAC). If the primary is a VPN (`utun*`, `ipsec*`, `ppp*`) it falls back to `en0` with no router. Subnet larger than /24 → only the /24 around the own address (max 254 hosts). /31, /32 → no neighbours. Own address excluded from probes (but shown as "This Mac"). Custom range overrides (max 4096).

Expected duration (progress bar): 8 s for own network; custom range: max(8, 4 + hosts × 0.003 + remoteHosts / 32 × 0.4).

Phases:

| # | Phase text | What happens |
|---|---|---|
| 0 | "Pinging…" | In parallel: start Bonjour browsing (22 service types, only if any target is local); load IEEE OUI list off-main; SSDP M-SEARCH (sent twice 0.3 s apart to 239.255.255.250:1900, TTL 2, listen 3 s; only local). |
| 1 | "Pinging…" | ICMP echo sweep to all targets: 2 rounds 1.0 s apart, 3 ms gap between packets, 0.85 s linger (unprivileged datagram ICMP socket; payload "Fusebox!"). Then read the kernel ARP table (sysctl `NET_RT_FLAGS`/`RTF_LLINFO`), keep entries on the scan interface inside the local subnet (skip broadcast/multicast MACs). Anything with an ARP entry, a ping reply, or a Bonjour hit counts as found. |
| 1b | "Trying quiet addresses…" | Only for targets **outside** the own subnet that didn't answer: TCP connect to ports 80 and 443 (0.4 s timeout, 64 at a time); SYN-ACK or RST both mean "someone is there". |
| 2 | "Looking up names…" | Concurrently: mDNS reverse PTR (`x.x.x.x.in-addr.arpa`, QU bit) to 224.0.0.251:5353 **and** unicast to each device, TTL 255, wait 1.5 s (local only); NetBIOS node-status (UDP 137, first unique name with suffix 0x00), wait 1.5 s; reverse DNS via `getnameinfo` (16 parallel, 1.5 s); latency = 3 pings 0.3 s apart, 1 s linger → average ms + loss (0, 1/3, 2/3, 1). Devices with no ping reply: TCP connect to 62078, 80, 443, 7000 (0.5 s) → ms "via TCP". |
| 3 | "Asking devices…" | For SSDP responders: follow `LOCATION` only if `http`, same LAN IP as sender, private range; minimal HTTP/1.0 GET (no TLS, no redirects, no chunked, ≤ 64 KB, 2 s), ≤ 6 in parallel, max 64 devices, ≤ 3 URLs each. Parse UPnP XML (`friendlyName`, `manufacturer`, `modelName`, `deviceType`; reject DOCTYPE/ENTITY, UTF-16, > 64 KB). |
| 4 | – | Let Bonjour keep running until 7.5 s after start (names trickle in), stop browsing, update the remembered store, notify new devices, end. |

All network replies are treated as untrusted (length limits: names ≤ 64 chars, vendor from UPnP ≤ 40, XML values ≤ 100, ≤ 400 Bonjour resolutions, ≤ 1024 UDP replies).

**Naming** (priority order, duplicates case-insensitively removed; the first is the displayed name): "Your name" (custom rename) → "This Mac" (computer name) → Bonjour → NetBIOS → mDNS → DNS → UPnP friendly name → "Remembered" (last name from earlier scans). Bonjour name cleaning: `_raop`/`_sonos` "MAC@Name" → "Name"; `_workstation` "host [aa:bb:…]" → "host" (the MAC in brackets is stripped); `_googlecast` uses TXT `fn`; `_matter` names ignored (hex id). Within Bonjour, the service earliest in the list wins: device-info, companion-link, airplay, raop, googlecast, sonos, spotify-connect, ipp, printer, scanner, smb, afpovertcp, workstation, ssh, sftp-ssh, rfb, hap(tcp), hap(udp), homekit, http, meshcop(udp), matter. Hostnames are shortened to the first label ("alex-iphone.fritz.box" → "alex-iphone"); pure IPs are not names.

Fallback name when nothing found: "Router" for the gateway; "Unknown device" if vendor unknown or MAC private; else "\<vendor short\> \<kind noun\>" (vendor kept if ≤ 16 chars, else its first word), e.g. "Sonos speaker", "Samsung TV".

**Model**: Bonjour TXT `model`/`am`/`rpMd`/`md`/`ty`/`usb_MDL` (device-info wins); Apple identifiers mapped ("MacBookPro18,3" → "MacBook Pro (MacBookPro18,3)", Macmini → "Mac mini", AudioAccessory5 → "HomePod mini", AppleTV, iPhone, iPad, Watch, RealityDevice → "Apple Vision Pro" …); UPnP modelName appended " by \<manufacturer\>"; joined with " · ".

**Vendor (OUI)**: first 3 MAC bytes. First a built-in short table (Apple, Samsung, Sonos, Google, Google Nest, Amazon, TP-Link, Ubiquiti, Netgear, AVM (Fritz!), Signify (Philips Hue), Raspberry Pi, Espressif, Intel, Xiaomi, Huawei, Sony, LG, HP, Canon, Epson, Brother, Nintendo, Microsoft, ASUS, Synology, QNAP, Ring, Roku, Bose) so kind-guessing recognises them; otherwise the bundled IEEE list `Resources/oui.tsv` (~40,500 lines, format `AABBCC<TAB>Maker`, loaded once off the main thread). Vendor names tidied: all-caps words of ≥ 4 letters → Title case ("NETGEAR" → "Netgear", "HUAWEI TECHNOLOGIES" → "Huawei Technologies"; "TP-LINK", "LG" unchanged), max 40 chars; "IEEE Registration Authority" and "Private" entries ignored. If no MAC vendor, UPnP manufacturer (≤ 40 chars). **Private (locally administered) MAC** = bit 0x02 of first byte set → vendor shows "Private address", no maker lookup.

**Kind guess** (first match wins): router (is gateway) → computer (is this Mac) → printer (IPP/printer/scanner service or HP/Canon/Epson/Brother/Xerox/Lexmark/Kyocera) → console (xbox/playstation/nintendo) → phone (iphone/ipad/galaxy/pixel/android/phone) → computer (mac/laptop/desktop/thinkpad/-pc/windows) → speaker (homepod/audioaccessory/sonos/speaker/echo/nest mini/home mini/soundbar/zoneplayer, Sonos/Bose) → tv (appletv/apple tv/tv/chromecast/roku/bravia/fire/mediarenderer, googlecast service, Roku) → speaker (sonos/spotify-connect/raop service) → router (internetgatewaydevice/router/access point) → smart home (HomeKit/Matter/Thread services, or hue/plug/bulb/thermostat/camera/doorbell/esp/bridge, or Signify/Espressif/Ring/Nest/tado/Shelly/Tuya/IKEA/Lumi) → nas (nas/diskstation/synology/qnap) → computer (rfb/workstation/ssh/afp/smb, or Intel/ASUS/Microsoft/Raspberry Pi/Dell/Lenovo/Hon Hai/LiteOn/AzureWave) → router (TP-Link/Ubiquiti/Netgear/AVM/eero/Zyxel/Sagemcom/Arcadyan/Technicolor) → phone (private MAC) → unknown. Keywords are matched in all names + model + UPnP fields, vendor by prefix.

**Remote devices** (outside own subnet): no ARP → no MAC, no vendor, no Bonjour/mDNS/SSDP; tooltip remoteHelp: "Outside your own network: routers don't pass on hardware addresses, so the MAC address and maker can't be seen".

---

### 6.11 Remembered devices, "New", offline, notifications

Store: JSON at **`~/Documents/Fusebox/network-devices.json`** (pretty-printed, sorted keys, ISO-8601 dates, atomic write): `{created, devices: {"AA:BB:CC:DD:EE:FF": {name?, custom?, ip?, net?, first, last}}}`. Keyed by MAC (only devices with a MAC are remembered). `net` = "\<network\>/\<prefix\> via \<router\>" to tell home and office apart. Windows: `%USERPROFILE%\Documents\Fusebox\network-devices.json`.

- After each completed scan, every online device with a MAC updates `last`, `ip`, `net` and `name` (best found name excluding Your name/Remembered/This Mac). New MACs get `first = now`.
- **Offline rows**: when scanning the own network (no custom range), the ARP table was readable and not blocked, remembered devices from the **same network key** (or with no key) that weren't seen are added as offline rows (id "off \<MAC\>", 50 % opacity, ms "off", "last seen …"). Their name: custom or remembered; kind guessed from name + vendor.
- **New** = first seen **today** and more than 60 s after the store was created (so the very first scan doesn't mark everything new). Shown as the "New" capsule badge and filterable.
- **Notification** (if `notifyNewDevice` on and the store is older than 60 s): for at most **3** newly remembered devices per scan: title **"New device on your network"**, body "\<name\> · \<ip\>" + " · \<vendor\>" when known. Only while the page scans (no background scanning).

---

### 6.12 Bluetooth devices and mobile uplink (rendered on the Docked tab)

`Draadloos.swift` feeds rows into the Docked page tree (see section 7 for row visuals); listed here because they are network-ish.

Bluetooth source: `system_profiler -json -timeout 10 SPBluetoothDataType` (off main, ~0.1–0.3 s), cached and shared: at most every **2 min** while the Docked page is open (re-checked by a 2 s timer), at most every **5 min** for the Live page's large battery card fact. No permission prompt (IOBluetooth is deliberately not used). Windows: `Windows.Devices.Enumeration` with `BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Connected)` / `BluetoothLEDevice`, battery via GATT Battery Service (0x180F) or the `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` device property.

- Controller: state on/off (`attrib_off`), chipset, firmware, transport (shown as State / Chip / Firmware / Bus pairs).
- Group "Bluetooth" (icon `antenna.radiowaves.left.and.right`) shown if Bluetooth is off or ≥ 1 device connected; fact "\<n\> connected" or "off". Children = connected devices sorted by name. Paired-but-not-connected devices are a separate collapsed, dimmed list (no battery value).
- Device kind from minorType (fallback by name): Keyboard `keyboard`; Mouse `computermouse`; Trackpad `rectangle.and.hand.point.up.left`; Headphones `headphones` / Earbuds `airpods` (when L/R batteries exist); Speaker `hifispeaker`; Controller `gamecontroller`; Phone `iphone`; Watch `applewatch`; Computer `laptopcomputer`; Pen `applepencil`; Tablet `ipad`; else minorType text or "Device" with `dot.radiowaves.left.and.right`.
- Batteries: Main, Left ("L"), Right ("R"), Case ("case"). Single value = main, else the lowest earbud (case ignored). Text when > 1: "L 81% · R 64% · case 40%". Row main value "64 %" with a battery icon; **≤ 20 %** = "slow"/attention styling; **≤ 10 %** warning "Battery low. Charge it soon."
- Signal: RSSI ≥ −60 "strong", ≥ −75 "fair", else "weak"; facts "signal \<word\>" (connected) or "in range" (paired with RSSI).
- Detail pairs: Type, Battery/Left/Right/Case, Signal "-58 dBm (strong)", Profiles "A2DP, AVRCP, HFP", Firmware, IDs "vendor 004C (Apple) · product B000".
- Live-page battery card fact: the lowest connected device, e.g. ("AirPods", "L 80% · R 75%") or ("Mouse", "64%").

Mobile uplink (`Uplink`): from `NWPathMonitor` (first non-VPN, non-loopback interface; `isExpensive`, `isConstrained`) + SystemConfiguration interface type/name + router. Rules: name contains iPhone/iPad → "Internet via iPhone (USB)" / "…iPad (USB)"; Bluetooth type/name → "Internet via Bluetooth tethering"; cellular/WWAN/`pdp_ip*`/`wwan*` or name with modem/LTE/4G/5G/cellular/mobile broadband → "Internet via mobile modem" (icon `antenna.radiowaves.left.and.right`); Wi-Fi with router 172.20.10.x → "Internet via iPhone hotspot"; Wi-Fi marked expensive → "Internet via phone hotspot"; other expensive → "Internet via a metered connection" (icon `gauge.with.dots.needle.33percent`); default icon `personalhotspot`; otherwise nothing shown. Row value = download rate with unit + " ↓" (respects `netBits`), facts: via, "metered", "Low Data Mode", "↑ \<up\> \<unit\>". Pairs: Interface, Through, Metered ("Yes: macOS and apps may hold back big downloads and backups" / "No"), Low Data (On/Off), Seen from (how it was detected), Now "↓ x · ↑ y unit". Rate from 32-bit interface byte counters every 2 s (wrap-safe). **No SSID** (needs Location permission). Windows: `NetworkInformation` connection profile (`IsWwanConnectionProfile`, `GetConnectionCost`), `GetIfTable2` for byte counters.

---

### 6.13 Privacy rules on this tab (exact)

| Data | Shown? |
|---|---|
| **MAC addresses of LAN devices (Network tab)** | **Yes, by design** — this is a LAN scanner: MAC column (only at ≥ 760 pt, can be hidden via `netHide` "mac"), "Hardware address …" detail line, searchable. Settings preview example "B8:27:EB:01:02:03". The store file is keyed by MAC (local only). |
| MAC embedded in Bonjour names (`_workstation` "host [aa:bb:…]", RAOP "ABCDEF@Name") | Stripped from names |
| Wi-Fi SSID / BSSID | Never read or shown |
| Bluetooth device addresses and serial numbers (Docked) | **Never shown** — raw key/value lists drop any key containing "address" or "serial"; selftest asserts no "AA:BB" appears |
| Public IP | Only after an explicit click; never stored |
| Internet traffic | Only to `pingHost` (default 1.1.1.1; empty = none), 1.1.1.1 for "Find the problem" (click), Cloudflare (click) |
| LAN probes | Only while the tab is visible or after Scan; HTTP fetches only to private-range LAN IPs that announced themselves |

Note for the Windows port: if the product rule "never show MAC addresses" must apply globally, the Network tab's MAC column/detail is the one deliberate exception in the macOS source; keep it hideable and consider defaulting `netHide` to include `mac` only if the product owner decides so.

---

### 6.14 Settings that affect this tab (Settings › Network, and Privacy)

| Key | Default | UI | Effect |
|---|---|---|---|
| `netAutoScan` | true | Toggle "Scan when you open Network" | auto scan on appear |
| `notifyNewDevice` | false | Toggle "Notify me about new devices" ("While Network scans. Same as the bell on the page.") | new-device notifications; same as bell |
| `netHide` | "" | Chips "MAC", "Vendor", "Type" + table preview row "Living Room · 192.168.1.5 · B8:27:EB:01:02:03 · Raspberry Pi · Computer · 3"; footer "Columns appear when the window is wide enough." | hides columns |
| `speedMB` | false | Segmented "Mbit/s / MB/s" | speed unit |
| `pingHost` | "1.1.1.1" | Shown read-only in Network settings ("Ping" — "On, to 1.1.1.1" / "Off: Fusebox makes no internet connections", button "Change in Privacy"); edited in Settings › Data & privacy ("Measure to" field) | internet ping target; "" disables internet checks |
| `warnPing` | 100 ms | Settings › Alerts ("Latency", 20–300, step 10) | used by the Live page network ping colouring, **not** by this tab |
| `netPanelOpen` | false (implicit) | chevron | internet panel expanded |
| `netRecentRanges` | "" | – | recent scan ranges (≤ 5) |
| `speedHistory` | – | – | last 10 speed results |

---

### 6.15 macOS data sources → Windows equivalents

| Data | macOS | Windows |
|---|---|---|
| Primary interface, router, DNS | SystemConfiguration dynamic store `State:/Network/Global/IPv4/IPv6/DNS`, `getifaddrs` | `GetBestRoute2`/`GetIpForwardTable2` (default route), `GetAdaptersAddresses` (IPs, mask, MAC, DNS, gateway) |
| Wi-Fi RSSI, noise, rate, channel, band, width, PHY | CoreWLAN `CWInterface` | `WlanQueryInterface` (`wlan_intf_opcode_current_connection`, `wlan_intf_opcode_rssi`), `WlanGetNetworkBssList` for channel/frequency; noise not available (omit Noise/SNR row) |
| ICMP ping | unprivileged `SOCK_DGRAM` ICMP socket | `IcmpSendEcho2` / `System.Net.NetworkInformation.Ping` (async, many in parallel) |
| ARP table (MACs) | sysctl `NET_RT_FLAGS` | `GetIpNetTable2` (filter `NlnsReachable/Stale`, interface index) |
| Bonjour browsing | `NWBrowser` | `Windows.Networking.ServiceDiscovery.Dnssd` / `DnssdServiceWatcher`, or `DnsServiceBrowse` (Win10+) |
| mDNS / NetBIOS / SSDP | raw UDP | same raw UDP via `Socket` (NetBIOS: also `NetServerEnum` is not needed) |
| Reverse DNS | `getnameinfo` | `Dns.GetHostEntryAsync(IPAddress)` |
| TCP probes | non-blocking `connect` + `poll` | `Socket.ConnectAsync` with `CancellationToken` timeout |
| Wake on LAN | UDP broadcast port 9 | same (`UdpClient`, `EnableBroadcast = true`) |
| Metered connection | `NWPathMonitor.isExpensive/isConstrained` | `ConnectionProfile.GetConnectionCost()` |
| Notifications | `UNUserNotificationCenter` | Windows App SDK `AppNotificationManager` |
| Open Local Network settings | `x-apple.systempreferences:…Privacy_LocalNetwork` | `ms-settings:windowsdefender` / `ms-settings:privacy` (adapt text) |
| Bluetooth list + batteries | `system_profiler SPBluetoothDataType` | `Windows.Devices.Bluetooth` + device property battery level |

---

### 6.16 Exact strings (Network tab, English source)

Toolbar & list: "All", "Online", "New", "Offline", "Search", "Clear search", "Filter on name, IP, MAC address or maker", "\<n\> online", " · \<n\> new", " · \<n\> offline", "This network", "Range", "Scan", "Stop", "Pinging…", "Trying quiet addresses…", "Looking up names…", "Asking devices…", "Name", "IP", "MAC", "Vendor", "Type", "ms", "Hardware address", "Reply time in milliseconds", "This Mac", "Router", "New", "last seen \<ago\>", "off", "Unknown device", "Private address".

Range popover: "Scan range", "This network (auto)", "Recent", "1 address", "\<n\> addresses".

Detail lines: "Name from you", "Name from \<source\>", " · also: ", "No name found; this is a guess from the maker", "IP address \<ip\>", "Last IP address \<ip\>", "Hardware address \<mac\>", "Hardware address unknown", "Made by \<vendor\>", "Made by an unknown maker", "Model: \<m\>", "Looks like: \<kind\>", "Offers", "Open ports", "No common ports open", "Open", "First seen \<date\>", "Last seen \<date\>", "Remembered", "Your name".

Actions: "Check ports", "Checking…", "Check again", "Wake on LAN", "Wake-up packet sent", "Couldn't send the wake-up packet", "Rename…", "Forget", "Rename device", "Save", "Cancel", "Leave empty to use the name Fusebox finds itself."

Internet panel: "This Mac \<ip\> on \<kind\>", " · signal \<word\> (\<rssi\> dBm)", "good", "fair", "weak", "Router", "Internet", "\<n\>% lost", "Testing the connection…", "Find the problem", "Check again", "Checking Wi-Fi, router, DNS and internet… (about 6 s)", "Not connected to a network", "More details: connection, Wi-Fi, quality, speed test", "Fewer details", "Connection", "Wi-Fi", "Quality (10 pings)", "Speed test", "Type", "IPv4", "IPv6", "none", "unknown", "DNS", "IPv6 internet", "Yes", "No", "Signal", "Noise", "Link speed", "Channel", "Standard", "DNS lookup", "off (Settings)", "no answer", " · jitter \<n\>", "Test again", "Testing…", "Quick", "Full", "Run anyway", "Not now", "Cancel", "Now", "Download", "Upload", "Delay", " · busy \<n\> ms", " · top \<x\>", "Stability", "Tested", "Quick test", "Full test", "single connection", "last \<n\> · wide = Full", "Public IP", "Provider", "Location", "Show public IP", "Asking Cloudflare…", "Couldn't reach Cloudflare", "Measuring delay…", "Speed test failed: \<error\>", "about \<bytes\>", "at most \<bytes\>".

Notification: "New device on your network".

---

## 7. Docked tab

Tab id `ports`, title **"Docked"** (never translated), tab icon SF Symbol `dock.rectangle` (Segoe Fluent: `E7F4` "TVMonitor"/`ECF0` "Dock" — pick the closest "dock/plug" glyph, e.g. `E83E` "Connect"/`E957` "USB"). Minimum window size when this tab is shown: **420 × 460 pt** (the window grows to this, never shrinks).

Purpose: at the top, what power the Mac is receiving right now; below it, one short tree per physical port: **port → cable → charger/dock/device → devices behind it**, plus displays, the 3.5 mm headphone jack, wireless (Bluetooth devices, phone hotspot/modem) and "Other devices". One number on the right of every row. Click a row for details. All data is read-only, no admin rights, no permission prompts.

Source files: `Poorten.swift` (UI + tree building), `PoortenData.swift` (parsing, IOKit reading, displays, audio jack, watcher), `Draadloos.swift` (wireless rows, shared with this page).

### 7.1 Layout sketches

Wide window (list + detail side by side only fits when the HStack can be **≥ 900 pt** wide; `ViewThatFits` picks it):

```
┌────────────────────────────────────────────────────────────────────────────────────────────┐
│ ●●●  [tabs…]                                                                    ⚙          │
├────────────────────────────────────────────────────────────────────────────────────────────┤
│ ┌───────────────────────────────── header card (themedRow) ──────────────────────────────┐ │
│ │ 61.4 W                                                                   82 %          │ │
│ │ into this Mac                                              charging · full in 1h 5m    │ │
│ │ (⚡ 96 W charger) (⇄ 10 Gbit/s link) (⋰ 3 devices) (📶 2 Bluetooth) (🖥 1 display)      │ │
│ └────────────────────────────────────────────────────────────────────────────────────────┘ │
│                                                         ⋰ Show hubs & internals            │
│ ┌──── port group card (max 560 wide) ─────────┐  ┌──── detail card (fills rest) ───────┐   │
│ │ ⌁ USB-C port 1                10 / 40 Gbit/s│  │ ⌁ Cable in USB-C port 1             │   │
│ │   USB 3 · 61.4 W in                         │  │ [! note box …]                      │   │
│ │ └─⌁ Cable                     10 / 40 Gbit/s│  │ CABLE              MEASURED BY …    │   │
│ │   │ passive · 100 W · ~1 m                  │  │ Type   Passive …   USB 3  USB 3.2 … │   │
│ │   └─▭ Dock                         max 96 W │  │ Data   USB4 …                       │   │
│ │       61.4 W to Mac · 20 V × 4.8 A          │  │ …                                   │   │
│ │       ├─⌨ Magic Keyboard          12 Mbit/s │  │                                     │   │
│ │       └─▢ T7 SSD             10 Gbit/s      │  │                                     │   │
│ └─────────────────────────────────────────────┘  └─────────────────────────────────────┘   │
│ ◌ USB-C port 2 · USB-C port 3 · Headphone jack · empty                                     │
│ ∿ WIRELESS                                                                                 │
│ ┌ Bluetooth  2 connected ──────────────────────┐                                           │
│ │ ├─ AirPods Pro          🔋 64 %               │                                           │
│ │ └─ Magic Mouse          🔋 7 %  ▲ Battery low │                                           │
│ └──────────────────────────────────────────────┘                                           │
│   › Bluetooth · paired, not connected  3                                                   │
│ › Other devices  2                                                                         │
└────────────────────────────────────────────────────────────────────────────────────────────┘
```

Narrow window (anything below the 900 pt wide layout): one column, list max **600 pt** wide (left-aligned, so numbers stay near names). Clicking a row expands its details **inline under that row**, indented 29 pt, inside the same tree lines:

```
┌───────────────────────────────────────┐
│ 61.4 W                     82 %       │
│ into this Mac   charging · full in 1h │
│ (⚡96 W charger)(⇄10 Gbit/s link)      │
│ (⋰3 devices)                           │
├───────────────────────────────────────┤
│              ⋰ Show hubs & internals  │
│ ┌───────────────────────────────────┐ │
│ │ ⌁ USB-C port 1     10 / 40 Gbit/s │ │
│ │ └─⌁ Cable          10 / 40 Gbit/s │ │  ← selected: accent 12 % fill
│ │   │   [details of cable here]     │ │
│ │   └─▭ Dock               max 96 W │ │
│ └───────────────────────────────────┘ │
│ ◌ USB-C port 2 · empty                │
│ › Other devices 2                     │
└───────────────────────────────────────┘
```

### 7.2 Page container

| Property | Value |
|---|---|
| Root | `ScrollView` (vertical), content padding horizontal **12**, bottom **12**; `.clipped()` so content never scrolls under the transparent toolbar |
| Content stack | `VStack(alignment: .leading, spacing: 14)` |
| Order top → bottom | 1. Header card ("into this Mac") with top padding 2 · 2. right-aligned "Show/Hide hubs & internals" button · 3. empty-state text (only if no ports) · 4. port list (`DockedList`) incl. empty-ports line and Wireless block · 5. "Other devices" disclosure |
| Lifetime | Watchers start on appear, stop on disappear. Nothing is read while the tab is not visible. |

### 7.3 Header card: "what goes into the Mac" (`IntoMac`)

Card style: `.themedRow(theme)` (theme row style: lines/plain/cards/blocks/glass — see Themes section; e.g. cards = padding h10 v8, `theme.card` fill, 12 pt radius, 1 pt `theme.line` stroke; glass = 14 pt continuous radius gradient fill). Full width, leading aligned. Inner `VStack(spacing: 10)`.

Row 1 (`HStack(alignment: .firstTextBaseline, spacing: 10)`):

| Element | Content | Style |
|---|---|---|
| Left big number | Watts in = `PowerNow.mW / 1000` **only when external power is connected** (`ExternalConnected`); else `"0"`. Unit `"W"`. | `BigNum` size **34**: value `theme.number(34)` (monospaced digits, theme weight/design), color `onRow()`; unit `theme.label(34×0.42 ≈ 14.3)` color `onRow(true)`, spacing 3, baseline aligned; `lineLimit(1)`, `minimumScaleFactor(0.6)` |
| Caption under it | `"into this Mac"` if external power, else `"on battery"` | `theme.label(10)`, `onRow(true)` (secondary) |
| Spacer | min 8 | |
| Right block (only if battery percent known) | `BigNum` size **24**: `"\(Int(percent.rounded()))"` + unit `"%"` | trailing aligned |
| Caption under it | battery text, see below | `theme.label(10)`, secondary, 1 line |

Battery text rules (`batteryText`):

| Condition | Text |
|---|---|
| fully charged | `battery full` |
| charging | `charging` + (if minutes known) ` · full in {duration}` |
| external power but not charging | `not charging` |
| on battery, minutes known | `{duration} left` |
| on battery, unknown | `on battery` |

`duration(minutes)`: ≥ 1440 → `"{d}d {h}h"`; ≥ 60 → `"{h}h {m}m"`; else `"{m}m"`. Percent and minutes come from the same sampler as Live (`Sampler.battery`).

Row 2: stat chips in a wrapping flow layout (`NetFlow`, spacing **6** both directions; wraps to the next line when a chip doesn't fit). Each chip = `HStack(spacing: 4)` [icon `.system(size: 9 × textScale)` secondary] [value `theme.number(11)` primary] [label `theme.label(10)` secondary], padding h **7** v **3**, background `theme.tile` in a **capsule**. `theme.tile` = `onRow().opacity(0.06)` (blocks style: `white.opacity(0.14)`).

| Chip (order) | Shown when | Icon (SF → Segoe Fluent) | Value | Label |
|---|---|---|---|---|
| Charger | any port has a charger | `bolt.fill` → `E945` LightningBolt | max watts of the strongest charger, `trim()` + ` W` | `charger` |
| Link | fastest data link among ports that have a non-DisplayPort link | `arrow.left.arrow.right` → `E8AB` Switch | `speedParts(bps)` joined by space, e.g. `10 Gbit/s` | `link` |
| Devices | total device count > 0 (USB leaf devices excl. Billboard + Thunderbolt devices) | `point.3.connected.trianglepath.dotted` → `E8A1`/network-ish `EC27` | count | `device` / `devices` |
| Bluetooth | wireless shown and ≥1 BT device connected | `antenna.radiowaves.left.and.right` → `E702` Bluetooth | count | `Bluetooth` |
| Displays | external displays (not built-in) > 0 | `display.2` → `E7F4` TVMonitor | count | `display` / `displays`; tooltip = one line per display (all, incl. built-in): `Built-in: 3024 × 1964 · 120 Hz` / `{name}: {w} × {h} · {hz} Hz` (built-in uses its native max resolution) |

### 7.4 "Show hubs & internals" toggle button

Right-aligned (`HStack(spacing: 14)` with leading `Spacer`). Plain button, `Label` with icon `point.3.filled.connected.trianglepath.dotted`, `theme.label(11)`, secondary color. Text toggles: **"Show hubs & internals"** / **"Hide hubs & internals"**. Toggling animates with `.snappy(duration: 0.25)`. Persisted as pref `dockedInternals` (default **false**); same pref as the Settings toggle.

### 7.5 Empty state

Only after the first read finished (`loaded`) and there are zero IOPort ports: `"No USB-C or MagSafe ports found on this Mac."` — `theme.label(12)`, secondary, vertical padding 20. (Before the first read nothing is shown; there is no spinner.)

### 7.6 Port list (`DockedList`)

Selection state `sel` (one row id, or none) is local to the page (not persisted).

Layout choice with `ViewThatFits(in: .horizontal)`:
1. **Wide**: `HStack(alignment: .top, spacing: 16)` with min width 900: list `frame(maxWidth: 560)` on the left, and on the right a detail card (`themedRow`) filling remaining width. Detail card shows the selected node's details, or the placeholder **"Click a row for its details."** (`theme.label(11)`, secondary).
2. **Narrow**: list only, `frame(maxWidth: 600, alignment: .leading)`; details appear inline under the clicked row.

List = `VStack(alignment: .leading, spacing: 14)`:
1. One card per group (`PortGroupList(...).themedRow(theme, index: i)` — the `index` matters for the Blocks theme, which cycles block colors per card).
2. Empty-ports line (if pref `dockedEmpty`, default **true**): `Label` with icon `circle.dashed` (Segoe: `EA3A` CircleRing), text = titles of all unconnected ports + `"Headphone jack"` (only if the jack is empty), joined by `" · "`, followed by `" · empty"`. E.g. `USB-C port 2 · USB-C port 3 · Headphone jack · empty`. Font `theme.label(11)`, color secondary at **75 %** opacity, leading padding 4.
3. Wireless block (pref `dockedWireless`, default **true**; only when there is something): header `Label("WIRELESS", systemImage: "wave.3.right")` (literal uppercase), `theme.label(10)`, secondary, top padding 4; then one card per wireless group (block index continues after the port groups); then a disclosure **"Bluetooth · paired, not connected"** with count, leading padding 4, containing paired-but-not-connected devices (no card, dimmed rows).

#### 7.6.1 Group order

1. Connected ports, sorted: non-MagSafe first by port number, then MagSafe (`sort by (isMagSafe ? 1 : 0, number)`).
2. Headphone-jack group (only when something is plugged in).
3. "Displays" group (external displays that could not be attached to a port), if any.
4. Wireless groups: hotspot/modem uplink row first, then "Bluetooth".

### 7.7 Tree row anatomy (`PortGroupList`)

The tree is flattened depth-first; each row knows, per ancestor level, whether that ancestor was the last child.

Row = `HStack(alignment: .top, spacing: 0)`:

**Connector columns** (one per depth level, each **16 pt** wide, full row height, stroked `onRow(true).opacity(0.4)`, line width **1.5**):

| Column kind | Drawn | ASCII |
|---|---|---|
| `pass` (ancestor not last) | vertical line top→bottom at x = midX − 2 | `│` |
| `none` (ancestor was last) | nothing | ` ` |
| `tee` (this row, not last child) | vertical top→bottom, plus horizontal from x to maxX + 2 at y = 15 | `├─` |
| `elbow` (this row, last child) | vertical from top to y = 11 (15 − 4), then a quadratic curve to (x+4, 15) with control point (x, 15) — a rounded corner, radius ≈ 4 — then horizontal to maxX + 2 | `└─` (rounded) |

y = 15 pt is the vertical centre of the title line. The horizontal stub overshoots 2 pt into the next column so it touches the icon area. Root rows (ports) have no connector columns.

**Content** (`VStack(spacing: 6)` → inner `VStack(spacing: 4)`):

| Part | Spec |
|---|---|
| Icon | SF Symbol, `.system(size: 11 × textScale)`, secondary color, frame width 16, top padding 2 |
| Gap icon→text | 7 |
| Title | `theme.label(13)` **semibold** for ports, `theme.label(12)` semibold for all other kinds; 1 line; color `onRow(dim)` → dimmed rows (hubs, Billboard, paired BT) use the secondary color |
| ⓘ info icon (if node has `info`) | `info.circle`, size 10 × scale, secondary, spacing 5 after title; tooltip (hover) = the info text |
| Facts line | up to **4** facts joined by `" · "`, `theme.label(10)`, secondary, 1 line, spacing 1 under the title |
| Spacer | min 6 |
| Right value | `LoadView` size **16** for ports, **14** for others, `fixedSize` (never truncated) |
| Warning line | `Label(text, systemImage: "exclamationmark.triangle.fill")`, `theme.label(10)`, color `theme.warn` (white on Blocks), wraps (fixed vertical), leading padding **23** (aligns with the title) |
| Row padding | vertical 5, horizontal 6 |
| Selected | background `theme.accent.opacity(0.12)` in a rect, corner radius **7** |
| Not selected | clear (no hover highlight) |
| Hit area | whole row rect (`contentShape(.rect)`) |
| Click | toggles selection (click again = deselect), animated `.snappy(duration: 0.2)` |
| Inline details (narrow only) | under the row, leading padding **29**, bottom padding 8, transition `.opacity` |

Row height is intrinsic (`fixedSize(horizontal: false, vertical: true)`); the connector columns stretch to full row height so vertical lines are continuous between rows (rows are stacked with spacing 0).

### 7.8 Right-hand number (`LoadView`, `LinkLoad`)

`LinkLoad { now, max, power, slow, unit, icon }`. Formatter per value: if `unit` set → (`trim(x)`, unit); else if `power` → (`trim(x)`, `"W"`); else data → `speedParts(x)`.

`speedParts(bps)`: default bits — ≥ 1e9 → `trim(bps/1e9)` `"Gbit/s"`, else `trim(bps/1e6)` `"Mbit/s"`. With pref `dockedBytes` = true: bytes = bps/8, ≥ 1e9 → `"GB/s"`, else `"MB/s"`.

`trim(x)`: round to 1 decimal, drop `.0` (`16.97 → "17"`, `17.53 → "17.5"`). `volt(mV)`: `trim` of volts at 0.1 V + `" V"` (`20 V`, `20.4 V`). `amp(mA)`: whole/tenth amps via `trim` (`3 A`, `4.7 A`), otherwise 2 decimals (`0.86 A`) + `" A"`.

| Case | Rendering | Example |
|---|---|---|
| now + max, same unit | `{now}` big + `/ {max} {unit}` small | `10 / 40 Gbit/s` |
| now + max, different unit | `{now}` big + `{unitNow} of {max} {unitMax}` small | `480 Mbit/s of 10 Gbit/s` |
| now only | `{now}` big + unit small | `5 Gbit/s`, `64 %` |
| max only | `max` small + `{max}` big + unit small | `max 100 W` |
| icon set (BT battery) | leading icon at size × 0.7 | `🔋 64 %` |
| slow (no icon) | number in warn color + trailing `arrow.down.circle.fill` at size × 0.55, warn color | `480 ⬇ …` |
| slow with icon | icon and number in warn color | low BT battery |

Big number: `theme.number(size)`, primary (`onRow()`), warn color if slow. Small text: `theme.label(max(size × 0.5, 9))`, secondary. HStack baseline aligned, spacing 3, 1 line. Warn color = `theme.warn` (Blocks theme: white).

### 7.9 Node types and how each row is built (`PortTree`)

Icons column: SF Symbol → suggested Segoe Fluent Icons (MDL2) glyph.

| Node kind | Icon | Title | Facts (max 4) | Right value | Warning | Info ⓘ |
|---|---|---|---|---|---|---|
| **port** (USB-C/Thunderbolt) | `cable.connector` → `E957`/`ECF0` | port title: `{kind} port {n}` e.g. `USB-C port 1` (kind from IORegistry `PortTypeDescription`) | link kind: `Thunderbolt` (CIO) / `USB 3` / `USB 2` / `video` (DisplayPort only); and `{W} W in` when this port delivers the live charge | if data known: `LinkLoad(now: dataBps, max: portMax)` → `10 / 40 Gbit/s`, slow flag; else watts in (power) | first warn note of topics port/data, else if slow: **"Runs slower than the port, cable and device allow. Try another cable."** | joined info notes (port/data) |
| **port** (MagSafe) | `bolt.horizontal.fill` → `E945` | `MagSafe 3` (kind string as is) | none or link kind | watts in (`61.4 W`) | as above | as above |
| **cable** (child of a non-MagSafe port) | `cable.connector.horizontal` | `Cable` | `passive`/`active`/`optical`/`no e-marker`, `{cableW ?? 60} W`, length `~{l} m` (1…7) or `over 7 m` (8); the watts fact is dropped when there is no data link | data link + e-marker speed known: `now: dataBps, max: cableBps` → `10 / 40 Gbit/s`; otherwise `max {cableW ?? 60} W` | first warn note of topic cable | info notes of topic cable |
| **source** (what is plugged in; child of cable, or of the MagSafe port directly) | `what.icon` (see below) | `what.name` | `{now} W to Mac` (if charger and live), `{V} × {A}` of the winning profile, or the device maker (single non-hub device without charger) | charger: `LinkLoad(now: maxW, power)` → e.g. `96 W`; else `dataBps` | warn of topics power/devices | info of topics power/devices |
| **device** (USB) | `deviceIcon(name)` | USB product name | maker, `USB 1`/`USB 2`/`USB 3` (<100 Mbit/s / <1 Gbit/s / else) | `bps` (now only), slow if USB 3-capable (`bcdUSB ≥ 0x300`) but running ≤ 480 Mbit/s | **"Runs at USB 2 speed, but it can do USB 3. Try another cable or port."** | — |
| **hub** (only with internals on) | `point.3.filled.connected.trianglepath.dotted` | hub name | maker, `{n} hubs` when chained hubs collapsed | `bps`; **dimmed** | — | — |
| **Billboard** device (internals on) | `info.circle` | name | `mode info from the dock`; **dimmed** | bps | — | **"Billboard: the dock tells the Mac which extra modes (such as video over USB-C) are not active."** |
| **tb** (Thunderbolt device on this socket) | `bolt.horizontal.fill` | `Device Model Name` or `Thunderbolt device` | vendor, `Thunderbolt` | — | — | — |
| **display** | `display` → `E7F4` | display name (`NSScreen.localizedName`, default `Display`) | `{w} × {h}`, `{inches}″`, `looks like {lw} × {lh}` (only if logical ≠ pixels) | refresh `{hz}` Hz; if max > now + 1: `60 / 120 Hz`-style with slow flag | display warning (7.12) | DisplayLink info (on the DisplayLink device row) |
| **Displays** group (port kind) | `display.2` | `Displays` | `not linked to a port` | — | — | — |
| **Headphone jack** (port kind) | `headphones` → `E7F6` Headphone | `Headphone jack` | `3.5 mm`, `{kHz} kHz` | — | — | — |
| jack device | `headset` (mic) / `headphones` | `Headset` / `Headphones or speakers` | `sound plays here` / `not used for sound` | — | — | when not in use: **"Plugged in, but your Mac sends sound elsewhere. Choose it in Control Center › Sound."** |

`what` (source name/icon) priority:

| Condition | Name | Icon |
|---|---|---|
| a Thunderbolt device on this socket | its name | `bolt.horizontal.fill` |
| any USB root is a hub | `Dock` (if port has charger) else `Hub` | `dock.rectangle` |
| a USB root device | its name | `deviceIcon` |
| DisplayPort link | `Display` | `display` |
| charger only | `MagSafe charger` / `Charger` | `powerplug.fill` → `E83E`/`EBAB` |
| else | partner kind capitalized (`Usb device`, `Power sink`…) or `Device` | `cable.connector.horizontal` |

Children of the source:
- **Internals off** (default): all unique leaf devices behind the port (hubs flattened away, Billboard excluded), plus Thunderbolt devices. If the port has exactly **one** root and it is **not a hub**, the source row *is* that device and gets no children.
- **Internals on**: the real USB tree. Hubs with no devices under them are dropped; a hub whose only child is another hub collapses into one row with fact `{n} hubs`; Billboard rows shown dimmed.

`deviceIcon` by lowercased name substring (first match): keyboard→`keyboard` (`E765`), mouse→`computermouse` (`E962`), trackpad→`rectangle.and.hand.point.up.left`, ethernet/lan→`network` (`E839`), audio→`hifispeaker` (`E7F5`), headset→`headphones`, card/reader→`sdcard` (`E7F1`), ssd/disk/drive→`externaldrive` (`EDA2`/`E88E`), camera→`web.camera` (`E960`), display/hdmi→`display`, phone→`iphone` (`E8EA`); hub→`point.3.filled.connected.trianglepath.dotted`; billboard→`info.circle`; default `cable.connector.horizontal`.

#### 7.9.1 Speed limits used for the "slow" judgement

| Value | Rule |
|---|---|
| `portMax` | supports CIO → 40 Gbit/s (TB5 80 not detectable, documented ceiling); USB3 → 10; USB2 → 480 Mbit/s; else none |
| `cableBps` (e-marker speed code) | 0 → 480 Mbit/s; 1 → 5 G; 2 → 10 G (20 G when a Thunderbolt link is up); 3 → 40 G; 4 → 80 G |
| `sourceMax` | from each root's `bcdUSB`: < 0x200 → 12 Mbit/s, 0x2xx → 480 M, 0x300 → 5 G, ≥ 0x310 → unknown; any unknown → no statement |
| `dataBps` | TB link: lane gbps (speed code 0x8 → 10, 0x4 → 20, 0x2 → 40) × link width; else max USB root `UsbLinkSpeed`; else parsed port link rate string (`"10 Gbps"`) |
| slow (port) | `dataBps < 0.9 × min(portMax, cableBps, sourceMax)` |

#### 7.9.2 Displays placement

1. External displays only (built-in never appears in the tree).
2. **DisplayLink** displays (USB vendor 0x17E9): attached under the first DisplayLink USB device row (or the source row if, with internals off, the port's only root is the DisplayLink device). That row gets fact `1 display via DisplayLink` / `{n} displays via DisplayLink` inserted first, and the DisplayLink info text appended to its ⓘ. Two DisplayLink devices → all under the first (documented simplification).
3. **Native** (DisplayPort/HDMI straight from the GPU): attached under the source row of the **only** port with an active DisplayPort link. If video runs on 0 or ≥ 2 ports → not attached.
4. Remaining → a `Displays` group card at the end, fact `not linked to a port`.
5. Pref `dockedDisplays` (default **true**; not in `Prefs.defaults`, `@AppStorage` default) hides all displays (tree, header chip).

### 7.10 Detail panel (`NodeDetails`)

Shown in the right card (wide) or inline (narrow). `VStack(spacing: 8)`. Which card:

| Node | Detail view |
|---|---|
| display | `DisplayCard` |
| USB device/hub | `DeviceCard` |
| wireless node (has `pairs`) | title `Label` `theme.label(14)` semibold; `LoadView` size 22; warn `NoteBox`; pairs as `PortFact` rows (spacing 3); `All values` disclosure with raw list |
| port / cable / source / tb | title `Label`: cable → `Cable in {port title}`, else node title; `theme.label(13)` semibold; then `PortDetails` with topics: cable → [cable]; port → [port]; others → [power, data] |

#### 7.10.1 Building blocks

| Component | Spec |
|---|---|
| `PortGroup` (section) | header `Label(TITLE.uppercased(), systemImage:)`, `theme.label(10)`, secondary; content spacing 4; full width |
| `PortFact` (key/value row) | `HStack(firstTextBaseline, spacing 8)`; key fixed width **84**, secondary; value primary, wraps, **text-selectable**; font `theme.label(11)` |
| Grid of sections | `LazyVGrid` adaptive columns min **220**, column spacing 14, row spacing 12, top-aligned (1 column narrow, 2+ wide) |
| `NoteBox` | `HStack(spacing 6)`: icon (good `checkmark.seal.fill` → `E930`, warn `exclamationmark.triangle.fill` → `E7BA`, info `info.circle.fill` → `E946`) in level color + text primary (wraps); `theme.label(11)`; padding h8 v5; background level color at **14 %**, radius **7**. Level colors: warn → `theme.warn`, info → `theme.accent`, good → `theme.good` (Blocks: all white) |
| `Disclosure` | button row: `chevron.right` (9 pt bold) rotating 0°→90° when open, title, optional count in `theme.number(10)` secondary; `theme.label(11)` secondary; toggle `.snappy(duration: 0.25)`; content transition `.opacity`; spacing 8; starts **closed** each time |
| `RawList` | lines `"{key}  {value}"` (two spaces), key secondary, value primary, monospaced system font size **10 × scale**, spacing 1, wraps, selectable |
| `BigNum` | see 7.3 (size 22 in detail cards) |

#### 7.10.2 `PortDetails` (port, cable, charger, link)

Order: notes (each as `NoteBox`, spacing 4) filtered to the topics → grid of sections → "Devices behind this port" → "All IOKit values".

**Cable section** (`CABLE`, icon `cable.connector.horizontal`; when topics include cable, port is not MagSafe and connected):

| Key | Value |
|---|---|
| Type | `Optical (active)` / `Active (has electronics)` / `Passive (no signal electronics)` |
| Data | e-marker speed: `USB 2.0 only (480 Mbit/s)`, `USB 3.2 Gen 1 (5 Gbit/s)`, `USB 3.2 Gen 2 (10 Gbit/s; 20 Gbit/s for Thunderbolt)`, `USB4 / Thunderbolt (40 Gbit/s)`, `USB4 Gen 4 (80 Gbit/s)` |
| Power | `{A}` + ` at up to {V}` + ` ≈ {W} W` + ` (EPR)` |
| Length | `about {l} m (from signal delay)` / `longer than about 7 m (from signal delay)` (latency class 1…8) |
| E-marker ID | `{VID:04X}:{PID:04X}` + ` · USB PD 3.x`/`USB PD 2.0` |
| (no e-marker) | text: **"No e-marker found. A plain USB-C cable carries up to 3 A (60 W); its data speed can't be read."** (11 pt secondary) + `Type: Active (has electronics)` if the port flags an active cable |

E-marker decoding (cable VDO, index 3): latency bits 16..13; current bits 6..5 (1 → 3000 mA, 2 → 5000 mA); speed bits 2..0 (0…4); PD ≥ 3: max voltage bits 10..9 (20/30/40/50 V) and EPR bit 17; PD 2 → 20 V. Active cable = product type 4 (ID header bits 29..27). Watts = mA × mV / 1e6.

**Charger section** (`CHARGER` / `MAGSAFE CHARGER`, icon `bolt.fill`):

| Key | Value |
|---|---|
| Agreed | `{V} × {A} = {W} W` (winning PDO) |
| Maximum | `{maxW} W` (max over all offered PDOs) |
| Reported as | adapter name from `AdapterDetails` (live port only) |
| Now | `{W} W now ({V} × {A}) · battery charging` / `· battery full` / `· battery not charging` (the leading "Drawing " is stripped) |
| Per power source with > 1 option or a winner | sub-header `Offered profiles` (USB-PD) or the source name, 10 pt secondary; one line per PDO: `checkmark.circle.fill` (good color) or `circle` (secondary), 9 pt; text `{V}  ·  {A}  ·  {W} W` in `theme.number(11)`; `in use` (9 pt secondary) after the winner. PDOs sorted by voltage ascending |
| Single option source | key `Type-C current` (source `TypeC`) or source name; value `{V} · {A} ({W} W)` |

**Link section** (`MEASURED BY THE MAC`, icon `gauge.with.needle`):

| Key | Value |
|---|---|
| `USB 2` / `USB 3` / `Thunderbolt/USB4` / `DisplayPort` (per active link) | link text: `USB 2.0 at 480 Mbit/s (High Speed)`, `{generation} at 10 Gbit/s`, `Thunderbolt / USB4 at 40 Gbit/s (2 lanes)`, `DisplayPort (video)`; ` (tunneled)` suffix; `Gbps`→`Gbit/s`, `Mbps`→`Mbit/s` |
| `  Device` | product · maker |
| `  Mac is` | data role, lowercased (`host`, `device`) |
| Data | `No data link (power only or not set up)` when no links |
| Device type | partner kind from SOP ID header (`USB hub`, `USB device`, `power sink`, `adapter`, `host`, `charger`, combined with ` + `) + ` (can switch modes)` |
| Device ID | `{VID:04X}:{PID:04X}` |
| Talks | `USB PD 3.x` / `USB PD 2.0` |
| Accessory | security/TRM profile description |

**Connection section** (`CONNECTION`, icon `info.circle`; port topic):

| Key | Value |
|---|---|
| SuperSpeed | `Yes`/`No` |
| Cable | `Optical`/`Active`/`Passive` (non-MagSafe) |
| Plug side | `A (normal)` / `B (flipped)` |
| Plug events | count |
| Can do | supported transports, e.g. `USB 2, USB 3, Thunderbolt/USB4, DisplayPort` |
| Active | active transports or `–` |
| Liquid | `Detected` |

**Devices behind this port** (`DEVICES BEHIND THIS PORT`, icon `point.3.connected.trianglepath.dotted`; devices topic): `DeviceTree`:
- USB rows: indent `min(depth, 6) × 14`; hub with children has a chevron button (8 pt bold, 12×14 frame, rotated 90° when expanded, tooltip `Show devices on this hub` / `Hide devices on this hub`, `.snappy(0.2)`); others a 12 pt spacer. Icon 11 pt, 16 wide, secondary. Name `theme.label(12)` 1 line tail-truncated; speed right in `theme.number(11)`. Detail line 10 pt secondary: maker · speed name (`Low Speed`, `Full Speed`, `High Speed (USB 2)`, `SuperSpeed (USB 3.2 Gen 1)`, `SuperSpeed+ (USB 3.2 Gen 2)`, `SuperSpeed+ (USB 3.2 Gen 2×2)`) · `1 device on it`/`{n} devices on it` · `via 1 hub`/`via {n} hubs` · `{mA} mA power budget` · `billboard (mode info)` · `ID {VID:04X}:{PID:04X}`. Click row (tooltip `Click for all values`) → raw list, leading 34, opacity transition.
- Thunderbolt rows: icon `bolt.horizontal.fill`, name 12 pt, then vendor · `Thunderbolt` · `1 device in between`/`{n} devices in between` (10 pt); click → raw list (leading 22).

**All IOKit values** disclosure (port topic): raw groups, each with semibold 10 pt title (`Port`, `Cable e-marker (SOP')`, `Device identity (SOP)`, `Power source: {name}`, `Link: {type}`) then its raw lines (VDOs as `VDO n  0x%08X`).

#### 7.10.3 `DeviceCard` (USB)

Title `Label(name, icon)` 14 pt semibold; `BigNum` speed size 22; line 11 pt secondary: maker · speed name · `{mA} mA` · `ID {VID:04X}:{PID:04X}`; `All values` disclosure.

#### 7.10.4 `DisplayCard` (no serial numbers)

Title 14 pt semibold with `display` icon; `BigNum` `{hz}` `Hz` size 22; warning NoteBox if any; DisplayLink info NoteBox (accent) for DisplayLink displays. Grid:

| Section `PICTURE` (icon display) | Value |
|---|---|
| Resolution | `{w} × {h}` + ` (native)` or ` (native {W} × {H})` |
| Looks like | logical size, if different |
| Refresh | `{hz} Hz` + ` (can do {max} Hz)` |
| Scale | `2× (Retina)` / `1× (not Retina)` |
| Size | `{inches}″ ({mmW} × {mmH} mm)` (diagonal from mm / 25.4) |
| HDR | `Up to {edr}× SDR brightness (EDR)` or `No` |
| Color | `{bits} bits per channel` |

| Section `CONNECTION` (icon cable) | Value |
|---|---|
| Type | `Built in` / `DisplayLink (compressed video over USB)` / `DisplayPort (straight from the Mac's graphics)` / `HDMI (straight from …)` / `DisplayPort or HDMI (straight from the Mac's graphics)` |
| Port | port title (if attached) |
| Link | DisplayPort link text (native only) |
| Role | `main display (menu bar)` / `extended desktop`, `, mirrored` |
| Rotation | `{deg}°` (if ≠ 0) |
| IDs | `vendor {%04X} · model {%04X}` |

### 7.11 PortNote warnings (every rule)

Notes have level (good/info/warn) and topic (port/power/cable/data/devices). Topic decides which row shows it (port row: port+data; cable row: cable; source row: power+devices). Row shows the **first warn** as its warning line; **info** notes go into the ⓘ tooltip; all are listed as NoteBoxes in details. `PortSum.status`: `Check {noun}` (noun: port/charger/cable/speed/devices) or `Good`.

| # | Condition | Level / topic | Exact text |
|---|---|---|---|
| 1 | `LDCM_LiquidDetected` | warn / port | `Liquid or dirt detected in this port. Unplug and let it dry.` |
| 2 | `TransportsUnauthorized` not empty | warn / port | `macOS is holding back {transports} until you allow this accessory.` |
| 3 | `Overcurrent Count` > 0 | warn / port | `Over-current protection cut power on this port {n}× (a device drew too much).` |
| — | (rules below only when connected) | | |
| 4a | winning PDO ≥ 3 A and ≥ 20 V, not MagSafe, cable ≤ 3 A or no e-marker; no e-marker | info / cable | `No e-marker in this cable, so charging stops at 60 W (3 A). Above 60 W needs a 5 A cable (and a charger that can).` |
| 4b | same, with a 3 A e-marker | info / cable | `This cable is rated 3 A, so charging stops at 60 W. Above 60 W needs a 5 A cable (and a charger that can).` |
| 5 | cable watts < agreed watts | warn / cable | `The cable is rated {cw} W but the charger agreed on {W} W. Use a cable rated for at least that.` |
| 6a | otherwise, charging, no e-marker | good / power | `Charging OK: charger agree on up to {W} W; the Mac takes what it needs.` (sic) |
| 6b | otherwise, with e-marker | good / power | `Charging OK: charger and cable agree on up to {W} W; the Mac takes what it needs.` |
| 7 | measured > 1.01 × e-marker max, no Thunderbolt | info / cable | `The cable's e-marker says up to {x}, but the link runs at {y}. One of the two is wrong (e-markers are often mislabelled).` |
| 8a | USB2 only, measured ≥ 480 M, cable speed code 0 | warn / cable | `This cable is USB 2 only, so data runs at 480 Mbit/s. Use a USB 3 or Thunderbolt cable if the device can go faster.` |
| 8b | USB2 only, otherwise | info / data | `Data runs at USB 2 speed (480 Mbit/s). If this device can go faster, try a USB 3 or Thunderbolt cable.` |
| 9 | Thunderbolt link with cable speed code ≤ 2 | warn / cable | `This cable holds Thunderbolt/USB4 back to {x} or less. A 40 Gbit/s cable is faster.` |
| 10 | CIO / USB3 link up | good / data | `Thunderbolt/USB4 link is up.` / `Fast data (SuperSpeed) link is up.` |
| 11 | a Billboard device present | info / devices | `Billboard device present: the dock or adapter reports that an extra mode (such as video over USB-C) is not active or not supported on this connection.` |

Extra row-level warnings: port slow (7.9 port row), USB device at USB 2 speed (device row), display warnings (below), BT `Battery low. Charge it soon.`

### 7.12 Displays: warnings and DisplayLink text

| Condition | Text |
|---|---|
| refresh below max (max > now + 1) | `Runs at {hz} Hz but can do {max} Hz. The cable, adapter or port may limit {mode}.` — mode: `4K60`, `5K60`, `6K60`, `1440p75`, `1080p144` for known sizes (3840/4096×2160 → 4K, 5120×2880 → 5K, 6016×3384 → 6K, 2560×1440 → 1440p, 1920×1080 → 1080p), else `{w} × {h} at {hz} Hz` |
| fewer pixels than native | `Runs at {w} × {h} pixels but the display has {W} × {H}, so text looks soft. Check System Settings › Displays, the cable or the port.` |
| DisplayLink info | `DisplayLink sends the picture compressed over USB, and the Mac's processor does the work. Fine for office work; not for games or video (motion can stutter, some protected video won't play).` |

Native max = mode flagged native (`0x02000000`), else largest unscaled mode; max Hz = highest refresh at that resolution.

### 7.13 Headphone jack (3.5 mm)

Read on every refresh (CoreAudio, microseconds). Built-in output device whose data source is `'hdpn'` = plugged; in use = it is the default output; built-in input with source `'emic'` = headset mic. Group card: port `Headphone jack` (facts `3.5 mm` · `{kHz} kHz`), child device. Details (pairs): `Headset with microphone`/`Headphones or speakers` → `connected`; `Sound output` → `this jack`/`elsewhere`; `Sample rate` → `{kHz} kHz`. Empty jack → listed in the empty line as `Headphone jack`. Windows: `IMMDeviceEnumerator` + jack info (`IKsJackDescription`).

### 7.14 Wireless block (`Draadloos.swift`)

**Uplink row** (internet through a phone/modem; omitted for normal Wi-Fi/Ethernet):

| Detection (first match) | Title | Via | Icon |
|---|---|---|---|
| interface name contains iPhone/iPad | `Internet via iPhone (USB)` / `Internet via iPad (USB)` | `USB` | `personalhotspot` |
| SC type Bluetooth / name contains bluetooth | `Internet via Bluetooth tethering` | `Bluetooth` | `personalhotspot` |
| cellular / WWAN / `pdp_ip*` / `wwan*` / name contains modem, lte, 4g, 5g, cellular, mobile broadband | `Internet via mobile modem` | name or `modem` | `antenna.radiowaves.left.and.right` |
| Wi-Fi with router 172.20.10.x | `Internet via iPhone hotspot` | `Wi-Fi` | `personalhotspot` |
| Wi-Fi marked expensive | `Internet via phone hotspot` | `Wi-Fi` | `personalhotspot` |
| any expensive | `Internet via a metered connection` | name/iface | `gauge.with.dots.needle.33percent` |

Row: right value = download rate with unit `{unit} ↓` (e.g. `1.5 MB/s ↓`; bits/bytes per Live pref `netBits`, via `ratePair`: <1e6 → KB/s|kbit/s, <1e9 → MB/s, else GB/s; <10 → 1 decimal); facts: via · `metered` · `Low Data Mode` · `↑ {up} {unit}`. Details pairs: Interface, Through, Metered (`Yes: macOS and apps may hold back big downloads and backups`/`No`), Low Data (`On`/`Off`), Seen from (how detected, e.g. `router at 172.20.10.1 (Personal Hotspot address)`), Now (`↓ … · ↑ … unit`). No SSID (would need location permission).

**Bluetooth group** (shown when BT is off, or ≥1 connected): title `Bluetooth`, icon `antenna.radiowaves.left.and.right` (Segoe `E702`), fact `{n} connected` / `off`; pairs State/Chip/Firmware/Bus. Children = connected devices sorted by name. Device row: icon/kind from minor type (Keyboard `keyboard`, Mouse `computermouse`, Trackpad, Earbuds `airpods` / Headphones `headphones`, Speaker `hifispeaker`, Controller `gamecontroller`, Phone `iphone`, Watch `applewatch`, Computer `laptopcomputer`, Pen `applepencil`, Tablet `ipad`, else `Device` with `dot.radiowaves.left.and.right`); right value = battery `%` with battery icon (`battery.{100|75|50|25|0}percent` at ≥88/63/38/13), slow (warn color) when ≤ 20 %; warning ≤ 10 %: `Battery low. Charge it soon.`; facts: kind · `L 81% · R 64% · case 40%` (only if > 1 battery) · `signal strong|fair|weak` (RSSI ≥ −60 / ≥ −75 / else). Details: Type, Battery/Case/Left/Right, Signal `{rssi} dBm (strong)`, Profiles (A2DP, HFP…), Firmware, IDs `vendor 004C (Apple) · product B000`, `All values` raw.

**Paired, not connected**: collapsed disclosure; rows dimmed, no right value, facts kind · `in range` (if RSSI known).

Refresh: `NWPathMonitor` for uplink changes; 2 s timer for byte rates and BT; BT data via `system_profiler SPBluetoothDataType -json` off the main thread, cached and re-read at most every **120 s** while Docked is open (5 min for the Live BAT card). Windows: `Windows.Devices.Bluetooth` / `BluetoothLEDevice` + battery GATT service; `NetworkInformation.GetInternetConnectionProfile()` (`IsWwanConnectionProfile`, `GetConnectionCost()` for metered).

### 7.15 "Other devices"

USB devices not claimed by any port, plus Thunderbolt devices whose socket is not one of the USB-C ports. Disclosure `Other devices` with count; inside: `Not linked to a specific port (built in, or behind a Thunderbolt dock).` (10 pt secondary) and a `DeviceTree`.

### 7.16 Refresh cadence (`PortWatcher`)

| Trigger | Action |
|---|---|
| Tab appears | read everything once (background queue, utility QoS) + read displays (main thread) |
| IOKit add/remove notifications for `IOPortTransportState`, `IOPortTransportComponent`, `IOPortFeaturePowerSource`, `IOUSBHostDevice`, `IOThunderboltSwitch` | debounced: one read **0.6 s** after the first event of a burst |
| Timer | every **5 s** (charging watts change) |
| Screen configuration changed | re-read displays only |
| Tab disappears | timer, notifications and observers removed; late results ignored |

UI updates only when the snapshot actually changed (equality check) — no redraw otherwise. Low own-CPU principle: nothing runs when the page is hidden.

### 7.17 Settings (Settings › Docked) and prefs

| Section | Option (title / subtitle) | Pref | Default |
|---|---|---|---|
| Ports | **Show hubs & internals** — "Also the hubs inside a dock and its Billboard part. Same as the button on the page." | `dockedInternals` | false |
| Ports | **List empty ports** — "One line at the bottom, like "USB-C 2 · USB-C 3 · empty"." | `dockedEmpty` | true |
| Ports | **Show displays** — "External monitors under the port or DisplayLink dock that drives them, with resolution and refresh rate." | `dockedDisplays` | true (AppStorage default) |
| Wireless | **Show wireless** — "Bluetooth devices with battery, and a row when your internet runs through a phone hotspot or mobile modem." | `dockedWireless` | true (AppStorage default) |
| Speed | **Link speed** segmented `Gbit/s` / `GB/s` — "Gbit/s shows 40 Gbit/s · GB/s shows 5 GB/s (handy to compare with a drive)" | `dockedBytes` | false (Gbit/s) |

### 7.18 Privacy (what is filtered)

- Display serial numbers are never read (`DisplayInfo` has no serial field; only vendor and model numbers).
- Bluetooth: device addresses are used only as an internal row id (`bt:{address}`), never displayed; raw values drop every key containing `address` or `serial` (case-insensitive), and the `device_` prefix is stripped from keys. Controller address is ignored.
- Thunderbolt raw values exclude `DROM` (which contains UID/serial).
- USB/port raw values ("All values" / "All IOKit values") show IORegistry properties minus a skip list (`IOGeneralInterest`, `IOPowerManagement`, `CFBundleIdentifier*`, `IOPersonalityPublisher`, `IOMatchCategory`, `IOProbeScore`, `IOProviderClass`, `IOUserClientClass`, `IOMatchedAtBoot`, `IOReportLegend*`, `IOCFPlugInTypes`, `Owner`, `IOFunctionParent*`). Data blobs are truncated to 48 bytes hex, any value to 600 chars. Note: USB raw lines can still include a `USB Serial Number` key from IORegistry — a Windows port should filter serial keys here too, per the global "never show serials/MACs" rule.
- Wi-Fi SSID is not read (location permission).

### 7.19 Data sources and Windows equivalents

| Data | macOS | Windows equivalent (one line) |
|---|---|---|
| Ports, connection state, transports, plug orientation/events, liquid, over-current | IORegistry `IOPort` plane (`AppleHPMInterfaceType*`: `PortTypeDescription`, `PortNumber`, `ConnectionActive`, `TransportsSupported/Active/Unauthorized`…) | UCSI via `Windows.Devices.Usb`/`UcmCx` WMI (`Win32_PnPEntity` + USB Type-C connector manager), `CM_Get_DevNode_Property` |
| Cable e-marker, partner identity | CC → SOP' / SOP components, `Metadata.VDOs` | UCSI `GET_CABLE_PROPERTY` / `GET_CONNECTOR_STATUS` (via UCM/UCSI IOCTL), often unavailable — hide gracefully |
| Charger PDOs, winner | `Power In` feature → `PowerSourceOptions`, `WinningPowerSourceOption` | UCSI `GET_PDOS`; or `Win32_Battery`/`BatteryStatus` WMI for totals |
| Live power in, charging state | `AppleSmartBattery` `PowerTelemetryData`, `AdapterDetails`, `IsCharging`, `FullyCharged`, `ExternalConnected` | `Windows.Devices.Power.Battery.AggregateBattery.GetReport()` (ChargeRateInMilliwatts), `GetSystemPowerStatus` |
| USB tree, speeds, power budget, bcdUSB | IORegistry `IOUSB` plane (`UsbLinkSpeed`, `bDeviceClass`, `UsbPowerSinkAllocation`, `bcdUSB`) | SetupAPI/CfgMgr32 device tree + `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2` (speed), `USB_NODE_CONNECTION_INFORMATION` |
| Thunderbolt devices and link width/speed | `IOThunderboltSwitch` (`Depth`, `Device Model Name`), `IOThunderboltPort` (`Socket ID`, `Current Link Width/Speed`) | USB4 router/CM device properties (`DEVPKEY_Device_*` under USB4 host router), Thunderbolt Control Center WMI where present |
| Displays | CoreGraphics (`CGGetOnlineDisplayList`, modes, `CGDisplayScreenSize`), `NSScreen` (name, scale, EDR, depth), `IOMobileFramebuffer` (`DisplayAttributes`, `Transport.Downstream`) | `QueryDisplayConfig`/`DisplayConfigGetDeviceInfo` (DisplayConfig API), EDID via `WmiMonitorID`/`DXGI_OUTPUT_DESC1` (HDR); DisplayLink = adapter driver name/USB VID 17E9 |
| Headphone jack | CoreAudio data source `hdpn` / `emic` | `IMMDevice` + `IKsJackDescription` |
| Bluetooth | `system_profiler SPBluetoothDataType -json` | `Windows.Devices.Bluetooth` + Battery Service GATT |
| Hotspot/modem | `NWPathMonitor` + SystemConfiguration + 172.20.10.x router | `NetworkInformation` connection profiles + cost |
| Change notifications | `IOServiceAddMatchingNotification` + 5 s timer | `CM_Register_Notification` / `DeviceWatcher` + 5 s timer |

### 7.20 Strings used on this page (English source text)

`Docked`, `into this Mac`, `on battery`, `battery full`, `charging`, ` · full in {d}`, `not charging`, `{d} left`, `charger`, `link`, `device`, `devices`, `display`, `displays`, `Show hubs & internals`, `Hide hubs & internals`, `No USB-C or MagSafe ports found on this Mac.`, `Click a row for its details.`, ` · empty`, `WIRELESS`, `Bluetooth · paired, not connected`, `Other devices`, `Not linked to a specific port (built in, or behind a Thunderbolt dock).`, `Headphone jack`, `Headset`, `Headphones or speakers`, `sound plays here`, `not used for sound`, `Headset with microphone`, `connected`, `Sound output`, `this jack`, `elsewhere`, `Sample rate`, `Displays`, `not linked to a port`, `1 display via DisplayLink`, `{n} displays via DisplayLink`, `{kind} port {n}`, `video`, `{W} W in`, `Cable`, `no e-marker`, `optical`, `active`, `passive`, `over 7 m`, `{W} W to Mac`, `Dock`, `Hub`, `Display`, `MagSafe charger`, `Charger`, `Device`, `mode info from the dock`, `{n} hubs`, `Cable in {port}`, `All values`, `All IOKit values`, `Devices behind this port`, `Picture`, `Resolution`, ` (native)`, `Looks like`, `Refresh`, `Scale`, `Size`, `HDR`, `Color`, `Connection`, `Type`, `Port`, `Link`, `Role`, `Rotation`, `IDs`, `Agreed`, `Maximum`, `Reported as`, `Now`, `Offered profiles`, `in use`, `Type-C current`, `Measured by the Mac`, `  Device`, `  Mac is`, `Data`, `Device type`, `Device ID`, `Talks`, `Accessory`, `SuperSpeed`, `Plug side`, `A (normal)`, `B (flipped)`, `Plug events`, `Can do`, `Active`, `Liquid`, `Detected`, `Show devices on this hub`, `Hide devices on this hub`, `Click for all values`, `Check {noun}`, `Good`, `max`, `of`, `metered`, `Low Data Mode`, `{n} connected`, `off`, `in range`, `Battery low. Charge it soon.` plus all note/warning texts quoted in 7.11–7.14.

---

## 8. Menu bar item and panel

Source: `Menubar.swift`, the scene wiring in `App.swift`, and `Prefs.applyDock`. Windows equivalent: one or more **notification-area (tray) icons**, each opening a **flyout** window anchored to the tray. The "Regular menu" style maps to a tray context menu.

### 8.1 Settings involved (defaults)

| Key | Default | Meaning |
|---|---|---|
| `menubarStyle` | `text` | how the item looks (§8.2) |
| `menubarMetrics` | `cpu` | comma list of metric ids shown in the item, in order (`cpu,gpu,ram,net,ssd,io,battery,fan`; `fan` only on Macs with a fan) |
| `menubarMetricsHidden` | "" | metrics switched off in the settings list (UI bookkeeping) |
| `menubarSplit` | false | "every metric gets its own item" |
| `menuStyle` | `panel` | `menu` = "Regular menu"; `panel` = "Panel with live values" |
| `menuParts` | `status,values,top,links` | which parts the regular menu and the classic panel show |
| `richPanel` | true | panel style: rich (Checkup-like) when true, classic compact list when false |
| `netBits` | false | network in bits ("5.1 Mbit/s", "640 kbit/s") instead of bytes |
| `absolute` | `ram,ssd` | metrics shown as real values instead of % (also used on Live) |

**Never hide both the Dock icon and the menu bar item.** If `dockIcon` is false and `menubarStyle` is `off`, `dockIcon` is forced back to true, otherwise the app can't be reached. On Windows: if the tray icon is off, always keep the taskbar button.

### 8.2 Menu bar styles (`MenubarStyle`)

| id | Settings label | Rendering |
|---|---|---|
| `text` | "Text (CPU 23%)" | Plain menu bar text with monospaced digits. Per metric: `"{ABBR} {value}"`, metrics separated by **two spaces**. Example: `CPU 23%  RAM 12.0 GB  NET ↓1.2 MB/s` |
| `compact` | "Compact (small symbols)" | Drawn into one template image 14 pt high, because macOS drops symbols placed inside menu bar text. Metrics are spaced **7 pt** apart. Per metric: the SF symbol at **11 pt medium**, 3 pt gap, then the value in **12 pt medium monospaced-digit** system font |
| `bars` | "Bars" | A template image, 14 pt high. Per metric a vertical bar **6 pt wide × 14 pt**: outline as a rounded rectangle (radius 1.5, 1 pt stroke), filled from the bottom to height `max(1, 14 × fraction)`. Bars are **2 pt** apart |
| `graph` | "Graphs" | A template image, 14 pt high. Per metric a polyline **22 pt wide**, stroke **1.2 pt**, over the last **30 samples** (1 minute at 2 s), y = fraction 0…1. Graphs are **3 pt** apart |
| `icon` | "Just an icon" | SF `gauge.with.needle` (Segoe `SpeedHigh` EC4A). When the status (§8.5) is not green: `exclamationmark.circle.fill` (Segoe `StatusErrorFull` EB90) |
| `off` | "Don't show" | no item at all |

- **Template images** take the menu bar's own color: black in a light menu bar, white in a dark one. They are re-rendered every sample (2 s) at the screen's backing scale.
- Windows tray icons are 16 × 16 px at 100% scale (20 or 24 at higher DPI) and cannot show text. So:
  - `text` / `compact`: render the number, or 2–3 digits, into the icon bitmap, and put the full text in the tooltip.
  - `bars` / `graph`: draw them into the icon bitmap.
  - Use white or black to match the taskbar theme (`SystemUsesLightTheme`).
- **Item metrics:** at most **4** metrics in one item (the first 4 enabled). Disabled or unavailable ids are skipped; `fan` is dropped on Macs without a fan.
- **Split (`menubarSplit`):** each metric gets its **own item**, up to **3** items, in order, so a 4th metric is not shown. Split does not apply to the `icon` style (one item only). Hidden items don't follow the meter, which saves work.

**Per-metric symbol** (`Metric.symbol`):

| Metric | SF symbol | Segoe suggestion |
|---|---|---|
| CPU | `cpu` | `Processing` E9F5 |
| GPU | `display` | `TVMonitor` E7F4 |
| RAM | `memorychip` | a memory glyph |
| NET | `network` | `Globe` E774 |
| SSD | `internaldrive` | `HardDrive` EDA2 |
| I/O | `arrow.up.arrow.down` | `Sort` E8CB |
| BAT | `battery.75percent` | `Battery7` E857 |
| FAN | `fan` | custom glyph |

**Value text** (`menuValue`):

- **NET:** `"↓" + short(down)`, with no space after the arrow (for example `↓1.2 MB/s`, or `↓5.1 Mbit/s` with bits on). Only download is shown.
- **I/O:** `short(read + write)`.
- **Other metrics:** the same value as the Live row. When the metric is in `absolute`, a unit is appended: CPU gets " cores" (for example `2.3 cores`), RAM and SSD get " GB". In % mode the value is `"23%"` and nothing is appended.

**Fractions** for bars and graphs (clamped to 0…1):

| Metric | Fraction |
|---|---|
| CPU | cpu ÷ 100 |
| GPU | gpu ÷ 100 |
| RAM | ram % ÷ 100 |
| NET | (down + up) ÷ 100 MB/s |
| SSD | 1 − free ÷ total |
| I/O | (read + write) ÷ 100 MB/s |
| BAT | battery ÷ 100 |
| FAN | rpm ÷ the largest fan maximum |

### 8.3 What opens on click (`menuStyle`)

| Setting | Label | Opens |
|---|---|---|
| `menu` | "Regular menu" | a standard drop-down menu (`MenuContent`, §8.4) |
| `panel` (default) | "Panel with live values" | a popover window: `RichPanel` (§8.6) if `richPanel` is true (default), otherwise `ClassicPanel` (§8.7) |

### 8.4 Regular menu (`MenuContent`)

The items appear in this order, each only if its part is enabled in `menuParts`:

1. **status** → a disabled text item with the status sentence (§8.5).
2. **values** → one disabled item per **Live** metric (setting `metrics`, in Live order): `"{label}   {value} {unit}  {side}"`. There are three spaces after the label; the unit is omitted if empty; `side` is the temperature, ping or battery time, for example `CPU   23%  52°`.
3. **top** (only if the minute monitor has apps) → a divider, then **"Heaviest (last minute)"**, then the 3 heaviest groups by CPU: `"{name}   {pct}"`.
4. A divider.
5. **MenuButtons:**
   - If **links** is enabled:
     - "Why is my Mac slow?": opens the main window on **Checkup**.
     - "Open Fusebox": opens or activates the main window.
     - "Setup…": opens the first-run setup window.
   - Always:
     - "Settings…" (**⌘,**): opens Settings.
     - "Quit Fusebox" (**⌘Q**).

On Windows, the tray right-click context menu has the same order. "Settings…" and "Quit Fusebox" are always present.

### 8.5 Status sentence (`Meter.status`)

The first match wins. CPU is the average over the last 30 s; the thresholds come from Settings.

| Condition | Text | Color |
|---|---|---|
| memory pressure ≥ 4 (critical) | "Memory is full — close a few apps" | red |
| temp ≥ warnTemp + 10 (default 105 °C) | "Your Mac is very hot — it's slowing itself down" | red |
| average CPU ≥ warnCPU (default 85%) | "Your Mac is busy" | orange |
| pressure = 2 (high) | "Memory is filling up" | orange |
| temp ≥ warnTemp (default 95 °C) | "Your Mac is getting warm" | orange |
| otherwise | "All calm" | green |

### 8.6 Rich panel (`RichPanel`, default)

**Theming:** the panel follows the app theme. It uses the theme from `theme`, adapted to light or dark (`appearanceMode`). In Low impact mode it uses the theme's solid variant. Its background is `theme.bg`; for Liquid Glass, whose bg is nil, the system popover material shows through.

**Size and layout:** fixed width **336 pt**, padding **12**, `VStack(leading, spacing 10)`.

**Loading:** the score and tip come from `Analysis.gist`, which loads when the panel opens. It uses today's Checkup report if that is at most 10 minutes old; otherwise it reads the logs once in the background. The panel exists only while it is open.

```
┌────────────────────────────────────────────┐ 336 pt
│ ┌────────────────────────────────────────┐ │
│ │ 8/10   Health                       ⏻ │ │ row 0 (themed)
│ │        Your Mac had a calm day.        │ │
│ └────────────────────────────────────────┘ │
│ ┌────────────────────────────────────────┐ │
│ │ ▣ CPU  ~~~~~~~~~~~~~~~~~~~~~   23%     │ │ row 1 (themed)
│ │ ▭ GPU  ~~~~~~~~~~~~~~~~~~~~~    4%     │ │ one line per CPU/GPU/RAM/NET/BAT
│ │ ▦ RAM  ~~~~~~~~~~~~~~~~~~~~~ 12.0 GB   │ │
│ │ ◎ NET  ~~~~~~~~~~~~~~~~~~~~~ ↓1.2 MB/s │ │
│ └────────────────────────────────────────┘ │
│ ┌────────────────────────────────────────┐ │
│ │ 💡 Restart your Mac                     │ │ row 2 (themed), click → Checkup
│ │    It's been on for 16 days …          │ │
│ └────────────────────────────────────────┘ │
│ [▭ Fusebox][▥ Checkup][▦ Processes][⚕ Network][⚙ Settings] │ quick tiles
└────────────────────────────────────────────┘
```

**Row 1: score** (`themedRow index 0`), an `HStack(center, spacing 10)`:

- **Score:** `theme.number(32)`, followed directly by "/10" in `number(12)`, `onRow(true)`. The two share a first baseline with 1 pt between them. The score shows "–" while loading.
- **Score color:**

  | Score | Color |
  |---|---|
  | ≥ 8 | `good` |
  | ≥ 5 | `warn` |
  | lower | `danger` |
  | unknown | `secondary` |
  | any score, `blocks` style | white |

- **Text column** (spacing 2):
  - **"Health"** in `label(13).semibold`, `onRow()`.
  - The reason sentence from the Checkup report (`label(11)`, `onRow(true)`, up to 2 lines). It shows **"Checking…"** while loading.
- **Quit button:** SF `power`, 11 pt semibold (Segoe `PowerButton` E7E8), plain, `onRow(true)`, tooltip "Quit Fusebox". It sits after a spacer, on the right.

**Row 2: live metrics** (`themedRow index 1`), a `VStack(spacing 5)`:

- **Which metrics:** only CPU, GPU, RAM, NET and BAT, and only those that are enabled on **Live**, in Live order.
- **Each line** is an `HStack(spacing 6)`:
  - The symbol at `10 × textScale` pt, semibold, in `theme.tone(0)` (accent; white in `blocks`), in a 16 pt wide frame.
  - The abbreviation (CPU, GPU, RAM, NET, BAT) in `label(11)`, `onRow(true)`, 34 pt wide, leading.
  - A sparkline 14 pt high that takes the remaining width. It plots the fraction over the last 30 samples. CPU, GPU, RAM and BAT use a fixed 0…1 scale; NET auto-scales (otherwise it would be flat). It doesn't glide, and its color is the accent.
  - The value in `number(12)`, 78 pt wide, trailing, one line. NET uses the menu value (`↓1.2 MB/s`); every other metric shows "value unit". Value color by level: level 2 → `tone(2)` (danger), level 1 → `tone(1)` (warn), otherwise `onRow()`.

**Row 3: top tip** (`themedRow index 2`), only if the gist has a tip. The tip shown is the first *serious* tip; if there is none, the first tip that isn't the fixed "login items" tip.

- **Layout:** an `HStack(top, spacing 8)`.
- **Icon:** `exclamationmark.triangle.fill` for a serious tip, in `tone(1)`; otherwise `lightbulb.fill` (Segoe `Lightbulb` EA80), in `tone(0)`. The icon is `12 × scale` pt in a 16 pt frame.
- **Text:** the tip headline in `label(12).semibold`, `onRow()`, wrapping. Below it, the rest in `label(11)`, `onRow(true)`, up to 2 lines.
- **Click:** the whole row is the hit area and opens the main window on **Checkup**.

**Row 4: quick tiles**, an `HStack(spacing 4)` of five equal-width tiles. These are not themed rows.

- **Tile look:**
  - A `VStack(spacing 3)`.
  - The icon at `13 × scale` pt, medium, in `tone(0)`, in a frame `17 × scale` high.
  - The title in `label(10)`, `onRow()`, one line.
  - 7 pt vertical padding, full width.
  - Background `onRow()` at **7%** opacity, radius **9**.
  - No hover state.

| Tile title | SF → Segoe | Tooltip | Action |
|---|---|---|---|
| "Fusebox" | `macwindow` → `TaskView` E7C4 / app window | "Open Fusebox" | opens or activates the main window (current tab) |
| "Checkup" | `chart.bar.xaxis` (the Checkup tab icon) | "Checkup" | main window on Checkup |
| "Processes" | `square.grid.2x2` → `AllApps` E71D | "Processes" | main window on Processes |
| "Network" | `stethoscope` → `Diagnostic` E9D9 | "Find the problem" | sets `netFindNow` = true and opens Network, which starts the "find the problem" diagnosis right away |
| "Settings" | `gearshape` → `Settings` E713 | (none) | opens Settings and activates the app |

Note that the rich panel **ignores `menuParts`**: it always shows the score, the metrics, the tip (when there is one) and the tiles.

### 8.7 Classic panel (`ClassicPanel`, `richPanel` = false)

- System styling, not themed. Width **260 pt**, padding **12**, `VStack(leading, spacing 8)`. It follows `menuParts`.
- **status:** an `HStack(spacing 6)` with an **8 × 8 circle** in the status color (green, orange or red), then the status sentence, in the `callout` font at medium weight.
- **values:** a grid with 4 pt vertical spacing, one row per Live metric:
  - the label in secondary color;
  - the value with monospaced digits, colored red at level 2, orange at level 1, primary otherwise;
  - the unit and side text, joined by a space, in secondary color.
- **top:** a divider, then 3 rows with the name (one line) on the left and the CPU % (monospaced) on the right. There is no heading.
- A divider, then the **MenuButtons** (§8.4) as borderless buttons stacked vertically.

### 8.8 Windows mapping summary

| macOS | Windows |
|---|---|
| `MenuBarExtra` item(s), up to 3 when split | `Shell_NotifyIcon` tray icons, one per item; their order is controlled by the user |
| Template-image rendering every 2 s | redraw the HICON every sample, but only when the image actually changed; keep it under 1 ms |
| `.menu` style | tray context menu (`MenuFlyout` on right-click and on left-click) |
| `.window` style (panel) | a borderless flyout window (Mica or Acrylic for Liquid Glass, else the theme bg) placed above the tray icon. It closes on deactivate |
| Status-colored icon (`icon` style) | swap in a warning-badge icon when the status isn't green |
| Dock icon toggle | taskbar button toggle (`ShowInTaskbar`); the same "never hide both" rule applies |

---

## 9. Settings window

Source: `Instellingen.swift` (shell + general pages), `InstellingenPaginas.swift` (Processes / Checkup / Network / Docked pages), `Fans.swift` (`FanSettings`, `HelperSection`, `CurveEditor`), `Laden.swift` (`ChargeSettings`), `Taal.swift` (`LanguagePicker`), `Sneltoets.swift` (`HotkeyRecorder`).

### 9.1 Window shell

| Property | Value |
|---|---|
| Opened by | the gear button in the main window tab bar (`SettingsLink`, tooltip `Settings  ⌘,`, or `Settings  ⌘,  ·  Low impact is on` when Low impact is active), ⌘, , the menu-bar panel, a "What's new" notification (opens on `about`) |
| Size | **fixed 740 × 560 pt** (`.frame(width: 740, height: 560)`), not resizable |
| Layout | `NavigationSplitView`: sidebar list left, detail right. Sidebar column width min 200 / ideal 200 / max 220. Sidebar toggle button removed from the toolbar. |
| Detail | `Form` with `.formStyle(.grouped)` = the standard macOS System Settings look: rounded grouped "inset" sections (≈10 pt radius, subtle fill one step lighter/darker than the window), section header text above each group in secondary small-caps-ish 11–13 pt semibold, footer text below in `.caption` secondary. Rows are separated by hairline dividers inside a group. Title of the detail column (`navigationTitle`) = the tab title. |
| Colors | Settings does **not** use the Fusebox theme colors for its chrome; it is a plain system window. It does follow the Fusebox Light/Dark choice: `.preferredColorScheme(mode.scheme)` (System = follow OS). Embedded previews (LivePreview, OptionPreview, TablePreview, ThemePreview, accent capsule, number demo) DO render in the current theme. |
| Accent | system accent color (`Color.accentColor`) for toggles, chips, selected cards |
| Remembered tab | pref `settingsTab` (default `profile`), local to this Mac (not exported) |
| Placement | every time it opens: `SettingsPlacement` (below) |

Windows port: a separate non-resizable 740×560 window with a WinUI `NavigationView` (PaneDisplayMode=Left, OpenPaneLength≈200, no hamburger toggle) and in the content a `ScrollViewer` of `SettingsCard`/`SettingsExpander` groups (CommunityToolkit.WinUI.Controls.SettingsControls) — that is the closest equivalent of macOS grouped forms.

#### SettingsPlacement (exact algorithm)

Runs each time the window appears. Find the visible main window; screen = its screen's visible frame (work area). Gap = **12 pt**.

1. If `main.right + 12 + settings.width <= screen.right` → x = `main.right + 12` (to the right).
2. Else if `main.left − 12 − settings.width >= screen.left` → x = `main.left − 12 − settings.width` (to the left).
3. Else centered over the main window: x = clamp(`main.midX − settings.width/2`, screen.left, screen.right − settings.width).
4. Tops aligned: top = min(main.top, screen.top); bottom y = max(top − settings.height, screen.bottom).

Self-test values: screen 1710×1070, settings 740×600, main at x 35 w 316 → settings x 363 (right of it). Main at x 1300 w 400 → x 548 (left). Main full screen → centered, x 485.

### 9.2 Sidebar tabs (order = on-screen order)

Pref id (stored in `settingsTab`), title, SF Symbol, suggested Segoe Fluent Icons glyph:

| # | id | Title | SF Symbol | Segoe Fluent Icons | Shown when |
|---|---|---|---|---|---|
| 1 | `profile` | Profile | `person.crop.circle` | Contact `E77B` | always |
| 2 | `look` | Appearance | `paintbrush` | Brush `E771` | always |
| 3 | `live` | Live page | `gauge.with.needle` | SpeedHigh `EC4A` | always |
| 4 | `processes` | Processes | `square.grid.2x2` (same as window tab) | AllApps `E71D` | always |
| 5 | `checkup` | Checkup | `chart.bar.xaxis` | Diagnostic `E9D9` (or BarChart-like glyph) | always |
| 6 | `network` | Network | `network` | Network `E968` | always |
| 7 | `docked` | Docked | `dock.rectangle` | Devices `E772` | always |
| 8 | `menubar` | Menu bar | `menubar.rectangle` | (tray) e.g. "Taskbar"/`E7C4`-style glyph; call it "Tray icon" on Windows | always |
| 9 | `alerts` | Notifications | `bell` | Ringer `EA8F` / Bell | always |
| 10 | `fans` | Fans | `fan` | no fan glyph in Segoe; use a custom fan path icon | **only if the Mac has fans** (`Fans.count > 0`) |
| 11 | `battery` | Battery | `battery.75percent` | Battery7 `EBA7` | **only if the Mac has a battery** |
| 12 | `data` | Privacy | `lock.shield` | Shield `EA18` | always |
| 13 | `about` | About | `info.circle` | Info `E946` | always |

Note: tab titles for 4–7 are the page names, which are **never translated** (Processes, Checkup, Network, Docked are product names; "Network"/"Processes" are translated in other languages only via the page title string — follow the localized `Page.title`).

Each sidebar row is a standard `Label(title, systemImage:)`: 13 pt regular text, icon in accent tint, selected row = system selection capsule (accent fill, white text).

ASCII sketch:

```
┌─ Settings ──────────────────────────────────────────────────────────────┐
│ ●●●                                                                      │
│ ┌──────────────────┐  Appearance                                         │
│ │ ◉ Profile        │  ┌───────────────────────────────────────────────┐ │
│ │ 🖌 Appearance  ▌ │  │ STYLE                                          │ │
│ │ ◔ Live page      │  │ Mode        [ System | Light | Dark ]          │ │
│ │ ▦ Processes      │  │ ┌────────┐ ┌────────┐ ┌────────┐               │ │
│ │ ▥ Checkup        │  │ │preview │ │preview │ │preview │  (3×2 tiles)  │ │
│ │ ⊕ Network        │  │ │Instrum.│ │Terminal│ │Minimal │               │ │
│ │ ▭ Docked         │  │ └────────┘ └────────┘ └────────┘               │ │
│ │ ▭ Menu bar       │  └───────────────────────────────────────────────┘ │
│ │ 🔔 Notifications │  TEXT SIZE                                          │
│ │ ✣ Fans*          │  ┌──────┐ ┌──────┐ ┌──────┐                         │
│ │ ▮ Battery*       │  │  Aa  │ │  Aa  │ │  Aa  │                         │
│ │ 🛡 Privacy       │  …                                                  │
│ │ ⓘ About          │                                                     │
│ └──────────────────┘                                                     │
└──────────────────────────────────────────────────────────────────────────┘
   200–220 pt sidebar         grouped form, scrolls vertically     740×560
```

### 9.3 Shared building blocks (appearance)

#### Card (`Card` in Stappenplan.swift) — big selectable tile
- Plain button; content `frame(maxWidth: .infinity)`, **padding 10**.
- Background: `.quaternary` fill at **opacity 0.35** (very faint gray, ≈ 5–8 % gray on the form background), **corner radius 12**.
- Border: `strokeBorder`, radius 12. Selected: **system accent, 2.5 pt**. Not selected: **secondary color at 0.25 opacity, 1 pt**.
- No hover effect, no animation on selection (instant border swap). Accessibility trait "selected".
- Grid: `LazyVGrid` with `GridItem(.flexible(), spacing: 10)` columns and **10 pt** row spacing (`grid(n)` helper).

#### option(title, detail, symbol) card
Card containing a VStack spacing **5**: SF Symbol at `.title2` (≈22 pt) in a 28 pt high frame; title `.headline` (13 pt semibold); detail `.caption` (10 pt) secondary, centered, max 2 lines with reserved space.

#### PresetCard (Profile page)
- Plain button. VStack spacing **4**: icon `.title2`; title `.headline`; blurb `.caption` secondary centered, 2 lines reserved; `Divider` with 2 pt vertical padding; "changes" line `.caption2` (≈10 pt) **tertiary** color, centered, 5 lines reserved.
- Full width, **padding 10**, background `.quaternary` at **0.5 opacity**, **corner radius 10**. No border, no selected state (presets are actions, not toggles).
- Tooltip: `Changes: <changes>`.
- The "changes" string is generated from the preset values, joined with ` · `: theme name; `large text` (scale >1) or `extra large text` (≥1.3); metric abbreviations joined with spaces (e.g. `CPU RAM SSD BAT`); `no graphs` if graphs = never; `advice line` if showAdvice; `real values` if `absolute` set; `Mbit/s` if netBits; menu-bar style: `menu bar icon` for icon, otherwise `menu bar <first word of style title lowercased>` (e.g. `menu bar compact`, `menu bar graphs`, `menu bar text`); `battery alert` if the battery alert is turned on.

#### Chips (on/off chip row, `Chips`)
- Flow layout (`NetFlow`, wraps onto next lines), spacing **6**.
- Each chip: `Label(title, systemImage: on ? "checkmark" : "plus")`, `.callout` (12 pt), padding **10 horizontal / 4 vertical**, **capsule** shape.
- On: text+icon in accent color, background accent at **0.15** opacity. Off: text secondary, background secondary at **0.10**.
- Click toggles. No animation. Segoe: CheckMark `E73E` / Add `E710`.

#### OptionPreview (a single fake Live row under a toggle)
- HStack first-text-baseline, spacing **6**: label (theme `label(13)`, color = accent for the Instrument theme, otherwise secondary-on-row); optional SF Symbol 13 pt medium (secondary); optional "side" text (theme `number(13)`, secondary); optional extra view (e.g. CoreBars, baseline aligned 1 pt above bottom); spacer; value in theme `number(22)` primary; optional unit `number(13)` secondary.
- Wrapped in `.themedRow(theme)` (the theme's own row style — line/plain/card/block/glass), then **padding 8**, background = theme `bg` (or thin material for Liquid Glass), **corner radius 10**.
- Animates with `.snappy` (SwiftUI snappy spring ≈ 0.3 s response, little bounce) when the `side` text changes (e.g. toggling ping on adds "17 ms").
- Uses the current theme adapted to the Fusebox appearance mode.

#### TablePreview (a fake table header + one row)
- `Grid` trailing-aligned, horizontal spacing **10**, vertical spacing **4**.
- Header row: `Name` left-aligned (flexible), then column titles; font theme `label(10)`, color secondary; the sorted column (and Name if sorted by name) is primary text and gets ` ▾` appended.
- Value row: name in `label(12)`, values in `number(12)`, color on-row primary.
- `lineLimit(1)`, `minimumScaleFactor(0.75)` (shrink rather than truncate).
- `.themedRow(theme)`, padding 8, theme bg, radius 10. `.snappy` animation when the set of columns changes (columns slide in/out).

#### NotificationExample (fake macOS notification)
- HStack top-aligned, spacing **8**: app icon 20×20; VStack spacing 1: title `.caption` semibold, body `.caption` secondary (wraps).
- Padding **8**, background `.quaternary` at 0.5, **radius 10**. Accessibility label `Example notification: <title>. <text>`.
- On Windows: render a mini toast mock (app icon 20 px, title semibold 12 px, body 12 px secondary).

#### Small "caption" text
`.caption` (≈10 pt) in secondary color — used for every explanatory line under a control.

#### Two-line toggle labels
Many toggles use `Toggle { Text(title); Text(subtitle) }` → macOS renders the second Text as a smaller (≈11 pt) secondary subtitle beneath the title. On Windows: `SettingsCard Header` + `Description`.

### 9.4 Page: Profile (`ProfileSettings`)

| Order | Section header | Control | Text | Pref / action |
|---|---|---|---|---|
| 1 | `Start with a profile (you can still change everything afterwards)` | 4 × PresetCard in an HStack (spacing 8) | see presets table | `Prefs.applyPreset` |
| 1b | (same) | status line (`.callout` secondary), shown after an action | `Profile <Title> is on` / import result / `Everything is back to default` | — |
| 2 | (no header) | LowImpactPicker (segmented) | see below | `lowImpact` (default `auto`) |
| 3 | `Share with friends` | buttons in a row | `Share my settings…` · `Import…` | save/open JSON panel |
| 3b | | caption | `Your ping server is not included.` | |
| 4 | (no header) | button | `Run setup again` | opens setup window, activates app |
| 4b | | destructive button (red text) | `Reset everything…` | confirmation dialog |

Reset dialog: title `Reset all settings to default?`, message `Your logs are kept.`, destructive button `Reset`. Resets **every** default key except `setupDone`; then status `Everything is back to default`.

**LowImpactPicker** (segmented, 3 segments `Off` / `On` / `Automatic`, values `off`/`on`/`auto`):
- Label: leaf icon (`leaf`) — **green** when Low impact is currently active, secondary otherwise; title `Low impact` or `Low impact · on now`.
- Subtitle: `Fusebox measures every 5 seconds instead of 2, without animations or glass effects. With the window open that is about 0.3% of a processor core instead of 2%. Automatic: on in Low Power Mode, or on battery below 20%.`
- Live-updates when Low impact switches (notification `fusebox.lowImpact`).
- Automatic rule: on if OS Low Power Mode, or (not charging AND battery < 20 %). Windows: Energy Saver / Battery Saver on (`PowerManager.EnergySaverStatus == On`) or on battery < 20 %.

#### Presets (exact values from `Prefs.presets`)

Applying a preset first **resets every key any preset touches** (union of all preset keys) to default, then applies the preset's values. Logging, history and ping are never touched.

| id | Title | SF Symbol → Segoe | Blurb | Values |
|---|---|---|---|---|
| `simple` | Simple | `leaf` → (leaf glyph / custom) | `Big, calm, with advice` | theme `liquid`, textScale 1.15, metrics `cpu,ram,ssd,battery`, graphs `never`, showAdvice true, menubarStyle `icon` |
| `gamer` | Gamer | `gamecontroller` → Game `E7FC` | `GPU and ping up front` | theme `instrument`, metrics `cpu,gpu,ram,net`, graphs `auto`, shortMinutes 5, menubarStyle `compact`, menubarMetrics `cpu,gpu`, netBits true, warnPing 60 |
| `nerd` | Nerd | `terminal` → CommandPrompt `E756` | `Everything, in real values` | theme `terminal`, metrics `cpu,gpu,ram,net,ssd,io,battery`, absolute `cpu,ram,ssd`, menubarStyle `graph`, menubarMetrics `cpu,ram,net` |
| `laptop` | Laptop | `laptopcomputer` → Laptop/`E7F8` | `Battery first, alert when almost empty` | theme `minimal`, metrics `battery,cpu,ram,ssd`, menubarStyle `text`, menubarMetrics `battery,cpu`, `alert.battery.on` true |

Resulting "changes" lines, e.g. Simple: `Liquid Glass · large text · CPU RAM SSD BAT · no graphs · advice line · menu bar icon`.

#### Share / import (trust boundary — must be replicated)
- Export: save panel, default file name `Fusebox-settings.json`, pretty-printed sorted JSON of **all known keys** (`Prefs.defaults` keys only).
- Import: open panel (.json). File must be < 100 000 bytes and a JSON object. Only **known keys** are applied, only when the value has the **same type as the default** (Bool must be a real JSON bool; Int must be an exact integer and not a bool; Double must be finite and not a bool; String ≤ 200 characters). Keys local to this machine are skipped: `pingHost, setupDone, page, settingsTab, fanMode, fanCurve, chargeLimitOn, chargeLimit, chargeHeat, keepDays, autoUpdate` (so a shared file can't wipe history, disable updates or redirect the ping).
- Result strings: `<n> settings imported` or `Fusebox can't read this file.`
- After profile/import/reset: re-apply the Dock-icon policy (never Dock and menu bar both hidden).

### 9.5 Page: Appearance (`LookSettings`)

| Order | Section | Control | Text / options | Pref (default) |
|---|---|---|---|---|
| 1 | `Style` | segmented picker | `Mode`: `System` · `Light` · `Dark` | `appearanceMode` (`system`) |
| 2 | `Style` | ThemeTiles (3-column grid of Cards) | 6 themes, see 11.7 | `theme` (`instrument`) |
| 3 | `Text size` | TextSizeCards | `Normal` 1.0 · `Large` 1.15 · `Extra large` 1.3 + live sample row | `textScale` (1.0; clamped 0.8–1.6 when read) |
| 4 | `Font` | FontCards (4-column grid) | 7 fonts | `font` (`style`) |
| 5 | (no header) | AccentPicker | `Accent color` | `accent` (`""` = theme accent) |
| 6 | (same) | NumberStylePicker | `Number changes`: `Roll` · `Fade` · `Instant` | `numberStyle` (`fade`) |
| 7 | `Window` | LanguagePicker (pop-up) | `Language` | `appLanguage` (`""`) |
| 8 | `Window` | toggle | `Always on top` | `onTop` (false) |
| 9 | `Window` | KeepRunningToggle | `Keep measuring when the window is closed` / `So Checkup always has data. Quit with ⌘Q to stop completely.` | `keepRunning` (true) |
| 10 | `Window` | HotkeyRecorder | `Shortcut to show or hide Fusebox` | `hotkeyCode` 3, `hotkeyMods` 6400, `hotkeyLabel` `⌃⌥⌘F` |
| 11 | `Window` | DockToggle | `Icon in the Dock` | `dockIcon` (true) |
| 12 | `Window` | LoginToggle | `Open at login` | OS login item (not a pref) |
| footer | `Window` | caption | `Fusebox always opens on Live, at the size you last gave that window.` | |

**AccentPicker**: HStack of
1. system `ColorPicker("Accent color")`, no opacity; current value = stored hex or the theme's accent. Stored as `#RRGGBB` hex.
2. sample capsule: HStack spacing 6 → `CPU` in theme `label(11)` (accent color for Instrument, otherwise secondary-on-row) + a 44×14 sparkline (last 12 preview CPU values, top 60, stroke **1.5 pt**, accent color; white on Color blocks). Padding 8 h / 4 v; capsule background = first block color for Color blocks, else theme bg, else thin material.
3. `Default` button, disabled when no custom accent.
Windows: a color swatch button opening a `ColorPicker` flyout (IsAlphaEnabled=false).

**NumberStylePicker**: `LabeledContent("Number changes")` → segmented picker (labels hidden) + a live demo: every **1.5 s** cycles through `17%, 23%, 9%, 41%, 34%, 12%` using the selected transition (`ChangingText`), font theme `number(20)`, 56 pt wide right-aligned, padding 8 h / 2 v, background as above with **radius 8**. Demo is hidden from accessibility and only ticks while visible. Tooltip: `Roll: digits flip from top to bottom · Fade: a short crossfade · Instant: no animation`.

**LanguagePicker**: options shown in their own language (never translated): `System`, `English`, `Nederlands`, `Deutsch`, `Français`, `Español` (ids `""`, `en`, `nl`, `de`, `fr`, `es`). Choosing sets the app's language override; then a row appears: caption `Applies after a restart of Fusebox.` + button `Restart Fusebox` (relaunch: wait until this process exits, then reopen). Windows: `ApplicationLanguages.PrimaryLanguageOverride`, restart.

**HotkeyRecorder**: label `Shortcut to show or hide Fusebox`, subtitle `Works from any app.` (while recording: `Esc cancels, ⌫ turns it off. Use ⌘, ⌥ or ⌃.`). Trailing button in monospaced font showing the current label (e.g. `⌃⌥⌘F`), `Off` when code < 0, `Press keys…` while recording. While recording: Esc cancels; Delete/Forward-delete turns the hotkey off (code −1); any key needs at least one of ⌘/⌥/⌃ (otherwise system beep and keep recording); Shift is allowed as an extra. The key is swallowed. Recording stops when the view disappears. Windows: Ctrl+Alt+Win+F is not ideal; suggest default **Ctrl+Alt+Shift+F** via `RegisterHotKey`, modifiers Ctrl/Alt/Win required.

**DockToggle**: disabled (cannot be turned off) while the menu bar style is `off`; tooltip then `Turn on the menu bar first, otherwise you can't find Fusebox anymore`. Rule enforced globally: never Dock icon off AND menu bar off. Windows: "Show in taskbar" toggle with the same rule against "tray icon off".

**LoginToggle**: `Open at login`. On failure shows caption `Didn't work: <error>`; if the OS needs approval: `Allow Fusebox in System Settings › General › Login Items.` Windows: `StartupTask` (packaged) or Run registry key.

**Always on top** (window behavior, see section 2): floating level + visible on all desktops/next to full-screen apps.

### 9.6 Page: Live page (`LiveSettings`)

| Order | Section | Control | Text | Pref (default) |
|---|---|---|---|---|
| 1 | `Preview` | LivePreview | see below | — |
| 2 | `Measurements (drag to change the order)` | MetricList("metrics") | one toggle per metric, drag to reorder, `Restore defaults` button | `metrics` (`cpu,gpu,ram,net,ssd,battery`), `metricsHidden` (`""`) |
| 3 | `Larger window: graphs` | GraphCards (2 cards) | `Automatic` / `Never` | `graphs` (`auto`) |
| 4 | | pop-up | `Graphs show the last`: `1 min`, `5 min`, `10 min`, `15 min`, `30 min`, `hour` (values 1,5,10,15,30,60) | `shortMinutes` (5). Disabled if graphs = never |
| 5 | | segmented (fixed size) | `CPU graph`: `Usage` · `Temperature` · `Both` | `cpuGraph` (`usage`). Disabled if never |
| 6 | | segmented | `GPU graph`: `Usage` · `Temperature` · `Both` | `gpuGraph` (`usage`). Disabled if never |
| 7 | | SmoothBigToggle | `Smooth large graphs` / `Large graphs glide instead of stepping with each measurement. Costs a little more processor in a big window.` | `smoothBig` (true). Disabled if never |
| 8 | `Small window` | toggle | `Per-core CPU` / `A thin bar per processor core next to the CPU temperature. Large windows always show them.` | `perCore` (false) |
| 9 | | OptionPreview | label `CPU`, side `57°`, value `23%`; when on, a CoreBars strip (10 bars `[70,55,40,30,20,15,60,45,35,25]`, split after 6 (performance/efficiency groups), bar width 4, height 14) | |
| 10 | | toggle | `Show advice line ("Your Mac is busy", "Memory is filling up" …)` | `showAdvice` (false) |
| 11 | | toggle | `Response time next to NET` / `How quickly the internet answers, in milliseconds (ms). Lower is better.` | `netPing` (false) |
| 12 | | toggle | `Internet speed next to NET` / `Your download speed from your last speed test (Network tab). Empty until you run one.` | `netSpeed` (false) |
| 13 | | OptionPreview | `NET` + `wifi` icon, side = `17 ms` and/or `412 Mbit` joined ` · `, value `↓8 ↑2`, unit `KB/s` | |
| 14 | | toggle | `Battery time` / `Next to BAT: on battery, how long until empty; while charging (battery with a bolt), how long until full. macOS's own estimate.` | `batteryTime` (true) |
| 15 | | OptionPreview | `BAT` + `battery.75percent` icon, side `2:35` (if on), value `81%` | |
| 16 | | toggle | `Learn battery time from my use` / `On battery, "time left" comes from how fast your battery drained over the last 7 days, mixed with the last 15 minutes. Needs 2 hours on battery first; until then macOS's estimate.` | `learnedBattery` (true) |
| 17 | `Units` | UnitPicker × 3 (CPU, RAM, SSD) | segmented `%` / `cores` (CPU) or `GB` (RAM, SSD) | `absolute` comma list (default `ram,ssd`) |
| 18 | `Units` | segmented | `Internet (NET)`: `MB/s` · `Mbit/s` | `netBits` (false = MB/s) |
| 19 | `Thresholds: orange from here, red 10 higher` | slider + value | `Processor` 50–100 step 5 `%` | `warnCPU` (85) |
| 20 | | slider | `Temperature` 70–110 step 5 `°C` | `warnTemp` (95) |
| 21 | | slider | `Latency` 20–300 step 10 `ms` | `warnPing` (100) |

Notes:
- **MetricList**: a `List` with one toggle per offered metric (`Processor (CPU)`, `Graphics chip (GPU)`, `Memory (RAM)`, `Internet (NET)`, `Free storage (SSD)`, `Storage activity (read/write)`, `Battery`, `Fans (FAN)` — FAN only on Macs with fans). Order: enabled metrics first in their order, then previously-disabled ones in their remembered order (`<key>Hidden`), then the rest. Rows draggable (`onMove`). List height = offered count × **28 pt + 12**. Toggling on inserts the metric at its current list position. `limit` disables further "on" toggles once the limit is reached. Button `Restore defaults` resets `<key>` and `<key>Hidden`.
- **UnitPicker** row: title = metric title; subtitle `% shows 23% · cores shows <0.23×cores> /<cores>` (CPU), `% shows 61% · GB shows <0.61×RAM GB> GB` (RAM), `% shows 40% · GB shows 300 GB` (SSD). Uses this machine's real core count and RAM size.
- **Threshold** row: `LabeledContent(title)` with a Slider and a value text `"<int> <unit>"` in monospaced digits, 56 pt wide, right aligned. Thresholds mean: orange (theme `warn`) from the value, red (theme `danger`) from value + 10.

**LivePreview** (fake Live page with fixed data, non-interactive):
- Uses the current theme (appearance mode applied), at most the first **4** enabled metrics, laid out in rows of 2 (HStack top-aligned, spacing **10**); a lone metric in the last row is padded with an empty cell.
- VStack spacing **0** for `lines`/`plain` row styles, **6** for cards/blocks/glass.
- If showAdvice: a 7×7 **green** dot + `All calm` (theme `label(12)`, secondary), 4 pt bottom padding.
- Each metric is the real `MetricView` at tier `.m` (graphs on) or `.s` (graphs never); with graphs on each row is **84 × textScale** pt high.
- No metrics: `No measurements on`.
- With graphs: a time axis under it: `−<n> min` (or `−1 hour`) left, `now` right, theme `label(9)`, secondary-on-row.
- Padding **10**, background theme bg (thin material for Liquid Glass), **radius 10**; colorScheme forced to the theme's scheme.
- Caption under it: `Graphs only appear when the window is big enough.` or `Graphs are off: just the numbers.`
- Preview data (`PreviewData`): 24 samples, CPU = 22 + 14·sin(i/2.3) + 6·sin(1.7i); temp 47; GPU 8+5·sin(i/3); RAM 72+2·sin(i/4) %; down 900 000 + 500 000·sin(i/1.8) B/s, up 150 000; ping 18; disk 500 GB total / 300 free; read 2.4 MB/s, write 0.8 MB/s; battery 80 − i/4. "Now" = last sample with CPU 17 %, RAM 74 %.

**GraphCards**: 2-column grid of option cards: `Automatic` / `Graphs appear when the window is big enough` / `chart.xyaxis.line` (Segoe: AreaChart `E9D2`-ish) — selected when graphs ≠ never (sets `auto`); `Never` / `Always just the numbers` / `textformat.123` (Segoe: "NumberSymbol" `E8EF`-ish) — sets `never`. (`always` exists as a stored value — title `Always` — but is not offered in the UI.)

### 9.7 Page: Processes (`ProcessesSettings`)

| Order | Section | Control | Options | Pref (default) |
|---|---|---|---|---|
| 1 | `When you open Processes` | segmented `Show` | `All` · `Apps` · `Background` · `macOS` (`all/apps/background/macos`) | `procFilter` (`all`) |
| 2 | | pop-up `Sort by` | `CPU` · `Memory` · `Energy` · `GPU` · `Name` (`cpu/ram/energy/gpu/name`) | `procSort` (`cpu`) |
| 3 | `Columns` | Chips | `Disk`, `Network`, `Energy`, `GPU`, `Threads`, `Running` | `procHide` (list of hidden ids `disk,net,energy,gpu,threads,running`; default `""` = all shown) — **except** `Network` which is bound to `netPerApp` (default **false**) |
| 4 | | TablePreview | name `Safari`; always `CPU 12%`, `RAM 1.4 GB`; then visible extra columns with samples `Disk 1.2 MB/s`, `Network ↓ 80 KB/s`, `Energy 1.3 W`, `GPU 4%`, `Threads 32`, `Running 2 h`; sorted column marked ▾ | |
| footer | | caption | `Columns appear when the window is wide enough. Network: download and upload per app, read on this Mac only while you look at Fusebox. Also shows the busiest app on the Live NET card.` | |
| 5 | `Small items` | segmented `Hide` | `Nothing` (`all`) · `Idle` (`idle`) · `Under 1% CPU` (`light`) | `procSmall` (`idle`) |
| 5b | | caption (depends) | all: `Every process is listed.` · idle: `Hides items using almost no CPU (< 0.05%) and under 20 MB memory. "Show all" brings them back.` · light: `Hides items under 1% CPU and under 200 MB memory. "Show all" brings them back.` | |

Storage rule for column lists: they store what is **off**, so newly added columns default to on.

### 9.8 Page: Checkup (`CheckupSettings`)

| Order | Section | Control | Options | Pref (default) |
|---|---|---|---|---|
| 1 | `When you open Checkup` | segmented `Period` | `Last used` (`last`) · `Today` · `Yesterday` · `7 days` (`week`) | `checkupStart` (`last`) |
| 2 | `Sections` | Chips (17 chips, in page order) | `Health score` (score), `Tips` (tips), `Top apps` (tops), `Peaks` (peaks), `Busy moments` (busy), `macOS background` (background), `Battery` (battery), `Battery health` (health), `Battery drain` (drain), `Sleep drain` (sleep), `Keeps awake` (awake), `Crashes` (crashes), `Startup items` (startup), `Disk space` (disk), `SSD speed test` (ssdBench), `Running time` (runtime), `Log` (log) | `checkupHide` (`""`, list of hidden ids) |
| footer | | caption | `"Why is my Mac slow?" is always there. A category with all its sections off disappears. Exported PDFs always contain everything.` | |
| 3 | `Restart reminder` | pop-up `Suggest a restart after` | `7 days` · `14 days` · `30 days` · `Never` (0) | `restartDays` (14) |
| 3b | | caption | Never: `Checkup never suggests a restart.` else `A tip in Checkup: "Not restarted for <n> days. A restart clears memory and finishes updates."` | |

Page order of categories referenced: summary, Performance, Battery & power, Stability, Startup & storage, History.

### 9.9 Page: Network (`NetworkSettings`)

| Order | Section | Control | Text | Pref (default) |
|---|---|---|---|---|
| 1 | `Devices` | toggle | `Scan when you open Network` / `Off: Fusebox only looks for devices on your network when you press Scan.` | `netAutoScan` (true) |
| 2 | | toggle | `Notify me about new devices` / `While Network scans. Same as the bell on the page.` (turning on requests notification permission) | `notifyNewDevice` (false) |
| 3 | `Columns` | Chips | `MAC` (mac), `Vendor` (vendor), `Type` (kind) | `netHide` (`""`) |
| 4 | | TablePreview | name `Living Room`; `IP 192.168.1.5`, then visible `MAC B8:27:EB:01:02:03`, `Vendor Raspberry Pi`, `Type Computer`, then `ms 3` | |
| footer | | caption | `Columns appear when the window is wide enough.` | |
| 5 | `Speed test` | segmented `Unit` | `Mbit/s` · `MB/s`; subtitle `Mbit/s shows 412 Mbit/s · MB/s shows 51.5 MB/s` | `speedMB` (false) |
| 6 | `Internet check` | label + button | `Ping` / subtitle `On, to <host>` or `Off: Fusebox makes no internet connections`; button `Change in Privacy` (switches sidebar to Privacy) | reads `pingHost` |

Privacy note for the port: the MAC column sample here is a fake value in a preview. (Section 11 privacy rules cover what the real pages may show.)

### 9.10 Page: Docked (`DockedSettings`)

| Order | Section | Control | Text | Pref (default) |
|---|---|---|---|---|
| 1 | `Ports` | toggle | `Show hubs & internals` / `Also the hubs inside a dock and its Billboard part. Same as the button on the page.` | `dockedInternals` (false) |
| 2 | | toggle | `List empty ports` / `One line at the bottom, like "USB-C 2 · USB-C 3 · empty".` | `dockedEmpty` (true) |
| 3 | | toggle | `Show displays` / `External monitors under the port or DisplayLink dock that drives them, with resolution and refresh rate.` | `dockedDisplays` (true; not in Prefs.defaults, @AppStorage default) |
| 4 | `Wireless` | toggle | `Show wireless` / `Bluetooth devices with battery, and a row when your internet runs through a phone hotspot or mobile modem.` | `dockedWireless` (true; @AppStorage default) |
| 5 | `Speed` | segmented `Link speed` | `Gbit/s` · `GB/s`; subtitle `Gbit/s shows 40 Gbit/s · GB/s shows 5 GB/s (handy to compare with a drive)` | `dockedBytes` (false) |

### 9.11 Page: Menu bar (`MenubarSettings`)

| Order | Section | Control | Text | Pref (default) |
|---|---|---|---|---|
| 1 | `In the menu bar` | MenubarStyleCards (3-column grid, 6 cards) | `Text (CPU 23%)`, `Compact (small symbols)`, `Bars`, `Graphs`, `Just an icon`, `Don't show` | `menubarStyle` (`text`) |
| 2 | | toggle | `Each measurement in its own spot (max. 3)` | `menubarSplit` (false) |
| 3 | `Measurements in the menu bar (max. 4)` | MetricList("menubarMetrics", limit 4) | | `menubarMetrics` (`cpu`), `menubarMetricsHidden` (`""`) |
| 4 | `When you click it` | segmented `Type` | `Regular menu` (`menu`) · `Panel with live values` (`panel`) | `menuStyle` (`panel`) |
| 5 | (only if panel) | toggle | `Rich menu bar panel` / `Health score, live values, the most important tip and quick buttons.` | `richPanel` (true) |
| 6 | (only if menu, or panel without rich) | 4 toggles | `How it's doing (one sentence)` (status), `All values` (values), `3 heaviest apps` (top), `Shortcuts` (links) | `menuParts` (`status,values,top,links`) |

**MenubarStyleCards**: each Card = VStack spacing 6 → `MenubarPreview` + style title (`.caption`, centered, 2 lines reserved). The `Don't show` card is **disabled and at 0.4 opacity** when the Dock icon is off, with caption `"Don't show" needs the Dock icon on, otherwise you can't find Fusebox.`

**MenubarPreview** (fixed data, first ≤4 menu-bar metrics, default `[cpu]`): content font **11 pt monospaced digits**, 1 line, min scale 0.5, **14 pt** tall; padding 8 h / 4 v; background `.quaternary` **capsule**.
- text: `CPU 17%  RAM 74%` (abbr + menu value, two spaces between)
- compact: SF Symbol per metric + value + space
- bars: HStack spacing 2 of mini bars 6×14: outline rounded rect (radius 1.5, 1 pt stroke) with a filled rect from the bottom at the metric fraction
- graph: HStack spacing 3, per metric a 22 pt wide sparkline of the last 12 samples (stroke 1.2)
- icon: `gauge.with.needle`
- off: `—` in secondary

Windows mapping: the macOS menu bar item = a notification-area (tray) icon. Text/compact/bars/graph styles must be drawn into the tray icon bitmap (16–32 px), or offered as a small always-on-top "taskbar widget"; document the limitation (see section 8).

### 9.12 Page: Notifications (`AlertSettings`)

| Order | Section | Control | Text | Pref (default) |
|---|---|---|---|---|
| 1 | (none) | toggle | `Show notifications` (turning on requests OS permission) | `notify` (true) |
| 2 | | stepper | `Same kind at most once every <n> min`, 5–240 step 5; disabled if notify off | `alertCooldown` (30) |
| 3 | | label + button | `Test` / `No notification? Allow Fusebox in System Settings › Notifications.`; button `Send test notification` → title `Fusebox update available (test)`, body `This is how Fusebox tells you about a new version.` | |
| 4 | `Per kind: limit and how many minutes in a row` (disabled if notify off) | AlertRow × 7 + PeakAlertRow | see table | `alert.<kind>.on/limit/min` |
| 5 | (none) | caption + link | `If Focus or Do Not Disturb is on, macOS keeps these notifications quiet too.` · link `Open Focus settings` | Windows: link to `ms-settings:quiethours` |

**AlertRow** layout: toggle with title + subtitle `<describe(limit)>, <minutes> min in a row`. When on (and not compact): an HStack (`.callout`, indented **20 pt**) with `Stepper("Limit: <describe>")` left and `Stepper("<n> min")` (1–60) right; below it a NotificationExample (indented 20) showing the real text produced with a value just over the limit.

| Kind (`alert.<id>.*`) | Title | Default on | Default limit | Default min | Limit range / step | describe(limit) | Example notification body |
|---|---|---|---|---|---|---|---|
| `cpu` | Processor busy | ✔ | 90 | 5 | 20–100 / 5 | `above 90%` | `Your Mac has been at 92% processor for 5 min` (+ ` — mostly because of <app>`) |
| `heat` | Mac too hot | ✔ | 100 | 5 | 70–110 / 5 | `above 100 °C` | `Your Mac has been at 102 °C for 5 min and is slowing itself down` |
| `mem` | Memory full | ✔ | 4 | 2 | 2–4 / 2 (2 = high, 4 = critical) | `pressure critical` / `pressure high` | `Your memory is full` (+ ` — <app> is using <x> GB`) |
| `disk` | Storage almost full | ✔ | 10 | 1 | 1–200 / 1 | `less than 10 GB free` | `Only 7 GB free on your SSD` |
| `net` | Internet slow or down | ✘ | 300 | 3 | 50–1000 / 50 | `above 300 ms or offline` | `Internet has been slow for 3 min (340 ms)` / `No internet for 3 min` (only if ping on) |
| `battery` | Battery low | ✘ (Laptop preset turns it on) | 20 | 1 | 5–50 / 5 | `20% or less` | `Battery at 18% — plug in the charger` (only when not charging) |
| `app` | One app hogging | ✘ | 50 | 10 | 20–100 / 5 | `above 50%` | fallback example `Safari has been using 55% processor for 10 min` |

**PeakAlertRow** (`alert.peak.on` default **true**, `alert.peak.min` default **2**): toggle `Sustained peak` / `Names the app behind a peak of <n> min or more, with a Stop app button`. When on: stepper `<n> min` (1–30, callout, indent 20) + NotificationExample with e.g. title `Safari is keeping your CPU busy`, body `CPU 92% for 2 minutes`. (Other kinds: `<app> is keeping your GPU busy`, `<app> is heating up your Mac`, `<app> is filling your memory`, `<app> is keeping your disk busy`; body `<kind> <value> for 1 minute` / `… for <m> minutes`.) Actions on the real notification: `Stop app` and `Show` (system processes only get `Show`).

### 9.13 Page: Fans (`FanSettings`, only Macs with fans)

- No fans: `This Mac has no fans.`
- Section `Fans now`: per fan a row — label `Fan` (or `Fan 1`, `Fan 2` …), subtitle `macOS range <min>–<max> rpm` (+ ` · set by Fusebox` when forced); trailing a 140×22 sparkline of the last 150 rpm readings (top = fan max, stroke 1.5 accent) and `<rpm> rpm` (en_US grouping, e.g. `1,850 rpm`), monospaced, 80 pt right-aligned. Empty: `Reading the fans…`.
- Section: segmented `Control`: `Automatic (macOS)` (`auto`) · `Custom curve` (`curve`) · `Full speed` (`max`) → pref `fanMode` (default `auto`). If not auto and helper not installed: orange caption `Install the helper below, then Fusebox can set the fans.` Footer: `Fusebox never runs the fans slower than macOS's minimum. Above 95 °C the fans go to full speed automatically.`
- Only when `curve`: section `Fan speed by temperature (drag the points)` → **CurveEditor** + preset buttons `Quiet` / `Balanced` / `Cool` (disabled when already active) + right `Now <t> °C → <p>%` (monospaced, secondary).
  - Pref `fanCurve` default Balanced `40:0,60:20,75:50,85:80,95:100`; Quiet `40:0,65:10,80:40,90:75,95:100`; Cool `40:10,55:35,70:65,80:90,90:100`. Format `°C:%` pairs; valid = 2–8 points, °C 40–100 strictly increasing, % 0–100.
  - CurveEditor visuals: plot **170 pt** tall, x = 40–100 °C, y = 0–100 % (between macOS min and max rpm). Grid: 7 vertical and 5 horizontal lines, `.quaternary` 0.5 pt. A **red 50 % opacity dashed** (3/3) vertical line at 95 °C. Curve: accent **2 pt** polyline, flat extension to both edges. Points: 12 pt accent circles with a 2 pt background-colored ring, 24 pt hit area, drag (min distance 0) — each point stays ≥1 °C from neighbours, rounded to whole °C and %. Current temperature: **10 pt orange dot** on the curve. Axis labels `.caption2` secondary: `40 °C` · `70 °C` · `95 °C: full speed` (red 0.8). Caption `Bottom = macOS's minimum speed, top = full speed.` Input temperature = max(CPU temp, GPU temp).
- **HelperSection** (also on Battery page): header `Fusebox helper`.
  - Installed: `Helper` value `Installed` / `Installed, not responding` / `Update needed`; outdated → orange caption `This Fusebox has a newer helper. Click Reinstall… to update it (asks for your password).`; buttons `Reinstall…` and destructive `Uninstall helper…` (uninstall resets fanMode to auto and charge limit off).
  - Not installed: `Helper` + button `Install…`, caption `Not installed. Installing asks for your password once.`
  - Footer: `A small background program that runs as administrator. It is the only part that can change fan speed or charging. If Fusebox stops talking to it, the fans go back to macOS after 30 s and charging back to normal after 10 minutes.`
  - Windows: fan control is generally not possible on consumer Windows without vendor drivers — hide the Fans page unless a supported vendor API (e.g. via LibreHardwareMonitor/WMI) is present; a helper would be a Windows service.

### 9.14 Page: Battery (`ChargeSettings`, only with a battery)

Section header `Charge limit`:
- Toggle `Limit charging` / `Keeps the battery healthier when you're mostly plugged in. Needs the Fusebox helper (admin password once).` → `chargeLimitOn` (false). Disabled when unsupported.
- `Stop charging at` → slider 50–100 step 5, 200 pt wide, + `<n>%` (44 pt, monospaced) → `chargeLimit` (80). Disabled unless on.
- Toggle `Pause charging when the battery is hot` / `From 40 °C until it has cooled to 37 °C.` → `chargeHeat` (false). Disabled unless on.
- Footer (caption): unsupported → orange `Not supported on this Mac: macOS doesn't let apps hold the charge here. Use macOS's own charge limit in System Settings › Battery.` + button `Open Battery Settings`; or (on, no helper) orange `Install the helper below, then Fusebox can hold the charge.`; always `Charging starts again 5% below the limit. When Fusebox quits or you turn this off, charging goes back to normal.`; plus status `Now 81% · 31 °C` + ` · holding` / ` · paused (warm)`.
- Then HelperSection.
- Windows: no general API; link to the OEM tool / "Smart charging" setting, or hide.

### 9.15 Page: Privacy (`DataSettings`)

| Order | Section | Control | Text | Pref (default) |
|---|---|---|---|---|
| 1 | `Measurement history` | pop-up `Keep logs` | `1 week` (7) · `30 days` (30) · `3 months` (90) · `1 year` (365) · `Forever` (0) | `keepDays` (30) |
| 2 | | read-only row | `Folder` = log folder path with home replaced by `~` | |
| 3 | | buttons | `Open log folder` (creates it if needed) · destructive `Delete all logs…` | |
| 3b | | confirmation | title `Delete all logs?`, message `All CSV files in the log folder will be removed. This can't be undone.`, button `Delete` | |
| footer | | caption | `Fusebox always saves a small file per day on your Mac, so Checkup can look back.` | |
| 4 | `Internet` | PingToggle | `Measure internet latency (ping)` (on = host `1.1.1.1`, off = empty string) | `pingHost` (`1.1.1.1`) |
| 5 | | text field (only if on) | `Measure to` (editable host) | `pingHost` |
| 5b | | caption | on: `One short connection to this server every 10 s, port 443. Nothing else goes to the internet.` · off: `Off: Fusebox makes no internet connections at all.` | |

### 9.16 Page: About (`AboutSettings`)

1. Header block: HStack spacing **14** → app icon **56×56** + VStack spacing 2: `Fusebox` (`.title3` semibold) and `Version <v>` secondary. Version string = `<CFBundleShortVersionString>` + ` beta` if it starts with `0.` + ` (<build>)`, e.g. `0.6 beta (202611211845)`.
2. Secondary text: `A lightweight activity monitor for your Mac: live measurements, apps and advice.`
3. Credits rows (`LabeledContent`): `Made by` → `ClaudIA Software & Development`; `Lead Developer` → `Joël Nieuwkoop`; `Chief Testing Officer` → `Stijn Mutsaers`.
4. **UpdateSection** — header `Updates`:
   - `Installed` → current version string.
   - Toggle `Update automatically` / `Fusebox checks GitHub every 15 minutes, tells you when there is a new version, installs it and restarts. Your settings stay.` → `autoUpdate` (true, local-only key).
   - Row: button `Check for updates` (disabled while checking/installing or when no repo configured) + right-aligned status caption:
     | State | Text |
     |---|---|
     | idle | `Last checked <time short>` (empty if never) |
     | checking | `Checking…` |
     | upToDate | `You're up to date` |
     | available(tag) | `Fusebox <0.7 beta> available` |
     | installing | `Installing…` |
     | failed(why) | the error text (e.g. `GitHub answered 403`, `Download failed`, `Update isn't signed by the Fusebox maker`, `Not a Fusebox update`, `Version mismatch`, `codesign failed`, `Couldn't replace the app: …`) |
5. Section header `Beta test`: label `Beta: how light is Fusebox on this Mac?` / subtitle `Fusebox notes its own CPU, memory, GPU and energy use once a minute, and looks for 5 seconds at what it is doing right now. This makes a small text file in Downloads (also copied) to send to the maker. Nothing is sent automatically.`; trailing button `Share performance report` → while running `Measuring 5 s…` (disabled).
6. **ChangelogSection** — header `What's new`:
   - Loading: `Loading…` (secondary). Failure (empty list): `Couldn't load the changelog. Check your internet connection.` (retried next time the page opens).
   - Each release: VStack spacing 4, 2 pt vertical padding. Top HStack spacing 6: release title (`.headline`, e.g. `Fusebox 0.6 beta`); if this release (or any merged rebuild of it) is the installed build → badge `Installed` (`.caption` medium, padding 6 h / 1 v, **capsule** filled with the tint at **0.15**); spacer; date `.caption` secondary (day month year, localized). Below: release notes rendered as **Markdown** (bold, links, bullets), secondary color, text selectable.
   - Releases with the same title (quick re-publishes of one version) are merged into one entry: newest build first, notes concatenated with newline.

### 9.17 Master table of all preferences

"Local" = never exported/imported. "UI" = where the user changes it.

| Key | Type | Default | UI location |
|---|---|---|---|
| `theme` | string | `instrument` (`instrument`,`terminal`,`minimal`,`glas`,`blokken`,`liquid`) | Appearance › Style; Setup step 2; presets |
| `appearanceMode` | string | `system` (`system`/`light`/`dark`) | Appearance › Style › Mode |
| `accent` | string hex | `""` | Appearance; Setup step 3 |
| `textScale` | double | 1.0 (1.0/1.15/1.3; clamp 0.8–1.6) | Appearance › Text size; Setup step 3 |
| `font` | string | `style` (`style`,`sfPro`,`rounded`,`mono`,`serif`,`montserrat`,`ubuntu`) | Appearance › Font |
| `numberStyle` | string | `fade` (`roll`/`fade`/`instant`) | Appearance |
| `lowImpact` | string | `auto` (`off`/`on`/`auto`) | Profile |
| `page` | string | `live` | (current window tab; local; app always opens on Live) |
| `settingsTab` | string | `profile` | (current Settings tab; local) |
| `dockIcon` | bool | true | Appearance › Window; Setup step 8 |
| `keepRunning` | bool | true | Appearance › Window |
| `autoUpdate` | bool | true | About › Updates (local) |
| `onTop` | bool | false | Appearance › Window |
| `hotkeyCode` | int | 3 (F key; −1 = off) | Appearance › Window |
| `hotkeyMods` | int | 6400 (⌃⌥⌘, Carbon 4096+2048+256) | Appearance › Window |
| `hotkeyLabel` | string | `⌃⌥⌘F` | Appearance › Window |
| `metrics` | string list | `cpu,gpu,ram,net,ssd,battery` | Live page › Measurements; Setup step 4 |
| `metricsHidden` | string list | `""` | (order memory of disabled metrics) |
| `graphs` | string | `auto` (`auto`/`always`/`never`) | Live page; Setup step 6 |
| `shortMinutes` | int | 5 (1,5,10,15,30,60) | Live page |
| `absolute` | string list | `ram,ssd` (subset of `cpu,ram,ssd`) | Live page › Units; Setup step 5; also clicking a number on Live |
| `showAdvice` | bool | false | Live page › Small window |
| `netBits` | bool | false | Live page › Units |
| `perCore` | bool | false | Live page › Small window |
| `batteryTime` | bool | true | Live page |
| `learnedBattery` | bool | true | Live page |
| `netSpeed` | bool | false | Live page |
| `netPing` | bool | false | Live page |
| `lastSeenBuild` | int | 0 | (updater bookkeeping) |
| `dockedInternals` | bool | false | Docked page + Settings › Docked |
| `cpuGraph` | string | `usage` (`usage`/`temp`/`both`) | Live page |
| `gpuGraph` | string | `usage` | Live page |
| `warnCPU` | double | 85 (50–100) | Live page › Thresholds |
| `warnTemp` | double | 95 (70–110) | Live page › Thresholds |
| `warnPing` | double | 100 (20–300) | Live page › Thresholds |
| `procFilter` | string | `all` | Processes |
| `procSort` | string | `cpu` | Processes |
| `procHide` | string list | `""` | Processes › Columns |
| `procSmall` | string | `idle` (`all`/`idle`/`light`) | Processes › Small items |
| `checkupStart` | string | `last` (`last`/`today`/`yesterday`/`week`) | Checkup |
| `checkupHide` | string list | `""` | Checkup › Sections |
| `restartDays` | int | 14 (7/14/30/0) | Checkup |
| `netAutoScan` | bool | true | Network |
| `notifyNewDevice` | bool | false | Network (+ bell on page) |
| `netHide` | string list | `""` | Network › Columns |
| `speedMB` | bool | false | Network › Speed test |
| `dockedEmpty` | bool | true | Docked |
| `dockedBytes` | bool | false | Docked › Speed |
| `dockedDisplays` | bool | true (view default, not in Prefs.defaults → not exported) | Docked |
| `dockedWireless` | bool | true (view default, not exported) | Docked |
| `menubarMetrics` | string list | `cpu` (max 4) | Menu bar; Setup step 7 |
| `menubarMetricsHidden` | string list | `""` | |
| `menubarStyle` | string | `text` (`text`,`compact`,`bars`,`graph`,`icon`,`off`) | Menu bar; Setup step 7 |
| `menubarSplit` | bool | false | Menu bar |
| `menuStyle` | string | `panel` (`menu`/`panel`) | Menu bar |
| `menuParts` | string list | `status,values,top,links` | Menu bar (menu or classic panel) |
| `richPanel` | bool | true | Menu bar |
| `notify` | bool | true | Notifications; Setup step 9 |
| `smoothBig` | bool | true | Live page › graphs |
| `alertCooldown` | int | 30 (5–240) | Notifications |
| `alert.peak.on` | bool | true | Notifications › Sustained peak |
| `alert.peak.min` | int | 2 (1–30) | Notifications |
| `alert.<cpu/heat/mem/disk/net/battery/app>.on/limit/min` | bool/double/int | see 9.12 table | Notifications; Setup step 9 (on/off only) |
| `keepDays` | int | 30 (7/30/90/365/0) | Privacy; Setup step 10 (7/30/90/0) — local |
| `pingHost` | string | `1.1.1.1` (`""` = off) | Privacy; Setup step 10 — local |
| `netPerApp` | bool | false | Processes › Columns (Network chip) |
| `fanMode` | string | `auto` (`auto`/`curve`/`max`) | Fans — local |
| `fanCurve` | string | Balanced curve | Fans — local |
| `chargeLimitOn` | bool | false | Battery — local |
| `chargeLimit` | int | 80 (50–100 step 5) | Battery — local |
| `chargeHeat` | bool | false | Battery — local |
| `setupDone` | bool | false | set by setup — local |

Non-exported bookkeeping keys seen in code (store them too, but never in the share file): `appLanguage`, `liveSize`, `windowSize`, `windowTopLeft`, `failedUpdateBuild`, `notifiedUpdate`, `notifyAskRound`, `lanAsked`, `loginRefreshedFusebox`, `ssdBenchLast`, `speedHistory`, `netFindNow`, `netRecentRanges`, `netPanelOpen`, `checkupOpen`, `analysisPeriod`, `appPeriod`, `fanMetricAdded`.

All stored choices are short fixed ids (never display text) — safe to share and language-independent. Windows: `ApplicationData.Current.LocalSettings` or a JSON file in `%LOCALAPPDATA%\Fusebox`.

---


---

## 10. Themes and visual system

Source: `Sources/Theme.swift` (tokens, row styles, Spark path math, Color(hex:), Notify), `Sources/Overzicht.swift` (SparkView/SparkNSView layers, CoreBars, ChangingText, IntroIn, stepTick), `Sources/Venster.swift` (where the theme is resolved and injected), `Sources/Instellingen.swift` (AccentPicker, NumberStylePicker, LowImpactPicker, SmoothBigToggle).

### 10.0 How a theme is resolved (pipeline — implement exactly in this order)

Every window/panel computes its theme like this on each render:

1. `Theme.named(pref "theme")` — falls back to **Instrument** if the id is unknown.
2. `resolved()` overlays three prefs:
   - `accent` (string `"#RRGGBB"`, `""` = theme default) → replaces `accent` if it parses.
   - `textScale` (Double, default `1.0`) → clamped to **0.8 … 1.6** → `scale`.
   - `font` (string id, default `"style"`) → `AppFont`; unknown → `.style`.
3. `adapted(to: scheme)` where scheme = Light if `appearanceMode == "light"`, Dark if `"dark"`, otherwise the OS scheme (`"system"`, default). This converts fixed-scheme themes to the other scheme (10.3) and then fixes accent legibility (10.4).
4. If Low impact is currently on → `.solid` (10.6) (only changes Liquid Glass).
5. Injected as an environment value (`\.theme`) plus `foregroundStyle(theme.text)` and `tint(theme.accent)` on the whole window content. The window's container background = `theme.bg` (or `.thinMaterial` when `bg == nil`, i.e. Liquid Glass). Windows equivalent: a `ThemeService` singleton raising `ThemeChanged`; root `Grid.Background` = bg, or Mica/Acrylic (`DesktopAcrylicBackdrop` / `MicaBackdrop`) when bg is null.
6. `preferredColorScheme`: nil when `appearanceMode == "system"`, else the forced scheme (WinUI: `RequestedTheme` on the root element).

The menu bar panel (Menubar.swift) uses the same pipeline (`Theme.resolved(themeID).adapted(to:)` + `.solid` if Low impact).

Theme equality (used to decide re-render): equal when `id`, `accent`, `scale`, `font` and `rowStyle` are equal.

### 10.1 Theme token model

| Token | Type | Meaning / where used |
|---|---|---|
| `id` | string | Stable stored id (`instrument`, `terminal`, `minimal`, `glas`, `blokken`, `liquid`). Note the Dutch ids for Glass (`glas`) and Color blocks (`blokken`) — keep them for settings-file compatibility. |
| `name` | string | Display name. Theme names are product names and are **never translated** (they go through `L()` but every language keeps them; "Liquid Glass" is a literal). |
| `bg` | color or null | Window/page background. `null` = system window glass (Liquid Glass theme only). |
| `text` | color | Primary text and numbers. |
| `secondary` | color | Labels, units, captions, inactive tab icons. |
| `accent` | color | Graph lines, selected tab, Instrument's metric labels, tint for controls. |
| `warn` | color | Warning level (level 1) values. |
| `danger` | color | Danger level (level 2) values. |
| `good` | color | OK/green state (e.g. the Low-impact leaf on the gear). |
| `line` | color | Separators (`.lines` row style) and card borders (`.cards`). |
| `card` | color | Card fill (`.cards` row style; also generic card backgrounds elsewhere). |
| `rowStyle` | enum | `lines`, `plain`, `cards`, `blocks`, `glass` (10.5). |
| `design` | font design | `rounded`, `monospaced`, `default` (system font family variant). |
| `weight` | font weight | Weight for **numbers** (`theme.number(size)`). Labels are always `.medium`. |
| `blocks` | color[] | Only for Color blocks: per-row fill palette, cycled by row index. |
| `scheme` | Light/Dark/null | The scheme the theme was designed for; null = follows the OS (Liquid Glass). |
| `scale` | 0.8–1.6 | Text scale multiplier. |
| `font` | AppFont | Font family override. |

Derived helpers:

| Helper | Definition |
|---|---|
| `number(size)` | font(`size × scale`, weight = theme weight, design = theme design) **with monospaced (tabular) digits**. WinUI: `FontWeight` + `Typography.NumeralAlignment="Tabular"` (or `font-feature tnum`). |
| `label(size = 12)` | font(`size × scale`, weight **medium**, theme design). No tabular digits. |
| `onRow(secondary)` | Text color on a row. For Color blocks always **white** (primary) or **white 80 % opacity** (secondary); otherwise `secondary` or `text`. |
| `background` | `bg` as a fill, or transparent when null. |
| `rowPad` | Extra vertical space each row consumes (padding + spacing) for layout-fit math on Live: `lines` 15 pt, `plain` 10 pt, `cards`/`blocks`/`glass` 22 pt. |

### 10.2 The six themes (exact values from code)

Order in pickers: Instrument, Terminal, Minimal light, Glass, Color blocks, Liquid Glass. Default theme: **Instrument**.

RGB floats in code converted to sRGB hex (rounded ×255).

#### Instrument (`instrument`) — default, designed **dark**

| Token | Value | Hex |
|---|---|---|
| bg | rgb(0.122, 0.169, 0.239) | `#1F2B3D` (deep slate blue) |
| text | rgb(0.95, 0.96, 0.97) | `#F2F5F7` |
| secondary | rgb(0.60, 0.65, 0.72) | `#99A6B8` |
| accent | rgb(0.96, 0.72, 0.24) | `#F5B83D` (amber) |
| warn | rgb(1.0, 0.60, 0.25) | `#FF9940` |
| danger | rgb(1.0, 0.38, 0.35) | `#FF6159` |
| good | rgb(0.35, 0.85, 0.60) | `#59D999` |
| line | rgb(0.17, 0.23, 0.31) | `#2B3B4F` |
| card | rgb(0.15, 0.20, 0.28) | `#263347` |
| rowStyle | `lines` | |
| design / weight | rounded / **semibold** | SF Rounded → Windows: no rounded system font; use Segoe UI Variable Display Semibold (or bundle "Nunito"/"SF Rounded"-like font) |
| scheme | dark | |
| Special | On Live, metric **labels** (CPU, RAM…) are drawn in the **accent** color only for Instrument (`theme.id == "instrument" ? accent : onRow(true)`). Same in the accent-picker preview. |

#### Terminal (`terminal`) — designed **dark**

| Token | Value | Hex |
|---|---|---|
| bg | rgb(0.04, 0.06, 0.05) | `#0A0F0D` (near-black green) |
| text | rgb(0.49, 0.99, 0.60) | `#7DFC99` (phosphor green) |
| secondary | rgb(0.25, 0.69, 0.37) | `#40B05E` |
| accent | = text | `#7DFC99` |
| warn | rgb(1.0, 0.80, 0.25) | `#FFCC40` |
| danger | rgb(1.0, 0.35, 0.35) | `#FF5959` |
| good | = text | `#7DFC99` |
| line | rgb(0.11, 0.16, 0.13) | `#1C2921` |
| card | rgb(0.07, 0.10, 0.08) | `#121A14` |
| rowStyle | `plain` | |
| design / weight | **monospaced** / regular | SF Mono → Windows: Cascadia Mono / Consolas |
| scheme | dark | |

#### Minimal light (`minimal`) — designed **light**

Contrast note in code: every text color ≥ 4.5:1 (WCAG AA) on white and on the card color (grey 6.7, orange 5.1, red 5.9, green 6.0). One calm blue accent; thin separators.

| Token | Value | Hex |
|---|---|---|
| bg | white | `#FFFFFF` |
| text | white 0.07 | `#121212` |
| secondary | white 0.36 | `#5C5C5C` |
| accent | rgb(0.0, 0.42, 0.85) | `#006BD9` |
| warn | rgb(0.69, 0.33, 0.0) | `#B05400` |
| danger | rgb(0.77, 0.12, 0.12) | `#C41F1F` |
| good | rgb(0.05, 0.45, 0.24) | `#0D733D` |
| line | white 0.90 | `#E6E6E6` |
| card | white 0.97 | `#F7F7F7` |
| rowStyle | `lines` | |
| design / weight | default (SF Pro) / regular | Windows: Segoe UI Variable |
| scheme | light | |

#### Glass (`glas`) — designed **dark** (white on violet; not real glass)

| Token | Value | Hex |
|---|---|---|
| bg | rgb(0.36, 0.29, 0.84) | `#5C4AD6` (violet) |
| text | white | `#FFFFFF` |
| secondary | white @ 78 % | `#FFFFFF` α 0.78 |
| accent | white | `#FFFFFF` |
| warn | rgb(1.0, 0.85, 0.40) | `#FFD966` |
| danger | rgb(1.0, 0.55, 0.55) | `#FF8C8C` |
| good | white | `#FFFFFF` |
| line | white @ 35 % | |
| card | white @ 16 % | |
| rowStyle | `cards` | |
| design / weight | rounded / semibold | |
| scheme | dark | |
| Special | In the light conversion the accent becomes `white 0.08` (`#141414`), because white accent is unreadable on white. |

#### Color blocks (`blokken`) — designed **light**

| Token | Value | Hex |
|---|---|---|
| bg | rgb(0.96, 0.95, 0.92) | `#F5F2EB` (warm paper) |
| text | white 0.10 | `#1A1A1A` |
| secondary | rgb(0.42, 0.40, 0.37) | `#6B665E` |
| accent | rgb(0.18, 0.49, 0.84) | `#2E7DD6` |
| warn | rgb(0.95, 0.55, 0.10) | `#F28C1A` |
| danger | rgb(0.85, 0.20, 0.20) | `#D93333` |
| good | rgb(0.11, 0.62, 0.46) | `#1C9E75` |
| line | rgb(0.85, 0.83, 0.77) | `#D9D4C4` |
| card | white | `#FFFFFF` |
| rowStyle | `blocks` | |
| design / weight | rounded / **bold** | |
| scheme | light | |
| blocks palette (row index mod 5) | 0 `#E0573D` coral · 1 `#2E7DD6` blue · 2 `#1C9E75` green · 3 `#7A54C7` purple · 4 `#D9991A` mustard | |
| Special | Everything on a block is white: text white, secondary white 80 %, graph line/fill white (`SparkView` and `CoreBars` use `.white` instead of accent when rowStyle is blocks), value colors do **not** turn warn/danger (always `onRow()` white — warnings are conveyed by text only). Accent legibility fix for light mode is skipped for this theme. |

#### Liquid Glass (`liquid`) — follows the OS scheme

| Token | Value |
|---|---|
| bg | **null** → window uses system translucent material (`.thinMaterial`) |
| text | system `.primary` (label color: black-ish in light, white in dark) |
| secondary | system `.secondary` |
| accent | system accent color (user's macOS accent) → Windows: `SystemAccentColor` |
| warn / danger / good | system `.orange` / `.red` / `.green` (Windows: `#FF9500`/`#FF3B30`/`#34C759`-like, or `SystemFillColorCaution/Critical/Success`) |
| line | `.primary` @ 10 % |
| card | clear |
| rowStyle | `glass` |
| design / weight | rounded / semibold |
| scheme | null (never converted; adapts automatically) |
| Name | Literal "Liquid Glass" (not even passed through translation). |

Windows mapping: window backdrop = **Mica** (or `DesktopAcrylicBackdrop` for stronger translucency); the "glass" cards are painted gradients (10.5), **not** acrylic.

### 10.3 Light/Dark conversion (`appearanceMode` = system / light / dark; default `system`)

Picker titles: "System", "Light", "Dark". A theme whose designed `scheme` differs from the target scheme is rebuilt with **neutral greys** while keeping accent, warn, danger, good, rowStyle, design, weight, blocks palette, scale and font:

| Token | Converted to Dark | Converted to Light |
|---|---|---|
| bg | white 0.11 `#1C1C1C` | white `#FFFFFF` |
| text | white 0.95 `#F2F2F2` | white 0.08 `#141414` |
| secondary | white 0.62 `#9E9E9E` | white 0.45 `#737373` |
| line | white 0.22 `#383838` | white 0.90 `#E6E6E6` |
| card | white 0.16 `#292929` | white 0.96 `#F5F5F5` |

So: Instrument / Terminal / Glass in Light mode become white pages with their own accent (Glass: accent → `#141414`); Minimal light / Color blocks in Dark mode become `#1C1C1C` pages. Liquid Glass is never converted. Color blocks keep their colored blocks in dark mode.

### 10.4 Accent adaptation (always applied after conversion)

Using WCAG relative luminance `L = 0.2126 R + 0.7152 G + 0.0722 B` (sRGB linearised: `v ≤ 0.04045 ? v/12.92 : ((v+0.055)/1.055)^2.4`):

| Condition | New accent |
|---|---|
| Target **dark** and L < 0.3 (dark accent vanishes on dark) | HSB(same hue, saturation × 0.6, brightness 1.0) |
| Target **light** and L > 0.6 and rowStyle ≠ blocks (light accent such as cyan vanishes on white) | HSB(same hue, min(sat × 1.3 + 0.2, 1), brightness 0.55) |
| otherwise | unchanged |

This also applies to a user-chosen custom accent.

**Custom accent** (`accent` pref, Settings › Look): system color picker "Accent color" without opacity, stored as `"#RRGGBB"` (uppercase, sRGB-rounded via `Color.hex`); a "Default" button clears it (disabled when already default). Next to the picker a live preview capsule (padding h 8 / v 4, filled with the theme bg, or block color 0 for Color blocks, or thin material for Liquid Glass) shows the label "CPU" (label font 11; accent color for Instrument, otherwise secondary-on-row) and a 44×14 pt sparkline of 12 sample points stroked 1.5 pt in the accent (white for Color blocks).

`Color(hex:)`: accepts `"#RRGGBB"` or `"RRGGBB"`, exactly 6 hex digits; anything else → null (= keep default).

### 10.5 Row styles (`ThemedRow` / `.themedRow(theme, index)`)

Every metric row/card on Live, and many rows elsewhere, is wrapped by this modifier. `index` selects the block color.

| rowStyle | Themes | Padding | Fill | Border / extra | Corner radius |
|---|---|---|---|---|---|
| `lines` | Instrument, Minimal light | vertical 7, horizontal 0 | none | 1 pt bottom separator in `line` color (full width) | — |
| `plain` | Terminal | vertical 5, horizontal 0 | none | none | — |
| `cards` | Glass (and Liquid Glass under Low impact) | horizontal 10, vertical 8 | `card` | 1 pt stroke in `line` color | 12 (plain rounded rect) |
| `blocks` | Color blocks | horizontal 10, vertical 8 | `blocks[index % 5]` | none | 10 |
| `glass` | Liquid Glass | horizontal 10, vertical 8 | vertical linear gradient top→bottom: **dark** white 11 % → white 5 %; **light** white 62 % → white 38 % | 1 pt inner stroke (strokeBorder), vertical gradient: **dark** white 28 % → white 6 %; **light** white 95 % → black 6 % (a "light rim" on top) | 14, **continuous** (squircle) |

No shadows in any row style. Spacing between rows is set by each page (see tabs), and `rowPad` above is what the Live layout assumes per row.

**Why glass cards are fake glass**: a comment explains that real glass (blur) under numbers that change every 2 s made macOS rebuild the glass constantly (lots of GPU, especially in large windows). Real Liquid Glass is used **only on the tab bar** (the control surface that changes only on click). Windows port: never put AcrylicBrush behind live numbers; use the static gradient.

### 10.6 Liquid Glass on the tab bar, and `.solid`

- Tab bar container: horizontal padding 4; when theme rowStyle is `glass` also vertical padding 2 and `.glassEffect(.regular, in: .capsule)` (a real glass capsule behind all tab buttons). All other themes: no glass (`.identity`). Windows: a capsule-shaped `Border` with `AcrylicBrush` (TintOpacity ~0.2) or `SystemControlAcrylicElementBrush`, CornerRadius = height/2.
- `.solid` (applied when Low impact is on): **only changes Liquid Glass** — bg = system window background color (`windowBackgroundColor`; Windows: `SolidBackgroundFillColorBase`), card = primary @ 5 %, rowStyle → `cards` (so rows become 12-radius cards with a 1 pt `line` = primary@10 % border). The tab bar glass disappears because rowStyle is no longer `glass`. Other themes are already solid and unchanged.

### 10.7 Fonts (`font` pref, Settings › Look; default `style`)

| id | Title (UI) | Font used | Windows suggestion |
|---|---|---|---|
| `style` | "Matches style" (translated) | System font with the theme's design (rounded/mono/default) | per theme (see above) |
| `sfPro` | "SF Pro" | System, default design | Segoe UI Variable |
| `rounded` | "SF Rounded" | System, rounded | bundled rounded font (e.g. Nunito) |
| `mono` | "SF Mono" | System, monospaced | Cascadia Mono |
| `serif` | "New York" | System, serif | Georgia / Sitka |
| `montserrat` | "Montserrat" | Bundled `Resources/Fonts` (custom name "Montserrat") | bundle the same TTF |
| `ubuntu` | "Ubuntu" | Bundled `Resources/Fonts` (custom name "Ubuntu") | bundle the same TTF |

Font option titles other than "Matches style" are font names and are not translated. The weight passed is always the theme's number weight for numbers, `.medium` for labels; the font choice survives light/dark conversion. Bundled fonts: `Resources/Fonts` (copy them into the Windows package and load via `ms-appx:///Fonts/Montserrat.ttf#Montserrat`).

### 10.8 Text scale (`textScale`, default 1.0)

Multiplies every size passed to `theme.number()` and `theme.label()`; clamped 0.8–1.6. The "Simple" profile sets 1.15. (Settings shows the choice as cards in the setup wizard — "TextSizeCards" in Stappenplan.swift, documented elsewhere.) System-font texts that don't go through the theme (Settings forms) are not scaled.

### 10.9 Number change animation (`numberStyle`, default `fade`) — `ChangingText`

Picker options (segmented): "Roll", "Fade", "Instant". Tooltip: "Roll: digits flip from top to bottom · Fade: a short crossfade · Instant: no animation".

| Style | Behaviour |
|---|---|
| `roll` | Numeric content transition, digits counting **down** (new digits slide in from top to bottom), animation `.snappy(duration: 0.35)`. Each number waits its own `delay` before switching, so values on a page don't all change at once. |
| `fade` (default) | Old and new strings stacked right-aligned in the same spot; crossfade in **6 app-driven steps** on a shared 62.5 ms (1/16 s) clock grid (`stepTick`: sleep until the next multiple of 0.0625 s of media time). Opacity of new = smoothstep `p²(3−2p)` for p = 1/6…6/6; old = 1 − that. Total ≈ 0.375 s. All numbers fade simultaneously (delay ignored) so they share the same 6 redraws. The old text stays in layout (invisible) so layout never recalculates. |
| `instant` | Text just changes. |

Common rules:
- `ready = false` (no real sample yet at launch) → shows empty, never "0%".
- The **first** real value always arrives with a one-time **roll/flip** regardless of style (after `0.1 + delay/3` s), flip mode ends 0.6 s later.
- If the window is not visible or Low impact is on (`onScreen == false`) → instant, no animation.
- Reason (from comments): SwiftUI animations kept the app at display refresh rate (60–120 wake-ups/s); the 6-step approach measured ~20 wake-ups/s. Windows: use a `DispatcherQueueTimer` at 62.5 ms for 6 ticks, not a Storyboard per number; or a Composition opacity animation (runs off-thread, also acceptable).

### 10.10 Sparkline graphs (`Spark`, `SparkView`, `SparkNSView`)

Geometry (`Spark`):
- Values are drawn **right-aligned**: newest point on the right edge; the series occupies `width × fill` (fill is always 1 now — graphs always use the full width; the axis label states the real span).
- y = `maxY − height × clamp((v − bottom)/(top − bottom), 0, 1)`. `top` = given max (100 for %, GB total for RAM in GB, or auto = max(values, second series, 1)).
- Thinning: if count > width × 2, downsample (bucket average) to `max(Int(width), 2)` points, and don't glide.
- **Path shape**: straight segments with slightly rounded corners, roundness k = **0.2**: for each interior point, line to the point lerped 20 % toward the previous point, then a quadratic curve to the point lerped 20 % toward the next point with the vertex as control point. Not a spline — "rounded polyline". Closed variant (area) continues down to the bottom edge and back.

Layers (`SparkNSView`, three CAShapeLayers, clipped to bounds, flipped coords, click-through):

| Layer | Stroke | Fill |
|---|---|---|
| `area` | none | series color @ **15 %** of its alpha (flat fill, no gradient) |
| `line` | series color, **1.5 pt**, round cap, round join | none |
| `line2` (optional second series, e.g. upload vs download, read vs write) | series color @ **50 %**, **1 pt** | none |

Series color = theme accent, or white for Color blocks.

Axis labels (when `axis` given): top-left label "100%" (top = 100) / `short(scale)` for auto rate scales / "N GB" (rounded) for GB scales; bottom row: axis text (e.g. "10 min") on the left, "now" on the right; padding 2; font `label(9)` in `onRow(secondary)`.

Glide animation (new sample arrives):
- Kinds: `slide` (window full: oldest dropped, one appended) → line shifts left by one step, old point slides out on the left, new point enters from the right (up to 2 "lead" points kept off-screen left); `grow` (series still filling) → line stretches (left end fixed) instead of shifting; `none` → jump.
- Duration **1.9 s** (slightly shorter than the 2 s cadence so it rests briefly and never lags). Linear progress. If the next sample arrives early it continues from the current position (no jump back). Scale (`top`) interpolates along when it changes.
- Driven by **one shared timer** for all graphs, step interval = duration / (pixels to move / 0.5 px), clamped to **1/15 s … 0.5 s**; when scale changes, always 15 steps/s. A step only redraws if the line moved ≥ 0.5 device pixel. Comments: CAAnimation made the render server redraw at 75–120 Hz and cost up to a quarter of GPU in big windows. Windows: one `DispatcherQueueTimer`, update `PathGeometry`/Win2D, never `CompositionAnimation` per frame on large areas.
- Glide disabled when: Reduce Motion is on, window not on screen, Low impact on. Large graphs (detail view, large window) do **not** glide unless "Smooth large graphs" (`smoothBig`, default **true**) is on; they then step per sample. Settings text: "Smooth large graphs" / "Large graphs glide instead of stepping with each measurement. Costs a little more processor in a big window."

Reveal on appear (only within 1.2 s after app launch or tab switch, `LaunchOnce.intro`, never in Low impact):
- Clip rect grows from the right edge leftward (`Reveal` shape: x from `maxX − width × drawn`), `drawn` 0 → 1 with timing curve **cubic-bezier(0.22, 1, 0.36, 1)**, duration **0.8 s × pace**, delay **(0.05 + order × 0.25) s × pace** — graphs appear one by one. `pace` = 1 at launch, **0.4 after a tab switch** (≈2.5× faster). If there is no line yet (<2 samples) it waits and reveals as soon as the 2nd sample arrives.

### 10.11 Per-core bars (`CoreBars`)

- One rounded bar per core, corner radius 1; track = color @ 15 %, fill = color, scaled vertically from the bottom by load/100. Color = accent (white for Color blocks).
- Spacing 2 pt (3 pt in wide/detail mode). Bar width 4 pt fixed (card header) or flexible equal widths filling the row (detail, `wide`).
- Efficiency cores come first (count from `hw.perflevel1.logicalcpu`); a gap is inserted before the first performance core: extra leading padding 4 pt (8 pt in wide mode). Windows: use `GetLogicalProcessorInformationEx` EfficiencyClass to order E-cores first.
- Animates toward new values in the same 6-step smoothstep on the 62.5 ms grid; instant when not on screen.

### 10.12 Row entrance (`IntroIn`)

Rows on a page slide in on launch/tab switch: opacity 0 → 1 and y offset +14 → 0, spring(duration **0.35 × pace**, bounce **0.2**), delay **(0.03 + index × 0.04) × pace**. Only within 1.2 s after launch/tab switch and never in Low impact.

### 10.13 Window-level animations that depend on the theme pipeline (reference; full window spec in section 2)

| What | Animation |
|---|---|
| Tab change, page content | new page `opacity` + `offset(y: 10)` transition, `easeOut 0.22 s` |
| Tab "drop" (selected-tab pill, accent @ 15 %, radius 8 continuous) | moves with `spring(response 0.38, damping 0.7)` via matched geometry; simultaneously stretches `x 1.35 / y 0.65` (easeOut 0.12 s) anchored at the side it leaves, then springs back to 1 (`spring(response 0.3, damping 0.55)`) after 0.12 s |
| Tab hover | `secondary` @ 12 % pill, icon/text → `text` color; selected icon/text = accent; inactive = secondary |
| Launch | window alpha 0 → 1, 0.18 s ease-out |
| Window resize on tab switch | 0.2 s ease-in-ease-out frame animation |
| Low impact | all animations in the window disabled via a transaction override |

Environment value `onScreen` = window visible (occlusion state) **and** not Low impact. Every animation checks it.

### 10.14 Icons in this area (SF Symbol → Segoe Fluent Icons)

| Where | SF Symbol | Segoe Fluent Icons (glyph) |
|---|---|---|
| Low impact leaf (Settings label, gear overlay `leaf.fill` size 8 in `good` color, padding 3, top-trailing) | `leaf` / `leaf.fill` | no leaf glyph in Segoe Fluent Icons; use a bundled leaf `PathIcon` (or `EnergySaver`-style battery-leaf `\uEBB4`-family glyph if acceptable) |
| Settings tab button | `gearshape` | `Settings` `\uE713` |
| Live tab | `gauge.with.needle` | `Speed`/"SpeedHigh" `\uEC4A` |
| Processes tab | `square.grid.2x2` | `ViewAll` `\uE8A9` / `AllApps` `\uE71D` |
| Checkup tab | `chart.bar.xaxis` | `BarChart`-like `\uE9D2` ("Diagnostic") or `\uEAFC` |
| Network tab | `network` | `Network` `\uE968` / `Globe` `\uE774` |
| Docked tab | `dock.rectangle` | `Devices` `\uE772` / `USB` `\uE88E` |
| Preset Simple / Gamer / Nerd / Laptop | `leaf`, `gamecontroller`, `terminal`, `laptopcomputer` | custom leaf, `Game` `\uE7FC`, `CommandPrompt` `\uE756`, `Laptop`/"Devices2" `\uE7F8` |

### 10.x Low impact mode

Pref `lowImpact`: `"off"`, `"on"`, `"auto"` (**default `auto`**). Settings › Look (Section "Low impact", segmented picker "Off" / "On" / "Automatic"). Label "Low impact" with a `leaf` icon; when currently active the label reads "Low impact · on now" and the leaf is green. Caption: "Fusebox measures every 5 seconds instead of 2, without animations or glass effects. With the window open that is about 0.3% of a processor core instead of 2%. Automatic: on in Low Power Mode, or on battery below 20%."

**Auto rule**: on when macOS Low Power Mode is enabled, **or** (not charging **and** battery < 20 %). Desktop (no battery) + no low-power = off. At exactly 20 % → off. Re-evaluated on every sample, on power-state change notification and on any prefs change. Windows: `PowerManager.EnergySaverStatus == On` or (`BatteryStatus != Charging` and `RemainingChargePercent < 20`).

What changes when on:

| Area | Normal | Low impact |
|---|---|---|
| Sample interval (`LowImpact.interval`) | 2 s | **5 s** |
| CPU/GPU temperature + battery temp (SMC) read | every 10 s | every 30 s |
| Ping | every 10 s if someone looks, 30 s otherwise | every **60 s** |
| Processes list refresh (when that page is visible) | every sample (2 s) | every 10 s (≥ 9.5 s) |
| Per-app network (`netPerApp`) | if enabled | disabled (no nettop) |
| Updater background check | normal | skipped if last check < 55 min ago |
| Animations (tab switch, number roll/fade, graph glide, intro rows, reveal, core bars) | on | **all off** (window transaction disables animations; `onScreen` = false) |
| Liquid Glass | glass tab bar + gradient cards | `.solid`: plain window background, 5 % cards, no glass tab bar |
| Gear icon | normal | small green `leaf.fill` badge; tooltip "Settings  ⌘,  ·  Low impact is on" (else "Settings  ⌘,") |
| Peak tracker step | 2 s | 5 s |

A change posts a `fusebox.lowImpact` notification; the window re-renders, and the measuring timer is restarted at the new interval (timer tolerance 0.5 s so the OS can coalesce wake-ups).

---

## 11.1 Sampling cadence and history

### Cadence (`Meter`)

| What | When |
|---|---|
| First sample (zero point for deltas) | immediately at start on the background "measure" queue |
| First visible values | after **0.6 s** |
| Regular tick | every **2 s** (Low impact **5 s**), main-thread timer, tolerance 0.5 s; actual measuring on a serial background queue (QoS utility) so animations never stutter |
| Temperatures (SMC) | every 10 s (Low impact 30 s) |
| Fans | every tick (only if Mac has fans) |
| Disk free/total | once per **60 s** (expensive call, ~10 ms) |
| Ping | 10 s when watched / 30 s unwatched / 60 s Low impact; `pingHost` "" = off |
| Per-minute tasks (apps CSV, alerts, peak alerts, self log, battery-health, SSD health ≤ 1/h, learned battery ≤ 1/30 min) | when ≥ 59 s since last minute task |
| CSV flush | buffer flushed every ≥ 60 s and on quit |

"Watched" = within the first 5 ticks, or (screen not asleep and any app window is visible and not fully occluded). When nobody watches, Fusebox still measures, logs and alerts, but does **not** notify the UI (no redraw). On screen wake / window becoming visible it pushes the latest values immediately.

### History in memory

| Buffer | Content | Limit |
|---|---|---|
| `recent` | every sample | last **3600 s** by time, hard cap **1800** samples |
| `today` | per-minute averages of today (reloaded from today's CSV at startup, averaged per minute) | cleared at day change |
| `minute` | samples of the current minute (folded into `today` when the minute changes) | — |

`since(seconds)` selects by time with half a step margin (so the point count doesn't flicker → graphs glide instead of jump).

### Time ranges (`TimeRange`)

| id | Title | Data |
|---|---|---|
| `5m` | "N min" where N = `shortMinutes` (pref, default **5**, clamped 1–60) | `since(N × 60)` |
| `1h` | "1 hour" | `recent` |
| `today` | "Today" | `today` + current partial minute average |

Minute average (`Sample.average`): averages cpu, ram, gpu, ramUsedGB, down, up, diskRead, diskWrite; optional averages (ignoring missing) for temp, gpuTemp, pingMs, fanRPM; pressure = **max**; all other fields from the last sample; time = first sample's time.

### Overall status sentence (`Meter.status`, used in menus/headers)

Evaluated in this order (first match wins); cpu = average of the last 30 s:

| Condition | Text | Color |
|---|---|---|
| memory pressure ≥ 4 (critical) | "Memory is full — close a few apps" | red |
| temp ≥ warnTemp + 10 | "Your Mac is very hot — it's slowing itself down" | red |
| cpu ≥ warnCPU (default 85) | "Your Mac is busy" | orange |
| pressure == 2 | "Memory is filling up" | orange |
| temp ≥ warnTemp (default 95) | "Your Mac is getting warm" | orange |
| else | "All calm" | green |

### Data sources per metric (macOS → Windows)

| Field | Unit | macOS source | Windows equivalent |
|---|---|---|---|
| cpu | % whole machine | `host_statistics(HOST_CPU_LOAD_INFO)` tick deltas: (user+system+nice)/(all) | PDH `\Processor Information(_Total)\% Processor Utility` or `GetSystemTimes` deltas |
| cores | % per logical core | `host_processor_info(PROCESSOR_CPU_LOAD_INFO)` deltas (wrap-safe) | PDH `\Processor Information(*)\% Processor Utility` |
| E-core count | — | `sysctl hw.perflevel1.logicalcpu` | `GetLogicalProcessorInformationEx` EfficiencyClass |
| ram, ramUsedGB | %, GB (2^30) | `vm_statistics64`: (internal − purgeable) + wired + compressor pages × page size (= Activity Monitor "Memory Used"); % of physical RAM | `GlobalMemoryStatusEx` (TotalPhys − AvailPhys) or PDH `\Memory\Committed Bytes`; "In use" like Task Manager |
| pressure | 1 normal / 2 high / 4 critical | `sysctl kern.memorystatus_vm_pressure_level` | derive: available < 10 % → 2, < 5 % or heavy paging (`\Memory\Pages/sec`) → 4 |
| gpu | % | IORegistry `IOAccelerator` → `PerformanceStatistics["Device Utilization %"]`, max over GPUs | PDH `\GPU Engine(*engtype_3D)\Utilization Percentage` summed per adapter, max |
| temp | °C hottest CPU core | SMC keys `Tp*`/`Te*` of type `flt `, max within 10–130 | LibreHardwareMonitorLib (CPU Package/Core Max); WMI `MSAcpi_ThermalZoneTemperature` fallback |
| gpuTemp | °C | SMC `Tg*` | LibreHardwareMonitor GPU Core / NVAPI / ADL |
| batteryTemp | °C | SMC `TB0T`/`TB1T`/`TB2T` max within −20–120 | WMI `BatteryStatus` (rare) / LHM |
| fans | rpm, min, max, target, forced | SMC `FNum`, `F{i}Ac/Mn/Mx/Tg/Md` | LibreHardwareMonitor fan sensors (often unavailable on laptops) |
| down/up | bytes/s | `getifaddrs` AF_LINK `if_data` ibytes/obytes for interfaces named `en*`, 32-bit wrap handled; per-interface delta > 1.25e9 B/s (10 Gbit/s) treated as counter restart → ignored | `GetIfTable2` (`MIB_IF_ROW2.InOctets/OutOctets`, 64-bit) for physical adapters |
| diskFreeGB / diskTotalGB | GB (10^9) | `/` `volumeAvailableCapacityForImportantUsage`, `volumeTotalCapacity` | `GetDiskFreeSpaceEx("C:\")` |
| diskRead/diskWrite | bytes/s | IORegistry `IOBlockStorageDriver` → `Statistics` "Bytes (Read)/(Write)" summed; if sum decreases (disk ejected) → 0 | PDH `\PhysicalDisk(_Total)\Disk Read Bytes/sec`, `Disk Write Bytes/sec` |
| swapGB | GB | `sysctl vm.swapusage` xsu_used | PDH `\Paging File(_Total)\% Usage` × size |
| battery, charging, minutes, onAC, adapterW | %, bool, min, bool, W | IOPowerSources (`CurrentCapacity/MaxCapacity`, IsCharging, TimeToFull/TimeToEmpty >0, PowerSourceState == AC, adapter Watts) | `Windows.Devices.Power.Battery.AggregateBattery.GetReport()`, `GetSystemPowerStatus`, `PowerManager` |
| batteryW | W, + charge / − discharge | AppleSmartBattery `Amperage` (signed: UInt64 bit pattern → Int64) × `Voltage` / 1e6; **if amperage == 0 → no value** (macOS sometimes reports 0 for a while; show nothing rather than "0 W") | `BatteryReport.ChargeRateInMilliwatts` (sign = direction), null when 0 |
| pingMs | ms | TCP connect time to `pingHost` (default `1.1.1.1`) **port 443** via NWConnection (no root); timeout 3 s → nil ("–"); `pingPending` until first answer (show nothing, not red "–") | `Socket.ConnectAsync` timing to port 443; timeout 3 s |
| wifiDbm | dBm; 0 = dropped, nil = no Wi-Fi | `WifiWatch` (Metrics.swift) | `WlanQueryInterface` RSSI / `WiFiAdapter.NetworkReport` |
| batteryMinutes fallback when charging | min to full | own estimate: rise since a sample 5–15 min ago; needs > 0.05 %/min; `(100 − b)/perMin` | same |
| batteryMinutes on battery (`learnedBattery`, default on) | min to empty | LearnedBattery rate from own history blended with last 15 min (Accu.swift) | same |

### CSV logging (always on)

Location: `~/Documents/Fusebox/` (old `~/Documents/Meterkast` folder is moved once). Windows: `%USERPROFILE%\Documents\Fusebox\`. Dates in filenames `yyyy-MM-dd` (Gregorian, POSIX). Timestamps `yyyy-MM-dd HH:mm:ss` local time, en_US_POSIX. Decimal separator always `.`. Header row column names are **Dutch** (keep them identical for compatibility with Checkup analysis and existing logs).

| File | Written | Purpose |
|---|---|---|
| `meterkast-YYYY-MM-DD.csv` | every sample (buffered, flushed ≥ 60 s) | main metrics |
| `apps-YYYY-MM-DD.csv` | per minute | top apps |
| `peaks-YYYY-MM-DD.csv` | when a peak ends (Pieken.swift) | peaks |
| `self-YYYY-MM-DD.csv` | per minute | Fusebox's own usage (11.5) |
| `battery-health.csv` | once per day | `datum,gezondheid_pct,cycli` (date, health %, cycle count) |

**Main CSV columns** (`meterkast-*.csv`, in order):

| # | Column | Meaning | Format |
|---|---|---|---|
| 1 | `tijd` | timestamp | `yyyy-MM-dd HH:mm:ss` |
| 2 | `cpu_pct` | CPU % | 1 decimal |
| 3 | `ram_pct` | RAM used % | 1 dec |
| 4 | `ram_gb` | RAM used GB | 1 dec |
| 5 | `geheugendruk` | memory pressure 1/2/4 | int |
| 6 | `gpu_pct` | GPU % | 1 dec |
| 7 | `temp_c` | hottest CPU °C | 1 dec or empty |
| 8 | `gpu_temp_c` | GPU °C | 1 dec or empty |
| 9 | `down_Bps` | download bytes/s | int |
| 10 | `up_Bps` | upload bytes/s | int |
| 11 | `ping_ms` | ping ms | 1 dec or empty |
| 12 | `schijf_vrij_gb` | disk free GB | 1 dec |
| 13 | `batterij_pct` | battery % | 1 dec or empty (desktop) |
| 14 | `schijf_totaal_gb` | disk total GB | 1 dec |
| 15 | `lezen_Bps` | disk read bytes/s | int |
| 16 | `schrijven_Bps` | disk write bytes/s | int |
| 17 | `swap_gb` | swap GB | 2 dec |
| 18 | `ventilator_pct` | fans average % of their max; empty without fans or when the helper forces them | 1 dec or empty |
| 19 | `wifi_dbm` | Wi-Fi dBm | int or empty |

Robustness rules: non-finite doubles → `0`; integer columns clamped to 0…1e15 (never crash on absurd values); writes use an error-returning append (full disk must not crash); if a flush fails the buffer is kept, and capped (if > 2 MB keep last 1 MB). On startup today's file (and today's apps file) is **migrated** if its header differs: columns remapped by name, missing ones empty (no data loss); files without a recognizable header are left alone. Reading tolerates duplicate column names (e.g. edited in Excel).

**Apps CSV columns** (`apps-*.csv`): `tijd,app,soort,cpu_pct,geheugen_gb,energie_w,gpu_pct,mac_energie_w`
- `app` = display name with commas replaced by spaces; `soort` = `app` / `achtergrond` (background) / `macos`; cpu 1 dec; memory GB 2 dec; energy W 2 dec; gpu 1 dec; `mac_energie_w` = sum of energy of all processes that minute (2 dec).
- Rows written: union of top 10 by CPU and top 10 by memory, **plus every regular open app** (so "how long open" on the app page is correct).

**Peaks CSV**: `tijd,eind,soort,hoogte,app,app_waarde` (start, end, kind, height, culprit app, app's value) — details in the Live/peaks section.

**Retention** (`keepDays`, default **30**; Settings › Data): at startup (background) delete `meterkast-`, `apps-`, `peaks-`, `self-` CSVs whose filename date is older than start-of-today − keepDays. `keepDays` 0 = keep forever (no deletion at startup). `deleteLogs(nil)` = wipe all (the "delete all" button in Settings, with confirmation). `battery-health.csv` is never auto-deleted. `keepDays` is a local-only setting (never imported from a shared settings file — so nobody can make you wipe history).

---

## 11.2 Alerts / notifications

Master switch `notify` (default **true**), Settings › Notifications: Toggle "Show notifications" (turning it on requests OS permission). Stepper "Same kind at most once every N min" (`alertCooldown`, default **30**, range 5–240, step 5). Test row: label "Test", caption "No notification? Allow Fusebox in System Settings › Notifications.", button "Send test notification" → title "Fusebox update available (test)", body "This is how Fusebox tells you about a new version.". Section "Per kind: limit and how many minutes in a row" with one row per alert + the Sustained peak row. Footer: "If Focus or Do Not Disturb is on, macOS keeps these notifications quiet too." + link "Open Focus settings". When `notify` is off the per-kind section is disabled.

Pref keys: `alert.<id>.on`, `alert.<id>.limit`, `alert.<id>.min`.

| id | Title (also notification title) | Default on | Default limit | Default min in a row | Limit range / step | Limit described as | Condition (evaluated per minute on the last 60 s of samples) | Notification body |
|---|---|---|---|---|---|---|---|---|
| `cpu` | "Processor busy" | on | 90 | 5 | 20–100 / 5 | "above N%" | avg CPU ≥ limit | "Your Mac has been at N% processor for M min" + optional " — mostly because of APP" (top non-macOS app) |
| `heat` | "Mac too hot" | on | 100 | 5 | 70–110 / 5 | "above N °C" | avg CPU temp ≥ limit | "Your Mac has been at N °C for M min and is slowing itself down" |
| `mem` | "Memory full" | on | 4 | 2 | 2–4 / 2 | limit ≥ 4: "pressure critical", else "pressure high" | latest pressure ≥ limit | "Your memory is full" + optional " — APP is using X.X GB" |
| `disk` | "Storage almost full" | on | 10 | 1 | 1–200 / 1 | "less than N GB free" | 0 < free GB < limit | "Only N GB free on your SSD" |
| `net` | "Internet slow or down" | **off** | 300 | 3 | 50–1000 / 50 | "above N ms or offline" | only if ping enabled; no ping answers in the minute → offline; else avg ping ≥ limit | "No internet for M min" / "Internet has been slow for M min (N ms)" |
| `battery` | "Battery low" | **off** | 20 | 1 | 5–50 / 5 | "N% or less" | battery present, not charging, % ≤ limit | "Battery at N% — plug in the charger" |
| `app` | "One app hogging" | **off** | 50 | 10 | 20–100 / 5 | "above N%" | top non-macOS app CPU ≥ limit | "APP has been using N% processor for M min" |

(The "Laptop" profile turns `alert.battery.on` on.)

Mechanics:
- Runs once per minute. Requires `notify` on and at least **40 s** of samples in the last minute (count × interval ≥ 40), else all streaks reset.
- Per kind a **streak** counts consecutive minutes over the limit; any minute not over → streak 0. Fires when streak ≥ `min` (minimum 1) **and** time since that kind's last alert ≥ cooldown.
- Cooldown = `alertCooldown` minutes for all kinds, except **disk = 24 h** fixed.
- Settings row per kind: Toggle (title + caption "<limit description>, N min in a row"); when on (not compact) an indented (20 pt) row with Stepper "Limit: <description>" and Stepper "N min" (1–60), font callout, and a **NotificationExample** mock below.
- NotificationExample appearance: HStack spacing 8, app icon 20×20, title `caption` semibold, body `caption` secondary (wraps), padding 8, background `quaternary` @ 50 %, corner radius 10. Example body = real `check()` text with a value just over the limit (e.g. cpu limit+2); for "One app" the fallback "Safari has been using N% processor for M min" (N = min(limit+5, 100)).
- Sustained peak row (documented with Peaks/PeakAlert): Toggle "Sustained peak", caption "Names the app behind a peak of N min or more, with a Stop app button"; keys `alert.peak.on` (default **true**), `alert.peak.min` (default **2**, stepper 1–30); uses the same `alertCooldown` per app; notification has actions "Stop app"/"Show" (category depends on whether the app can be stopped). See the Live/Peaks section for texts and timing.

Notification plumbing (`Notify`):
- `send(title, body, category?, userInfo)` → immediate local notification, random id. Shown as banner **with sound** even when Fusebox is frontmost. Windows: `AppNotificationBuilder` (Windows App SDK) with `AppNotificationManager`.
- Permission: requested when `notify` is on at launch; plus once per "ask round" (`askRound = 1`, pref `notifyAskRound`), 2 s after launch: if never decided → ask (and set `notify` true); if denied → app alert "Allow notifications from Fusebox?" / "Fusebox tells you when an update is ready and when an app keeps your Mac busy. Notifications for Fusebox are off in System Settings." buttons "Open Settings" (opens Notification settings for this app, sets notify true) / "Not now".
- Categories: `whatsnew` with action "What's new" (foreground) → opens Settings › About (sets `settingsTab = "about"`) showing the changelog; clicking the notification itself does the same. Plus the PeakAlert categories.

---

## 11.3 Hotkey

- Prefs: `hotkeyCode` (virtual key code, default **3** = F; **−1 = off**), `hotkeyMods` (Carbon modifier mask, default **6400** = ⌃ 4096 + ⌥ 2048 + ⌘ 256), `hotkeyLabel` (display, default **"⌃⌥⌘F"**).
- Global, registered with Carbon `RegisterEventHotKey` (works in every app, no accessibility permission). Re-registered at launch and after each change.
- Behaviour (`toggle`): if Fusebox is the active app and has a visible key-capable window → **hide the app** (keeps measuring). Otherwise unhide, open the main window (also if it had been closed) and activate it.
- UI (Settings, `HotkeyRecorder`): LabeledContent with title "Shortcut to show or hide Fusebox", caption "Works from any app." (while recording: "Esc cancels, ⌫ turns it off. Use ⌘, ⌥ or ⌃."). Button text = label (monospaced font), "Off" when code < 0, "Press keys…" while recording; click toggles recording. While recording: Esc cancels; Delete/Forward-Delete sets off; any key **without** at least one of ⌘/⌥/⌃ → system beep and keep recording; otherwise store code, mods (⇧ allowed in addition), label in fixed order ⌃⌥⇧⌘ + uppercased key; key is swallowed. Recording stops when the view disappears.
- Windows: `RegisterHotKey` with default **Ctrl+Alt+Win+F**? — not recommended (Win-combos are often reserved). Suggest **Ctrl+Alt+F** (MOD_CONTROL|MOD_ALT|MOD_NOREPEAT) displayed "Ctrl+Alt+F"; require at least Ctrl, Alt or Win; Shift optional; label order Ctrl+Alt+Shift+Win+Key. Toggle = if foreground and visible → minimize/hide to tray; else show + `SetForegroundWindow`.

## 11.4 Languages

- Source language English; the English text **is** the key (`Resources/en.lproj/Localizable.strings` is intentionally empty). Translations: `nl`, `de`, `fr`, `es` (`Resources/<lang>.lproj/Localizable.strings`). Glossary `docs/translation-glossary.md` uses the OS vendor's own wording; forms of address: nl "je", de "du", fr "vous", es "tú"; path separator " › ".
- Picker "Language" (Settings › Look, pref `appLanguage`, default `""`): "System" (translated) + "English", "Nederlands", "Deutsch", "Français", "Español" — each language shown in **its own name, never translated**, so you can always find your language. Choosing sets the app's language override (`AppleLanguages`; System removes it). Then a row appears: caption "Applies after a restart of Fusebox." + button "Restart Fusebox" (waits for the process to exit, then reopens the app). Windows: `ApplicationLanguages.PrimaryLanguageOverride` + `AppInstance.Restart`.
- Never translated: product name Fusebox, tab names **Live, Checkup, Docked** (Processes / Network / Settings are translated), theme names (Instrument, Terminal, Minimal light, Glass, Color blocks, Liquid Glass — per the product rule), font names, language names.
- Number formatting in `L()` uses POSIX locale (no thousands grouping; "1.5" with a dot via `one()`); byte units always decimal with a dot: `< 1e6` → "N KB" (integer), `< 1e9` → "X.X MB", else "X.X GB"; rates append "/s". `pct(v)` = rounded integer + "%". `duration(min)`: ≥ 1440 → "Nd Nh", ≥ 60 → "Nh Nm", else "Nm". Day/month names on screen use the UI language locale (e.g. "do 16:31" in Dutch); files/logs always en_US_POSIX.

## 11.5 Fusebox's own usage (SelfLog, beta)

- Every minute one line in `Documents/Fusebox/self-YYYY-MM-DD.csv` (first call only sets a zero point; needs > 5 s since the previous mark). Cost < 5 ms/min.
- Columns: `tijd,versie,cpu_pct,geheugen_mb,energie_w,gpu_pct,ws_gpu_pct,wakeups_s,pagina,venster,zichtbaar,thema,lettertype,low_impact,stroom` = time, app version, own CPU % (of one core, 2 dec), physical footprint MB (0 dec), energy W (3 dec, from `ri_energy_nj`), own GPU % (2 dec), WindowServer GPU % (2 dec, compositor context), wake-ups/s (interrupt + package idle, 1 dec), current page id, main window size `WxH` or `-`, visible 1/0, theme id, font id, lowImpact pref, power `ac`/`charging`/`battery`.
- macOS source: `proc_pid_rusage(RUSAGE_INFO_V6)`, GPU times per pid from the GPU driver. Windows: `GetProcessTimes`, `PROCESS_MEMORY_COUNTERS_EX.PrivateUsage`, PDH `\GPU Engine(pid_*)`, DWM (`dwm.exe`) GPU for compositor context; energy via `PROCESS_POWER_THROTTLING`/E3 is unavailable — leave empty.
- "Share performance report" (button in Settings, see Settings section): builds `~/Downloads/Fusebox-performance-YYYY-MM-DD.txt` from the last **7 days**: header "Fusebox performance report", line "Version V (B) · <model> · macOS x.y.z · N cores · N GB · N display(s)", "Last N day(s), N minutes measured. Fusebox's own use; ws_gpu = WindowServer GPU (whole screen, for context).", then a fixed-width table grouped by situation `visible|page|size|theme|low:<mode>` (size bucket by area: < 250 000 px² "small", < 700 000 "medium", else "large"), sorted by minutes, columns minutes, cpu avg, cpu p95, mem MB, gpu %, ws_gpu %, watt, wakeups/s. Appended: a 5 s `/usr/bin/sample` profile of Fusebox itself condensed to "Sort by top of stack" (30 lines) + first 120 lines of "Call graph:" (each ≤ 160 chars), with the home path replaced by `~`. The text is also copied to the clipboard and revealed in Finder. **Nothing is uploaded automatically.** Windows: use ETW/`dotnet-trace`-style sampling or skip the profile part.

## 11.6 Privacy rules found in these files

- Stored preference values are short fixed ids (safe to share), never user-visible text.
- Settings export (`Fusebox-settings.json`) contains only known keys. Import is a **trust boundary**: only known keys, same type as the default (Bool must be a real boolean, Int must be integral, Double finite), strings ≤ 200 chars, file < 100 000 bytes. Local-only keys are never imported: `pingHost`, `setupDone`, `page`, `settingsTab`, `fanMode`, `fanCurve`, `chargeLimitOn`, `chargeLimit`, `chargeHeat`, `keepDays`, `autoUpdate` (a shared file must not be able to wipe your history, disable updates or redirect your ping). Profiles never touch `keepDays` or `pingHost`.
- Ping goes only to the user-chosen host (default `1.1.1.1`, TCP 443); empty host = no network traffic at all for ping.
- Logs stay local in Documents/Fusebox; the performance report is created locally and only shared by the user; home-directory paths are replaced with `~` in the report and in Settings' folder display.
- Per-app network measurement (`netPerApp`, default **off**) runs `nettop` at most once per minute and only when someone is looking; disabled in Low impact.
- (MAC-address / serial-number rules live in the Network and Docked code — see those sections.)

---

## 11.7 First-run setup ("Set up Fusebox" window)

Source: `Stappenplan.swift` (`SetupView`).

### When it appears
- Window id `setup`, title `Set up Fusebox`, size **fixed 560 × 480 pt** (`windowResizability(.contentSize)`), follows the Fusebox Light/Dark mode.
- Shown at launch while `setupDone == false`; can be reopened from Settings › Profile › `Run setup again` (and the menu bar).
- **Every choice is written immediately**, so the main window and menu bar change live behind it.
- On open: a snapshot of all settings is taken (`Prefs.export()`), step resets to 0.
- Closing with the window's close button = **cancel**: the snapshot is restored (all changes undone) but `setupDone` is still set to true (so it isn't asked at every launch). Exception: when the Installer is handing over to another copy, or the app is quitting, closing does nothing (setup stays not-done so the new copy shows it).
- `Skip setup` and `Start using Fusebox` both **keep** the choices (finish: request notification permission if `notify` is on, set `setupDone`, close).

### Layout

```
┌─ Set up Fusebox ───────────────────────────────── 560 × 480 ─┐
│  Step 3 of 12   ▓▓▓▓▓▓░░░░░░░░░░░░░░░░░ (small progress bar)  │  ← padding 20 (h, top)
│  [icon48]  How big should the text be?        (title2 semibold)│
│            Pick what reads comfortably from where you sit.     │  ← callout, secondary
│ ┌──────────────────────────────────────────────────────────┐ │
│ │  grouped Form (scrolls) — the step's content               │ │
│ │  ┌────────┐ ┌────────┐ ┌────────┐                         │ │
│ │  │   Aa   │ │   Aa   │ │   Aa   │                         │ │
│ │  │ Normal │ │ Large  │ │Extra lg│                         │ │
│ │  └────────┘ └────────┘ └────────┘                         │ │
│ └──────────────────────────────────────────────────────────┘ │
│  [Skip setup]                              [Back]  [ Next ]   │  ← large controls, padding 20 h/bottom, 8 top
└───────────────────────────────────────────────────────────────┘
```

- Header: VStack spacing 6 → HStack: `Step <n> of 12` (`.caption`, secondary) + small `ProgressView` (determinate, value n/12). Below (6 pt top padding) HStack spacing 12: app icon **48×48 only on step 1**; VStack spacing 4 → question (`.title2` semibold ≈ 17 pt) and help (`.callout` secondary, wraps). Header padding **20** horizontal + top.
- Body: grouped `Form` (same look as Settings).
- Footer HStack, `.controlSize(.large)`, padding 20 h / 20 bottom / 8 top: `Skip setup` (left), spacer, `Back` (from step 2 on), primary default button (Return key) `Next` or on the last step `Start using Fusebox`.
- No animated transitions between steps (content swaps).

### Steps (12)

| # | Question (title) | Help text | Content |
|---|---|---|---|
| 1 | `Welcome to Fusebox` | `Fusebox shows how busy your Mac is: processor, memory, storage, internet and battery.` ⏎ `In a small window, in your menu bar, or both.` | `Label("This takes about a minute.", systemImage: "clock")` (Segoe Clock `E823`); link-style button `Import settings from a friend…` (opens JSON import; on success shows the result message and **jumps to the last step**; on failure shows `Fusebox can't read this file.`) |
| 2 | `Pick a style` | `This is how the Fusebox window looks. You can switch any time.` | ThemeTiles |
| 3 | `How big should the text be?` | `Pick what reads comfortably from where you sit.` | TextSizeCards; then AccentPicker with footer `Optional: your own accent color. "Default" uses the style's color.` |
| 4 | `What do you want to see?` | `Turn measurements on or off. Drag them to change the order.` | MetricPicker: like MetricList but each toggle has an explanation subtitle; row height 40 (list height = count × 40 + 12) |
| 5 | `Percent or real values?` | `Percent is quick to read. Real values tell you exactly how much.` | UnitPicker × CPU/RAM/SSD; footer `Tip: you can also click a number on the Live page to switch.` |
| 6 | `Show graphs when you make the window bigger?` | `Graphs show the last few minutes. They only appear when there's room.` | GraphCards |
| 7 | `What should the menu bar show?` | `Something small next to the clock, so you can check at a glance.` | MenubarStyleCards; if style is not off/icon also section `Which measurements? (max. 4)` with MetricList("menubarMetrics", limit 4) |
| 8 | `How should Fusebox start?` | `Fusebox uses very little power, so it's fine to keep it running.` | LoginToggle (`Open at login`), DockToggle (`Icon in the Dock`); footer `Without the Dock icon, you'll find Fusebox in the menu bar.` |
| 9 | `Should Fusebox warn you?` | `A notification when something needs attention, like your Mac getting too hot.` | toggle `Show notifications`; section with 7 compact AlertRows (toggle + subtitle only, no steppers/examples), footer `You can change the limits later in Settings › Notifications.`; disabled when notifications off |
| 10 | `Data and privacy` | `Everything stays on your Mac.` | segmented `Keep measurement history for`: `7 days` · `30 days` · `90 days` · `Forever`; toggle `Measure internet response time` / `Every 10 seconds Fusebox makes one tiny connection to 1.1.1.1 to time it. Nothing about you is sent. Off means no internet connections at all.` |
| 11 | `Permissions` | `macOS asks you once for each of these. Allow what you want to use; you can change it later in System Settings.` | PermissionsStep (below) |
| 12 | `You're all set!` | `Here's what you picked.` | Summary (below) |

MetricPicker subtitles: CPU `How hard your Mac is working, and how warm it is`; GPU `How hard the graphics chip is working`; RAM `How full the working memory is`; NET `Download and upload speed`; SSD `How full your storage is`; I/O `How much is being read from and written to storage`; Battery `Charge and time left`; Fan `How fast the fans spin`.

### ThemeTiles / ThemePreview
- `LazyVGrid` **3 columns** (spacing 10), 4 pt vertical padding; 6 Cards in order **Instrument, Terminal, Minimal light, Glass, Color blocks, Liquid Glass** (names: `Instrument`, `Terminal`, `Minimal light`, `Glass`, `Color blocks`, `Liquid Glass` — Liquid Glass never translated; the others are localized style names but the brand-like ones should stay as-is). Selected = accent 2.5 pt border.
- Card content: VStack spacing 6 → ThemePreview + theme name (`.callout`).
- ThemePreview: a fixed mini Live window in that theme (adapted to the current Light/Dark mode): two rows — `CPU` / side `47°` / value `17%`; `RAM` / value `11.8` / unit `GB`. Row font sizes: label `label(9)`, side & unit `number(9)`, value `number(14)`; HStack first-baseline spacing 3, spacer min 2, 1 line; label color = accent for Instrument, else secondary-on-row; each row `.themedRow(theme, index: i)` (so Color blocks shows two differently colored blocks). Rows spacing 0 (lines/plain) or 4. Padding 8, min height **86**, full width, theme bg (thin material for Liquid Glass), **radius 8**.

### TextSizeCards
- 3-column grid of Cards: `Normal` (1.0), `Large` (1.15), `Extra large` (1.3). Each: `Aa` in system rounded semibold at **22 × scale** pt in a 36 pt frame + name `.callout`.
- Below: a sample row in the current theme: `CPU` (`label(13)`), `47°` (`number(13)` secondary), spacer, `17%` (`number(30)`); themedRow, padding 10, theme bg, radius 10. Re-renders on accent/font change.

### FontCards (Settings › Appearance only)
- 4-column grid of Cards for: `Matches style`, `SF Pro`, `SF Rounded`, `SF Mono`, `New York`, `Montserrat`, `Ubuntu`.
- Each: `17%` in that font at 22 pt with the theme's weight/design, monospaced digits (30 pt frame); `CPU 47°` at 11 pt medium secondary; font name `.caption` (2 pt top padding).
- Windows mapping: SF Pro → Segoe UI Variable; SF Rounded → (no system rounded; bundle e.g. "Nunito"/use Segoe UI Variable Display semibold); SF Mono → Cascadia Mono; New York → Georgia / Sitka; Montserrat and Ubuntu are bundled fonts (ship the same TTFs).

### PermissionsStep
Section of 4 rows; each row HStack spacing 10: SF Symbol (22 pt wide, secondary) · VStack(title, caption help) · spacer · state label (green `checkmark.circle.fill` + text, `.callout`) when granted · action button when not granted (or `Open Settings` when it still makes sense). Refreshes every **2 s** while the step is open.

| Icon | Title | Help | State text | Button / action | Windows equivalent |
|---|---|---|---|---|---|
| `bell.badge` | Notifications | `Updates, peaks, heat and battery warnings` | `Allowed` / `Off` (denied) | `Allow` → OS permission request (sets notify on); when denied `Open Settings` → notification settings for Fusebox | toasts work without a prompt; link `ms-settings:notifications` if disabled |
| `network` | Local network | `To find the devices on your network (Network tab)` | `Asked` (after first click; `lanAsked`) | `Allow` → sends one tiny UDP packet to the mDNS group 224.0.0.251:5353 to trigger the macOS prompt; afterwards `Open Settings` → Privacy › Local Network | Windows Defender Firewall prompt for inbound/discovery; usually no prompt needed |
| `internaldrive` | Full Disk Access | `So Checkup can measure all your folders (Photos, Mail, app data)` | `Allowed` | `Open Settings` → Privacy › Full Disk Access | not needed (run disk scan as user; skip protected folders) |
| `fan` | Fusebox helper | `Optional: fan control and charge limit. Asks for your password once` (or the install result note) | `Installed` | `Install` | optional Windows service installed with UAC elevation |

Footer: `Full Disk Access: in the list, turn on Fusebox (or add it with +). Fusebox only measures; it never reads, sends or deletes your files.`

Nothing is requested silently: every OS prompt follows a click. (Separately, at app launch: if `notify` is on, the app requests notification permission; 2 s after launch `Notify.askOnce()` runs once per "ask round": not-determined → system prompt; denied → an alert `Allow notifications from Fusebox?` / `Fusebox tells you when an update is ready and when an app keeps your Mac busy. Notifications for Fusebox are off in System Settings.` with `Open Settings` / `Not now`.)

### Summary (step 12)
Section with `LabeledContent` rows (optional import message first):
- `Look` → `<Theme name>, <normal|large|extra large|custom> text`
- `Measurements` → abbreviations joined `, ` (e.g. `CPU, GPU, RAM, NET, SSD, BAT`)
- `Menu bar` → style title
- `Notifications` → `<n> kinds on` or `Off`
- `Data` → `History kept forever` / `History kept <n> days` + ` · ` + `internet check on` / `internet check off`
Footer HStack: caption `You can change everything later in Settings (⌘,).` + small button `Share my settings…`.

---

## 11.8 Updater and installer

Source: `Updater.swift`, `Installer.swift`, `release.sh` / `scripts/sign-update.swift` (signing).

### Update feed
- Repo comes from Info.plist key `FuseboxUpdateRepo` (`owner/name`); without it the updater is disabled (`Check for updates` disabled).
- Latest: `GET https://api.github.com/repos/<repo>/releases/latest` with headers `Accept: application/vnd.github+json`, `User-Agent: Fusebox-Updater`, timeout 20 s.
- Changelog: `GET …/releases?per_page=60` (drafts skipped).
- Tag format: `v<version>-beta-<build>`, e.g. `v0.1-beta-202611162337`. **Build number** = last run of digits in the tag with ≥ 8 digits (`yyyyMMddHHmm`). Pretty version = tag without leading `v`, split on `-`, last part dropped, joined with space → `0.1 beta` (`v1.0-202701010000` → `1.0`).
- Asset: the release asset named exactly `Fusebox.zip`; its URL must be **https**, host `github.com` (path must start with `/<repo>/releases/download/`) or `*.githubusercontent.com`. Response < 2 MB.

### Schedule
- `start()` once at launch: announce-if-updated; first check **15 s** after launch; then a timer every **15 min** (tolerance 60 s) — 4 requests/hour, within GitHub's 60/hour anonymous limit.
- Low impact on: skip unless the last check was ≥ **55 min** ago (≈ hourly).
- Automatic check failures are silent (state back to idle); user-initiated failures show the error.

### Flow
1. Parse; if build ≤ current → `You're up to date`.
2. Newer → state `available(tag)`; if not user-initiated, send **one notification per new version** (remembered in `notifiedUpdate`, respects `notify`): title `Fusebox <0.7 beta> is available`, body `Installing now. Fusebox restarts in a moment.` (auto) or `Open Settings › About to install it.` (manual).
3. Install if user-initiated, or if `autoUpdate` is on **and** this build didn't fail before (`failedUpdateBuild`; a failed build is only retried via `Check for updates`).
4. Install: download `Fusebox.zip.sig` first (must be HTTP 200), then the zip. Verify **Ed25519 signature** (base64 signature over the exact zip bytes, zip < 200 MB) against the public key embedded in the app: `uYp37BJBFTd9+oS28OpFCW60sRliYE4uHvyGBnCrnXU=` (32-byte raw Curve25519 signing key, base64). Unsigned/invalid → `Update isn't signed by the Fusebox maker`.
5. Unzip to a temp folder (`ditto -x -k`), require `Fusebox.app` with the **same bundle id** (`Not a Fusebox update`), **same build number** as the tag (`Version mismatch`), and `codesign --verify --deep --strict` passes (`codesign failed`). Temp folder removed on any failure.
6. Replace the running app bundle in place (`replaceItemAt`), remove temp folder, then spawn `/bin/sh` that waits until this PID exits and reopens the app; terminate. Settings and history stay (same bundle id/user defaults).
7. Any failure: store `failedUpdateBuild`, state `failed(<message>)`.

### "What's new" after an update
- At start: compare `lastSeenBuild` with the current build, then store the current build.
- If current > seen, AND (seen > 0 OR setup done), AND notifications on: fetch `…/releases?per_page=10`, take the release whose tag build = current build (else the newest) and send a notification: title `Fusebox updated to <0.6 beta>` (or `Fusebox updated`), body = **summary** of the notes, category `whatsnew` with action button `What's new`. Fresh installs get no announcement.
- Summary rule: take the lines starting with `- ` or `* `; first 3 bullets, each cut at the first `:` or ` — `; joined with ` · `; max 110 chars (cut at 107 + `…`). No bullets → first line, max 110 chars. Empty → `See what's new in Settings › About.` Example: `- Docked: see what's plugged in\n- Much lighter — 5% CPU\n- Fonts: six choices` → `Docked · Much lighter · Fonts`.
- Clicking the notification or `What's new` sets `settingsTab = about`, activates the app and opens Settings (changelog visible, installed release marked `Installed`).

### Installer (first launch from outside Applications)
Runs before any window (`applicationWillFinishLaunching`). Does nothing if the app is already in `/Applications/` or `~/Applications/`.
- Another copy with the same bundle id is installed:
  - Installed build **newer** → alert `A newer Fusebox is already installed` / `This copy (<v (b)>) is older than the one in Applications (<v (b)>).` buttons `Open installed version` / `Not now`.
  - **Same** build → just open the installed one (and quit this copy).
  - Installed build **older** → alert `Update Fusebox?` / `Replace Fusebox <old> in Applications with this version (<new>). Your settings and history stay.` buttons `Update` / `Not now`. Then: terminate other running instances, wait up to ~5 s (50 × 0.1 s, run loop kept running); still running → `The old Fusebox is still running. Quit it (⌘Q) and open this one again.`; else move the old copy to the **Trash** (recoverable), copy this one in, clear the quarantine flag, open it, quit. Error → `Couldn't update Fusebox: <error>. Drag Fusebox into Applications yourself.`
- Not installed → alert `Move Fusebox to Applications?` / `Then it's easy to find, and future updates replace it automatically.` buttons `Move to Applications` / `Not now`. Target `/Applications` if writable, else `~/Applications` (created). Copy, clear quarantine, trash the original (unless it runs from App Translocation), open the new copy, quit. Error → `Couldn't move Fusebox: <error>. Drag it into Applications yourself.`
- Opening another copy: if that copy is already running, just activate it and quit; else launch it as a new instance and quit when launched. While handing over, the setup window's close is not treated as "setup done".
- All failures show a plain alert titled `Fusebox`.

### Windows equivalent
- Prefer **MSIX** with an App Installer file (`.appinstaller`, `UpdateSettings` → `OnLaunch` + `AutomaticBackgroundTask`), or winget; that gives signed in-place updates and keeps `LocalSettings`.
- If self-updating from GitHub Releases (same feed design): poll `releases/latest` every 15 min (hourly in Low impact), asset `Fusebox-win-x64.zip` + `.sig`, verify an **Ed25519** signature with an embedded public key (e.g. NSec/BouncyCastle), verify Authenticode signature + same product id + build number equals tag, swap the install folder via a tiny updater process that waits for the main PID to exit, then relaunch. Keep `failedUpdateBuild`, `notifiedUpdate`, `lastSeenBuild` semantics and the same UI strings.
- "Move to Applications" has no direct equivalent; with MSIX it is unnecessary; with a zip distribution offer "Install to %LOCALAPPDATA%\Programs\Fusebox" with the same newer/same/older logic.

---

## 12. Porting checklist and key strings

### 12.1 Open points found while writing this spec (decide before porting)

| # | Finding in the macOS code | Product rule | Recommendation for the port |
|---|---|---|---|
| 1 | The Network tab shows MAC addresses: a "MAC" column (window ≥ 760 pt, hideable via `netHide` id `mac`), a "Hardware address …" line in the expanded device row, and search matches MACs (§6). | Never show MAC addresses or serials. | Do **not** show MACs. Use the MAC internally only (vendor lookup via OUI, private-address detection, device identity). Drop the column and the line; keep "Private address" as the vendor text. |
| 2 | The Docked tab "All values" raw list for USB devices does not filter serial-number keys (§7). Bluetooth raw keys containing "address" or "serial" are dropped. | Never show serials. | Filter any key containing `serial`, `address`, `uuid`/`guid` from every raw list. |
| 3 | `dockedDisplays`, `dockedWireless`, `appLanguage` are not in `Prefs.defaults` (default true / true / "" only via the view). | All settings live in one table. | Add them to the Windows settings table so export/import covers them (keep `appLanguage` local-only). |
| 4 | `warnPing` only colours ping on Live (set in Settings › Alerts), not on the Network tab. | — | Keep as is or also apply on Network; be consistent. |

### 12.2 Build order suggestion

1. Settings table (§9 master table) + theme tokens (§10) + formatting helpers (§3) — everything else depends on them.
2. Sampler + history + CSV (§11.1).
3. Window shell + tab bar + placement (§2).
4. Live (§3), menu bar/tray (§8), then Processes (§4), Checkup (§5), Network (§6), Docked (§7).
5. Notifications (§11.2, §3 peak alerts), first-run setup (§11.7), updater (§11.8).
6. Low impact pass: verify no animation and no redraw when hidden/minimized/screen off.

### 12.3 Key strings (exact English)

| Where | String |
|---|---|
| Window title | "Fusebox" |
| Setup window title | "Set up Fusebox" |
| Tabs | "Live", "Processes", "Checkup", "Network", "Docked", "Settings" |
| Tab tooltip | "Live  ⌘1" … "Docked  ⌘5"; "Settings  ⌘,"; "Settings  ⌘,  ·  Low impact is on" |
| Profiles | "Simple" — "Big, calm, with advice"; "Gamer" — "GPU and ping up front"; "Nerd" — "Everything, in real values"; "Laptop" — "Battery first, alert when almost empty" |
| Settings import | "Fusebox can't read this file." / "\(n) settings imported" |
| Settings export filename | "Fusebox-settings.json" |
| Language picker | "System", "English", "Nederlands", "Deutsch", "Français", "Español" |

All other strings are quoted inline in their sections. Profile values (from `Prefs.presets`; applying a profile first resets every key any profile touches, then applies its own values; logging, retention and ping host are never touched):

| Profile | Values |
|---|---|
| Simple (`leaf`) | theme `liquid`, textScale 1.15, metrics `cpu,ram,ssd,battery`, graphs `never`, showAdvice true, menubarStyle `icon` |
| Gamer (`gamecontroller`) | theme `instrument`, metrics `cpu,gpu,ram,net`, graphs `auto`, shortMinutes 5, menubarStyle `compact`, menubarMetrics `cpu,gpu`, netBits true, warnPing 60 |
| Nerd (`terminal`) | theme `terminal`, metrics `cpu,gpu,ram,net,ssd,io,battery`, absolute `cpu,ram,ssd`, menubarStyle `graph`, menubarMetrics `cpu,ram,net` |
| Laptop (`laptopcomputer`) | theme `minimal`, metrics `battery,cpu,ram,ssd`, menubarStyle `text`, menubarMetrics `battery,cpu`, battery alert on |

Settings import trust rules: only known keys; value must have the same type as the default (Bool/Int/Double/String; Double must be finite; Int must be exact); strings ≤ 200 chars; file < 100 000 bytes; local-only keys skipped: `pingHost, setupDone, page, settingsTab, fanMode, fanCurve, chargeLimitOn, chargeLimit, chargeHeat, keepDays, autoUpdate`.
