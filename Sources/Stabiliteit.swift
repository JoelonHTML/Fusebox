import SwiftUI

// MARK: - Checkup › Stability: wat erbij kwam naast de Crashes-kaart
//
// Niets draait op een timer: de kaarten lezen alleen terwijl de groep Stability open staat, buiten de hoofdthread.

/// Crashes, vastlopers en kernel panics per dag over de laatste 30 dagen, los van de gekozen periode.
/// Dezelfde rapporten van macOS als de Crashes-kaart (alleen de kop van elk bestand); geen eigen score.
struct StabilityStrip: View {
    @Environment(\.theme) private var theme
    let index: Int
    static let days = 30
    @MainActor private static var cache: [Int]?
    @State private var counts: [Int]? = MainActor.assumeIsolated { StabilityStrip.cache }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            CardTitle(icon: "calendar", title: L("Last 30 days"))
            if let counts {
                let n = counts.reduce(0, +), cal = Calendar.current
                let day = { (i: Int) in cal.date(byAdding: .day, value: i - (counts.count - 1), to: Date()) ?? Date() }
                Headline(big: n == 0 ? L("None") : "\(n)", small: L("Crashes and hangs per day, from macOS' own reports"), tone: n == 0 ? 3 : 0)
                // Dag van de maand onder elke zevende dag, geteld vanaf vandaag (rechts)
                Bars(values: counts, labels: counts.indices.map { ($0 - (counts.count - 1)) % 7 == 0 ? "\(cal.component(.day, from: day($0)))" : "" }) {
                    day($0).formatted(Date.FormatStyle().weekday(.abbreviated).day().month(.abbreviated).locale(uiLocale)) + " · \(counts[$0])"
                }
            } else {
                ProgressView().controlSize(.small)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .task {
            let days = Self.days
            let r = await Task.detached(priority: .utility) { () -> [Int] in
                let now = Date()
                let from = Calendar.current.startOfDay(for: now.addingTimeInterval(-Double(days - 1) * 86_400))
                // ponytail: crashes() leest hoogstens de nieuwste 300 rapporten; bij meer in 30 dagen tellen de oudste dagen te laag (grens verhogen als dat speelt)
                return Diag.perDay(Diag.crashes(from: from, to: .distantFuture).stamps, days: days, now: now)
            }.value
            Self.cache = r
            counts = r
        }
    }
}

// MARK: - Software-update
//
// Bron: de eigen voorkeuren van macOS (/Library/Preferences/com.apple.SoftwareUpdate.plist, voor iedereen leesbaar):
// direct, zonder netwerk of subproces. `softwareupdate -l` (netwerk, ~7–60 s) draait alleen na een klik op "Check now".
// macOS laat niet zien of een update al gedownload is (in /Library/Updates staat op Apple Silicon niets bruikbaars),
// dus we zeggen alleen "update beschikbaar", nooit "herstart nodig".

enum Updates {
    struct Update: Codable, Equatable, Sendable {
        let name: String
        var version: String? = nil
        var macOS = false          // update van macOS zelf (vraagt een herstart)
        /// "macOS Tahoe 26.2"; de versie alleen erbij als die niet al in de naam staat.
        var title: String { version.map { name.contains($0) ? name : "\(name) \($0)" } ?? name }
    }
    /// Wat er klaarstaat en wanneer er voor het laatst gezocht is.
    struct State: Equatable, Sendable { var list: [Update] = []; var checked: Date? }

