import SwiftUI
import Security
import CoreServices

// MARK: - App-pagina: alles over één programma (nu, terugkijken, over de app)
//
// Zelfde venster als de lijst (zoals het Live-detail): terug met de pijl of Esc.
// Nu-cijfers komen van de Programs-monitor (die meet al zolang deze tab open is); geschiedenis en
// "over" worden pas bij openen gelezen, op de achtergrond, en bewaard.

/// Wat de pagina toont: een groep uit de lijst (id = bundelpad of procesnaam) of een geïnstalleerde app (id = pad).
struct AppRef: Hashable { let id: String, name: String }

// MARK: - Geïnstalleerde apps (zoeken)

enum Installed {
    struct Item: Identifiable, Sendable { let path: String, name: String; var id: String { path } }

    /// Eén keer per start ingelezen (~200 apps, enkele tientallen ms), op de achtergrond bij de eerste zoekopdracht.
    /// ponytail: apps die je daarna installeert verschijnen pas na herstart; opnieuw scannen bij elke zoekopdracht als dat stoort.
    static let all: [Item] = scan(["/Applications", NSHomeDirectory() + "/Applications", "/System/Applications"])

    /// .app-bundels in de mappen, plus één niveau submappen (Utilities, "Adobe Photoshop 2026", …).
    static func scan(_ roots: [String]) -> [Item] {
        let fm = FileManager.default
        var out: [Item] = [], seen = Set<String>()
        func walk(_ dir: String, depth: Int) {
            for n in (try? fm.contentsOfDirectory(atPath: dir)) ?? [] where !n.hasPrefix(".") {
                let p = dir + "/" + n
                if n.hasSuffix(".app") {
                    if seen.insert(p).inserted { out.append(Item(path: p, name: fm.displayName(atPath: p).replacingOccurrences(of: ".app", with: ""))) }
                } else if depth < 1 {
                    var isDir: ObjCBool = false
                    if fm.fileExists(atPath: p, isDirectory: &isDir), isDir.boolValue { walk(p, depth: depth + 1) }
                }
            }
        }
        roots.forEach { walk($0, depth: 0) }
        return out.sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
    }

    /// Zoekresultaten: naam bevat de zoektekst, niet al open (op naam: Safari draait vanuit een ander pad dan /Applications),
    /// begint-met eerst. Hoogstens `limit`.
    static func matches(_ q: String, in items: [Item], running: Set<String>, limit: Int = 30) -> [Item] {
        let q = q.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return [] }
        let hits = items.filter { $0.name.localizedCaseInsensitiveContains(q) && !running.contains($0.name) }
        let starts = hits.filter { $0.name.lowercased().hasPrefix(q.lowercased()) }
        return Array((starts + hits.filter { !$0.name.lowercased().hasPrefix(q.lowercased()) }).prefix(limit))
    }

    static func selfTest() {
        let items = [Item(path: "/A/Zap Notes.app", name: "Zap Notes"), Item(path: "/A/Notely.app", name: "Notely"),
                     Item(path: "/A/Utilities/Note Mover.app", name: "Note Mover"), Item(path: "/A/Paint.app", name: "Paint")]
        let r = matches(" note ", in: items, running: ["Notely"])
        precondition(r.map(\.name) == ["Note Mover", "Zap Notes"], "zoeken \(r.map(\.name))")
        precondition(matches("", in: items, running: []).isEmpty && matches("x", in: items, running: [], limit: 1).isEmpty, "leeg")
        print("geïnstalleerd zoeken: ok")
    }
}

// MARK: - Over de app (bundel, ondertekening, Spotlight, opstarten)

struct AppAbout: Sendable {
    var bundleID: String?, version: String?, arch: String?, signedBy: String?
    var sandboxed: Bool?, size: Int64?, added: Date?, lastUsed: Date?
    var launchAgent = false

