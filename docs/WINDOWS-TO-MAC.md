# Windows → Mac hand-over

Features the Windows app (`windows/`, reference for behaviour) has and the Mac app (`Sources/`, 0.13.2) should get. Build them in one go, in the order at the end. The Mac target is **macOS 26, Apple Silicon only** (`build.sh`: `-target arm64-apple-macos26.0`), so no Intel/T2 cases.

Rules that apply to everything:
- **Light**: nothing polls. Heavy work (subprocesses, log reads) only while the page that shows it is visible, in `Task.detached(priority: .utility)`, with a timeout (`Diag.run(..., timeout:)`), result cached in a `@MainActor static var cache` like `StartupSection.cache`.
- **Privacy**: no user names, serial numbers, Wi-Fi names, or home paths in reports, the log or exports. App names are fine. Tooltips may show a path on screen but it never goes into a report.
- **Reuse** the Checkup components (`CardTitle`, `Headline`, `Capped`, `Pill`, `themedRow`, `Tip`) and the existing look; no new styles.
- **Strings**: every new English string goes in all four `Resources/{nl,de,fr,es}.lproj/Localizable.strings` (wording from `docs/translation-glossary.md`; `en.lproj` too if it lists keys). `L("...")` for code strings, plain literals for SwiftUI `Text`/`Button`.
- **Self-test**: every new parser/decision is a pure function with a `precondition` test in its type's `selfTest()`, registered in `Sources/App.swift` (`Entry.main`, list at ~line 129–165). `./build.sh` runs `--selftest`.
- Anything marked **(verify)** was not checked on a real macOS 26 machine: check it first, adapt, and note the result in the commit message.

---

## 1. Startup items on/off [M]

### What Windows does
`windows/src/Fusebox.Core/Checkup/Startup.cs` (model, merge, count), `Fusebox.Platform/Checkup/StartupScanner.cs` (read + `SetDirect`/`SetElevatedAsync`), `Fusebox.App/Pages/CheckupCardsStorage.cs` (`StartupCard`, lines ~12–125). Each row has a switch that flips the same flag Task Manager uses (`StartupApproved`), so it is reversible and the item stays in the list, shown as "off". Items that need admin rights get an elevated switch with the tip "Needs administrator permission". Items with the same app name merge into one row and the switch applies to all of them. Headline counts only enabled, non-orphan items.

### What the Mac has today
`Sources/Opstart.swift`: `Startup.read()` (line 68) reads `~/Library/LaunchAgents`, `/Library/LaunchAgents`, `/Library/LaunchDaemons`; `Startup.item()` (line 41) **drops** `com.apple.*` and plists with `Disabled = true`; `StartupSection` (line ~320) shows groups "Apps that start at login" / "Background helpers" / orphans, running dot, usage text, merged by name, buttons "Show in Finder" and "Login Items…" (opens `x-apple.systempreferences:com.apple.LoginItems-Settings.extension`). Strictly read-only. `AppPagina.swift:357` states Fusebox cannot see the Login Items list of System Settings. `LoginToggle` (`Instellingen.swift:152`) only handles Fusebox itself (`SMAppService.mainApp`).

### Build on the Mac
**Mechanism (recommended): `launchctl disable` / `enable` on the user's GUI domain.**
- Off: `launchctl disable gui/<uid>/<label>`, then, if the item is running, `launchctl bootout gui/<uid>/<label>` (stops it now; `disable` alone only acts at next login).
- On: `launchctl enable gui/<uid>/<label>`. Do not start it now; it comes back at next login (same as Windows). Tooltip says so.
- Why this one: no admin needed for your own `gui/<uid>` domain; stored by launchd in its own override database, so the other app's plist is never edited (apps and their updaters overwrite plists; editing `Disabled` would be undone and could break code-signed/managed files); fully reversible; works the same on macOS 14/15/26. Rejected: `launchctl unload -w` (deprecated), editing `Disabled` in the plist, `bootout` alone (not persistent, KeepAlive agents return).
- Read state: `launchctl print-disabled gui/<uid>` (no admin, fast). Output is `"label" => disabled` / `=> enabled` (older: `=> true/false`); accept both. An override of `enabled` beats `Disabled = true` in the plist; no override → use the plist key. (verify: run `launchctl disable gui/$(id -u)/com.example.x`, then `print-disabled`, then `enable`, on macOS 26 as a normal user.)
- Run via `Diag.run("/bin/launchctl", [...], timeout: 5)`; get the uid with `getuid()`.