    /// Wat macOS zelf bijhoudt. Alle sleutels zijn optioneel: op de test-Mac stond geen update klaar, dus de
    /// sleutels van een item zijn niet gezien; een item zonder bruikbare sleutels telt wel mee.
    static func state(_ d: [String: Any]) -> State {
        let list = (d["RecommendedUpdates"] as? [Any] ?? []).map { e -> Update in
            let x = e as? [String: Any] ?? [:]
            let s = { (k: String) in (x[k] as? String).flatMap { $0.isEmpty ? nil : $0 } }
            let name = s("Display Name")
            // Niet op vertaalde tekst leunen: de productsleutel van een macOS-update begint met MSU_UPDATE_
            let mac = [s("Product Key"), s("Identifier")].contains { $0?.hasPrefix("MSU_UPDATE_") == true || $0?.hasPrefix("macOS") == true }
                || name?.hasPrefix("macOS") == true
            return Update(name: name ?? (mac ? "macOS" : s("Identifier") ?? "–"), version: s("Display Version"), macOS: mac)
        }
        return State(list: list, checked: (d["LastSuccessfulDate"] ?? d["LastFullSuccessfulDate"]) as? Date)
    }

    static func system() -> State {
        state(NSDictionary(contentsOfFile: "/Library/Preferences/com.apple.SoftwareUpdate.plist") as? [String: Any] ?? [:])
    }

    /// Uitvoer van `softwareupdate -l` (stdout en stderr samen). Leeg = niets beschikbaar; nil = onbekende opmaak.
    static func parse(_ out: String) -> [Update]? {
        var list: [Update] = []
        for raw in out.split(separator: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces)
            if line.hasPrefix("* ") {
                var label = line.dropFirst(2).trimmingCharacters(in: .whitespaces)
                if label.hasPrefix("Label:") { label = label.dropFirst(6).trimmingCharacters(in: .whitespaces) }
                list.append(Update(name: label, macOS: label.hasPrefix("macOS")))
            } else if line.hasPrefix("Title:"), let i = list.indices.last {
                // "Title: macOS Tahoe 26.2, Version: 26.2, Size: 4215837KiB, Recommended: YES, Action: restart,"
                var f: [String: String] = [:]
                for part in line.split(separator: ",") {
                    let kv = part.split(separator: ":", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
                    if kv.count == 2 { f[kv[0]] = kv[1] }
                }
                let title = f["Title"] ?? list[i].name
                list[i] = Update(name: title, version: f["Version"], macOS: title.hasPrefix("macOS") || list[i].macOS || f["Action"] == "restart")
            }
        }
        if !list.isEmpty { return list }
        return out.contains("No new software available") ? [] : nil
    }

