import SwiftUI
import IOKit.ps

// MARK: - Accu en slaap (Checkup-pagina)
//
// Vier onderdelen: wat de accu leegtrok (apps-CSV), verbruik tijdens slaap (`pmset -g log`), wat de Mac wakker houdt
// (`pmset -g assertions`) en accugezondheid over weken (battery-health.csv, één regel per dag).
// Alles wordt pas gelezen als Checkup open gaat, buiten de hoofdthread; alleen de dagelijkse gezondheidsregel loopt mee
// met de minuutmeting (Meter.minuteTasks) en schrijft alleen als de datum veranderde.

enum Power {
    /// Eén regel uit `pmset -g log` die ertoe doet.
    struct Event: Sendable, Equatable {
        enum Kind: Sendable { case sleep, darkWake, wake, summary }
        let time: Date, kind: Kind
        let reason: String          // tekst na "due to" (of de slaapreden tussen quotes)
        let ac: Bool?, charge: Int? // "Using AC (Charge:100%)" / "Using Batt (Charge:48%)"
    }

    private static let stamp: DateFormatter = {
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "yyyy-MM-dd HH:mm:ss Z"; return f
    }()
    private static let day: DateFormatter = {
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.calendar = Calendar(identifier: .gregorian); f.dateFormat = "yyyy-MM-dd"; return f
    }()
    static func dayName(_ d: Date) -> String { day.string(from: d) }

    /// "2026-11-18 12:40:31 +0100 DarkWake            \tDarkWake from Deep Idle [CDNP] : due to … Using BATT (Charge:48%) 6 secs"
    static func parse(_ line: Substring) -> Event? {
        guard line.count > 26, let tab = line.firstIndex(of: "\t"), let time = stamp.date(from: String(line.prefix(25))) else { return nil }
        let type = line[line.index(line.startIndex, offsetBy: 25)..<tab].trimmingCharacters(in: .whitespaces)
        let msg = line[line.index(after: tab)...]
        let kind: Event.Kind
        switch type {
        case "Sleep": kind = .sleep
        case "Wake": kind = .wake
        case "DarkWake": kind = .darkWake
        case "Assertions" where msg.hasPrefix("Summary"): kind = .summary
        default: return nil
        }
        var ac: Bool?, charge: Int?
        if let u = msg.range(of: "Using ") {
            let rest = msg[u.upperBound...]
            ac = rest.hasPrefix("AC")
            if let c = rest.range(of: "Charge:") { charge = Int(rest[c.upperBound...].drop { $0 == " " }.prefix { $0.isNumber }) }
        }
        var reason = ""
        if let d = msg.range(of: "due to ") {
            reason = String(msg[d.upperBound...]).components(separatedBy: " Using ")[0].trimmingCharacters(in: .whitespaces)
            if reason.hasPrefix("'"), let q = reason.dropFirst().firstIndex(of: "'") { reason = String(reason[reason.index(after: reason.startIndex)..<q]) }
        }
        return Event(time: time, kind: kind, reason: reason, ac: ac, charge: charge)
    }

    /// Het hele log is groot (tientallen MB, ~5 s): grep houdt alleen de soorten regels over, en alleen de laatste 2 dagen tellen.
    static func readLog(now: Date = Date()) -> [Event] {
        let out = Diag.run("/bin/sh", ["-c", "/usr/bin/pmset -g log | /usr/bin/grep -E '[+-][0-9]{4} (Sleep|Wake|DarkWake) |Summary- '"], timeout: 30)
        let cutoff = now.addingTimeInterval(-2 * 86400)
        return out.split(separator: "\n").compactMap(parse).filter { $0.time >= cutoff }
    }

    /// Waarom de Mac wakker werd, in gewone taal. Volgorde telt: "lid … multi-touch" is het deksel.
    static func reason(_ raw: String) -> String {
        let s = raw.lowercased()
        let rules: [(String, String)] = [("usb-c_plug", "charger connected"), ("acattach", "charger connected"), ("lid", "lid opened"),
                                         ("pwrbtn", "power button"), ("keyboard", "keyboard"), ("multi-touch", "trackpad"),
                                         ("hid activity", "keyboard or trackpad"), ("useractivity", "you"), ("notification", "a notification"),
                                         ("maintenance", "Power Nap / maintenance"), ("sleepservice", "scheduled background task"),
                                         ("wifibt", "network access (Wi-Fi/Bluetooth)"), ("bluetooth", "Bluetooth"), ("wlan", "network access (Wi-Fi)"),
                                         ("usb", "USB device"), ("rtc", "timer")]
        return rules.first { s.contains($0.0) }?.1 ?? (s.isEmpty ? "unknown" : "other")
    }