**Code:**
1. `StartupItem` (Opstart.swift:8): add `var enabled = true`. `Startup.item()`: do not return nil for `Disabled == true`; set `enabled = false` instead. Keep dropping `com.apple.*` and items without a Label.
2. `Startup.read()`: call `disabledLabels()` once (parse of `print-disabled`, pure function `parseDisabled(_ s: String) -> [String: Bool]` = label → disabled) and set `enabled = !(override ?? plistDisabled)`.
3. New `Startup.set(_ item: StartupItem, on: Bool) -> Bool`: only when `item.user` (plist under `~/Library/LaunchAgents`); builds commands with a pure `toggleCommands(label:uid:on:running:) -> [[String]]`; after running, re-read `print-disabled` and return whether the state now matches (so a silent failure flips the switch back).
4. `StartupSection.group()` (line ~385): the grouping `Dictionary(grouping:)` must keep the members (`var members: [StartupItem]`) so the switch applies to all user members. Row: add a `Toggle("", isOn:).toggleStyle(.switch).labelsHidden().controlSize(.mini)` at the right (after usage, replacing nothing), `.accessibilityLabel(name)`. Off rows: dot grey, state word "off", name `onRow(true)`, no usage. Sort enabled before disabled, then as today (watt, name).
5. Headline count (`login` in `StartupSection.body` and in `Analyse.swift:917`): add `&& $0.enabled`.
6. Rows that cannot be switched (`!user`: /Library agents, daemons) get no switch, instead a small lock icon (`lock`) with `.help("Managed by macOS: change it in System Settings › General › Login Items & Extensions")`. The existing "Login Items…" pill stays.

**Login Items (the "Open at Login" list) and SMAppService items of other apps: stay out.** There is no public API to read or remove another app's login item without an Apple-Events (Automation) consent prompt (`System Events`), and BTM (`sfltool dumpbtm`) needs admin. Do not add prompts. Optional read-only extra (verify): parse `~/Library/Application Support/com.apple.sharedfilelist/com.apple.LSSharedFileList.SessionLoginItems.sfl3` and show names read-only; macOS 13+ may not fill it for items added in System Settings. If it is empty or unreadable, skip silently. The button "Login Items…" is the way to change those.

**Strings (English):**
- Card text (replace the current line): `Apps from other makers that start with your Mac or wait in the background. Turn off what you don't use.` (the old key mentioned "in Login Items"; remove or leave unused)
- State word: `off` (existing: `running`, `you`, `all users`; make sure all four have entries in the .strings files)
- Switch tooltip when off: `Off. Switch on to start it again at your next login.`
- Failure note under the row: `Couldn't change this item.`
- Lock tooltip as above.

### Edge cases
- Some apps re-enable their agent on update or at launch: after the re-read, show the real state; do not fight it.
- `bootout` of an agent that is not loaded returns an error: ignore errors from `bootout`, judge only by the `print-disabled` re-read.
- Agents installed through `SMAppService` live inside the app bundle and do not appear in `~/Library/LaunchAgents`; they are not in this list at all (known gap, covered by the button).
- Orphans (program gone): keep current display; they also get a switch (turning off is harmless), "Show in Finder" stays.
- Switching must not block the UI: run the commands on a utility task, disable the switch while busy.
- Items with the same name but one `user` and one system copy: switch applies to the user one only; show the lock tooltip only if no member is switchable.
- Report/PDF (`Analyse.swift` startup lines): list names of enabled items only; no labels, no paths.

### Done when
- Selftest: `parseDisabled` (both `disabled/enabled` and `true/false` formats, blank lines, labels with dots/dashes), `item()` keeps `Disabled=true` as `enabled == false`, `toggleCommands` for on/off × running/not running, count excludes disabled. The old precondition at Opstart.swift:289 (`Disabled → nil`) is changed accordingly.
- Manual: with a throw-away user agent (`~/Library/LaunchAgents/com.fusebox.test.plist`, `RunAtLoad`): switch off → process stops, row says "off", count drops by one; relaunch Fusebox → still off; log out/in → not started; switch on → row "off" gone, starts at next login; `launchctl print-disabled gui/$(id -u)` agrees at each step.
- System agents/daemons show no switch and the lock tooltip.
- No `Process` is spawned while Checkup is not open.

---

## 2. Updates and restart needed [M]

### What Windows does
`CheckupReader.PendingReboot()` and `LastUpdate()` (`Fusebox.Platform/Checkup/CheckupReader.cs:71–95`, registry, instant). `Tips.cs:140`: serious tip `Restart needed — Windows is waiting for a restart to finish installing updates (last update <date>).` with button `Open Windows Update`. Also the housekeeping line `Windows Update is installing updates` (`Housekeeping.cs:18`).

### What the Mac has today
Only the "Software update is checking or installing updates" job in `Diagnose.swift:109` (process `softwareupdated`) and the generic uptime tip (`Analyse.swift:489`, "Not restarted for N days…", setting `restartDays`). Nothing reads pending updates.

