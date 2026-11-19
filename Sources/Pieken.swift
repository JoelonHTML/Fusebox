import SwiftUI
import UserNotifications

// MARK: - Pieken: wanneer schoot iets omhoog, en welke app deed dat?
//
// Elke meting (2 s) gaat langs een `PeakTracker` per soort. Bij de eerste meting boven de grens meteen (hooguit
// eens per 30 s per soort) een momentopname van de apps, zodat ook een piek van een paar seconden een oorzaak heeft.
// Afgelopen pieken: in het geheugen (vandaag + laatste uur) en achteraan peaks-JJJJ-MM-DD.csv (Checkup leest die).
// Geen momentopname? Dan de apps van die minuut (de meting die ook naar de apps-log gaat).

enum PeakKind: String, CaseIterable, Sendable {
    case cpu, gpu, temp, mem, io
    var title: String {
        switch self { case .cpu: "CPU"; case .gpu: "GPU"; case .temp: L("Heat"); case .mem: L("Memory"); case .io: L("Disk") }
    }
    var metric: Metric {
        switch self { case .cpu, .temp: .cpu; case .gpu: .gpu; case .mem: .ram; case .io: .io }
    }
    /// Welke app-waarde de oorzaak bepaalt (warmte komt van de processor).
    var dim: Culprit.Dim {
        switch self { case .cpu, .temp: .cpu; case .gpu: .gpu; case .mem: .mem; case .io: .disk }
    }
    func value(_ s: Sample) -> Double? {
        switch self {
        case .cpu: s.cpu
        case .gpu: s.gpu
        case .temp: s.temp
        case .mem: Double(s.pressure)
        case .io: s.diskRead + s.diskWrite
        }
    }
    func format(_ v: Double) -> String {
        switch self {
        case .cpu, .gpu: pct(v)
        case .temp: deg(v)
        case .mem: v >= 4 ? L("critical") : L("high")
        case .io: rate(v)
        }
    }
}

struct Peak: Identifiable, Equatable, Sendable {
    let kind: PeakKind
    var start: Date, end: Date
    var top: Double, at: Date          // hoogste waarde en wanneer
    var app: String? = nil             // grootste oorzaak
    var appValue = ""                  // "61%", "3.2 GB"
    var id: String { "\(kind.rawValue)\(start.timeIntervalSince1970)" }
    var seconds: Int { Int(end.timeIntervalSince(start).rounded()) }
    var cause: String? { app.map { appValue.isEmpty ? $0 : "\($0) \(appValue)" } }
}

/// Eén app in een momentopname.
struct Culprit: Sendable {
    enum Dim: CaseIterable { case cpu, mem, gpu, disk, energy }
    let name: String
    var cpu = 0.0, mem = 0.0, gpu = 0.0, disk = 0.0, energy = 0.0
    func value(_ d: Dim) -> Double {
        switch d { case .cpu: cpu; case .mem: mem; case .gpu: gpu; case .disk: disk; case .energy: energy }
    }
    static func format(_ v: Double, _ d: Dim) -> String {
        switch d { case .cpu, .gpu: pct(v); case .mem: "\(one(v)) GB"; case .disk: rate(v); case .energy: String(format: "%.1f W", v) }
    }
}

struct Snapshot: Sendable {
    let time: Date
    let minute: Bool                   // true = minuutmeting (gemiddelde van de minuut vóór `time`)
    let apps: [Culprit]
    /// Grootste app voor `d`, als die echt iets deed.
    func top(_ d: Culprit.Dim) -> Culprit? { apps.max { $0.value(d) < $1.value(d) }.flatMap { $0.value(d) > 0 ? $0 : nil } }
    func top(_ d: Culprit.Dim, _ n: Int) -> [Culprit] { apps.filter { $0.value(d) > 0 }.sorted { $0.value(d) > $1.value(d) }.prefix(n).map { $0 } }
}

/// Volgt één soort meting; puur (zelftest).
struct PeakTracker {
    let kind: PeakKind
    var minLength = 2, gap = 1         // minstens 2 metingen (4 s); 1 meting eronder onderbreekt een piek niet
    private(set) var open: Peak?
    private var count = 0, below = 0
    init(_ kind: PeakKind) { self.kind = kind; if kind == .mem { minLength = 1 } }

    /// Lopende piek, als hij al lang genoeg is.
    var current: Peak? { count >= minLength ? open : nil }

    /// `started`: eerste meting boven de grens (moment voor een momentopname); `closed`: zojuist afgelopen piek.
    mutating func feed(_ t: Date, _ v: Double, level: Double, step: TimeInterval = 2) -> (started: Bool, closed: Peak?) {
        if v >= level {
            below = 0
            guard var p = open else {
                open = Peak(kind: kind, start: t, end: t + step, top: v, at: t); count = 1
                return (true, nil)
            }
            p.end = t + step; count += 1
            if v > p.top { p.top = v; p.at = t }
            open = p
            return (false, nil)
        }
        guard let p = open else { return (false, nil) }
        below += 1
        guard below > gap else { return (false, nil) }
        defer { open = nil; count = 0; below = 0 }
        return (false, count >= minLength ? p : nil)
    }
}

