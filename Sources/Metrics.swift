import SwiftUI
import CoreWLAN

// MARK: - Metingen: elke meting is een `Metric`; welke en in welke volgorde kies je in Instellingen.
//
// Nieuwe meting toevoegen:
//   1. een `case` in `Metric` (+ `title`)
//   2. een tak in `MetricView.content` die een `MetricRowData` teruggeeft
//   (en zo nodig het veld in `Sample` + meten in `Sampler`)

enum Metric: String, CaseIterable, Identifiable {
    case cpu, gpu, ram, net, ssd, io, battery, fan
    var id: String { rawValue }
    var title: String {
        switch self {
        case .cpu: "Processor (CPU)"
        case .gpu: "Graphics chip (GPU)"
        case .ram: "Memory (RAM)"
        case .net: "Internet (NET)"
        case .ssd: "Free storage (SSD)"
        case .io: "Storage activity (read/write)"
        case .battery: "Battery"
        case .fan: "Fans (FAN)"
        }
    }
    /// Korte naam (rij-label, menubalk).
    var abbr: String {
        switch self {
        case .cpu: "CPU"
        case .gpu: "GPU"
        case .ram: "RAM"
        case .net: "NET"
        case .ssd: "SSD"
        case .io: "I/O"
        case .battery: "BAT"
        case .fan: "FAN"
        }
    }
    static let defaultOrder = "cpu,gpu,ram,net,ssd,battery"
    /// Ingeschakelde metingen in volgorde, uit de instelling "metrics".
    /// Zonder ventilator valt "fan" weg, ook als hij (bv. via import) in de lijst staat.
    static func enabled(_ raw: String) -> [Metric] {
        raw.split(separator: ",").compactMap { Metric(rawValue: String($0)) }.filter { $0 != .fan || Fans.count > 0 }
    }
    /// Wat deze Mac kan tonen (keuzelijsten in Instellingen en het stappenplan).
    static var offered: [Metric] { allCases.filter { $0 != .fan || Fans.count > 0 } }
}

enum GraphMode: String, CaseIterable, Identifiable {
    case auto = "auto", always = "always", never = "never"
    var id: String { rawValue }
    /// Tekst voor de gebruiker; rawValue is een vaste korte id (veilig om op te slaan en te delen).
    var title: String {
        switch self {
        case .auto: "Automatic (when the window is larger)"
        case .always: "Always"
        case .never: "Never"
        }
    }
}

/// Wat één rij toont: label + `side` (temperatuur, ms, ⚡︎) links, getal + korte eenheid rechts uitgelijnd.
struct MetricRowData {
    var label: String
    var value: String
    var unit = ""                       // korte eenheid direct achter het getal ("GB", "/10")
    var side = ""                       // klein, naast het label (temperatuur, reactietijd, resterende tijd)
    var sideIcon: String?               // SF Symbol vóór `side` (batterij: laden ⚡︎ of hoe vol)
    var template = "100%"               // breedste vorm van `value`: houdt de breedte vast (geen verspringen bij 7% → 17%)
    var level = 0, sideLevel = 0        // 0 normaal, 1 let op, 2 hoog
    var detail = ""                     // (niet meer op de Live-pagina; voor andere plekken)
    var values: [Double] = [], second: [Double] = []
    var cores: [Double] = []            // CPU per kern (staafjes onder de grafiek, M+)
    var graphIsTemp = false             // grafiek toont temperatuur (°C) i.p.v. gebruik (%)
    var top: Double? = 100, bottom = 0.0
    var valueSize: CGFloat = 30
    var facts: [Fact] = []              // L/XL op de Live-pagina: extra regels, belangrijkste eerst
    var hot = 0                         // CPU: thermalState van macOS (1 warm, 2 remt af, 3 kritiek); icoontje naast het label
}

/// Eén regel extra info in een grote kaart: "Swap   0.8 GB", of een app met icoon.
struct Fact {
    var label: String, value: String
    var icon: NSImage? = nil
    var app: AppRef? = nil              // top-app: klik opent zijn app-pagina
}


/// De rij-inhoud per meting; gedeeld door Live-pagina en menubalk.
extension Metric {
    /// Echte waarden (kernen, GB) i.p.v. % — dezelfde keuze voor Live-pagina en menubalk.
    var prefersAbsolute: Bool { (UserDefaults.standard.string(forKey: "absolute") ?? "").split(separator: ",").contains(Substring(rawValue)) }