### Build on the Mac
**Source: the system's own preferences, not `softwareupdate -l`.** Read `/Library/Preferences/com.apple.SoftwareUpdate.plist` (world-readable) with `NSDictionary(contentsOfFile:)`: `RecommendedUpdates` (array of dicts with `Display Name`, `Display Version`, `Identifier`, `Product Key`), `LastRecommendedUpdatesAvailable` (count), `LastSuccessfulDate` / `LastFullSuccessfulDate` (last check). Instant, no network, no subprocess. (verify on macOS 26 that `RecommendedUpdates` is still maintained after a check and emptied after an install; if not, fall back to the on-demand button only.)

**On demand only:** a pill `Check now` runs `/usr/sbin/softwareupdate -l` via `Diag.run(..., timeout: 90)` (slow: 10–60 s, uses the network, no admin needed). Never automatic, never on a timer, never at Checkup open. Show `Checking…` and a `ProgressView`; cancel when the page disappears (`.task(id:)`). Cache result + date in memory (`static var cache`) and in `UserDefaults` (`swuCheckedAt`, `swuResult` as a short JSON); at most one automatic re-use per 6 h, a click always re-runs.
Parse (pure function `Updates.parse(_ out: String) -> [Update]`): lines `* Label: <label>` followed by `\tTitle: <t>, Version: <v>, Size: <n>KiB, Recommended: YES, Action: restart,`; `No new software available.` = none. `Action: restart` marks restart-needed updates. (verify exact text on macOS 26; keep the parser tolerant, unknown format → "Couldn't read the result.")

**Restart needed:** macOS has no registry-style "reboot pending" flag. Rule: an update is a restart item when it is a macOS update (`Display Name` starts with `macOS`, or `Action: restart` after a manual check). Do not claim "waiting for restart" unless an update is already downloaded and prepared: check `/Library/Updates/` for a non-empty product folder (verify; world-readable?). If that cannot be read, word it as "update available", never "restart needed".

**UI:** a new card `UpdatesCard` as the first card of the **Stability** group (`Analyse.swift:1002`, `case "stability"`), same `themedRow`, `CardTitle(icon: "arrow.down.circle", title: L("Software Update"), tone:)`; tone 1 when updates are pending. Add `"updates"` to the checkup hide list (`InstellingenPaginas.swift:128`, `L("Software Update")`) like the other cards. `Headline` shows `2 updates available`/`Up to date`.
Plus one serious `Tip` (id prefix `restart`, add to `Tip.serious` list at Analyse.swift:109 and category `stability` at line 123): when a macOS update is pending: text + action `.open(URL("x-apple.systempreferences:com.apple.Software-Update-Settings.extension")!, L("Open Software Update"))`.

**Strings (English):**
- `Software Update` (card title)
- `Up to date` / `1 update available` / `%lld updates available`
- `Last checked %@` (date, `uiLocale` formatting like SSDSpeedCard)
- `Check now`, `Checking…`, `Couldn't check for updates.`
- `Open Software Update`
- Tip: `A macOS update is waiting (%@). Install it and restart to finish — updates fix security problems and crashes.` (if downloaded: `Restart needed — macOS is waiting for a restart to finish installing updates`)
- Cache hint under the headline when the data is old: `Fusebox doesn't check by itself. Press Check now.`

### Edge cases
- No `LastSuccessfulDate` or older than 14 days: say `Not checked recently` (info tone, not warning); do not claim "up to date".
- Managed/MDM Macs may hide updates or have `softwareupdate` blocked: error → generic failure line.
- Beta channel and `Display Name` in another language: match on `Product Key`/`Identifier` prefix `MSU_UPDATE_`/`macOS`, not on translated text (verify keys).
- Reading must never trigger Full Disk Access prompts (these paths do not).
- Report: only counts, no product keys.

### Done when
- Selftest: `Updates.parse` with a real `softwareupdate -l` sample (one restart update + one Safari), the "No new software available." sample, garbage → empty/nil; plist dict → list.
- On a Mac with a pending update: Checkup shows it with zero subprocesses spawned (check Activity Monitor/`ps` during open); Check now spawns exactly one `softwareupdate` and it ends with the result; leaving the page cancels it.
- The button opens System Settings › Software Update.

---

## 3. Checkup useful on day one [M]

