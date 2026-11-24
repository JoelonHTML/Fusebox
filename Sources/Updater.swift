import AppKit
import CryptoKit

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
    nonisolated static var currentBuild: Int { Int(Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") ?? 0 }
    static var currentVersion: String {
        let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        return "\(v)\(v.hasPrefix("0.") ? " beta" : "") (\(currentBuild))"
    }
    /// Vanzelf installeren alleen vanuit een Programma's-map (niet vanuit Downloads, een bouwmap of een door macOS verplaatste kopie): daar alleen melden.
    private var auto: Bool {
        (UserDefaults.standard.object(forKey: "autoUpdate") as? Bool ?? true) && Installer.isInApplications(Bundle.main.bundleURL.resolvingSymlinksInPath())
    }

    // MARK: Changelog (release-notes van GitHub, zo hoef je ze maar op één plek te schrijven)

    struct Release: Identifiable, Equatable {
        let id: String; let title: String; let date: Date?; var notes: String
        var builds: [String] = []   // tags van snelle herpublicaties met dezelfde versie (samengevoegd)
    }
    /// Zelfde versie meerdere keren gepubliceerd (snelle herstel-release): één regel, nieuwste bovenaan, notities samen.
    nonisolated static func merged(_ list: [Release]) -> [Release] {
        var out: [Release] = []
        // Op buildnummer (in de tag), nieuwste eerst: GitHub geeft de lijst niet altijd op volgorde terug
        for r in list.sorted(by: { (buildNumber(in: $0.id) ?? 0) > (buildNumber(in: $1.id) ?? 0) }) {
            if let last = out.last, last.title == r.title {
                out[out.count - 1].notes = [last.notes, r.notes].filter { !$0.isEmpty }.joined(separator: "\n")
                out[out.count - 1].builds.append(r.id)
            } else { out.append(r) }
        }
        return out
    }
    /// nil = laden, [] = mislukt (wordt bij de volgende keer openen opnieuw geprobeerd)
    @Published private(set) var changelog: [Release]?

    func loadChangelog() {
        guard changelog?.isEmpty != false, let repo = Self.repo else { return }
        changelog = nil
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases?per_page=60")!, timeoutInterval: 20)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Fusebox-Updater", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, _, _ in
            let list = Self.merged(Self.parseReleases(data))
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
        // Draaide de vorige update terug (waakhond of rollBackIfNeverConfirmed)? Die build niet nog eens vanzelf proberen, en één melding.
        if let bad = Self.takeFailed(.live) {
            UserDefaults.standard.set(bad.build, forKey: "failedUpdateBuild")
            if UserDefaults.standard.bool(forKey: "notify") {
                Notify.send(title: L("Fusebox went back to the previous version"),
                            body: bad.version.isEmpty ? L("The update didn't start properly. Fusebox will not try that version again automatically.")
                                : L("The update didn't start properly. Fusebox will not try version \(bad.version) again automatically."))
            }
        }
        // 3 s in leven met een werkende main-runloop = deze versie start: marker en back-up weg. Netjes afsluiten binnen die 3 s telt ook.
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { Self.confirm() }
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: nil) { _ in Self.confirm() }
        announceIfUpdated()
        DispatchQueue.main.asyncAfter(deadline: .now() + 15) { [weak self] in self?.check(userInitiated: false) }
        let t = Timer(timeInterval: 15 * 60, repeats: true) { [weak self] _ in
            Task { @MainActor in
                // Low impact: één keer per uur is genoeg
                if LowImpact.on, let last = self?.lastCheck, Date().timeIntervalSince(last) < 55 * 60 { return }
                self?.check(userInitiated: false)
            }
        }
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
                    self.state = userInitiated ? .failed(error?.localizedDescription ?? L("GitHub answered \(code)")) : .idle
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
        Notify.send(title: L("Fusebox \(Self.pretty(tag)) is available"),
                    body: auto ? L("Installing now. Fusebox restarts in a moment.") : L("Open Settings › About to install it."))
    }

    /// Eerste start na een update: één melding met in een paar woorden wat er nieuw is, knop naar de changelog.
    private func announceIfUpdated() {
        let d = UserDefaults.standard, seen = d.integer(forKey: "lastSeenBuild"), now = Self.currentBuild
        d.set(now, forKey: "lastSeenBuild")
        // Verse installatie (setup nog niet gedaan): niets melden. Oudere versies onthielden de build nog niet (seen = 0).
        guard now > seen, seen > 0 || d.bool(forKey: "setupDone"), d.bool(forKey: "notify"), let repo = Self.repo else { return }
        var req = URLRequest(url: URL(string: "https://api.github.com/repos/\(repo)/releases?per_page=10")!, timeoutInterval: 20)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Fusebox-Updater", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, _, _ in
            let list = Self.parseReleases(data)
            let mine = list.first { Self.buildNumber(in: $0.id) == now } ?? list.first
            let version = mine.map { Self.pretty($0.id) } ?? ""
            Notify.send(title: version.isEmpty ? L("Fusebox updated") : L("Fusebox updated to \(version)"),
                        body: mine.map { Self.summary($0.notes) }.flatMap { $0.isEmpty ? nil : $0 } ?? L("See what's new in Settings › About."),
                        category: Notify.whatsNew)
        }.resume()
    }

    /// Release-notes → een paar woorden: de eerste drie punten, elk tot de eerste ":" of "—", met " · " ertussen.
    nonisolated static func summary(_ notes: String) -> String {
        let lines = notes.split(separator: "\n").map { $0.trimmingCharacters(in: .whitespaces) }.filter { !$0.isEmpty }
        let bullets = lines.filter { $0.hasPrefix("- ") || $0.hasPrefix("* ") }.map { String($0.dropFirst(2)) }
        guard !bullets.isEmpty else { return String((lines.first ?? "").prefix(110)) }
        let short = bullets.prefix(3).map { b -> String in
            let cut = b.firstRange(of: ":")?.lowerBound ?? b.firstRange(of: " — ")?.lowerBound ?? b.endIndex
            return String(b[..<cut]).trimmingCharacters(in: .whitespaces)
        }
        let s = short.joined(separator: " · ")
        return s.count > 110 ? String(s.prefix(107)) + "…" : s
    }

    /// "v0.1-beta-202611162337" → "0.1 beta"
    nonisolated static func pretty(_ tag: String) -> String {
        tag.drop(while: { $0 == "v" }).split(separator: "-").dropLast().joined(separator: " ")
    }

    /// Publieke helft van de release-sleutel. De geheime helft staat alleen in de sleutelhanger van de maker
    /// (release.sh ondertekent Fusebox.zip daarmee). Zonder geldige handtekening wordt een update geweigerd,
    /// ook als iemand een release op GitHub zou zetten.
    nonisolated static let releaseKey = "uYp37BJBFTd9+oS28OpFCW60sRliYE4uHvyGBnCrnXU="

    /// Klopt de handtekening (base64, Ed25519) bij deze bytes?
    nonisolated static func signatureValid(_ data: Data, signature: String, key: String = releaseKey) -> Bool {
        guard let k = Data(base64Encoded: key), let pub = try? Curve25519.Signing.PublicKey(rawRepresentation: k),
              let sig = Data(base64Encoded: signature.trimmingCharacters(in: .whitespacesAndNewlines)) else { return false }
        return pub.isValidSignature(sig, for: data)
    }

    /// Downloaden (eerst de handtekening, dan de zip), controleren, vervangen en opnieuw starten.
    func install() {
        guard let r = latest else { return }
        state = .installing
        let sigURL = r.zip.appendingPathExtension("sig")   // .../download/<tag>/Fusebox.zip.sig
        URLSession.shared.dataTask(with: sigURL) { sigData, sigResponse, _ in
        let sig = (sigResponse as? HTTPURLResponse)?.statusCode == 200 ? sigData.flatMap { String(data: $0, encoding: .utf8) } : nil
        URLSession.shared.downloadTask(with: r.zip) { tmp, _, error in
            let outcome: Result<URL, Error> = Result {
                guard let tmp else { throw error ?? Self.err(L("Download failed")) }
                guard let sig, let bytes = try? Data(contentsOf: tmp), bytes.count < 200_000_000,
                      Self.signatureValid(bytes, signature: sig) else { throw Self.err(L("Update isn't signed by the Fusebox maker")) }
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
        }.resume()
    }

    private func replaceAndRelaunch(with newApp: URL) {
        let p = Paths.live, fm = FileManager.default, tmp = newApp.deletingLastPathComponent()
        func fail(_ why: String) {
            try? fm.removeItem(at: tmp)
            try? fm.removeItem(at: p.backup.deletingLastPathComponent())
            if let b = latest?.build { UserDefaults.standard.set(b, forKey: "failedUpdateBuild") }
            state = .failed(why)
        }
        // Eerst een kopie van de huidige app (APFS-kloon, kost niets). Lukt dat niet (schijf vol): niet bijwerken.
        do { try Self.backUp(p) } catch { return fail(L("Couldn't back up the current version")) }
        do {
            _ = try fm.replaceItemAt(p.app, withItemAt: newApp, backupItemName: nil, options: [])
        } catch {
            return fail(L("Couldn't replace the app: \(error.localizedDescription)"))
        }
        try? fm.removeItem(at: tmp)   // lege tijdelijke map opruimen
        let build = latest?.build ?? 0
        try? JSONEncoder().encode(Marker(build: build, from: Self.currentBuild, at: Int(Date().timeIntervalSince1970))).write(to: p.marker)
        Self.relaunch(p, failedNote: latest?.tag ?? String(build))
        NSApp.terminate(nil)
    }

    // MARK: Terugdraaien als een update niet start
    //
    // Vóór het vervangen gaat er een kopie van de huidige app naar update-previous/, daarna komt de marker update-pending.
    // De nieuwe versie wist beide zodra ze 3 s draait (confirm). Staat de marker er dan nog, dan zet de waakhond in het
    // herstartscript de vorige versie terug (na 20 s; vangt ook crashes vóór er Swift-code draait), en anders doet de
    // volgende start dat (rollBackIfNeverConfirmed). Beide schrijven update-failed; de oude versie leest dat in start().

    struct Paths {
        let app: URL, dir: URL
        var backup: URL { dir.appendingPathComponent("update-previous/Fusebox.app") }
        var marker: URL { dir.appendingPathComponent("update-pending") }
        var failed: URL { dir.appendingPathComponent("update-failed") }
        static var live: Paths {
            Paths(app: Bundle.main.bundleURL,
                  dir: FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Fusebox"))
        }
    }
    /// update-pending: {"build": nieuw, "from": oud, "at": epoch}
    struct Marker: Codable, Equatable { let build: Int, from: Int, at: Int }

    nonisolated static func readMarker(_ p: Paths) -> Marker? {
        (try? Data(contentsOf: p.marker)).flatMap { try? JSONDecoder().decode(Marker.self, from: $0) }
    }

    /// Spiegel van UpdateSafety.ShouldRollBack (Windows): marker van déze build, ouder dan 2 min, en er is iets om terug te zetten.
    nonisolated static func shouldRollBack(markerAge: TimeInterval, backupExists: Bool, markerBuild: Int, currentBuild: Int) -> Bool {
        backupExists && markerBuild == currentBuild && markerAge > 120
    }

    nonisolated static func backUp(_ p: Paths) throws {
        let fm = FileManager.default, dir = p.backup.deletingLastPathComponent()
        try? fm.removeItem(at: dir)
        try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        do { try fm.copyItem(at: p.app, to: p.backup) } catch { try? fm.removeItem(at: dir); throw error }
    }

    /// Deze versie draait echt: marker, back-up en uitgepakte resten weg. Alleen op de main queue (bewijs dat de runloop werkt).
    nonisolated static func confirm(_ p: Paths = .live, build: Int = currentBuild, temp: URL = FileManager.default.temporaryDirectory) {
        let fm = FileManager.default
        if let m = readMarker(p), m.build != build { return }   // marker van een andere build (de update die nu klaarstaat): niet van ons
        try? fm.removeItem(at: p.marker)
        try? fm.removeItem(at: p.backup.deletingLastPathComponent())
        for f in (try? fm.contentsOfDirectory(atPath: temp.path)) ?? [] where f.hasPrefix("fusebox-update-") {
            try? fm.removeItem(at: temp.appendingPathComponent(f))
        }
    }

    /// Vorige versie terugzetten: slechte app opzij, back-up op zijn plek, marker weg, update-failed voor de oude versie.
    /// false = niets veranderd (back-up zonder geldige handtekening, of verplaatsen lukte niet): dan blijft de nieuwe staan.
    nonisolated static func restore(_ p: Paths, badBuild: Int, verify: (URL) -> Bool = signed) -> Bool {
        let fm = FileManager.default, bad = p.backup.appendingPathExtension("bad")
        guard verify(p.backup) else { return false }
        try? fm.removeItem(at: bad)
        do { try fm.moveItem(at: p.app, to: bad) } catch { return false }
        do { try fm.moveItem(at: p.backup, to: p.app) } catch { try? fm.moveItem(at: bad, to: p.app); return false }
        try? fm.removeItem(at: p.marker)
        try? Data(String(badBuild).utf8).write(to: p.failed)
        try? fm.removeItem(at: p.backup.deletingLastPathComponent())   // met de slechte versie erin
        return true
    }

    nonisolated static func signed(_ app: URL) -> Bool {
        (try? run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])) != nil
    }

    /// Tweede laag, vóór er UI is (Entry.main): deze build is ooit gestart maar heeft nooit bevestigd → vorige versie terug en die starten.
    nonisolated static func rollBackIfNeverConfirmed(_ p: Paths = .live) {
        guard let m = readMarker(p), Installer.isInApplications(p.app.resolvingSymlinksInPath()),
              shouldRollBack(markerAge: Date().timeIntervalSince1970 - Double(m.at), backupExists: FileManager.default.fileExists(atPath: p.backup.path),
                             markerBuild: m.build, currentBuild: currentBuild),
              restore(p, badBuild: m.build) else { return }
        relaunch(p)
        exit(0)
    }

    /// Wacht tot dit proces echt weg is (afsluiten kan >1 s duren: logboek, ventilator-helper) en start de app.
    /// Na een update (met $2…) ook de waakhond: staat de marker er na 20 s nog en draait de app niet, dan startte de nieuwe
    /// versie niet → vorige versie terugzetten (alleen met geldige handtekening) en die openen. Het script gooit zelf niets weg:
    /// de teruggezette versie ruimt op (takeFailed wist de marker, confirm de map update-previous met de slechte kopie erin).
    /// "Draait niet" = geen proces uit Contents/MacOS van die app (de ventilator-helper staat in Resources); letterlijk vergeleken,
    /// want pgrep -f leest haakjes of een + in de mapnaam als patroon. Zo telt een app die nog bezig is met starten niet als mislukt.
    /// $0 app, $1 pid, $2 marker, $3 back-up, $4 update-failed, $5 wat daarin komt (tag of bouwnummer). Paden nooit in de scripttekst.
    nonisolated static let relaunchScript = """
        while kill -0 "$1" 2>/dev/null; do sleep 0.2; done
        /usr/bin/open "$0"
        [ -n "$2" ] || exit 0
        sleep 20
        [ -e "$2" ] || exit 0
        /bin/ps -axo comm= | /usr/bin/grep -qF -- "$0/Contents/MacOS/" && exit 0
        /usr/bin/codesign --verify --deep --strict "$3" || exit 0
        mv "$0" "$3.bad" || exit 0
        mv "$3" "$0" || { mv "$3.bad" "$0"; exit 0; }
        printf %s "$5" > "$4"
        /usr/bin/open "$0"
        """

    nonisolated static func relaunch(_ p: Paths, failedNote: String? = nil) {
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/bin/sh")
        proc.arguments = ["-c", relaunchScript, p.app.path, String(getpid())]
            + (failedNote.map { [p.marker.path, p.backup.path, p.failed.path, $0] } ?? [])
        try? proc.run()
    }

    /// update-failed lezen en weggooien, samen met de marker van die update → (bouwnummer, "0.14 beta" of "" als alleen het nummer bekend is).
    nonisolated static func takeFailed(_ p: Paths) -> (build: Int, version: String)? {
        guard let s = try? String(contentsOf: p.failed, encoding: .utf8) else { return nil }
        try? FileManager.default.removeItem(at: p.failed)
        try? FileManager.default.removeItem(at: p.marker)
        return failedInfo(s)
    }
    nonisolated static func failedInfo(_ s: String) -> (build: Int, version: String)? {
        let t = s.trimmingCharacters(in: .whitespacesAndNewlines)
        if let b = Int(t) { return (b, "") }
        return buildNumber(in: t).map { ($0, pretty(t)) }
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
            guard let b = Bundle(url: app), b.bundleIdentifier == Bundle.main.bundleIdentifier else { throw err(L("Not a Fusebox update")) }
            guard Int(b.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") == expectedBuild else { throw err(L("Version mismatch")) }
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
        guard p.terminationStatus == 0 else { throw err(L("\((tool as NSString).lastPathComponent) failed"))
 }
    }

    nonisolated private static func err(_ s: String) -> NSError { NSError(domain: "Fusebox", code: 1, userInfo: [NSLocalizedDescriptionKey: s]) }

    nonisolated static func selfTest() {
        precondition(buildNumber(in: "v0.1-beta-202611162309") == 202611162309, "tag")
        precondition(buildNumber(in: "v0.1-beta") == nil, "kort")
        precondition(summary("- Docked: see what's plugged in\n- Much lighter — 5% CPU\n- Fonts: six choices\n- More") == "Docked · Much lighter · Fonts", "samenvatting")
        // Handtekening: echt sleutelpaar in de test, geknoeide data en een vreemde sleutel worden geweigerd
        let testKey = Curve25519.Signing.PrivateKey(), blob = Data("Fusebox.zip".utf8)
        let good = (try! testKey.signature(for: blob)).base64EncodedString(), pub = testKey.publicKey.rawRepresentation.base64EncodedString()
        precondition(signatureValid(blob, signature: good, key: pub), "handtekening goed")
        precondition(!signatureValid(Data("Fusebox.zip!".utf8), signature: good, key: pub), "geknoeid")
        precondition(!signatureValid(blob, signature: good), "andere sleutel")
        precondition(Data(base64Encoded: releaseKey)?.count == 32, "publieke sleutel")
        let rels = parseReleases(Data(#"[{"tag_name":"v0.2-beta-202611162352","name":"Fusebox 0.2 beta","published_at":"2026-11-16T22:52:00Z","body":"New stuff."},{"tag_name":"v0.3-beta-202612010000","draft":true}]"#.utf8))
        precondition(rels.count == 1 && rels[0].title == "Fusebox 0.2 beta" && rels[0].date != nil && rels[0].notes == "New stuff.", "changelog")
        let m = merged([Release(id: "v0.4-beta-202611170900", title: "Fusebox 0.4 beta", date: nil, notes: "- A"), Release(id: "v0.5-beta-202611171200", title: "Fusebox 0.5 beta", date: nil, notes: "- C"),
                        Release(id: "v0.5-beta-202611171100", title: "Fusebox 0.5 beta", date: nil, notes: "- B")])
        precondition(m.count == 2 && m[0].notes == "- C\n- B" && m[0].builds == ["v0.5-beta-202611171100"] && m[1].title == "Fusebox 0.4 beta", "changelog samenvoegen")
        precondition(pretty("v0.1-beta-202611162337") == "0.1 beta" && pretty("v1.0-202701010000") == "1.0", "mooie naam")
        let json = #"{"tag_name":"v1.2-202611200101","assets":[{"name":"Fusebox.zip","browser_download_url":"https://github.com/x/y/releases/download/v1.2-202611200101/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(json.utf8))?.build == 202611200101, "release")
        let evil = #"{"tag_name":"v9-209912312359","assets":[{"name":"Fusebox.zip","browser_download_url":"http://evil.example/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(evil.utf8)) == nil, "alleen https van GitHub")
        let other = #"{"tag_name":"v9-209912312359","assets":[{"name":"Fusebox.zip","browser_download_url":"https://github.com/someone/else/releases/download/v9/Fusebox.zip"}]}"#
        precondition(parseRelease(Data(other.utf8), repo: "x/y") == nil && parseRelease(Data(json.utf8), repo: "x/y") != nil, "alleen eigen repo")
        rollbackSelfTest()
        print("updater ok")
    }

    /// Terugdraaien: beslissing, marker, en back-up → bevestigen / terugzetten in een tijdelijke map (nooit de echte paden).
    nonisolated static func rollbackSelfTest() {
        precondition(!shouldRollBack(markerAge: 30, backupExists: true, markerBuild: 7, currentBuild: 7), "marker te vers")
        precondition(shouldRollBack(markerAge: 180, backupExists: true, markerBuild: 7, currentBuild: 7), "3 min, zelfde build")
        precondition(!shouldRollBack(markerAge: 180, backupExists: false, markerBuild: 7, currentBuild: 7), "geen back-up")
        precondition(!shouldRollBack(markerAge: 180, backupExists: true, markerBuild: 8, currentBuild: 7), "andere build")
        precondition(failedInfo("v0.14-beta-202611241200\n").map { $0 == (202611241200, "0.14 beta") } == true
                     && failedInfo("1").map { $0 == (1, "") } == true && failedInfo("rommel") == nil, "update-failed")

        let fm = FileManager.default, root = fm.temporaryDirectory.appendingPathComponent("fusebox-selftest-\(UUID().uuidString)")
        defer { try? fm.removeItem(at: root) }
        let p = Paths(app: root.appendingPathComponent("cur/Fusebox.app"), dir: root.appendingPathComponent("data"))
        let x = p.app.appendingPathComponent("x"), temp = root.appendingPathComponent("tmp")
        func put(_ text: String) { try! fm.createDirectory(at: p.app, withIntermediateDirectories: true); try! Data(text.utf8).write(to: x) }
        func marker(_ build: Int) { try! JSONEncoder().encode(Marker(build: build, from: 6, at: 1_795_000_000)).write(to: p.marker) }
        let exists = { (u: URL) in fm.fileExists(atPath: u.path) }

        // Zoals replaceAndRelaunch: back-up van "old", app wordt "new", marker erbij
        put("old"); try! backUp(p); put("new"); marker(7)
        precondition(readMarker(p) == Marker(build: 7, from: 6, at: 1_795_000_000), "marker heen en terug")
        precondition((try? JSONDecoder().decode(Marker.self, from: Data(#"{"build": 7, "from": 6, "at": 1795000000}"#.utf8))) == readMarker(p), "marker-JSON")

        // Bevestigen: niet door een andere build; wel door de nieuwe (marker, back-up en uitgepakte resten weg)
        try! fm.createDirectory(at: temp.appendingPathComponent("fusebox-update-rest"), withIntermediateDirectories: true)
        confirm(p, build: 6, temp: temp)
        precondition(exists(p.marker) && exists(p.backup), "andere build bevestigt niet")
        confirm(p, build: 7, temp: temp)
        precondition(!exists(p.marker) && !exists(p.backup) && (try! fm.contentsOfDirectory(atPath: temp.path)).isEmpty, "bevestigd")

        // Terugzetten: ongeldige back-up → niets veranderd; geldige → "old" terug, slechte kopie en marker weg, update-failed voor de oude versie
        put("old"); try! backUp(p); put("new"); marker(7)
        precondition(!restore(p, badBuild: 7, verify: { _ in false }) && (try! String(contentsOf: x, encoding: .utf8)) == "new" && exists(p.backup), "ongeldige back-up")
        precondition(restore(p, badBuild: 7, verify: { _ in true }) && (try! String(contentsOf: x, encoding: .utf8)) == "old", "teruggezet")
        precondition(!exists(p.marker) && !exists(p.backup.deletingLastPathComponent()), "opgeruimd")
        precondition(takeFailed(p).map { $0 == (7, "") } == true && !exists(p.failed) && takeFailed(p) == nil, "update-failed één keer")

        // Na de waakhond (script): marker staat er nog, update-failed bevat de tag, slechte kopie in update-previous → de oude versie ruimt op
        marker(7); try! Data("v0.14-beta-202611241200".utf8).write(to: p.failed)
        try! fm.createDirectory(at: p.backup.appendingPathExtension("bad"), withIntermediateDirectories: true)
        precondition(takeFailed(p).map { $0 == (202611241200, "0.14 beta") } == true && !exists(p.marker), "waakhond: marker weg")
        confirm(p, build: 6, temp: temp)
        precondition(!exists(p.backup.deletingLastPathComponent()), "waakhond: slechte kopie weg")
    }
}
