import Foundation
import IOKit
import IOKit.ps
import Darwin
import SystemConfiguration

// MARK: - Extra diagnoses voor de Checkup-pagina
//
// Lekken, weglopers en macOS-huishouding komen uit de apps-CSV (geen extra metingen).
// Crashes, batterij, ps en tmutil worden alleen gelezen bij openen/verversen van de pagina of "Check now",
// altijd buiten `body` (Task.detached).

enum Diag {
    /// Eén minuut van één app uit de apps-log (cpu = % van de hele Mac, mem in GB).
    struct Point: Sendable { let t: Date; let cpu: Double; let mem: Double }
    struct Leak: Sendable { let app: String; let from: Double, to: Double; let start: Date, end: Date
        var hours: Int { max(1, Int((end.timeIntervalSince(start) / 3600).rounded())) }
    }
    struct Run: Sendable { let app: String; let start: Date, end: Date
        var minutes: Int { Int((end.timeIntervalSince(start) / 60).rounded()) }
    }

    // MARK: Aan sinds

    /// Echte opstarttijd (ProcessInfo.systemUptime telt slaap niet mee).
    static func bootDate() -> Date? {
        var tv = timeval(), size = MemoryLayout<timeval>.size
        guard sysctlbyname("kern.boottime", &tv, &size, nil, 0) == 0, tv.tv_sec > 0 else { return nil }
        return Date(timeIntervalSince1970: Double(tv.tv_sec))
    }
    /// Herstart-tip vanaf zoveel dagen aan (Instellingen › Checkup); 0 = nooit.
    static var restartDays: Int { let d = UserDefaults.standard.integer(forKey: "restartDays"); return d > 0 ? d : .max }

    static func sysctlString(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 0 else { return nil }
        var buf = [CChar](repeating: 0, count: size)
        guard sysctlbyname(name, &buf, &size, nil, 0) == 0 else { return nil }
        return String(cString: buf)
    }

    // MARK: Lekken en weglopers

    static func median(_ v: [Double]) -> Double { let s = v.sorted(); return s.isEmpty ? 0 : s[s.count / 2] }

    /// Correlatie tijd ↔ geheugen: 1 = keurig gelijkmatige stijging.
    static func trend(_ p: ArraySlice<Point>) -> Double {
        guard let t0 = p.first?.t else { return 0 }
        let n = Double(p.count), xs = p.map { $0.t.timeIntervalSince(t0) }, ys = p.map(\.mem)
        let mx = xs.reduce(0, +) / n, my = ys.reduce(0, +) / n
        var sxy = 0.0, sxx = 0.0, syy = 0.0
        for i in xs.indices { let dx = xs[i] - mx, dy = ys[i] - my; sxy += dx * dy; sxx += dx * dx; syy += dy * dy }
        return sxx > 0 && syy > 0 ? sxy / (sxx * syy).squareRoot() : 0
    }

    /// Knipt een reeks bij een gat > 30 min of een herstart (geheugen ineens < 60 % van de vorige minuut).
    static func pieces(_ pts: [Point]) -> [ArraySlice<Point>] {
        var out: [ArraySlice<Point>] = [], start = 0
        for i in pts.indices.dropFirst() where pts[i].t.timeIntervalSince(pts[i - 1].t) > 1800 || pts[i].mem < pts[i - 1].mem * 0.6 {
            out.append(pts[start..<i]); start = i
        }
        if !pts.isEmpty { out.append(pts[start...]) }
        return out
    }

    /// Geheugen dat ≥ 3 uur gelijkmatig stijgt: ≥ 1,5× en ≥ 1 GB erbij, weinig dalingen. Per app de grootste, grootste eerst.
    /// ponytail: alleen het hele stuk tussen gaten/herstarts telt; lang vlak en daarna een korte stijging valt erbuiten (glijdend venster als dat gemist wordt).
    static func leaks(_ series: [String: [Point]]) -> [Leak] {
        series.compactMap { app, pts in
            pieces(pts).compactMap { p -> Leak? in
                guard p.count >= 30, let a = p.first, let b = p.last, b.t.timeIntervalSince(a.t) >= 3 * 3600 else { return nil }
                let from = median(p.prefix(5).map(\.mem)), to = median(p.suffix(5).map(\.mem))
                var peak = 0.0, drops = 0
                for x in p { if x.mem < peak * 0.85 { drops += 1 }; peak = max(peak, x.mem) }
                guard from > 0, to >= from * 1.5, to - from >= 1, drops <= p.count / 10, trend(p) >= 0.7 else { return nil }
                return Leak(app: app, from: from, to: to, start: a.t, end: b.t)
            }.max { $0.to - $0.from < $1.to - $1.from }
        }.sorted { $0.to - $0.from > $1.to - $1.from }
    }