### What Windows does
`Fusebox.Platform/Checkup/EventLogReader.cs`: crashes/hangs (Application log), blue screens (WER 1001), **unexpected shutdowns (Kernel-Power 41)**, low memory, last 30 days; sleep/wake last 7 days; `CheckupReader.ReliabilityHistory()` = Windows' stability index for 30 days (card "Windows stability": headline, sparkline, `Last 30 days, from Windows' Reliability Monitor`, `CheckupCards.cs:350–380`). Stability card rows: `Blue screen N×`, `Unexpected shutdown N×` (`CheckupCards.cs:338–339`).

### What the Mac has today (so only the gaps are handed over)
- App crashes, hangs, resource reports, jetsam from `DiagnosticReports`: **done**, already day-one because macOS keeps them: `Diag.crashes(from:to:)` (`Diagnose.swift:256`, reads `~/Library/Logs/DiagnosticReports` and `/Library/Logs/DiagnosticReports`, header only, newest 300, test builds skipped), `parseHeader` (line 211). Used by `Analyse.swift:309`, stability group (908), Crashes card (1281), tips (468).
- Sleep/wake from `pmset -g log`: **done**, `Power.readLog()` (`Accu.swift:56`, grep-filtered, ~5 s, cached 1 h at line 332). It keeps only the last 2 days (`cutoff = now − 2 days`). Windows shows 7; raising it is optional (`Accu.swift:58`, constant) and cheap since grep already filters.
- **Missing:** kernel panics, unexpected shutdowns / resets, and a 30-day overview. `parseHeader` returns `.none` for bug types other than 309/109/298, so panics are ignored; nothing reads shutdown causes.

### Build on the Mac
**3a. Kernel panics and watchdog resets (cheap, same files).**
- Extend `Diag.Entry` with `case panic` (and `case reset`), `Diag.Crashes` with `var panics = 0, resets = 0` (count in `isEmpty`, `rows`).
- `parseHeader` for `.ips`: `bug_type` `210` = kernel panic → `.panic`. Files named `Kernel-*.panic` / `panic-full-*.panic` (text) and `ResetCounter-*.diag` (unexpected reset; header has `Boot faults:`/`Reset count`) → `.panic` / `.reset` (match on file name prefix + first lines). (verify: bug_type numbers and file names on macOS 26 Apple Silicon; check `ls /Library/Logs/DiagnosticReports` and `~/Library/Logs/DiagnosticReports` on the MacBook, and whether `/Library/Logs/DiagnosticReports` is readable without admin; the code already skips unreadable directories.)
- Panics are always real: do not apply `testBuild`; apply the `is_simulated` skip.
- UI: in the Crashes card `rows`: `Mac restarted by a crash` / `kernel panic N×` (value). Group summary at Analyse.swift:908–914: add `L("\(n) restart(s) after a crash")`; `warn` true when panics > 0. Serious tip (id `panic`, add to `Tip.serious` and to the `stability` prefix list at line 123): `Your Mac restarted after a crash (kernel panic) %lld× %@. Update macOS and unplug extra USB devices; if it keeps happening, contact Apple Support.` with action `.open(Software Update URL, L("Open Software Update"))`.

**3b. Unexpected shutdowns (needs the unified log; slow, so once and cached).**
- Source: the kernel logs `Previous shutdown cause: <n>` at every boot. Read with `/usr/bin/log show --last <N>d --style compact --predicate 'process == "kernel" AND eventMessage CONTAINS "Previous shutdown cause"'`. Keep the predicate on `process` (indexed), not only on `eventMessage`, otherwise it scans everything. This is **slow (10–60 s for 7 days) and CPU heavy**: therefore only on demand and cached.
- Plan: when Checkup's Stability group is opened and no cached value younger than 6 h exists, run it once in `Task.detached(priority: .utility)` with timeout 60 s (`Diag.run`), show `Checking shutdowns…` meanwhile (the rest of the card must already be visible). Store the parsed result in `~/Library/Application Support/Fusebox/shutdowns.csv` (`date,cause`) so older entries survive log expiry. First run reads `--last 7d`; later runs read `--last 2d` and merge by timestamp. (verify: how far back `log show` actually goes for kernel default-level messages on macOS 26; if less than 7 days, say "last N days" from the data.) `log show --last boot` is a cheap option for "this boot only" (verify it exists on macOS 26).
- Classify with a pure `Shutdowns.classify(_ cause: Int) -> Kind`: `5` = normal; `3` = forced (power button held); `0` and negative = unexpected (power lost, thermal, fault); other positive = ignore. (verify the table against current Apple docs/observations; unknown → ignore, never alarm.)
- Stability card rows: `Unexpected shutdown N×` (same wording as Windows), `Forced shutdown N×`. Tip when `N ≥ 2`: `Your Mac shut down unexpectedly %lld× %@. Check the charger and cable, and keep macOS up to date.` (not serious unless ≥ 3.)

**3c. 30-day overview (optional, S).** Windows' stability index has no Mac equivalent, so do not invent a score. Instead a strip of 30 small bars, one per day = crashes + hangs + panics + unexpected shutdowns (from the same `DiagnosticReports` pass; `Diag.crashes(from: now − 30 d, to: .distantFuture)` plus `times`), in the Stability group, label `Last 30 days, from macOS' own reports`. Reuse the existing sparkline/bars component (`Spark`, see `Spark.selfTest` in App.swift) if it fits; otherwise skip.

### Edge cases
- A fresh install with no reports: show `No crashes or hangs %@` (existing); never an error.
- `log show` unavailable/times out → silently show nothing for 3b, no retry loop (retry on next Checkup open after 6 h).
- Reports of other users' apps are not readable; only count what is readable.
- Privacy: app names only (as today); panic reports contain serials and user paths: read the header line only (first 4 KB, as `crashes()` already does), never copy panic text into reports or the log; store only date + cause number.
- Performance: nothing in the minute loop (`Meter.minuteTasks`) may call `log show`.

