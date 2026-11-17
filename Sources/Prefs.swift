import AppKit

// MARK: - Instellingen: één plek met alle sleutels en standaardwaarden.
// `register()` draait als eerste in Entry.main; daarna is dit de bron van waarheid voor
// @AppStorage én UserDefaults-lezers. Nieuwe instelling = één regel hier.
// Opgeslagen keuzes zijn altijd korte vaste ids (veilig om te delen), nooit de tekst die de gebruiker ziet.

enum Prefs {
    static let defaults: [String: Any] = {
        var d: [String: Any] = [
            // Weergave
            "theme": "instrument", "appearanceMode": "system", "accent": "", "textScale": 1.0, "numberStyle": "fade",
            "page": "live", "settingsTab": "profile",
            "dockIcon": true, "keepRunning": true, "autoUpdate": true, "onTop": false,
            "hotkeyCode": 3, "hotkeyMods": 6400, "hotkeyLabel": "⌃⌥⌘F",   // F met ⌃⌥⌘ (Carbon: 4096+2048+256)
            // Live-pagina
            "metrics": Metric.defaultOrder, "metricsHidden": "",
            "graphs": "auto", "shortMinutes": 5, "absolute": "ram,ssd", "showAdvice": false, "netBits": false, "perCore": false,
            "warnCPU": 85.0, "warnTemp": 95.0, "warnPing": 100.0,
            // Menubalk
            "menubarMetrics": "cpu", "menubarMetricsHidden": "", "menubarStyle": "text", "menubarSplit": false,
            "menuStyle": "menu", "menuParts": "status,values,top,links",
            // Meldingen
            "notify": true, "alertCooldown": 30,
            // Gegevens en privacy
            "keepDays": 30, "pingHost": "1.1.1.1",
            // Ventilatoren (alleen Macs met ventilator): "auto" | "curve" | "max", curve = "°C:%,…"
            "fanMode": "auto", "fanCurve": FanCurve.balanced,
            "setupDone": false,
        ]
        for a in Alert.allCases {
            d[a.key("on")] = a.start.on; d[a.key("limit")] = a.start.limit; d[a.key("min")] = a.start.minutes
        }
        return d
    }()

    /// Horen bij deze Mac, gaan niet mee bij importeren.
    static let local: Set<String> = ["pingHost", "setupDone", "page", "settingsTab", "fanMode", "fanCurve"]

    static func register(_ d: UserDefaults = .standard) { d.register(defaults: defaults) }

    /// Alle bekende instellingen als JSON (om te delen of als momentopname).
    static func export(_ d: UserDefaults = .standard) -> Data {
        let values = defaults.keys.reduce(into: [String: Any]()) { $0[$1] = d.object(forKey: $1) }
        return (try? JSONSerialization.data(withJSONObject: values, options: [.prettyPrinted, .sortedKeys])) ?? Data()
    }

    /// Vertrouwensgrens: alleen bekende sleutels, met hetzelfde type als de standaardwaarde, teksten ≤ 200 tekens.
    /// Geeft terug hoeveel instellingen zijn overgenomen.
    @discardableResult
    static func apply(_ values: [String: Any], skipping: Set<String> = [], to d: UserDefaults = .standard) -> Int {
        var n = 0
        for (key, raw) in values where !skipping.contains(key) {
            guard let def = defaults[key], let v = coerce(raw, like: def) else { continue }
            d.set(v, forKey: key)
            n += 1
        }
        return n
    }

    /// Snapshot (uit `export`) terugzetten.
    static func restore(_ data: Data) {
        if let v = try? JSONSerialization.jsonObject(with: data) as? [String: Any] { apply(v) }
    }

    private static func coerce(_ v: Any, like def: Any) -> Any? {
        let num = v as? NSNumber, isBool = num.map { CFGetTypeID($0) == CFBooleanGetTypeID() } ?? false
        switch def {
        case is Bool: return isBool ? num!.boolValue : nil
        case is Int: return isBool ? nil : num.flatMap { Int(exactly: $0.doubleValue) }
        case is Double: return isBool ? nil : num.map(\.doubleValue).flatMap { $0.isFinite ? $0 : nil }
        case is String: return (v as? String).flatMap { $0.count <= 200 ? $0 : nil }
        default: return nil
        }
    }

    /// Instellingen van een vriend: alles wat bekend is, behalve wat bij deze Mac hoort (zoals de ping-server).
    static func importFile(_ url: URL, to d: UserDefaults = .standard) throws -> Int {
        let data = try Data(contentsOf: url)
        guard data.count < 100_000, let values = try JSONSerialization.jsonObject(with: data) as? [String: Any] else {
            throw CocoaError(.fileReadCorruptFile)
        }
        return apply(values, skipping: local, to: d)
    }

    static func reset(_ keys: some Sequence<String>, in d: UserDefaults = .standard) { keys.forEach(d.removeObject) }

