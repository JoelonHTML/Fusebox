import SwiftUI

// MARK: - Model: elke 2 s meten, geschiedenis van 1 uur + vandaag per minuut, CSV per minuut wegschrijven

enum TimeRange: String, CaseIterable, Identifiable {
    // `fiveMin` = de periode van de Live-grafieken (instelbaar, standaard 10 min). Id "5m" blijft voor bestaande instellingen.
    case fiveMin = "5m", hour = "1h", today = "today"
    static var shortMinutes: Int { max(1, min(60, UserDefaults.standard.integer(forKey: "shortMinutes"))) }
    var id: String { rawValue }
    /// Tekst voor de gebruiker; rawValue is een vaste korte id (veilig om op te slaan en te delen).
    var title: String {
        switch self {
        case .fiveMin: "\(TimeRange.shortMinutes) min"
        case .hour: "1 hour"
        case .today: "Today"
        }
    }
}

final class Meter: ObservableObject {
    @Published private(set) var recent: [Sample] = []   // laatste uur, elke 2 s
    @Published private(set) var today: [Sample] = []    // vandaag, gemiddelde per minuut
    /// Programma's: `ui` ververst elke 2 s zolang die pagina open is; `minuteApps` meet per minuut voor de log/analyse.
    let ui = AppMonitor(network: true), minuteApps = AppMonitor()
    /// Bij openen van de Programs-pagina meteen meten, zodat de lijst niet 2 s leeg is.
    var programsVisible = false {
        didSet { if programsVisible && !oldValue { ui.refresh(totalCPU: latest.cpu, totalMemGB: latest.ramUsedGB) } }
    }
    private let pinger = Pinger()
    private var ticks = 0
    private var pingHost = ""
    private var streak: [Alert: Int] = [:], lastAlert: [Alert: Date] = [:]
    /// Wordt waar bij afsluiten (dan geen instellingen meer terugzetten vanuit het stappenplan).
    private(set) var quitting = false
    let logDir = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Fusebox")
    private let sampler = Sampler()
    private let fanControl = FanController()
    private var timer: Timer?
    private var minute: [Sample] = []
    private var buffer = ""
    private var lastFlush = Date()

    var latest: Sample { recent.last ?? Sample() }

