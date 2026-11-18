import os
import SwiftUI

// MARK: - Checkup: wat start mee met de Mac, en wat neemt schijfruimte in?
// Alleen lezen en tonen; nooit iets verwijderen. Niets draait tenzij Checkup open is (opstart) of op de knop gedrukt wordt (schijf).

struct StartupItem: Identifiable, Sendable, Equatable {
    let label: String, name: String
    let program: String?
    let plist: URL
    let user: Bool          // ~/Library/LaunchAgents (alleen van deze gebruiker)
    let atLogin: Bool       // LaunchAgent met RunAtLoad/KeepAlive: start bij inloggen
    var running = false
    /// Het programma bestaat niet meer (app weggegooid, de plist bleef staan)
    var orphan = false
    /// Wat het kost (alleen te meten bij processen van deze gebruiker; die van het systeem/root niet): gemiddeld sinds de start
    var usage: StartupUsage?
    var id: String { plist.path }
}

/// Gebruik van de processen van een opstartitem: CPU en vermogen gemiddeld sinds ze draaien, geheugen nu.
struct StartupUsage: Sendable, Equatable {
    var cpu = 0.0, memMB = 0.0, watt = 0.0
    static func + (a: Self, b: Self) -> Self { .init(cpu: a.cpu + b.cpu, memMB: a.memMB + b.memMB, watt: a.watt + b.watt) }
    /// "0.4% CPU · 52 MB · 0.1 W"
    var text: String {
        let c = cpu < 0.1 ? "<0.1" : cpu < 10 ? String(format: "%.1f", cpu) : "\(Int(cpu.rounded()))"
        let m = memMB >= 1024 ? String(format: "%.1f GB", memMB / 1024) : "\(Int(memMB.rounded())) MB"
        let w = watt < 0.1 ? "<0.1" : String(format: "%.1f", watt)
        return "\(c)% CPU · \(m) · \(w) W"
    }
}

enum Startup {
    /// Eén plist → item. Apple's eigen (com.apple.*) en uitgeschakelde items tellen niet mee.
    static func item(_ d: [String: Any], plist: URL, user: Bool, agent: Bool) -> StartupItem? {
        guard let label = d["Label"] as? String, !label.hasPrefix("com.apple."), d["Disabled"] as? Bool != true else { return nil }
        let program = (d["Program"] as? String) ?? (d["ProgramArguments"] as? [String])?.first
        let keepAlive = (d["KeepAlive"] as? Bool) ?? (d["KeepAlive"] is [String: Any])   // KeepAlive kan ook een voorwaarden-dict zijn
        return StartupItem(label: label, name: friendlyName(program: program, label: label), program: program, plist: plist,
                           user: user, atLogin: agent && (d["RunAtLoad"] as? Bool == true || keepAlive),
                           orphan: program.map(missing) ?? false)
    }

    /// Absoluut pad dat niet (meer) bestaat; fileExists volgt symlinks, dus een kapotte link telt ook als weg.
    /// Geen pad (alleen een naam, of BundleProgram) of een schijf die nu niet is aangesloten (/Volumes): niet beoordelen.
    static func missing(_ path: String) -> Bool {
        path.hasPrefix("/") && !path.hasPrefix("/Volumes/") && !FileManager.default.fileExists(atPath: path)
    }

    /// Naam van de app waar het bij hoort (buitenste .app in het pad), anders iets leesbaars uit het label.
    static func friendlyName(program: String?, label: String) -> String {
        if let app = program?.split(separator: "/").first(where: { $0.hasSuffix(".app") }) { return String(app.dropLast(4)) }
        var words: [String] = []
        for w in label.split(whereSeparator: { ".-_".contains($0) }).dropFirst() where words.last?.lowercased() != w.lowercased() {
            words.append(w.prefix(1).uppercased() + w.dropFirst())
        }
        return words.isEmpty ? label : words.joined(separator: " ")
    }