enum Peaks {
    static let busy = 80.0                     // CPU en GPU
    static let ioFloor = 50e6                  // schijf: minstens 50 MB/s …
    static let header = "tijd,eind,soort,hoogte,app,app_waarde\n"

    /// Grens per soort. Schijf: boven het 95e percentiel van het afgelopen uur (en minstens 50 MB/s).
    static func level(_ k: PeakKind, warnTemp: Double, io: [Double] = []) -> Double {
        switch k {
        case .cpu, .gpu: busy
        case .temp: warnTemp > 0 ? warnTemp : 95
        case .mem: 2
        case .io: max(ioFloor, percentile(io, 0.95))
        }
    }

    static func percentile(_ v: [Double], _ p: Double) -> Double {
        guard !v.isEmpty else { return 0 }
        let s = v.sorted()
        return s[min(Int(Double(s.count - 1) * p), s.count - 1)]
    }

    /// Top 3 per soort (zonder de restpost "macOS itself", die is altijd groot bij geheugen).
    static func compact(_ apps: [AppUsage]) -> [Culprit] {
        let own = apps.filter { $0.id != "macos" }
        var ids = Set<String>()
        for k: (AppUsage) -> Double in [{ $0.cpu }, { $0.memGB }, { $0.gpu }, { $0.diskRead + $0.diskWrite }, { $0.energyW }] {
            ids.formUnion(own.sorted { k($0) > k($1) }.prefix(3).map(\.id))
        }
        return own.filter { ids.contains($0.id) }.map {
            Culprit(name: $0.name, cpu: $0.cpu, mem: $0.memGB, gpu: $0.gpu, disk: $0.diskRead + $0.diskWrite, energy: $0.energyW)
        }
    }

    /// Momentopname die bij tijdstip `t` hoort: een piek-opname binnen 20 s (dichtstbij), anders de minuutmeting
    /// die `t` dekt (de eerstvolgende binnen 70 s), anders de dichtstbijzijnde minuutmeting binnen 90 s.
    static func snapshot(at t: Date, in snaps: [Snapshot]) -> Snapshot? {
        func d(_ s: Snapshot) -> TimeInterval { abs(s.time.timeIntervalSince(t)) }
        if let s = snaps.filter({ !$0.minute && d($0) <= 20 }).min(by: { d($0) < d($1) }) { return s }
        let minutes = snaps.filter(\.minute)
        if let s = minutes.filter({ $0.time >= t && $0.time.timeIntervalSince(t) <= 70 }).min(by: { $0.time < $1.time }) { return s }
        return minutes.filter { d($0) <= 90 }.min { d($0) < d($1) }
    }

    /// Plek van tijdstip `t` in een oplopende reeks tijden (dichtstbijzijnde index).
    static func index(of t: Date, in times: [Date]) -> Int? {
        guard !times.isEmpty else { return nil }
        var lo = 0, hi = times.count - 1
        while lo < hi { let mid = (lo + hi) / 2; if times[mid] < t { lo = mid + 1 } else { hi = mid } }
        if lo > 0, abs(times[lo - 1].timeIntervalSince(t)) < abs(times[lo].timeIntervalSince(t)) { return lo - 1 }
        return lo
    }

    // MARK: CSV (kolommen op naam, zoals de andere logs)

    static func csv(_ p: Peak) -> String {
        let f = { (v: Double) in String(format: "%.1f", v.isFinite ? v : 0) }
        let app = (p.app ?? "").replacingOccurrences(of: ",", with: " ").replacingOccurrences(of: "\n", with: " ")
        return "\(Sample.stamp.string(from: p.start)),\(Sample.stamp.string(from: p.end)),\(p.kind.rawValue),\(f(p.top)),\(app),\(p.appValue.replacingOccurrences(of: ",", with: " "))\n"
    }

    static func parse(_ text: String) -> [Peak] {
        let lines = text.split(separator: "\n")
        guard let head = lines.first else { return [] }
        var col: [Substring: Int] = [:]
        for (i, c) in head.split(separator: ",").enumerated() where col[c] == nil { col[c] = i }
        guard let iT = col["tijd"], let iK = col["soort"], let iH = col["hoogte"] else { return [] }
        let iE = col["eind"], iA = col["app"], iV = col["app_waarde"]
        return lines.dropFirst().compactMap { line in
            let v = line.split(separator: ",", omittingEmptySubsequences: false)
            func s(_ i: Int?) -> String? { i.flatMap { $0 < v.count && !v[$0].isEmpty ? String(v[$0]) : nil } }
            guard let start = s(iT).flatMap({ Sample.stamp.date(from: $0) }), let kind = s(iK).flatMap({ PeakKind(rawValue: $0) }), let top = s(iH).flatMap(Double.init) else { return nil }
            let end = s(iE).flatMap { Sample.stamp.date(from: $0) } ?? start + 2
            return Peak(kind: kind, start: start, end: max(end, start), top: top, at: start, app: s(iA), appValue: s(iV) ?? "")
        }
    }

    // MARK: Teksten

    static let clock: DateFormatter = { let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "HH:mm"; return f }()

    static func length(_ seconds: Int) -> String { seconds < 120 ? "\(max(seconds, 2)) s" : "\(seconds / 60) min" }