    /// Eén keer `softwareupdate -l`. Stopt het proces bij annuleren (pagina weg) en na 90 s. nil = niet gelukt.
    static func list() async -> String? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/softwareupdate"); p.arguments = ["-l"]
        p.environment = ["LC_ALL": "C"]
        let pipe = Pipe()
        p.standardOutput = pipe; p.standardError = pipe   // "No new software available." komt op stderr
        let job = Task.detached(priority: .utility) { () -> String? in
            guard (try? p.run()) != nil else { return nil }
            if Task.isCancelled { p.terminate() }   // geannuleerd vlak voor de start
            DispatchQueue.global().asyncAfter(deadline: .now() + 90) { if p.isRunning { p.terminate() } }
            let data = pipe.fileHandleForReading.readDataToEndOfFile()
            p.waitUntilExit()
            return p.terminationReason == .exit && p.terminationStatus == 0 ? String(decoding: data, as: UTF8.self) : nil
        }
        return await withTaskCancellationHandler { await job.value } onCancel: { job.cancel(); if p.isRunning { p.terminate() } }
    }

    // De laatste handmatige controle, ook na een herstart van Fusebox (alleen namen en versies, geen productsleutels).
    static func json(_ list: [Update]) -> String? { (try? JSONEncoder().encode(list)).map { String(decoding: $0, as: UTF8.self) } }
    static func list(json: String) -> [Update]? { try? JSONDecoder().decode([Update].self, from: Data(json.utf8)) }

    static func save(_ list: [Update], at: Date) {
        UserDefaults.standard.set(at, forKey: "swuCheckedAt")
        UserDefaults.standard.set(json(list), forKey: "swuResult")
    }
    static func saved() -> State? {
        let d = UserDefaults.standard
        guard let at = d.object(forKey: "swuCheckedAt") as? Date, let list = d.string(forKey: "swuResult").flatMap(list(json:)) else { return nil }
        return State(list: list, checked: at)
    }

    /// De handmatige controle telt hoogstens 6 uur, en alleen als macOS zelf daarna niet meer gezocht heeft.
    static func pick(system: State, manual: State?, now: Date) -> State {
        guard let m = manual, let at = m.checked, now.timeIntervalSince(at) < 6 * 3600, at > system.checked ?? .distantPast else { return system }
        return m
    }

    /// Wat Checkup toont. Alleen lezen (plist + eigen voorkeuren); start niets.
    static func current(now: Date = Date()) -> State { pick(system: system(), manual: saved(), now: now) }

    /// Kernfeit van de kaart. Zonder recente controle (14 dagen) zeggen we niet "Up to date".
    static func headline(_ s: State, now: Date) -> (big: String, tone: Int, stale: Bool) {
        if !s.list.isEmpty { return (s.list.count == 1 ? L("1 update available") : L("\(s.list.count) updates available"), 1, false) }
        guard let c = s.checked, now.timeIntervalSince(c) < 14 * 86_400 else { return (L("Not checked recently"), 0, true) }
        return (L("Up to date"), 3, false)
    }

    static func selfTest() {
        // Opmaak van `softwareupdate -l` zoals macOS 11+ die afdrukt (uit de documentatie nagebouwd: op de test-Mac stond niets klaar)
        let sample = "Software Update Tool\n\nFinding available software\nSoftware Update found the following new or updated software:\n"
            + "* Label: macOS Tahoe 26.2-25C56\n\tTitle: macOS Tahoe 26.2, Version: 26.2, Size: 4215837KiB, Recommended: YES, Action: restart, \n"
            + "* Label: Safari26.2TahoeAuto-26.2\n\tTitle: Safari, Version: 26.2, Size: 173522KiB, Recommended: YES, \n"
        let two = parse(sample)
        precondition(two == [Update(name: "macOS Tahoe 26.2", version: "26.2", macOS: true), Update(name: "Safari", version: "26.2")], "softwareupdate \(String(describing: two))")
        precondition(two?.map(\.title) == ["macOS Tahoe 26.2", "Safari 26.2"], "titel zonder dubbele versie")
        // Echt gezien op macOS 26: de melding "niets" staat op stderr, de kop op stdout
        precondition(parse("Software Update Tool\n\nFinding available software\nNo new software available.\n") == [], "niets beschikbaar")
        precondition(parse("rommel\n") == nil && parse("") == nil && parse("Software Update Tool\n\nFinding available software\n") == nil, "onbekende opmaak")
        precondition(parse("   * Firmware-1.2\n") == [Update(name: "Firmware-1.2")], "alleen een label")

        let checked = Date(timeIntervalSince1970: 1_790_000_000)
        let st = state(["LastSuccessfulDate": checked, "RecommendedUpdates": [
            ["Display Name": "macOS Tahoe 26.2", "Display Version": "26.2", "Identifier": "MSU_UPDATE_25C56_patch_26.2_minor", "Product Key": "MSU_UPDATE_25C56_patch_26.2_minor"],
            ["Product Key": "MSU_UPDATE_25C56_patch_26.2_minor"], ["Display Name": "Safari", "Display Version": "26.2", "Identifier": "Safari26.2TahoeAuto"],
            ["Identifier": "XProtectPlistConfigData"], "geen woordenboek"] as [Any]])
        precondition(st.checked == checked && st.list == [Update(name: "macOS Tahoe 26.2", version: "26.2", macOS: true), Update(name: "macOS", macOS: true),
                                                           Update(name: "Safari", version: "26.2"), Update(name: "XProtectPlistConfigData"), Update(name: "–")], "plist \(st)")
        precondition(state([:]) == State() && state(["RecommendedUpdates": [Any](), "LastFullSuccessfulDate": checked]) == State(list: [], checked: checked), "lege plist")
        precondition(two.flatMap(json).flatMap(list(json:)) == two && list(json: "kapot") == nil, "bewaren")

        // Handmatige controle: 6 uur geldig, en macOS wint zodra het zelf later zocht
        let manual = State(list: [Update(name: "Safari")], checked: checked.addingTimeInterval(60))
        precondition(pick(system: st, manual: manual, now: checked.addingTimeInterval(3600)) == manual, "handmatig is nieuwer")
        precondition(pick(system: st, manual: manual, now: checked.addingTimeInterval(7 * 3600)) == st, "handmatig verlopen")
        precondition(pick(system: State(list: [], checked: checked.addingTimeInterval(120)), manual: manual, now: checked.addingTimeInterval(3600)).list.isEmpty
                     && pick(system: st, manual: nil, now: checked) == st, "macOS zocht later")

        let h = headline(st, now: checked)
        precondition(h.big == "5 updates available" && h.tone == 1 && !h.stale && headline(manual, now: checked).big == "1 update available", "kop")
        precondition(headline(State(list: [], checked: checked), now: checked.addingTimeInterval(86_400)) == ("Up to date", 3, false), "bij")
        precondition(headline(State(), now: checked) == ("Not checked recently", 0, true)
                     && headline(State(list: [], checked: checked), now: checked.addingTimeInterval(15 * 86_400)).stale, "lang niet gezocht")
        print("updates ok")
    }
}

