# Fusebox voor Windows — plan

Status: **voorstel, wacht op akkoord.** Er is nog geen code geschreven. De Mac-app (`Sources/`, `Helper/`, `Resources/`, `build.sh`, `release.sh`, `share.sh`) blijft onaangeroerd; alles voor Windows staat in `windows/` en `.github/workflows/windows.yml`.

Doel: dezelfde Fusebox (Clear · Light · Live) als native Windows 11-app, net zo zuinig als de Mac-versie: meten elke 2 s, geen doorlopende animaties, niets doen als het venster verborgen is.

---

## 1. Techniekkeuze

| | **C# / .NET 10 + WinUI 3** (aanbevolen) | Rust + Tauri | C++ / Win32 (of C++/WinRT + WinUI) |
|---|---|---|---|
| Uiterlijk | Echt Fluent: Mica/Acrylic, systeemlettertype, licht/donker, accentkleur. Voelt als Taakbeheer/Instellingen. | Webpagina in WebView2. Mica kan, maar knoppen/lijsten zijn HTML; voelt "web". | Win32: oud uiterlijk tenzij je alles zelf tekent. C++ met WinUI: zelfde look als C#, maar veel trager te bouwen. |
| Geheugen (schatting) | ~50–90 MB | ~100–150 MB (WebView2-processen tellen mee) | ~10–30 MB (Win32), ~50 MB (WinUI) |
| CPU in rust | Laag, als we geen timers laten lopen terwijl het venster dicht is | WebView2 heeft eigen processen en timers | Laagst |
| Systeem-API's | P/Invoke naar PDH, IP Helper, SetupAPI, WLAN, Event Log; WinRT direct bruikbaar | Alles via Rust-crates of FFI; prima, maar UI-laag is JS | Alles direct |
| Bouwsnelheid / onderhoud | Snel; goede tooling; unit-tests met `dotnet test` op GitHub Actions | Twee talen (Rust + TS) | Traag, foutgevoelig |
| Installatie | Unpackaged + self-contained: één map, geen .NET-installatie nodig. Met Native AOT ~30–50 MB zip. | Kleine exe (~10 MB) maar WebView2 moet aanwezig zijn (op Win 11 standaard) | Klein |

**Aanbeveling: C# op .NET 10 (LTS) + WinUI 3 (Windows App SDK, nieuwste stabiele versie), unpackaged, self-contained, Native AOT.**

- Het is de Windows-tegenhanger van SwiftUI: native Fluent-look, en **Mica/Acrylic** is wat bij ons het "Glass"-thema (Liquid Glass) wordt.
- Let op: **.NET 8 is sinds 10 november 2026 niet meer ondersteund**, daarom .NET 10 (LTS tot nov. 2028).
- Native AOT (ondersteund door WinUI 3 sinds Windows App SDK 1.6): snellere start, minder geheugen, geen JIT.
- Unpackaged (geen MSIX): installeren = map uitpakken in `%LOCALAPPDATA%\Programs\Fusebox`, geen beheerdersrechten, eenvoudig zelf bijwerken (zoals de Mac-zip).
- Doelplatformen: Windows 11 (en Windows 10 22H2 waar het "gratis" werkt), **x64 en arm64** (Snapdragon-laptops: de Windows-tegenhanger van Apple Silicon).

---

## 2. Functies → Windows-API's

Algemene regel (zoals op de Mac): één `Sampler` die elke 2 s één keer alles leest wat zichtbaar is, en stopt als venster én traypaneel dicht zijn. Alleen wat op het scherm staat wordt gemeten. Grafieken tekenen alleen bij nieuwe data.

### Live (CPU · GPU · RAM · NET · SSD · BAT)

