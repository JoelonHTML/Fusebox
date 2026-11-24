# Fusebox voor Windows — plan

Status: v1 (§1–6) is gebouwd en draait groen in CI. **§7 = ontwerp v2 op basis van `docs/FEATURES.md`, goedgekeurd (§7.8).** Beta 0.1.0 (v1) en 0.2.0 (v2) zijn uitgebracht. De Mac-app (`Sources/`, `Helper/`, `Resources/`, `build.sh`, `release.sh`, `share.sh`) blijft onaangeroerd; alles voor Windows staat in `windows/` en `.github/workflows/windows.yml`.

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
- Doelplatformen: **alleen Windows 11**, **alleen x64** (arm64 vervallen, besluit Joël 22 nov.: "arm hoeft niet, supporten we niet").

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

## 6. Besluiten (21 nov. 2026)

1. **Techniek:** .NET 10 + WinUI 3, unpackaged, self-contained, Native AOT, x64 + arm64. ✔
2. **Releases:** zelfde repo, tags `win-v…`, assets `Fusebox-Windows-<arch>.zip`, altijd `--latest=false`, plus de éénregelige Mac-wijziging in `parseReleases` (Windows-releases niet in de Mac-changelog). ✔
3. **Update-handtekening:** automatisch in CI (optie A), ECDSA P-256, eigen Windows-sleutel als secret. ✔
4. **Alleen Windows 11** (build 22000+). ✔
5. Alles in één keer bouwen (0.1 t/m 0.6), met agents.

---

## 7. Ontwerp v2 — op basis van `docs/FEATURES.md`

Aanleiding: v1 werkt (alle tabs, CI groen, ~105 MB, 0,2–0,6 % van één kern in rust, langste UI-hapering ≤ 18 ms), maar oogt als een technische demo, niet als Fusebox. De lat is de Mac-versie. Ik heb de volledige spec laten samenvatten en naast de Windows-code gelegd; hieronder per onderdeel wat ik **houd**, **verbeter** en **anders doe op Windows**.

### 7.1 Wat blijft (fundament is goed)
- Techniek: .NET 10 + WinUI 3, Native AOT, unpackaged, x64 + arm64, alleen Windows 11.
- Alle meet-code (Platform-laag): CPU/GPU/RAM/net/schijf/batterij, processen, Event Log, USB-boom, beeldschermen, Bluetooth, wifi, LAN-scan, snelheidstest. Die is op echte Windows getest (probe in CI).
- CI met rooktest per pagina, screenshots, idle-CPU en UI-hapering; updater + ECDSA-handtekening; vertalingen via de Mac-`.strings`.

### 7.2 Wat vervalt of wordt herbouwd (de UI-laag)
Vrijwel alle schermen worden opnieuw opgezet volgens de spec. Concreet de grootste verschillen met v1:
1. **Venster:** v1 heeft een zijbalk en opent op 1100×720. Wordt: **tabs bovenin, in de titelbalk** (Live · Processes · Checkup · Network · Docked + tandwiel), compact standaardvenster (300×340 zoals de Mac), twee onthouden maten (Live / rest), minimummaat per tab, Ctrl+1…5 en Ctrl+,. Mica-achtergrond, inhoud loopt achter de titelbalk door.
2. **Thema's:** v1 heeft 5 zelfverzonnen stijlen. Wordt: de **6 Mac-thema's met exacte kleuren** (Instrument standaard, Terminal, Minimal light, Glass, Color blocks, Liquid Glass = Mica), dezelfde opgeslagen ids, licht/donker-omzetting, accentcorrectie (WCAG), rij-stijlen (lijnen/plain/cards/blocks/glass), tekstgrootte 0,8–1,6, eigen accentkleur.
3. **Live:** v1 heeft één soort grote kaart met scrollen. Wordt: het **XS/S/M/L/XL-raster** van de Mac (op breedte én hoogte, nooit scrollen), kaart-anatomie met vaste cijferbreedte (geen springende cijfers), drempelkleuren, piek-stip, grafiek met vlakvulling en zacht meeglijden, detailscherm met 5 min / 1 uur / vandaag.
4. **Data-fundament:** v1 kijkt alleen naar het geheugen van de app. Wordt: **minuutlog per app** (`apps-*.csv`), pieken-CSV en meet-CSV met exact de Mac-kolommen. Daardoor kunnen Checkup, de app-pagina en pieken terugkijken (vandaag / gisteren / 7 dagen).
5. **Instellingen:** v1 is één lange pagina. Wordt: **apart venster zoals Windows-Instellingen** (zijbalk + nette rijen met titel/uitleg/schakelaar): Profiel (4 profielen, delen/importeren, alles terugzetten), Uiterlijk, Live, Processen, Checkup, Netwerk, Docked, Systeemvak, Meldingen, Batterij, Privacy, Over.
6. **Meldingen:** v1 meldt alleen updates. Wordt: de 7 Mac-meldingen (CPU, hitte, geheugen, opslag, internet, batterij, app die blijft slurpen) + "houdt je CPU bezig" met knoppen *Stop app* / *Toon*, met afkoeltijd.
7. **Systeemvak:** v1 is een kaal venstertje bij de muis. Wordt een **flyout zoals Snelle instellingen**: verankerd aan het icoon (niet de muis), score + reden, live-regels met mini-grafiek, één tip, vijf snelknoppen; icoon toont desgewenst cijfers/balkjes; rechtsklikmenu met status en zwaarste apps.
8. **Instelhulp:** 12 Mac-stappen, op Windows ingekort tot ±9 (geen Mac-rechten), met voorbeeld-tegels, stappenteller, "Instelhulp overslaan", annuleren zet alles terug.