    /// Leest de drie launchd-mappen (plists zijn voor iedereen leesbaar; geen toestemming nodig).
    /// Login-items van SMAppService/sfltool vragen beheerdersrechten: die slaan we over.
    static func read() -> [StartupItem] {
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let running = runningPIDs()
        var out: [StartupItem] = []
        for (dir, user, agent) in [(home + "/Library/LaunchAgents", true, true), ("/Library/LaunchAgents", false, true), ("/Library/LaunchDaemons", false, false)] {
            for f in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] where f.hasSuffix(".plist") {
                let url = URL(fileURLWithPath: dir).appendingPathComponent(f)
                guard let d = NSDictionary(contentsOf: url) as? [String: Any], var it = item(d, plist: url, user: user, agent: agent) else { continue }
                let pids = it.program.map { running[$0] ?? running[URL(fileURLWithPath: $0).resolvingSymlinksInPath().path] ?? [] } ?? []
                it.running = !pids.isEmpty
                it.usage = pids.compactMap(usage).reduce(nil) { ($0 ?? StartupUsage()) + $1 }
                out.append(it)
            }
        }
        return out.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    /// Pad → pids van alle draaiende programma's (proc_pidpath werkt ook voor root-processen).
    private static func runningPIDs() -> [String: [pid_t]] {
        var n = proc_listallpids(nil, 0)
        var pids = [pid_t](repeating: 0, count: Int(max(n, 0)) + 64)
        n = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        var paths: [String: [pid_t]] = [:]
        var buf = [CChar](repeating: 0, count: 4096)
        for pid in pids.prefix(Int(max(n, 0))) where pid > 0 {
            if proc_pidpath(pid, &buf, UInt32(buf.count)) > 0 { paths[String(cString: buf), default: []].append(pid) }
        }
        return paths
    }

    private static let tick: Double = { var t = mach_timebase_info(); mach_timebase_info(&t); return Double(t.numer) / Double(t.denom) }()

    /// Eén proces: processortijd en energie gedeeld door hoe lang het al draait; geheugen zoals Activiteitenweergave.
    /// nil voor processen van andere gebruikers (root): die mag alleen macOS zelf meten.
    static func usage(_ pid: pid_t) -> StartupUsage? {
        var ri = rusage_info_v6()
        let ok = withUnsafeMutablePointer(to: &ri) { p in p.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V6, $0) } }
        guard ok == 0 else { return nil }
        let alive = Double(mach_absolute_time() &- ri.ri_proc_start_abstime) * tick / 1e9
        return average(cpuNs: Double(ri.ri_user_time + ri.ri_system_time) * tick, energyNj: Double(ri.ri_energy_nj), seconds: alive,
                       memBytes: Double(ri.ri_phys_footprint), cores: Double(ProcessInfo.processInfo.activeProcessorCount))
    }

    /// CPU in % van de hele Mac (alle kernen samen = 100%, zoals overal in Fusebox).
    static func average(cpuNs: Double, energyNj: Double, seconds: Double, memBytes: Double, cores: Double) -> StartupUsage {
        let t = max(seconds, 1)
        return StartupUsage(cpu: cpuNs / 1e9 / t / cores * 100, memMB: memBytes / 1_048_576, watt: energyNj / 1e9 / t)
    }
}

struct DiskItem: Identifiable, Sendable, Equatable {
    let url: URL, title: String
    var bytes: Int64?       // nil = nog niet gemeten
    var id: String { url.path }
}