| Kaart | API | Opmerking |
|---|---|---|
| CPU totaal + per kern | PDH `\Processor Information(*)\% Processor Utility` | Zelfde getal als Taakbeheer. Alternatief zonder PDH: `NtQuerySystemInformation(SystemProcessorPerformanceInformation)`. |
| P-/E-kernen | `GetLogicalProcessorInformationEx` → `EfficiencyClass` | Intel hybride en Snapdragon; tegenhanger van Apple's P/E-kernen. |
| Kloksnelheid | `CallNtPowerInformation(ProcessorInformation)` of PDH `% Processor Performance` | |
| GPU | PDH `\GPU Engine(*)\Utilization Percentage` (som per engine-type, max = Taakbeheer-getal), `\GPU Adapter Memory(*)\Dedicated/Shared Usage` | Werkt voor alle merken (WDDM 2.0+). |
| GPU-temperatuur | `D3DKMTQueryStatistics` / adapter-perfdata (WDDM 2.4+) | Zelfde bron als Taakbeheer; niet elke driver vult het. |
| RAM | `GlobalMemoryStatusEx`, `GetPerformanceInfo` (commit, cache), PDH `\Memory\*` | "Geheugendruk" bestaat niet op Windows; tegenhanger = commit-belasting + gecomprimeerd geheugen. |
| NET | `GetIfTable2` (`MIB_IF_ROW2` in/out-octets per adapter) | Geen PDH nodig, heel goedkoop. Latency: `IcmpSendEcho2` naar het gekozen adres. |
| SSD | PDH `\PhysicalDisk(*)\Disk Read/Write Bytes/sec`, vrije ruimte via `GetDiskFreeSpaceEx` | |
| BAT | `GetSystemPowerStatus` + `IOCTL_BATTERY_QUERY_INFORMATION/STATUS` | Geeft ontwerp- en volle capaciteit (conditie), cycli, laad-/ontlaadvermogen (mW). Beter en sneller dan WMI `Win32_Battery`. |
| Pieken, min/gem/max, 5 min / 1 uur / vandaag | Eigen ringbuffers + CSV per dag in `Documenten\Fusebox` | Zelfde formaat als de Mac, zodat logica (parsers, pieken) gedeeld getest kan worden. |
| Temperatuur CPU, ventilatoren | **Niet goed mogelijk** — zie §3 | |

### Processen

| Onderdeel | API |
|---|---|
| CPU/RAM per proces | Eén aanroep `NtQuerySystemInformation(SystemProcessInformation)` geeft alle processen met CPU-tijd, werkgeheugen, threads — veel goedkoper dan WMI of een PDH-teller per proces. |
| GPU per proces | PDH `\GPU Engine(pid_*)\Utilization Percentage` (wildcard, één query). |
| Netwerk per proces | Alleen met beheerdersrechten (ETW `Microsoft-Windows-Kernel-Network` of `GetPerTcpConnectionEStats`). Optioneel, net als op de Mac. |
| Apps groeperen | Op venster-eigenaar (`EnumWindows` → PID) + pakket (`GetPackageFullName`) + bovenliggend proces; icoon en naam uit de exe (`SHGetFileInfo`, `FileVersionInfo`). |
| App-pagina | Eigen historie uit de CSV's, uitgever/handtekening via `WinVerifyTrust`, installatiedatum uit Uninstall-register. |
| Stoppen | Eerst netjes (`WM_CLOSE` naar de vensters), na time-out vragen om geforceerd (`TerminateProcess`). Systeemprocessen niet. |
| Zoeken in geïnstalleerde apps | Uninstall-registersleutels + Startmenu-snelkoppelingen + `PackageManager` (Store-apps). |

### Checkup