### 7.3 Per tab: houden / verbeteren / anders op Windows
| Tab | Houden (Mac) | Verbeteren | Anders op Windows |
|---|---|---|---|
| **Live** | Raster XS–XL, kaart-anatomie, drempels, grafiek, detail, pieken | Cijfers met tabular-cijfers (Segoe UI Variable Display), grafieken alleen hertekenen bij nieuwe data | Geen temperatuur/ventilator (geen API); P/E-kernen alleen tonen op hybride CPU's |
| **Processes** | Eén rij per app, chips Alles/Apps/Achtergrond/Windows, kolommen per breedte, uitklaprij, splitpaneel ≥ 1100, kleine items verbergen, "Windows zelf"-rij, app-pagina met geschiedenis per uur/dag | Stop-kruisje pas bij hover/focus; zoeken toont "Actief" + "Apps" | **Efficiëntiemodus per app** als zachtere knop naast Stop; Energie-kolom standaard uit (Windows geeft geen watt per proces) |
| **Checkup** | Score 1–10 + reden + vergelijking, periode Vandaag/Gisteren/7 dagen, 5 inklapbare groepen, "Waarom is mijn pc traag?" (max 2 zinnen + Stop-knop), Top 5, pieken, drukke momenten, crashes, opstart-items, schijfruimte met oordeel, SSD-gezondheid/snelheid, batterij, rapport delen (geschoond) | Werkt **op dag 1** door Windows' eigen geschiedenis (Betrouwbaarheidsmonitor, Event Log, batteryreport) | Opstart-items uit Run-sleutels + Opstartmap + **Taakplanner + services**; **Windows Update / herstart nodig**; **sleepstudy** per slaapsessie; OneDrive als achtergrondtaak; geen ventilatoren/laadlimiet |
| **Network** | Internetpaneel met oordeel en live-grafiek, "Vind het probleem", snelheidstest Snel/Volledig met geschiedenisbalkjes, publiek IP op klik, apparatenlijst met kolommen per breedte, onthouden/Nieuw/offline, hernoemen/vergeten, Wake on LAN | Meteren-waarschuwing via Windows' "verbinding met datalimiet" | **Netwerkprofiel (openbaar/privé)**, **VPN**, **versleutelde DNS**; geen wifinaam/BSSID; MAC-adres alleen in de LAN-scanner en **standaard verborgen** |
| **Docked** | Kopkaart (stroom in, batterij), poortboom met lijnen, getal rechts "nu / max", detailpaneel, waarschuwingen (te traag, Hz/resolutie), draadloos-blok, hubs tonen/verbergen | Vernieuwen op apparaat-events + 5 s alleen zolang zichtbaar | Geen USB-C PD/lader-watt/e-marker (Windows geeft het niet): stil weglaten, geen lege rijen; nooit serienummers |