    // MARK: Slaap

    struct SleepSpan: Sendable, Identifiable {
        let start: Date, end: Date
        let from: Int?, to: Int?
        let charger: Bool               // ergens tijdens de slaap aan de lader: verlies zegt dan niets
        let darkWakes: [String: Int]    // korte wekmomenten per reden
        let wokeBy: String
        var id: Date { start }
        var lost: Int? { charger ? nil : from.flatMap { f in to.map { f - $0 } } }
        var minutes: Int { Int(end.timeIntervalSince(start) / 60) }
    }

    /// Een slaapperiode loopt van de eerste Sleep na een volledige Wake tot de volgende volledige Wake;
    /// DarkWakes (even wakker voor onderhoud/netwerk) horen erbij. Korter dan `minMinutes` telt niet.
    static func sleeps(_ events: [Event], minMinutes: Double = 30) -> [SleepSpan] {
        var spans: [SleepSpan] = []
        var open: (start: Date, from: Int?, charger: Bool, dark: [String: Int])?
        for e in events {
            switch e.kind {
            case .sleep:
                if open == nil { open = (e.time, e.charge, e.ac == true, [:]) } else if e.ac == true { open!.charger = true }
            case .darkWake:
                guard open != nil else { continue }
                open!.dark[reason(e.reason), default: 0] += 1
                if e.ac == true { open!.charger = true }
            case .wake:
                guard let o = open else { continue }
                open = nil
                guard e.time.timeIntervalSince(o.start) >= minMinutes * 60 else { continue }
                spans.append(SleepSpan(start: o.start, end: e.time, from: o.from, to: e.charge, charger: o.charger || e.ac == true,
                                       darkWakes: o.dark, wokeBy: reason(e.reason)))
            case .summary: break
            }
        }
        return spans
    }

    // MARK: Wat de accu leegtrok

    /// Laatste stuk op de accu: vanaf de overgang lader → accu tot (eventueel) weer aan de lader.
    struct Stretch: Sendable, Equatable { var start: Date, from: Int?, end: Date? = nil, to: Int? = nil }
    static func lastStretch(_ events: [Event]) -> Stretch? {
        var cur: Stretch?, last: Stretch?, prev: Bool?
        for e in events {
            guard let ac = e.ac else { continue }
            if !ac && prev != false { cur = Stretch(start: e.time, from: e.charge) }
            if ac && prev == false, var c = cur { c.end = e.time; c.to = e.charge; last = c; cur = nil }
            prev = ac
        }
        return cur ?? last
    }

    /// Wh per app tussen `from` en `to` (tijd als tekst "yyyy-MM-dd HH:mm:ss": vergelijken zonder datums te lezen).
    /// Elke regel is één minuut. Totaal: mac_energie_w (alle processen) per minuut, anders de som van de regels.
    static func rank(_ texts: [String], from: String, to: String) -> (apps: [(name: String, wh: Double)], total: Double) {
        var perApp: [String: Double] = [:], mac: [String: Double] = [:]
        for text in texts {
            var cols: [String: Int] = [:]
            for line in text.split(separator: "\n") {
                let v = line.split(separator: ",", omittingEmptySubsequences: false)
                if cols.isEmpty || v.first == "tijd" { for (i, c) in v.enumerated() { cols[String(c)] = i }; continue }
                guard let ti = cols["tijd"], let ai = cols["app"], let ei = cols["energie_w"], v.count > max(ti, ai, ei) else { continue }
                let t = String(v[ti])
                guard t >= from, t <= to, let w = Double(v[ei]), w > 0 else { continue }
                perApp[String(v[ai]), default: 0] += w / 60
                if let mi = cols["mac_energie_w"], mi < v.count, let m = Double(v[mi]) { mac[t] = m / 60 }
            }
        }
        let sum = perApp.values.reduce(0, +)
        let total = max(mac.values.reduce(0, +), sum)
        return (perApp.sorted { $0.value != $1.value ? $0.value > $1.value : $0.key < $1.key }.map { ($0.key, $0.value) }, total)
    }