    /// `abs`: echte waarden (kernen, GB) i.p.v. percentages, waar dat kan. `apps`: voor de top-apps in grote kaarten.
    /// `today`: minuutpunten van vandaag (SSD: geschreven vandaag).
    func row(_ s: Sample, _ h: [Sample], abs: Bool, apps: [AppUsage] = [], today: [Sample] = []) -> MetricRowData {
        var r = data(s, h, abs: abs)
        r.facts = facts(s, h, apps, today)
        return r
    }

    /// Extra regels voor grote kaarten (L/XL), belangrijkste eerst: wat niet past valt onderaan weg.
    private func facts(_ s: Sample, _ h: [Sample], _ apps: [AppUsage], _ today: [Sample]) -> [Fact] {
        let d = UserDefaults.standard, bits = d.bool(forKey: "netBits"), host = d.string(forKey: "pingHost") ?? ""
        let cores = Double(ProcessInfo.processInfo.activeProcessorCount)
        /// Laagste · gemiddelde · hoogste over de periode van de grafiek; eenheid één keer achteraan
        func spread(_ v: [Double], unit: String = "%", _ f: (Double) -> String = { "\(Int($0.rounded()))" }) -> Fact? {
            guard v.count > 1, let lo = v.min(), let hi = v.max() else { return nil }
            return Fact(label: "Low · avg · high", value: "\(f(lo)) · \(f(v.reduce(0, +) / Double(v.count))) · \(f(hi))\(unit)")
        }
        func top(_ by: (AppUsage) -> Double, n: Int = 3, _ f: (AppUsage) -> String) -> [Fact?] {
            apps.sorted { by($0) > by($1) }.prefix(n).filter { by($0) > 0 }.map { Fact(label: $0.name, value: f($0), icon: $0.icon, app: AppRef(id: $0.id, name: $0.name)) }
        }
        let gb = { (v: Double) in "\(one(v)) GB" }
        let rate = { (v: Double) in short(v, bits: bits) }
        var out: [Fact?]
        switch self {
        case .cpu:
            out = [Fact(label: "In use", value: "\(one(s.cpu / 100 * cores)) of \(Int(cores)) cores"),
                   s.temp.map { Fact(label: "Hottest core", value: deg($0)) }]
            out += top(\.cpu) { pct($0.cpu) }
            out.append(spread(h.map(\.cpu)))
        case .gpu:
            out = [s.gpuTemp.map { Fact(label: "Temperature", value: deg($0)) }, spread(h.map(\.gpu))]
        case .ram:
            let total = Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824
            out = [Fact(label: "In use", value: "\(one(s.ramUsedGB)) of \(Int(total.rounded())) GB"),
                   Fact(label: "Pressure", value: s.pressure >= 4 ? "critical" : s.pressure == 2 ? "high" : "normal"),
                   Fact(label: "Swap", value: gb(s.swapGB))]
            out += top(\.memGB) { gb($0.memGB) }
            out.append(spread(h.map(\.ramUsedGB), unit: " GB", one))
        case .net:
            // Niet het grote getal herhalen: wat er in de periode doorheen ging, en de piek
            out = [host.isEmpty || s.pingPending ? nil : Fact(label: "Response time", value: s.pingMs.map { "\(Int($0.rounded())) ms" } ?? "no answer"),
                   // Welke app nu het meeste internet gebruikt (minuutgemiddelde; alleen met "netPerApp" aan)
                   top({ $0.netIn + $0.netOut }, n: 1) { let (d, u, unit) = ratePair($0.netIn, $0.netOut, bits: bits); return "↓\(d) ↑\(u) \(unit)" }.first ?? nil,
                   Fact(label: "Downloaded", value: bytes(total(h, \.down))),
                   Fact(label: "Uploaded", value: bytes(total(h, \.up)))]
            if let dn = h.map(\.down).max(), let up = h.map(\.up).max() { out.append(Fact(label: "Peak", value: "↓\(rate(dn)) ↑\(rate(up))")) }
            out.append(LinkRate.fact.map { Fact(label: $0.0, value: $0.1) })
            out.append(LinkRate.signal.map { Fact(label: "Wi-Fi signal", value: $0) })
        case .ssd:
            out = [Fact(label: "Free", value: "\(Int(s.diskFreeGB)) GB"),
                   today.isEmpty ? nil : Fact(label: "Written today", value: bytes(total(today, \.diskWrite))),
                   Fact(label: "Size", value: "\(Int(s.diskTotalGB)) GB")]
        case .io:
            out = [Fact(label: "Read", value: bytes(total(h, \.diskRead))),
                   Fact(label: "Written", value: bytes(total(h, \.diskWrite))),
                   Fact(label: "Swap", value: gb(s.swapGB))]
            if let r = h.map(\.diskRead).max(), let w = h.map(\.diskWrite).max() { out.append(Fact(label: "Peak", value: "R \(rate(r)) W \(rate(w))")) }
        case .battery:
            guard let b = s.battery else { return [] }
            let w = s.batteryW.map { "\(Int(Swift.abs($0).rounded())) W" }
            // Aan de lader maar niet aan het laden en niet vol: macOS houdt hem vast (oplaadlimiet / geoptimaliseerd laden)
            out = s.charging ? [Fact(label: "Charging", value: s.batteryMinutes.map { "full in \(duration($0))" } ?? "–"), w.map { Fact(label: "Charging at", value: $0) }]
                : s.onAC ? [b < 99 ? Fact(label: "Charge limit", value: "holding at \(pct(b))") : nil]
                : [Fact(label: "On battery", value: s.batteryMinutes.map { "\(duration($0)) left" } ?? "–"), w.map { Fact(label: "Using", value: $0) }]
            if s.onAC { out.append(Fact(label: "Power adapter", value: s.adapterW.map { "\($0) W" } ?? "connected")) }
            out.append(spread(h.compactMap(\.battery)))
        case .fan:
            out = s.fans.count > 1 ? s.fans.indices.map { Fact(label: "Fan \($0 + 1)", value: "\(Int(s.fans[$0].rpm.rounded())) rpm") } : []
            out.append(spread(h.compactMap(\.fanRPM), unit: " rpm"))
        }
        return out.compactMap { $0 }
    }