    /// Reeksen minuten waarvoor `test` geldt (onderbreking ≤ `gap` s), met minstens `minutes` minuten erin.
    static func spans(_ pts: [Point], minutes: Int, gap: Double = 150, _ test: (Point) -> Bool) -> [(start: Date, end: Date)] {
        var out: [(start: Date, end: Date)] = [], first: Date?, last: Date?, n = 0
        func close() { if let first, let last, n >= minutes { out.append((first, last.addingTimeInterval(60))) }; first = nil; last = nil; n = 0 }
        for p in pts where test(p) {
            if let l = last, p.t.timeIntervalSince(l) > gap { close() }
            if first == nil { first = p.t }
            last = p.t; n += 1
        }
        close()
        return out
    }

    /// Een app die ≥ 10 minuten op rij een hele kern (100 / kernen % van de Mac) gebruikt.
    static func runaways(_ series: [String: [Point]], cores: Double) -> [Run] {
        series.flatMap { app, pts in spans(pts, minutes: 10) { $0.cpu >= 100 / cores }.map { Run(app: app, start: $0.start, end: $0.end) } }
            .sorted { $0.start > $1.start }
    }

    // MARK: Huishouding van macOS (gaat vanzelf over)

    struct Job { let doing: String; let procs: [String]; let names: [String] }
    /// `names` = gewone namen uit AppMonitor.friendly (zo staan ze in de apps-CSV); `procs` = procesnamen voor ps.
    static let jobs: [Job] = [
        Job(doing: L("Spotlight is indexing"), procs: ["mds", "mdworker", "corespotlightd"], names: ["Spotlight (search)"]),
        Job(doing: L("iCloud is syncing"), procs: ["bird", "cloudd", "fileproviderd"], names: ["iCloud", "iCloud Drive"]),
        Job(doing: L("Photos is analysing your library"), procs: ["photoanalysisd", "mediaanalysisd"], names: ["Photo analysis", "Media analysis"]),
        Job(doing: L("Time Machine is making a backup"), procs: ["backupd"], names: ["Time Machine"]),
        Job(doing: L("Software Update is checking or installing updates"), procs: ["softwareupdated"], names: ["Software update"]),
        Job(doing: coolingDown, procs: ["kernel_task"], names: []),   // kernel_task is niet te meten; zie thermalState hieronder
    ]
    static let jobCore = 25.0   // % van één kern: daarboven telt het als "bezig"
    static func job(name: String) -> Job? { jobs.first { $0.names.contains(name) } }
    static func job(proc: String) -> Job? { jobs.first { $0.procs.contains { proc.hasPrefix($0) } } }

    /// Uit de apps-log: minuten per klus en reeksen van ≥ 5 min (onderbreking ≤ 10 min) voor het logboek.
    static func housekeeping(_ series: [String: [Point]], cores: Double) -> (minutes: [String: Int], runs: [Run]) {
        var bucket: [String: [Date: Double]] = [:]
        for (name, pts) in series { guard let j = job(name: name) else { continue }
            for p in pts { bucket[j.doing, default: [:]][p.t, default: 0] += p.cpu }
        }
        var minutes: [String: Int] = [:], runs: [Run] = []
        for (doing, b) in bucket {
            let pts = b.sorted { $0.key < $1.key }.map { Point(t: $0.key, cpu: $0.value * cores, mem: 0) }
            let n = pts.filter { $0.cpu >= jobCore }.count
            if n > 0 { minutes[doing] = n }
            runs += spans(pts, minutes: 5, gap: 600) { $0.cpu >= jobCore }.map { Run(app: doing, start: $0.start, end: $0.end) }
        }
        return (minutes, runs)
    }