### 7.4 Verbeteringen ten opzichte van de Mac-versie
1. **Checkup is nuttig vanaf de eerste start**: Windows bewaart zelf al weken geschiedenis (betrouwbaarheid, crashes, batterijcapaciteit), dus geen "kom over een paar dagen terug".
2. **Opstart-items compleet**: ook taken uit Taakplanner en services, met de echte aan/uit-status zoals Taakbeheer, en (optioneel) uitzetten met één omkeerbare schakelaar.
3. **Efficiëntiemodus per app** als vriendelijk alternatief voor Stop: de app blijft open maar mag minder stroom gebruiken.
4. **Slaapverbruik per sessie** via sleepstudy (Modern Standby): precies zien welke nacht de batterij leegliep en waardoor.
5. **Netwerk-extra's**: openbaar/privé-profiel, VPN actief, versleutelde DNS en verbinding met datalimiet, in gewone taal.
6. **Systeemvak-flyout zoals Snelle instellingen**, volledig met toetsenbord te bedienen en met Verteller-labels.
7. **Toegankelijk en eerlijk**: volgt Windows' tekstgrootte, "animaties uit" en hoog contrast; lege toestanden in gewone taal i.p.v. lege lijsten.
8. **Zuinigheid bewaakt door CI**: elke build meet idle-CPU en UI-haperingen; boven een grens (bijv. 1 % CPU of 50 ms hapering) faalt de build. Plus Windows' Efficiëntiemodus voor Fusebox zelf in Zuinig.
9. **Privacy strenger dan de Mac-code**: geen wifinaam, MAC alleen verborgen-standaard in de scanner, rapporten automatisch geschoond (gebruikersnaam, pc-naam, paden, IP's).

### 7.5 Privacy-fouten in v1 die ik als eerste herstel
- Netwerk toont nu de **wifinaam** en vraagt daarvoor locatietoegang → weg (de Mac toont die nooit).
- Netwerk toont het **MAC-adres** in het detailpaneel en zoekt erop → alleen in de scanner, standaard verborgen, met schakelaar.
- Er is nog geen **rapport-schoonmaker** → komt vóór "Rapport delen".

### 7.6 Volgorde (één doorlopende bouw, met screenshots per stap in CI)
1. Instellingentabel (alle Mac-sleutels + standaardwaarden) + thema-tokens + opmaakregels (Core, met tests).
2. Meten + geschiedenis + CSV's (meet-, app-, pieken-, batterijlog) in Mac-formaat.
3. Vensterschil: tabs in titelbalk, maten, plaatsing.
4. Live → systeemvak-flyout → Processes → Checkup → Network → Docked.
5. Meldingen, instelhulp, Instellingen-venster.
6. Zuinig-controle: geen animatie en geen hertekenen als verborgen/geminimaliseerd/scherm uit; CI-grenzen aan.

### 7.7 Beslissingen voor Joël
1. **Instellingen**: apart venster zoals Windows-Instellingen (mijn advies) of een pagina in het hoofdvenster?
2. **MAC-adres in de scanner**: standaard verborgen met schakelaar (advies), of helemaal nooit?
3. **Opstart-items**: alleen tonen (zoals Mac) of ook uitzetten zoals Taakbeheer (advies: uitzetten mag, omkeerbaar)?
4. **Lettertype**: Segoe UI Variable overal (advies, voelt het meest Windows), of een afgerond lettertype meeleveren voor Instrument/Glass/Color blocks/Liquid Glass?
5. **Sneltoets**: Ctrl+Alt+F (advies; Win-combinaties botsen vaak met Windows zelf) of Ctrl+Alt+Win+F?

### 7.8 Besluiten Joël (21 nov. 2026) — bij twijfel: doe wat de Mac doet
1. **Instellingen = eigen venster** (zijbalk + rijen zoals Windows-Instellingen; Mac: 740×560, 13 tabs).
2. **MAC-adres standaard zichtbaar in de LAN-scanner**, te verbergen (pref `netHide` bevat `mac`). Nergens anders: niet in Docked, logs, rapporten of export. Nooit serienummers, nooit wifinaam/BSSID.
3. **Opstart-items mogen uit/aan** (omkeerbaar, zoals Taakbeheer via `StartupApproved`; items die beheerder vragen via de verhoogde PowerShell-taak).
4. **Lettertypes zoals de Mac** (7 ids, zelfde opgeslagen waarden): `style` = past bij thema, `sfPro` → Segoe UI Variable, `rounded` → meegeleverd afgerond lettertype (OFL, bijv. Nunito), `mono` → Cascadia Mono (fallback Consolas), `serif` → Georgia, `montserrat` en `ubuntu` meegeleverd. Thema's met design "rounded" gebruiken het afgeronde lettertype.
5. **Sneltoets Ctrl+Alt+F** (Mac ⌃⌥⌘F zonder Win-toets), te wijzigen met een recorder.

### 7.9 Stand na v2 (22 nov. 2026)
- Uitgebracht: **0.2.0 beta** (`win-v0.2.0-beta-202611220044`). CI groen op x64 + arm64, incl. rondgang door alle pagina's (klein/licht en breed/donker), Instellingen › Uiterlijk en de systeemvak-flyout.
- Gevonden en opgelost met bewijs uit CI: crash Processen (Parent null), crash Checkup (niet-partial subklassen onder AOT), flyout InvalidCastException (presenter-cast), flyout rechts afgekapt (buiten- vs binnenmaat), "Alle logs verwijderen" wiste ook eigen CSV's van de gebruiker, MAC van de gateway in de openbare CI-log, Netwerk bleef pingen als het venster verborgen was.
- Nog open (klein): lege ruimte onderin de flyout; pagina's renderen door als alleen de flyout open is; resultaatbestand van de beheerdertaak staat in een map van de gebruiker (laag risico); twee dialogen tegelijk kan een fout geven; app-namen met regeleinde in peaks.csv.
- Alleen op een echte pc te testen: batterij, wifi, Bluetooth, USB, beheerdertaken, meldingen en de automatische update 0.1.0 → 0.2.0.

### 7.10 Stand en vergelijking met de Mac (24 nov. 2026, Mac 0.13.2 ↔ Windows 0.2.5)
- Uitgebracht t/m **0.2.5 beta**, alleen x64. Mac-main t/m 0.13.2 is in de `windows`-branch gemerged.
- Mac 0.13.1/0.13.2 nagelopen:
  - Venster volgt de muis naar een ander scherm: Windows sleept zelf (SetTitleBar), dus de AppKit-fout speelt hier niet. Niet te testen in CI (één scherm).
  - Positie bewaren: gebeurde op Windows al pas na loslaten (WM_EXITSIZEMOVE).
  - Openen op het bewaarde scherm: deed Windows al; **fix**: eerst naar dat scherm, dan de maat met díe schaal (100 % ↔ 200 %).
  - Crash GPU-namen (gedeelde dictionary, twee threads): op Windows is die map lokaal, dus geen tweeling. Audit van alle gedeelde staat gaf geen crash-race. **Fixes**: CSV-schrijver met een slot (laatste minuut kon wegvallen bij afsluiten), Reliability-cache met een slot, app-beschrijving leest het bestand buiten het slot (UI wachtte). Latent, niet gefixt: `Notify.handlers` (wordt nu alleen bij start gevuld).

**Windows mist nog (kan wel):**
- Efficiëntiemodus heeft Windows al; de Mac-variant per app is gelijk. Niets te doen.
- "Transparantie-effecten uit" van Windows volgen (Mac: Reduce transparency) [S]
- Wifi-signaal zonder locatietoestemming (24H2): nu "geen signaalinformatie" als Windows de BSSID weigert; het signaalpercentage kan zonder BSSID [S] (?)
- Klassiek menu als alternatief voor het systeemvak-paneel (Mac `richPanel` uit); nu alleen het rechtsklikmenu [S] (?)
- Opruimknoppen bij opslag (Prullenbak legen / Schijfopruiming openen) als de Mac die heeft [S] (?)
- Netwerkgebruik per app [M] (?: nagaan of de Mac dit toont)

**Windows heeft, de Mac niet:**
- Opstart-apps aan/uit (Run, Opstartmap, Taakplanner, services) met terugzetten
- Windows Update / "herstart nodig", OneDrive als achtergrondtaak
- Logboek en Betrouwbaarheidsmonitor: Checkup werkt op dag één
- SSD-gezondheid en "wat houdt de pc wakker" via een eenmalige beheerdertaak
- Netwerkprofiel (openbaar/privé) en versleutelde DNS
- Vermelding in Apps en onderdelen + verwijderen; update-terugdraaien als de nieuwe versie niet start
- Sneltoets-recorder met conflictcontrole

**Kan niet of is niet zinvol op Windows:**
- CPU-temperatuur en ventilatoren: vergt een kerneldriver (Defender blokkeert); ventilatorcurves, stofdetectie en de fan-helper vervallen
- Geheugendruk bestaat niet op Windows (wel commit en gecomprimeerd geheugen)
- Energie per app in watt: Windows geeft het niet
- Laadlimiet: geen merkonafhankelijke Windows-API
- Wifi-naam/BSSID: bewust nooit getoond (privacy); ruisniveau/SNR geeft Windows niet
- USB-C-vermogen, lader en kabel: Windows geeft het niet
- Mac-vensterfixes, Liquid Glass (→ Mica/Acrylic), Dock-icoon (→ taakbalkknop), Sparkle (→ eigen updater met SHA-256)