    /// "Peak 14:32 · 94% · Xcode"
    static func line(_ p: Peak) -> String {
        L("Peak \(clock.string(from: p.at)) · \(p.kind.format(p.top))") + (p.app.map { " · \($0)" } ?? "")
    }

    /// Per soort: "Xcode caused 4 of 6 CPU peaks today" (of "6 CPU peaks today" zonder bekende oorzaak).
    static func summary(_ peaks: [Peak], when: String) -> [String] {
        PeakKind.allCases.compactMap { k in
            let list = peaks.filter { $0.kind == k }
            guard !list.isEmpty else { return nil }
            let n = list.count, t = k.title
            let by = Dictionary(grouping: list.compactMap(\.app), by: { $0 }).mapValues(\.count)
            // Alleen een naam noemen als die app er echt uitspringt (minstens 2, of de enige piek)
            guard let top = by.max(by: { $0.value == $1.value ? $0.key > $1.key : $0.value < $1.value }), top.value >= 2 || n == 1 else {
                return n == 1 ? L("1 \(t) peak \(when)") : L("\(n) \(t) peaks \(when)")
            }
            // hele zinnen (één per vorm), zodat ze te vertalen zijn
            if top.value < n { return L("\(top.key) caused \(top.value) of \(n) \(t) peaks \(when)") }
            return n == 1 ? L("\(top.key) caused the \(t) peak \(when)") : L("\(top.key) caused all \(n) \(t) peaks \(when)")
        }
    }

    // MARK: Zelftest (verzonnen data)

    static func selfTest() {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        func run(_ v: [Double], _ k: PeakKind = .cpu, level: Double = busy) -> (starts: Int, peaks: [Peak], open: Peak?) {
            var tr = PeakTracker(k), starts = 0, out: [Peak] = []
            for (i, x) in v.enumerated() {
                let r = tr.feed(t0 + Double(i) * 2, x, level: level)
                if r.started { starts += 1 }
                if let p = r.closed { out.append(p) }
            }
            return (starts, out, tr.current)
        }
        // Eén piek van 3 metingen met een dipje ertussen; een losse uitschieter telt niet; een lopende piek wel.
        let a = run([10, 85, 70, 95, 90, 20, 20, 99, 10, 10, 82, 83, 84])
        precondition(a.starts == 3 && a.peaks.count == 1, "pieken \(a.peaks)")
        let p = a.peaks[0]
        precondition(p.start == t0 + 2 && p.end == t0 + 10 && p.top == 95 && p.at == t0 + 6 && p.seconds == 8, "piek \(p)")
        precondition(a.open?.start == t0 + 20 && a.open?.top == 84, "lopende piek")
        precondition(run([10, 79.9, 79, 10, 10]).starts == 0, "onder de grens")
        precondition(run([1, 2, 1, 1], .mem, level: 2).peaks.count == 1, "geheugendruk: 1 meting is genoeg")
        // Schijf: 95e percentiel, maar nooit onder 50 MB/s
        precondition(level(.io, warnTemp: 95, io: Array(repeating: 1e6, count: 100)) == ioFloor, "io-vloer")
        let busyDisk = (0..<100).map { Double($0) * 1e6 + 60e6 }
        precondition(level(.io, warnTemp: 95, io: busyDisk) == 154e6 && level(.temp, warnTemp: 90) == 90, "io-percentiel")
        // Oorzaak: piek-opname gaat voor, anders de minuut die het tijdstip dekt
        let demo = Culprit(name: "DemoApp", cpu: 61, mem: 2), other = Culprit(name: "Sketchpad", cpu: 5, mem: 9, disk: 80e6)
        let snaps = [Snapshot(time: t0 + 60, minute: true, apps: [other]), Snapshot(time: t0 + 5, minute: false, apps: [demo, other]),
                     Snapshot(time: t0 + 120, minute: true, apps: [demo])]
        precondition(snapshot(at: t0 + 4, in: snaps)?.minute == false, "piek-opname")
        precondition(snapshot(at: t0 + 90, in: snaps)?.time == t0 + 120, "minuut die het dekt")
        precondition(snapshot(at: t0 + 500, in: snaps) == nil, "te ver weg")
        precondition(snaps[1].top(.cpu)?.name == "DemoApp" && snaps[1].top(.mem)?.name == "Sketchpad" && snaps[1].top(.gpu) == nil, "top")
        let apps = [AppUsage(id: "macos", name: "macOS itself", explanation: "", kind: .macos, pids: [], cpu: 90, memGB: 12, energyW: 0, icon: nil),
                    AppUsage(id: "/a.app", name: "DemoApp", explanation: "", kind: .app, pids: [1], cpu: 40, memGB: 1, energyW: 2, icon: nil)]
        precondition(compact(apps).map(\.name) == ["DemoApp"], "zonder restpost")
        precondition(index(of: t0 + 5, in: [t0, t0 + 4, t0 + 8]) == 1 && index(of: t0 + 99, in: [t0, t0 + 4]) == 1 && index(of: t0, in: []) == nil, "index")
        // CSV heen en terug, ook met een komma in de naam en kolommen in een andere volgorde
        var q = p; q.app = "Demo, App"; q.appValue = "61%"
        let back = parse(header + csv(q))
        precondition(back.count == 1 && back[0].kind == .cpu && back[0].start == q.start && back[0].end == q.end && back[0].top == 95
                     && back[0].app == "Demo  App" && back[0].appValue == "61%", "csv \(back)")
        let odd = parse("soort,hoogte,tijd\nio,1000.0,2026-11-16 14:20:05\nrommel\n,,\n")
        precondition(odd.count == 1 && odd[0].kind == .io && odd[0].app == nil, "csv andere kolommen")
        // Samenvatting
        func pk(_ app: String?, _ k: PeakKind = .cpu) -> Peak { Peak(kind: k, start: t0, end: t0 + 4, top: 90, at: t0, app: app) }
        let s = summary([pk("DemoApp"), pk("DemoApp"), pk("Sketchpad"), pk(nil), pk("DemoApp", .gpu)], when: "today")
        precondition(s == ["DemoApp caused 2 of 4 CPU peaks today", "DemoApp caused the GPU peak today"], "samenvatting \(s)")
        precondition(summary([pk(nil, .temp)], when: "yesterday") == ["1 Heat peak yesterday"]
                     && summary([pk("DemoApp"), pk("Sketchpad")], when: "today") == ["2 CPU peaks today"], "zonder (duidelijke) oorzaak")
        precondition(line(q).hasSuffix(" · 95% · Demo, App") && length(8) == "8 s" && length(300) == "5 min", "regel")
        print("pieken ok")
    }
}