    struct Drain: Sendable {
        var from: Date, to: Date?
        var unplugged: Bool             // false: vandaag, aan de lader
        var startPct: Int?, endPct: Int?
        var apps: [(name: String, wh: Double)] = []
        var total = 0.0
    }

    /// Accu nu: nil op een Mac zonder accu.
    static func state() -> (ac: Bool, pct: Int?)? {
        let info = IOPSCopyPowerSourcesInfo().takeRetainedValue()
        for src in IOPSCopyPowerSourcesList(info).takeRetainedValue() as [CFTypeRef] {
            if let d = IOPSGetPowerSourceDescription(info, src)?.takeUnretainedValue() as? [String: Any],
               d[kIOPSTypeKey] as? String == kIOPSInternalBatteryType {
                return (d[kIOPSPowerSourceStateKey] as? String == kIOPSACPowerValue, d[kIOPSCurrentCapacityKey] as? Int)
            }
        }
        return nil
    }
    static let hasBattery = state() != nil

    static func drain(_ events: [Event], dir: URL, now: Date = Date()) -> Drain {
        let st = state(), cal = Calendar.current
        var d = Drain(from: cal.startOfDay(for: now), to: nil, unplugged: false)
        if let s = lastStretch(events), st?.ac == false ? s.end == nil : s.end.map(cal.isDateInToday) == true {
            d = Drain(from: s.start, to: s.end, unplugged: true, startPct: s.from, endPct: s.end == nil ? st?.pct : s.to)
        }
        var texts: [String] = [], day = cal.startOfDay(for: d.from)
        while day <= now {
            if let t = try? String(contentsOf: dir.appendingPathComponent("apps-\(dayName(day)).csv"), encoding: .utf8) { texts.append(t) }
            day = cal.date(byAdding: .day, value: 1, to: day)!
        }
        let r = rank(texts, from: Sample.stamp.string(from: d.from), to: Sample.stamp.string(from: d.to ?? now))
        d.apps = Array(r.apps.prefix(6)); d.total = r.total
        return d
    }

    // MARK: Wat houdt de Mac wakker

    struct Hold: Sendable, Identifiable, Equatable {
        let app: String, what: String, age: String
        var id: String { app + what }
    }