    /// Langzaam (map doorlopen voor de grootte, handtekening controleren): alleen buiten de hoofdthread.
    static func load(_ path: String) -> AppAbout {
        var a = AppAbout()
        let url = URL(fileURLWithPath: path)
        if let b = Bundle(path: path), path.hasSuffix(".app") {
            a.bundleID = b.bundleIdentifier
            let v = b.infoDictionary?["CFBundleShortVersionString"] as? String, build = b.infoDictionary?["CFBundleVersion"] as? String
            a.version = [v, build.flatMap { $0 == v ? nil : "(\($0))" }].compactMap { $0 }.joined(separator: " ").ifEmpty("–")
            let arch = Set((b.executableArchitectures ?? []).map(\.intValue))
            let arm = arch.contains(NSBundleExecutableArchitectureARM64), intel = arch.contains(NSBundleExecutableArchitectureX86_64)
            a.arch = arm && intel ? L("Universal (Apple silicon + Intel)") : arm ? "Apple silicon" : intel ? L("Intel (runs via Rosetta)") : nil
        }
        // Handtekening: wie heeft het gemaakt, en zit het in de sandbox
        var code: SecStaticCode?, info: CFDictionary?
        if SecStaticCodeCreateWithPath(url as CFURL, [], &code) == errSecSuccess, let code,
           SecCodeCopySigningInformation(code, SecCSFlags(rawValue: kSecCSSigningInformation | kSecCSRequirementInformation), &info) == errSecSuccess,
           let d = info as? [String: Any] {
            let leaf = (d[kSecCodeInfoCertificates as String] as? [SecCertificate])?.first.flatMap { SecCertificateCopySubjectSummary($0) as String? }
            a.signedBy = leaf.map(signer) ?? L("Not signed by a developer")
            a.sandboxed = (d[kSecCodeInfoEntitlementsDict as String] as? [String: Any])?["com.apple.security.app-sandbox"] as? Bool ?? false
        }
        if let item = MDItemCreateWithURL(nil, url as CFURL) {
            a.added = MDItemCopyAttribute(item, kMDItemDateAdded) as? Date
            a.lastUsed = MDItemCopyAttribute(item, kMDItemLastUsedDate) as? Date
        }
        a.launchAgent = launchAgent(bundleID: a.bundleID, path: path)
        a.size = size(url)
        return a
    }

    /// "Developer ID Application: Firma B.V. (ABCDE12345)" → "Firma B.V. (ABCDE12345)"; Apple's eigen certificaten → "Apple".
    static func signer(_ summary: String) -> String {
        if summary.hasSuffix("Software Signing") { return "Apple" }
        if summary == "Apple Mac OS Application Signing" { return "Mac App Store" }
        for p in ["Developer ID Application: ", "Apple Development: ", "Apple Distribution: ", "Mac Developer: "] where summary.hasPrefix(p) {
            return String(summary.dropFirst(p.count))
        }
        return summary
    }

    /// Een LaunchAgent/-Daemon die naar deze app verwijst = start bij inloggen. Inlog-onderdelen uit Systeeminstellingen
    /// zijn niet leesbaar voor andere apps; dan weten we het niet.
    private static func launchAgent(bundleID: String?, path: String) -> Bool {
        let fm = FileManager.default
        for dir in [NSHomeDirectory() + "/Library/LaunchAgents", "/Library/LaunchAgents", "/Library/LaunchDaemons"] {
            for n in (try? fm.contentsOfDirectory(atPath: dir)) ?? [] where n.hasSuffix(".plist") {
                guard let d = NSDictionary(contentsOfFile: dir + "/" + n) else { continue }
                let text = "\(d)"
                if text.contains(path) || bundleID.map({ !$0.isEmpty && text.contains($0) }) == true { return true }
            }
        }
        return false
    }

    private static func size(_ url: URL) -> Int64? {
        let own = try? url.resourceValues(forKeys: [.isDirectoryKey, .totalFileAllocatedSizeKey])
        if own?.isDirectory == false { return own?.totalFileAllocatedSize.map(Int64.init) }   // los programma, geen bundel
        guard let e = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.totalFileAllocatedSizeKey]) else { return nil }
        var total: Int64 = 0
        for case let u as URL in e { total += Int64((try? u.resourceValues(forKeys: [.totalFileAllocatedSizeKey]).totalFileAllocatedSize) ?? 0) }
        return total
    }
}

// MARK: - De pagina

struct AppPage: View {
    @ObservedObject var monitor: AppMonitor
    let ref: AppRef
    let close: () -> Void
    @EnvironmentObject private var meter: Meter
    @Environment(\.theme) private var theme
    @AppStorage("netPerApp") private var netPerApp = false
    @AppStorage("appPeriod") private var days = 1
    @State private var history: Analysis.AppHistory?
    @State private var about: AppAbout?
    @State private var cpuLine: [Double] = []
    @State private var memLine: [Double] = []
    @State private var confirm: Bool?        // false = stoppen, true = geforceerd stoppen
    @State private var failed = false
    @MainActor private static var aboutCache: [String: AppAbout] = [:]