    private func data(_ s: Sample, _ h: [Sample], abs: Bool) -> MetricRowData {
        let cores = Double(ProcessInfo.processInfo.activeProcessorCount)
        let d = UserDefaults.standard
        let warnCPU = d.double(forKey: "warnCPU"), warnTemp = d.double(forKey: "warnTemp"), bits = d.bool(forKey: "netBits")
        let host = d.string(forKey: "pingHost") ?? ""
        switch self {
        case .cpu:
            return MetricRowData(label: abbr, value: abs ? one(s.cpu / 100 * cores) : pct(s.cpu), unit: abs ? "/\(Int(cores))" : "",
                                 side: deg(s.temp), template: abs ? "88.8" : "100%",
                                 level: lvl(s.cpu, warnCPU, warnCPU + 10), sideLevel: lvl(s.temp ?? 0, warnTemp, warnTemp + 10),
                                 detail: abs ? "\(pct(s.cpu)) · hottest core \(deg(s.temp))" : "\(one(s.cpu / 100 * cores)) of \(Int(cores)) cores · hottest core \(deg(s.temp))",
                                 values: h.map(\.cpu), cores: s.cores).graph(usage: h.map(\.cpu), temp: h.map(\.temp), mode: d.string(forKey: "cpuGraph"))
        case .gpu:
            return MetricRowData(label: abbr, value: pct(s.gpu), side: deg(s.gpuTemp), level: lvl(s.gpu, 90, 98), sideLevel: lvl(s.gpuTemp ?? 0, warnTemp, warnTemp + 10),
                                 detail: s.gpuTemp.map { "temperature \(deg($0))" } ?? "", values: h.map(\.gpu))
                .graph(usage: h.map(\.gpu), temp: h.map(\.gpuTemp), mode: d.string(forKey: "gpuGraph"))
        case .ram:
            let total = Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824
            let druk = s.pressure >= 4 ? "critical" : s.pressure == 2 ? "high" : "normal"
            return MetricRowData(label: abbr, value: abs ? one(s.ramUsedGB) : pct(s.ram), unit: abs ? "GB" : "", template: abs ? "88.8" : "100%",
                                 level: s.pressure >= 4 ? 2 : s.pressure == 2 ? 1 : 0,
                                 detail: "pressure \(druk) · swap \(one(s.swapGB)) GB · " + (abs ? pct(s.ram) : "\(one(s.ramUsedGB)) of \(Int(total.rounded())) GB"),
                                 values: h.map(\.ramUsedGB), top: total)
        case .net:
            // Eenheid één keer (in de eenheidskolom): "↓8 ↑120  KB/s" i.p.v. "↓8 KB/s ↑120 KB/s"
            let (dn, upv, unit) = ratePair(s.down, s.up, bits: bits)
            // Reactietijd (ms) naast NET alleen met de optie "netPing"; meten gebeurt altijd (meldingen, Checkup)
            let ping = host.isEmpty || s.pingPending || !d.bool(forKey: "netPing") ? nil : s.pingMs.map { "\(Int($0.rounded())) ms" } ?? "– ms"
            // Optie "netSpeed": je internetsnelheid (laatste snelheidstest op het Network-tabblad) naast de ms.
            // Nog geen snelheidstest gedaan: niets. (De Wi-Fi-verbindingssnelheid stond hier als noodoplossing, maar die
            // verandert bijna nooit en is geen internetsnelheid; hij staat nu als "Wi-Fi link" bij de NET-feiten.)
            let speed = d.bool(forKey: "netSpeed") ? InternetStats.loadHistory().first.map { "\(Int($0.down.rounded())) Mbit" } : nil   // nieuwste staat vooraan
            var row = MetricRowData(label: abbr, value: "↓\(dn) ↑\(upv)", unit: unit,
                                 side: [ping, speed].compactMap { $0 }.joined(separator: " · "),
                                 template: "↓888 ↑888",
                                 sideLevel: host.isEmpty || s.pingPending || !d.bool(forKey: "netPing") ? 0 : lvl(s.pingMs ?? 9999, d.double(forKey: "warnPing"), d.double(forKey: "warnPing") + 10),
                                 detail: host.isEmpty ? "response time check is off" : "internet response time",
                                 values: h.map(\.down), second: h.map(\.up), top: nil, valueSize: 24)
            row.sideIcon = LinkRate.wifiMbit() != nil ? "wifi" : "cable.connector.horizontal"   // draadloos of bedraad
            return row
        case .ssd:
            let usedPct = s.diskTotalGB > 0 ? (1 - s.diskFreeGB / s.diskTotalGB) * 100 : 0
            return MetricRowData(label: abbr, value: abs ? "\(Int(s.diskFreeGB))" : pct(usedPct), unit: abs ? "GB" : "", template: abs ? "8888" : "100%",
                                 level: usedPct >= 95 ? 2 : usedPct >= 90 ? 1 : 0,
                                 detail: abs ? "\(pct(usedPct)) full of \(Int(s.diskTotalGB)) GB" : "\(Int(s.diskFreeGB)) GB free of \(Int(s.diskTotalGB)) GB", values: h.map { $0.diskTotalGB - $0.diskFreeGB },
                                 top: s.diskTotalGB > 0 ? s.diskTotalGB : nil, valueSize: 24)
        case .io:
            let (rd, wr, ioUnit) = ratePair(s.diskRead, s.diskWrite, bits: bits)
            return MetricRowData(label: abbr, value: "R \(rd) W \(wr)", unit: ioUnit, template: "R 888 W 888", detail: "reading and writing to the SSD",
                                 values: h.map(\.diskRead), second: h.map(\.diskWrite), top: nil, valueSize: 24)
        case .battery:
            guard let b = s.battery else { return MetricRowData(label: abbr, value: "–", detail: "no battery", valueSize: 24) }
            let time = s.batteryMinutes.map { s.charging ? "full in \(duration($0))" : "\(duration($0)) left" } ?? (s.charging ? "charging" : "")
            // Naast BAT: laden → batterij met bliksem, op de accu → batterij die toont hoe vol; plus (instelling "batteryTime")
            // de schatting van macOS: op de accu hoe lang tot leeg, aan de lader hoe lang tot vol (bijv. "2:35" / "0:48").
            // Vanaf 10 uur "14h": "14:27" leest als een kloktijd
            let left = d.bool(forKey: "batteryTime") ? s.batteryMinutes.map { $0 >= 600 ? "\($0 / 60)h" : String(format: "%d:%02d", $0 / 60, $0 % 60) } : nil
            var row = MetricRowData(label: abbr, value: pct(b), side: left ?? "", level: !s.charging && b <= 10 ? 2 : !s.charging && b <= 20 ? 1 : 0,
                                 detail: time.isEmpty ? "on power adapter" : time, values: h.compactMap(\.battery), valueSize: 24)
            row.sideIcon = s.charging ? "battery.100percent.bolt" : batteryIcon(b)
            return row
        case .fan:
            guard let rpm = s.fanRPM else { return MetricRowData(label: abbr, value: "–", detail: "no fans", valueSize: 24) }
            let top = s.fans.map(\.max).max() ?? 0
            let forced = s.fans.contains(where: \.forced)
            return MetricRowData(label: abbr, value: Int(rpm.rounded()).formatted(.number.locale(Locale(identifier: "en_US"))), unit: "rpm",
                                 side: forced ? (d.string(forKey: "fanMode") == "max" ? "full" : "curve") : "auto", template: "8,888",
                                 detail: s.fans.count > 1 ? "average of \(s.fans.count) fans" : "", values: h.compactMap(\.fanRPM),
                                 top: top > 0 ? top : nil, valueSize: 24)
        }
    }

}

