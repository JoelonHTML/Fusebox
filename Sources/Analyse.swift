import SwiftUI
import UniformTypeIdentifiers

// MARK: - Analyse / Advies: terugkijken in de CSV-logs + "waarom is mijn Mac traag?"
//
// Zwaar werk (bestanden lezen) gebeurt alleen in Task.detached bij openen, ander tijdvak of ⟳.
// Per dag wordt het resultaat bewaard (op wijzigingsdatum van de bestanden), dus meestal wordt alleen vandaag opnieuw gelezen.

fileprivate enum Period: String, CaseIterable, Identifiable {
    case today = "today", yesterday = "yesterday", week = "week"
    var id: String { rawValue }
    /// Tekst voor de gebruiker; rawValue is een vaste korte id (veilig om op te slaan en te delen).
    var title: String {
        switch self {
        case .today: "Today"
        case .yesterday: "Yesterday"
        case .week: "7 days"
        }
    }
    /// Zelfde, vertaald (voor op het scherm); `title` blijft Engels voor bestandsnamen en het gedeelde rapport.
    var label: String {
        switch self {
        case .today: L("Today")
        case .yesterday: L("Yesterday")
        case .week: L("7 days")
        }
    }
    /// Dagen terug vanaf vandaag (0 = vandaag), oudste eerst.
    var offsets: [Int] { switch self { case .today: [0]; case .yesterday: [1]; case .week: Array((0...6).reversed()) } }
    var previous: [Int] { switch self { case .today: [1]; case .yesterday: [2]; case .week: Array((7...13).reversed()) } }
    var previousName: String { switch self { case .today: L("yesterday"); case .yesterday: L("the day before"); case .week: L("the week before") } }
    var when: String { switch self { case .today: L("today"); case .yesterday: L("yesterday"); case .week: L("in the last 7 days") } }
}

/// Eén minuut uit de hoofdlog (gemiddelde van de 2-secondenmetingen).
fileprivate struct Minute: Sendable {
    var time: Date
    var cpu: Double, ramGB: Double
    var pressure: Int = 1          // hoogste in die minuut
    var temp: Double? = nil        // gemiddelde
    var diskFreeGB: Double? = nil, diskTotalGB: Double? = nil
    var ping: Double? = nil        // gemiddelde ms
    var offline = false            // ping-kolom bestaat, maar meer dan de helft leeg (geen internet)
    var wifi: Double? = nil        // gemiddeld Wi-Fi-signaal (dBm)
    var wifiDrop = false           // Wi-Fi viel weg in deze minuut
}

fileprivate struct AppAgg: Sendable {
    var kind: String; var cpu = 0.0, mem = 0.0, energy = 0.0, minutes = 0
    var peak = 0.0, peakMem = 0.0                      // hoogste minuut
    var gpu = 0.0, gpuMinutes = 0                      // alleen regels met gpu_pct
    var shared = 0.0                                   // energie in minuten mét mac_energie_w (voor het aandeel)
    var hours = [Int](repeating: 0, count: 24)         // minuten per uur van de dag
}

fileprivate struct Day: Sendable {
    var minutes: [Minute] = []
    var apps: [String: AppAgg] = [:]
    var busyApps: [Date: [String: Double]] = [:]   // alleen minuten met CPU ≥ 85: app → cpu
    var leaks: [Diag.Leak] = [], runaways: [Diag.Run] = [], jobs: [Diag.Run] = []
    var jobMinutes: [String: Int] = [:]             // macOS-huishouding: klus → minuten bezig
    var macEnergy = 0.0                             // som van mac_energie_w (W·min) over de minuten die het hebben
    var since: Date?                                // eerste minuut met de nieuwe kolommen
}

fileprivate struct AppStat: Sendable, Identifiable {
    let name: String, kind: String, cpu: Double, mem: Double, energy: Double, minutes: Int
    var id: String { name }
}

fileprivate struct Busy: Sendable, Identifiable {
    let start: Date, end: Date, cpu: Double, app: String?
    var id: Date { start }
}

fileprivate struct Event: Sendable, Identifiable {
    let start: Date, end: Date, icon: String, text: String, level: Int   // level 0 = ter info, 1 = let op, 2 = ernstig
    var id: String { "\(start.timeIntervalSince1970)\(icon)\(text)" }
}

fileprivate struct LogFile: Sendable, Identifiable { let name: String, bytes: Int; var id: String { name } }

fileprivate struct Report: Sendable {
    var minutes = 0, busyMin = 0, pressureMin = 0, hotMin = 0
    var wifiDrops = 0, weakWifiMin = 0
    var first: Date?
    var diskFreeGB: Double?, diskTotalGB: Double?
    var score: Int?, reason = L("No measurements for this period yet")
    var prevScore: Int?
    var apps: [AppStat] = []
    var topCPU: [AppStat] = [], topRAM: [AppStat] = [], topSeen: [AppStat] = [], topEnergy: [AppStat] = []
    var busy: [Busy] = []
    var peaks: [Peak] = [], peakFrom: Date?, peakDays = 1   // uit peaks-*.csv; tijdlijn vanaf `peakFrom`
    var events: [Event] = []
    var files: [LogFile] = []
    var leaks: [Diag.Leak] = [], runaways: [Diag.Run] = []
    var jobMinutes: [String: Int] = [:]
    var crashes: Diag.Crashes?, battery: Diag.Battery?, boot: Date?
    var dusty = false                               // ventilator hard bij weinig werk (FanDust, 7 dagen)
    var when = L("today")                           // "today" / "yesterday" / "in the last 7 days" (vertaald)
    var uptimeDays: Int { boot.map { Int(Date().timeIntervalSince($0) / 86_400) } ?? 0 }
}

fileprivate struct Tip: Identifiable {
    enum Action { case stop(String), open(URL, String) }
    let id: String, text: String, action: Action?

    /// Iets dat echt aandacht vraagt (crashlus, geheugen op, schijf vol, warm, versleten accu): waarschuwingsdriehoek i.p.v. lampje.
    var serious: Bool { ["loop", "jetsam", "disk", "mem", "hot", "battery", "leak"].contains { id.hasPrefix($0) } }

    /// Wat er is (vet) en wat je kunt doen (klein): splitsen op " — ", anders op de eerste zin.
    var parts: (head: String, rest: String?) {
        let r = text.range(of: " — ") ?? text.range(of: ". ")
        guard let r else { return (text, nil) }
        let head = text[..<r.lowerBound] + (text[r] == ". " ? "." : "")
        let rest = text[r.upperBound...].trimmingCharacters(in: .whitespacesAndNewlines)
        return (String(head), rest.isEmpty ? nil : rest.prefix(1).uppercased() + rest.dropFirst())
    }

    /// In welke Checkup-categorie de tip staat (op het begin van de id): perf, power, stability, storage of history.
    var category: String {
        let starts = { (p: [String]) in p.contains { id.hasPrefix($0) } }
        if starts(["loop", "crashed", "stopped", "jetsam", "wifidrop", "wifiweak"]) { return "stability" }
        if starts(["battery"]) { return "power" }
        if starts(["disk", "login", "orphan"]) { return "storage" }
        return "perf"   // geheugen, warmte, stof, zware apps, herstarten
    }
}

enum Analysis {
    fileprivate static let busyCPU = 85.0, hotTemp = 95.0, slowPing = 200.0, weakWifi = -75.0
    fileprivate static let cores = Double(ProcessInfo.processInfo.activeProcessorCount)

    /// Fallback voor oude logs zonder kolom schijf_totaal_gb.
    fileprivate static let diskTotalGB: Double = {
        let v = try? URL(fileURLWithPath: "/").resourceValues(forKeys: [.volumeTotalCapacityKey]).volumeTotalCapacity
        return v.map { Double($0) / 1_000_000_000 } ?? 0
    }()
    fileprivate static func diskLow(free: Double?, total: Double?) -> Bool {
        guard let free, free > 0 else { return false }
        let t = total.flatMap { $0 > 0 ? $0 : nil } ?? diskTotalGB
        return t > 0 && free < t * 0.1
    }

    // MARK: Lezen

    /// Hoofdlog → minuten. Kolommen op naam (oude logs hebben andere kolommen); kapotte regels worden overgeslagen.
    fileprivate static func parseMain(_ text: String) -> [Minute] {
        let lines = text.split(separator: "\n")
        guard let head = lines.first else { return [] }
        var col: [Substring: Int] = [:]
        for (i, c) in head.split(separator: ",").enumerated() { col[c] = i }
        guard let iCPU = col["cpu_pct"] else { return [] }
        let iRAM = col["ram_gb"], iP = col["geheugendruk"], iT = col["temp_c"], iD = col["schijf_vrij_gb"]
        let iDT = col["schijf_totaal_gb"], iPing = col["ping_ms"], iW = col["wifi_dbm"]
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "yyyy-MM-dd HH:mm"
        var out: [Minute] = []
        var key: Substring = "", time: Date?
        var n = 0.0, cpu = 0.0, ram = 0.0, p = 1, tSum = 0.0, tN = 0.0, disk: Double?, total: Double?, pSum = 0.0, pN = 0.0, wSum = 0.0, wN = 0.0, wDrop = false
        func close() {
            guard let time, n > 0 else { return }
            out.append(Minute(time: time, cpu: cpu / n, ramGB: ram / n, pressure: p, temp: tN > 0 ? tSum / tN : nil,
                              diskFreeGB: disk, diskTotalGB: total, ping: pN > 0 ? pSum / pN : nil, offline: iPing != nil && pN < n / 2,
                              wifi: wN > 0 ? wSum / wN : nil, wifiDrop: wDrop))
        }
        for line in lines.dropFirst() {
            let v = line.split(separator: ",", omittingEmptySubsequences: false)
            func d(_ i: Int?) -> Double? { guard let i, i < v.count else { return nil }; return Double(v[i]) }
            guard v[0].count >= 16, let c = d(iCPU) else { continue }
            let k = v[0].prefix(16)
            if k != key {
                close()
                key = k; time = f.date(from: String(k))
                n = 0; cpu = 0; ram = 0; p = 1; tSum = 0; tN = 0; disk = nil; total = nil; pSum = 0; pN = 0; wSum = 0; wN = 0; wDrop = false
            }
            n += 1; cpu += c; ram += d(iRAM) ?? 0
            p = max(p, d(iP).flatMap { Int(exactly: $0.rounded()) } ?? 1)   // "nan"/"inf" uit een bewerkt bestand: niet crashen
            if let t = d(iT) { tSum += t; tN += 1 }
            if let x = d(iPing) { pSum += x; pN += 1 }
            if let x = d(iW) { if x == 0 { wDrop = true } else if x < 0 { wSum += x; wN += 1 } }
            if let x = d(iD) { disk = x }
            if let x = d(iDT) { total = x }
        }
        close()
        return out
    }

