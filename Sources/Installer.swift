import AppKit

// MARK: - Bijwerken i.p.v. opnieuw installeren
//
// Opent iemand een nieuwe Fusebox (bijv. uit Downloads) terwijl er al één in Programma's staat,
// dan vervangt deze de oude en start vanaf daar. Instellingen en geschiedenis blijven (zelfde bundle-id).
// Staat er nog geen, dan biedt hij aan zichzelf naar Programma's te verplaatsen.

enum Installer {
    /// Aanroepen bij het opstarten (vóór er vensters zijn). Doet niets als de app al in een Programma's-map staat.
    @MainActor static func checkOnLaunch() {
        let me = Bundle.main.bundleURL.resolvingSymlinksInPath()
        guard !isInApplications(me) else { return }
        let fm = FileManager.default
        let installed = installedCopies().first
        let myBuild = build(of: me)

        if let installed {
            let theirBuild = build(of: installed)
            if theirBuild > myBuild {
                // De geïnstalleerde is nieuwer: die openen en deze sluiten
                if ask("A newer Fusebox is already installed",
                       "This copy (\(version(of: me))) is older than the one in Applications (\(version(of: installed))).",
                       ok: "Open installed version") {
                    open(installed)
                }
                return
            }
            if theirBuild == myBuild { open(installed); return }   // zelfde versie: gewoon de geïnstalleerde openen
            guard ask("Update Fusebox?",
                      "Replace Fusebox \(version(of: installed)) in Applications with this version (\(version(of: me))). Your settings and history stay.",
                      ok: "Update") else { return }
            // Draaiende oude versie netjes sluiten (zelfde bundle-id, ander proces)
            for app in NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            where app.processIdentifier != ProcessInfo.processInfo.processIdentifier { app.terminate() }
            guard waitForOtherInstancesToQuit() else {
                fail("The old Fusebox is still running. Quit it (⌘Q) and open this one again.")
                return
            }
            do {
                try fm.trashItem(at: installed, resultingItemURL: nil)          // oude naar de Prullenmand (terug te halen)
                try fm.copyItem(at: me, to: installed)
                clearQuarantine(installed)
                open(installed)
            } catch {
                fail("Couldn't update Fusebox: \(error.localizedDescription). Drag Fusebox into Applications yourself.")
            }
            return
        }

        // Nog niet geïnstalleerd: aanbieden om naar Programma's te verplaatsen
        guard ask("Move Fusebox to Applications?",
                  "Then it's easy to find, and future updates replace it automatically.",
                  ok: "Move to Applications") else { return }
        let target = applicationsDir().appendingPathComponent(me.lastPathComponent)
        do {
            try fm.copyItem(at: me, to: target)
            clearQuarantine(target)
            if !me.path.contains("/AppTranslocation/") { try? fm.trashItem(at: me, resultingItemURL: nil) }   // origineel opruimen als dat kan
            open(target)
        } catch {
            fail("Couldn't move Fusebox: \(error.localizedDescription). Drag it into Applications yourself.")
        }
    }

    // MARK: Hulpjes

    static func isInApplications(_ url: URL) -> Bool {
        let p = url.path
        return p.hasPrefix("/Applications/") || p.hasPrefix(NSHomeDirectory() + "/Applications/")
    }

    /// Andere kopieën met dezelfde bundle-id in /Applications of ~/Applications (niet deze zelf).
    static func installedCopies() -> [URL] {
        let me = Bundle.main.bundleURL.resolvingSymlinksInPath().path
        let fromLS = NSWorkspace.shared.urlsForApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
        let guesses = ["/Applications/Fusebox.app", NSHomeDirectory() + "/Applications/Fusebox.app"].map { URL(fileURLWithPath: $0) }
        var seen = Set<String>()
        return (fromLS + guesses).filter { url in
            let p = url.resolvingSymlinksInPath().path
            guard p != me, isInApplications(url), FileManager.default.fileExists(atPath: p), seen.insert(p).inserted else { return false }
            return Bundle(url: url)?.bundleIdentifier == Bundle.main.bundleIdentifier
        }
    }

    /// /Applications als we daar mogen schrijven (beheerders), anders ~/Applications.
    static func applicationsDir() -> URL {
        let system = URL(fileURLWithPath: "/Applications")
        if FileManager.default.isWritableFile(atPath: system.path) { return system }
        let home = URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Applications")
        try? FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        return home
    }

    /// Bouwnummer (CFBundleVersion, door build.sh als datum-tijd gezet); oudere builds hebben "1".
    static func build(of url: URL) -> Int {
        Int(Bundle(url: url)?.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "") ?? 0
    }
    static func version(of url: URL) -> String {
        let b = Bundle(url: url)
        let v = b?.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        return "\(v) (\(b?.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"))"
    }

    /// Quarantaine-vlag van onze eigen kopie halen: de gebruiker heeft deze versie al bewust geopend.
    private static func clearQuarantine(_ url: URL) {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/xattr")
        p.arguments = ["-dr", "com.apple.quarantine", url.path]
        try? p.run(); p.waitUntilExit()
    }

    /// true als alle andere Fuseboxen weg zijn. De runloop laten draaien (niet Thread.sleep): de lijst met
    /// draaiende apps ververst alleen dan. Na ~5 s opgeven i.p.v. een draaiende app weg te gooien.
    @MainActor private static func waitForOtherInstancesToQuit() -> Bool {
        let id = Bundle.main.bundleIdentifier ?? "", me = ProcessInfo.processInfo.processIdentifier
        for _ in 0..<50 {
            if NSRunningApplication.runningApplications(withBundleIdentifier: id).allSatisfy({ $0.processIdentifier == me }) { return true }
            RunLoop.current.run(until: Date().addingTimeInterval(0.1))
        }
        return false
    }

    @MainActor private static func open(_ url: URL) {
        // Draait die al? Dan alleen naar voren halen en deze kopie sluiten (geen tweede Fusebox die dubbel logt/bijwerkt)
        let target = url.resolvingSymlinksInPath().path
        if let running = NSRunningApplication.runningApplications(withBundleIdentifier: Bundle.main.bundleIdentifier ?? "")
            .first(where: { $0.processIdentifier != ProcessInfo.processInfo.processIdentifier && $0.bundleURL?.resolvingSymlinksInPath().path == target }) {
            running.activate()
            NSApp.terminate(nil)
            return
        }
        let cfg = NSWorkspace.OpenConfiguration()
        cfg.createsNewApplicationInstance = true   // anders denkt macOS dat deze (zelfde bundle-id) al "de" Fusebox is
        NSWorkspace.shared.openApplication(at: url, configuration: cfg) { _, _ in
            DispatchQueue.main.async { NSApp.terminate(nil) }
        }
    }

    @MainActor private static func ask(_ title: String, _ text: String, ok: String) -> Bool {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: ok)
        a.addButton(withTitle: "Not now")
        NSApp.activate()
        return a.runModal() == .alertFirstButtonReturn
    }

    @MainActor private static func fail(_ text: String) {
        let a = NSAlert(); a.messageText = "Fusebox"; a.informativeText = text; a.runModal()
    }

    /// Zelftest voor de pure delen.
    static func selfTest() {
        precondition(isInApplications(URL(fileURLWithPath: "/Applications/Fusebox.app")), "apps")
        precondition(isInApplications(URL(fileURLWithPath: NSHomeDirectory() + "/Applications/Fusebox.app")), "home apps")
        precondition(!isInApplications(URL(fileURLWithPath: NSHomeDirectory() + "/Downloads/Fusebox.app")), "downloads")
        print("installer ok")
    }
}
