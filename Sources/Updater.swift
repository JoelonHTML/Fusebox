import AppKit

// MARK: - Automatisch bijwerken via GitHub Releases
//
// Kijkt bij het starten en daarna elke 6 uur naar de nieuwste release van de GitHub-repo (Info.plist "FuseboxUpdateRepo").
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
        "\(Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?") (\(currentBuild))"
    }
    private var auto: Bool { UserDefaults.standard.object(forKey: "autoUpdate") as? Bool ?? true }

    /// Eén keer bij het opstarten aanroepen.
    func start() {
        guard Self.repo != nil, timer == nil else { return }
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { [weak self] in self?.check(userInitiated: false) }
        let t = Timer(timeInterval: 6 * 3600, repeats: true) { [weak self] _ in Task { @MainActor in self?.check(userInitiated: false) } }
        t.tolerance = 600
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
            let result = Self.parseRelease(data)
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
                if self.auto || userInitiated { self.install() }
            }
        }.resume()
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
                case .failure(let e): self.state = .failed(e.localizedDescription)
                }
            }
        }.resume()
    }

    private func replaceAndRelaunch(with newApp: URL) {
        let current = Bundle.main.bundleURL
        do {
            _ = try FileManager.default.replaceItemAt(current, withItemAt: newApp, backupItemName: nil, options: [])
        } catch {
            state = .failed("Couldn't replace the app: \(error.localizedDescription)")
            return
        }
        // Na het afsluiten de nieuwe versie starten (los proces, overleeft het afsluiten)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "sleep 1; /usr/bin/open \"$0\"", current.path]
        try? p.run()
        NSApp.terminate(nil)
    }

    // MARK: Puur (testbaar)

    /// Uit de GitHub-API: tag, bouwnummer (laatste getal ≥ 8 cijfers in de tag, bijv. "v1.1-202611162309") en de URL van Fusebox.zip.
    nonisolated static func parseRelease(_ data: Data?) -> (tag: String, build: Int, zip: URL)? {
        guard let data, data.count < 2_000_000,
              let j = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = j["tag_name"] as? String,
              let build = buildNumber(in: tag),
              let assets = j["assets"] as? [[String: Any]],
              let zip = assets.first(where: { ($0["name"] as? String) == "Fusebox.zip" })?["browser_download_url"] as? String,
              let url = URL(string: zip), url.scheme == "https",
              url.host == "github.com" || url.host?.hasSuffix(".githubusercontent.com") == true
        else { return nil }
        return (tag, build, url)
    }

    nonisolated static func buildNumber(in tag: String) -> Int? {
        tag.split(whereSeparator: { !$0.isNumber }).last.flatMap { $0.count >= 8 ? Int($0) : nil }
    }

    nonisolated private static func unpackAndVerify(zip: URL, expectedBuild: Int) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("fusebox-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        try run("/usr/bin/ditto", ["-x", "-k", zip.path, dir.path])
        let app = dir.appendingPathComponent("Fusebox.app")
        guard let b = Bundle(url: app), b.bundleIdentifier == Bundle.main.bundleIdentifier else { throw err("Not a Fusebox update") }
        guard Int(b.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") == expectedBuild else { throw err("Version mismatch") }
        try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
        return app
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
        precondition(buildNumber(in: "v1.1-202611162309") == 202611162309, "tag")
        precondition(buildNumber(in: "v1.1") == nil, "kort")
        let json = #"{"tag_name":"v1.2-202611200101","assets":[{"name":"Fusebox.zip","browser_download_url":"https://github.com/x/y/releases/download/v1.2-202611200101/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(json.utf8))?.build == 202611200101, "release")
        let evil = #"{"tag_name":"v9-209912312359","assets":[{"name":"Fusebox.zip","browser_download_url":"http://evil.example/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(evil.utf8)) == nil, "alleen https van GitHub")
        print("updater ok")
    }
}