enum DiskScan {
    /// Mappen waarvoor macOS om toestemming vraagt (Bureaublad, Documenten, Downloads, iCloud, gegevens van andere apps)
    /// of die zonder Volledige schijftoegang dicht zitten. Zonder die toegang: niet lezen, wel noemen.
    static let protected = ["Desktop", "Documents", "Downloads", ".Trash", "Library/Mobile Documents", "Library/Containers",
                            "Library/Group Containers", "Library/Application Support/MobileSync",
                            // Muziek-, TV- en Fotobibliotheek: macOS vraagt toestemming (Media & Apple Music, Foto's); lezen bleef hangen op die vraag
                            "Music/Music", "Movies/TV", "Pictures/Photos Library.photoslibrary", "Library/Photos",
                            "Library/Mail", "Library/Messages", "Library/Safari", "Library/Calendars", "Library/HomeKit"]
    /// Nooit doorlopen: cloudmappen (File Provider) kunnen bij lezen gaan downloaden.
    static let never = ["Library/CloudStorage"]
    static let titles = [".Trash": "Trash", "Library/Application Support/MobileSync": "iPhone & iPad backups", "Music/Music": "Music library", "Movies/TV": "TV library", "Pictures/Photos Library.photoslibrary": "Photos library", "Library/Photos": "Photos data"]

    /// Volledige schijftoegang? Openen van de TCC-database faalt dan stil (geen venster).
    static var fullDiskAccess: Bool {
        FileHandle(forReadingAtPath: "/Library/Application Support/com.apple.TCC/TCC.db") != nil
    }

    static func title(_ rel: String) -> String { titles[rel] ?? rel.replacingOccurrences(of: "Library/", with: "Library › ") }

    /// Wat we meten: de bovenste mappen van de thuismap, ~/Library opgesplitst, plus iOS-backups.
    /// `skipped` = beschermd en dus niet bekeken.
    static func targets(home: URL, fda: Bool) -> (scan: [DiskItem], skipped: [DiskItem]) {
        let fm = FileManager.default
        func children(_ rel: String) -> [String] {
            ((try? fm.contentsOfDirectory(atPath: home.appendingPathComponent(rel).path)) ?? []).map { rel.isEmpty ? $0 : rel + "/" + $0 }
        }
        var rels = children("").filter { $0 != "Library" } + children("Library") + ["Library/Application Support/MobileSync"]
        rels = rels.filter { !never.contains($0) && fm.fileExists(atPath: home.appendingPathComponent($0).path) }
        let items = rels.map { DiskItem(url: home.appendingPathComponent($0), title: title($0)) }
        if fda { return (items, []) }
        let blocked = Set(protected)
        return (items.filter { !blocked.contains(rel($0.url, home)) }, items.filter { blocked.contains(rel($0.url, home)) })
    }

    static func rel(_ url: URL, _ home: URL) -> String { String(url.path.dropFirst(home.path.count + 1)) }

    /// Grootte op schijf; mappen in `skip` (eigen regel of beschermd) worden niet doorlopen. Stopt bij annuleren.
    /// async zonder actor: loopt buiten de hoofdthread.
    /// Zelfde, maar hoogstens `within` seconden: macOS kan het openen van een beschermde map laten wachten op een
    /// toestemmingsvraag. Dan nil ("niet bekeken") en door met de volgende; die ene draad blijft wachten tot macOS antwoordt.
    static func size(_ url: URL, skip: Set<String>, within: Double) async -> Int64? {
        let once = OSAllocatedUnfairLock(initialState: false)
        return await withCheckedContinuation { (c: CheckedContinuation<Int64?, Never>) in
            func finish(_ v: Int64?) { if once.withLock({ let first = !$0; $0 = true; return first }) { c.resume(returning: v) } }
            Task.detached(priority: .utility) { finish(await size(url, skip: skip)) }
            Task.detached { try? await Task.sleep(for: .seconds(within)); finish(nil) }
        }
    }