| Onderdeel | API |
|---|---|
| Gezondheidsscore | Eigen score zoals Mac, plus **Windows Betrouwbaarheidsmonitor**: WMI `Win32_ReliabilityStabilityMetrics` (stabiliteitsindex 1–10) en `Win32_ReliabilityRecords`. |
| Crashes | Event Log (`EvtQuery`): Application ID 1000/1001 (app-crash, WER), System `BugCheck` 1001 (blauw scherm), `Kernel-Power` 41 (onverwacht uit). WER-rapporten in `%ProgramData%\Microsoft\Windows\WER`. |
| Opstart-apps | `HKCU/HKLM\...\Run(Once)`, Opstartmappen, Taakplanner (taken met aanmeld-trigger, via `ITaskService`), automatische services. Aan/uit via `...\Explorer\StartupApproved` (zoals Taakbeheer; HKCU zonder beheerder). |
| Grote mappen | `FindFirstFileEx` met `FIND_FIRST_EX_LARGE_FETCH`, op achtergrond met lage I/O-prioriteit, alleen als de gebruiker het start. |
| SSD-gezondheid | WMI `MSFT_PhysicalDisk` + `MSFT_StorageReliabilityCounter` (slijtage, temperatuur, leesfouten, uren). **Vraagt beheerdersrechten** → op verzoek. |
| Slaap/wakker | Event Log: `Kernel-Power` 42 (slaap), `Power-Troubleshooter` 1 (wakker + wekbron). Modern Standby: `powercfg /sleepstudy` (beheerder). |
| Wat houdt hem wakker | `powercfg /requests` (beheerder) — tegenhanger van "Verhindert sluimerstand". |
| Batterijconditie | Zie BAT; eventueel `powercfg /batteryreport` als extra bron. |
| Tips per categorie | Zelfde categorieën (Prestaties, Batterij & energie, Stabiliteit, Opstart & opslag, Historie), Windows-teksten en links naar `ms-settings:`-pagina's (bijv. `ms-settings:startupapps`, `ms-settings:batterysaver`). |

Beheerdersrechten: de app draait altijd als gewone gebruiker. Voor de paar dingen die beheerder vragen start Fusebox zichzelf éénmalig verhoogd (`Fusebox.exe --elevated <taak>`, UAC-vraag), dat schrijft JSON terug en stopt. Tegenhanger van de Mac-FanHelper, maar zonder blijvende service.

### Netwerk

| Onderdeel | API |
|---|---|
| LAN-scan | Eigen subnet uit `GetAdaptersAddresses`; per IP `SendARP` (geeft MAC, geen beheerder nodig) + `IcmpSendEcho2` (ms), ARP-cache via `GetIpNetTable2`. Namen: reverse DNS, NetBIOS, mDNS (`DnsServiceBrowse`). Maker: hergebruik `Resources/oui.tsv` (wordt bij het bouwen gekopieerd, niet gewijzigd). |
| Elk IP-bereik scannen, zoeken | Zelfde logica als Mac, gedeelde unit-tests. |
| Snelheidstest | Zelfde Cloudflare-endpoints als de Mac (`speed.cloudflare.com/__down`, `__up`, `/meta`), alleen op verzoek. |
| Vind het probleem | Gateway uit `GetIpForwardTable2` → ping; DNS-tijd met `DnsQueryEx` (cache omzeilen); internet-latency; wifi via WLAN API (`WlanQueryInterface`, `WlanGetNetworkBssList`: signaal, kanaal, band, linksnelheid). |
| Wifi-naam | **Windows 11 24H2+ vraagt locatietoestemming** voordat een app de wifinaam/BSSID mag lezen. Zonder toestemming tonen we signaal maar geen naam, met een knop naar `ms-settings:privacy-location`. |

### Docked

| Onderdeel | Wat kan | API |
|---|---|---|
| USB-apparaten + boom (achter een dock) | Ja | SetupAPI / `CM_Get_*` met `DEVPKEY`-eigenschappen, hub-boom via `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX`. |
| Snelheid nu vs. maximaal | Ja (USB 2/3/3.2) | `IOCTL_USB_GET_NODE_CONNECTION_INFORMATION_EX_V2` (SuperSpeed/SuperSpeedPlus ondersteund vs. actief). USB4/Thunderbolt: alleen beperkt (apparaat zichtbaar, snelheid vaak niet). |
| USB-C Power Delivery (watt van de lader, protocol) | **Grotendeels niet** | Windows heeft UCSI-drivers maar geen openbare API voor de PD-afspraak. We tonen wat de batterij-IOCTL geeft (laadvermogen, netstroom ja/nee). |
| Kabel (e-marker) | **Niet** | Geen API. |
| Beeldschermen | Ja | `QueryDisplayConfig` (resolutie, Hz, aansluiting: HDMI/DisplayPort/intern/USB-C via `DISPLAYCONFIG_OUTPUT_TECHNOLOGY`), naam uit EDID, DXGI voor HDR. |
| Bluetooth + batterij | Ja, meestal | PnP-eigenschap `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2` (wat Instellingen › Bluetooth toont), BLE: GATT Battery Service 0x180F via `Windows.Devices.Bluetooth`. |
| Koptelefoonaansluiting | Ja | Core Audio `IMMDeviceEnumerator` + `IKsJackDescription` (ingeplugd, type). |

