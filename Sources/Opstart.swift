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
    var id: String { plist.path }
}

enum Startup {
    /// Eén plist → item. Apple's eigen (com.apple.*) en uitgeschakelde items tellen niet mee.
    static func item(_ d: [String: Any], plist: URL, user: Bool, agent: Bool) -> StartupItem? {
        guard let label = d["Label"] as? String, !label.hasPrefix("com.apple."), d["Disabled"] as? Bool != true else { return nil }
        let program = (d["Program"] as? String) ?? (d["ProgramArguments"] as? [String])?.first
        let keepAlive = (d["KeepAlive"] as? Bool) ?? (d["KeepAlive"] is [String: Any])   // KeepAlive kan ook een voorwaarden-dict zijn
        return StartupItem(label: label, name: friendlyName(program: program, label: label), program: program, plist: plist,
                           user: user, atLogin: agent && (d["RunAtLoad"] as? Bool == true || keepAlive))
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
        let running = runningPaths()
        var out: [StartupItem] = []
        for (dir, user, agent) in [(home + "/Library/LaunchAgents", true, true), ("/Library/LaunchAgents", false, true), ("/Library/LaunchDaemons", false, false)] {
            for f in (try? FileManager.default.contentsOfDirectory(atPath: dir)) ?? [] where f.hasSuffix(".plist") {
                let url = URL(fileURLWithPath: dir).appendingPathComponent(f)
                guard let d = NSDictionary(contentsOf: url) as? [String: Any], var it = item(d, plist: url, user: user, agent: agent) else { continue }
                it.running = it.program.map { running.contains($0) || running.contains(URL(fileURLWithPath: $0).resolvingSymlinksInPath().path) } ?? false
                out.append(it)
            }
        }
        return out.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    /// Paden van alle draaiende programma's (proc_pidpath werkt ook voor root-processen).
    private static func runningPaths() -> Set<String> {
        var n = proc_listallpids(nil, 0)
        var pids = [pid_t](repeating: 0, count: Int(max(n, 0)) + 64)
        n = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        var paths = Set<String>()
        var buf = [CChar](repeating: 0, count: 4096)
        for pid in pids.prefix(Int(max(n, 0))) where pid > 0 {
            if proc_pidpath(pid, &buf, UInt32(buf.count)) > 0 { paths.insert(String(cString: buf)) }
        }
        return paths
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
                            "Library/Group Containers", "Library/Application Support/MobileSync"]
    /// Nooit doorlopen: cloudmappen (File Provider) kunnen bij lezen gaan downloaden.
    static let never = ["Library/CloudStorage"]
    static let titles = [".Trash": "Trash", "Library/Application Support/MobileSync": "iPhone & iPad backups"]

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
        let d: [String: Any] = ["Label": "com.vendor.thing_helper", "ProgramArguments": ["/Applications/Thing.app/Contents/Library/Helper.app/Contents/MacOS/h", "-x"], "RunAtLoad": true]
        let p = URL(fileURLWithPath: "/tmp/x.plist")
        let i = Startup.item(d, plist: p, user: true, agent: true)
        precondition(i?.name == "Thing" && i?.atLogin == true && i?.program?.hasSuffix("/h") == true, "opstart-plist \(String(describing: i))")
        precondition(Startup.item(["Label": "x.y", "KeepAlive": ["SuccessfulExit": false]], plist: p, user: false, agent: true)?.atLogin == true, "KeepAlive-dict")
        precondition(Startup.item(["Label": "x.y", "RunAtLoad": true], plist: p, user: false, agent: false)?.atLogin == false, "daemon = achtergrond")
        precondition(Startup.item(["Label": "com.apple.foo"], plist: p, user: true, agent: true) == nil
                     && Startup.item(["Label": "x.y", "Disabled": true], plist: p, user: true, agent: true) == nil
                     && Startup.item(["Program": "/bin/x"], plist: p, user: true, agent: true) == nil, "apple/uit/geen label")
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
    @MainActor private static var cache: [StartupItem]?
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
                group("Apps that start at login", items.filter(\.atLogin))
                group("Background helpers", items.filter { !$0.atLogin })
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

    @ViewBuilder private func group(_ title: String, _ list: [StartupItem]) -> some View {
        Text(title).font(theme.label(11)).foregroundStyle(theme.onRow()).padding(.top, 4)
        // Zelfde app meerdere keren (bijv. 3× OneDrive): één regel; draait als er één draait
        let list: [StartupItem] = Dictionary(grouping: list, by: \.name).values
            .map { (g: [StartupItem]) -> StartupItem in var f = g.first(where: \.user) ?? g[0]; f.running = g.contains(where: \.running); return f }
            .sorted { (a: StartupItem, b: StartupItem) in a.name.localizedCaseInsensitiveCompare(b.name) == .orderedAscending }
        if list.isEmpty { Text("None").font(theme.label(11)).foregroundStyle(theme.onRow(true)) }
        ForEach(list) { i in
            HStack(spacing: 6) {
                Circle().fill(i.running ? (theme.rowStyle == .blocks ? .white : theme.good) : theme.onRow(true).opacity(0.35)).frame(width: 6, height: 6)
                Text(i.name).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                Text(i.running ? "running" : i.user ? "you" : "all users").foregroundStyle(theme.onRow(true)).lineLimit(1)
                Spacer(minLength: 4)
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
                Text("What takes my disk space?").font(theme.label(12)).foregroundStyle(theme.onRow())
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
                    Text("Not checked (no access): " + r.skipped.map(\.title).joined(separator: ", ") + ". Give Fusebox Full Disk Access to include these.")
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
            let skip = Set((todo + skipped).map(\.url.path))
            var done: [DiskItem] = []
            for (n, item) in todo.enumerated() {
                progress = (n, todo.count, item.title)
                var i = item
                i.bytes = await DiskScan.size(i.url, skip: skip.subtracting([i.url.path]))
                if Task.isCancelled { progress = nil; return }   // pagina verlaten: half resultaat niet tonen
                done.append(i)
            }
            let r = (DiskScan.top(done), skipped)
            Self.last = r
            result = r
            progress = nil
        }
    }
}