    /// Dock-icoon aan/uit; nooit Dock én menubalk tegelijk weg (dan is de app onvindbaar).
    @MainActor static func applyDock(_ d: UserDefaults = .standard) {
        if !d.bool(forKey: "dockIcon") && d.string(forKey: "menubarStyle") == "off" { d.set(true, forKey: "dockIcon") }
        let policy: NSApplication.ActivationPolicy = d.bool(forKey: "dockIcon") ? .regular : .accessory
        guard NSApp.activationPolicy() != policy else { return }
        NSApp.setActivationPolicy(policy)
        NSApp.activate()
    }

    // MARK: Profielen

    struct Preset: Identifiable {
        let id: String, title: String, icon: String, blurb: String
        let values: [String: Any]
    }

    static let presets: [Preset] = [
        Preset(id: "simple", title: "Simple", icon: "leaf", blurb: "Big, calm, with advice", values: [
            "theme": "liquid", "textScale": 1.15, "metrics": "cpu,ram,ssd,battery", "graphs": "never",
            "showAdvice": true, "menubarStyle": "icon"]),
        Preset(id: "gamer", title: "Gamer", icon: "gamecontroller", blurb: "GPU and ping up front", values: [
            "theme": "instrument", "metrics": "cpu,gpu,ram,net", "graphs": "auto", "shortMinutes": 5,
            "menubarStyle": "compact", "menubarMetrics": "cpu,gpu", "netBits": true, "warnPing": 60.0]),
        Preset(id: "nerd", title: "Nerd", icon: "terminal", blurb: "Everything, in real values", values: [
            "theme": "terminal", "metrics": "cpu,gpu,ram,net,ssd,io,battery", "absolute": "cpu,ram,ssd",
            "menubarStyle": "graph", "menubarMetrics": "cpu,ram,net"]),
        Preset(id: "laptop", title: "Laptop", icon: "laptopcomputer", blurb: "Battery first, alert when almost empty", values: [
            "theme": "minimal", "metrics": "battery,cpu,ram,ssd", "menubarStyle": "text",
            "menubarMetrics": "battery,cpu", Alert.battery.key("on"): true]),
    ]

    /// Profiel toepassen: eerst alles terug wat een profiel kan raken, dan dit profiel (loggen, bewaren en ping blijven staan).
    static func applyPreset(_ p: Preset, to d: UserDefaults = .standard) {
        reset(Set(presets.flatMap(\.values.keys)), in: d)
        apply(p.values, to: d)
    }

    // MARK: Delen

    @MainActor static func shareDialog() {
        let p = NSSavePanel()
        p.nameFieldStringValue = "Fusebox-settings.json"
        p.allowedContentTypes = [.json]
        guard p.runModal() == .OK, let url = p.url else { return }
        try? export().write(to: url)
    }

    /// Geeft een zin terug voor de gebruiker, of nil als er geannuleerd is.
    @MainActor static func importDialog() -> String? {
        let p = NSOpenPanel()
        p.allowedContentTypes = [.json]
        guard p.runModal() == .OK, let url = p.url else { return nil }
        guard let n = try? importFile(url) else { return "Fusebox can't read this file." }
        return "\(n) settings imported"
    }

    // MARK: Zelftest

    static func selfTest() {
        func fresh(_ name: String) -> UserDefaults {
            let d = UserDefaults(suiteName: name)!
            d.removePersistentDomain(forName: name)
            register(d)
            return d
        }
        let a = fresh("meterkast.selftest.a"), b = fresh("meterkast.selftest.b")
        a.set("terminal", forKey: "theme"); a.set(70.0, forKey: "warnCPU"); a.set(false, forKey: "notify"); a.set(7, forKey: "keepDays")
        let json = try! JSONSerialization.jsonObject(with: export(a)) as! [String: Any]
        precondition(apply(json, to: b) == defaults.count, "export → apply")
        precondition(b.string(forKey: "theme") == "terminal" && b.double(forKey: "warnCPU") == 70
                     && !b.bool(forKey: "notify") && b.integer(forKey: "keepDays") == 7, "round-trip")
        precondition(apply(["warnCPU": "x", "theme": 3], to: b) == 0, "verkeerd type")
        precondition(apply(["notify": 1, "keepDays": 2.5, "nope": "x", "theme": String(repeating: "x", count: 201)], to: b) == 0, "vertrouwensgrens")
        let file = FileManager.default.temporaryDirectory.appendingPathComponent("meterkast-selftest.json")
        try! JSONSerialization.data(withJSONObject: ["pingHost": "evil.example", "theme": "minimal"]).write(to: file)
        precondition((try? importFile(file, to: b)) == 1 && b.string(forKey: "pingHost") == "1.1.1.1", "import negeert pingHost")
        try? FileManager.default.removeItem(at: file)
        for p in presets {
            precondition(apply(p.values, to: b) == p.values.count, "profiel \(p.id)")
            precondition(p.values.keys.allSatisfy { !["keepDays", "pingHost"].contains($0) }, "profiel \(p.id) raakt privacy")
        }
        applyPreset(presets[0], to: b)
        precondition(b.string(forKey: "menubarMetrics") == "cpu" && b.string(forKey: "theme") == "liquid", "profiel wisselen")
        a.removePersistentDomain(forName: "meterkast.selftest.a"); b.removePersistentDomain(forName: "meterkast.selftest.b")
        print("prefs: ok (\(defaults.count) sleutels)")
    }
}