    /// "   pid 36877(Claude): [0x…] 00:02:29 NoIdleSleepAssertion named: "Electron"" (+ evt. "Created for PID: 4895.")
    static func assertions(_ text: String) -> [(pid: Int32, process: String, type: String, age: String, name: String, forPID: Int32?)] {
        var out: [(pid: Int32, process: String, type: String, age: String, name: String, forPID: Int32?)] = []
        for raw in text.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("Created for PID: "), !out.isEmpty {
                out[out.count - 1].forPID = Int32(line.dropFirst(17).prefix { $0.isNumber }); continue
            }
            guard line.hasPrefix("pid "), let open = line.firstIndex(of: "("), let close = line.range(of: "): ["),
                  let pid = Int32(line[line.index(line.startIndex, offsetBy: 4)..<open]),
                  let bracket = line.range(of: "] ", range: close.upperBound..<line.endIndex) else { continue }
            let rest = line[bracket.upperBound...].split(separator: " ", maxSplits: 2)
            guard rest.count == 3 else { continue }
            let name = rest[2].range(of: "named: \"").map { String(rest[2][$0.upperBound...].dropLast(rest[2].hasSuffix("\"") ? 1 : 0)) } ?? ""
            out.append((pid, String(line[line.index(after: open)..<close.lowerBound]), String(rest[1]), String(rest[0]), name, nil))
        }
        return out
    }

    /// Alleen wat slaap of schermslaap tegenhoudt; vaste systeemonderdelen (powerd, WindowServer …) niet.
    static func holds(_ text: String, appName: (Int32) -> String? = { _ in nil }) -> [Hold] {
        let what = ["PreventUserIdleDisplaySleep": "keeping the screen on", "NoDisplaySleepAssertion": "keeping the screen on",
                    "PreventUserIdleSystemSleep": "preventing sleep", "NoIdleSleepAssertion": "preventing sleep",
                    "PreventSystemSleep": "preventing all sleep"]
        let system: Set<String> = ["powerd", "WindowServer", "kernel_task", "hidd", "loginwindow", "sharingd", "bluetoothd"]
        var seen = Set<String>(), out: [Hold] = []
        for a in assertions(text) {
            guard let w = what[a.type] else { continue }
            let app: String
            if let p = a.forPID { app = appName(p) ?? "pid \(p)" }
            else if a.process == "runningboardd" || system.contains(a.process) { continue }
            else if a.process == "coreaudiod" { app = "Audio playing" }
            else { app = appName(a.pid) ?? a.process }
            // "00:02:29" → "2m"; korter dan een minuut: geen tijd
            let p = a.age.split(separator: ":").compactMap { Int($0) }
            let mins = p.count == 3 ? p[0] * 60 + p[1] : 0
            let h = Hold(app: app, what: w, age: mins > 0 ? duration(mins) : "")
            if seen.insert(h.id).inserted { out.append(h) }
        }
        return out
    }

    static func readHolds() -> [Hold] {
        holds(Diag.run("/usr/bin/pmset", ["-g", "assertions"], timeout: 5)) { NSRunningApplication(processIdentifier: $0)?.localizedName?.trimmingCharacters(in: .whitespaces.union(.controlCharacters)) }   // WhatsApp begint met een onzichtbaar richtingsteken
    }

    static func selfTest() {
        let log = """
        2026-11-17 23:10:00 +0100 Sleep               \tEntering Sleep state due to 'Clamshell Sleep':TCPKeepAlive=active Using Batt (Charge:80%) 600 secs
        2026-11-18 01:00:00 +0100 DarkWake            \tDarkWake from Deep Idle [CDNP] : due to smc.sysState.Wake(0x70070000) wifibt SMC.OutboxNotEmpty/ Using BATT (Charge:79%) 6 secs
        2026-11-18 01:00:06 +0100 Sleep               \tEntering Sleep state due to 'Maintenance Sleep':TCPKeepAlive=active Using Batt (Charge:79%) 99 secs
        2026-11-18 02:00:00 +0100 DarkWake            \tDarkWake from Deep Idle [CDNP] : due to NUB.SPMI0.SW3 nub-spmi0.0x02 rtc/Maintenance Using BATT (Charge:78%) 5 secs
        2026-11-18 02:00:00 +0100 Wake Requests       \t[process=mDNSResponder request=Maintenance]
        2026-11-18 07:40:00 +0100 Wake                \tWake from Deep Idle [CDNVA] : due to smc.sysState.Wake(0x70070000) lid SMC.OutboxNotEmpty RTP.multi-touch/HID Activity Using BATT (Charge:74%)
        2026-11-18 08:00:00 +0100 Sleep               \tEntering Sleep state due to 'Idle Sleep':TCPKeepAlive=active Using Batt (Charge:73%) 60 secs
        2026-11-18 08:05:00 +0100 Wake                \tWake from Deep Idle [CDNVA] : due to UserActivity Assertion Using BATT (Charge:73%)
        2026-11-18 09:00:00 +0100 Assertions          \tSummary- [System: PrevIdle DeclUser kDisp] Using AC(Charge: 73)
        2026-11-18 10:00:00 +0100 Assertions          \tSummary- [System: PrevIdle DeclUser kDisp] Using Batt(Charge: 90)
        rommel
        """
        let ev = log.split(separator: "\n").compactMap(parse)
        precondition(ev.count == 9 && ev[0].kind == .sleep && ev[0].reason == "Clamshell Sleep" && ev[0].charge == 80 && ev[0].ac == false
                     && ev[8].kind == .summary && ev[7].ac == true && ev[7].charge == 73, "pmset-regels \(ev.count)")
        let s = sleeps(ev)
        precondition(s.count == 1 && s[0].lost == 6 && s[0].wokeBy == "lid opened" && s[0].minutes == 510
                     && s[0].darkWakes == ["network access (Wi-Fi/Bluetooth)": 1, "Power Nap / maintenance": 1], "slaapperiodes \(s)")
        precondition(reason("NUB.SPMI0.SW3 nub-spmi0.0x02 rtc/SleepService") == "scheduled background task"
                     && reason("MTP.DOCK.CHANNELS.AP0.IRQ RTP.keyboard/UserActivity Assertion") == "keyboard", "wekredenen")
        let st = lastStretch(ev)
        precondition(st?.from == 90 && st?.end == nil && st?.start == ev[8].time, "laatste stuk op de accu \(String(describing: st))")
        let done = lastStretch(Array(ev.prefix(8)))
        precondition(done?.from == 80 && done?.to == 73 && done?.end == ev[7].time, "afgesloten stuk")

        let csv = AppUsage.csvHeader + """
        2026-11-18 09:59:00,Zoom,app,30.0,1.00,6.00,0.0,12.00
        2026-11-18 10:00:00,Zoom,app,30.0,1.00,6.00,0.0,12.00
        2026-11-18 10:00:00,Safari,app,5.0,1.00,3.00,0.0,12.00
        2026-11-18 10:01:00,Safari,app,5.0,1.00,9.00,0.0,
        2026-11-18 10:01:00,kort,app
        """
        let r = rank([csv], from: "2026-11-18 10:00:00", to: "2026-11-18 23:59:59")
        precondition(r.apps.map(\.name) == ["Safari", "Zoom"] && abs(r.apps[0].wh - 0.2) < 1e-9 && abs(r.total - 0.3) < 1e-9, "energie-ranglijst \(r)")

        let asr = """
        Listed by owning process:
           pid 416(runningboardd): [0x000051580001a0da] 00:00:02 PreventUserIdleSystemSleep named: "app<application.net.whatsapp>(FinishTask)"
        \tCreated for PID: 4895.
           pid 411(WindowServer): [0x0000501500099f0e] 00:02:29 UserIsActive named: "com.apple.iohideventsystem.queue.tickle"
           pid 36877(zoom.us): [0x000050c50001a06c] 01:12:29 PreventUserIdleDisplaySleep named: "Zoom meeting"
           pid 354(powerd): [0x0000501500019f10] 00:05:25 PreventUserIdleSystemSleep named: "Powerd - Prevent sleep while display is on"
           pid 120(coreaudiod): [0x0000501500019f11] 00:03:00 PreventUserIdleSystemSleep named: "com.apple.audio.context"
        No kernel assertions.
        """
        let h = holds(asr) { $0 == 4895 ? "WhatsApp" : $0 == 36877 ? "Zoom" : nil }
        precondition(h == [Hold(app: "WhatsApp", what: "preventing sleep", age: ""), Hold(app: "Zoom", what: "keeping the screen on", age: "1h 12m"),
                           Hold(app: "Audio playing", what: "preventing sleep", age: "3m")], "assertions \(h)")
        precondition(BatteryHealth.parse(BatteryHealth.header + "2026-11-17,103,31\n2026-11-18,102,\nrommel\n").map(\.pct) == [103, 102], "gezondheid-csv")
    }
}