    init() {
        // Eenmalig: logmap van de oude naam (Meterkast) verhuizen naar Fusebox, zodat de geschiedenis meegaat
        let fm = FileManager.default, oldDir = logDir.deletingLastPathComponent().appendingPathComponent("Meterkast")
        if fm.fileExists(atPath: oldDir.path), !fm.fileExists(atPath: logDir.path) { try? fm.moveItem(at: oldDir, to: logDir) }
        // Log van een oudere versie (andere kolommen)? Omzetten naar de huidige kolommen, zonder data te verliezen.
        // Sneller opstarten: opruimen, omzetten en de log van vandaag inlezen gebeurt op de achtergrond
        let keep = UserDefaults.standard.integer(forKey: "keepDays")
        let f = file(for: Date())
        DispatchQueue.global(qos: .utility).async { [weak self] in
            guard let self else { return }
            if keep > 0 { self.deleteLogs(olderThan: keep) }
            Self.migrate(f)
            let loaded = self.loadToday()
            DispatchQueue.main.async { self.today = loaded + self.today }
        }
        // Eenmalig: op een Mac met ventilatoren de FAN-meting achteraan de Live-pagina zetten
        let d = UserDefaults.standard
        if Fans.count > 0, !d.bool(forKey: "fanMetricAdded") {
            d.set(true, forKey: "fanMetricAdded")
            let m = d.string(forKey: "metrics") ?? Metric.defaultOrder
            if !m.split(separator: ",").contains("fan") { d.set(m.isEmpty ? "fan" : m + ",fan", forKey: "metrics") }
        }
        _ = sampler.sample() // eerste meting = nulpunt voor de deltas
        // Eerste echte waarden snel tonen (de getallen flippen dan binnen), daarna elke 2 s
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in self?.tick() }
        let t = Timer(timeInterval: 2, repeats: true) { [weak self] _ in self?.tick() }
        t.tolerance = 0.5 // laat macOS wake-ups bundelen
        RunLoop.main.add(t, forMode: .common)
        timer = t
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: nil) { [weak self] _ in self?.quitting = true; self?.flush() }
    }

    func tick() {
        var s = sampler.sample()
        ticks += 1
        if ticks % 5 == 1 { pingHost = UserDefaults.standard.string(forKey: "pingHost") ?? "" }
        if ticks % 5 == 1 && !pingHost.isEmpty { pinger.ping(host: pingHost) }   // "" = uit: geen netwerk
        s.pingMs = pingHost.isEmpty ? nil : pinger.lastMs
        if programsVisible { ui.refresh(totalCPU: s.cpu, totalMemGB: s.ramUsedGB) }
        fanControl.update(s)
        if ticks % 30 == 0 { minuteTasks(s) }
        let cal = Calendar.current
        if let first = minute.first, !cal.isDate(first.time, equalTo: s.time, toGranularity: .minute) {
            if !cal.isDate(first.time, inSameDayAs: s.time) { today.removeAll() }
            today.append(Sample.average(minute))
            minute.removeAll()
        }
        minute.append(s)
        recent.append(s)
        if recent.count > 1800 { recent.removeFirst(recent.count - 1800) }
        buffer += s.csv
        if s.time.timeIntervalSince(lastFlush) >= 60 { flush() }
    }

    func samples(_ r: TimeRange) -> [Sample] {
        switch r {
        case .fiveMin: Array(recent.suffix(TimeRange.shortMinutes * 30))   // 30 metingen per minuut
        case .hour: recent
        case .today: minute.isEmpty ? today : today + [Sample.average(minute)]
        }
    }

    /// Grafieken gebruiken altijd de hele breedte (zoals "Today"); het aslabel toont de echte tijdspanne.
    func fill(_ r: TimeRange) -> Double { 1 }

    func file(for day: Date, prefix: String = "meterkast") -> URL {
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd"
        return logDir.appendingPathComponent("\(prefix)-\(f.string(from: day)).csv")
    }

    /// Elke minuut: programma's meten, top naar apps-CSV schrijven, en zo nodig een melding geven.
    private func minuteTasks(_ s: Sample) {
        minuteApps.refresh(totalCPU: s.cpu, totalMemGB: s.ramUsedGB)
        let apps = minuteApps.apps
        do {   // loggen staat altijd aan
            let top = Set(apps.sorted { $0.cpu > $1.cpu }.prefix(10).map(\.id) + apps.sorted { $0.memGB > $1.memGB }.prefix(10).map(\.id))
            var lines = ""
            let t = Sample.stamp.string(from: s.time)
            for a in apps where top.contains(a.id) {
                let name = a.name.replacingOccurrences(of: ",", with: " ")
                lines += "\(t),\(name),\(a.kind.rawValue),\(String(format: "%.1f", a.cpu)),\(String(format: "%.2f", a.memGB)),\(String(format: "%.2f", a.energyW))\n"
            }
            let file = file(for: s.time, prefix: "apps")
            try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
            if !FileManager.default.fileExists(atPath: file.path) { try? AppUsage.csvHeader.write(to: file, atomically: true, encoding: .utf8) }
            if let h = try? FileHandle(forWritingTo: file) { h.seekToEndOfFile(); h.write(Data(lines.utf8)); try? h.close() }
        }
        checkAlerts(s, top: apps.first { $0.kind != .macos })
    }

    /// Eén keer per minuut: per soort melding tellen hoeveel minuten op rij de grens overschreden is.
    private func checkAlerts(_ s: Sample, top: AppUsage?) {
        let d = UserDefaults.standard
        let last = Array(recent.suffix(30))
        guard d.bool(forKey: "notify"), last.count >= 20 else { streak = [:]; return }
        let pingOn = !(d.string(forKey: "pingHost") ?? "").isEmpty
        for a in Alert.allCases {
            let need = max(1, d.integer(forKey: a.key("min")))
            guard d.bool(forKey: a.key("on")),
                  let text = a.check(last, top: top, limit: d.double(forKey: a.key("limit")), minutes: need, pingOn: pingOn)
            else { streak[a] = 0; continue }
            streak[a, default: 0] += 1
            let cooldown = a == .disk ? 86_400 : Double(max(1, d.integer(forKey: "alertCooldown"))) * 60
            guard streak[a]! >= need, s.time.timeIntervalSince(lastAlert[a] ?? .distantPast) >= cooldown else { continue }
            lastAlert[a] = s.time
            Notify.send(title: a.title, body: text)
        }
    }

    /// CSV-logs weggooien: ouder dan `days` dagen, of allemaal (nil).
    func deleteLogs(olderThan days: Int?) {
        let fm = FileManager.default
        guard let files = try? fm.contentsOfDirectory(at: logDir, includingPropertiesForKeys: nil) else { return }
        let f = DateFormatter(); f.dateFormat = "yyyy-MM-dd"
        let cutoff = days.flatMap { Calendar.current.date(byAdding: .day, value: -$0, to: Calendar.current.startOfDay(for: Date())) }
        for u in files where u.pathExtension == "csv" {
            let name = u.deletingPathExtension().lastPathComponent
            guard name.hasPrefix("meterkast-") || name.hasPrefix("apps-"), let day = f.date(from: String(name.suffix(10))) else { continue }
            if cutoff.map({ day < $0 }) ?? true { try? fm.removeItem(at: u) }
        }
    }

    func flush() {
        lastFlush = Date()
        guard !buffer.isEmpty else { return }
        let file = file(for: lastFlush)
        try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: file.path) {
            try? Sample.csvHeader.write(to: file, atomically: true, encoding: .utf8)
        }
        if let h = try? FileHandle(forWritingTo: file) {
            h.seekToEndOfFile()
            h.write(Data(buffer.utf8))
            try? h.close()
            buffer = ""
        }
    }

    /// Zet een CSV met andere kolommen om naar `Sample.csvHeader`: kolommen op naam, ontbrekende blijven leeg.
    static func migrate(_ file: URL) {
        guard let text = try? String(contentsOf: file, encoding: .utf8) else { return }
        var lines = text.split(separator: "\n", omittingEmptySubsequences: true)
        guard let head = lines.first, String(head) + "\n" != Sample.csvHeader else { return }
        lines.removeFirst()
        let old = head.split(separator: ",").map(String.init)
        let new = Sample.csvHeader.dropLast().split(separator: ",").map(String.init)
        let map = new.map { old.firstIndex(of: $0) }
        var out = Sample.csvHeader
        for line in lines {
            let v = line.split(separator: ",", omittingEmptySubsequences: false)
            out += map.map { i in i.flatMap { $0 < v.count ? String(v[$0]) : nil } ?? "" }.joined(separator: ",") + "\n"
        }
        try? out.write(to: file, atomically: true, encoding: .utf8)
    }

    /// Leest de CSV van vandaag terug en maakt er minuutgemiddelden van, zodat "Vandaag" na herstart niet leeg is.
    private func loadToday() -> [Sample] {
        guard let text = try? String(contentsOf: file(for: Date()), encoding: .utf8) else { return [] }
        var lines = text.split(separator: "\n")
        guard !lines.isEmpty else { return [] }
        let cols = Dictionary(uniqueKeysWithValues: lines.removeFirst().split(separator: ",").enumerated().map { (String($1), $0) })
        var out: [Sample] = [], bucket: [Sample] = [], key = "", keyTime: Date?
        for line in lines {
            let v = line.split(separator: ",", omittingEmptySubsequences: false)
            func d(_ c: String) -> Double? { cols[c].flatMap { $0 < v.count ? Double(v[$0]) : nil } }
            guard let ts = v.first, ts.count >= 16 else { continue }
            let k = String(ts.prefix(16)) // per minuut groeperen zonder elke regel te parsen
            if k != key {
                if !bucket.isEmpty { out.append(Sample.average(bucket)); bucket.removeAll() }
                key = k
                keyTime = Sample.stamp.date(from: k + ":00")
            }
            guard let time = keyTime else { continue }
            var s = Sample()
            s.time = time
            s.cpu = d("cpu_pct") ?? 0; s.ram = d("ram_pct") ?? 0; s.ramUsedGB = d("ram_gb") ?? 0; s.gpu = d("gpu_pct") ?? 0
            s.temp = d("temp_c"); s.gpuTemp = d("gpu_temp_c"); s.down = d("down_Bps") ?? 0; s.up = d("up_Bps") ?? 0
            s.pressure = Int(d("geheugendruk") ?? 1); s.diskFreeGB = d("schijf_vrij_gb") ?? 0; s.battery = d("batterij_pct")
            s.pingMs = d("ping_ms"); s.diskRead = d("lezen_Bps") ?? 0; s.diskWrite = d("schrijven_Bps") ?? 0; s.swapGB = d("swap_gb") ?? 0
            bucket.append(s)
        }
        if !bucket.isEmpty { out.append(Sample.average(bucket)) }
        return out
    }

    /// Eén zin over hoe het gaat, met een kleur.
    var status: (text: String, color: Color) {
        let s = latest
        let last = recent.suffix(15) // ~30 s
        let cpu = last.isEmpty ? 0 : last.reduce(0) { $0 + $1.cpu } / Double(last.count)
        let temp = s.temp ?? 0
        let d = UserDefaults.standard, warnTemp = d.double(forKey: "warnTemp")
        if s.pressure >= 4 { return ("Memory is full — close a few apps", .red) }
        if temp >= warnTemp + 10 { return ("Your Mac is very hot — it's slowing itself down", .red) }
        if cpu >= d.double(forKey: "warnCPU") { return ("Your Mac is busy", .orange) }
        if s.pressure == 2 { return ("Memory is filling up", .orange) }
        if temp >= warnTemp { return ("Your Mac is getting warm", .orange) }
        return ("All calm", .green)
    }
}