    /// Apps-log → totaal per app, per drukke minuut welke apps het meest deden, plus lekken/weglopers/huishouding.
    /// Kolommen op naam (zonder kop: vaste volgorde tijd,app,soort,cpu_pct,geheugen_gb,energie_w).
    fileprivate static func parseApps(_ text: String, busy: Set<Date>, into day: inout Day, cores: Double = cores) {
        let lines = text.split(separator: "\n")
        var col: [Substring: Int] = [:]
        for (i, c) in (lines.first ?? "").split(separator: ",").enumerated() { col[c] = i }
        let iT = col["tijd"] ?? 0, iA = col["app"] ?? 1, iK = col["soort"] ?? 2, iC = col["cpu_pct"] ?? 3, iM = col["geheugen_gb"] ?? 4, iE = col["energie_w"]
        let iG = col["gpu_pct"], iME = col["mac_energie_w"]
        let need = max(iT, iA, iK, iC, iM)
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "yyyy-MM-dd HH:mm"
        var lastKey: Substring = "", lastTime: Date?, macKey: Substring = ""
        var series: [String: [Date: (cpu: Double, mem: Double)]] = [:], kinds: [String: String] = [:]
        for line in lines.dropFirst() {
            let v = line.split(separator: ",", omittingEmptySubsequences: false)
            guard v.count > need, v[iT].count >= 16, let cpu = Double(v[iC]), let mem = Double(v[iM]) else { continue }
            let kind = String(v[iK]), name = kind == "macos" && v[iA] == "macOS zelf" ? "macOS itself" : String(v[iA])
            func opt(_ i: Int?) -> Double? { i.flatMap { $0 < v.count ? Double(v[$0]) : nil } }
            let energy = opt(iE) ?? 0, macW = opt(iME)
            day.apps[name, default: AppAgg(kind: kind)].cpu += cpu
            day.apps[name]!.mem += mem
            day.apps[name]!.energy += energy
            day.apps[name]!.minutes += 1
            if let g = opt(iG) { day.apps[name]!.gpu += g; day.apps[name]!.gpuMinutes += 1 }
            if macW != nil { day.apps[name]!.shared += energy }
            let k = v[iT].prefix(16)
            if k != lastKey { lastKey = k; lastTime = f.date(from: String(k)) }
            guard let t = lastTime else { continue }
            if let macW, k != macKey { macKey = k; day.macEnergy += macW; day.since = day.since ?? t }   // één keer per minuut
            // dezelfde naam kan vaker per minuut voorkomen (Spotlight = mds + mdworker): optellen
            let prev = series[name]?[t] ?? (0, 0)
            series[name, default: [:]][t] = (prev.cpu + cpu, prev.mem + mem)
            kinds[name] = kind
            if kind != "macos", busy.contains(t) { day.busyApps[t, default: [:]][name, default: 0] += cpu }
        }
        // Per app: hoogste minuut en minuten per uur (zelfde naam vaker per minuut is hierboven al opgeteld)
        let cal = Calendar.current
        for (name, pts) in series {
            for (t, p) in pts {
                day.apps[name]!.hours[cal.component(.hour, from: t)] += 1
                day.apps[name]!.peak = max(day.apps[name]!.peak, p.cpu)
                day.apps[name]!.peakMem = max(day.apps[name]!.peakMem, p.mem)
            }
        }
        // ponytail: per dag; een lek dat over middernacht loopt wordt in twee stukken beoordeeld
        let pts = series.mapValues { $0.sorted { $0.key < $1.key }.map { Diag.Point(t: $0.key, cpu: $0.value.cpu, mem: $0.value.mem) } }
        let own = pts.filter { kinds[$0.key] != "macos" }
        day.leaks = Diag.leaks(own)
        day.runaways = Diag.runaways(own, cores: cores)
        (day.jobMinutes, day.jobs) = Diag.housekeeping(pts, cores: cores)
    }

    private final class Cache: @unchecked Sendable {
        let lock = NSLock()
        var days: [String: (stamp: String, day: Day)] = [:]
    }
    private static let cache = Cache()

    fileprivate static func dayName(_ date: Date) -> String {
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.calendar = Calendar(identifier: .gregorian); f.dateFormat = "yyyy-MM-dd"; return f.string(from: date)
    }

    /// `withMain: false` = alleen de apps-log (app-pagina: de hoofdlog is daar niet nodig, scheelt bij 30 dagen veel lezen).
    fileprivate static func day(_ date: Date, dir: URL, withMain: Bool = true) -> Day {
        let name = dayName(date), key = withMain ? name : name + "~apps"
        let main = dir.appendingPathComponent("meterkast-\(name).csv"), apps = dir.appendingPathComponent("apps-\(name).csv")
        func mtime(_ u: URL) -> String { ((try? u.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate)?.timeIntervalSince1970).map { "\($0)" } ?? "-" }
        let stamp = (withMain ? mtime(main) : "") + "|" + mtime(apps)
        cache.lock.lock()
        if let c = cache.days[key], c.stamp == stamp { cache.lock.unlock(); return c.day }
        cache.lock.unlock()
        var d = Day()
        if withMain, let t = try? String(contentsOf: main, encoding: .utf8) { d.minutes = parseMain(t) }
        if let t = try? String(contentsOf: apps, encoding: .utf8) {
            parseApps(t, busy: Set(d.minutes.filter { $0.cpu >= busyCPU }.map(\.time)), into: &d)
        }
        cache.lock.lock(); cache.days[key] = (stamp, d); cache.lock.unlock()
        return d
    }

    fileprivate static func merge(_ days: [Day]) -> Day {
        var out = Day()
        for d in days {
            out.minutes += d.minutes
            out.busyApps.merge(d.busyApps) { $1 }
            out.leaks += d.leaks; out.runaways += d.runaways; out.jobs += d.jobs
            out.jobMinutes.merge(d.jobMinutes, uniquingKeysWith: +)
            out.macEnergy += d.macEnergy
            out.since = [out.since, d.since].compactMap { $0 }.min()
            for (k, a) in d.apps {
                out.apps[k, default: AppAgg(kind: a.kind)].cpu += a.cpu
                out.apps[k]!.mem += a.mem
                out.apps[k]!.energy += a.energy
                out.apps[k]!.minutes += a.minutes
                out.apps[k]!.peak = max(out.apps[k]!.peak, a.peak)
                out.apps[k]!.peakMem = max(out.apps[k]!.peakMem, a.peakMem)
                out.apps[k]!.gpu += a.gpu; out.apps[k]!.gpuMinutes += a.gpuMinutes
                out.apps[k]!.shared += a.shared
                out.apps[k]!.hours = zip(out.apps[k]!.hours, a.hours).map(+)
            }
        }
        return out
    }

    fileprivate static func dates(_ offsets: [Int], now: Date) -> [Date] {
        offsets.compactMap { Calendar.current.date(byAdding: .day, value: -$0, to: now) }
    }

