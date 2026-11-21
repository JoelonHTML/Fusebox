# Working on Fusebox for Windows (read first)

Fusebox is a very light system monitor (Clear · Light · Live). The Mac app (Swift, `Sources/`) is the reference for behaviour and wording; **never edit anything outside `windows/` and `.github/workflows/windows.yml`.**
Plan and API choices: `windows/PLAN.md` (Dutch). Windows 11 only, x64 + arm64, .NET 10, WinUI 3 unpackaged, Native AOT.

## Layout
- `src/Fusebox.Core` (net10.0): pure logic, no Windows APIs. Parsers, scoring, formatting, history. **Unit-test everything here** in `tests/Fusebox.Tests` (xunit v3).
- `src/Fusebox.Platform` (net10.0-windows): Windows readers via `[LibraryImport]` P/Invoke (AOT-safe; no `DllImport` with runtime marshalling, no `System.Management`/WMI classes, no reflection-based JSON — use `JsonNode` or a `JsonSerializerContext`). Shared: `Native.cs`, `Pdh.cs` (PdhQuery: Add/Collect/Value/Values), `Elevated.cs` (admin-only one-off tasks: register in `Elevated.Tasks`, call `Elevated.RequestAsync`). For rare WMI/CIM data use `powershell -NoProfile -Command "Get-CimInstance … | ConvertTo-Json"`, run on demand only.
- `src/Fusebox.App` (WinUI 3): pages in `Pages/`, `LiveHub` (the 2 s clock; `Updated` fires only while someone looks), `Prefs` (JSON settings), `Loc.Apply(this)` translates XAML text, `Notify.Send`, `Controls/Sparkline`, theme keys in `Themes/Fusebox.xaml` (use `{StaticResource FuseboxCard}` for cards, `FuseboxGood/Warning/Bad`, `FuseboxGraphStroke`). Use **x:Bind or code-behind only, never `{Binding}`** (AOT). Mark classes `partial` (CsWinRT AOT).
- `tools/Fusebox.Probe`: console self-test run in CI on real Windows; add a check for each reader you write (print values in the JSON, fail only for things every Windows PC has — CI runners are VMs without battery, GPU, Wi-Fi or Bluetooth).

## Rules
- Light: no constant animations, no polling while hidden, work only when the page is visible (subscribe in OnNavigatedTo, unsubscribe in OnNavigatedFrom), heavy work on a background thread, nothing per-frame.
- Text: write English in XAML/code; code uses `Strings.L("English text %@", arg)` with Mac printf placeholders (%@, %lld, %.1f). Reuse the Mac's exact English wording where the feature is the same (grep `Sources/*.swift` for `L("` / `Text("`), so the existing Mac translations in `Resources/*.lproj` apply. Windows-only wording: just write English; translations come later. Windows terms: Settings, Task Manager, system tray, Startup apps, PC (not Mac).
- No new NuGet packages without a strong reason. Small, boring code. One unit test per non-trivial parser/logic.
- Build check on Linux (no WinUI there): `export PATH=/root/.dotnet:$PATH; cd windows && dotnet test tests/Fusebox.Tests && dotnet build tools/Fusebox.Probe`. The WinUI project only builds on Windows (CI), so be extra careful with XAML: valid WinUI 3 (Microsoft.UI.Xaml) syntax, matching x:Name fields, `xmlns:c="using:Fusebox.App.Controls"`.
- C# type check of the WinUI app on Linux (catches compile errors before CI; XAML itself is only checked on Windows): `windows/tools/linux-check/check.sh`.
- Interactive stdin commands hang the shell: never run `cat > file` without a heredoc.
- No personal data (names, emails) in code.