/// Wi-Fi-verbindingssnelheid (Mbit/s), signaal en kanaal, hooguit eens per 10 s opgevraagd. nil op kabel of zonder Wi-Fi.
/// Geen netwerknaam (SSID): die vraagt om locatietoestemming en hoort hier niet.
enum LinkRate {
    nonisolated(unsafe) private static var cached: (at: Date, mbit: Double?, rssi: Int, ch: Int) = (.distantPast, nil, 0, 0)
    private static func read() {
        guard Date().timeIntervalSince(cached.at) > 10 else { return }
        let i = CWWiFiClient.shared().interface(), r = i?.transmitRate() ?? 0
        cached = (Date(), r > 0 ? r : nil, i?.rssiValue() ?? 0, i?.wlanChannel()?.channelNumber ?? 0)
    }
    static func wifiMbit() -> Double? { read(); return cached.mbit }
    /// Signaal in dBm als Wi-Fi verbonden is (hooguit 10 s oud), anders nil.
    static var rssi: Int? { read(); return cached.mbit != nil && cached.rssi < 0 ? cached.rssi : nil }
    /// "Good · −52 dBm · ch 36"
    static var signal: String? { read(); return cached.mbit != nil && cached.rssi < 0 ? signalText(cached.rssi, cached.ch) : nil }
    static func signalText(_ rssi: Int, _ ch: Int) -> String {
        let q = rssi >= -55 ? "Excellent" : rssi >= -67 ? "Good" : rssi >= -75 ? "Fair" : "Weak"
        return "\(q) · −\(-rssi) dBm" + (ch > 0 ? " · ch \(ch)" : "")
    }
    /// Regel voor de NET-feiten: ("Wi-Fi link", "286 Mbit/s"). Wat de router je Mac geeft, niet je internetsnelheid.
    static var fact: (String, String)? { wifiMbit().map { ("Wi-Fi link", "\(Int($0.rounded())) Mbit/s") } }
}