    fileprivate static func load(_ p: Period, dir: URL, now: Date = Date()) -> Report {
        let current = dates(p.offsets, now: now)
        var r = report(merge(current.map { day($0, dir: dir) }))
        r.prevScore = score(merge(dates(p.previous, now: now).map { day($0, dir: dir) }).minutes).score
        r.peaks = current.flatMap { Peaks.parse((try? String(contentsOf: dir.appendingPathComponent("peaks-\(dayName($0)).csv"), encoding: .utf8)) ?? "") }
        r.peakFrom = current.first.map { Calendar.current.startOfDay(for: $0) }
        r.peakDays = current.count
        r.files = current.flatMap { d in ["meterkast", "apps", "peaks"].compactMap { prefix -> LogFile? in
            let name = "\(prefix)-\(dayName(d)).csv"
            let size = (try? dir.appendingPathComponent(name).resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? nil
            return size.map { LogFile(name: name, bytes: $0) }
        } }
        // Alleen bij openen/verversen: rapporten van macOS, batterij, opstarttijd
        let cal = Calendar.current
        r.crashes = Diag.crashes(from: cal.startOfDay(for: current.first ?? now), to: p == .yesterday ? cal.startOfDay(for: now) : .distantFuture)
        r.battery = Diag.battery()
        r.boot = Diag.bootDate()
        r.dusty = FanDust.check(dir: dir, now: now)
        r.when = p.when
        return r
    }

    // MARK: Rekenen

    fileprivate static func report(_ d: Day) -> Report {
        var r = Report()
        let m = d.minutes
        r.minutes = m.count
        r.first = m.first?.time
        r.busyMin = m.filter { $0.cpu >= busyCPU }.count
        r.pressureMin = m.filter { $0.pressure >= 2 }.count
        r.hotMin = m.filter { ($0.temp ?? 0) >= hotTemp }.count
        r.wifiDrops = wifiDrops(m).count
        r.weakWifiMin = m.filter { !$0.wifiDrop && ($0.wifi ?? 0) < weakWifi }.count
        let lastDisk = m.last { $0.diskFreeGB != nil }
        r.diskFreeGB = lastDisk?.diskFreeGB; r.diskTotalGB = lastDisk?.diskTotalGB
        (r.score, r.reason) = score(m)
        r.apps = d.apps.map { AppStat(name: $0.key, kind: $0.value.kind, cpu: $0.value.cpu / Double($0.value.minutes),
                                      mem: $0.value.mem / Double($0.value.minutes), energy: $0.value.energy / Double($0.value.minutes), minutes: $0.value.minutes) }
        // Apps die maar een paar minuten in de top stonden zeggen weinig over de periode
        let solid = r.apps.filter { $0.minutes >= min(5, max(r.minutes / 20, 1)) }
        r.topCPU = Array(solid.sorted { $0.cpu > $1.cpu }.prefix(5))
        r.topRAM = Array(solid.sorted { $0.mem > $1.mem }.prefix(5))
        r.topSeen = Array(r.apps.filter { $0.kind != "macos" }.sorted { $0.minutes > $1.minutes }.prefix(5))
        r.topEnergy = Array(solid.filter { $0.energy > 0 }.sorted { $0.energy > $1.energy }.prefix(5))
        r.busy = busyMoments(m, apps: d.busyApps)
        // per app het grootste lek over de hele periode
        r.leaks = Dictionary(grouping: d.leaks, by: \.app).compactMap { $0.value.max { $0.to - $0.from < $1.to - $1.from } }
            .sorted { $0.to - $0.from > $1.to - $1.from }
        r.runaways = d.runaways.sorted { $0.start > $1.start }
        r.jobMinutes = d.jobMinutes
        let found = d.leaks.map { Event(start: $0.start, end: $0.end, icon: "memorychip", text: L("\($0.app) grew from \(one($0.from)) to \(one($0.to)) GB"), level: 1) }
            + d.runaways.map { Event(start: $0.start, end: $0.end, icon: "cpu", text: L("\($0.app) used a full processor core for \(mins($0.minutes))"), level: 1) }
            + d.jobs.map { Event(start: $0.start, end: $0.end, icon: "gearshape", text: L("\($0.app) — passes by itself"), level: 0) }
        r.events = Array((events(m, apps: d.busyApps) + found).sorted { $0.start > $1.start }.prefix(30))
        return r
    }

    /// Gezondheidsscore 1–10. Begin op 10, dan aftrek (aandeel = deel van de gemeten minuten):
    ///   - CPU ≥ 85 %:          aandeel × 20, max 4   (20 % van de tijd druk = −4)
    ///   - geheugendruk ≥ 2:     aandeel × 15, max 3   (20 % van de tijd = −3)
    ///   - temperatuur ≥ 95 °C:  aandeel × 10, max 2   (20 % van de tijd = −2)
    ///   - schijf < 10 % vrij (laatste meting): −1
    /// Afgerond, minimaal 1. Reden = de grootste aftrekpost (als die ≥ 0,25 is, anders "rustig").
    fileprivate static func score(_ m: [Minute]) -> (score: Int?, reason: String) {
        guard !m.isEmpty else { return (nil, L("No measurements for this period yet")) }
        let n = Double(m.count)
        let busy = Double(m.filter { $0.cpu >= busyCPU }.count) / n
        let mem = Double(m.filter { $0.pressure >= 2 }.count) / n
        let hot = Double(m.filter { ($0.temp ?? 0) >= hotTemp }.count) / n
        let last = m.last { $0.diskFreeGB != nil }
        let low = diskLow(free: last?.diskFreeGB, total: last?.diskTotalGB)
        let minus: [(Double, String)] = [
            (min(busy * 20, 4), L("\(pct(busy * 100)) of the time the CPU was busy (≥ 85%)")),
            (min(mem * 15, 3), L("\(pct(mem * 100)) of the time memory was (almost) full")),
            (min(hot * 10, 2), L("\(pct(hot * 100)) of the time your Mac was hot (≥ 95 °C)")),
            (low ? 1 : 0, L("your disk is almost full")),
        ]
        let s = max(1, Int((10 - minus.reduce(0) { $0 + $1.0 }).rounded()))
        guard let worst = minus.max(by: { $0.0 < $1.0 }), worst.0 >= 0.25 else { return (s, L("Calm: not busy, memory and temperature fine")) }
        return (s, worst.1.prefix(1).uppercased() + worst.1.dropFirst())
    }

    /// Reeksen opeenvolgende minuten (gat ≤ `gap` s) waarvoor `test` geldt, van minstens `minLength` minuten, oudste eerst.
    fileprivate static func runs(_ m: [Minute], minLength: Int, gap: Double = 90, _ test: (Minute) -> Bool) -> [[Minute]] {
        var out: [[Minute]] = [], cur: [Minute] = []
        for x in m {
            if test(x), let last = cur.last, x.time.timeIntervalSince(last.time) <= gap { cur.append(x); continue }
            if cur.count >= minLength { out.append(cur) }
            cur = test(x) ? [x] : []
        }
        if cur.count >= minLength { out.append(cur) }
        return out
    }

    /// Wi-Fi-uitval: opeenvolgende minuten waarin hij wegviel = één keer.
    fileprivate static func wifiDrops(_ m: [Minute]) -> [[Minute]] { runs(m, minLength: 1) { $0.wifiDrop } }

    /// Drukke momenten: ≥ 5 opeenvolgende minuten met CPU ≥ 85 %, nieuwste eerst, max 5.
    fileprivate static func busyMoments(_ m: [Minute], apps: [Date: [String: Double]], limit: Int = 5) -> [Busy] {
        runs(m, minLength: 5) { $0.cpu >= busyCPU }.reversed().prefix(limit).map { run in
            var sum: [String: Double] = [:]
            for x in run { sum.merge(apps[x.time] ?? [:], uniquingKeysWith: +) }
            return Busy(start: run[0].time, end: run[run.count - 1].time.addingTimeInterval(60),
                        cpu: run.reduce(0) { $0 + $1.cpu } / Double(run.count), app: sum.max { $0.value < $1.value }?.key)
        }
    }

    /// Logboek: opvallende gebeurtenissen, nieuwste eerst, max 30. Warmte/geheugen/internet: onderbrekingen ≤ 5 min worden samengevoegd.
    fileprivate static func events(_ m: [Minute], apps: [Date: [String: Double]]) -> [Event] {
        func span(_ run: [Minute]) -> (Date, Date) { (run[0].time, run[run.count - 1].time.addingTimeInterval(60)) }
        var out: [Event] = busyMoments(m, apps: apps, limit: 1000).map { b in
            Event(start: b.start, end: b.end, icon: "flame", text: L("Busy: \(pct(b.cpu)) CPU") + (b.app.map { L(" · mostly \($0)") } ?? ""), level: 1)
        }
        for run in runs(m, minLength: 1, gap: 360, { ($0.temp ?? 0) >= hotTemp }) {
            let (a, b) = span(run)
            out.append(Event(start: a, end: b, icon: "thermometer.high", text: L("Hot: up to \(Int(run.compactMap(\.temp).max() ?? hotTemp)) °C"), level: 2))
        }
        for run in runs(m, minLength: 1, gap: 360, { $0.pressure >= 2 }) {
            let (a, b) = span(run)
            out.append(Event(start: a, end: b, icon: "memorychip", text: run.contains { $0.pressure >= 4 } ? L("Memory full") : L("Memory filling up"), level: run.contains { $0.pressure >= 4 } ? 2 : 1))
        }
        for run in runs(m, minLength: 1, gap: 360, { $0.offline || ($0.ping ?? 0) >= slowPing }) {
            let (a, b) = span(run)
            let pings = run.compactMap(\.ping)
            let text = run.allSatisfy(\.offline) ? L("Internet down") : L("Internet slow: avg. \(Int(pings.reduce(0, +) / Double(max(pings.count, 1)))) ms")
            out.append(Event(start: a, end: b, icon: "wifi.exclamationmark", text: text, level: 1))
        }
        for run in wifiDrops(m) {
            let (a, b) = span(run)
            out.append(Event(start: a, end: b, icon: "wifi.slash", text: run.count > 1 ? L("Wi-Fi dropped for \(mins(run.count))") : L("Wi-Fi dropped"), level: 1))
        }
        for run in runs(m, minLength: 1, { !$0.wifiDrop && ($0.wifi ?? 0) < weakWifi }) {
            let (a, b) = span(run)
            out.append(Event(start: a, end: b, icon: "wifi.exclamationmark", text: L("Weak Wi-Fi: around \(Int((run.compactMap(\.wifi).reduce(0, +) / Double(run.count)).rounded())) dBm"), level: 1))
        }
        if let low = m.first(where: { diskLow(free: $0.diskFreeGB, total: $0.diskTotalGB) }) {
            out.append(Event(start: low.time, end: low.time.addingTimeInterval(60), icon: "internaldrive",
                             text: L("Disk almost full: \(Int(low.diskFreeGB ?? 0)) GB free"), level: 2))
        }
        return Array(out.sorted { $0.start > $1.start }.prefix(30))
    }

    private static let browsers: Set<String> = ["Google Chrome", "Chrome", "Safari", "Brave Browser", "Brave", "Firefox", "Microsoft Edge", "Edge", "Arc"]

    @MainActor fileprivate static func tips(_ r: Report, current: [AppUsage]) -> [Tip] {
        var out: [Tip] = []
        let front = NSWorkspace.shared.frontmostApplication?.processIdentifier
        func canStop(_ name: String) -> Bool { current.first { $0.name == name && $0.canStop }.map { $0.runningApp?.processIdentifier != front } ?? false }
        for l in r.leaks.prefix(2) {
            out.append(Tip(id: "leak" + l.app, text: L("\(l.app) keeps growing: +\(one(l.perHour)) GB per hour for \(l.hours) h (now \(one(l.to)) GB) — restart it to free memory."),
                           action: canStop(l.app) ? .stop(l.app) : nil))
        }
        var seen = Set<String>()
        for x in r.runaways where seen.insert(x.app).inserted && seen.count <= 2 {
            out.append(Tip(id: "runaway" + x.app, text: L("\(x.app) used a full processor core for \(mins(x.minutes)) (\(span(x.start, x.end)))."),
                           action: canStop(x.app) ? .stop(x.app) : nil))
        }
        let leaking = Set(r.leaks.map(\.app))
        let candidates = r.apps.filter { $0.kind != "macos" && !leaking.contains($0.name) && ($0.mem > 2 || ($0.minutes >= 30 && ($0.mem > 0.3 || $0.cpu > 5))) }
        let base = out.count
        for a in candidates.sorted(by: { $0.mem > $1.mem }) where out.count < base + 3 {
            let stoppable = canStop(a.name)
            let text: String
            if a.mem > 2 {
                text = browsers.contains(a.name)
                    ? L("\(a.name) uses \(one(a.mem)) GB on average — close tabs you're not using; every tab costs memory")
                    : L("\(a.name) uses \(one(a.mem)) GB on average — close windows and documents you're not using")
            } else if stoppable {
                text = L("\(a.name) has been running for \(duration(a.minutes)) (avg. \(one(a.cpu))% CPU, \(one(a.mem)) GB) — quit it if you're not using it")
            } else { continue }
            out.append(Tip(id: "app" + a.name, text: text, action: stoppable ? .stop(a.name) : nil))
        }
        if let c = r.crashes {
            let loops = c.loops
            for (app, n) in loops.sorted(by: { $0.value > $1.value }).prefix(2) {
                out.append(Tip(id: "loop" + app, text: L("\(app) keeps crashing and restarting (\(n)× in one hour) — that costs battery. Update or remove it."), action: nil))
            }
            for (verb, list) in [("crashed", c.crashes.filter { loops[$0.key] == nil }), ("stopped responding", c.hangs)] {
                for (app, n) in list.sorted(by: { $0.value > $1.value }).prefix(2) where n >= 2 {
                    out.append(Tip(id: verb + app, text: verb == "crashed" ? L("\(app) crashed \(n) times \(r.when). Update or reinstall it.")
                                   : L("\(app) stopped responding \(n) times \(r.when). Update or reinstall it."), action: nil))
                }
            }
            if c.jetsam > 0 {
                out.append(Tip(id: "jetsam", text: c.jetsam == 1 ? L("macOS closed apps once \(r.when) because memory ran out. Quit apps you're not using, or restart your Mac.")
                               : L("macOS closed apps \(c.jetsam) times \(r.when) because memory ran out. Quit apps you're not using, or restart your Mac."), action: nil))
            }
        }
        if let b = r.battery, b.worn {
            let state = b.capacity.map { L("Your battery holds \($0)% of its original charge") } ?? L("Your battery may need attention")
            out.append(Tip(id: "battery", text: state + (b.condition.map { L(" (macOS says: \($0))") } ?? "") + L(". A battery service gives you longer battery life again."),
                           action: .open(URL(string: "x-apple.systempreferences:com.apple.Battery-Settings.extension")!, L("Open Battery"))))
        }
        if r.uptimeDays >= Diag.restartDays {
            out.append(Tip(id: "uptime", text: L("Not restarted for \(r.uptimeDays) days. A restart clears memory and finishes updates.\nHow to restart: click the Apple logo at the top left of the screen and choose Restart…"), action: nil))
        }
        if diskLow(free: r.diskFreeGB, total: r.diskTotalGB), let free = r.diskFreeGB {
            out.append(Tip(id: "disk", text: L("Your disk is almost full (\(Int(free)) GB free) — clean up large files; macOS gets slow without free space"),
                           action: .open(URL(string: "x-apple.systempreferences:com.apple.settings.Storage")!, L("Open Storage"))))
        }
        if r.wifiDrops >= 3 {
            out.append(Tip(id: "wifidrop", text: L("Wi-Fi dropped \(r.wifiDrops)× \(r.when). Move closer to the router or restart it; a cable never drops."), action: nil))
        }
        if r.weakWifiMin >= 15 {
            out.append(Tip(id: "wifiweak", text: L("Weak Wi-Fi signal for \(duration(r.weakWifiMin)) \(r.when) — move closer to the router, or add a mesh point where you work"), action: nil))
        }
        if r.pressureMin >= 30 {
            out.append(Tip(id: "mem", text: L("Memory was (almost) full for \(duration(r.pressureMin)) — close apps you're not using, or restart your Mac"), action: nil))
        }
        if r.hotMin >= 15 {
            out.append(Tip(id: "hot", text: L("Your Mac was hot (≥ 95 °C) for \(duration(r.hotMin)) — put it on a hard, flat surface and keep the vents clear"), action: nil))
        }
        if r.dusty { out.append(Tip(id: "dust", text: FanDust.tip, action: nil)) }
        out.append(Tip(id: "login", text: L("Check which apps open automatically at login — you can turn off what you don't need there"),
                       action: .open(URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension")!, L("Open Login Items"))))
        return out
    }

    /// "Waarom is mijn Mac traag?" op basis van nu. Max twee zinnen; app = wat je zou kunnen stoppen.
    /// `jobs` = macOS-huishouding die nu bezig is; `report` levert lekken/weglopers die nog lopen en de opstarttijd.
    fileprivate static func diagnose(_ s: Sample, cpu: Double, apps: [AppUsage], jobs: [String] = [], report: Report? = nil, now: Date = Date()) -> (text: String, app: AppUsage?) {
        let own = apps.filter { $0.kind != .macos }
        let cpuTop = own.max { $0.cpu < $1.cpu }, memTop = own.max { $0.memGB < $1.memGB }
        var why: [String] = [], app: AppUsage?
        var job = jobs.first.map { L("\($0) — this passes by itself, no action needed.") }
        if cpu >= busyCPU {
            if let a = cpuTop, a.cpu >= 10 { why.append(L("The CPU is at \(pct(cpu)), mostly because of \(a.name) (\(pct(a.cpu))).")); app = a }
            else if let j = job { why.append(L("The CPU is at \(pct(cpu)), mostly because of macOS itself: \(j)")); job = nil }
            else { why.append(L("The CPU is at \(pct(cpu)), mostly because of macOS itself.")) }
        }
        if s.pressure >= 2 {
            let m = memTop.map { L(" — \($0.name) is using \(one($0.memGB)) GB") } ?? ""
            why.append((s.pressure >= 4 ? L("Memory is full") : L("Memory is filling up")) + m + ".")
            if app == nil, let a = memTop, a.memGB >= 1 { app = a }
        }
        // Wegloper die nog bezig is (laatste minuut ≤ 5 min geleden) en nog niet genoemd
        if let r = report?.runaways.first(where: { now.timeIntervalSince($0.end) < 300 }), app?.name != r.app {
            why.append(L("\(r.app) has been using a full processor core for \(mins(r.minutes))."))
            if app == nil { app = own.first { $0.name == r.app } }
        }
        if let l = report?.leaks.first(where: { now.timeIntervalSince($0.end) < 1800 }), let a = own.first(where: { $0.name == l.app }) {
            why.append(L("\(l.app) grew from \(one(l.from)) to \(one(l.to)) GB in \(l.hours) h — quitting and reopening it frees that memory."))
            if app == nil { app = a }
        }
        if let t = s.temp, t >= hotTemp { why.append(L("Your Mac is hot (\(Int(t)) °C) and may be slowing itself down.")) }
        if diskLow(free: s.diskFreeGB, total: s.diskTotalGB) { why.append(L("Your disk is almost full (\(Int(s.diskFreeGB)) GB free).")) }
        if !why.isEmpty, let j = job { why.append(j); job = nil }
        if let p = s.pingMs, p >= 100 { why.append(L("Internet is responding slowly (\(Int(p)) ms) — that's probably not your Mac.")) }
        if !why.isEmpty, let d = report?.uptimeDays, d >= Diag.restartDays { why.append(L("Your Mac hasn't been restarted for \(d) days — a restart clears memory.")) }
        if why.isEmpty {
            let t = s.temp.map { ", \(Int($0)) °C" } ?? ""
            return (L("Nothing wrong: CPU \(pct(cpu)), memory normal\(t).") + (cpuTop.map { L(" Biggest user: \($0.name).") } ?? "") + (job.map { " " + $0 } ?? ""), nil)
        }
        return (why.prefix(2).joined(separator: " "), app?.canStop == true ? app : nil)
    }

    /// "Tue 14:20–14:35"
    fileprivate static func span(_ a: Date, _ b: Date) -> String {
        let d = DateFormatter(); d.locale = uiLocale; d.dateFormat = "EEE HH:mm"
        let t = DateFormatter(); t.dateFormat = "HH:mm"
        return b.timeIntervalSince(a) <= 60 ? d.string(from: a) : "\(d.string(from: a))–\(t.string(from: b))"
    }

    fileprivate static func busyLabel(_ b: Busy) -> String {
        "\(span(b.start, b.end)) · \(pct(b.cpu))" + (b.app.map { L(" · mostly \($0)") } ?? "")

    }

    /// "40 min", "2h 5m"
    fileprivate static func mins(_ m: Int) -> String { m < 60 ? "\(m) min" : duration(m) }

    /// Platte tekst om te delen (~60 regels). Alleen namen van apps; geen gebruikersnaam, paden, computernaam of adressen.
    fileprivate static func shareText(_ r: Report, period: Period, now: Date = Date()) -> String {
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "EEE d MMM yyyy HH:mm"
        let v = ProcessInfo.processInfo.operatingSystemVersion
        func names(_ l: [AppStat]) -> String { l.isEmpty ? "–" : l.map(\.name).joined(separator: ", ") }
        var o = ["Fusebox Checkup report — \(period.title)", "Created \(f.string(from: now))", "", "MAC",
                 "Model: \(Diag.sysctlString("hw.model") ?? "?")",
                 "Chip: \(Diag.sysctlString("machdep.cpu.brand_string") ?? "?")",
                 "Memory: \(Int((Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824).rounded())) GB",
                 "macOS: \(v.majorVersion).\(v.minorVersion).\(v.patchVersion)" + (Diag.sysctlString("kern.osversion").map { " (\($0))" } ?? "")]
        if let b = r.boot { o.append("On for: \(duration(Int(now.timeIntervalSince(b) / 60))) (since \(f.string(from: b)))") }
        if let free = r.diskFreeGB {
            let total = r.diskTotalGB.flatMap { $0 > 0 ? $0 : nil } ?? diskTotalGB
            o.append("Disk: \(Int(total)) GB, \(Int(free)) GB free")
        }
        if let b = r.battery { o += ["", "BATTERY", b.summary] }
        o += ["", "HEALTH (\(period.title.lowercased()))", "Score: \(r.score.map { "\($0)/10" } ?? "–") — \(r.reason)",
              "Measured: \(duration(r.minutes))", "Busy (CPU ≥ 85%): \(duration(r.busyMin))",
              "Memory (almost) full: \(duration(r.pressureMin))", "Hot (≥ 95 °C): \(duration(r.hotMin))"]
        if !r.busy.isEmpty { o += ["", "BUSY MOMENTS"] + r.busy.map { "- " + busyLabel($0) } }
        o += ["", "TOP APPS", "By CPU: " + names(r.topCPU), "By memory: " + names(r.topRAM), "By energy: " + names(r.topEnergy)]
        let found = r.leaks.prefix(5).map { "- \($0.app) grew from \(one($0.from)) to \(one($0.to)) GB in \($0.hours) h" }
            + r.runaways.prefix(5).map { "- \($0.app) used a full processor core for \(mins($0.minutes)) (\(span($0.start, $0.end)))" }
        if !found.isEmpty { o += ["", "FINDINGS"] + found }
        if let c = r.crashes, !c.isEmpty { o += ["", "CRASHES AND REPORTS FROM MACOS"] + c.rows.map { "- \($0.label): \($0.value)" } }
        if !r.jobMinutes.isEmpty {
            o += ["", "MACOS HOUSEKEEPING SEEN"] + r.jobMinutes.sorted { $0.value > $1.value }.map { "- \($0.key): \(duration($0.value))" }
        }
        return Diag.scrub(o.joined(separator: "\n") + "\n")
    }

    /// Hoofdlogs van de periode in één CSV, in de huidige kolomvolgorde (ontbrekende kolommen leeg).
    fileprivate static func combinedCSV(_ p: Period, dir: URL) -> String {
        let cols = Sample.csvHeader.dropLast().split(separator: ",").map(String.init)
        var out = Sample.csvHeader
        for date in dates(p.offsets, now: Date()) {
            guard let text = try? String(contentsOf: dir.appendingPathComponent("meterkast-\(dayName(date)).csv"), encoding: .utf8) else { continue }
            let lines = text.split(separator: "\n")
            guard let head = lines.first else { continue }
            let idx = head.split(separator: ",").map(String.init)
            let map = cols.map { idx.firstIndex(of: $0) }
            for line in lines.dropFirst() {
                let v = line.split(separator: ",", omittingEmptySubsequences: false)
                out += map.map { i in i.flatMap { $0 < v.count ? String(v[$0]) : nil } ?? "" }.joined(separator: ",") + "\n"
            }
        }
        return out
    }

    // MARK: Eén app terugkijken (app-pagina)

    /// Alles wat de app-pagina over een periode laat zien; uit dezelfde apps-logs (en cache) als Checkup.
    struct AppHistory: Sendable {
        var minutes = 0                                    // minuten gezien (open, of vóór de app-pagina: alleen in de top 10)
        var avgCPU = 0.0, peakCPU = 0.0, avgMem = 0.0, peakMem = 0.0
        var avgGPU: Double?                                // nil = nog geen gpu_pct in deze periode
        var energyWh = 0.0, avgW = 0.0
        var share: Double?                                 // deel (0…1) van de energie van alle processen; nil = nog niet gemeten
        var hours = [Int](repeating: 0, count: 24)         // minuten per uur van de dag
        var weekdays = [Int](repeating: 0, count: 7)       // minuten per weekdag, 0 = maandag
        var since: Date?                                   // eerste minuut met de nieuwe kolommen
    }

    /// `name` zoals in de log (komma's vervangen door spaties, zoals bij het schrijven).
    fileprivate static func appHistory(_ name: String, days: [(date: Date, day: Day)]) -> AppHistory {
        var h = AppHistory(), cpu = 0.0, mem = 0.0, gpu = 0.0, gpuMin = 0, shared = 0.0, mac = 0.0, rows = 0
        let cal = Calendar(identifier: .gregorian)
        for (date, d) in days {
            if let s = d.since { h.since = min(h.since ?? s, s) }
            mac += d.macEnergy
            guard let a = d.apps[name] else { continue }
            let seen = a.hours.reduce(0, +)
            h.minutes += seen
            h.hours = zip(h.hours, a.hours).map(+)
            h.weekdays[(cal.component(.weekday, from: date) + 5) % 7] += seen   // zondag = 1 → 6
            cpu += a.cpu; mem += a.mem; rows += a.minutes
            gpu += a.gpu; gpuMin += a.gpuMinutes
            h.energyWh += a.energy / 60; shared += a.shared
            h.peakCPU = max(h.peakCPU, a.peak); h.peakMem = max(h.peakMem, a.peakMem)
        }
        if rows > 0 { h.avgCPU = cpu / Double(rows); h.avgMem = mem / Double(rows); h.avgW = h.energyWh * 60 / Double(rows) }
        if gpuMin > 0 { h.avgGPU = gpu / Double(gpuMin) }
        if mac > 0 { h.share = min(shared / mac, 1) }
        return h
    }

    /// Laatste `days` dagen (vandaag meegeteld). Leest alleen apps-logs; per dag bewaard zolang het bestand niet wijzigt.
    static func appHistory(_ name: String, days n: Int, dir: URL, now: Date = Date()) -> AppHistory {
        appHistory(name, days: dates(Array((0..<n).reversed()), now: now).map { ($0, day($0, dir: dir, withMain: false)) })
    }

    /// Accu-invloed in gewone woorden, op het gemiddelde vermogen terwijl de app open is.
    /// < 0,5 W nauwelijks, < 2 W klein, < 6 W merkbaar, daarboven groot (een MacBook in rust zit rond 3–6 W totaal).
    static func batteryWords(avgW: Double) -> String {
        avgW < 0.5 ? L("Barely affects your battery") : avgW < 2 ? L("Small effect on your battery")
            : avgW < 6 ? L("Noticeable effect on your battery") : L("Big drain on your battery")
    }

    // MARK: Zelftest (Meterkast --selftest)

    static func selfTest() {
        // Oude kolomindeling (geen ping/gpu_temp), 10 drukke minuten (2 regels per minuut), dan 5 rustige, plus kapotte regels.
        var csv = "tijd,cpu_pct,ram_pct,ram_gb,geheugendruk,gpu_pct,temp_c,down_Bps,up_Bps,schijf_vrij_gb,batterij_pct\n"
        for i in 0..<15 {
            let cpu = i < 10 ? "95.0" : "10.0", p = i < 3 ? "2" : "1", t = i < 2 ? "97.0" : ""
            for s in ["05", "35"] { csv += "2026-11-16 14:\(20 + i):\(s),\(cpu),50,8.0,\(p),0,\(t),0,0,77.5,\n" }
        }
        csv += "rommel\n2026-11-16 14:99\n"
        let m = parseMain(csv)
        precondition(m.count == 15, "minuten \(m.count)")
        precondition(abs(m[0].cpu - 95) < 0.01 && m[0].pressure == 2 && m[0].temp == 97 && m[5].temp == nil, "minuutwaarden")
        precondition(m[14].diskFreeGB == 77.5 && !m[0].offline, "schijf/ping")

        var apps = AppUsage.csvHeader
        for i in 0..<15 {
            apps += "2026-11-16 14:\(20 + i):52,Xcode,app,\(i < 10 ? "60.0" : "2.0"),3.00,1.0\n"
            apps += "2026-11-16 14:\(20 + i):52,macOS zelf,macos,30.0,0.00,0.00\n"
        }
        var d = Day(); d.minutes = m
        parseApps(apps, busy: Set(m.filter { $0.cpu >= busyCPU }.map(\.time)), into: &d)
        precondition(d.apps["Xcode"]?.minutes == 15 && d.busyApps.count == 10, "apps")

        let r = report(d)
        precondition(r.busy.count == 1 && r.busy[0].app == "Xcode" && abs(r.busy[0].cpu - 95) < 0.01, "druk \(r.busy)")
        precondition(r.busy[0].end.timeIntervalSince(r.busy[0].start) == 600, "drukduur")
        precondition(busyLabel(r.busy[0]).hasSuffix("14:20–14:30 · 95% · mostly Xcode"), busyLabel(r.busy[0]))
        precondition(r.topRAM.first?.name == "Xcode" && abs(r.topRAM[0].mem - 3) < 0.01, "top ram")
        precondition(r.topSeen.first?.name == "Xcode" && r.topSeen[0].minutes == 15, "looptijd")
        // logboek: druk + heet + geheugen + wegloper (Xcode 10 min ≥ één kern) = 4, nieuwste eerst
        precondition(r.events.count == 4 && r.events[0].start >= r.events[3].start && r.events.contains { $0.text.hasPrefix("Hot: up to 97") }
                     && r.events.contains { $0.text == "Xcode used a full processor core for 10 min" }, "logboek \(r.events.map(\.text))")
        // 10/15 druk → −4, 3/15 geheugen → −3, 2/15 heet → −1,33 → 10 − 8,33 = 1,67 → 2
        precondition(r.score == 2, "score \(String(describing: r.score))")
        precondition(score([]).score == nil, "lege score")
        precondition(score([Minute(time: Date(), cpu: 5, ramGB: 4, temp: 50)]).score == 10, "rustige score")
        // Onderbroken reeks (gat > 90 s) telt niet als één drukte; 4 minuten is te kort
        let gap = (0..<8).map { Minute(time: Date(timeIntervalSince1970: Double($0 < 4 ? $0 : $0 + 5) * 60), cpu: 99, ramGB: 1) }
        precondition(busyMoments(gap, apps: [:]).isEmpty, "gat")
        // Nieuwe kolommen: lege ping = internet weg
        let net = parseMain("tijd,cpu_pct,ping_ms,schijf_vrij_gb,schijf_totaal_gb\n2026-11-16 10:00:01,5,,5,100\n2026-11-16 10:00:03,5,,5,100\n2026-11-16 10:01:01,5,250,5,100\n")
        precondition(net.count == 2 && net[0].offline && net[1].ping == 250 && diskLow(free: net[0].diskFreeGB, total: net[0].diskTotalGB), "net")
        precondition(events(net, apps: [:]).map(\.text).contains("Internet down") == false, "weg+traag is één reeks")
        precondition(events(net, apps: [:]).contains { $0.text.hasPrefix("Disk almost full") }, "schijf-event")
        // Wi-Fi: weg (2 min), zwak (1 min, gem. −81), weer weg (kort), daarna kabel (leeg)
        var wcsv = "tijd,cpu_pct,wifi_dbm\n"
        for (t, w) in [("00:01", "-60"), ("00:03", "0"), ("01:01", "0"), ("02:01", "-60"), ("03:01", "-80"), ("03:31", "-82"),
                       ("04:01", "-60"), ("05:01", "0"), ("06:01", "-60"), ("07:01", "")] { wcsv += "2026-11-16 10:\(t),5,\(w)\n" }
        let wm = parseMain(wcsv), wt = events(wm, apps: [:]).map(\.text)
        precondition(wm.count == 8 && wm[0].wifiDrop && wm[7].wifi == nil && !wm[7].wifiDrop, "wifi-minuten")
        precondition(wt.filter { $0.hasPrefix("Wi-Fi dropped") }.count == 2 && wt.contains("Wi-Fi dropped for 2 min") && wt.contains("Weak Wi-Fi: around -81 dBm"), "wifi-log \(wt)")
        var wd = Day(); wd.minutes = wm
        let wr = report(wd)
        precondition(wr.wifiDrops == 2 && wr.weakWifiMin == 1, "wifi-telling")

        var s = Sample(); s.cpu = 97; s.pressure = 1; s.temp = 60; s.diskFreeGB = 0
        let hog = AppUsage(id: "/x/Hog.app", name: "Hog", explanation: "", kind: .app, pids: [1], cpu: 80, memGB: 1, energyW: 0, icon: nil)
        let dg = diagnose(s, cpu: 97, apps: [hog])
        precondition(dg.text.contains("Hog") && dg.app?.name == "Hog", dg.text)
        precondition(diagnose(s, cpu: 5, apps: [hog]).text.hasPrefix("Nothing wrong"), "rustig")

        // Apps-log met andere kolomvolgorde: 4 uur lek + wegloper (10 kernen) + Spotlight-huishouding
        var log = "app,tijd,cpu_pct,soort,geheugen_gb,energie_w\n"
        let fm = DateFormatter(); fm.locale = Locale(identifier: "en_US_POSIX"); fm.dateFormat = "yyyy-MM-dd HH:mm:ss"
        let t0 = fm.date(from: "2026-11-16 08:00:10")!
        for i in 0..<240 {
            let t = fm.string(from: t0.addingTimeInterval(Double(i) * 60))
            log += "Leaky,\(t),1.0,app,\(String(format: "%.2f", 1 + 4 * Double(i) / 239)),0.5\n"
            if i < 30 { log += "Hog,\(t),15.0,app,0.50,3.0\n" }
            if i < 8 { log += "Spotlight (search),\(t),2.0,macos,0.10,0\nSpotlight (search),\(t),1.0,macos,0.05,0\n" }
        }
        var d2 = Day()
        parseApps(log, busy: [], into: &d2, cores: 10)
        precondition(d2.leaks.count == 1 && d2.leaks[0].app == "Leaky" && d2.leaks[0].hours == 4, "lek uit log \(d2.leaks)")
        precondition(d2.runaways.count == 1 && d2.runaways[0].app == "Hog" && d2.runaways[0].minutes == 30, "wegloper uit log")
        precondition(d2.jobMinutes == ["Spotlight is indexing": 8] && d2.jobs.count == 1, "huishouding uit log \(d2.jobMinutes)")
        precondition(abs((d2.apps["Hog"]?.energy ?? 0) - 90) < 0.01, "energie op kolomnaam")
        let r2 = report(d2)
        precondition(r2.events.contains { $0.text == "Leaky grew from 1.0 to 5.0 GB" } && r2.events.contains { $0.text.hasPrefix("Hog used a full processor core for 30 min") }
                     && r2.events.contains { $0.level == 0 && $0.text.hasPrefix("Spotlight is indexing") }, "logboek \(r2.events.map(\.text))")
        precondition(r2.topEnergy.first?.name == "Hog", "top energie")

        // Diagnose: drukte door macOS zelf + Spotlight; lopende wegloper en lek uit het rapport; lang aan
        var calm = Sample(); calm.cpu = 95; calm.diskFreeGB = 0
        let busyMac = diagnose(calm, cpu: 95, apps: [], jobs: ["Spotlight is indexing"])
        precondition(busyMac.text.contains("macOS itself: Spotlight is indexing — this passes by itself"), busyMac.text)
        precondition(diagnose(calm, cpu: 5, apps: [], jobs: ["iCloud is syncing"]).text.hasSuffix("iCloud is syncing — this passes by itself, no action needed."), "rustig + icloud")
        var r3 = r2; r3.boot = Date().addingTimeInterval(-20 * 86_400)
        let leaky = AppUsage(id: "/x/Leaky.app", name: "Leaky", explanation: "", kind: .app, pids: [1], cpu: 1, memGB: 5, energyW: 0, icon: nil)
        let lk = diagnose(calm, cpu: 5, apps: [leaky], report: r3, now: d2.leaks[0].end)
        precondition(lk.text.hasPrefix("Leaky grew from 1.0 to 5.0 GB in 4 h") && lk.app?.name == "Leaky" && lk.text.contains("restarted for 20 days") == (20 >= Diag.restartDays), lk.text)

        // Deelrapport: geen thuismap of gebruikersnaam
        r3.crashes = Diag.Crashes(crashes: ["OneDrive": 3])
        let txt = shareText(r3, period: .week)
        precondition(txt.contains("OneDrive: crashed 3×") && txt.contains("Leaky grew from") && txt.contains("By energy: Hog"), txt)
        precondition(!txt.contains("/Users/") && !txt.contains(NSUserName()), "privacy")

        // App-pagina: maandag een oude log (zonder gpu/mac-kolommen), dinsdag de nieuwe
        let old = "tijd,app,soort,cpu_pct,geheugen_gb,energie_w\n2026-11-16 09:00:05,Quillo,app,10.0,1.00,2.00\n"
        var new = AppUsage.csvHeader
        for i in 0..<4 {
            new += "2026-11-17 14:0\(i):05,Quillo,app,\(i == 2 ? "50.0" : "10.0"),\(i == 3 ? "2.00" : "1.00"),3.00,\(i == 0 ? "20.0" : "0.0"),12.00\n"
            new += "2026-11-17 14:0\(i):05,Brambl,app,1.0,0.50,9.00,0.0,12.00\n"
        }
        new += "2026-11-17 15:00:05,Brambl,app,1.0,0.50,6.00,0.0,6.00\n"
        var mon = Day(), tue = Day()
        parseApps(old, busy: [], into: &mon); parseApps(new, busy: [], into: &tue)
        let h = appHistory("Quillo", days: [(fm.date(from: "2026-11-16 12:00:00")!, mon), (fm.date(from: "2026-11-17 12:00:00")!, tue)])
        precondition(h.minutes == 5 && abs(h.avgCPU - 18) < 0.01 && h.peakCPU == 50, "app cpu \(h)")
        precondition(abs(h.avgMem - 1.2) < 0.01 && h.peakMem == 2, "app geheugen")
        precondition(h.hours[9] == 1 && h.hours[14] == 4 && h.hours.reduce(0, +) == 5, "per uur \(h.hours)")
        precondition(h.weekdays == [1, 4, 0, 0, 0, 0, 0], "per weekdag \(h.weekdays)")
        precondition(h.avgGPU == 5 && abs(h.energyWh - 14.0 / 60) < 0.001 && abs(h.avgW - 2.8) < 0.01, "gpu/energie")
        precondition(abs((h.share ?? 0) - 12.0 / 54) < 0.001, "aandeel \(String(describing: h.share))")   // 4×3 van (4×12 + 6)
        precondition(h.since == fm.date(from: "2026-11-17 14:00:00"), "sinds")
        let none = appHistory("Quillo", days: [(fm.date(from: "2026-11-16 12:00:00")!, mon)])
        precondition(none.share == nil && none.avgGPU == nil && none.since == nil && none.minutes == 1, "oude log")
        precondition(appHistory("Nobody", days: [(Date(), tue)]).minutes == 0, "onbekende app")
        precondition(batteryWords(avgW: 0.2).hasPrefix("Barely") && batteryWords(avgW: 2.8).hasPrefix("Noticeable") && batteryWords(avgW: 9).hasPrefix("Big"), "accu-woorden")
        precondition(AppMonitor.creatorPid("pid 35649, WindowServer") == 35649 && AppMonitor.creatorPid("kernel") == nil, "gpu-client pid")
        precondition(AppAbout.signer("Developer ID Application: Quillo Labs B.V. (AB12CD34EF)") == "Quillo Labs B.V. (AB12CD34EF)"
                     && AppAbout.signer("macOS Software Signing") == "Apple", "ondertekend door")
        print("analyse ok")
    }
}

// MARK: - Pagina

struct AnalysisView: View {
    @EnvironmentObject var meter: Meter
    var body: some View { AnalysisPage(meter: meter).equatable() }
}

/// De pagina zelf volgt alleen de programmalijst (per minuut), niet elke meting: anders werd het hele rapport
/// elke 2 s opnieuw opgebouwd (~10 ms) terwijl er niets verandert.
private struct AnalysisPage: View, Equatable {
    let meter: Meter
    @ObservedObject private var apps: AppMonitor
    @Environment(\.theme) private var theme
    @AppStorage("analysisPeriod") private var period: Period = .today
    @AppStorage("checkupStart") private var start = "last"   // "last" of een Period-id (Instellingen › Checkup)
    @AppStorage("checkupHide") private var hideRaw = ""      // onderdelen die uit staan
    @State private var report: Report?
    /// Laatste rapport per tijdvak: bij terugkomen op het tabblad staat het er meteen, daarna wordt het ververst.
    @MainActor private static var cache: [Period: Report] = [:]
    static func == (a: Self, b: Self) -> Bool { a.meter === b.meter }
    @State private var loading = false
    @State private var reload = 0
    @State private var columns = 1
    @State private var diagnosis: (text: String, app: AppUsage?)?
    @State private var confirm: AppUsage?
    @State private var note: String?
    @State private var checking = false
    @State private var share: String?
    @Environment(\.accessibilityReduceMotion) private var reduceMotion

    init(meter: Meter) {
        self.meter = meter
        _apps = ObservedObject(wrappedValue: meter.minuteApps)
        let d = UserDefaults.standard
        let p = Period(rawValue: d.string(forKey: "checkupStart") ?? "") ?? Period(rawValue: d.string(forKey: "analysisPeriod") ?? "") ?? .today
        _report = State(initialValue: MainActor.assumeIsolated { Self.cache[p] })
    }

    var body: some View {
        let tips = report.map { Analysis.tips($0, current: meter.minuteApps.apps) } ?? []
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                picker
                // Samenvatting bovenaan: gezondheidsscore, daarna de belangrijkste tips ("Why is my Mac slow?" staat onder Performance)
                if let report, shown("score") { part(report, tips, .score) }
                if report == nil { why }   // nog aan het laden: de knop alvast bruikbaar
                if let report {
                    VStack(alignment: .leading, spacing: 0) {
                        ForEach(groups(report)) { g in group(g, report, tips) }
                    }
                    if !groups(report).contains(where: { $0.id == "history" }) { exportBar(report, tips) }
                } else {
                    HStack { Spacer(); ProgressView().controlSize(.small); Spacer() }.padding(20)
                }
            }
            .padding(.horizontal, 12).padding(.bottom, 12)
        }
        .scrollIndicators(.never)
        .clipped()   // niet onder de doorzichtige werkbalk doorschuiven (dan verdwijnen de tab-iconen)
        .onGeometryChange(for: Int.self) { $0.size.width >= 620 ? 2 : 1 } action: { columns = $0 }
        .onAppear { if let p = Period(rawValue: start) { period = p } }   // vaste beginstand; "last" = laatst gekozen
        .task(id: "\(period.rawValue)#\(reload)") {
            loading = true
            let p = period, dir = meter.logDir
            if let c = Self.cache[p] { report = c }
            let r = await Task.detached(priority: .utility) { Analysis.load(p, dir: dir) }.value
            Self.cache[p] = r
            report = r
            loading = false
        }
        .alert("Quit \(confirm?.name ?? "")?", isPresented: Binding(get: { confirm != nil }, set: { if !$0 { confirm = nil } })) {
            Button("Stop", role: .destructive) {
                if let a = confirm, !meter.minuteApps.stop(a) { note = L("\(a.name) couldn't be stopped") } else { diagnosis = nil }
                confirm = nil
            }
            Button("Cancel", role: .cancel) { confirm = nil }
        } message: { Text("Unsaved work may be lost.") }
        .sheet(isPresented: Binding(get: { share != nil }, set: { if !$0 { share = nil } })) { shareSheet(share ?? "") }
    }

    // MARK: Categorieën

    /// Onderdeel aan in Instellingen › Checkup (en accu-onderdelen alleen op een Mac met accu).
    private func shown(_ id: String) -> Bool {
        let off = Set(hideRaw.split(separator: ",").map(String.init))
        return !off.contains(id) && (Power.hasBattery || !["drain", "sleep", "health"].contains(id))
    }

    /// Eén onderdeel uit het rapport (dezelfde kaart als in de PDF).
    private func part(_ r: Report, _ tips: [Tip], _ p: ReportSections.Part) -> some View {
        ReportSections(report: r, tips: tips, previousName: period.previousName, parts: [p], logDir: meter.logDir, act: act)
    }

    private struct Group: Identifiable { let id, title, icon, summary: String; let warn: Bool }

    /// De inklapbare categorieën; een categorie zonder zichtbare onderdelen valt weg.
    /// De samenvatting in de kop gebruikt alleen wat al bekend is (rapport of cache), niets wordt ervoor ingelezen.
    private func groups(_ r: Report) -> [Group] {
        var g: [Group] = []
        if ["tops", "busy", "peaks", "background"].contains(where: shown) {
            let top = r.topCPU.first.map { L("\($0.name) busiest") } ?? L("No app data yet")
            let peaks = r.peaks.isEmpty ? L("no peaks") : r.peaks.count == 1 ? L("1 peak") : L("\(r.peaks.count) peaks")
            let busy = r.busy.isEmpty ? [] : [r.busy.count == 1 ? L("1 busy moment") : L("\(r.busy.count) busy moments")]
            g.append(Group(id: "perf", title: L("Performance"), icon: "gauge.with.dots.needle.67percent",
                           summary: ([top] + busy + [peaks]).joined(separator: " · "), warn: !r.busy.isEmpty))
        }
        if ["battery", "drain", "sleep", "awake", "health"].contains(where: shown), Power.hasBattery || shown("awake") {
            let b = r.battery
            let lost = SleepDrainSection.cache?.last.flatMap { s in s.lost.flatMap { $0 > 0 ? L("lost \($0)% while asleep") : nil } }
            let parts = [lost, b?.capacity.map { L("\($0)% capacity") }, b?.cycles.map { L("\($0) cycles") }].compactMap { $0 }
            g.append(Group(id: "power", title: Power.hasBattery ? L("Battery & power") : L("Power"), icon: Power.hasBattery ? "battery.75percent" : "bolt",
                           summary: parts.isEmpty ? L("What uses energy and what keeps your Mac awake") : parts.joined(separator: " · "),
                           warn: b?.worn ?? false))
        }
        if shown("crashes") {
            let c = r.crashes ?? Diag.Crashes()
            let parts = [c.crashes.isEmpty ? nil : c.crashes.count == 1 ? L("1 app crashed") : L("\(c.crashes.count) apps crashed"),
                         c.hangs.isEmpty ? nil : L("\(c.hangs.count) stopped responding"),
                         c.jetsam > 0 ? L("out of memory \(c.jetsam)×") : nil].compactMap { $0 }
            g.append(Group(id: "stability", title: L("Stability"), icon: "exclamationmark.triangle",
                           summary: parts.isEmpty ? L("No crashes or hangs \(r.when)") : parts.joined(separator: " · "), warn: !c.crashes.isEmpty || !c.hangs.isEmpty))
        }
        if shown("startup") || shown("disk") {
            let items = StartupSection.cache
            let login = items.map { l in L("\(l.filter { $0.atLogin && !$0.orphan }.count) items start at login") }
            let orphans = items.map { $0.filter(\.orphan).count } ?? 0
            let free = r.diskFreeGB.map { L("\(Int($0)) GB free") }
            let parts = [login, orphans > 0 ? L("\(orphans) left behind") : nil, free].compactMap { $0 }
            g.append(Group(id: "storage", title: L("Startup & storage"), icon: "internaldrive",
                           summary: parts.isEmpty ? L("Login items and disk space") : parts.joined(separator: " · "),
                           warn: orphans > 0 || Analysis.diskLow(free: r.diskFreeGB, total: r.diskTotalGB)))
        }
        if shown("runtime") || shown("log") {
            let ev = r.events.isEmpty ? L("nothing unusual") : r.events.count == 1 ? L("1 event") : L("\(r.events.count) events")
            g.append(Group(id: "history", title: L("History & export"), icon: "clock.arrow.circlepath",
                           summary: L("Measured \(duration(r.minutes)) · \(ev)"), warn: false))
        }
        return g
    }

    private func group(_ g: Group, _ r: Report, _ tips: [Tip]) -> some View {
        let open = openIDs.contains(g.id)
        // Tips horen bij hun categorie: aantal vooraan in de kop, de tips zelf bovenaan als je openklapt
        let mine = shown("tips") ? tips.filter { $0.category == g.id } : []
        let summary = (mine.isEmpty ? [] : [mine.count == 1 ? L("1 tip") : L("\(mine.count) tips")]) + [g.summary]
        return VStack(alignment: .leading, spacing: 10) {
            Button {
                var s = openIDs
                if open { s.remove(g.id) } else { s.insert(g.id) }
                // Kort en goedkoop: alleen doorzichtigheid en een paar punten schuiven (geen vervaging/schaal/schaduw).
                // Low impact: het venster zet animaties uit via een transaction; dat blijft zo werken.
                // Eigen @State animeren (een @AppStorage-wijziging nam de animatie niet betrouwbaar mee); daarna pas opslaan
                withAnimation(reduceMotion ? .easeOut(duration: 0.2) : .spring(duration: 0.32, bounce: 0.12)) { openState = s }
                openRaw = s.sorted().joined(separator: ",")
            } label: {
                HStack(spacing: 10) {
                    Image(systemName: g.icon).font(.system(size: 15, weight: .semibold))
                        .foregroundStyle(g.warn ? theme.warn : link).frame(width: 22)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(g.title).font(theme.label(15).bold()).foregroundStyle(theme.text)
                        Text(summary.joined(separator: " · ")).font(theme.label(11)).foregroundStyle(g.warn ? theme.warn : theme.secondary).lineLimit(1).truncationMode(.tail)
                    }
                    Spacer(minLength: 6)
                    Image(systemName: "chevron.right").font(.system(size: 12, weight: .semibold)).foregroundStyle(theme.secondary)
                        .rotationEffect(.degrees(open ? 90 : 0))
                }
                .padding(.vertical, 10).contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .accessibilityLabel("\(g.title), \(open ? L("expanded") : L("collapsed"))")
            // Ingeklapt staat de inhoud niet in de boom: de .task van die kaarten (pmset, opstartitems, schijf) loopt dan niet
            if open {
                VStack(alignment: .leading, spacing: 10) {
                    if !mine.isEmpty { ReportSections(report: r, tips: mine, previousName: period.previousName, parts: [.tips], logDir: meter.logDir, act: act) }
                    content(g.id, r, tips)
                }
                .padding(.bottom, 12)
                // Minder beweging: alleen vervagen, niet schuiven
                .transition(reduceMotion ? .opacity : .asymmetric(insertion: .opacity.combined(with: .offset(y: -14)), removal: .opacity))
            }
        }
        .overlay(alignment: .top) { Rectangle().fill(theme.line).frame(height: 1) }
    }

    /// Accentkleur buiten de kaarten; bij kleurblokken is die te licht op de pagina, dan de tekstkleur.
    private var link: Color { theme.rowStyle == .blocks ? theme.text : theme.accent }
    @AppStorage("checkupOpen") private var openRaw = "perf"   // open categorieën, komma-gescheiden
    @State private var openState: Set<String>?
    private var openIDs: Set<String> { openState ?? Set(openRaw.split(separator: ",").map(String.init)) }

    @ViewBuilder private func content(_ id: String, _ r: Report, _ tips: [Tip]) -> some View {
        switch id {
        case "perf":
            why
            if shown("tops") { part(r, tips, .tops) }
            cols { if shown("peaks") { part(r, tips, .peaks) } } right: {
                if shown("busy") { part(r, tips, .busy) }
                if shown("background") { BackgroundSection(apps: meter.minuteApps.apps.map { ($0.name, $0.cpu) }, seen: r.jobMinutes, when: r.when, index: 11) }
            }
        case "power":
            cols {
                // "Battery health" toont capaciteit en cycli al (plus de lijn): de oude Battery-kaart alleen als die uit staat
                if shown("health") { BatteryHealthSection(index: 15, dir: meter.logDir) } else if shown("battery") { part(r, tips, .battery) }
                if shown("drain") { DrainSection(index: 12, dir: meter.logDir) }
            } right: {
                if shown("sleep") { SleepDrainSection(index: 13) }
                if shown("awake") { AwakeSection(index: 14) }
            }
        case "stability":
            if let c = r.crashes, !c.isEmpty { part(r, tips, .crashes) }
            else {
                VStack(alignment: .leading, spacing: 4) {
                    CardTitle(icon: "checkmark.seal", title: L("Crashes"), tone: 3)
                    Headline(big: L("None"), small: L("No apps crashed or stopped responding \(r.when)"), tone: 3)
                }
                .frame(maxWidth: .infinity, alignment: .leading).themedRow(theme, index: 8)
            }
        case "storage":
            cols { if shown("startup") { StartupSection(index: 9) } } right: { if shown("disk") { DiskSection(index: 10) } }
        default:
            cols { if shown("runtime") { part(r, tips, .runtime) } } right: { if shown("log") { part(r, tips, .log) } }
            exportBar(r, tips)
        }
    }

    /// Twee kolommen in een breed venster, anders onder elkaar.
    @ViewBuilder private func cols<L: View, R: View>(@ViewBuilder _ left: () -> L, @ViewBuilder right: () -> R) -> some View {
        if columns == 2 {
            HStack(alignment: .top, spacing: 12) {
                VStack(alignment: .leading, spacing: 10) { left() }.frame(maxWidth: .infinity, alignment: .topLeading)
                VStack(alignment: .leading, spacing: 10) { right() }.frame(maxWidth: .infinity, alignment: .topLeading)
            }
        } else {
            VStack(alignment: .leading, spacing: 10) { left(); right() }
        }
    }

    /// De drie belangrijkste tips; de rest achter "Show all".
    private var picker: some View {
        HStack(spacing: 8) {
            Picker("Period", selection: $period) { ForEach(Period.allCases) { Text($0.label).tag($0) } }
                .pickerStyle(.segmented).labelsHidden().controlSize(.small)
            Button { reload += 1 } label: { Image(systemName: "arrow.clockwise") }
                .buttonStyle(.plain).foregroundStyle(theme.secondary).help("Reload")
                .opacity(loading ? 0.4 : 1).disabled(loading)
        }
    }

    // MARK: Waarom traag?

    private var why: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                Image(systemName: "magnifyingglass").font(.system(size: 13 * theme.scale, weight: .semibold)).foregroundStyle(theme.tone(0)).frame(width: 18)
                Text("Why is my Mac slow?").font(theme.label(15).bold()).foregroundStyle(theme.onRow())
                Spacer()
                Button(diagnosis == nil ? "Check now" : "Check again") {
                    let last = meter.since(30)
                    let cpu = last.isEmpty ? meter.latest.cpu : last.reduce(0) { $0 + $1.cpu } / Double(last.count)
                    let s = meter.latest, apps = meter.minuteApps.apps, r = report
                    let light = apps.map { (name: $0.name, cpu: $0.cpu) }
                    checking = true
                    Task {
                        // ps/tmutil (~5–30 ms) buiten de hoofdthread
                        let jobs = await Task.detached(priority: .userInitiated) { Diag.housekeepingNow(apps: light, cores: Analysis.cores) }.value
                        diagnosis = Analysis.diagnose(s, cpu: cpu, apps: apps, jobs: jobs, report: r)
                        note = nil; checking = false
                    }
                }
                .buttonStyle(Pill(color: theme.onRow()))
                .disabled(checking)
            }
            if let d = diagnosis {
                Text(d.text).font(theme.label(12)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
                if meter.minuteApps.apps.isEmpty {
                    Text("Apps are measured every minute — the first measurement is coming soon.").font(theme.label(11)).foregroundStyle(theme.onRow(true))
                }
                if let a = d.app { Button("Stop \(a.name)") { confirm = a }.buttonStyle(Pill(color: theme.rowStyle == .blocks ? .white : theme.danger)) }
            }
            if let note { Text(note).font(theme.label(11)).foregroundStyle(theme.rowStyle == .blocks ? .white : theme.warn) }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)   // naast de score even hoog
        .themedRow(theme, index: 0)
    }

