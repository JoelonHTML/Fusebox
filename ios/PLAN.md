# Fusebox voor iPhone — plan

Status: **versie 0.1 is gebouwd en getest in de simulator** (24 november 2026). Nog niet op een echte iPhone getest. Zie §9.
Nagekeken op 24 november 2026 in de documentatie van Apple (bronnen in §8).
De Mac-app (`Sources/`, `Helper/`, `Resources/`, `build.sh`) blijft zoals hij is; alles voor de iPhone komt in `ios/`.

---

## 0. In het kort

- **Wat het wordt:** Fusebox op zak voor je netwerk. Internet-check, snelheidstest, apparaten op je netwerk, en een klein overzicht van je iPhone zelf.
- **Wat niet kan:** Processes en bijna heel Checkup. Docked alleen in het klein. Een iPhone-app mag niet bij andere apps, temperaturen, de GPU of de poortgegevens.
- **De LAN-scan kan**, maar kaler dan op de Mac: wel namen, IP-adressen, soort apparaat en reactietijd; **geen hardware-adres (MAC) en dus geen merk**.
- **Zonder betaald account** kun je alles uit versie 1 bouwen en op je eigen iPhone gebruiken. De app verloopt dan elke 7 dagen.
- **Voor je vrienden** is een betaald account nodig (TestFlight). Dat lost ook de twee geparkeerde Mac-punten op (notarisatie en widgets).
- **Mijn advies:** begin gratis met netwerk als kern (§2). Betaal pas als de app goed genoeg is om te delen.

---

## 1. Wat kan wel en wat niet

Leeswijzer: **Ja** = werkt zoals op de Mac. **Deels** = werkt, maar met minder. **Nee** = iOS staat het niet toe.

### Network