/// Wi-Fi-uitval via CoreWLAN-meldingen (linkDidChange), dus zonder te pollen. Geen netwerknaam, BSSID of MAC: alleen of de verbinding er is.
/// Slaapstand en Wi-Fi zelf uitzetten tellen niet als uitval.
final class WifiWatch: NSObject, CWEventDelegate, @unchecked Sendable {
    static let shared = WifiWatch()
    private let lock = NSLock()
    private var down = false, dropped = false, quietUntil = Date.distantPast

    func start() {
        let c = CWWiFiClient.shared()
        c.delegate = self
        try? c.startMonitoringEvent(with: .linkDidChange)
        // Rond slapen/wakker worden valt de link vanzelf weg en komt terug: 30 s niet meetellen
        for n in [NSWorkspace.willSleepNotification, NSWorkspace.didWakeNotification] {
            NSWorkspace.shared.notificationCenter.addObserver(forName: n, object: nil, queue: nil) { [weak self] _ in
                self?.lock.withLock { self?.quietUntil = Date().addingTimeInterval(30); self?.down = false }
            }
        }
    }

    func linkDidChangeForWiFiInterface(withName name: String) {
        let i = CWWiFiClient.shared().interface(withName: name)
        let up = (i?.rssiValue() ?? 0) != 0, on = i?.powerOn() ?? false
        lock.withLock {
            if up || !on || Date() < quietUntil { down = false; return }
            if !down { dropped = true }
            down = true
        }
    }