### Done when
- Selftest: `parseHeader` with a `bug_type 210` header → `.panic`, `ResetCounter` sample → `.reset`, `is_simulated` still `.none`; `Shutdowns.parse` on 4 sample lines (normal, forced, power loss, garbage); `classify` table; CSV merge dedupes by timestamp.
- Manual: with a copied panic file in `~/Library/Logs/DiagnosticReports` (rename a sample) the Crashes card and group summary change; after the first Checkup open the shutdown line appears or the "Checking…" ends within 60 s; the second open spawns no `log` process.

---

## 4. SSD health [S]

### What Windows does
`ElevatedScripts.cs:12` runs one elevated PowerShell (`Get-PhysicalDisk | Get-StorageReliabilityCounter`) behind `Show details` (UAC, one-off): Wear, temperature, read errors, power-on hours; plain line `Windows reports this drive as healthy` otherwise (`CheckupCardsStorage.cs:281–305`).

### What the Mac has today
**Already more than Windows, without admin:** `SSDHealth` in `Opstart.swift` (~line 540): NVMe SMART log page via IOKit `NVMeSMARTLib` plugin (`IOEmbeddedNVMeBlockDevice`/`IONVMeBlockDevice`, `read()`), `parse()` takes 512 bytes → wear %, spare %, spare threshold, TB written, power-on hours, media errors, critical-warning bits; `SSDHealthCard` shows `OK`/`Check soon`, `Wear 3% · 41 TB written in total · 10,000 hours on`, warn text; self-test included. `SSDBench` = speed test. No `smartctl` needed.

### Build on the Mac (only the difference)
1. **Details line (optional, S):** the log page already holds what Windows shows beyond this: composite temperature (bytes 1–2, little-endian Kelvin → °C), power cycles (offset 112, 16 bytes), unsafe shutdowns (offset 144, 16 bytes), error-log entries (offset 176), data read (offset 32). Add to `Info` and parse in `SSDHealth.parse` (same `le()` helper), show as an expandable `Show details` pill → small `Capped` list rows: `Temperature 41 °C`, `Available spare 100%`, `Power cycles 312`, `Unsafe shutdowns 4`, `Data read 38 TB`. (verify offsets against the NVMe spec, "SMART / Health Information (Log Identifier 02h)": temperature 1–2, available spare 3, threshold 4, percentage used 5, data units read 32–47, written 48–63, power cycles 112–127, power-on hours 128–143, unsafe shutdowns 144–159, media errors 160–175, error-log entries 176–191.)
2. Unsafe shutdowns rising is a useful pointer to the unexpected-shutdown story of feature 3b; show it only in details.
3. **Not building:** an elevated one-off (nothing needs it on the Mac), a "healthy" line from `diskutil info` (Apple-silicon internal SSDs report `SMART Status: Not Supported`/unknown there; the IOKit read is the real source), external/USB SSDs (not NVMe-SMART-capable via this plugin; the card stays hidden, as today).
4. If `read()` returns nil (card currently shows nothing) keep that behaviour: no "unknown" noise.

**Strings (English):** `Show details`, `Hide details`, `Temperature %lld °C`, `Available spare %lld%%`, `%@ power cycles`, `%@ unsafe shutdowns`, `%@ TB read`.

### Edge cases
- Temperature byte 0 (not reported) → omit the row. Counters are 128-bit: clamp as `clamp()` does.
- The read happens at card appearance (`SSDHealthCard.task`); keep it that way, no timer. `SSDHealth.refresh` (hourly) stays as is.

### Done when
- Selftest extended with a synthetic 512-byte log containing temperature 313 K → 40 °C and the three counters.
- On the MacBook the details match `system_profiler SPNVMeDataType` where overlapping and values are plausible.

---

## 5. Update rollback [M]

### What Windows does
`Fusebox.App/Updater.cs`: `Swap()` renames each replaced file to `*.old` and lists them in `update-old.txt`; after the swap it writes the marker `update-pending` (content = new build) and relaunches. In `Boot.cs:48` a 3 s timer calls `Updater.Confirm()`: still running after 3 s = it starts → marker and `*.old/*.bad` deleted. On the next start, `RollBackIfNeverConfirmed()` (called before any UI) restores the old files when the marker is still there, older than 2 min (`UpdateSafety.ShouldRollBack`, `Core/Update/UpdateSafety.cs:23`) and old files exist; sets `failedUpdateBuild` so the same build is not auto-installed again.