| Op de Mac | iPhone | Hoe | Wat je verliest |
|---|---|---|---|
| Router- en internet-ping (ms, jitter, verlies, live grafiek) | **Ja** | Dezelfde code als de Mac (gewone ping, met TCP als reserve). Vraagt één keer toestemming "Lokaal netwerk". | Niets. Werkt alleen terwijl de app open is. |
| Oordeel in gewone taal + **Find the problem** | **Deels** | Router, internet en DNS meten gaat hetzelfde. | De wifi-sterkte (dBm, ruis, kanaal) is op iOS niet uit te lezen. Het advies "ga dichter bij de router zitten" kan dus alleen uit de ping naar de router komen, niet uit het signaal. |
| Kaart **Verbinding** (soort, IP, router, DNS, VPN, datalimiet) | **Deels** | Soort (wifi / mobiel / kabel), eigen IP, datalimiet en Weinig data-modus: ja. Router-adres: iOS geeft het niet zomaar; Fusebox vindt het met één ping die maar één stap ver mag (werkt in de simulator). | DNS-servers en VPN zijn niet betrouwbaar te zien. Die regels laten we weg in plaats van te gokken. |
| Kaart **Wi-Fi** (signaal, ruis, snelheid, kanaal, standaard) | **Nee** | iOS geeft apps geen signaalsterkte. Apple: "geen ondersteunde manier, in geen enkele iOS-versie". | De hele kaart. In de plaats komt één regel: de verbindingskwaliteit volgens iOS (goed / matig / minimaal, nieuw in iOS 26). |
| Wifi-naam | n.v.t. | Kan alleen met betaald account plus locatietoestemming. | Niets: Fusebox toont de netwerknaam nergens. |
| **Snelheidstest** Quick en Full (Cloudflare) | **Ja** | Dezelfde code. | De app moet open blijven tijdens de test (10 tot 30 s). |
| Waarschuwing bij datalimiet | **Ja** | Zelfde controle. Op de iPhone belangrijker: een Full-test op mobiel kost veel data. | Niets. |
| Publiek IP (na een klik) | **Ja** | Zelfde. | Niets. |
| **Netwerkrapport (PDF)** | **Ja** | Zelfde opmaakcode. In plaats van "Bewaar als" komt het deelmenu (Mail, WhatsApp, Bestanden). | Het wifi-blok in het rapport. |
| **LAN-scan: apparaten vinden** | **Deels** | Ping-ronde, Bonjour (de 22 vaste diensten uit de Mac-app), poort-proefjes, namen via DNS en NetBIOS per apparaat. | Stille apparaten die niet op ping antwoorden en niets aanbieden blijven onzichtbaar (de Mac ziet ze via de ARP-tabel). Reken op iets minder apparaten dan de Mac vindt. |
| Namen en modellen via UPnP (tv's, speakers) | **Nee** (zonder goedkeuring) | Vraagt het "multicast"-recht: betaald account plus een aanvraag bij Apple (dagen tot weken). | Een deel van de namen en modellen van tv's en speakers. Veel daarvan komt ook via Bonjour binnen. |
| Hardware-adres (MAC) en **merk** | **Nee** | iOS blokkeert dit sinds iOS 11. Zie de kanttekening onder de tabel. | Kolom MAC, kolom merk, de regel "privé-adres", en de merkenlijst (`oui.tsv`) is niet nodig. |
| Soort apparaat raden (telefoon, tv, printer…) | **Deels** | Dezelfde regels op naam en aangeboden diensten. | De regels die op merk leunen. Vaker "Onbekend". |
| Onthouden, **New**, offline, hernoemen, vergeten | **Deels** | De Mac herkent een apparaat aan zijn MAC. De iPhone doet het met het IP-adres (namen bleken per scan te verschillen: "iPad (4)" of "iPad-4"). | Krijgt een apparaat een nieuw IP-adres, dan lijkt het nieuw. |
| Melding "nieuw apparaat" | **Nee** | iOS laat apps niet op vaste tijden op de achtergrond scannen. | Alleen een **New**-label zodra je de app opent. |
| **Poorten checken** (15 bekende poorten) | **Ja** | Zelfde code. | Niets. |
| Eigen bereik scannen (bijv. `192.168.1.0/24`) | **Ja** | Zelfde code, tot 4096 adressen. | Niets. |
| **Wake on LAN** | **Nee** (rechtstreeks) | Vraagt hetzelfde multicast-recht, en het MAC-adres moet je dan zelf intypen. | Kan wel via de Mac (idee 2 in §2). |
| Hint "scan geblokkeerd" | **Ja** | iOS-versie: "Sta Lokaal netwerk toe" met een knop naar Instellingen › Fusebox. | Niets. |

**Kanttekening MAC-adressen.** In iOS 27 lijkt een nieuw recht te bestaan ("Network Topology Observation") waarmee een app de MAC-adressen weer mag zien. Twee makers van andere scanners zeggen dat het op de iPhone werkt. Apple zelf bevestigt het alleen voor de Mac. Ik ga er in dit plan **niet** van uit. Het is wel het eerste wat ik op je iPhone test (mijlpaal 0): als het werkt, komen merk en vaste herkenning terug.

### Live

| Op de Mac | iPhone | Hoe | Wat je verliest |
|---|---|---|---|
| BAT | **Deels** | Percentage, laadt / vol / op accu, Energiebesparingsmodus. | Conditie, laadcycli, watt van de lader, resterende tijd. |
| Temperatuur | **Deels** | iOS geeft alleen vier standen: normaal, warm, heet, kritiek. | Graden. |
| SSD | **Deels** | Vrije en totale opslag. | Lees-/schrijfsnelheid, slijtage. |
| NET | **Deels** | Down en up van de hele iPhone, terwijl de app open is. | Per app. |
| RAM | **Deels** | Totaal geheugen en een grove schatting van wat in gebruik is. Apple noemt "vrij geheugen" op iOS zelf "in wezen betekenisloos". | Geheugendruk, swap, top-apps. Advies: weglaten. |
| CPU | **Onzeker** | Het werkt technisch, maar Apple raadt de gebruikte route af voor apps die je uitbrengt. | Advies: weglaten in versie 1. |
| GPU, FAN, I/O | **Nee** | Geen toegang. | Alles. |
| Pieken, historie, logboek (CSV) | **Nee** | De app draait niet op de achtergrond, dus er is niets om terug te kijken. | Alles behalve de laatste minuten terwijl de app open is. |

### De rest

| Op de Mac | iPhone | Waarom |
|---|---|---|
| **Processes** | **Nee** | Een iPhone-app mag andere apps niet zien. |
| **Checkup** | **Nee** | Crashes, opstartitems, grote mappen, slaap, accuverloop: allemaal afgesloten. Wel mogelijk: een paar tips op de Live-pagina ("opslag bijna vol", "iPhone is warm"). De Checkup van je **Mac** kan wel op de iPhone (idee 2). |
| **Docked** | **Deels** | Wel: laden, waar het geluid heen gaat (koptelefoon, Bluetooth-naam, AirPlay, CarPlay), extern beeldscherm, toetsenbord, muis, gamecontroller (met batterij) en een netwerkadapter. Niet: vermogen van de lader, kabelgegevens, USB-schijven, batterij van oortjes. |
| Menubalk | Anders | Op de iPhone zijn dat widgets (idee 6). |
| De zes thema's | **Ja** | Zelfde kleuren en stijlen. Liquid Glass bestaat ook op iOS 26. |
| Vijf talen | **Ja** | Zelfde vertaalbestanden. Taal kiezen gaat via Instellingen › Fusebox van iOS zelf; geen eigen keuzemenu en geen herstart nodig. |
| Low impact | Anders | Nauwelijks nodig: de app doet niets als hij dicht is. Wel: bij Energiebesparingsmodus minder pings en geen animaties. |
| Ventilatoren, oplaadlimiet, sneltoets, Dock-icoon, altijd bovenop, start bij inloggen | n.v.t. | Bestaat niet op de iPhone. |
| Eigen updater | Anders | Op de iPhone regelt TestFlight de updates. |

---

## 2. Aanbeveling voor versie 1

### Opzet

Drie knoppen onderin: **Network** (startscherm), **Live**, **Instellingen**. Later een vierde: **Mac**.

```
┌──────────────────────────────┐
│ Network                      │
│ ┌──────────────────────────┐ │
│ │ Wi-Fi · kwaliteit goed   │ │
│ │ Router 3 ms  Internet 14 │ │
│ │ ✓ Alles goed             │ │
│ │ ( Find the problem )     │ │
│ └──────────────────────────┘ │
│ ┌──────────────────────────┐ │
│ │ Snelheidstest            │ │
│ │ ↓ 412   ↑ 38   11 ms     │ │
│ │ ( Quick )  ( Full )      │ │
│ └──────────────────────────┘ │
│ Apparaten   12 online  (Scan)│
│  Router        192.168.1.1  2│
│  Woonkamer-tv  192.168.1.52 8│
│  Alex-iPhone   192.168.1.40 –│
│ ──────────────────────────── │
│   Network    Live    ⚙       │
└──────────────────────────────┘
```

Tik op een apparaat: een blad met de details (naam en waar die vandaan komt, IP, soort, diensten, reactietijd, poorten checken, hernoemen, vergeten).

### Wat erin zit

| # | Onderdeel | Waarom |
|---|---|---|
| 1 | Internet-paneel met oordeel en **Find the problem** | Het eerste wat je wilt weten als het wifi hapert. |
| 2 | Snelheidstest Quick en Full, met historie | Werkt ook op 4G/5G. Elke uitslag krijgt het label wifi of mobiel. |
| 3 | LAN-scan met details per apparaat | Jouw wens. Kaler dan de Mac, maar goed bruikbaar. |
| 4 | Netwerkrapport (PDF) delen | Direct naar je provider of helpdesk sturen vanaf je telefoon. |
| 5 | Live: accu, warmte, opslag, netwerk | Vier kaarten, elk met uitleg. Klein, maar het maakt het Fusebox. |
| 6 | De zes thema's en vijf talen | Zelfde uiterlijk als de Mac. |

### Wat eruit blijft, en waarom

| Onderdeel | Waarom niet (nu) |
|---|---|
| Processes, Checkup | Kan niet op iOS. Docked kwam er toch in, kleiner (zie §1). |
| CPU en RAM op Live | Onbetrouwbaar of afgeraden door Apple. Liever vier eerlijke kaarten dan zes halve. |
| MAC, merk, UPnP, Wake on LAN | Geblokkeerd of vraagt goedkeuring van Apple. |
| Meldingen | Zonder achtergrondwerk valt er niets te melden. |
| Koppeling met de Mac | Waardevol, maar vraagt ook werk aan de Mac-app. Eerst de basis. |
| iPad-indeling | De app draait wel op een iPad, maar krijgt nog geen eigen indeling. |

### Waarom netwerk de kern is

- Je telefoon is wat je pakt als het internet raar doet, en je kunt ermee rondlopen.
- Het grootste deel van de netwerkcode van de Mac is zo te hergebruiken (§5).
- Er is geen betaald account voor nodig.
- De rest van Fusebox (processen, historie, poorten) kan op iOS simpelweg niet.

### Ideeën die alleen op de iPhone kunnen, op volgorde van nut

| # | Idee | Wat het is | Account | Werk |
|---|---|---|---|---|
| 1 | **Loop-test** | Loop door je huis terwijl Fusebox de router blijft pingen. Tik per kamer op "Hier". Daarna zie je per kamer: goed, matig of slecht. Vindt dode hoeken zonder signaalmeter. | Gratis | Klein |
| 2 | **Mac** (je Mac op je iPhone) | De Live-cijfers en de Checkup-score van je Mac, thuis op hetzelfde wifi. De Mac kan ook zijn apparatenlijst delen (mét merk) en een Wake on LAN-pakket voor je versturen. Koppelen met een code; staat standaard uit. | Gratis | Groot (ook aan de Mac-app) |
| 3 | **Wifi tegen mobiel** | Vergelijk je laatste tests: "Je wifi is hier 3× sneller dan 5G." | Gratis | Klein |
| 4 | **Opdrachten en Siri** | "Test mijn internet" en "Check mijn verbinding" als opdracht. Een Quick-test past binnen de 30 seconden die iOS geeft. | Gratis | Klein tot middel |
| 5 | **Rapport delen** | Zit al in versie 1 (deelmenu). | Gratis | – |
| 6 | **Widgets** (beginscherm en toegangsscherm) | Laatste snelheidstest, laatste oordeel, later de status van je Mac. Toont de laatst bekende waarde, niet live: iOS ververst een widget 40 tot 70 keer per dag. | Gratis kan, zie §3 | Middel |
| 7 | **Knop in het bedieningspaneel** | Eén tik start de snelheidstest. | Gratis | Klein (na 4) |
| 8 | **Wake on LAN** | Via de Mac (idee 2), of rechtstreeks met goedkeuring van Apple. | Zie §3 | Klein na 2 |
| 9 | Live-activiteit bij een test | Een test duurt 10 tot 30 s met de app open. Voegt weinig toe. | Gratis | **Niet doen** |
| 10 | Melding bij nieuw apparaat | Onbetrouwbaar op de iPhone. Via de Mac zou een eigen server en pushberichten vragen. | Betaald | **Niet doen** |

---

## 3. Wat vraagt een betaald account?

Het Apple Developer Program kost 99 dollar per jaar (in Nederland rond de 99 euro; het precieze bedrag zie je bij aanmelden).

| | Gratis Apple ID | Betaald |
|---|---|---|
| App op je eigen iPhone | Ja, maar **verloopt na 7 dagen**. Dan opnieuw installeren via Xcode. Maximaal 3 apps per toestel. | Ja, 1 jaar geldig. |
| App voor vrienden | **Nee.** | **TestFlight**: uitnodigen per mail of link, updates komen vanzelf. Apple keurt de eerste build van elke versie. Een build werkt 90 dagen. |
| Internet-check, snelheidstest, rapport | Ja | Ja |
| LAN-scan (ping, Bonjour, poorten) | Ja | Ja |
| Thema's en talen | Ja | Ja |
| Koppeling met de Mac op hetzelfde wifi | Ja | Ja |
| Opdrachten, Siri, knop in bedieningspaneel | Ja | Ja |
| Widgets | Technisch ja (het benodigde "App Groups" mag gratis). Verlopen wel mee na 7 dagen. | Ja |
| UPnP-namen en Wake on LAN rechtstreeks | Nee | Ja, **na een aanvraag bij Apple** (multicast-recht; dagen tot weken, uitkomst onzeker) |
| MAC-adressen op iOS 27 (als het werkt) | Onbekend, testen | Waarschijnlijk ja |
| Wifi-naam | Nee | Ja, met locatietoestemming. Niet nodig: Fusebox toont hem niet. |
| iCloud en pushberichten | Nee | Ja |
| **Mac:** notarisatie en Mac-widgets (geparkeerd) | Nee | Ja, met hetzelfde account |

**Advies:** bouw en test gratis op je eigen iPhone (mijlpaal 0 tot 3). Neem het betaalde account op het moment dat je vrienden de app moeten krijgen. Er is geen andere werkbare route naar hun iPhones, en je lost er meteen de twee Mac-punten mee op.

---

## 4. Open beslissingen

| # | Vraag | Mijn advies |
|---|---|---|
| 1 | **Wat zit in versie 1?** | Zoals §2: netwerk als kern, plus een kleine Live-pagina. |
| 2 | **Gratis beginnen of meteen betalen?** | Gratis beginnen. Betalen bij mijlpaal 4 (vrienden). |
| 3 | **Welke iOS minimaal?** | iOS 26, net als de Mac op macOS 26. Dan komt Liquid Glass vanzelf mee. Vraag aan jou: welke iOS draait op jouw iPhone en die van je vrienden? |
| 4 | **Xcode bijwerken?** Je hebt Xcode 26.6; de huidige is 27.2. | Ja, vóór mijlpaal 0, als je iPhone op iOS 27 staat (en om het MAC-recht te kunnen testen). Let op: de Mac-app gebruikt dezelfde compiler. Dus op een rustig moment doen en daarna `./build.sh` draaien. |
| 5 | **Hoe delen we de code?** | De iPhone-app gebruikt de bestanden uit `Sources/` waar ze nu staan. Niets verhuizen, geen apart pakket. In een handvol Mac-bestanden komt wel een kleine "alleen op de Mac"-markering (§5). Dat stem ik af met de sessie die aan de Mac werkt. |
| 6 | **Waar komt de code?** | In `ios/` op `main`. Een aparte branch (zoals Windows) loopt snel uit de pas, juist omdat de code gedeeld is. De Mac-build raakt het niet: `build.sh` kijkt alleen naar `Sources/`. |
| 7 | **Koppeling met de Mac: nu of later?** | Later (mijlpaal 6). Het is het leukste idee, maar het vraagt een nette beveiliging en werk aan de Mac-app. |
| 8 | **App-kenmerk (bundle-id).** De Mac heet intern `local.meterkast`; dat mag op iOS niet. | Eén keer kiezen vóór TestFlight, daarna zit je eraan vast. Voorstel: `io.github.joelonhtml.fusebox`. |
| 9 | **Loop-test al in versie 1?** | Nee, direct erna (mijlpaal 5). Hij is klein, maar versie 1 moet eerst kloppen. |
| 10 | **Multicast-recht aanvragen bij Apple** (UPnP-namen, Wake on LAN)? | Pas na TestFlight, en alleen als je de namen in de praktijk mist. Wake on LAN loopt liever via de Mac. |

---

## 5. Code delen met de Mac

Regel: gedeelde rekenregels en teksten blijven op één plek (`Sources/` en `Resources/`). Alleen schermen en systeemkoppelingen krijgen een iPhone-versie in `ios/`.

| Bestand | Delen? | Toelichting |
|---|---|---|
| `NetwerkProbes.swift` (ping, poorten, namen, DNS) | **Zoals het is** | Gebruikt alleen wat iOS ook heeft. De UPnP- en Wake on LAN-delen blijven op iOS ongebruikt. |
| `NetwerkInternet.swift` | **Grotendeels** | Snelheidstest, oordeel, Find the problem, rapport: gedeeld. Het uitlezen van verbinding en wifi (macOS-koppelingen) krijgt een iOS-versie. |
| `Netwerk.swift` | **Half** | Scanregels (bereik lezen, namen opschonen, soort raden) gedeeld. Netwerk-info en ARP-tabel: iOS-versie. Het scherm (tabel met kolommen) wordt een iPhone-lijst. |
| `NetwerkKaarten.swift` | **Half** | De uitlegteksten per waarde gedeeld. De kaarten krijgen een iPhone-indeling; "Bewaar als" wordt het deelmenu. |
| `Theme.swift` | **Bijna helemaal** | De zes thema's, rijstijlen en grafieklijn gaan één op één mee. Drie plekken met Mac-kleurcode krijgen een iOS-variant. |
| `Taal.swift` | **Deels** | `L()` gaat mee. Het taalmenu en de herstart zijn alleen voor de Mac. |
| `Resources/*.lproj` | **Zoals het is** | Zelfde vertaalbestanden. Teksten die alleen de iPhone heeft komen in `ios/Strings/<taal>.strings`, zodat de Mac-controle ze niet als "ongebruikt" meldt (zelfde aanpak als Windows). |
| `Resources/Fonts` | **Zoals het is** | Montserrat en Ubuntu, met licenties. |
| `Resources/oui.tsv` | Niet nodig | Zonder MAC geen merk. Komt terug als het iOS 27-recht werkt. |
| Opmaak van getallen (`pct`, `bytes`, `rate`…) | **Ja** | Staat nu in `Meten.swift` tussen Mac-meetcode; krijgt een markering. |
| `Prefs.swift` | Zelfde opzet | Eigen, kortere lijst voor de iPhone. |
| Alle overige (Processes, Checkup, Docked-gegevens, menubalk, venster, updater, ventilatoren, accu) | **Nee** | Mac-only. Docked op de iPhone is eigen code (`ios/Fusebox/DockedScreen.swift`). |

Verder:

- **Teksten met "Mac" erin** ("This Mac", "Your Mac is fine") krijgen een iPhone-variant in alle vijf talen. Live, Checkup, Docked en de themanamen blijven onvertaald.
- **Bouwen:** een iPhone-app kan niet met het losse `swiftc`-script. Er komt een Xcode-project in `ios/`. Bouwen kan nog steeds vanaf de opdrachtregel.
- **Testen:** de zelftests van de Mac (`--selftest`) draaien ook in de iPhone-build. De simulator controleert de toestemming "Lokaal netwerk" niet, dus de LAN-scan moet op een echte iPhone getest worden.
- **Privacy:** geen serienummers, geen netwerknaam, geen MAC-adressen (kan ook niet). Het rapport blijft zonder adressen en namen. Voor TestFlight komt er een privacy-verklaring in de app (verplicht voor opslag-info en instellingen).

---

## 6. Mijlpalen

| # | Mijlpaal | Wat je daarna hebt | Account |
|---|---|---|---|
| 0 | **Proef op je iPhone** | Een kale app die op jouw iPhone test wat echt werkt: ping-ronde, toestemming, router-adres, Bonjour, en (op iOS 27) het MAC-recht. Uitkomst: een korte lijst "werkt / werkt niet", en dit plan bijgewerkt. | Gratis |
| 1 | **Internet** | Internet-paneel, oordeel, Find the problem, snelheidstest met historie. | Gratis |
| 2 | **Apparaten** | LAN-scan, details, poorten, hernoemen, onthouden. | Gratis |
| 3 | **Afmaken** | Live-pagina, thema's, talen, instellingen, rapport delen. Dit is versie 0.1 op jouw iPhone. | Gratis |
| 4 | **Naar je vrienden** | Betaald account, icoon, privacy-verklaring, TestFlight. | **Betaald** |
| 5 | **iPhone-extra's** | Loop-test, wifi tegen mobiel, opdrachten en Siri. | – |
| 6 | **Mac** | Je Mac op je iPhone, apparatenlijst van de Mac, Wake on LAN via de Mac. Ook een update van de Mac-app. | – |
| 7 | **Widgets** | Beginscherm, toegangsscherm, knop in het bedieningspaneel. | – |

Mijlpaal 0 is klein en bewust eerst: een paar dingen (router-adres, ping zonder omweg, MAC op iOS 27) staan niet hard in de documentatie van Apple en moeten op een echt toestel blijken.

---

## 7. Wat nog onzeker is

| Punt | Waarom onzeker | Als het tegenvalt |
|---|---|---|
| MAC-adressen op iOS 27 | Apple bevestigt het recht alleen voor de Mac. | Het plan gaat al uit van "geen MAC". |
| Router-adres opvragen | De nette route bestaat, maar Apple-medewerkers noemen haar "mogelijk". | Omweg: één stap van een route-test. Werkt altijd, kost een seconde. |
| Ping-ronde over 254 adressen | Werkt volgens Apple's eigen voorbeeld; of iOS afremt staat nergens. | Langzamer scannen, of poort-proefjes. |
| Aantal Bonjour-diensten | Elke dienst moet vooraf in de app staan (22 stuks). Geen limiet bekend. | Lijst inkorten tot de belangrijkste. |
| Goedkeuring multicast | Apple beslist; geen vaste termijn. | Namen via Bonjour, Wake on LAN via de Mac. |

---

## 8. Bronnen (Apple, gelezen op 24 november 2026)

- Lokaal netwerk en multicast: technote TN3179 "Understanding local network privacy" (herzien 6 oktober 2026); documentatie van `com.apple.developer.networking.multicast`.
- MAC en ARP: Apple Developer Forums, threads 741616, 689735 en 841958 (antwoorden van Apple-medewerker Quinn).
- Wifi: technote TN3111 "iOS Wi-Fi API overview"; `NEHotspotNetwork.fetchCurrent`; forum-thread 835163 (geen signaalsterkte).
- Verbinding: `NWPath` (o.a. `isExpensive`, `isConstrained`, `linkQuality`); forum-thread 774636 (VPN niet betrouwbaar te zien).
- Toestel: `UIDevice.batteryLevel`, `ProcessInfo.thermalState`, `volumeAvailableCapacityForImportantUsageKey`; forum-threads 723334 en 11393.
- Gratis tegen betaald: developer.apple.com/support/compare-memberships en de tabel "Supported capabilities (iOS)".
- TestFlight: App Store Connect Help, "TestFlight overview" en "Invite external testers".
- Widgets, Live-activiteiten, opdrachten: WidgetKit "Keeping a widget up to date"; ActivityKit; App Intents (`supportedModes`, `LongRunningIntent`).
- Achtergrond: forum-thread 685525 (geen vaste achtergrondtaken).

---

## 9. Gebouwd: versie 0.1 (24 november 2026)

### Wat erin zit

| Tab | Inhoud |
|---|---|
| **Network** | Internet-paneel (router, internet, live grafiek, oordeel, Zoek het probleem, details, publiek IP), snelheidstest Snel en Volledig met historie en "wifi tegen mobiel", netwerkrapport (pdf) via het deelmenu, apparatenlijst met filter, detailblad, poorten checken (webpoorten tikbaar), hernoemen, vergeten. |
| **Live** | Batterij, warmte (vier standen), opslag, netwerkverkeer met grafiek, en tips bovenaan ("opslag bijna vol", "iPhone is heet"). |
| **Docked** | Wat er aan de iPhone hangt (zie §1). |
| **Instellingen** | De zes thema's, licht/donker, tekstgrootte, lettertype, taal (via iOS), scannen bij openen, eenheid snelheidstest, ping-adres. |

Op mobiele data krijgt Zoek het probleem een eigen oordeel (geen router om te meten). In Energiebesparingsmodus meet Live elke 5 s in plaats van 2 en zijn er geen animaties.

### Waar het staat

| Wat | Waar |
|---|---|
| iPhone-schermen en iOS-koppelingen | `ios/Fusebox/*.swift` |
| Projectbeschrijving (Xcode-project wordt eruit gemaakt) | `ios/project.yml` |
| Bouwen + zelftest in de simulator | `ios/build.sh` |
| Vertaalcontrole | `ios/strings-check.sh` (alle teksten in vijf talen: 0 ontbreken) |
| Teksten die alleen de iPhone heeft | `ios/Strings/<taal>.strings` |
| Gedeelde code | `Sources/` (zeven bestanden, zie hieronder) |

### Wat er in de Mac-bestanden veranderd is

Alleen markeringen "alleen op de Mac" of "alleen op de iPhone", plus twee kleine dingen die ook voor de Mac gelden:

- `Theme.swift`: licht/donker telt mee als SwiftUI kijkt of het thema veranderd is. Zonder dit bleef de pagina licht als je naar donker schakelde.
- `Netwerk.swift`: apparaten krijgen een sleutel (`key`) voor de onthouden-lijst. Op de Mac is dat het MAC-adres, net als altijd.

Mac-app gecontroleerd na de wijzigingen: bouwt, `--selftest` slaagt, `scripts/strings-check.sh` geeft 0 ontbrekende vertalingen.

### Getest (simulator, iPhone 17 Pro, iOS 26.5)

- Zelftest (`--selftest`): de netwerk-zelftests van de Mac plus iPhone-eigen tests (Bonjour-lijst gelijk aan Info.plist, oordeel op mobiele data).
- Router gevonden, pings, snelheidstest (385/238 Mbit/s), scan vond 16 apparaten met namen, poorten checken, pdf delen.
- Thema's Instrument, Color blocks en Liquid Glass, licht en donker, live wisselen.
- Nederlands. De andere talen alleen via de vertaalcontrole.
- Eigen verbruik: 2 à 3% van één processorkern met Network open (debug-build).

### Niet getest

- **Op een echte iPhone.** De simulator gebruikt het netwerk van de Mac: geen toestemming "Lokaal netwerk", geen blokkade van multicast. Op een iPhone vallen namen via UPnP weg en duurt de eerste scan pas na "Sta toe".
- Mobiele data (de simulator heeft het niet).
- Docked met echte apparaten (de simulator heeft er geen).

### Zelf op je iPhone zetten (gratis Apple ID)

1. Sluit je iPhone aan en zet Ontwikkelaarsmodus aan (Instellingen › Privacy en beveiliging).
2. Draai `ios/build.sh` één keer; dat maakt `ios/Fusebox.xcodeproj`.
3. Open dat project in Xcode, kies bij Signing & Capabilities je eigen team (je Apple ID), kies je iPhone bovenin en druk op Run.
4. Na 7 dagen opnieuw stap 3.

Tip: geef me je Team ID (staat in Xcode bij je account). Dan zet ik hem in `project.yml`, zodat je hem niet na elke `build.sh` opnieuw hoeft te kiezen.