    private func act(_ a: Tip.Action) {
        switch a {
        case .open(let url, _): NSWorkspace.shared.open(url)
        case .stop(let name):
            if let app = meter.minuteApps.apps.first(where: { $0.name == name && $0.canStop }) { confirm = app } else { note = L("\(name) is no longer running") }
        }
    }

    // MARK: Export

    private func exportBar(_ r: Report, _ tips: [Tip]) -> some View {
        HStack(spacing: 8) {
            Spacer()
            Button("Share report…") { share = Analysis.shareText(r, period: period) }
            Button("Export CSV") { exportCSV() }
            Button("Export PDF") { exportPDF(r, tips) }
        }
        .buttonStyle(Pill(color: theme.secondary))
        .padding(.top, 4)
    }

    private func shareSheet(_ text: String) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Share report").font(theme.label(14))
            Text("Contains no user name, file names, computer name, serial number or network addresses.")
                .font(theme.label(11)).foregroundStyle(.secondary)
            ScrollView {
                Text(text).font(.system(size: 11, design: .monospaced)).textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading).padding(8)
            }
            .background(.quaternary.opacity(0.5), in: .rect(cornerRadius: 8))
            HStack(spacing: 8) {
                Button("Copy") { NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string) }
                Button("Save as .txt…") {
                    guard let url = save("fusebox-report.txt", .plainText) else { return }
                    do { try text.write(to: url, atomically: true, encoding: .utf8) } catch { note = L("Saving failed: \(error.localizedDescription)") }
                }
                Spacer()
                Button("Close") { share = nil }.keyboardShortcut(.cancelAction)
            }
        }
        .padding(16).frame(width: 540, height: 560)
    }

    private func save(_ name: String, _ type: UTType) -> URL? {
        let p = NSSavePanel()
        p.nameFieldStringValue = name
        p.allowedContentTypes = [type]
        return p.runModal() == .OK ? p.url : nil
    }

    private var fileStem: String { "fusebox-" + period.title.lowercased().replacingOccurrences(of: " ", with: "-") }

    private func exportCSV() {
        guard let url = save(fileStem + ".csv", .commaSeparatedText) else { return }
        let p = period, dir = meter.logDir
        Task.detached(priority: .utility) {
            do { try Analysis.combinedCSV(p, dir: dir).write(to: url, atomically: true, encoding: .utf8) }
            catch { let msg = error.localizedDescription; await MainActor.run { note = L("Saving failed: \(msg)") } }
        }
    }

    private func exportPDF(_ r: Report, _ tips: [Tip]) {
        guard let url = save(fileStem + ".pdf", .pdf) else { return }
        let content = VStack(alignment: .leading, spacing: 12) {
            Text("Fusebox — \(period.label)").font(.system(size: 20, weight: .semibold))
            Text(Date().formatted(Date.FormatStyle(date: .long, time: .shortened).locale(Locale(identifier: "en_US")))).font(.system(size: 11)).foregroundStyle(.secondary)
            ReportSections(report: r, tips: tips, previousName: period.previousName, parts: ReportSections.Part.allCases, logDir: nil, act: nil)
        }
        .padding(32).frame(width: 595, alignment: .topLeading)
        .background(.white)
        .foregroundStyle(Theme.minimal.text)
        .environment(\.theme, .minimal).environment(\.colorScheme, .light)
        var failed = false
        ImageRenderer(content: content).render { size, draw in
            var box = CGRect(origin: .zero, size: size)
            guard let pdf = CGContext(url as CFURL, mediaBox: &box, nil) else { failed = true; return }
            pdf.beginPDFPage(nil); draw(pdf); pdf.endPDFPage(); pdf.closePDF()
        }
        if failed { note = L("Creating the PDF failed") }
    }
}