// MARK: - Live bijhouden (hoofdthread; alleen de momentopname meet op Meter.work)

final class PeakWatch {
    private(set) var log: [Peak] = []          // afgelopen pieken: vandaag + het laatste uur
    private(set) var snaps: [Snapshot] = []    // laatste uur
    private var trackers = Dictionary(uniqueKeysWithValues: PeakKind.allCases.map { ($0, PeakTracker($0)) })
    private var lastSnap: [PeakKind: Date] = [:]
    private var pending = false
    private var ioLevel = Peaks.ioFloor, fed = 0
    private let monitor = AppMonitor()         // eigen teller: de minuutmeting blijft een echt minuutgemiddelde
    private let file: (Date) -> URL

    init(file: @escaping (Date) -> URL) {
        self.file = file
        let today = file(Date())
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let loaded = Peaks.parse((try? String(contentsOf: today, encoding: .utf8)) ?? "")
            DispatchQueue.main.async { self?.log = loaded + (self?.log ?? []) }
        }
    }

    /// Elke meting (Meter.publish). Kost niets zolang er niets piekt.
    func feed(_ s: Sample, recent: [Sample]) {
        fed += 1
        if fed % 30 == 1 { ioLevel = Peaks.level(.io, warnTemp: 0, io: recent.map { $0.diskRead + $0.diskWrite }) }
        let warnTemp = UserDefaults.standard.double(forKey: "warnTemp")
        for k in PeakKind.allCases {
            guard let v = k.value(s) else { continue }
            let r = trackers[k]!.feed(s.time, v, level: k == .io ? ioLevel : Peaks.level(k, warnTemp: warnTemp), step: LowImpact.interval)   // 2 s, of 5 s in low impact
            if r.started, s.time.timeIntervalSince(lastSnap[k] ?? .distantPast) >= 30 {
                lastSnap[k] = s.time
                snap(s)
            }
            if var p = r.closed {
                if let c = cause(p) { (p.app, p.appValue) = c }
                log.append(p)
                write(p)
            }
        }
        let cal = Calendar.current, hour = s.time - 3600
        log.removeAll { !cal.isDate($0.start, inSameDayAs: s.time) && $0.start < hour }
        if snaps.first.map({ $0.time < hour }) == true { snaps.removeAll { $0.time < hour } }
    }

    /// Elke minuut: de apps die net gemeten zijn (terugval als er geen momentopname is).
    func minute(_ apps: [AppUsage], at t: Date) {
        snaps.append(Snapshot(time: t, minute: true, apps: Peaks.compact(apps)))
    }

    /// Twee metingen 1 s na elkaar op `work`: de apps tijdens de piek zelf.
    private func snap(_ s: Sample) {
        guard !pending, !snaps.contains(where: { !$0.minute && s.time.timeIntervalSince($0.time) < 5 }) else { return }
        pending = true
        Meter.work.async { [self] in
            _ = monitor.measure(totalCPU: 0, totalMemGB: 0)   // nulpunt
            Meter.work.asyncAfter(deadline: .now() + 1) { [self] in
                let apps = Peaks.compact(monitor.measure(totalCPU: 0, totalMemGB: 0))
                DispatchQueue.main.async { self.snaps.append(Snapshot(time: s.time, minute: false, apps: apps)); self.pending = false }
            }
        }
    }

    private func write(_ p: Peak) {
        let url = file(p.start), fm = FileManager.default
        try? fm.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !fm.fileExists(atPath: url.path) { try? Peaks.header.write(to: url, atomically: true, encoding: .utf8) }
        _ = Meter.append(Peaks.csv(p), to: url)
    }

    // MARK: Opvragen (views)

    private func cause(_ p: Peak) -> (String, String)? {
        guard let c = Peaks.snapshot(at: p.at, in: snaps)?.top(p.kind.dim) else { return nil }
        return (c.name, Culprit.format(c.value(p.kind.dim), p.kind.dim))
    }

    /// Pieken van deze meting sinds `since`, oudste eerst, inclusief een lopende piek.
    func peaks(_ m: Metric, since: Date?) -> [Peak] {
        let from = since ?? .distantPast
        let open = trackers.values.compactMap(\.current)
        return (log + open).filter { $0.kind.metric == m && $0.end >= from }
            .map { p in
                guard p.app == nil, let c = cause(p) else { return p }
                var q = p; (q.app, q.appValue) = c; return q
            }
            .sorted { $0.start < $1.start }
    }

    /// Lopende pieken (alle soorten, vaste volgorde) met hun oorzaak: voor de melding "Sustained peak".
    func running() -> [Peak] {
        PeakKind.allCases.compactMap { trackers[$0]?.current }.map { p in
            var q = p; if let c = cause(p) { (q.app, q.appValue) = c }; return q
        }
    }

    func snapshot(for p: Peak) -> Snapshot? { Peaks.snapshot(at: p.at, in: snaps) }

    /// "Xcode 61%" op tijdstip `t` (alleen het laatste uur).
    func cause(at t: Date, _ m: Metric) -> String? {
        guard let d = m.dim, let c = Peaks.snapshot(at: t, in: snaps)?.top(d) else { return nil }
        return "\(c.name) \(Culprit.format(c.value(d), d))"
    }
}