### What the Mac has today
`Sources/Updater.swift`: `install()` downloads signed zip → `unpackAndVerify` (bundle id, build number, `codesign --verify --deep --strict`) → `replaceAndRelaunch` (line ~198): `FileManager.replaceItemAt(current, withItemAt: newApp, backupItemName: nil)` (**no backup: the old app is gone**), then a detached `sh` waits for the old pid and runs `open <app>`. Failure handling only for the replace step (`failedUpdateBuild` at lines 192/206, honoured at line 110). Nothing detects a new version that does not start.

### Build on the Mac
Data dir `~/Library/Application Support/Fusebox/` (not `Documents/Fusebox`, that is `logDir`). Files: `update-previous/Fusebox.app` (backup) and `update-pending` (marker, tiny JSON `{"build": <new>, "from": <old>, "at": <epoch>}`).

1. **Backup before replacing** (in `replaceAndRelaunch`, before `replaceItemAt`): remove any old `update-previous`, then `FileManager.copyItem(at: current, to: backup)` (APFS clone, cheap, same volume). If the copy fails, **abort the update** (`state = .failed(L("Couldn't back up the current version"))`); safety first. Write the marker only after the replace succeeded.
2. **Watchdog in the relaunch script** (covers "crashes before any Swift code runs": dyld, signature, Gatekeeper, missing framework; the Windows start-time check cannot cover that). Extend the existing `sh -c` (line ~207) so after `open` it waits and decides:
   `while kill -0 $PID; do sleep 0.2; done; open "$APP"; sleep 20; if [ -e "$MARKER" ]; then rm -rf "$APP" && mv "$BACKUP" "$APP" && open "$APP"; fi`
   i.e. the new version deletes the marker after 3 s alive; if the marker is still there after 20 s the new build did not confirm → restore and open the old one. Pass paths as positional args (`$0`, `$1`…), never string-interpolated. The script must also remove the marker after a restore and write `update-failed` containing the bad build number; the old version reads it at start (`Updater.start()`), sets `failedUpdateBuild`, deletes the file, and shows one notification (`Notify.send`) with the strings below.
3. **Confirm** (new `Updater.confirm()`, called from `Updater.start()` via `DispatchQueue.main.asyncAfter(deadline: .now() + 3)`): delete the marker, delete `update-previous`, remove leftovers in `/tmp`-style dirs. It must run only when the process is really alive and the main run loop works (hence a main-queue timer, not a background thread).
4. **Start-time check** (second layer, like Windows): `Updater.rollBackIfNeverConfirmed()` called early in `Entry.main` (Sources/App.swift:~118, before `MeterkastApp.main()`; not in `--selftest` mode): marker exists && marker.build == `currentBuild` && marker age > 2 min && backup exists && `Bundle.main.bundleURL` is the install location → move bad app aside, `mv backup → current`, set `failedUpdateBuild`, relaunch with the same detached `sh` pattern, `exit(0)`. Pure decision function `Updater.shouldRollBack(markerAge:backupExists:markerBuild:currentBuild:) -> Bool` (mirror of `UpdateSafety.ShouldRollBack`).
5. Restore check: run `codesign --verify --deep --strict` on the backup before restoring (it was the installed app, so it passes; if it does not, do nothing and keep the new one).
6. Not building: a manual "go back to previous version" button (backup is deleted on confirm). Mention only if Joël wants it later.

**Strings (English):**
- `Couldn't back up the current version` (install failure)
- Notification title `Fusebox went back to the previous version`, body `The update didn't start properly. Fusebox will not try version %@ again automatically.` (use `Updater.pretty` on the tag/build as available; else omit the version)

### Edge cases
- Translocated or non-`/Applications` copies (`/private/var/folders/.../AppTranslocation/`, build folders): the Mac updater currently replaces `Bundle.main.bundleURL` wherever it is; add the Windows rule: automatic install only when `Bundle.main.bundleURL` is in `/Applications` or `~/Applications` (`Installer.isInApplications`), otherwise announce only (existing "available" state). (verify what `Installer.isInApplications` accepts.)
- Disk full: the backup copy fails → abort as above.
- The user quits Fusebox within 3 s after an update: marker stays; the watchdog would roll back at +20 s. To avoid a false rollback, the watchdog test is "marker still there **and** no process of that bundle running"; use `pgrep -f "$APP/Contents/MacOS/"` as the second condition. (verify pgrep pattern matches the helper `fusebox-fand` too; match only the main binary.)
- Two Fusebox processes (Installer flow): untouched.
- `defaults` domain is the same in old and new version, so `failedUpdateBuild` works across the rollback.
- Self-update Fusebox 0.13.x → next: the first update has no marker yet; it installs the new updater that has the backup logic.

### Done when
- Selftest: `shouldRollBack` truth table (marker age 30 s / 3 min, backup missing, build mismatch, build match); marker JSON round trip; a temp-dir test of the restore function (create `cur/Fusebox.app/x` = "new", `prev/Fusebox.app/x` = "old", run restore, assert `x == "old"` and the bad copy is gone/aside).
- Manual: build a release whose binary exits immediately (`exit(1)` at start) and publish/serve it as the update (or run `replaceAndRelaunch` by hand with that app): within ~25 s the old version is back and running, a notification appears, and `failedUpdateBuild` equals the bad build so the next 15-minute check does not install it again. Normal update: marker and backup are gone 3 s after start.

