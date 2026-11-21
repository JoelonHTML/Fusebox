<h1 align="center">Fusebox for Windows (beta)</h1>

**Clear · Light · Live.** A Windows activity monitor: CPU, GPU, RAM, internet, SSD and battery, which apps use the most, a checkup, and advice. The Windows sibling of the Mac app.

> **Beta.** Fusebox for Windows is still in development. Expect rough edges; updates arrive automatically.

**Runs on:** Windows 11 (x64 or Arm).

## Install

1. Go to the [Releases page](https://github.com/JoelonHTML/Fusebox/releases) and pick the latest release whose name starts with **Fusebox for Windows**.
2. Download **`Fusebox-Windows-x64.zip`** (most PCs) or **`Fusebox-Windows-arm64.zip`** (Snapdragon and other Arm PCs).
3. Unzip it and open **`Fusebox.exe`**.

Fusebox isn't code-signed yet, so Windows SmartScreen says *"Windows protected your PC"*. Click **More info**, then **Run anyway**. You only need to do this once.

After the first start, Fusebox offers to move itself to your Programs folder (`%LOCALAPPDATA%\Programs\Fusebox`) and adds a Start menu shortcut. You don't need administrator rights for this.

## Update

Fusebox updates itself: it checks GitHub every 15 minutes and installs new versions, but only ones signed by its maker. Your settings and history stay. In Low impact mode it checks once an hour.

## Use

- **Live:** CPU (total and per core, performance and efficiency cores), GPU, memory, network, SSD and battery as cards with graphs. Click a card for its history (5 min, 1 hour, today; min, average and max).
- **Processes:** apps grouped by window, with CPU, memory and GPU use. Click an app for its own page: history, publisher and install date. You can stop an app from there. Search finds running and installed apps.
- **Checkup:** why the PC feels slow, a health score, app crashes, startup apps, large folders, battery condition, sleep and wake events, and tips with links to the matching Settings page.
- **Network:** every device on your network (name, IP, maker, latency), speed test (only when you start it), a "Find the problem" check for gateway, DNS, internet and Wi-Fi, and the Wi-Fi signal.
- **Docked:** USB devices and hubs with their speed now and at most, displays (resolution, refresh rate, connection), Bluetooth battery levels and the headphone jack.
- **Settings:** style (Glass, Minimal light, Color blocks, Instrument, Terminal), light, dark or system theme, which measurements you see, notifications, Low impact mode, and a shortcut (Ctrl+Alt+Win+F) to show or hide Fusebox.
- **System tray icon:** a small panel with the key numbers, next to the clock. The icon can show CPU %.

Some things ask for administrator approval, once each time: **SSD health** (Checkup) and **what keeps the PC awake** (Checkup). Fusebox then asks Windows for elevation (UAC) to run one small PowerShell script from Windows itself (never Fusebox's own files) and that's it. It runs as a normal user the rest of the time.

**Wi-Fi name:** since Windows 11 24H2, apps need location access to read the name of the Wi-Fi network. Without it, Fusebox still shows the signal, with a button to open the location settings.

## Differences from the Mac version

- **No CPU temperature and no fan control or fan speed.** Windows has no public API for them, and the usual tools need a kernel driver that Windows Defender blocks. Only the GPU and SSD temperatures are shown.
- **No charger, cable or USB-C Power Delivery details.** Windows doesn't expose the negotiated wattage or the cable's e-marker. Fusebox shows the battery's charging power and whether the PC is on mains power.
- **No "memory pressure".** Windows has no equivalent. Fusebox shows commit charge and compressed memory instead, with an explanation.
- **No energy impact score per app.** Fusebox estimates it from CPU and GPU time, the way Task Manager does.
- **Network use per app** and **what keeps the PC awake** need administrator approval.
- **Mica or Acrylic instead of Liquid Glass** for the Glass style.

## Privacy

Measurements are saved in `Documents\Fusebox` as CSV (one small file per day). Nothing goes to the internet, except a short connection to measure latency, checking GitHub for updates, and a speed test only when you start one. You choose the latency address yourself in Settings.

---

## For the maker: releasing

### One-time: create the signing key

Updates are signed with an ECDSA P-256 key. The private key goes into a secret of a GitHub Environment named **`release`**; the public key is built into the app. Do this once, on a Mac with openssl.

1. **Create the private key** and keep it out of the repo:

   ```bash
   openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out fusebox-win.pem
   openssl pkcs8 -topk8 -nocrypt -in fusebox-win.pem -outform DER | base64
   ```

   First create the Environment (GitHub › Settings › Environments › New environment, name **`release`**) with yourself as **required reviewer**. Copy the output into an **Environment secret** named **`FUSEBOX_WIN_SIGNING_KEY`** in that Environment (not a repository secret, so no other workflow or branch can read it). Each release run then waits for your approval.

2. **Get the public key** and put it in `ReleaseKey` in `windows/src/Fusebox.Core/Update/Signing.cs`, replacing the development key that is there now:

   ```bash
   openssl pkey -in fusebox-win.pem -pubout -outform DER | base64
   ```

3. **Keep `fusebox-win.pem` private**, for example in Keychain or a password manager. Never commit it.

Alternative with .NET, which prints both keys: `dotnet run --project windows/tools/Fusebox.Sign keygen`.

Releases only run from the `windows` branch. Until `ReleaseKey` matches the secret, the release job fails at its verify step.

### Each release

1. Set the version in `windows/Directory.Build.props` (`<Version>`), and commit and push to the `windows` branch (later `main`).
2. Go to **Actions › Windows › Run workflow**, choose the branch, set **release** to true, and write the **notes** as bullets:

   ```
   - Live: faster graphs
   - Network: Wi-Fi name shown after location access
   ```

   Each `- Topic: what` bullet appears in the update notification.
3. The release job signs both zips, checks the signatures against the built-in key, and publishes **`Fusebox for Windows <version> beta`**.

Tags look like **`win-v<version>-beta-<build>`**, where the build number is the UTC time of the release (`yyyyMMddHHmm`). Releases are never marked "latest", so the Mac app, which reads "latest", isn't affected.

---

## Building

CI builds on GitHub Actions for both architectures (`windows-latest` for x64, `windows-11-arm` for Arm). The workflow runs the unit tests, a probe on a real Windows machine, a smoke test, and then publishes the zips as artifacts.

To build locally on Windows (with the .NET 10 SDK):

```powershell
dotnet publish windows/src/Fusebox.App -c Release -r win-x64 -p:Platform=x64
```

Use `-r win-arm64 -p:Platform=arm64` on an Arm PC.