    /// Draait hij? Op id, of op naam (een app uit /Applications draait soms vanuit een ander pad, zoals Safari).
    private var app: AppUsage? {
        monitor.apps.first { $0.id == ref.id } ?? monitor.apps.first { $0.name == ref.name && $0.kind == .app }
    }
    private var path: String? { [app?.id, ref.id].compactMap { $0 }.first { $0.hasPrefix("/") } }
    private var logName: String { ref.name.replacingOccurrences(of: ",", with: " ") }   // zoals in de apps-log

    var body: some View {
        let app = app
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                header(app)
                if let app { now(app) }
                historySection
                aboutSection
            }
            .padding(.horizontal, 14).padding(.bottom, 14)
        }
        .clipped()
        .background { Button("", action: close).keyboardShortcut(.cancelAction).hidden() }   // Esc = terug
        .onReceive(monitor.$apps) { _ in   // elke meting (2 s, alleen zolang Programs open is) een punt erbij
            guard let a = self.app else { return }
            cpuLine = Array((cpuLine + [a.cpu]).suffix(300)); memLine = Array((memLine + [a.memGB]).suffix(300))
        }
        .task(id: days) {
            history = nil
            let name = logName, dir = meter.logDir, n = days
            history = await Task.detached(priority: .utility) { Analysis.appHistory(name, days: n, dir: dir) }.value
        }
        .task {
            guard let p = path else { return }
            if let c = Self.aboutCache[p] { about = c; return }
            let a = await Task.detached(priority: .utility) { AppAbout.load(p) }.value
            Self.aboutCache[p] = a; about = a
        }
        .alert(confirm == true ? "Force quit \(ref.name)?" : "Quit \(ref.name)?", isPresented: Binding(get: { confirm != nil }, set: { if !$0 { confirm = nil } }), presenting: confirm) { force in
            Button(force ? "Force Quit" : "Quit", role: .destructive) {
                if let app, !(force ? monitor.forceStop(app) : monitor.stop(app)) { failed = true }
            }
            Button("Cancel", role: .cancel) {}
        } message: { _ in Text("Unsaved work in \(ref.name) may be lost.") }
        .alert("\(ref.name) couldn't be stopped", isPresented: $failed) {
            Button("OK", role: .cancel) {}
        } message: { Text("macOS doesn't allow it, or the app isn't responding.") }
    }

    // MARK: Kop

    private func header(_ app: AppUsage?) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 10) {
                Button(action: close) {
                    Label("Back", systemImage: "chevron.left").labelStyle(.iconOnly).font(.system(size: 13, weight: .semibold))
                        .frame(width: 26, height: 26).contentShape(.rect)
                }
                .buttonStyle(.borderless).help("Back to Processes (Esc)")
                Group {
                    if let i = app?.icon ?? path.map({ NSWorkspace.shared.icon(forFile: $0) }) { Image(nsImage: i).resizable().interpolation(.high) } else { Color.clear }
                }
                .frame(width: 36, height: 36)
                VStack(alignment: .leading, spacing: 2) {
                    Text(ref.name).font(theme.label(15).weight(.semibold)).foregroundStyle(theme.text).lineLimit(1)
                    Text(status(app)).font(theme.label(11)).foregroundStyle(theme.secondary).lineLimit(1)
                }
                Spacer(minLength: 0)
            }
            HStack(spacing: 12) {
                if let p = path, p.hasSuffix(".app") {
                    Button { NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: p), configuration: .init()) } label: {
                        Label(app == nil ? "Open" : "Bring to front", systemImage: "arrow.up.forward.app")
                    }
                }
                if let app, app.canStop {
                    Button { confirm = false } label: { Label("Quit", systemImage: "xmark.circle") }
                    Button { confirm = true } label: { Label("Force quit", systemImage: "bolt.horizontal.circle") }
                        .foregroundStyle(theme.danger)
                }
                if let app { EfficiencyButton(app: app) }
                if let p = path {
                    Button { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: p)]) } label: { Label("Show in Finder", systemImage: "folder") }
                }
            }
            .buttonStyle(.plain).font(theme.label(11)).foregroundStyle(theme.accent)
            .padding(.leading, 36)
        }
    }

    private func status(_ app: AppUsage?) -> String {
        let v = about?.version.map { L("Version \($0) · ") } ?? ""
        guard let app else { return v + L("Not running") }
        return v + (app.started.map { L("Running for \(duration(max(Int(Date().timeIntervalSince($0) / 60), 1)))") } ?? L("Running"))
    }

    // MARK: Nu

    @ViewBuilder private func now(_ a: AppUsage) -> some View {
        section("Now")
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 8)], spacing: 8) {
            stat("CPU", pct(a.cpu))
            stat("Memory", mem(a.memGB))
            stat("Energy", a.energyW >= 0.05 ? "\(one(a.energyW)) W" : "–")
            stat("GPU", a.gpu >= 0.5 ? pct(min(a.gpu, 100)) : "–")
        }
        HStack(spacing: 8) {
            spark("CPU", cpuLine, top: nil, now: pct(a.cpu))
            spark("Memory", memLine, top: nil, now: mem(a.memGB))
        }
        .frame(height: 90)
        Facts(rows: [(L("Processes"), a.pids.isEmpty ? "–" : "\(a.pids.count)"),
                     (L("Threads"), a.threads > 0 ? "\(a.threads)" : "–"),
                     (L("Disk read · write"), "\(rate(a.diskRead)) · \(rate(a.diskWrite))")]
                    + (netPerApp ? [(L("Network ↓ · ↑"), "\(rate(a.netIn)) · \(rate(a.netOut))")] : []))
    }

    private func spark(_ title: LocalizedStringKey, _ v: [Double], top: Double?, now: String) -> some View {
        DetailCard {
            VStack(alignment: .leading, spacing: 4) {
                HStack {
                    Text(title).font(theme.label(11)).foregroundStyle(theme.onRow(true))
                    Spacer()
                    Text(now).font(theme.number(11)).foregroundStyle(theme.onRow())
                }
                if v.count > 1 { SparkView(values: v, top: top, fill: 1, axis: nil, glides: false) }
                else { Text("Collecting…").font(theme.label(11)).foregroundStyle(theme.secondary).frame(maxWidth: .infinity, maxHeight: .infinity) }
            }
        }
    }

    // MARK: Terugkijken

    @ViewBuilder private var historySection: some View {
        section("History")
        Picker("", selection: $days) {
            Text("Today").tag(1); Text("7 days").tag(7); Text("30 days").tag(30)
        }
        .pickerStyle(.segmented).labelsHidden()
        if let h = history {
            if h.minutes == 0 {
                DetailCard { Text("Not seen \(when) — Fusebox logs open apps once a minute.").font(theme.label(12)).foregroundStyle(theme.onRow(true)) }
            } else {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 110), spacing: 8)], spacing: 8) {
                    stat("Average CPU", pct(h.avgCPU))
                    stat("Peak CPU", pct(h.peakCPU))
                    stat("Average memory", mem(h.avgMem))
                    stat("Time open", duration(h.minutes))
                    if let g = h.avgGPU { stat("Average GPU", pct(min(g, 100))) }
                }
                DetailCard {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("When you use it · by hour").font(theme.label(11)).foregroundStyle(theme.onRow(true))
                        Bars(values: h.hours, labels: (0..<24).map { $0 % 6 == 0 ? "\($0)" : "" }) { "\($0):00 – \($0 + 1):00 · \(duration(h.hours[$0]))" }
                        if days > 1 {
                            Text("By day of the week").font(theme.label(11)).foregroundStyle(theme.onRow(true)).padding(.top, 4)
                            Bars(values: h.weekdays, labels: (0..<7).map { Calendar.current.veryShortWeekdaySymbols[($0 + 1) % 7] }) { "\(Calendar.current.weekdaySymbols[($0 + 1) % 7]) · \(duration(h.weekdays[$0]))" }
                        }
                    }
                }
                DetailCard { energy(h) }
            }
            if let note = collecting(h) {
                Text(note).font(theme.label(10)).foregroundStyle(theme.secondary).fixedSize(horizontal: false, vertical: true)
            }
        } else {
            DetailCard { Text("Reading the logs…").font(theme.label(12)).foregroundStyle(theme.secondary) }
        }
    }

    private func energy(_ h: Analysis.AppHistory) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Label { Text(h.energyWh >= 0.1 ? "About \(one(h.energyWh)) Wh \(when) (≈ \(one(h.avgW)) W while open)" : "Hardly any energy \(when)") }
                icon: { Image(systemName: "bolt").frame(width: 14) }
            if let s = h.share {
                Label { Text("About \(pct(s * 100)) of your Mac's energy \(when)") } icon: { Image(systemName: "chart.pie").frame(width: 14) }
            }
            Label { Text(Analysis.batteryWords(avgW: h.avgW)) } icon: { Image(systemName: "battery.75percent").frame(width: 14) }
        }
        .font(theme.label(12)).foregroundStyle(theme.onRow())
    }

    private var when: String { days == 1 ? L("today") : L("in the last \(days) days") }

    /// Nieuwe kolommen (GPU, aandeel, alle open minuten) bestaan pas sinds deze versie: zeggen vanaf wanneer.
    private func collecting(_ h: Analysis.AppHistory) -> String? {
        let start = Calendar.current.date(byAdding: .day, value: 1 - days, to: Calendar.current.startOfDay(for: Date())) ?? Date()
        guard let since = h.since else { return L("GPU, energy share and full open time: collecting from now on.") }
        guard since > start.addingTimeInterval(3600) else { return nil }
        let f = DateFormatter()
        f.setLocalizedDateFormatFromTemplate(Calendar.current.isDateInToday(since) ? "HH:mm" : "d MMM")
        return L("GPU, energy share and full open time: collecting since \(Calendar.current.isDateInToday(since) ? L("today ") : "")\(f.string(from: since)). Before that, only minutes when \(ref.name) was among the busiest apps count.")
    }

    // MARK: Over

    @ViewBuilder private var aboutSection: some View {
        if let p = path {
            section("About")
            if let a = about {
                let running = app?.runningApp
                let arch = running?.executableArchitecture == NSBundleExecutableArchitectureX86_64 ? L("Intel (running via Rosetta now)") : a.arch
                Facts(rows: [(L("Bundle ID"), a.bundleID ?? "–"), (L("Version"), a.version ?? "–"), (L("Architecture"), arch ?? "–"),
                             (L("Size on disk"), a.size.map { bytes(Double($0)) } ?? "–"),
                             (L("Location"), (p as NSString).deletingLastPathComponent.replacingOccurrences(of: NSHomeDirectory(), with: "~")),
                             (L("Signed by"), a.signedBy ?? "–"), (L("Sandboxed"), a.sandboxed.map { $0 ? L("Yes") : L("No") } ?? "–"),
                             (L("Installed"), a.added.map(date) ?? "–"), (L("Last opened"), a.lastUsed.map(date) ?? "–"),
                             (L("Opens at login"), a.launchAgent ? L("Yes (launch agent)") : L("Unknown"))]
                      .filter { p.hasSuffix(".app") || ![L("Bundle ID"), L("Version"), L("Architecture"), L("Sandboxed")].contains($0.0) })
                    .help("“Opens at login”: Fusebox can see launch agents, but not the Login Items list in System Settings.")
            } else {
                DetailCard { Text("Looking it up…").font(theme.label(12)).foregroundStyle(theme.secondary) }
            }
        }
    }

    // MARK: Hulpjes

    private func section(_ t: LocalizedStringKey) -> some View {
        Text(t).font(theme.label(12).weight(.semibold)).foregroundStyle(theme.secondary).padding(.top, 4)
    }

    private func stat(_ label: LocalizedStringKey, _ value: String) -> some View {
        DetailCard {
            VStack(alignment: .leading, spacing: 2) {
                Text(label).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                Text(value).font(theme.number(16)).foregroundStyle(theme.onRow()).lineLimit(1).minimumScaleFactor(0.6)
            }
        }
    }

    private func date(_ d: Date) -> String { d.formatted(date: .abbreviated, time: .omitted) }
}

/// Staafjes (minuten per uur of per weekdag; in Checkup: crashes per dag), geschaald op de hoogste.
struct Bars: View {
    let values: [Int], labels: [String]
    let help: (Int) -> String
    @Environment(\.theme) private var theme

    var body: some View {
        let top = CGFloat(max(values.max() ?? 1, 1)), color = theme.rowStyle == .blocks ? Color.white : theme.accent
        HStack(alignment: .bottom, spacing: 2) {
            ForEach(values.indices, id: \.self) { i in
                VStack(spacing: 3) {
                    RoundedRectangle(cornerRadius: 2).fill(values[i] > 0 ? color : color.opacity(0.15))
                        .frame(height: max(2, 44 * CGFloat(values[i]) / top))
                    Text(labels[i]).font(theme.label(9)).foregroundStyle(theme.onRow(true)).lineLimit(1).fixedSize()
                }
                .frame(maxWidth: .infinity).contentShape(.rect).help(help(i))
            }
        }
        .frame(height: 60, alignment: .bottom)
    }
}