    /// Voor de log (elke meting): 0 = weggevallen (sinds de vorige meting, ook als hij al terug is), <0 = signaal in dBm, nil = geen Wi-Fi.
    func value() -> Int? {
        let gone = lock.withLock { let g = dropped || down; dropped = false; return g }
        return gone ? 0 : LinkRate.rssi
    }
}

/// Batterij-icoon dat toont hoe vol: "battery.75percent".
func batteryIcon(_ b: Double) -> String {
    "battery.\(b >= 88 ? "100" : b >= 63 ? "75" : b >= 38 ? "50" : b >= 13 ? "25" : "0")percent"
}

/// Bytes over een reeks metingen: snelheid × de tijd sinds de vorige meting. Het meettempo is 2 s, 5 s (low impact)
/// of een minuut ("Today"); een gat (Mac sliep, app stond uit) telt als hoogstens één minuut.
func total(_ h: [Sample], _ rate: KeyPath<Sample, Double>) -> Double {
    guard h.count > 1 else { return (h.first?[keyPath: rate] ?? 0) * LowImpact.interval }
    func gap(_ i: Int) -> Double { min(max(h[i].time.timeIntervalSince(h[i - 1].time), 0), 60) }
    return h.indices.reduce(0) { $0 + h[$1][keyPath: rate] * gap(max($1, 1)) }   // eerste punt: de stap naar het tweede
}

extension MetricRowData {
    /// CPU/GPU-grafiek: "usage" (standaard), "temp" of "both" (temperatuur als tweede, dunne lijn).
    /// Temperatuur wordt elke 10 s gemeten: tussenliggende punten krijgen de laatst bekende waarde.
    func graph(usage: [Double], temp: [Double?], mode: String?) -> MetricRowData {
        var r = self, last: Double?
        let t = temp.map { v -> Double in if let v { last = v }; return last ?? v ?? 0 }
        switch mode {
        case "temp": r.values = t; r.second = []; r.top = 110; r.bottom = 20; r.graphIsTemp = true
        case "both": r.values = usage; r.second = t.map { min(max(($0 - 20) / 90 * 100, 0), 100) }   // 20–110 °C op de %-schaal
        default: r.values = usage
        }
        return r
    }
}

/// Twee snelheden in één eenheid; de grootste bepaalt welke: ("8", "120", "KB/s"). Onder 10 één decimaal.
func ratePair(_ a: Double, _ b: Double, bits: Bool) -> (String, String, String) {
    let k = bits ? 8.0 : 1, m = max(a, b, 0) * k
    let (div, unit): (Double, String) = m < 1e6 ? (1e3, bits ? "kbit/s" : "KB/s") : m < 1e9 ? (1e6, bits ? "Mbit/s" : "MB/s") : (1e9, bits ? "Gbit/s" : "GB/s")
    func f(_ v: Double) -> String { let x = max(v, 0) * k / div; return div > 1e3 && x < 10 ? one(x) : "\(Int(x.rounded()))" }
    return (f(a), f(b), unit)
}

func deg(_ t: Double?) -> String { t.map { "\(Int($0.rounded()))°" } ?? "" }
func lvl(_ v: Double, _ warn: Double, _ danger: Double) -> Int { v >= danger ? 2 : v >= warn ? 1 : 0 }
/// Kort: 640 KB/s, 1.2 MB/s — of met `bits` (instelling "netBits"): 5.1 Mbit/s
func short(_ bps: Double, bits: Bool = false) -> String {
    guard bits else { return rate(bps) }
    let b = bps * 8
    return b < 1_000_000 ? "\(Int(b / 1000)) kbit/s" : "\(one(b / 1_000_000)) Mbit/s"
}