// MARK: - Accugezondheid per dag (Documents/Fusebox/battery-health.csv)

enum BatteryHealth {
    static let header = "datum,gezondheid_pct,cycli\n"
    nonisolated(unsafe) private static var lastDay = ""   // alleen vanaf de hoofdthread (Meter.minuteTasks)

    static func file(_ dir: URL) -> URL { dir.appendingPathComponent("battery-health.csv") }

    /// Elke minuut aangeroepen; doet pas iets als de datum veranderde (dus één keer per dag, en bij opstarten).
    static func tick(_ now: Date, dir: URL) {
        let day = Power.dayName(now)
        guard day != lastDay else { return }
        lastDay = day
        DispatchQueue.global(qos: .utility).async { record(day, dir: dir) }
    }

    private static func record(_ day: String, dir: URL) {
        guard let b = Diag.battery(), let cap = b.capacity else { return }
        let f = file(dir), text = (try? String(contentsOf: f, encoding: .utf8)) ?? ""
        guard !text.contains("\n\(day),") else { return }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        if text.isEmpty { try? header.write(to: f, atomically: true, encoding: .utf8) }
        _ = Meter.append("\(day),\(cap),\(b.cycles.map(String.init) ?? "")\n", to: f)
    }

    static func parse(_ text: String) -> [(day: String, pct: Int, cycles: Int?)] {
        text.split(separator: "\n").compactMap { l in
            let v = l.split(separator: ",", omittingEmptySubsequences: false)
            guard v.count >= 2, let p = Int(v[1]) else { return nil }
            return (String(v[0]), p, v.count > 2 ? Int(v[2]) : nil)
        }
    }
}