    static func size(_ url: URL, skip: Set<String>) async -> Int64 {
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey]
        func one(_ u: URL) -> Int64 {
            let v = try? u.resourceValues(forKeys: Set(keys))
            return Int64(v?.totalFileAllocatedSize ?? v?.fileAllocatedSize ?? 0)
        }
        var total = one(url)
        guard let e = FileManager.default.enumerator(at: url, includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in true }) else { return total }
        var n = 0
        while let u = e.nextObject() as? URL {
            if skip.contains(u.path) { e.skipDescendants(); continue }
            total += one(u)
            n += 1
            if n % 2000 == 0, Task.isCancelled { break }
        }
        return total
    }

    /// Grootste eerst, lege en ongemeten weg.
    static func top(_ items: [DiskItem], _ n: Int = 10) -> [DiskItem] {
        Array(items.filter { ($0.bytes ?? 0) > 0 }.sorted { $0.bytes! > $1.bytes! }.prefix(n))
    }

    static func selfTest() {
        let su = Startup.average(cpuNs: 36e9, energyNj: 3600e9, seconds: 3600, memBytes: 52 * 1_048_576, cores: 10)
        precondition(abs(su.cpu - 0.1) < 0.0001 && abs(su.watt - 1) < 0.0001 && su.memMB == 52 && su.text == "0.1% CPU · 52 MB · 1.0 W", "opstart-gebruik")
        precondition(StartupUsage(cpu: 0.01, memMB: 2048, watt: 0.05).text == "<0.1% CPU · 2.0 GB · <0.1 W", "opstart-tekst")
        let d: [String: Any] = ["Label": "com.vendor.thing_helper", "ProgramArguments": ["/Applications/Thing.app/Contents/Library/Helper.app/Contents/MacOS/h", "-x"], "RunAtLoad": true]
        let p = URL(fileURLWithPath: "/tmp/x.plist")
        let i = Startup.item(d, plist: p, user: true, agent: true)
        precondition(i?.name == "Thing" && i?.atLogin == true && i?.program?.hasSuffix("/h") == true, "opstart-plist \(String(describing: i))")
        precondition(Startup.item(["Label": "x.y", "KeepAlive": ["SuccessfulExit": false]], plist: p, user: false, agent: true)?.atLogin == true, "KeepAlive-dict")
        precondition(Startup.item(["Label": "x.y", "RunAtLoad": true], plist: p, user: false, agent: false)?.atLogin == false, "daemon = achtergrond")
        precondition(Startup.item(["Label": "com.apple.foo"], plist: p, user: true, agent: true) == nil
                     && Startup.item(["Label": "x.y", "Disabled": true], plist: p, user: true, agent: true) == nil
                     && Startup.item(["Program": "/bin/x"], plist: p, user: true, agent: true) == nil, "apple/uit/geen label")
        // Achtergelaten: programma bestaat niet meer; wel bestaand, zonder pad of op een losse schijf: niet
        let gone: [String: Any] = ["Label": "com.vendor.gone", "Program": "/Applications/Weg-\(UUID().uuidString).app/Contents/MacOS/helper"]
        precondition(Startup.item(gone, plist: p, user: true, agent: true)?.orphan == true
                     && Startup.item(["Label": "x.y", "ProgramArguments": ["/bin/sh", "-c", "true"]], plist: p, user: true, agent: true)?.orphan == false
                     && Startup.item(["Label": "x.y", "ProgramArguments": ["node"]], plist: p, user: true, agent: true)?.orphan == false
                     && Startup.item(["Label": "x.y"], plist: p, user: true, agent: true)?.orphan == false
                     && !Startup.missing("/Volumes/Extern/x"), "achtergelaten")
        precondition(Startup.friendlyName(program: "/usr/local/bin/x", label: "com.teamviewer.teamviewer_service") == "Teamviewer Service"
                     && Startup.friendlyName(program: nil, label: "com.google.keystone.agent") == "Google Keystone Agent"
                     && Startup.friendlyName(program: nil, label: "solo") == "solo", "vriendelijke naam")
        let u = URL(fileURLWithPath: "/a")
        let t = top([DiskItem(url: u, title: "a", bytes: 5), DiskItem(url: u, title: "b", bytes: nil), DiskItem(url: u, title: "c", bytes: 9_000_000_000),
                     DiskItem(url: u, title: "d", bytes: 0), DiskItem(url: u, title: "e", bytes: 2_500_000)], 2)
        precondition(t.map(\.title) == ["c", "e"] && bytes(Double(t[0].bytes!)) == "9.0 GB" && bytes(Double(t[1].bytes!)) == "2.5 MB", "schijf-top \(t.map(\.title))")
        let home = URL(fileURLWithPath: "/Users/x")
        precondition(rel(home.appendingPathComponent("Library/Caches"), home) == "Library/Caches" && title("Library/Caches") == "Library › Caches"
                     && title(".Trash") == "Trash", "schijf-namen")
    }
}