extension Metric {
    var peakKinds: [PeakKind] { PeakKind.allCases.filter { $0.metric == self } }
    /// Welke app-waarde bij deze meting hoort (internet per app wordt niet per minuut gemeten).
    var dim: Culprit.Dim? {
        switch self {
        case .cpu, .fan: .cpu
        case .gpu: .gpu
        case .ram: .mem
        case .io, .ssd: .disk
        case .battery: .energy
        case .net: nil
        }
    }
    /// Een waarde uit de grafiek als tekst.
    func format(_ v: Double, temp: Bool = false) -> String {
        if temp { return deg(v) }
        return switch self {
        case .cpu, .gpu, .battery: pct(v)
        case .ram, .ssd: "\(one(v)) GB"
        case .net, .io: rate(v)
        case .fan: "\(Int(v.rounded())) rpm"
        }
    }
}

// MARK: - Views

/// Wat een kaart nodig heeft voor pieken: welke meting, de bron, de tijden onder de grafiek en wat een klik doet.
struct PeakContext {
    let metric: Metric
    let watch: PeakWatch
    var times: [Date] = []
    var open: (Peak) -> Void = { _ in }
}

/// Klein (XS/S): stipje naast het label bij een piek in de laatste 5 min. Hover = één regel, klik = detail.
struct PeakDot: View {
    @Environment(\.theme) private var theme
    let ctx: PeakContext
    var body: some View {
        if let p = ctx.watch.peaks(ctx.metric, since: Date() - 300).last {
            Circle().fill(theme.rowStyle == .blocks ? .white : theme.warn).frame(width: 6, height: 6)
                .padding(3).contentShape(.rect)
                .alignmentGuide(.firstTextBaseline) { $0[VerticalAlignment.center] + 4 }
                .help(Peaks.line(p))
                .onTapGesture { ctx.open(p) }
                .accessibilityLabel(Peaks.line(p))
        }
    }
}

/// Muis boven een grafiek: tijd en waarde (M), met lijn en grootste oorzaak (L/XL, detail). Een klik op een piek
/// (alleen `full`) toont de top 3 apps van dat moment. AppKit-laag: bewegen kost geen SwiftUI-verversing.
struct PeakHover: NSViewRepresentable {
    /// Hoogte van de vaste strook boven de grafiek waarin het label staat (de grafiek begint eronder).
    static let band: CGFloat = 20
    let ctx: PeakContext
    let values: [Double], second: [Double]
    var full = false
    var temp = false
    let color: Color
    func makeNSView(context: Context) -> PeakHoverView { PeakHoverView() }
    func updateNSView(_ v: PeakHoverView, context: Context) {
        v.ctx = ctx; v.values = values; v.second = second; v.full = full; v.temp = temp
        v.color = color.resolve(in: context.environment).cgColor
        v.theme = context.environment.theme
        let env = context.environment
        v.style = .init(font: { size, bold in v.theme.font.nsFont(size, bold: bold, design: v.theme.design) },
                        text: v.theme.text.resolve(in: env).cgColor, sub: v.theme.secondary.resolve(in: env).cgColor,
                        card: v.theme.card.resolve(in: env).cgColor, accent: v.theme.accent.resolve(in: env).cgColor)
        v.show()
    }
}

final class PeakHoverView: NSView {
    var ctx: PeakContext?
    var values: [Double] = [], second: [Double] = []
    var full = false, temp = false
    var color = CGColor(gray: 0.5, alpha: 1)
    var theme = Theme.instrument
    /// Label in de stijl van het thema: lettertype, tekst-, kaart- en accentkleur
    struct Style {
        var font: (CGFloat, Bool) -> NSFont = { s, b in .systemFont(ofSize: s, weight: b ? .semibold : .medium) }
        var text = CGColor(gray: 1, alpha: 1), sub = CGColor(gray: 0.7, alpha: 1), card = CGColor(gray: 0.15, alpha: 1), accent = CGColor(gray: 0.8, alpha: 1)
    }
    var style = Style()
    private let line = CAShapeLayer(), label = CATextLayer()
    private var mouse: CGFloat?
    private var area: NSTrackingArea?
    private var hand = false