---

## 6a. Encrypted DNS indicator [S, with a stop condition]

### What Windows does
`WinNet.cs:49` `EncryptedDns(net)`: true if the adapter's DNS server has a DoH setting (registry) or Windows auto-upgrades a well-known server; shown in the Connection details as `Encrypted DNS: on/off` (`NetworkPage.Panel.cs:243`, term in `Core/Network/NetTerms.cs:22`) and in the network report/PDF. The Windows "public/private network profile" is **not** ported: macOS has no such concept.

### What the Mac has today
Nothing about encryption. `NetConnection.current()` (`NetwerkInternet.swift:21`) reads `State:/Network/Global/DNS` → `dns: [String]` (server addresses); the Connection card shows them as `DNS` (`NetwerkKaarten.swift:231`, term `.dnsServers`).

### Build on the Mac
macOS has no single public API to ask "is DNS encrypted right now". Candidates, in order, for the Mac session to try **first for ≤ 30 minutes**:
1. `scutil --dns` and SCDynamicStore `State:/Network/Global/DNS` / `State:/Network/Service/<id>/DNS` while a DoH/DoT configuration is active (DNS Settings configuration profile of type `com.apple.dnsSettings.managed`, or an app such as 1.1.1.1/NextDNS using `NEDNSSettingsManager`). Look for a field that names the protocol or a `https://`/`tls://` server. (verify: do this on the MacBook with and without such a profile and diff the output.)
2. iCloud Private Relay and DDR-upgraded resolvers are invisible to these sources; do not guess.
3. `NEDNSSettingsManager` only reads this app's own configuration: useless here.

**Decision rule:** build it only if step 1 gives a reliable positive signal. Then add `NetTerm.encryptedDNS` (`NetwerkKaarten.swift:11`, title `Encrypted DNS`, explanation `Whether the names you look up are sent encrypted, so your provider and others on the network can't read them. Set it up with a DNS profile or a DNS app.`), a `kv(.encryptedDNS, ...)` row after `.dnsServers` (line 231) shown **only when positively detected** with value `on`; add the row to `NetTerm.connection`/`report` lists (lines 84/88) and to the report text like the Windows `NetReport`. **Never show `off`**: the Mac cannot prove the negative, and a wrong "off" is worse than no row. If nothing reliable is found: do not build, and tell Joël in one line.

### Edge cases
- VPN DNS and per-domain resolvers: look only at the primary service.
- No DNS servers (offline): no row.

### Done when
- Pure parser with selftest on two captured `scutil --dns` samples (encrypted/plain); row appears only with the encrypted sample.

---

## 6b. Hotkey recorder with conflict detection [S]

### What Windows does
`Fusebox.App/Settings/HotkeyRecorder.cs` + `Core/Settings/SettingsLogic.cs` (`HotkeyLogic.Record`) + `Fusebox.App/Hotkey.cs`: click, press keys; Esc cancels, Delete turns off, a key needs Ctrl or Alt, keys have readable names (`F5`, `Space`, `PgUp`…); **the shortcut is suspended while recording** (`Hotkey.Suspend`); a refused `RegisterHotKey` sets `Conflict` and the card shows a warning `Another app already uses this shortcut.`

