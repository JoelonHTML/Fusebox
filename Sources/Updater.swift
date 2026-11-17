import AppKit

// MARK: - Automatisch bijwerken via GitHub Releases
//
// Kijkt bij het starten en daarna elke 15 minuten naar de nieuwste release van de GitHub-repo (Info.plist "FuseboxUpdateRepo").
// Nieuwer bouwnummer → Fusebox.zip downloaden, controleren (zelfde bundle-id, geldige handtekening, hoger bouwnummer),
// de app vervangen en opnieuw starten. Downloads via URLSession krijgen geen quarantaine-vlag, dus geen "Open Anyway".

@MainActor final class Updater: ObservableObject {
    static let shared = Updater()

    enum State: Equatable { case idle, checking, upToDate, available(String), installing, failed(String) }
    @Published private(set) var state = State.idle
    @Published private(set) var lastCheck: Date?

    private var timer: Timer?
    private var latest: (tag: String, build: Int, zip: URL)?

    static var repo: String? { Bundle.main.object(forInfoDictionaryKey: "FuseboxUpdateRepo") as? String }
    static var currentBuild: Int { Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") ?? 0 }
    static var currentVersion: String {
        let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        return "\(v)\(v.hasPrefix("0.") ? " beta" : "") (\(currentBuild))"
    }
    private var auto: Bool { UserDefaults.standard.object(forKey: "autoUpdate") as? Bool ?? true }

    // MARK: Changelog (release-notes van GitHub, zo hoef je ze maar op één plek te schrijven)

    struct Release: Identifiable, Equatable { let id: String; let title: String; let date: Date?; let notes: String }
    /// nil = laden, [] = mislukt (wordt bij de volgende keer openen opnieuw geprobeerd)
    @Published private(set) var changelog: [Release]?

    func loadChangelog() {
        guard changelog?.isEmpty != false, let repo = Self.repo else { return }
        changelog = nil
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases?per_page=15")!, timeoutInterval: 20)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Fusebox-Updater", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, _, _ in
            let list = Self.parseReleases(data)
            Task { @MainActor in self.changelog = list }
        }.resume()
    }