    override var isFlipped: Bool { true }

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        line.lineWidth = 1; line.isHidden = true
        label.alignmentMode = .left; label.isHidden = true   // los tekst linksboven, geen kaartje (rustig, zoals Aandelen)
        layer?.addSublayer(line); layer?.addSublayer(label)
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        label.contentsScale = window?.backingScaleFactor ?? 2
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let area { removeTrackingArea(area) }
        let a = NSTrackingArea(rect: .zero, options: [.mouseEnteredAndExited, .mouseMoved, .activeInActiveApp, .inVisibleRect], owner: self)
        addTrackingArea(a); area = a
    }

    override func mouseEntered(with e: NSEvent) { mouseMoved(with: e) }
    override func mouseMoved(with e: NSEvent) { mouse = convert(e.locationInWindow, from: nil).x; show() }
    override func mouseExited(with e: NSEvent) {
        mouse = nil; show()
        if hand { hand = false; NSCursor.arrow.set() }
    }

    /// Klikken gaan naar de kaart eronder (detail), behalve op een piek in een grote kaart.
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard full, let sv = superview else { return nil }
        let p = convert(point, from: sv)
        return bounds.contains(p) && peak(at: p.x) != nil ? self : nil
    }

    /// SwiftUI ziet de klik ook (tik op de kaart = detail openen); die tik slaat de kaart dan over.
    private static var claimedAt: TimeInterval = 0
    static func claimed() -> Bool { defer { claimedAt = 0 }; return ProcessInfo.processInfo.systemUptime - claimedAt < 1 }

    override func mouseDown(with e: NSEvent) {
        let x = convert(e.locationInWindow, from: nil).x
        guard let ctx, let p = peak(at: x) else { return }
        Self.claimedAt = e.timestamp
        let pop = NSPopover()
        pop.behavior = .transient
        pop.contentViewController = NSHostingController(rootView: PeakPopover(peak: p, snap: ctx.watch.snapshot(for: p)).environment(\.theme, theme))
        pop.show(relativeTo: NSRect(x: x - 1, y: 0, width: 2, height: bounds.height), of: self, preferredEdge: .minY)
    }

    private func index(_ x: CGFloat) -> Int? {
        let n = values.count
        guard n > 1, ctx?.times.count == n, bounds.width > 0 else { return nil }
        return min(max(Int((x / bounds.width * CGFloat(n - 1)).rounded()), 0), n - 1)
    }

    private func peak(at x: CGFloat) -> Peak? {
        guard let ctx, let i = index(x), let first = ctx.times.first, let last = ctx.times.last else { return nil }
        let t = ctx.times[i], tol = last.timeIntervalSince(first) / Double(bounds.width) * 5   // ±5 pt
        return ctx.watch.peaks(ctx.metric, since: first).filter { $0.start - tol <= t && t <= $0.end + tol }.max { $0.top < $1.top }
    }

    /// Lijn en label op de muisplek zetten (of verbergen); ook na een nieuwe meting.
    func show() {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        defer { CATransaction.commit() }
        guard let x = mouse, let ctx, let i = index(x) else { line.isHidden = true; label.isHidden = true; return }
        let px = (CGFloat(i) / CGFloat(values.count - 1) * bounds.width).rounded() + 0.5
        let time = Peaks.clock.string(from: ctx.times[i])
        let value: String
        if i < second.count, ctx.metric == .net || ctx.metric == .io {
            value = ctx.metric == .net ? "↓\(rate(values[i])) ↑\(rate(second[i]))" : "R \(rate(values[i])) W \(rate(second[i]))"
        } else {
            value = ctx.metric.format(values[i], temp: temp)
        }
        let cause = full ? ctx.watch.cause(at: ctx.times[i], ctx.metric) : nil
        let over = full && peak(at: x) != nil
        if over != hand { hand = over; (over ? NSCursor.pointingHand : NSCursor.arrow).set() }

        // Tijd rustig, waarde vet, boosdoener in de accentkleur; lettertype van het thema, groot genoeg om te lezen
        let size: CGFloat = full ? 13 : 12
        let s = NSMutableAttributedString()
        func add(_ t: String, _ c: CGColor, bold: Bool) {
            s.append(NSAttributedString(string: t, attributes: [.font: style.font(size, bold), .foregroundColor: NSColor(cgColor: c) ?? .labelColor]))
        }
        add(time + "  ", style.sub, bold: false)
        add(value, style.text, bold: true)
        if let cause { add("  " + cause, style.accent, bold: true) }
        let ts = s.size(), w = min(ceil(ts.width) + 2, bounds.width), h = ceil(ts.height)
        label.string = s
        // Tekst altijd op dezelfde plek: links in de strook boven de grafiek; het streepje alleen door de grafiek
        let band = PeakHover.band
        label.frame = CGRect(x: 0, y: max((band - h) / 2, 0), width: w, height: h)
        label.isHidden = false
        line.isHidden = false
        line.frame = bounds
        line.strokeColor = color.copy(alpha: 0.55)
        line.path = CGPath(rect: CGRect(x: px, y: band, width: 0, height: max(bounds.height - band, 0)), transform: nil)
    }
}