// MARK: - Weergave

/// Het pmset-log één keer lezen voor beide accu-onderdelen; 10 minuten bewaard.
@MainActor private enum PowerLog {
    private static var task: Task<[Power.Event], Never>?, at = Date.distantPast
    static func events() async -> [Power.Event] {
        if task == nil || Date().timeIntervalSince(at) > 3600 {   // pmset-log lezen kost ~5 s processor: hooguit 1× per uur
            at = Date(); task = Task.detached(priority: .utility) { Power.readLog() } }
        return await task!.value
    }
}

private let when: DateFormatter = { let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "EEE HH:mm"; return f }()

private struct PowerCard<Content: View>: View {
    @Environment(\.theme) private var theme
    let title: String, index: Int
    var refresh: (() -> Void)?
    @ViewBuilder let content: Content
    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text(title).font(theme.label(12)).foregroundStyle(theme.onRow())
                Spacer()
                if let refresh {
                    Button(action: refresh) { Image(systemName: "arrow.clockwise") }.buttonStyle(.plain).foregroundStyle(theme.onRow(true)).help("Check again")
                }
            }
            content.font(theme.label(11))
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
    }
}

/// 1. Wat trok de accu leeg: apps op energie sinds het loskoppelen.
struct DrainSection: View {
    @Environment(\.theme) private var theme
    let index: Int, dir: URL
    @MainActor private static var cache: Power.Drain?
    @State private var drain = MainActor.assumeIsolated { DrainSection.cache }

    var body: some View {
        PowerCard(title: "What drained my battery", index: index) {
            if let d = drain {
                Text(subtitle(d)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                if d.apps.isEmpty { Text("No app data for this time yet").foregroundStyle(theme.onRow(true)) }
                let top = d.apps.first?.wh ?? 1, bar = theme.rowStyle == .blocks ? Color.white : theme.accent
                ForEach(d.apps, id: \.name) { a in
                    HStack(spacing: 6) {
                        Text(a.name).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.tail)
                        Spacer(minLength: 4)
                        Capsule().fill(bar.opacity(0.7)).frame(width: 50 * max(a.wh / max(top, 0.001), 0.04), height: 4)
                        // Alleen het aandeel: macOS telt per app maar een deel van de energie (scherm e.d. niet), de Wh zeiden niets
                        Text(d.total > 0 ? pct(a.wh / d.total * 100) : "–").font(theme.number(11)).foregroundStyle(theme.onRow()).frame(width: 38, alignment: .trailing)
                    }
                }
            } else { ProgressView().controlSize(.small) }
        }
        .task {
            let ev = await PowerLog.events(), dir = dir
            let d = await Task.detached(priority: .utility) { Power.drain(ev, dir: dir) }.value
            Self.cache = d; drain = d
        }
    }

    private func subtitle(_ d: Power.Drain) -> String {
        guard d.unplugged else { return "Today · on the charger now" }
        let pcts = d.startPct.map { s in "\(s)% → " + (d.endPct.map { "\($0)%" } ?? "?") } ?? ""
        return "Since unplugged \(when.string(from: d.from))" + (d.to.map { " until \(when.string(from: $0))" } ?? "") + (pcts.isEmpty ? "" : " · " + pcts)
    }
}

/// 2. Accuverbruik tijdens slaap en wat de Mac wekte.
struct SleepDrainSection: View {
    @Environment(\.theme) private var theme
    let index: Int
    @MainActor private static var cache: [Power.SleepSpan]?
    @State private var spans = MainActor.assumeIsolated { SleepDrainSection.cache }