// MARK: - Weergave (Checkup-pagina)

/// Opstart- en achtergronditems: pas gelezen als Checkup open gaat, daarna uit de cache.
struct StartupSection: View {
    @Environment(\.theme) private var theme
    let index: Int
    @MainActor static var cache: [StartupItem]?
    @State private var items: [StartupItem]? = MainActor.assumeIsolated { StartupSection.cache }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text("Startup & background items").font(theme.label(12)).foregroundStyle(theme.onRow())
                Spacer()
                Button("Login Items…") { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension")!) }
                    .buttonStyle(Pill(color: theme.onRow()))
            }
            Text("Apps from other makers that start with your Mac or wait in the background. Turn off what you don't use in Login Items.")
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            if let items {
                orphans(items.filter(\.orphan))
                group("Apps that start at login", items.filter { $0.atLogin && !$0.orphan })
                group("Background helpers", items.filter { !$0.atLogin && !$0.orphan })
            } else {
                ProgressView().controlSize(.small)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .task {
            guard items == nil else { return }
            let r = await Task.detached(priority: .utility) { Startup.read() }.value
            Self.cache = r
            items = r
        }
    }

    /// Achtergelaten door weggegooide apps: opvallend (warn), alleen tonen als er iets is. Wij verwijderen niets.
    @ViewBuilder private func orphans(_ list: [StartupItem]) -> some View {
        if !list.isEmpty {
            Text("Left behind by deleted apps").font(theme.label(11)).foregroundStyle(theme.warn).padding(.top, 4)
            Text("These try to start every time but their app is gone. Safe to remove.")
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            ForEach(list) { i in
                HStack(spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(theme.warn)
                    Text(i.name).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                    if !i.user { Text("needs your password to remove").foregroundStyle(theme.onRow(true)).lineLimit(1) }
                    Spacer(minLength: 4)
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([i.plist]) }.buttonStyle(Pill(color: theme.onRow()))
                }
                .font(theme.label(11))
                .help(i.plist.path + (i.program.map { "\nMissing: " + $0 } ?? ""))
            }
        }
    }

    @ViewBuilder private func group(_ title: String, _ list: [StartupItem]) -> some View {
        Text(title).font(theme.label(11)).foregroundStyle(theme.onRow()).padding(.top, 4)
        // Zelfde app meerdere keren (bijv. 3× OneDrive): één regel; draait als er één draait
        let list: [StartupItem] = Dictionary(grouping: list, by: \.name).values
            .map { (g: [StartupItem]) -> StartupItem in
                var f = g.first(where: \.user) ?? g[0]; f.running = g.contains(where: \.running)
                f.usage = g.compactMap(\.usage).reduce(nil) { ($0 ?? StartupUsage()) + $1 }; return f
            }
            // Wat het meest kost bovenaan (energie), daarna op naam
            .sorted { (a: StartupItem, b: StartupItem) in
                let x = a.usage?.watt ?? -1, y = b.usage?.watt ?? -1
                return x != y ? x > y : a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending
            }
        if list.isEmpty { Text("None").font(theme.label(11)).foregroundStyle(theme.onRow(true)) }
        ForEach(list) { i in
            HStack(spacing: 6) {
                Circle().fill(i.running ? (theme.rowStyle == .blocks ? .white : theme.good) : theme.onRow(true).opacity(0.35)).frame(width: 6, height: 6)
                Text(i.name).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                Text(i.running ? "running" : i.user ? "you" : "all users").foregroundStyle(theme.onRow(true)).lineLimit(1)
                Spacer(minLength: 4)
                if let u = i.usage {
                    Text(u.text).font(theme.number(11)).foregroundStyle(u.watt >= 1 || u.cpu >= 5 ? theme.warn : theme.onRow(true)).lineLimit(1)
                        .help("Average since it started (memory: now)")
                }
                if i.user { Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([i.plist]) }.buttonStyle(Pill(color: theme.onRow())) }
            }
            .font(theme.label(11))
            .help(i.label + (i.program.map { "\n" + $0 } ?? ""))
        }
    }
}

