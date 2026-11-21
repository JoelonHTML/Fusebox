import os
import IOKit
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
    /// false = gemeten via `ps` (systeemhelpers van root): processortijd en geheugen wel, energie niet
    var energyKnown = true
    static func + (a: Self, b: Self) -> Self {
        .init(cpu: a.cpu + b.cpu, memMB: a.memMB + b.memMB, watt: a.watt + b.watt, energyKnown: a.energyKnown && b.energyKnown)
    }
    /// "0.4% CPU · 52 MB · 0.1 W"
    var text: String {
        let c = cpu < 0.1 ? "<0.1" : cpu < 10 ? String(format: "%.1f", cpu) : "\(Int(cpu.rounded()))"
        let m = memMB >= 1024 ? String(format: "%.1f GB", memMB / 1024) : "\(Int(memMB.rounded())) MB"
        let w = watt < 0.1 ? "<0.1" : String(format: "%.1f", watt)
        return energyKnown ? "\(c)% CPU · \(m) · \(w) W" : "\(c)% CPU · \(m)"
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
                // Eigen processen: proc_pid_rusage (met energie). Die van root (systeemhelpers) mag dat niet: dan `ps`.
                let own = pids.compactMap(usage)
                let other = own.count == pids.count ? [] : psUsage(pids.filter { usage($0) == nil })
                it.usage = (own + other).reduce(nil) { ($0 ?? StartupUsage()) + $1 }
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

    /// Via `ps` (werkt ook voor processen van root, zonder rechten): processortijd / looptijd, en het geheugen (RSS).
    static func psUsage(_ pids: [pid_t]) -> [StartupUsage] {
        guard !pids.isEmpty else { return [] }
        let out = Diag.run("/bin/ps", ["-o", "time=,etime=,rss=", "-p", pids.map(String.init).joined(separator: ",")], timeout: 3)
        let cores = Double(ProcessInfo.processInfo.activeProcessorCount)
        return out.split(separator: "\n").compactMap { l in
            let f = l.split(separator: " ", omittingEmptySubsequences: true)
            guard f.count >= 3, let cpu = seconds(String(f[0])), let alive = seconds(String(f[1])), let rss = Double(f[2]) else { return nil }
            var u = average(cpuNs: cpu * 1e9, energyNj: 0, seconds: alive, memBytes: rss * 1024, cores: cores)
            u.energyKnown = false
            return u
        }
    }

    /// "4:05.35", "1:02:03", "01-03:19:23" (dagen-uren:min:sec) → seconden.
    static func seconds(_ s: String) -> Double? {
        let dayParts = s.split(separator: "-")
        let days = dayParts.count == 2 ? Double(dayParts[0]) ?? 0 : 0
        let parts = (dayParts.last ?? "").split(separator: ":").compactMap { Double($0) }
        guard !parts.isEmpty, parts.count == (dayParts.last ?? "").split(separator: ":").count else { return nil }
        return days * 86_400 + parts.reduce(0) { $0 * 60 + $1 }
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
    var inside: [Child] = []   // grootste mappen erin (hoogstens 3), zodat je ziet wát er zo groot is
    var id: String { url.path }
    struct Child: Sendable, Equatable { let name: String, bytes: Int64 }
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
    static let titles = [".Trash": L("Trash"), "Library/Application Support/MobileSync": L("iPhone & iPad backups"), "Music/Music": L("Music library"), "Movies/TV": L("TV library"), "Pictures/Photos Library.photoslibrary": L("Photos library"), "Library/Photos": L("Photos data")]

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
    static func measure(_ url: URL, skip: Set<String>, within: Double) async -> (total: Int64, children: [DiskItem.Child])? {
        let once = OSAllocatedUnfairLock(initialState: false)
        return await withCheckedContinuation { (c: CheckedContinuation<(total: Int64, children: [DiskItem.Child])?, Never>) in
            func finish(_ v: (total: Int64, children: [DiskItem.Child])?) { if once.withLock({ let first = !$0; $0 = true; return first }) { c.resume(returning: v) } }
            Task.detached(priority: .utility) { finish(await measure(url, skip: skip)) }
            Task.detached { try? await Task.sleep(for: .seconds(within)); finish(nil) }
        }
    }

    static func size(_ url: URL, skip: Set<String>) async -> Int64 { await measure(url, skip: skip).total }

    /// Totaal plus per map direct erin (in dezelfde ronde, dus niets extra's te lezen).
    static func measure(_ url: URL, skip: Set<String>) async -> (total: Int64, children: [DiskItem.Child]) {
        let keys: [URLResourceKey] = [.totalFileAllocatedSizeKey, .fileAllocatedSizeKey]
        func one(_ u: URL) -> Int64 {
            let v = try? u.resourceValues(forKeys: Set(keys))
            return Int64(v?.totalFileAllocatedSize ?? v?.fileAllocatedSize ?? 0)
        }
        var total = one(url), per: [String: Int64] = [:]
        let base = url.standardizedFileURL.pathComponents.count
        guard let e = FileManager.default.enumerator(at: url, includingPropertiesForKeys: keys, options: [], errorHandler: { _, _ in true }) else { return (total, []) }
        var n = 0
        while let u = e.nextObject() as? URL {
            if skip.contains(u.path) { e.skipDescendants(); continue }
            let b = one(u)
            total += b
            let c = u.standardizedFileURL.pathComponents
            if c.count > base { per[c[base], default: 0] += b }
            n += 1
            if n % 2000 == 0, Task.isCancelled { break }
        }
        let kids = per.sorted { $0.value > $1.value }.prefix(3).filter { $0.value > 100_000_000 }.map { DiskItem.Child(name: $0.key, bytes: $0.value) }
        return (total, Array(kids))
    }

    // MARK: Advies: wat is het en kan het weg?

    enum Verdict: Sendable { case safe, careful, keep, unknown }

    /// Bekende mappen in gewone taal. `name` is het laatste deel van het pad (of een submap-naam).
    static func advice(_ rel: String) -> (what: String, verdict: Verdict)? {
        let name = (rel as NSString).lastPathComponent
        let table: [String: (String, Verdict)] = [
            "Library/Developer": (L("Xcode: build files, simulators and device support. Clear in Xcode or delete DerivedData; old simulators: Xcode › Settings › Components"), .careful),
            "DerivedData": (L("Xcode build files. Safe to delete; Xcode rebuilds them"), .safe),
            "CoreSimulator": (L("iPhone/iPad simulators from Xcode. Remove old ones in Xcode › Settings › Components"), .careful),
            "iOS DeviceSupport": (L("Debug files for every iPhone you connected to Xcode. Old versions can go"), .safe),
            "Archives": (L("Xcode app archives (builds you exported). Keep the ones you still need"), .careful),
            "Library/Caches": (L("Caches of your apps. Safe to delete; apps rebuild them (first start is a bit slower)"), .safe),
            "Caches": (L("Cache files. Safe to delete; they are rebuilt"), .safe),
            "Library/Logs": (L("Log files of apps and macOS. Safe to delete"), .safe),
            "Library/Application Support": (L("Data and settings of your apps. Keep it; look inside for an app you no longer use"), .keep),
            "Library/Containers": (L("Data of App Store apps. Keep it; delete an app to remove its data"), .keep),
            ".Trash": (L("Trash. Empty it to get the space back"), .safe),
            "Downloads": (L("Your downloads. Often old installers and zips that can go"), .careful),
            "Movies": (L("Your videos. Keep, or move to an external drive"), .keep),
            "Music": (L("Your music. Keep, or move to an external drive"), .keep),
            "Pictures": (L("Your photos. Keep, or move the Photos library to an external drive"), .keep),
            "Desktop": (L("Files on your desktop. Your own files"), .keep),
            "Documents": (L("Your documents. Your own files"), .keep),
            "node_modules": (L("JavaScript packages. Safe to delete; `npm install` brings them back"), .safe),
            ".npm": (L("npm download cache. Safe to delete"), .safe),
            ".gradle": (L("Gradle cache (Android/Java builds). Safe to delete; it downloads again"), .safe),
            ".android": (L("Android SDK settings and emulators. Delete old emulators in Android Studio"), .careful),
            ".m2": (L("Maven cache (Java). Safe to delete; it downloads again"), .safe),
            ".cargo": (L("Rust packages and tools. The registry cache can go"), .careful),
            ".cache": (L("Cache of command-line tools. Safe to delete"), .safe),
            ".docker": (L("Docker settings. The big data is in Docker itself: clean up there"), .careful),
            ".claude": (L("Claude Code: your sessions and history. Keep it unless you don't need old conversations"), .keep),
            ".vscode": (L("VS Code extensions. Remove unused extensions in VS Code"), .careful),
            "Library/Mobile Documents": (L("iCloud Drive files on this Mac. Remove downloads with \"Remove Download\" in Finder"), .careful),
            "Library/Mail": (L("Your Mail messages and attachments"), .keep),
            "MobileSync": (L("iPhone/iPad backups. Delete old backups in Finder › your iPhone › Manage Backups"), .careful),
            "Library/Application Support/MobileSync": (L("iPhone/iPad backups. Delete old backups in Finder › your iPhone › Manage Backups"), .careful),
        ]
        if let a = table[rel] ?? table[name] { return (a.0, a.1) }
        return nil
    }

    /// Grootste eerst, lege en ongemeten weg.
    static func top(_ items: [DiskItem], _ n: Int = 10) -> [DiskItem] {
        Array(items.filter { ($0.bytes ?? 0) > 0 }.sorted { $0.bytes! > $1.bytes! }.prefix(n))
    }

    static func selfTest() {
        let su = Startup.average(cpuNs: 36e9, energyNj: 3600e9, seconds: 3600, memBytes: 52 * 1_048_576, cores: 10)
        precondition(abs(su.cpu - 0.1) < 0.0001 && abs(su.watt - 1) < 0.0001 && su.memMB == 52 && su.text == "0.1% CPU · 52 MB · 1.0 W", "opstart-gebruik")
        precondition(StartupUsage(cpu: 0.01, memMB: 2048, watt: 0.05).text == "<0.1% CPU · 2.0 GB · <0.1 W", "opstart-tekst")
        precondition(Startup.seconds("4:05.35") == 245.35 && Startup.seconds("1:02:03") == 3723 && Startup.seconds("01-03:19:23") == 98_363 && Startup.seconds("x") == nil, "ps-tijden")
        precondition(StartupUsage(cpu: 0.5, memMB: 40, watt: 0, energyKnown: false).text == "0.5% CPU · 40 MB", "zonder energie")
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
        precondition(advice("Library/Caches")?.verdict == .safe && advice("Library/Application Support")?.verdict == .keep
                     && advice("DerivedData")?.verdict == .safe && advice("dev") == nil, "map-advies")
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
                CardTitle(icon: "power", title: L("Startup & background items"), tone: items?.contains(where: \.orphan) == true ? 1 : 0)
                Spacer()
                Button("Login Items…") { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.LoginItems-Settings.extension")!) }
                    .buttonStyle(Pill(color: theme.onRow()))
            }
            if let items {
                let login = Set(items.filter { $0.atLogin && !$0.orphan }.map(\.name)).count
                Headline(big: "\(login)", small: login == 1 ? L("app starts at login") : L("apps start at login"))
            }
            Text("Apps from other makers that start with your Mac or wait in the background. Turn off what you don't use in Login Items.")
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            if let items {
                orphans(items.filter(\.orphan))
                group(L("Apps that start at login"), items.filter { $0.atLogin && !$0.orphan })
                group(L("Background helpers"), items.filter { !$0.atLogin && !$0.orphan })
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
            Text("Left behind by deleted apps").font(theme.label(11).weight(.semibold)).foregroundStyle(theme.tone(1, icon: false)).padding(.top, 4)
            Text("These try to start every time but their app is gone. Safe to remove.")
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            ForEach(list) { i in
                HStack(spacing: 6) {
                    Image(systemName: "exclamationmark.triangle.fill").foregroundStyle(theme.tone(1))
                    Text(i.name).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                    if !i.user { Text("needs your password to remove").font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(1) }
                    Spacer(minLength: 4)
                    Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([i.plist]) }.buttonStyle(Pill(color: theme.onRow()))
                }
                .font(theme.label(11))
                .help(i.plist.path + (i.program.map { "\n" + L("Missing: \($0)") } ?? ""))
            }
        }
    }

    @ViewBuilder private func group(_ title: String, _ list: [StartupItem]) -> some View {
        Text(title).font(theme.label(11).weight(.semibold)).foregroundStyle(theme.onRow()).padding(.top, 4)
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
        Capped(items: list, id: \.id) { i in
            HStack(spacing: 6) {
                Circle().fill(i.running ? (theme.rowStyle == .blocks ? .white : theme.good) : theme.onRow(true).opacity(0.35)).frame(width: 6, height: 6)
                Text(i.name).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                Text(i.running ? "running" : i.user ? "you" : "all users").font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(1)
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
    @State private var askFDA = false
    @State private var progress: (done: Int, total: Int, now: String)?
    /// Vrij en totaal (GB), één keer bij openen gelezen
    @State private var space: (free: Double, total: Double)? = {
        let v = try? URL(fileURLWithPath: "/").resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeTotalCapacityKey])
        guard let f = v?.volumeAvailableCapacityForImportantUsage, let t = v?.volumeTotalCapacity, t > 0 else { return nil }
        return (Double(f) / 1e9, Double(t) / 1e9)
    }()

    private var low: Bool { space.map { $0.free < $0.total * 0.1 } ?? false }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                CardTitle(icon: "internaldrive", title: L("What takes my disk space?"), tone: low ? 1 : 0)
                Spacer()
                // Zonder Volledige schijftoegang tellen beschermde mappen (Foto's, Mail, Containers, delen van Caches) niet mee:
                // dan eerst vragen, anders zijn de groottes niet representatief
                Button(result == nil ? L("Find large folders") : L("Check again")) { if DiskScan.fullDiskAccess { run += 1 } else { askFDA = true } }
                    .buttonStyle(Pill(color: theme.onRow())).disabled(progress != nil)
            }
            if let space { Headline(big: L("\(Int(space.free)) GB free"), small: L("of \(Int(space.total)) GB"), tone: low ? 1 : 0) }
            if let p = progress {
                ProgressView(value: Double(p.done), total: Double(max(p.total, 1))).controlSize(.small).tint(theme.rowStyle == .blocks ? .white : theme.accent)
                Text("Checking \(p.now)…").font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1).truncationMode(.middle)
            } else if let r = result {
                let biggest = Double(r.top.first?.bytes ?? 1)
                ForEach(r.top) { i in
                    VStack(alignment: .leading, spacing: 2) {
                        HStack(spacing: 6) {
                            Text(i.title).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                            if let a = DiskScan.advice(DiskScan.rel(i.url, FileManager.default.homeDirectoryForCurrentUser)) { verdictTag(a.verdict) }
                            Spacer(minLength: 4)
                            Capsule().fill((theme.rowStyle == .blocks ? Color.white : theme.accent).opacity(0.7))
                                .frame(width: 50 * max(Double(i.bytes ?? 0) / biggest, 0.04), height: 4)
                            Text(bytes(Double(i.bytes ?? 0))).font(theme.number(11)).foregroundStyle(theme.onRow()).frame(width: 58, alignment: .trailing)
                            Button("Show in Finder") { NSWorkspace.shared.activateFileViewerSelecting([i.url]) }.buttonStyle(Pill(color: theme.onRow()))
                        }
                        .font(theme.label(11))
                        // Wat is het, en wat zit er het meest in (vage mappen worden zo duidelijk)
                        let what = DiskScan.advice(DiskScan.rel(i.url, FileManager.default.homeDirectoryForCurrentUser))?.what
                            ?? (i.inside.isEmpty ? L("Your own folder. Check what's inside before deleting anything") : nil)
                        if let what { Text(what).font(theme.label(10)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true) }
                        if !i.inside.isEmpty {
                            Text(L("Biggest inside: ") + i.inside.map { c in "\(c.name) \(bytes(Double(c.bytes)))" + (DiskScan.advice(c.name).map { " (\(label($0.verdict)))" } ?? "") }.joined(separator: " · "))
                                .font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(2)
                        }
                    }
                    .padding(.vertical, 2)
                }
                if !r.skipped.isEmpty {
                    Text(L("Not counted (macOS protects these): \(r.skipped.map(\.title).joined(separator: ", ")). Sizes are too small without Full Disk Access."))
                        .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Text("Finds the biggest folders in your home folder. Nothing is deleted.").font(theme.label(11)).foregroundStyle(theme.onRow(true))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .alert("Give Fusebox Full Disk Access?", isPresented: $askFDA) {
            Button("Open Settings") { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_AllFiles")!) }
            Button("Scan without") { run += 1 }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Without it, macOS hides your Photos library, Mail, app containers and parts of the caches, so the sizes come out too small. In Privacy & Security › Full Disk Access, turn on Fusebox (or add it with +), then press Find large folders again. Fusebox only measures sizes; it never reads or deletes your files.")
        }
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
                let m = await DiskScan.measure(url, skip: s, within: 15)
                i.bytes = m?.total; i.inside = m?.children ?? []
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


extension DiskSection {
    /// Klein label: kan weg / voorzichtig / houden.
    func verdictTag(_ v: DiskScan.Verdict) -> some View {
        let c: Color = theme.rowStyle == .blocks ? .white : v == .safe ? theme.good : v == .careful ? theme.warn : theme.secondary
        return Text(label(v)).font(theme.label(9).weight(.semibold)).foregroundStyle(c)
            .padding(.horizontal, 5).padding(.vertical, 1).overlay(Capsule().stroke(c.opacity(0.6), lineWidth: 1))
    }
    func label(_ v: DiskScan.Verdict) -> String {
        switch v { case .safe: L("can go"); case .careful: L("check first"); case .keep: L("keep"); case .unknown: "?" }
    }
}

// MARK: - SSD-gezondheid: NVMe S.M.A.R.T. van de interne SSD, zonder root (NVMeSMARTLib-plugin van IOKit).
// Hooguit eens per uur gelezen, op de achtergrond. Nooit serienummers.

enum SSDHealth {
    struct Info: Equatable {
        var used = 0, spare = 100, spareMin = 0       // % versleten (kan > 100), % reserve, drempel van de fabrikant
        var writtenTB = 0.0, hours = 0, mediaErrors = 0
        var critical: UInt8 = 0                       // NVMe "critical warning"-bits, 0 = niets
        var warn: Bool { used >= 80 || critical != 0 || spare < spareMin || mediaErrors > 0 }
        var text: String {
            let tb = writtenTB < 1 ? "\(Int((writtenTB * 1000).rounded())) GB" : writtenTB < 10 ? "\(one(writtenTB)) TB" : "\(Int(writtenTB.rounded())) TB"
            return L("Wear \(used)% · \(tb) written in total")
        }
    }

    private static let lock = NSLock()
    nonisolated(unsafe) private static var info: Info?, readAt = Date.distantPast, busy = false
    static var cached: Info? { lock.lock(); defer { lock.unlock() }; return info }

    /// Eens per uur opnieuw lezen (achtergrond). Mislukt het (andere Mac, geen plugin), dan blijft het leeg.
    static func refresh(now: Date = Date()) {
        lock.lock()
        guard !busy, now.timeIntervalSince(readAt) >= 3600 else { lock.unlock(); return }
        busy = true; readAt = now
        lock.unlock()
        DispatchQueue.global(qos: .utility).async {
            let i = read()
            lock.lock(); info = i ?? info; busy = false; lock.unlock()
        }
    }

    /// 512 bytes NVMe SMART/Health-log → Info. Getallen zijn little-endian; data units = 512 000 bytes.
    static func parse(_ b: [UInt8]) -> Info? {
        guard b.count >= 192 else { return nil }
        func le(_ o: Int, _ n: Int) -> Double { (0..<n).reversed().reduce(0.0) { $0 * 256 + Double(b[o + $1]) } }
        let clamp = { (v: Double) in Int(Swift.min(v, 1e9)) }
        return Info(used: Int(b[5]), spare: Int(b[3]), spareMin: Int(b[4]), writtenTB: le(48, 16) * 512_000 / 1e12,
                    hours: clamp(le(128, 16)), mediaErrors: clamp(le(160, 16)), critical: b[0])
    }

    /// Interne SSD (IOEmbeddedNVMeBlockDevice / IONVMeBlockDevice met "NVMe SMART Capable").
    static func read() -> Info? {
        // kIONVMeSMARTUserClientTypeID, kIONVMeSMARTInterfaceID, kIOCFPlugInInterfaceID
        let client = CFUUIDGetConstantUUIDWithBytes(nil, 0xAA, 0x0F, 0xA6, 0xF9, 0xC2, 0xD6, 0x45, 0x7F, 0xB1, 0x0B, 0x59, 0xA1, 0x32, 0x53, 0x29, 0x2F)
        let iface = CFUUIDGetConstantUUIDWithBytes(nil, 0xCC, 0xD1, 0xDB, 0x19, 0xFD, 0x9A, 0x4D, 0xAF, 0xBF, 0x95, 0x12, 0x45, 0x4B, 0x23, 0x0A, 0xB6)
        let plug = CFUUIDGetConstantUUIDWithBytes(nil, 0xC2, 0x44, 0xE8, 0x58, 0x10, 0x9C, 0x11, 0xD4, 0x91, 0xD4, 0x00, 0x50, 0xE4, 0xC6, 0x42, 0x6F)
        for cls in ["IOEmbeddedNVMeBlockDevice", "IONVMeBlockDevice"] {
            var it: io_iterator_t = 0
            guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching(cls), &it) == KERN_SUCCESS else { continue }
            defer { IOObjectRelease(it) }
            while case let svc = IOIteratorNext(it), svc != 0 {
                defer { IOObjectRelease(svc) }
                guard (IORegistryEntryCreateCFProperty(svc, "NVMe SMART Capable" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? Bool) == true else { continue }
                var plugin: UnsafeMutablePointer<UnsafeMutablePointer<IOCFPlugInInterface>?>?, score: Int32 = 0
                guard IOCreatePlugInInterfaceForService(svc, client, plug, &plugin, &score) == KERN_SUCCESS, let plugin, let p = plugin.pointee else { continue }
                defer { IODestroyPlugInInterface(plugin) }
                var raw: LPVOID?
                guard p.pointee.QueryInterface(plugin, CFUUIDGetUUIDBytes(iface), &raw) == S_OK, let raw else { continue }
                // IONVMeSMARTInterface: IUnknown (4 pointers), version+revision (8 bytes), dan SMARTReadData op offset 40
                let vtbl = raw.assumingMemoryBound(to: UnsafeRawPointer.self).pointee
                typealias ReadFn = @convention(c) (UnsafeMutableRawPointer?, UnsafeMutableRawPointer?) -> IOReturn
                typealias ReleaseFn = @convention(c) (UnsafeMutableRawPointer?) -> UInt32
                defer { _ = unsafeBitCast(vtbl.load(fromByteOffset: 24, as: UnsafeRawPointer.self), to: ReleaseFn.self)(raw) }
                var log = [UInt8](repeating: 0, count: 512)
                let r = log.withUnsafeMutableBytes { unsafeBitCast(vtbl.load(fromByteOffset: 40, as: UnsafeRawPointer.self), to: ReadFn.self)(raw, $0.baseAddress) }
                if r == kIOReturnSuccess, let i = parse(log) { return i }
            }
        }
        return nil
    }

    static func selfTest() {
        var b = [UInt8](repeating: 0, count: 512)
        b[3] = 100; b[4] = 10; b[5] = 3
        // 80 069 000 data units ≈ 41 TB; little-endian
        let units: UInt32 = 80_069_000
        for i in 0..<4 { b[48 + i] = UInt8(units >> (8 * UInt32(i)) & 0xff) }
        b[128] = 0x10; b[129] = 0x27   // 10 000 uur
        let i = parse(b)!
        precondition(i.used == 3 && i.spare == 100 && i.hours == 10_000 && i.mediaErrors == 0 && !i.warn, "smart \(i)")
        precondition(i.text == "Wear 3% · 41 TB written in total", "tekst \(i.text)")
        precondition(Info(writtenTB: 0.82).text == "Wear 0% · 820 GB written in total" && Info(writtenTB: 8.0088).text == "Wear 0% · 8.0 TB written in total", "kleine getallen")
        precondition(Info(used: 80).warn && Info(critical: 4).warn && Info(spare: 5, spareMin: 10).warn && Info(mediaErrors: 1).warn, "waarschuwen")
        precondition(parse([1, 2, 3]) == nil, "te kort")
        if let real = read() { print("ssd: \(real.text), \(real.hours) h") } else { print("ssd: geen S.M.A.R.T.") }
    }
}

/// Checkup › Startup & storage: klein kaartje met slijtage van de SSD.
struct SSDHealthCard: View {
    @Environment(\.theme) private var theme
    let index: Int
    @State private var info = SSDHealth.cached

    var body: some View {
        if let info {
            VStack(alignment: .leading, spacing: 4) {
                CardTitle(icon: "internaldrive", title: L("SSD health"), tone: info.warn ? 1 : 3)
                Headline(big: info.warn ? L("Check soon") : L("OK"), small: info.text + L(" · \(info.hours.formatted(.number.locale(Locale(identifier: "en_US")))) hours on"), tone: info.warn ? 1 : 3)
                if info.warn {
                    Text("The SSD is wearing out or reports errors. Keep a recent backup.").font(theme.label(11)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
                }
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .themedRow(theme, index: index)
        } else {
            Color.clear.frame(height: 0).task { info = await Task.detached { SSDHealth.read() }.value }
        }
    }
}

// MARK: - SSD-snelheid: alleen op verzoek. ~1 GB schrijven en daarna lezen in de tijdelijke map, buiten de cache om
// (F_NOCACHE), daarna weg. Niets draait vanzelf.

enum SSDBench {
    struct Result: Codable, Equatable { var read, write: Double; var date: Date }   // MB/s
    enum Failure: Error { case space, io, cancelled }
    static let block = 8 << 20, blocks = 128   // 8 MB × 128 = 1 GB

    /// Laatste uitslag, bewaard met datum.
    static var last: Result? {
        get { UserDefaults.standard.data(forKey: "ssdBenchLast").flatMap { try? JSONDecoder().decode(Result.self, from: $0) } }
        set { UserDefaults.standard.set(newValue.flatMap { try? JSONEncoder().encode($0) }, forKey: "ssdBenchLast") }
    }
    static func speed(_ mb: Double) -> String { mb >= 1000 ? "\(one(mb / 1000)) GB/s" : "\(Int(mb.rounded())) MB/s" }
    static func text(_ r: Result) -> String { L("Read \(speed(r.read)) · Write \(speed(r.write))") }
    /// Interne SSD's van Apple Silicon halen ±1,5–7 GB/s lezen en ±1–6 GB/s schrijven (256 GB-modellen het traagst).
    static func fast(_ r: Result) -> Bool { r.read >= 1500 && r.write >= 1000 }

    /// Synchroon, dus buiten de hoofdthread aanroepen. `step(0…1)` krijgt de voortgang; false = stoppen.
    static func run(dir: URL = FileManager.default.temporaryDirectory, step: (Double) -> Bool) throws -> Result {
        let free = (try? dir.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]))?.volumeAvailableCapacityForImportantUsage ?? 0
        guard free >= 5_000_000_000 else { throw Failure.space }
        let path = dir.appendingPathComponent("fusebox-speedtest-\(getpid()).tmp").path
        defer { unlink(path) }
        let buf = UnsafeMutableRawPointer.allocate(byteCount: block, alignment: 16384)
        defer { buf.deallocate() }
        arc4random_buf(buf, block)   // willekeurig: niets valt weg door compressie
        func pass(_ writing: Bool) throws -> Double {
            let fd = writing ? open(path, O_CREAT | O_TRUNC | O_WRONLY, 0o600) : open(path, O_RDONLY)
            guard fd >= 0 else { throw Failure.io }
            defer { close(fd) }
            _ = fcntl(fd, F_NOCACHE, 1)
            let t = DispatchTime.now().uptimeNanoseconds
            for i in 0..<blocks {
                guard (writing ? write(fd, buf, block) : read(fd, buf, block)) == block else { throw Failure.io }
                guard step((Double(i + 1) / Double(blocks) + (writing ? 0 : 1)) / 2) else { throw Failure.cancelled }
            }
            // Echt op de SSD (F_FULLFSYNC leegt ook de cache van de schijf zelf; fsync alleen als dat niet lukt)
            if writing, fcntl(fd, F_FULLFSYNC) != 0, fsync(fd) != 0 { throw Failure.io }
            return Double(block * blocks) / 1e6 / (Double(DispatchTime.now().uptimeNanoseconds - t) / 1e9)
        }
        let w = try pass(true), r = try pass(false)
        return Result(read: r, write: w, date: Date())
    }

    static func selfTest() {
        precondition(text(Result(read: 3120, write: 2840, date: .distantPast)) == "Read 3.1 GB/s · Write 2.8 GB/s", "snelheidstekst")
        precondition(speed(850) == "850 MB/s" && fast(Result(read: 3000, write: 2500, date: .distantPast)) && !fast(Result(read: 900, write: 2500, date: .distantPast)), "oordeel")
        // Te weinig ruimte: weigeren zonder iets te schrijven (een map op een volume dat niet bestaat geeft 0 vrij)
        do { _ = try run(dir: URL(fileURLWithPath: "/nonexistent-fusebox")) { _ in true }; preconditionFailure("ruimte") } catch Failure.space {} catch { preconditionFailure("ruimte \(error)") }
    }
}

/// Checkup › Startup & storage: SSD-snelheid meten op verzoek. Pagina verlaten of Stop = afbreken (.task wordt geannuleerd).
struct SSDSpeedCard: View {
    @Environment(\.theme) private var theme
    let index: Int
    @State private var result = SSDBench.last
    @State private var run = 0
    @State private var progress: Double?
    @State private var note: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                CardTitle(icon: "speedometer", title: L("SSD speed"), tone: result.map { SSDBench.fast($0) ? 3 : 1 } ?? 0)
                Spacer()
                Button(progress != nil ? L("Stop") : result == nil ? L("Test speed") : L("Test again")) { run = progress != nil ? 0 : run + 1 }
                    .buttonStyle(Pill(color: theme.onRow()))
            }
            if let p = progress {
                ProgressView(value: p).controlSize(.small).tint(theme.rowStyle == .blocks ? .white : theme.accent)
                Text(p < 0.5 ? "Writing a test file…" : "Reading it back…").font(theme.label(11)).foregroundStyle(theme.onRow(true))
            } else if let r = result {
                let fast = SSDBench.fast(r)
                Headline(big: fast ? L("As fast as expected") : L("Slower than usual"),
                         small: SSDBench.text(r) + " · " + r.date.formatted(Date.FormatStyle(date: .abbreviated, time: .shortened).locale(uiLocale)), tone: fast ? 3 : 1)
                if !fast {
                    Text("The disk may be almost full or busy. Try again when nothing else is running.").font(theme.label(11)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
                }
            } else {
                Text("Writes and reads 1 GB in a temporary file (a few seconds), then deletes it.").font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            }
            if let note { Text(note).font(theme.label(11)).foregroundStyle(theme.warn) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .task(id: run) {
            guard run > 0 else { return }
            note = nil; progress = 0
            let show = $progress
            // Echt van de hoofdthread af (een gewone async-functie draait hier anders op de hoofdthread mee)
            let job = Task.detached(priority: .userInitiated) {
                var shown = 0.0
                return try SSDBench.run { p in
                    if p - shown >= 0.02 || p == 1 { shown = p; DispatchQueue.main.async { if show.wrappedValue != nil { show.wrappedValue = p } } }
                    return !Task.isCancelled
                }
            }
            let r = await withTaskCancellationHandler { await job.result } onCancel: { job.cancel() }
            progress = nil
            switch r {
            case .success(let x): result = x; SSDBench.last = x
            case .failure(SSDBench.Failure.space): note = L("Needs at least 5 GB of free space.")
            case .failure(SSDBench.Failure.cancelled): break
            case .failure: note = L("The test couldn't finish.")
            }
        }
    }
}