// MARK: - Meldingen per soort (sleutels alert.<id>.on/.limit/.min)

enum Alert: String, CaseIterable, Identifiable {
    case cpu, heat, mem, disk, net, battery, app
    var id: String { rawValue }
    func key(_ field: String) -> String { "alert.\(rawValue).\(field)" }
    var title: String {
        switch self {
        case .cpu: "Processor busy"
        case .heat: "Mac too hot"
        case .mem: "Memory full"
        case .disk: "Storage almost full"
        case .net: "Internet slow or down"
        case .battery: "Battery low"
        case .app: "One app hogging"
        }
    }
    /// Standaard: aan, grens, minuten op rij.
    var start: (on: Bool, limit: Double, minutes: Int) {
        switch self {
        case .cpu: (true, 90, 5)
        case .heat: (true, 100, 5)
        case .mem: (true, 4, 2)
        case .disk: (true, 10, 1)
        case .net: (false, 300, 3)
        case .battery: (false, 20, 1)
        case .app: (false, 50, 10)
        }
    }
    /// Instelbereik van de grens.
    var range: ClosedRange<Double> {
        switch self {
        case .cpu, .app: 20...100
        case .heat: 70...110
        case .mem: 2...4
        case .disk: 1...200
        case .net: 50...1000
        case .battery: 5...50
        }
    }
    var step: Double {
        switch self {
        case .mem: 2
        case .disk: 1
        case .net: 50
        default: 5
        }
    }
    /// De grens in woorden: "boven 90%", "minder dan 10 GB vrij".
    func describe(_ limit: Double) -> String {
        let v = Int(limit)
        switch self {
        case .cpu: return "above \(v)%"
        case .heat: return "above \(v) °C"
        case .mem: return limit >= 4 ? "pressure critical" : "pressure high"
        case .disk: return "less than \(v) GB free"
        case .net: return "above \(v) ms or offline"
        case .battery: return "\(v)% or less"
        case .app: return "above \(v)%"
        }
    }