### What the Mac has today
`Sources/Sneltoets.swift`: `Hotkey.apply()` (Carbon `RegisterEventHotKey`, **return value ignored**), `HotkeyRecorder` (line 51; shown at `Instellingen.swift:301`): click-to-record, Esc cancels, ⌫ turns off, needs ⌘/⌥/⌃, label from `charactersIgnoringModifiers` uppercased, beep on a refused key. Default `hotkeyCode 3` (F), mods 6400 (⌃⌥⌘). Note: on the Mac code `0` is a valid key (A); only `-1` = off (do not copy Windows' `> 0`).

### Build on the Mac (the difference only)
1. **Suspend while recording:** `Hotkey.suspended: Bool`; `apply()` skips registering while true (also unregisters); recorder sets it in `start()`, clears it in `stop()`. Today pressing the current combination while recording toggles the window.
2. **Readable key names:** `Hotkey.keyName(keyCode:chars:) -> String?`, pure, with a table: `kVK_F1…F20` → `F1…F20`, `kVK_Space` → `Space`, `kVK_Return` → `↩`, `kVK_Tab` → `⇥`, arrows → `← → ↑ ↓`, `kVK_PageUp/Down` → `PgUp/PgDn`, `kVK_Home/End` → `Home/End`; printable ANSI keys → `charactersIgnoringModifiers.uppercased()` (single character, not a control/private-use scalar); anything else → nil = beep, keep listening. This fixes labels like `⌃⌥⌘` + private-use glyph for F-keys today.
3. **Conflict detection** (store `OSStatus` of `RegisterEventHotKey` in `Hotkey.status`; `Hotkey.conflict` = status ≠ noErr and not suspended):
   - Registration failure (`eventHotKeyExistsErr` = -9878): show `Another app already uses this shortcut.` (same as Windows). (verify whether macOS 26 reports a clash with another app's Carbon hotkey; if it succeeds silently, say so in the commit and rely on the next two checks.)
   - macOS' own shortcuts: `CopySymbolicHotKeys()` (Carbon HIToolbox; array of dicts `kHISymbolicHotKeyCode`, `kHISymbolicHotKeyModifiers`, `kHISymbolicHotKeyEnabled`) → at recording time refuse an enabled system combination with the message `macOS already uses this shortcut.` (beep, keep listening, do not store). (verify the API is still available/unchanged on macOS 26.)
   - Fusebox's own menu shortcuts (`NSApp.mainMenu` items' `keyEquivalent` + `keyEquivalentModifierMask`): refuse with `Fusebox already uses this shortcut.`
   - Other apps' shortcuts cannot be listed: do not claim to detect them.
4. **UI** (`HotkeyRecorder.body`, `Instellingen.swift:301`): below the row, when `Hotkey.conflict` or a refusal message is set, a `Text(...).font(.caption).foregroundStyle(.orange)` (the same pattern as `LoginToggle`'s note). The state must refresh after `apply()` (make `Hotkey.status` an `@Published`-style via a small `ObservableObject`, or read it right after `apply()` into `@State`).

**Strings (English):** `Another app already uses this shortcut.`, `macOS already uses this shortcut.`, `Fusebox already uses this shortcut.` (existing: `Press keys…`, `Off`, `Esc cancels, ⌫ turns it off. Use ⌘, ⌥ or ⌃.`, `Works from any app.`)

### Edge cases
- Disabled system shortcuts (`kHISymbolicHotKeyEnabled == false`) do not count.
- Global shortcuts stored in a keyboard layout other than US: compare key codes, not characters.
- A conflict must not block saving a choice the user insists on (Windows keeps it and warns): for registration failure keep the stored value and warn; for the two *refusals* above do not store.
- Never register while the Settings window is recording (suspend), and always unsuspend in `.onDisappear` (already calls `stop()`).

### Done when
- Selftest: `keyName` table (F5, Space, ←, "a" → "A", a private-use scalar → nil); a pure `Hotkey.conflicts(code:mods:, system: [(Int, Int, Bool)], menu: [(Int, Int)])` for system/menu clashes.
- Manual: record ⌘Space → refused with the macOS message; record ⌃⌥⌘F5 → label `⌃⌥⌘F5`; pressing the current shortcut while recording does not toggle the window; reserve ⌃⌥⌘F in another app first (e.g. a second copy of a Carbon-hotkey tool) and see whether the warning appears (verify, see above).

---

## Suggested order and size

| # | Feature | Size | Why this place |
|---|---|---|---|
| 1 | 5 Update rollback | M | Protects every later release; self-contained in `Updater.swift`/`App.swift`. |
| 2 | 1 Startup items on/off | M | Biggest visible gain; reworks `StartupItem`/`StartupSection` only. |
| 3 | 3a Panics + 3b shutdowns (+3c strip) | M | Extends `Diag`; 3a is S, 3b is the slow part. |
| 4 | 2 Updates / restart needed | M | New card + tip; reuses the Software Update URL also used in 3a. |
| 5 | 6b Hotkey recorder | S | Isolated in `Sneltoets.swift`. |
| 6 | 4 SSD details | S | Small addition to `SSDHealth.parse`. |
| 7 | 6a Encrypted DNS | S | Last: may end as "not reliably detectable". |

## All (verify) items in one place
1. `launchctl disable/enable gui/<uid>/<label>` + `print-disabled` work as a normal user on macOS 26, and the output format.
2. Optional `SessionLoginItems.sfl3` still lists Login Items on macOS 26.
3. `RecommendedUpdates` in `com.apple.SoftwareUpdate` stays maintained; `softwareupdate -l` output format; `/Library/Updates/` readable and meaningful for "downloaded"; product key/identifier format.
4. Panic file names / `bug_type 210` / `ResetCounter-*.diag` on Apple Silicon macOS 26; `/Library/Logs/DiagnosticReports` readable without admin.
5. `log show` retention for `Previous shutdown cause`, speed, `--last boot`; the cause-code table.
6. NVMe SMART log offsets for the details line.
7. Watchdog `pgrep` pattern; `Installer.isInApplications` semantics.
8. Encrypted-DNS signal in `scutil --dns` / SCDynamicStore.
9. `RegisterEventHotKey` conflict status with other apps; `CopySymbolicHotKeys()` on macOS 26.