    /// Nu bezig? Apps uit de minuutmeting; root-processen (mds_stores, backupd…) alleen via ps als "macOS itself" > 30 %.
    static func housekeepingNow(apps: [(name: String, cpu: Double)], cores: Double) -> [String] {
        var busy = Set(apps.compactMap { $0.cpu * cores >= jobCore ? job(name: $0.name)?.doing : nil })
        if (apps.first { $0.name == "macOS itself" }?.cpu ?? 0) > 30 {
            var n = proc_listallpids(nil, 0)
            var all = [pid_t](repeating: 0, count: Int(max(n, 0)) + 64)
            n = proc_listallpids(&all, Int32(all.count * MemoryLayout<pid_t>.size))
            var pids: [String] = []
            for pid in all.prefix(Int(max(n, 0))) where pid > 0 {
                var buf = [CChar](repeating: 0, count: 64)
                if proc_name(pid, &buf, UInt32(buf.count)) > 0, job(proc: String(cString: buf)) != nil { pids.append(String(pid)) }
            }
            if !pids.isEmpty {
                for p in parsePS(run("/bin/ps", ["-o", "%cpu=,comm=", "-p", pids.joined(separator: ",")])) where p.cpu >= jobCore {
                    if let j = job(proc: p.proc) { busy.insert(j.doing) }
                }
            }
        }
        if run("/usr/bin/tmutil", ["status"]).contains("Running = 1") { busy.insert(jobs[3].doing) }
        if isCooling(ProcessInfo.processInfo.thermalState) { busy.insert(coolingDown) }
        return jobs.map(\.doing).filter(busy.contains)
    }

    /// "  12.5 /usr/libexec/mds_stores" → ("mds_stores", 12.5). Komma als decimaalteken mag ook.
    static func parsePS(_ out: String) -> [(proc: String, cpu: Double)] {
        out.split(separator: "\n").compactMap { line in
            let parts = line.trimmingCharacters(in: .whitespaces).split(separator: " ", maxSplits: 1)
            guard parts.count == 2, let c = Double(parts[0].replacingOccurrences(of: ",", with: ".")) else { return nil }
            return ((parts[1].trimmingCharacters(in: .whitespaces) as NSString).lastPathComponent, c)
        }
    }