// MARK: - Score, toplijsten, looptijd, drukte, tips, logboek (ook gebruikt voor de PDF)

fileprivate struct ReportSections: View {
    enum Part: String, CaseIterable { case score, tops, runtime, battery, busy, peaks, crashes, tips, log }
    @Environment(\.theme) private var theme
    let report: Report, tips: [Tip], previousName: String, parts: [Part]
    let logDir: URL?                                   // nil = geen "Open logmap"-knop (PDF)
    let act: ((Tip.Action) -> Void)?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(parts, id: \.self) { part in
                switch part {
                case .score: scoreRow
                case .tops:
                    ViewThatFits(in: .horizontal) {
                        HStack(alignment: .top, spacing: 8) { cpuTop; ramTop }
                        VStack(spacing: 8) { cpuTop; ramTop }
                    }
                case .runtime: runtime
                case .battery: if let b = report.battery { battery(b) }
                case .busy: busy
                case .peaks: peakSection
                case .crashes: if let c = report.crashes, !c.isEmpty { crashes(c) }
                case .tips: tipList
                case .log: log
                }
            }
        }
    }

    private func header(_ t: String) -> some View {
        Text(t.uppercased()).font(theme.label(10)).foregroundStyle(theme.secondary).padding(.top, 4)
    }

    private var scoreRow: some View {
        let s = report.score
        let color: Color = theme.rowStyle == .blocks ? .white : s.map { $0 >= 8 ? theme.good : $0 >= 5 ? theme.warn : theme.danger } ?? theme.secondary
        return HStack(alignment: .center, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 2) {
                Text(s.map(String.init) ?? "–").font(theme.number(40)).foregroundStyle(color)
                Text("/10").font(theme.number(14)).foregroundStyle(theme.onRow(true))
            }
            VStack(alignment: .leading, spacing: 3) {
                Text("Health").font(theme.label(14).weight(.semibold)).foregroundStyle(theme.onRow())
                Text(report.reason).font(theme.label(12)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                if let s, let p = report.prevScore {
                    Text(s > p ? "↑ better than \(previousName) (\(p))" : s < p ? "↓ worse than \(previousName) (\(p))" : "= same as \(previousName)")
                        .font(theme.label(11)).foregroundStyle(theme.onRow(true))
                }
            }
            Spacer(minLength: 0)
        }
        .themedRow(theme, index: 0)
    }

    private var cpuTop: some View {
        top(L("Top 5 CPU"), icon: "cpu", report.topCPU, value: { pct($0.cpu) }, frac: { $0.cpu / max(report.topCPU.first?.cpu ?? 1, 0.1) }, index: 1)
    }
    private var ramTop: some View {
        top(L("Top 5 RAM"), icon: "memorychip", report.topRAM, value: { one($0.mem) + " GB" }, frac: { $0.mem / max(report.topRAM.first?.mem ?? 1, 0.01) }, index: 2)
    }

    private func top(_ title: String, icon: String, _ list: [AppStat], value: @escaping (AppStat) -> String, frac: @escaping (AppStat) -> Double, index: Int) -> some View {
        let bar = theme.rowStyle == .blocks ? Color.white : theme.accent
        return VStack(alignment: .leading, spacing: 5) {
            CardTitle(icon: icon, title: title)
            if list.isEmpty { Text("No data yet").font(theme.label(11)).foregroundStyle(theme.onRow(true)) }
            ForEach(list) { a in
                VStack(alignment: .leading, spacing: 2) {
                    HStack(spacing: 4) {
                        Text(a.name).lineLimit(1).truncationMode(.tail).foregroundStyle(theme.onRow())
                        Spacer(minLength: 2)
                        Text(value(a)).font(theme.number(12)).foregroundStyle(theme.onRow())
                    }
                    .font(theme.label(12))
                    HStack(spacing: 6) {
                        ZStack(alignment: .leading) {
                            Capsule().fill(bar.opacity(0.18))
                            Capsule().fill(bar).frame(width: 70 * min(max(frac(a), 0.03), 1))
                        }
                        .frame(width: 70, height: 4)
                        Text(duration(a.minutes)).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                    }
                }
            }
        }
        .frame(minWidth: 190, maxWidth: .infinity, alignment: .topLeading)
        .themedRow(theme, index: index)
    }

    private var runtime: some View {
        // kern.boottime (telt slaap mee); systemUptime alleen als dat niet lukt
        let up = Int(report.boot.map { Date().timeIntervalSince($0) } ?? ProcessInfo.processInfo.systemUptime) / 60
        let f = DateFormatter(); f.locale = uiLocale; f.dateFormat = "EEE d MMM HH:mm"
        return VStack(alignment: .leading, spacing: 4) {
            CardTitle(icon: "clock", title: L("Running time"), tone: report.uptimeDays >= Diag.restartDays ? 1 : 0)
            Headline(big: duration(up), small: L("Mac on since \(f.string(from: Date().addingTimeInterval(-Double(up) * 60)))"),
                     tone: report.uptimeDays >= Diag.restartDays ? 1 : 0)
            line(L("Fusebox measuring since"), report.first.map { f.string(from: $0) } ?? "–")
            line(L("Measured this period"), duration(report.minutes))
            if !report.topSeen.isEmpty {
                Text("Seen active longest").font(theme.label(10)).foregroundStyle(theme.onRow(true)).padding(.top, 3)
                ForEach(report.topSeen) { a in item(a.name, duration(a.minutes)) }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 3)
    }

    private func battery(_ b: Diag.Battery) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            CardTitle(icon: "battery.100percent", title: L("Battery"), tone: b.worn ? 1 : 0)
            Text(b.summary).font(theme.label(11)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 7)
    }

    private func crashes(_ c: Diag.Crashes) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            CardTitle(icon: "exclamationmark.octagon", title: L("Crashes"), tone: c.crashes.isEmpty && c.hangs.isEmpty ? 1 : 2)
            let n = c.crashes.count + c.hangs.count
            if n > 0 { Headline(big: n == 1 ? L("1 app") : L("\(n) apps"), small: L("crashed or stopped responding \(report.when)"), tone: 1) }
            Capped(items: Array(c.rows.enumerated()), id: \.offset, limit: limit) { item($0.element.label, $0.element.value) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 8)
    }

    /// Lijsten inkorten op het scherm; in de PDF (geen logmap) alles.
    private var limit: Int? { logDir == nil ? nil : 5 }

    /// Regel waarin de naam opvalt en de bijzaak (tijd, aantal) klein en grijs staat.
    private func item(_ name: String, _ detail: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(name).font(theme.label(12)).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.tail)
            Spacer(minLength: 4)
            Text(detail).font(theme.number(10)).foregroundStyle(theme.onRow(true)).lineLimit(1).minimumScaleFactor(0.8)
        }
    }

    private func line(_ a: String, _ b: String) -> some View {
        HStack(spacing: 6) {
            Text(a).foregroundStyle(theme.onRow(true)).lineLimit(1)
            Spacer(minLength: 4)
            Text(b).font(theme.number(11)).foregroundStyle(theme.onRow()).lineLimit(1).minimumScaleFactor(0.8)
        }
        .font(theme.label(11))
    }

    private var busy: some View {
        VStack(alignment: .leading, spacing: 4) {
            CardTitle(icon: "flame", title: L("Busy moments"), tone: report.busy.isEmpty ? 0 : 1)
            Headline(big: report.busy.isEmpty ? L("None") : "\(report.busy.count)", small: L("periods of ≥ 5 min above 85% CPU \(report.when)"),
                     tone: report.busy.isEmpty ? 3 : 1)
            ForEach(report.busy) { b in item(b.app.map { L("Mostly \($0)") } ?? L("Busy"), "\(Analysis.span(b.start, b.end)) · \(pct(b.cpu))") }
        }
        .font(theme.label(11)).frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 4)
    }

    /// Pieken van de periode: tijdlijn, wie ze veroorzaakte, en de laatste paar met oorzaak.
    private var peakSection: some View {
        let p = report.peaks
        return VStack(alignment: .leading, spacing: 5) {
            CardTitle(icon: "waveform.path.ecg", title: L("Peaks"))
            if p.isEmpty {
                Headline(big: L("None"), small: L("No peaks recorded \(report.when) (CPU or GPU ≥ 80%, hot, memory pressure or heavy disk use)"), tone: 3)
            } else {
                Headline(big: "\(p.count)", small: p.count == 1 ? L("peak \(report.when)") : L("peaks \(report.when)"))
                if let from = report.peakFrom { PeakTimeline(peaks: p, from: from, days: report.peakDays).padding(.vertical, 2) }
                ForEach(Peaks.summary(p, when: report.when), id: \.self) { Text($0).foregroundStyle(theme.onRow()) }
                Capped(items: logDir == nil ? Array(p.suffix(6).reversed()) : p.reversed(), id: \.id, limit: limit) { x in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(Analysis.span(x.start, x.end)).font(theme.number(10)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                        Text("\(x.kind.title) \(x.kind.format(x.top))" + (x.cause.map { " · \($0)" } ?? ""))
                            .foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                    }
                }
            }
        }
        .font(theme.label(11)).frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 4)
    }

    private var tipList: some View {
        VStack(alignment: .leading, spacing: 6) {
            header(L("Tips"))
            ForEach(Array(tips.enumerated()), id: \.element.id) { i, tip in
                HStack(alignment: .center, spacing: 8) {
                    let (head, rest) = tip.parts
                    Image(systemName: tip.serious ? "exclamationmark.triangle.fill" : "lightbulb")
                        .font(.system(size: 13 * theme.scale, weight: .semibold)).foregroundStyle(theme.tone(tip.serious ? 1 : 0)).frame(width: 18)
                    VStack(alignment: .leading, spacing: 2) {
                        Text(head).font(theme.label(12).weight(.semibold)).foregroundStyle(theme.onRow())
                        if let rest { Text(rest).font(theme.label(11)).foregroundStyle(theme.onRow(true)) }
                    }
                    .fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 4)
                    if let act, let a = tip.action {
                        switch a {
                        case .stop(let name): Button("Stop") { act(a) }.buttonStyle(Pill(color: theme.rowStyle == .blocks ? .white : theme.danger)).help("Stop \(name)")
                        case .open(_, let label): Button(label) { act(a) }.buttonStyle(Pill(color: theme.onRow()))
                        }
                    }
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .themedRow(theme, index: 5 + i)
            }
        }
    }

    private var log: some View {
        VStack(alignment: .leading, spacing: 4) {
            CardTitle(icon: "list.bullet.rectangle", title: L("Log"))

            if report.events.isEmpty { Text("Nothing unusual happened").font(theme.label(11)).foregroundStyle(theme.onRow(true)) }
            Capped(items: report.events, id: \.id, limit: limit) { e in
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Image(systemName: e.icon).frame(width: 14)
                        .foregroundStyle(theme.rowStyle == .blocks ? .white : e.level >= 2 ? theme.danger : e.level == 1 ? theme.warn : theme.secondary)
                    Text(e.text).font(theme.label(12)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
                    Spacer(minLength: 4)
                    Text(Analysis.span(e.start, e.end)).font(theme.number(10)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                }
                .font(theme.label(11))
            }
            if !report.files.isEmpty || logDir != nil {
                Rectangle().fill(theme.rowStyle == .blocks ? Color.white.opacity(0.3) : theme.line).frame(height: 1).padding(.vertical, 3)
                ForEach(report.files) { f in
                    line(f.name, bytes(Double(f.bytes)))
                }
                if let logDir {
                    Button("Open log folder") {
                        try? FileManager.default.createDirectory(at: logDir, withIntermediateDirectories: true)
                        NSWorkspace.shared.open(logDir)
                    }
                    .buttonStyle(Pill(color: theme.onRow())).padding(.top, 2)
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 6 + tips.count)
    }
}

/// Klein knopje in de kleur van het thema (geen standaard-macOS-knop).
struct Pill: ButtonStyle {
    let color: Color
    func makeBody(configuration: Configuration) -> some View {
        configuration.label
            .font(.system(size: 11, weight: .semibold)).lineLimit(1)
            .foregroundStyle(color)
            .padding(.horizontal, 9).padding(.vertical, 3)
            .overlay(Capsule().stroke(color.opacity(0.6), lineWidth: 1))
            .contentShape(Capsule())
            .opacity(configuration.isPressed ? 0.55 : 1)
    }
}

// MARK: - Bouwstenen voor de Checkup-kaarten (ook in Accu/Opstart/Gezondheid)

/// Toon van een kaart: 0 gewoon, 1 let op, 2 ernstig, 3 goed.
extension Theme {
    /// Kleur voor icoon of kop; bij kleurblokken altijd wit (op het blok). Gewone toon: accent voor iconen, tekstkleur voor koppen.
    func tone(_ t: Int, icon: Bool = true) -> Color {
        if rowStyle == .blocks { return .white }
        switch t { case 1: return warn; case 2: return danger; case 3: return good; default: return icon ? accent : text }
    }
}

/// Kaarttitel met een klein SF Symbol ervoor.
struct CardTitle: View {
    @Environment(\.theme) private var theme
    let icon: String, title: String
    var tone = 0
    var body: some View {
        HStack(spacing: 6) {
            Image(systemName: icon).font(.system(size: 11 * theme.scale, weight: .semibold)).foregroundStyle(theme.tone(tone)).frame(width: 16)
            Text(title).font(theme.label(12).weight(.semibold)).foregroundStyle(theme.onRow())
        }
    }
}

/// Eén kernfeit groot, de uitleg klein eronder: zo zie je bij scrollen meteen waar het om gaat.
struct Headline: View {
    @Environment(\.theme) private var theme
    let big: String
    var small: String? = nil
    var tone = 0
    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            Text(big).font(theme.number(22)).foregroundStyle(theme.tone(tone, icon: false)).lineLimit(1).minimumScaleFactor(0.7)
            if let small { Text(small).font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true) }
        }
        .padding(.bottom, 2)
    }
}

/// Lange lijst: eerst `limit` regels, de rest achter "Show all (N)". `limit` nil = alles (PDF).
struct Capped<Item, ID: Hashable, Row: View>: View {
    @Environment(\.theme) private var theme
    let items: [Item], id: KeyPath<Item, ID>
    var limit: Int? = 5
    @ViewBuilder let row: (Item) -> Row
    @State private var all = false

    var body: some View {
        // +1: geen "Show all (6)" voor één verborgen regel
        let cut = limit.map { all || items.count <= $0 + 1 ? items.count : $0 } ?? items.count
        ForEach(items.prefix(cut), id: id) { row($0) }
        if let limit, items.count > limit + 1 {
            Button(all ? "Show less" : "Show all (\(items.count))") { all.toggle() }
                .buttonStyle(.plain).font(theme.label(11).weight(.semibold)).foregroundStyle(theme.tone(0)).padding(.top, 1)
        }
    }
}