/// Checkup › Stability: klaarstaande updates. Zoekt nooit zelf; "Check now" start één keer `softwareupdate -l`,
/// en dat stopt als de kaart verdwijnt (.task wordt geannuleerd).
struct UpdatesCard: View {
    @Environment(\.theme) private var theme
    let state: Updates.State, index: Int
    let changed: () -> Void            // na een controle: het rapport (tips, kop van de groep) opnieuw opbouwen
    @State private var fresh: Updates.State?   // uitslag van de controle, tot het rapport hem ook heeft
    @State private var run = 0
    @State private var checking = false
    @State private var note: String?

    var body: some View {
        let s = fresh ?? state, h = Updates.headline(s, now: Date())
        VStack(alignment: .leading, spacing: 5) {
            HStack {
                CardTitle(icon: "arrow.down.circle", title: L("Software Update"), tone: h.tone)
                Spacer()
                Button("Check now") { run += 1 }.buttonStyle(Pill(color: theme.onRow())).disabled(checking).opacity(checking ? 0.4 : 1)
            }
            if checking {
                HStack(spacing: 6) {
                    ProgressView().controlSize(.small)
                    Text("Checking…").font(theme.label(11)).foregroundStyle(theme.onRow(true))
                }
            } else {
                Headline(big: h.big, small: s.checked.map { L("Last checked \($0.formatted(Date.FormatStyle(date: .abbreviated, time: .shortened).locale(uiLocale)))") }, tone: h.tone)
                Capped(items: Array(s.list.enumerated()), id: \.offset) {
                    Text($0.element.title).font(theme.label(12)).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.tail)
                }
                if h.stale {
                    Text("Fusebox doesn't check by itself. Press Check now.").font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                }
                if !s.list.isEmpty {
                    Button("Open Software Update") { NSWorkspace.shared.open(Analysis.softwareUpdate) }.buttonStyle(Pill(color: theme.onRow())).padding(.top, 3)
                }
            }
            if let note { Text(note).font(theme.label(11)).foregroundStyle(theme.warn) }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .onChange(of: state) { fresh = nil }
        .task(id: run) {
            guard run > 0 else { return }
            note = nil; checking = true
            let out = await Updates.list()
            checking = false
            guard !Task.isCancelled else { return }
            guard let out else { note = L("Couldn't check for updates."); return }
            guard let list = Updates.parse(out) else { note = L("Couldn't read the result."); return }
            let now = Date()
            Updates.save(list, at: now)
            fresh = Updates.State(list: list, checked: now)
            changed()
        }
    }
}