    /// `timeout` > 0: daarna afbreken (dan geeft het wat er tot dan toe kwam).
    static func run(_ path: String, _ args: [String], timeout: Double = 0) -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path); p.arguments = args
        p.environment = ["LC_ALL": "C"]
        let pipe = Pipe()
        p.standardOutput = pipe; p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return "" }
        if timeout > 0 { DispatchQueue.global().asyncAfter(deadline: .now() + timeout) { if p.isRunning { p.terminate() } } }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(decoding: data, as: UTF8.self)
    }

    // MARK: Crashes en meldingen van macOS (DiagnosticReports)

    /// `panic` = kernel panic: de hele Mac crashte en startte opnieuw op.
    enum Entry: Equatable { case crash(String), hang(String), jetsam, resource(String), panic, none }

    struct Crashes: Sendable {
        var crashes: [String: Int] = [:], hangs: [String: Int] = [:], resource: [String: Int] = [:]
        var jetsam = 0, panics = 0
        var times: [String: [Date]] = [:]   // tijdstippen per gecrashte app (voor crashlussen)
        var stamps: [Date] = []             // elk tijdstip van een crash, vastloper of kernel panic (strook per dag)
        var isEmpty: Bool { crashes.isEmpty && hangs.isEmpty && resource.isEmpty && jetsam == 0 && panics == 0 }
        /// Regels voor scherm en rapport, meeste eerst.
        var rows: [(label: String, value: String)] {
            func list(_ d: [String: Int], _ what: (Int) -> String) -> [(label: String, value: String)] {
                d.sorted { $0.value == $1.value ? $0.key < $1.key : $0.value > $1.value }.map { ($0.key, what($0.value)) }
            }
            let loops = self.loops
            return (panics > 0 ? [(L("Mac restarted by a crash"), L("kernel panic \(panics)×"))] : [])
                + list(crashes, { L("crashed \($0)×") }).map { ($0.label, $0.value + (loops[$0.label].map { L(", \($0)× in one hour") } ?? "")) }
                + list(hangs, { L("stopped responding \($0)×") })
                + (jetsam > 0 ? [(L("Out of memory"), L("macOS closed apps \(jetsam)×"))] : [])
                + list(resource, { L("flagged by macOS \($0)×") }).prefix(5)
        }
    }

    private static let stampFormat: DateFormatter = {
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "yyyy-MM-dd HH:mm:ss Z"; return f
    }()
    /// "2026-11-10 19:56:59.00 +0100" (breuk van seconden wisselt in lengte).
    static func stamp(_ s: String) -> Date? {
        let s = s.trimmingCharacters(in: .whitespaces)
        guard s.count >= 19, let tz = s.split(separator: " ").last, tz.hasPrefix("+") || tz.hasPrefix("-") else { return nil }
        return stampFormat.date(from: "\(s.prefix(19)) \(tz)")
    }

    /// Kop van een rapport: .ips = JSON op regel 1; .diag/.hang/.spin = tekst met "Command:" en "Event:".
    /// Kernel panic: bug_type 210 (oud: 10) volgens de tabel van macOS zelf (OSAnalytics, submissionConfig.plist),
    /// of een bestand dat "panic-…" of "….panic" heet. Van een panic lezen we alleen deze kopregel, nooit de tekst
    /// (daar staan serienummers en paden in).
    static func parseHeader(_ head: String, fileName: String) -> (entry: Entry, time: Date?) {
        let panicName = fileName.hasPrefix("panic-") || fileName.hasSuffix(".panic")
        if fileName.hasSuffix(".ips") {
            guard let line = head.split(separator: "\n").first,
                  let j = try? JSONSerialization.jsonObject(with: Data(line.utf8)) as? [String: Any] else { return (panicName ? .panic : .none, nil) }
            let time = (j["timestamp"] as? String).flatMap(stamp)
            if fileName.hasPrefix("JetsamEvent") { return (.jetsam, time) }
            if (j["is_simulated"] as? Int) == 1 { return (.none, time) }   // nagebootst rapport, geen echte crash
            let name = (j["app_name"] as? String) ?? (j["name"] as? String)
            if panicName { return (.panic, time) }
            switch "\(j["bug_type"] ?? "")" {
            case "210", "10": return (.panic, time)
            case "309", "109": return (name.map(Entry.crash) ?? .none, time)
            case "298": return (.jetsam, time)
            default: return (.none, time)
            }
        }
        if panicName { return (.panic, nil) }   // tekstbestand zonder vaste kop: de bestandsdatum telt
        var app: String?, proc: String?, event = "", time: Date?
        for line in head.split(separator: "\n").prefix(40) {
            let parts = line.split(separator: ":", maxSplits: 1)
            guard parts.count == 2 else { continue }
            let value = parts[1].trimmingCharacters(in: .whitespaces)
            switch parts[0] {
            case "Command": app = value
            case "Process": proc = value.components(separatedBy: " [").first   // spindump: "Process: Microsoft Teams [812]"
            case "Event": event = value.lowercased()
            case "Date/Time": time = stamp(value)
            default: break
            }
        }
        guard let app = app ?? proc else { return (.none, time) }
        if event.contains("hang") || event.contains("spin") { return (.hang(app), time) }
        if event.contains("disk writes") { return (.resource(L("\(app) (disk writes)")), time) }
        if event.contains("cpu") { return (.resource(L("\(app) (processor)")), time) }
        if event.contains("wakeups") { return (.resource(L("\(app) (wakeups)")), time) }
        return (.none, time)
    }

    /// Rapport van een programma dat uit een tijdelijke map draaide (/tmp, /private/var/folders): een test- of
    /// proefbouw, geen app die de gebruiker gebruikt. procPath staat in de JSON-kop met \/ als scheidingsteken.
    static func testBuild(_ head: String) -> Bool {
        guard let r = head.range(of: "\"procPath\"") else { return false }
        let line = head[r.upperBound...].prefix(300).replacingOccurrences(of: "\\/", with: "/")
        let path = line.split(separator: "\"").dropFirst().first.map(String.init) ?? ""
        return ["/tmp/", "/private/tmp/", "/private/var/folders/", "/var/folders/"].contains { path.hasPrefix($0) }
    }

    /// Telt rapporten tussen `from` en `to`. Alleen de eerste 4 KB per bestand, hoogstens de nieuwste 300; Retired/ telt niet.
    static func crashes(from: Date, to: Date) -> Crashes {
        let fm = FileManager.default, keys: Set<URLResourceKey> = [.contentModificationDateKey, .isRegularFileKey]
        let dirs = [fm.homeDirectoryForCurrentUser, URL(fileURLWithPath: "/")].map { $0.appendingPathComponent("Library/Logs/DiagnosticReports") }
        var files: [(url: URL, date: Date)] = []
        for d in dirs {
            // geen leesrecht (bv. /Library) = gewoon overslaan
            guard let list = try? fm.contentsOfDirectory(at: d, includingPropertiesForKeys: Array(keys), options: .skipsHiddenFiles) else { continue }
            for u in list {
                guard let v = try? u.resourceValues(forKeys: keys), v.isRegularFile == true,
                      let m = v.contentModificationDate, m >= from.addingTimeInterval(-86_400) else { continue }
                files.append((u, m))
            }
        }
        var out = Crashes()
        for f in files.sorted(by: { $0.date > $1.date }).prefix(300) {
            guard let h = try? FileHandle(forReadingFrom: f.url) else { continue }
            let data = try? h.read(upToCount: 4096)
            try? h.close()
            guard let data else { continue }
            let head = String(decoding: data, as: UTF8.self)
            let (entry, time) = parseHeader(head, fileName: f.url.lastPathComponent)
            // proefversies uit een tijdelijke map (bouwen/testen) zijn geen echte crashes; een kernel panic is altijd echt
            if entry != .panic, testBuild(head) { continue }
            let t = time ?? f.date
            guard t >= from, t < to else { continue }
            switch entry {
            case .crash(let a): out.crashes[a, default: 0] += 1; out.times[a, default: []].append(t); out.stamps.append(t)
            case .hang(let a): out.hangs[a, default: 0] += 1; out.stamps.append(t)
            case .panic: out.panics += 1; out.stamps.append(t)
            case .jetsam: out.jetsam += 1
            case .resource(let a): out.resource[a, default: 0] += 1
            case .none: break
            }
        }
        return out
    }

    /// Aantal tijdstippen per kalenderdag over de laatste `days` dagen: oudste dag eerst, vandaag als laatste.
    static func perDay(_ stamps: [Date], days: Int, now: Date, calendar: Calendar = .current) -> [Int] {
        let today = calendar.startOfDay(for: now)
        var out = Array(repeating: 0, count: days)
        for s in stamps {
            let back = calendar.dateComponents([.day], from: calendar.startOfDay(for: s), to: today).day ?? -1
            if (0..<days).contains(back) { out[days - 1 - back] += 1 }
        }
        return out
    }

    // MARK: Batterij

    struct Battery: Sendable {
        let health: String, condition: String?
        let cycles: Int?, designCycles: Int
        let capacity: Int?          // % van de oorspronkelijke capaciteit
        var worn: Bool { (capacity.map { $0 < 80 } ?? false) || condition != nil || health != "Good" }
        /// "Good · 31 of ~1000 cycles · 103% of original capacity"
        var summary: String {
            [condition ?? (health == "Good" ? L("Good") : health == "Worn" ? L("Worn") : health),
             cycles.map { L("\($0) of ~\(String(designCycles)) cycles") }, capacity.map { L("\($0)% of original capacity") }]

                .compactMap { $0 }.joined(separator: " · ")
        }
    }

    /// nil op een Mac zonder batterij.
    static func battery() -> Battery? {
        let info = IOPSCopyPowerSourcesInfo().takeRetainedValue()
        var ps: [String: Any]?
        for src in IOPSCopyPowerSourcesList(info).takeRetainedValue() as [CFTypeRef] {
            if let d = IOPSGetPowerSourceDescription(info, src)?.takeUnretainedValue() as? [String: Any],
               d[kIOPSTypeKey] as? String == kIOPSInternalBatteryType { ps = d }
        }
        guard let ps else { return nil }
        var smart: [String: Any] = [:]
        let svc = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
        if svc != 0 {
            var props: Unmanaged<CFMutableDictionary>?
            if IORegistryEntryCreateCFProperties(svc, &props, kCFAllocatorDefault, 0) == KERN_SUCCESS {
                smart = props?.takeRetainedValue() as? [String: Any] ?? [:]
            }
            IOObjectRelease(svc)
        }
        return battery(ps: ps, smart: smart)
    }

    /// Capaciteit staat los in het register of (nieuwere macOS) onder "BatteryData".
    static func battery(ps: [String: Any], smart: [String: Any]) -> Battery {
        let data = smart["BatteryData"] as? [String: Any] ?? [:]
        func int(_ k: String) -> Int? { ((smart[k] as? NSNumber) ?? (data[k] as? NSNumber))?.intValue }
        let cap = int("NominalChargeCapacity").flatMap { n in int("DesignCapacity").flatMap { $0 > 0 ? Int((Double(n) / Double($0) * 100).rounded()) : nil } }
        // Geen gezondheidssleutel (Apple silicon): afleiden uit de capaciteit
        let health = ps[kIOPSBatteryHealthKey] as? String ?? (cap.map { $0 < 80 ? "Worn" : "Good" } ?? "Good")
        return Battery(health: health, condition: ps[kIOPSBatteryHealthConditionKey] as? String,
                       cycles: int("CycleCount"), designCycles: int("DesignCycleCount9C") ?? 1000, capacity: cap)
    }

    // MARK: Privacy voor het deelrapport

    /// Haalt gebruikersnaam, thuismap, computernaam, IP- en MAC-adressen weg. Vangnet: het rapport bevat ze sowieso niet.
    static func scrub(_ text: String, user: String = NSUserName(), fullName: String = NSFullUserName(),
                      hosts: [String] = hostNames()) -> String {
        var s = text.replacingOccurrences(of: #"/Users/[^/\s]+"#, with: "~", options: .regularExpression)
        let generic: Set<String> = ["mac", "macbook", "imac", "apple", "macos", "user", "admin", "home", "local"]
        let words = [user, fullName] + hosts + hosts.map { $0.components(separatedBy: ".").first ?? "" }
        for w in Set(words).sorted(by: { $0.count > $1.count }) where w.count >= 4 && !generic.contains(w.lowercased()) {
            s = s.replacingOccurrences(of: #"\b"# + NSRegularExpression.escapedPattern(for: w) + #"\b"#, with: "…", options: [.regularExpression, .caseInsensitive])
        }
        s = s.replacingOccurrences(of: #"\b(?:\d{1,3}\.){3}\d{1,3}\b"#, with: "x.x.x.x", options: .regularExpression)
        s = s.replacingOccurrences(of: #"\b(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\b"#, with: "xx:xx:xx:xx:xx:xx", options: .regularExpression)
        return s
    }

    /// Netwerknaam (gethostname, geen DNS) en de computernaam uit Instellingen.
    static func hostNames() -> [String] {
        var buf = [CChar](repeating: 0, count: 256)
        let net = gethostname(&buf, 255) == 0 ? String(cString: buf) : ""
        return [net, (SCDynamicStoreCopyComputerName(nil, nil) as String?) ?? ""]
    }

    // MARK: Zelftest

    static func selfTest() {
        precondition(testBuild(#"{"procPath" : "\/private\/tmp\/fb\/Fusebox","x":1}"#) && testBuild(#""procPath":"\/private\/var\/folders\/ab\/T\/Fusebox""#), "proefbouw")
        precondition(!testBuild(#""procPath" : "\/Applications\/Safari.app\/Contents\/MacOS\/Safari""#) && !testBuild("geen kop"), "echte app")
        let t0 = Date(timeIntervalSince1970: 1_790_000_000)
        func pts(_ n: Int, gapAt: Int? = nil, _ f: (Int) -> (Double, Double)) -> [Point] {
            (0..<n).map { i in let (c, m) = f(i); return Point(t: t0.addingTimeInterval(Double(i + (gapAt.map { i >= $0 ? 60 : 0 } ?? 0)) * 60), cpu: c, mem: m) }
        }
        // Lek: 6 uur van 2 naar 9 GB, met kleine schommelingen
        let leak = pts(360) { i in (1, 2 + 7 * Double(i) / 359 + (i % 7 == 0 ? -0.1 : 0)) }
        let l = leaks(["Safari": leak])
        precondition(l.count == 1 && l[0].app == "Safari" && abs(l[0].from - 2) < 0.1 && abs(l[0].to - 9) < 0.1 && l[0].hours == 6, "lek \(l)")
        precondition(leaks(["Flat": pts(360) { _ in (1, 3) }]).isEmpty, "vlak is geen lek")
        precondition(leaks(["Short": pts(120) { i in (1, 1 + Double(i) / 20) }]).isEmpty, "2 uur is te kort")
        // Herstart na 4 uur lekken (9 → 0,3 GB): alleen het stuk ervoor telt
        let rs = leaks(["Restart": pts(360) { i in (1, i < 240 ? 2 + 7 * Double(i) / 239 : 0.3) }])
        precondition(rs.count == 1 && rs[0].hours == 4 && abs(rs[0].to - 9) < 0.1, "herstart \(rs)")
        // Zaagtand met grote dalingen: te veel drops
        precondition(leaks(["GC": pts(360) { i in (1, (2 + 7 * Double(i) / 359) * (i % 3 == 0 ? 0.7 : 1)) }]).isEmpty, "zaagtand")

        // Wegloper: 10 kernen → 10 % van de Mac = één kern
        let r = runaways(["Hog": pts(40) { _ in (12, 1) }, "Short": pts(9) { _ in (50, 1) }, "Low": pts(60) { _ in (8, 1) }], cores: 10)
        precondition(r.count == 1 && r[0].app == "Hog" && r[0].minutes == 40, "wegloper \(r)")
        precondition(runaways(["Gap": pts(16, gapAt: 8) { _ in (50, 1) }], cores: 10).isEmpty, "gat breekt de reeks")

        // Huishouding: Spotlight 5 % van 10 kernen = 50 % van een kern, 10 minuten
        let hk = housekeeping(["Spotlight (search)": pts(10) { _ in (5, 0.1) }, "iCloud": pts(10) { _ in (1, 0.1) }, "Safari": pts(10) { _ in (50, 1) }], cores: 10)
        precondition(hk.minutes == ["Spotlight is indexing": 10] && hk.runs.count == 1 && hk.runs[0].minutes == 10, "huishouding \(hk)")
        precondition(job(proc: "mds_stores")?.doing == "Spotlight is indexing" && job(proc: "mdworker_shared") != nil && job(proc: "Safari") == nil, "procnaam")
        let ps = parsePS("  12.5 /usr/libexec/mds_stores\n 0,7 bird\nrommel\n")
        precondition(ps.count == 2 && ps[0].proc == "mds_stores" && ps[0].cpu == 12.5 && ps[1].cpu == 0.7, "ps \(ps)")

        // Crashrapporten
        let ips = #"{"app_name":"OneDrive","timestamp":"2026-11-13 03:01:41.00 +0100","bug_type":"309","name":"OneDrive"}"# + "\n{\n  \"uptime\" : 1\n"
        let c = parseHeader(ips, fileName: "OneDrive-2026-11-13-030141.ips")
        precondition(c.entry == .crash("OneDrive") && c.time == stamp("2026-11-13 03:01:41 +0100") && c.time != nil, "ips \(c)")
        precondition(parseHeader(#"{"is_simulated":1,"app_name":"x","bug_type":"309"}"#, fileName: "ExcUserFault_x.ips").entry == .none, "nagebootst")
        precondition(parseHeader(#"{"bug_type":"298","timestamp":"2026-11-09 21:43:34.00 +0100"}"#, fileName: "JetsamEvent-2026-11-09-214334.ips").entry == .jetsam, "jetsam")
        let diag = "Date/Time:        2026-11-16 09:34:48.362 +0100\nCommand:          Brave Browser\nPath:             /Applications/Brave Browser.app\nEvent:            disk writes\n"
        let dg = parseHeader(diag, fileName: "Brave Browser_2026-11-16-095042_x.diag")
        precondition(dg.entry == .resource("Brave Browser (disk writes)") && dg.time != nil, "diag \(dg)")
        precondition(parseHeader("Command: Foo\nEvent: hang\n", fileName: "Foo.hang").entry == .hang("Foo"), "hang")
        precondition(parseHeader("nonsense", fileName: "x.ips").entry == .none, "kapot")
        var cr = Crashes(); cr.crashes = ["A": 1, "B": 3]; cr.jetsam = 2
        precondition(cr.rows.map(\.label) == ["B", "A", "Out of memory"], "volgorde")
        // Kernel panic: alleen een nagemaakte kopregel (op deze Mac lag geen echt panic-rapport om mee te vergelijken)
        let pn = parseHeader(#"{"bug_type":"210","timestamp":"2026-11-20 08:15:02.00 +0100","os_version":"macOS 26.1 (25B78)","incident_id":"X"}"# + "\n{\n", fileName: "panic-full-2026-11-20-081502.0002.ips")
        precondition(pn.entry == .panic && pn.time == stamp("2026-11-20 08:15:02 +0100") && pn.time != nil, "panic \(pn)")
        precondition(parseHeader(#"{"bug_type":"210","timestamp":"2026-11-20 08:15:02.00 +0100"}"#, fileName: "Kernel-2026-11-20-081502.ips").entry == .panic
                     && parseHeader(#"{"bug_type":"10"}"#, fileName: "x.ips").entry == .panic, "panic op bug_type")
        precondition(parseHeader("panic(cpu 2 caller 0xfffffe00): geheim\n", fileName: "Kernel-2026-11-20-081502.panic") == (.panic, nil)
                     && parseHeader("geen json", fileName: "panic-full-2026-11-20.ips").entry == .panic, "panic op bestandsnaam")
        precondition(parseHeader(#"{"is_simulated":1,"bug_type":"210"}"#, fileName: "panic-full-x.ips").entry == .none, "nagebootste panic")
        precondition(parseHeader(#"{"bug_type":"115"}"#, fileName: "ResetCounter-2026-11-20.ips").entry == .none, "reset tellen we niet")
        cr.panics = 2
        precondition(!Crashes(panics: 1).isEmpty && cr.rows.first.map { $0 == ("Mac restarted by a crash", "kernel panic 2×") } == true, "panic-regel")
        // Per dag: vandaag als laatste, buiten het venster en in de toekomst telt niet
        var utc = Calendar(identifier: .gregorian); utc.timeZone = TimeZone(identifier: "UTC")!
        let noon = Date(timeIntervalSince1970: 1_789_992_000)   // 12:00 UTC
        let pd = perDay([noon, noon.addingTimeInterval(-3600), noon.addingTimeInterval(-86_400 * 2), noon.addingTimeInterval(-86_400 * 5), noon.addingTimeInterval(86_400)], days: 4, now: noon, calendar: utc)
        precondition(pd == [0, 1, 0, 2] && perDay([], days: 30, now: noon).count == 30, "per dag \(pd)")

        // Batterij
        let b = battery(ps: [:], smart: ["CycleCount": 31, "DesignCycleCount9C": 1000, "BatteryData": ["NominalChargeCapacity": 5957, "DesignCapacity": 5760]])
        precondition(b.summary == "Good · 31 of ~1000 cycles · 103% of original capacity" && !b.worn, b.summary)
        precondition(battery(ps: [:], smart: ["NominalChargeCapacity": 70, "DesignCapacity": 100]).worn, "versleten")

        // Privacy
        let p = scrub("Path /Users/alice/Library/Logs, Mac model Mac16,13 on alices-mbp (Alice Smith) 192.168.1.20 aa:bb:cc:dd:ee:ff",
                      user: "alice", fullName: "Alice Smith", hosts: ["alices-mbp.local", "Mac.home"])
        precondition(p == "Path ~/Library/Logs, Mac model Mac16,13 on … (…) x.x.x.x xx:xx:xx:xx:xx:xx", p)
        print("diagnose ok")
    }
}