    /// Melding-tekst als de afgelopen minuut (`last`) over de grens ging, anders nil.
    func check(_ last: [Sample], top: AppUsage?, limit: Double, minutes: Int, pingOn: Bool) -> String? {
        guard let s = last.last else { return nil }
        func avg(_ v: [Double]) -> Double { v.isEmpty ? 0 : v.reduce(0, +) / Double(v.count) }
        let cpu = avg(last.map(\.cpu))
        switch self {
        case .cpu:
            guard cpu >= limit else { return nil }
            return "Your Mac has been at \(Int(cpu))% processor for \(minutes) min" + (top.map { " — mostly because of \($0.name)" } ?? "")
        case .heat:
            let t = avg(last.compactMap(\.temp))
            return t >= limit ? "Your Mac has been at \(Int(t)) °C for \(minutes) min and is slowing itself down" : nil
        case .mem:
            guard Double(s.pressure) >= limit else { return nil }
            return "Your memory is full" + (top.map { " — \($0.name) is using \(one($0.memGB)) GB" } ?? "")
        case .disk:
            return s.diskFreeGB > 0 && s.diskFreeGB < limit ? "Only \(Int(s.diskFreeGB)) GB free on your SSD" : nil
        case .net:
            guard pingOn else { return nil }
            let pings = last.compactMap(\.pingMs)
            if pings.isEmpty { return "No internet for \(minutes) min" }
            return avg(pings) >= limit ? "Internet has been slow for \(minutes) min (\(Int(avg(pings))) ms)" : nil
        case .battery:
            guard let b = s.battery, !s.charging, b <= limit else { return nil }
            return "Battery at \(Int(b))% — plug in the charger"
        case .app:
            guard let top, top.cpu >= limit else { return nil }
            return "\(top.name) has been using \(Int(top.cpu))% processor for \(minutes) min"
        }
    }

    static func selfTest() {
        var s = Sample(); s.cpu = 95; s.diskFreeGB = 5; s.battery = 15; s.pressure = 2
        let hot = Array(repeating: s, count: 30)
        precondition(Alert.cpu.check(hot, top: nil, limit: 90, minutes: 5, pingOn: true) != nil, "cpu-melding")
        precondition(Alert.cpu.check(hot, top: nil, limit: 99, minutes: 5, pingOn: true) == nil, "cpu onder grens")
        precondition(Alert.disk.check(hot, top: nil, limit: 10, minutes: 1, pingOn: true) != nil, "schijf-melding")
        precondition(Alert.mem.check(hot, top: nil, limit: 4, minutes: 2, pingOn: true) == nil, "geheugen hoog ≠ kritiek")
        precondition(Alert.battery.check(hot, top: nil, limit: 20, minutes: 1, pingOn: true) != nil, "batterij")
        precondition(Alert.net.check(hot, top: nil, limit: 300, minutes: 3, pingOn: false) == nil, "ping uit = geen melding")
        precondition(Alert.net.check(hot, top: nil, limit: 300, minutes: 3, pingOn: true) != nil, "offline")
        print("meldingen: ok")
    }
}