/// Grootste mappen: alleen op verzoek; stopt vanzelf als je de pagina verlaat (.task wordt dan geannuleerd).
struct DiskSection: View {
    @Environment(\.theme) private var theme
    let index: Int
    @MainActor private static var last: (top: [DiskItem], skipped: [DiskItem])?
    @State private var result = MainActor.assumeIsolated { DiskSection.last }
    @State private var run = 0
    @State private var progress: (done: Int, total: Int, now: String)?

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                Text("What takes my disk space?").font(theme.label(12).weight(.semibold)).foregroundStyle(theme.onRow())
                Spacer()
                Button(result == nil ? "Find large folders" : "Check again") { run += 1 }
                    .buttonStyle(Pill(color: theme.onRow())).disabled(progress != nil)
            }
            if let p = progress {
                ProgressView(value: Double(p.done), total: Double(max(p.total, 1))).controlSize(.small).tint(theme.rowStyle == .blocks ? .white : theme.accent)
                Text("Checking \(p.now)…").font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1).truncationMode(.middle)
            } else if let r = result {
                let biggest = Double(r.top.first?.bytes ?? 1)
                ForEach(r.top) { i in
                    HStack(spacing: 6) {
                        Text(i.title).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                        Spacer(minLength: 4)
                        Capsule().fill((theme.rowStyle == .blocks ? Color.white : theme.accent).opacity(0.7))
                            .frame(width: 50 * max(Double(i.bytes ?? 0) / biggest, 0.04), height: 4)
                        Text(bytes(Double(i.bytes ?? 0))).font(theme.number(11)).foregroundStyle(theme.onRow()).frame(width: 58, alignment: .trailing)
                        Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([i.url]) }.buttonStyle(Pill(color: theme.onRow()))
                    }
                    .font(theme.label(11))
                }
                if !r.skipped.isEmpty {
                    Text("Not checked (macOS protects these): " + r.skipped.map(\.title).joined(separator: ", ") + ". Give Fusebox Full Disk Access to include them.")
                        .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Text("Finds the biggest folders in your home folder. Nothing is deleted.").font(theme.label(11)).foregroundStyle(theme.onRow(true))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .task(id: run) {
            guard run > 0 else { return }
            let home = FileManager.default.homeDirectoryForCurrentUser
            let (todo, skipped) = await Task.detached(priority: .utility) { DiskScan.targets(home: home, fda: DiskScan.fullDiskAccess) }.value
            // Elke regel en elke beschermde map overslaan bij het doorlopen: niets dubbel tellen, geen toestemmingsvragen.
            let fda = DiskScan.fullDiskAccess
            // … ook beschermde mappen díep in een regel (Muziek/Music, Foto's-bibliotheek): zonder toegang nooit openen
            let skip = Set((todo + skipped).map(\.url.path) + (fda ? [] : DiskScan.protected.map { home.appendingPathComponent($0).path }))
            var done: [DiskItem] = [], slow: [DiskItem] = []
            for (n, item) in todo.enumerated() {
                progress = (n, todo.count, item.title)
                var i = item
                let url = i.url, s = skip.subtracting([i.url.path])
                // Echt van de hoofdthread af: een async-functie zonder eigen actor draait anders op de hoofdthread mee (venster bevroor)
                i.bytes = await DiskScan.size(url, skip: s, within: 15)
                if Task.isCancelled { progress = nil; return }   // pagina verlaten: half resultaat niet tonen
                if i.bytes == nil { slow.append(i) } else { done.append(i) }
            }
            let r = (DiskScan.top(done), skipped + slow)
            Self.last = r
            result = r
            progress = nil
        }
    }
}