### Overig

| Mac | Windows |
|---|---|
| Menubalk + paneel | **Systeemvak-icoon** (`Shell_NotifyIcon` via P/Invoke; WinUI heeft dit niet zelf) met een klein randloos Mica-venster naast het icoon als paneel. Icoon toont optioneel een getal (CPU%) — alleen hertekenen als het getal verandert. |
| Meldingen | Windows App SDK `AppNotificationManager` (werkt ook unpackaged). Zelfde regels: één melding per piek/update, schakelaar in Instellingen. |
| Sneltoets ⌃⌥⌘F | `RegisterHotKey`, standaard Ctrl+Alt+Win+F. |
| Starten bij inloggen | `HKCU\...\Run`-waarde, schakelaar in Instelhulp/Instellingen. |
| Thema's | Glass = Mica/Acrylic; Minimal light, Color blocks, Instrument, Terminal als eigen stijlen. Licht/donker/systeem. Namen blijven Engels (glossary). |
| Lettertypes | Segoe UI Variable (systeem, = "SF Pro"-rol), Cascadia Mono (= "SF Mono"), Montserrat en Ubuntu meegeleverd (zelfde bestanden als `Resources/Fonts`, licenties meegekopieerd). |
| Zuinig (low impact) | Meten elke 5 s i.p.v. 2 s, geen grafieken, update-check 1×/uur, en Fusebox zet zichzelf in **Windows Efficiëntiemodus** (EcoQoS via `SetProcessInformation(ProcessPowerThrottling)`). |
| Eigen verbruik | Fusebox toont zijn eigen CPU/RAM, zoals op de Mac. |
| Talen en/nl/de/fr/es | Zie hieronder. |

### Vertalingen

- Op de Mac is de **Engelse tekst de sleutel** (`L("…")` + `Localizable.strings`). Dat houden we: `.resw`-sleutels mogen geen willekeurige tekst bevatten, en `x:Uid`-sleutels zouden betekenen dat elke tekst twee keer beheerd wordt.
- Voorstel: een klein script zet `Resources/*.lproj/Localizable.strings` bij het bouwen om naar een ingebed bestand (de Mac-bestanden worden alleen gelezen). Windows-specifieke teksten (Taakbeheer, systeemvak, "pc" i.p.v. "Mac") staan in `windows/Strings/<taal>.strings` en gaan vóór.
- `.resw` gebruiken we alleen voor wat Windows zelf leest (app-naam, tray-tooltip) — zeg het als je toch volledig `.resw` wilt.
- `docs/translation-glossary.md` krijgt (in `windows/`) een eigen Windows-tabel met Microsoft-termen, gecontroleerd op support.microsoft.com per taal, net als de Apple-tabel. Bijvoorbeeld: Settings = Instellingen/Einstellungen/Paramètres/Configuración, Task Manager = Taakbeheer/Task-Manager/Gestionnaire des tâches/Administrador de tareas, system tray = systeemvak, Startup apps = Opstart-apps, Efficiency mode = Efficiëntiemodus.
- Een check zoals `scripts/strings-check.sh` draait in CI: ontbrekende vertalingen = build faalt.

---

## 3. Wat niet kan of anders is op Windows