/// Top 3 apps per soort op het moment van een piek.
struct PeakPopover: View {
    let peak: Peak
    let snap: Snapshot?
    @Environment(\.theme) private var theme
    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("\(peak.kind.title) peak \(Peaks.clock.string(from: peak.at)) · \(peak.kind.format(peak.top)) · \(Peaks.length(peak.seconds))")
                .font(theme.label(14).weight(.semibold))
            if let snap {
                Text(snap.minute ? "Apps that minute (no snapshot at the moment itself)" : "Apps at that moment")
                    .font(theme.label(12)).foregroundStyle(.secondary)
                Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 10) {
                    GridRow { column("CPU", .cpu, snap); column("Memory", .mem, snap) }
                    GridRow { column("Disk", .disk, snap); column("GPU", .gpu, snap) }
                }
            } else {
                Text("No app measurement near this moment").font(theme.label(12)).foregroundStyle(.secondary)
            }
        }
        .padding(14).frame(width: 380, alignment: .leading)
    }

    private func column(_ title: String, _ d: Culprit.Dim, _ s: Snapshot) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title.uppercased()).font(theme.label(11).weight(.semibold)).foregroundStyle(.secondary)
            let top = s.top(d, 3)
            if top.isEmpty { Text("–").font(theme.label(12)).foregroundStyle(.secondary) }
            ForEach(top.indices, id: \.self) { i in
                HStack(spacing: 6) {
                    Text(top[i].name).lineLimit(1).truncationMode(.middle)
                    Spacer(minLength: 4)
                    Text(Culprit.format(top[i].value(d), d)).font(theme.number(12)).foregroundStyle(theme.accent)
                }
                .font(theme.label(12))
            }
        }
        .frame(width: 165, alignment: .leading)
    }
}

/// Checkup: pieken van de periode op een tijdlijn, met per soort een kleur.
struct PeakTimeline: View {
    @Environment(\.theme) private var theme
    let peaks: [Peak]
    let from: Date, days: Int

    var body: some View {
        let span = Double(max(days, 1)) * 86_400
        VStack(alignment: .leading, spacing: 3) {
            Canvas { g, size in
                let mid = size.height / 2
                g.fill(Path(CGRect(x: 0, y: mid - 0.5, width: size.width, height: 1)), with: .color(theme.onRow(true).opacity(0.4)))
                let ticks = days == 1 ? 4 : days
                for i in 0...ticks {   // 0, 6, 12, 18, 24 uur (of elke dag)
                    let x = size.width * CGFloat(i) / CGFloat(ticks)
                    g.fill(Path(CGRect(x: min(x, size.width - 1), y: mid - 4, width: 1, height: 8)), with: .color(theme.onRow(true).opacity(0.4)))
                }
                for p in peaks {
                    let x = size.width * CGFloat(p.start.timeIntervalSince(from) / span)
                    let w = max(size.width * CGFloat(p.end.timeIntervalSince(p.start) / span), 5)
                    g.fill(Path(roundedRect: CGRect(x: x, y: mid - 5, width: w, height: 10), cornerRadius: 3), with: .color(color(p.kind).opacity(0.85)))
                }
            }
            .frame(height: 16)
            HStack {
                Text(days == 1 ? "0:00" : Peaks.day.string(from: from))
                Spacer()
                Text(days == 1 ? "12:00" : "")
                Spacer()
                Text(days == 1 ? "24:00" : "today")
            }
            .font(theme.number(9)).foregroundStyle(theme.onRow(true))
            HStack(spacing: 10) {
                ForEach(PeakKind.allCases.filter { k in peaks.contains { $0.kind == k } }, id: \.self) { k in
                    HStack(spacing: 4) { Circle().fill(color(k)).frame(width: 6, height: 6); Text(k.title) }
                }
            }
            .font(theme.label(10)).foregroundStyle(theme.onRow(true))
        }
    }

    private func color(_ k: PeakKind) -> Color {
        if theme.rowStyle == .blocks { return .white }
        return switch k { case .cpu: theme.accent; case .gpu: theme.good; case .temp: theme.warn; case .mem: theme.danger; case .io: theme.secondary }
    }
}

extension Peaks {
    static let day: DateFormatter = { let f = DateFormatter(); f.locale = uiLocale; f.dateFormat = "EEE"; return f }()
}