    nonisolated static func parseReleases(_ data: Data?) -> [Release] {
        guard let data, let a = try? JSONSerialization.jsonObject(with: data) as? [[String: Any]] else { return [] }
        return a.compactMap { j in
            guard let tag = j["tag_name"] as? String, j["draft"] as? Bool != true else { return nil }
            let title = (j["name"] as? String).flatMap { $0.isEmpty ? nil : $0 } ?? "Fusebox \(pretty(tag))"
            let date = (j["published_at"] as? String).flatMap { ISO8601DateFormatter().date(from: $0) }
            return Release(id: tag, title: title, date: date, notes: (j["body"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines))
        }
    }

    /// Eén keer bij het opstarten aanroepen.
    func start() {
        guard Self.repo != nil, timer == nil else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { [weak self] in self?.check(userInitiated: false) }
        let t = Timer(timeInterval: 15 * 60, repeats: true) { [weak self] _ in Task { @MainActor in self?.check(userInitiated: false) } }
        t.tolerance = 60   // 4 vragen per uur; GitHub staat er 60 per uur toe zonder inloggen
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    /// Nieuwste release ophalen; bij "automatisch" meteen installeren, anders alleen melden.
    func check(userInitiated: Bool) {
        guard let repo = Self.repo, state != .checking, state != .installing else { return }
        state = .checking
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases/latest")!, timeoutInterval: 20)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Fusebox-Updater", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, response, error in
            let result = Self.parseRelease(data, repo: repo)
            Task { @MainActor in
                self.lastCheck = Date()
                guard let r = result else {
                    let code = (response as? HTTPURLResponse)?.statusCode ?? 0
                    self.state = userInitiated ? .failed(error?.localizedDescription ?? "GitHub answered \(code)") : .idle
                    return
                }
                guard r.build > Self.currentBuild else { self.state = .upToDate; return }
                self.latest = r
                self.state = .available(r.tag)
                if !userInitiated { self.notify(r.tag) }
                // Deze versie lukte al eens niet (geen schrijfrechten, codesign…): niet elke 15 min opnieuw downloaden
                let failedBefore = UserDefaults.standard.integer(forKey: "failedUpdateBuild") == r.build
                if userInitiated || (self.auto && !failedBefore) { self.install() }
            }
        }.resume()
    }

    /// Eén melding per nieuwe versie (niet bij elke controle opnieuw); volgt de meldingen-schakelaar.
    private func notify(_ tag: String) {
        let d = UserDefaults.standard
        guard d.bool(forKey: "notify"), d.string(forKey: "notifiedUpdate") != tag else { return }
        d.set(tag, forKey: "notifiedUpdate")
        Notify.send(title: "Fusebox \(Self.pretty(tag)) is available",
                    body: auto ? "Installing now. Fusebox restarts in a moment." : "Open Settings › About to install it.")
    }

    /// "v0.1-beta-202611162337" → "0.1 beta"
    nonisolated static func pretty(_ tag: String) -> String {
        tag.drop(while: { $0 == "v" }).split(separator: "-").dropLast().joined(separator: " ")
    }

    /// Downloaden, controleren, vervangen en opnieuw starten.
    func install() {
        guard let r = latest else { return }
        state = .installing
        URLSession.shared.downloadTask(with: r.zip) { tmp, _, error in
            let outcome: Result<URL, Error> = Result {
                guard let tmp else { throw error ?? Self.err("Download failed") }
                return try Self.unpackAndVerify(zip: tmp, expectedBuild: r.build)
            }
            Task { @MainActor in
                switch outcome {
                case .success(let newApp): self.replaceAndRelaunch(with: newApp)
                case .failure(let e):
                    UserDefaults.standard.set(r.build, forKey: "failedUpdateBuild")   // alleen nog op "Check for updates"
                    self.state = .failed(e.localizedDescription)
                }
            }
        }.resume()
    }

    private func replaceAndRelaunch(with newApp: URL) {
        let current = Bundle.main.bundleURL
        do {
            _ = try FileManager.default.replaceItemAt(current, withItemAt: newApp, backupItemName: nil, options: [])
        } catch {
            try? FileManager.default.removeItem(at: newApp.deletingLastPathComponent())
            if let b = latest?.build { UserDefaults.standard.set(b, forKey: "failedUpdateBuild") }
            state = .failed("Couldn't replace the app: \(error.localizedDescription)")
            return
        }
        try? FileManager.default.removeItem(at: newApp.deletingLastPathComponent())   // lege tijdelijke map opruimen
        // Pas starten als dit proces echt weg is (afsluiten kan >1 s duren: logboek, ventilator-helper)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$0\"", current.path, String(getpid())]
        try? p.run()
        NSApp.terminate(nil)
    }

    // MARK: Puur (testbaar)

    /// Uit de GitHub-API: tag, bouwnummer (laatste getal ≥ 8 cijfers in de tag, bijv. "v0.1-beta-202611162309") en de URL van Fusebox.zip.
    nonisolated static func parseRelease(_ data: Data?, repo: String? = nil) -> (tag: String, build: Int, zip: URL)? {
        guard let data, data.count < 2_000_000,
              let j = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = j["tag_name"] as? String,
              let build = buildNumber(in: tag),
              let assets = j["assets"] as? [[String: Any]],
              let zip = assets.first(where: { ($0["name"] as? String) == "Fusebox.zip" })?["browser_download_url"] as? String,
              let url = URL(string: zip), url.scheme == "https",
              url.host == "github.com" || url.host?.hasSuffix(".githubusercontent.com") == true,
              // Alleen een download uit déze repo (niet een willekeurige link die in de release staat)
              repo.map({ url.host != "github.com" || url.path.hasPrefix("/\($0)/releases/download/") }) ?? true
        else { return nil }
        return (tag, build, url)
    }

    nonisolated static func buildNumber(in tag: String) -> Int? {
        tag.split(whereSeparator: { !$0.isNumber }).last.flatMap { $0.count >= 8 ? Int($0) : nil }
    }

    nonisolated private static func unpackAndVerify(zip: URL, expectedBuild: Int) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("fusebox-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        do {
            try run("/usr/bin/ditto", ["-x", "-k", zip.path, dir.path])
            let app = dir.appendingPathComponent("Fusebox.app")
            guard let b = Bundle(url: app), b.bundleIdentifier == Bundle.main.bundleIdentifier else { throw err("Not a Fusebox update") }
            guard Int(b.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") == expectedBuild else { throw err("Version mismatch") }
            try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
            return app
        } catch {
            try? FileManager.default.removeItem(at: dir)   // geen uitgepakte kopieën laten slingeren
            throw error
        }
    }

    nonisolated private static func run(_ tool: String, _ args: [String]) throws {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tool)
        p.arguments = args
        try p.run(); p.waitUntilExit()
        guard p.terminationStatus == 0 else { throw err("\((tool as NSString).lastPathComponent) failed") }
    }

    nonisolated private static func err(_ s: String) -> NSError { NSError(domain: "Fusebox", code: 1, userInfo: [NSLocalizedDescriptionKey: s]) }

    nonisolated static func selfTest() {
        precondition(buildNumber(in: "v0.1-beta-202611162309") == 202611162309, "tag")
        precondition(buildNumber(in: "v0.1-beta") == nil, "kort")
        let rels = parseReleases(Data(#"[{"tag_name":"v0.2-beta-202611162352","name":"Fusebox 0.2 beta","published_at":"2026-11-16T22:52:00Z","body":"New stuff."},{"tag_name":"v0.3-beta-202612010000","draft":true}]"#.utf8))
        precondition(rels.count == 1 && rels[0].title == "Fusebox 0.2 beta" && rels[0].date != nil && rels[0].notes == "New stuff.", "changelog")
        precondition(pretty("v0.1-beta-202611162337") == "0.1 beta" && pretty("v1.0-202701010000") == "1.0", "mooie naam")
        let json = #"{"tag_name":"v1.2-202611200101","assets":[{"name":"Fusebox.zip","browser_download_url":"https://github.com/x/y/releases/download/v1.2-202611200101/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(json.utf8))?.build == 202611200101, "release")
        let evil = #"{"tag_name":"v9-209912312359","assets":[{"name":"Fusebox.zip","browser_download_url":"http://evil.example/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(evil.utf8)) == nil, "alleen https van GitHub")
        let other = #"{"tag_name":"v9-209912312359","assets":[{"name":"Fusebox.zip","browser_download_url":"https://github.com/someone/else/releases/download/v9/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(other.utf8), repo: "x/y") == nil && parseRelease(Data(json.utf8), repo: "x/y") != nil, "alleen eigen repo")
        print("updater ok")
    }
}