- **CPU-temperatuur en ventilatoren:** Windows heeft geen openbare API. Tools als LibreHardwareMonitor gebruiken een kerneldriver (WinRing0) die Windows Defender als kwetsbaar blokkeert. Dat doen we niet. WMI `MSAcpi_ThermalZoneTemperature` vraagt beheer en is vaak nep/vast. → Alleen GPU-temperatuur (en SSD-temperatuur via de beheerdertaak). Geen ventilatorcurve.
- **USB-C PD, lader-watt, kabel-e-marker:** niet uit te lezen (zie Docked).
- **Netwerk per app** en **SSD-gezondheid**, **wat houdt hem wakker**: alleen met een UAC-vraag.
- **Wifinaam:** locatietoestemming nodig (24H2+).
- **"Geheugendruk"** bestaat niet; we tonen commit-belasting en gecomprimeerd geheugen met uitleg.
- **Energie-impact per app** zoals op de Mac bestaat niet als API; benadering via CPU+GPU-tijd (Taakbeheer's "Energieverbruik" is ook een schatting).
- **Uiterlijk:** Liquid Glass ≠ Mica. Mica is subtieler (achtergrondkleur van het bureaublad), Acrylic is de echte "glas"-blur.

---

## 4. Bouwen, testen en uitbrengen

Deze cloudsessie draait op Linux en kan geen Windows-venster starten. Daarom:

- **`.github/workflows/windows.yml`** op `windows-latest`, alleen bij wijzigingen in `windows/**` (of het workflowbestand zelf) en op de branch `windows`:
  1. `dotnet build` (x64 en arm64), 2. `dotnet test` (unit-tests voor alle logica zonder UI: parsers, pieken, score, IP-bereiken, OUI, vertaal-check, update-tag-parser), 3. een **rooktest**: de app start met `--selftest`, meet 3× en schrijft JSON (draait echt op de Windows-runner, dus de API-aanroepen worden getest), 4. inpakken als `Fusebox-Windows-x64.zip` / `-arm64.zip` als artifact.
- Ik kan de UI niet zelf zien. Optie: de workflow maakt een **screenshot** van het venster op de runner en hangt die als artifact aan, zodat ik toch kan controleren hoe het eruitziet.
- Jij (of een vriend met Windows) test de zip uit de Actions-artifacts.

### Releases zonder de Mac-updater in de war te brengen

Wat ik in `Sources/Updater.swift` zag:

1. De Mac vraagt `releases/latest` op. Dat is altijd de **nieuwste release die niet als "latest: false" of prerelease is gemarkeerd**. Als een Windows-release "latest" wordt, vindt de Mac daar geen `Fusebox.zip` → Mac-gebruikers krijgen **geen Mac-updates meer** tot de volgende Mac-release. Ook de installatieregel in de README (`releases/latest/download/Fusebox.zip`) zou dan 404 geven.
2. De Mac-changelog (`releases?per_page=60`) en de "wat is er nieuw"-melding (`releases?per_page=10`) lezen **alle** releases. Windows-releases zouden in de Mac-changelog verschijnen ("Fusebox win v0.1 beta") en Mac-releases uit het lijstje van 60 duwen.
3. Installeren van een Windows-zip op de Mac kan niet (bundle-id, codesign en handtekening worden gecontroleerd). Dat is veilig.

Voorstel:

- Windows-tags `win-v0.1-beta-<bouwnummer>`, bestanden `Fusebox-Windows-x64.zip` (+ `.sig`), nooit `Fusebox.zip`.
- Windows-releases altijd met **`make_latest: false`** publiceren (`gh release create … --latest=false`). Dan blijft `releases/latest` de Mac-release → punt 1 opgelost **zonder Mac-code te wijzigen**.
- Punt 2 kan alleen met een kleine Mac-wijziging, **ter goedkeuring** (niet gedaan): in `parseReleases` één regel `guard !tag.hasPrefix("win-")`. Zonder die regel zien Mac-gebruikers Windows-releases in hun changelog.
- **Alternatief:** Windows-releases in een aparte repo (bijv. `JoelonHTML/Fusebox-Windows`, alleen voor releases; code blijft hier). Dan hoeft er aan de Mac niets te veranderen en is er geen enkele vermenging. Kost één extra repo en een token in Actions.

De Windows-updater (zelfde gedrag als Mac: elke 15 min, 1×/uur bij Zuinig) leest `releases?per_page=30`, neemt de nieuwste met tag `win-`, controleert de handtekening en het bouwnummer, pakt uit naast de oude versie, hernoemt de lopende `Fusebox.exe` naar `.old` (dat mag op Windows) en herstart.

### Handtekening van updates

Zoals op de Mac weigert de app updates zonder geldige handtekening. Twee opties:

- **A (automatisch):** aparte Windows-sleutel (ECDSA P-256, zit standaard in .NET) als GitHub Actions-secret; de workflow tekent bij elke release. Eenvoudig, maar wie schrijfrechten op de repo heeft, kan dan een release uitbrengen.
- **B (veiligst, zoals nu):** de workflow bouwt; jij downloadt de zip op je Mac en tekent met de bestaande `scripts/sign-update.swift` (Ed25519, sleutel in je sleutelhanger). Dan moet de Windows-app Ed25519 kunnen controleren (kleine extra bibliotheek, bijv. NSec, of een eigen implementatie met tests).

### Code signing (SmartScreen)

Zonder certificaat zegt Windows bij de eerste start "Windows heeft uw pc beschermd" → *Meer informatie* → *Toch uitvoeren* (tegenhanger van "Open Anyway" op de Mac). Updates daarna hebben dat niet nodig als we de zip zelf uitpakken (geen "Mark of the Web").

| Optie | Kosten (indicatie) | Opmerking |
|---|---|---|
| Niet ondertekenen | € 0 | Prima voor vrienden-beta, uitleg in README. |
| **Azure Trusted Signing** (Artifact Signing) | ~$10/maand | Werkt vanuit GitHub Actions, geen USB-token. Vereist identiteitscontrole; beschikbaarheid voor particulieren/eenmanszaken in NL eerst controleren. Bouwt SmartScreen-reputatie op. |
| OV-certificaat (Sectigo, DigiCert…) | ~€ 200–500/jaar | Sinds 2023 alleen op hardware-token of cloud-HSM; SmartScreen-waarschuwing verdwijnt pas na reputatie. |
| EV-certificaat | ~€ 300–700/jaar | Geeft geen directe SmartScreen-bonus meer (sinds 2024). Niet de moeite. |
| Microsoft Store (MSIX) | eenmalig ~$19 (particulier) | Geen waarschuwing, Store-updates; maar dan packaged en Store-regels. Later te overwegen. |

Advies: beta **ongetekend**, later Azure Trusted Signing.

---

## 5. Mijlpalen

| Versie | Inhoud | Klaar als |
|---|---|---|
| **0.1** | Projectopzet, CI, Live (CPU/GPU/RAM/NET/SSD/BAT met grafiek en pieken), CSV-log, systeemvak-icoon + paneel, licht/donker + Mica, en/nl/de/fr/es, `--selftest` | CI groen; selftest meet echte waarden op de runner; zip start op een Windows-pc |
| 0.2 | Processen: lijst, groeperen per app, app-pagina, stoppen, zoeken | |
| 0.3 | Checkup: score, crashes, opstart-apps, grote mappen, batterij, slaap/wakker, SSD (beheerdertaak), tips | |
| 0.4 | Netwerk: LAN-scan, snelheidstest, Vind het probleem, wifi | |
| 0.5 | Docked: USB-boom + snelheid, beeldschermen, Bluetooth-batterij, audio-aansluiting | |
| 0.6+ | Instelhulp, alle thema's en lettertypes, meldingen, sneltoets, Zuinig, updater + releases, README voor Windows | |

Elke mijlpaal: unit-tests voor de logica, selftest uitgebreid, CI-artifact om te testen.

---

## 6. Beslissingen die ik van je nodig heb

1. **Techniek:** akkoord met .NET 10 + WinUI 3, unpackaged, Native AOT, x64 + arm64?
2. **Releases:** zelfde repo met `win-`-tags en `--latest=false` (+ eventueel de éénregelige Mac-wijziging voor de changelog), of een aparte release-repo?
3. **Update-handtekening:** A (automatisch in CI) of B (tekenen op je Mac)?
4. **Windows 10:** meenemen waar het vanzelf werkt, of alleen Windows 11?
5. Daarna bouw ik mijlpaal 0.1, de workflow, en open ik een draft-PR "Fusebox for Windows (draft)".