extension AppFont {
    /// Zelfde lettertype als in SwiftUI, maar als NSFont (voor het piek-label dat in een laag getekend wordt).
    func nsFont(_ size: CGFloat, bold: Bool, design: Font.Design) -> NSFont {
        let w: NSFont.Weight = bold ? .semibold : .medium
        func sys(_ d: NSFontDescriptor.SystemDesign) -> NSFont {
            let base = NSFont.systemFont(ofSize: size, weight: w)
            return base.fontDescriptor.withDesign(d).flatMap { NSFont(descriptor: $0, size: size) } ?? base
        }
        switch self {
        case .montserrat: return NSFont(name: bold ? "Montserrat-SemiBold" : "Montserrat-Medium", size: size) ?? NSFont(name: "Montserrat", size: size) ?? sys(.default)
        case .ubuntu: return NSFont(name: bold ? "Ubuntu-Bold" : "Ubuntu-Medium", size: size) ?? sys(.default)
        case .mono: return .monospacedSystemFont(ofSize: size, weight: w)
        case .serif: return sys(.serif)
        case .rounded: return sys(.rounded)
        case .sfPro: return sys(.default)
        case .style:
            switch design {
            case .rounded: return sys(.rounded)
            case .monospaced: return .monospacedSystemFont(ofSize: size, weight: w)
            case .serif: return sys(.serif)
            default: return sys(.default)
            }
        }
    }
}

// MARK: - Melding bij een aanhoudende piek: "Chrome is keeping your CPU busy", met knoppen Stop app / Show
//
// Geen eigen timer: Meter kijkt elke minuut (minuteTasks) naar de lopende pieken. Sleutels alert.peak.on/.min;
// hoe vaak per app volgt "alertCooldown" (zelfde instelling als de andere meldingen, standaard 30 min).

enum PeakAlert {
    static let category = "peak", categoryShow = "peakshow"   // met en zonder "Stop app" (systeemprocessen nooit)
    static let stopAction = "stop", showAction = "show"
    static let openPage = Notification.Name("fusebox.openAppPage")
    static func key(_ field: String) -> String { "alert.peak.\(field)" }
    /// De minuutmeting die de melding gaf: daar zoeken de knoppen de app terug (verse pids, canStop opnieuw bekeken).
    @MainActor static weak var monitor: AppMonitor?
    /// App-pagina die Processes moet openen (gezet door "Show", opgehaald door ProgramsList).
    @MainActor static var pendingPage: AppRef?

    /// Melden? Piek lang genoeg en deze app niet al binnen `cooldown` gemeld. Puur (zelftest).
    static func due(_ p: Peak, app: String, now: Date, minutes: Int, cooldown: TimeInterval, last: [String: Date]) -> Bool {
        p.seconds >= max(1, minutes) * 60 && now.timeIntervalSince(last[app] ?? .distantPast) >= cooldown
    }

    static func text(_ p: Peak, app: String) -> (title: String, body: String) {
        let title = switch p.kind {
        case .cpu: L("\(app) is keeping your CPU busy")
        case .gpu: L("\(app) is keeping your GPU busy")
        case .temp: L("\(app) is heating up your Mac")
        case .mem: L("\(app) is filling your memory")
        case .io: L("\(app) is keeping your disk busy")
        }
        let m = p.seconds / 60, t = p.kind.title, v = p.kind.format(p.top)
        return (title, m == 1 ? L("\(t) \(v) for 1 minute") : L("\(t) \(v) for \(m) minutes"))
    }

    static func registerCategories() -> [UNNotificationCategory] {
        let stop = UNNotificationAction(identifier: stopAction, title: L("Stop app"), options: [.destructive])
        let show = UNNotificationAction(identifier: showAction, title: L("Show"), options: [.foreground])

        return [UNNotificationCategory(identifier: category, actions: [stop, show], intentIdentifiers: []),
                UNNotificationCategory(identifier: categoryShow, actions: [show], intentIdentifiers: [])]
    }

    /// Knop of klik op de melding. "Stop app" = netjes stoppen (zoals ⌘Q, AppMonitor.stop); de knop zelf is de bevestiging.
    @MainActor static func handle(_ action: String, id: String, name: String) {
        if action == stopAction {
            if let app = monitor?.apps.first(where: { $0.id == id && $0.canStop }) { monitor?.stop(app) }
            return
        }
        UserDefaults.standard.set(Page.programs.rawValue, forKey: "page")
        pendingPage = AppRef(id: id, name: name)
        NSApp.unhide(nil)
        Hotkey.openMain?()
        NSApp.activate()
        NotificationCenter.default.post(name: openPage, object: nil)
    }

    static func selfTest() {
        let t0 = Date(timeIntervalSince1970: 1_800_000_000)
        let p = Peak(kind: .cpu, start: t0, end: t0 + 185, top: 92, at: t0 + 10)
        precondition(due(p, app: "DemoApp", now: t0 + 185, minutes: 2, cooldown: 1800, last: [:]), "lang genoeg")
        precondition(!due(p, app: "DemoApp", now: t0 + 185, minutes: 4, cooldown: 1800, last: [:]), "te kort")
        precondition(!due(p, app: "DemoApp", now: t0 + 185, minutes: 2, cooldown: 1800, last: ["DemoApp": t0 - 600]), "binnen 30 min al gemeld")
        precondition(due(p, app: "DemoApp", now: t0 + 185, minutes: 2, cooldown: 1800, last: ["DemoApp": t0 - 1700, "Other": t0]), "na 30 min weer, per app")
        let t = text(p, app: "DemoApp")
        precondition(t.title == "DemoApp is keeping your CPU busy" && t.body == "CPU 92% for 3 minutes", "tekst \(t)")
        print("piekmelding ok")
    }
}