    var body: some View {
        PowerCard(title: "Battery use while asleep", index: index) {
            if let spans {
                if spans.isEmpty { Text("No sleep longer than 30 minutes in the last 2 days").foregroundStyle(theme.onRow(true)) }
                ForEach(spans.suffix(4).reversed()) { s in
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 6) {
                            Text("\(when.string(from: s.start)) → \(when.string(from: s.end))").foregroundStyle(theme.onRow())
                            Text(duration(s.minutes)).foregroundStyle(theme.onRow(true))
                            Spacer(minLength: 4)
                            Text(s.lost.map { $0 > 0 ? "−\($0)%" : "0%" } ?? "on charger").font(theme.number(11))
                                .foregroundStyle((s.lost ?? 0) >= 3 && (s.lost ?? 0) * 60 > s.minutes ? theme.warn : theme.onRow())   // meer dan 1% per uur
                        }
                        Text(detail(s)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                    }
                }
            } else { ProgressView().controlSize(.small) }
        }
        .task {
            let ev = await PowerLog.events()
            let s = await Task.detached(priority: .utility) { Power.sleeps(ev) }.value
            Self.cache = s; spans = s
        }
    }

    /// "Woken by lid opened · woke briefly 41×, mostly network access (Wi-Fi/Bluetooth)"
    private func detail(_ s: Power.SleepSpan) -> String {
        let n = s.darkWakes.values.reduce(0, +)
        let most = s.darkWakes.max { $0.value < $1.value }?.key
        return "Woken by \(s.wokeBy)" + (n > 0 ? " · woke briefly \(n)×" + (most.map { ", mostly \($0)" } ?? "") : "")
    }
}

/// 3. Wat houdt de Mac nu wakker (pmset -g assertions).
struct AwakeSection: View {
    @Environment(\.theme) private var theme
    let index: Int
    @MainActor private static var cache: [Power.Hold]?
    @State private var holds = MainActor.assumeIsolated { AwakeSection.cache }
    @State private var run = 0

    var body: some View {
        PowerCard(title: "What keeps my Mac awake", index: index, refresh: { run += 1 }) {
            if let holds {
                if holds.isEmpty { Text("Nothing: your Mac can sleep normally").foregroundStyle(theme.onRow(true)) }
                ForEach(holds) { h in
                    HStack(spacing: 6) {
                        Text(h.app).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.tail)
                        Text(h.what).foregroundStyle(theme.onRow(true)).lineLimit(1)
                        Spacer(minLength: 4)
                        if !h.age.isEmpty { Text(h.age).font(theme.number(11)).foregroundStyle(theme.onRow(true)) }
                    }
                }
            } else { ProgressView().controlSize(.small) }
        }
        .task(id: run) {
            let h = await Task.detached(priority: .utility) { Power.readHolds() }.value
            Self.cache = h; holds = h
        }
    }
}

/// 4. Accugezondheid over weken: lijn van de dagelijkse metingen + laadcycli.
struct BatteryHealthSection: View {
    @Environment(\.theme) private var theme
    let index: Int, dir: URL
    @State private var data: (rows: [(day: String, pct: Int, cycles: Int?)], now: Diag.Battery?)?

    var body: some View {
        PowerCard(title: "Battery health", index: index) {
            if let data {
                let rows = data.rows
                if let b = data.now {
                    Text([b.capacity.map { "\($0)% of original capacity" }, b.cycles.map { "\($0) cycles" }].compactMap { $0 }.joined(separator: " · "))
                        .foregroundStyle(theme.onRow())
                }
                if rows.count >= 2, let first = rows.first, let last = rows.last {
                    let v = rows.map { Double($0.pct) }, lo = (v.min() ?? 0) - 1, hi = (v.max() ?? 100) + 1
                    Spark(values: v, top: hi, fill: 1, closed: false, bottom: lo)
                        .stroke(theme.rowStyle == .blocks ? Color.white : theme.accent, style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
                        .frame(height: 28)
                    let cyc = first.cycles.flatMap { f in last.cycles.map { $0 - f } }
                    Text("Since \(first.day): \(first.pct)% → \(last.pct)%" + (cyc.map { " · +\($0) cycles" } ?? ""))
                        .foregroundStyle(theme.onRow(true))
                } else {
                    Text("Recorded once a day; the trend shows after a few days").foregroundStyle(theme.onRow(true))
                }
            } else { ProgressView().controlSize(.small) }
        }
        .task {
            let f = BatteryHealth.file(dir)
            data = await Task.detached(priority: .utility) {
                (BatteryHealth.parse((try? String(contentsOf: f, encoding: .utf8)) ?? ""), Diag.battery())
            }.value
        }
    }
}
