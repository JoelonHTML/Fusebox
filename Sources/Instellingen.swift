import SwiftUI
import ServiceManagement

// MARK: - Instellingen: tabjes links, per onderwerp één pagina.
// Nieuwe instelling? Sleutel + standaard in `Prefs.defaults`, dan een regel in de tab waar hij hoort.
// De tab-views zijn niet privé: het stappenplan (Stappenplan.swift) gebruikt ze ook.

enum SettingsTab: String, CaseIterable, Identifiable {
    case profile, look, live, menubar, alerts, fans, data, about
    /// "Fans" alleen op Macs met ventilatoren.
    static var shown: [SettingsTab] { allCases.filter { $0 != .fans || Fans.count > 0 } }
    var id: String { rawValue }
    var title: String {
        switch self {
        case .profile: "Profile"
        case .look: "Appearance"
        case .live: "Live page"
        case .menubar: "Menu bar"
        case .alerts: "Notifications"
        case .fans: "Fans"
        case .data: "Privacy"
        case .about: "About"
        }
    }
    var icon: String {
        switch self {
        case .profile: "person.crop.circle"
        case .look: "paintbrush"
        case .live: "gauge.with.needle"
        case .menubar: "menubar.rectangle"
        case .alerts: "bell"
        case .fans: "fan"
        case .data: "lock.shield"
        case .about: "info.circle"
        }
    }
}

struct SettingsView: View {
    @AppStorage("settingsTab") private var tab: SettingsTab = .profile
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system

    var body: some View {
        NavigationSplitView {
            List(SettingsTab.shown, selection: Binding(get: { tab }, set: { if let t = $0 { tab = t } })) { t in
                Label(t.title, systemImage: t.icon).tag(t)
            }
            .navigationSplitViewColumnWidth(min: 200, ideal: 200, max: 220)
            .toolbar(removing: .sidebarToggle)
        } detail: {
            Form { page }
                .formStyle(.grouped)
                .navigationTitle(tab.title)
        }
        .frame(width: 740, height: 560)
        .preferredColorScheme(mode.scheme)   // Settings volgt Licht/Donker van Fusebox, niet alleen van macOS
    }

    @ViewBuilder private var page: some View {
        switch tab {
        case .profile: ProfileSettings()
        case .look: LookSettings()
        case .live: LiveSettings()
        case .menubar: MenubarSettings()
        case .alerts: AlertSettings()
        case .fans: FanSettings()
        case .data: DataSettings()
        case .about: AboutSettings()
        }
    }
}

// MARK: - Bouwstenen

/// Komma-lijst als aan/uit per id: `member($raw, "cpu")`.
func member(_ raw: Binding<String>, _ id: String) -> Binding<Bool> {
    Binding(
        get: { raw.wrappedValue.split(separator: ",").contains(Substring(id)) },
        set: { on in
            var list = raw.wrappedValue.split(separator: ",").map(String.init).filter { $0 != id }
            if on { list.append(id) }
            raw.wrappedValue = list.joined(separator: ",")
        })
}

/// Na profiel, import of terugzetten: wat niet vanzelf meeloopt (Dock) bijwerken.
@MainActor func prefsChanged(_ meter: Meter, open: OpenWindowAction, dismiss: DismissWindowAction) {
    Prefs.applyDock()
}

/// Aan/uit en volgorde van metingen, opgeslagen als komma-lijst onder `key` (uitgeschakelde volgorde onder `key`+"Hidden").
struct MetricList: View {
    @AppStorage private var raw: String
    @AppStorage private var hidden: String
    private let key: String, limit: Int

    init(_ key: String, limit: Int = 99) {
        self.key = key; self.limit = limit
        _raw = AppStorage(wrappedValue: Prefs.defaults[key] as? String ?? "", key)
        _hidden = AppStorage(wrappedValue: "", key + "Hidden")
    }

    /// Eerst de ingeschakelde in hun volgorde, dan de rest.
    private var order: [Metric] {
        let on = Metric.enabled(raw)
        let rest = Metric.enabled(hidden).filter { !on.contains($0) }
        return on + rest + Metric.offered.filter { !on.contains($0) && !rest.contains($0) }
    }

    var body: some View {
        let on = Metric.enabled(raw)
        List {
            ForEach(order) { m in
                Toggle(m.title, isOn: Binding(
                    get: { on.contains(m) },
                    set: { v in
                        var list = on.filter { $0 != m }
                        if v { list.insert(m, at: min(order.firstIndex(of: m) ?? list.count, list.count)) }
                        raw = list.map(\.rawValue).joined(separator: ",")
                    }))
                .disabled(!on.contains(m) && on.count >= limit)
            }
            .onMove { from, to in
                var all = order
                all.move(fromOffsets: from, toOffset: to)
                raw = all.filter(on.contains).map(\.rawValue).joined(separator: ",")
                hidden = all.filter { !on.contains($0) }.map(\.rawValue).joined(separator: ",")
            }
        }
        .frame(height: CGFloat(Metric.offered.count) * 28 + 12)
        Button("Restore defaults") { Prefs.reset([key, key + "Hidden"]) }
    }
}

struct LoginToggle: View {
    @State private var on = false
    @State private var note: String?
    var body: some View {
        Toggle("Open at login", isOn: Binding(get: { on }, set: { v in
            do { try v ? SMAppService.mainApp.register() : SMAppService.mainApp.unregister(); note = nil }
            catch { note = "Didn't work: \(error.localizedDescription)" }
            read()
        }))
        .onAppear(perform: read)
        if let note { Text(note).font(.caption).foregroundStyle(.secondary) }
    }
    private func read() {
        on = SMAppService.mainApp.status == .enabled
        if SMAppService.mainApp.status == .requiresApproval { note = "Allow Fusebox in System Settings › General › Login Items." }
    }
}

struct DockToggle: View {
    @AppStorage("dockIcon") private var dock = true
    @AppStorage("menubarStyle") private var menubar: MenubarStyle = .text
    var body: some View {
        Toggle("Icon in the Dock", isOn: $dock)
            .disabled(dock && menubar == .off)
            .help(menubar == .off ? "Turn on the menu bar first, otherwise you can't find Fusebox anymore" : "")
            .onChange(of: dock) { Prefs.applyDock() }
    }
}

struct PingToggle: View {
    @AppStorage("pingHost") private var host = "1.1.1.1"
    var body: some View {
        Toggle("Measure internet latency (ping)", isOn: Binding(get: { !host.isEmpty }, set: { host = $0 ? "1.1.1.1" : "" }))
    }
}

struct KeepDaysPicker: View {
    @AppStorage("keepDays") private var keepDays = 30
    var body: some View {
        Picker("Keep logs", selection: $keepDays) {
            Text("1 week").tag(7); Text("30 days").tag(30); Text("3 months").tag(90); Text("1 year").tag(365); Text("Forever").tag(0)
        }
    }
}

struct PresetCard: View {
    let preset: Prefs.Preset
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            VStack(spacing: 4) {
                Image(systemName: preset.icon).font(.title2)
                Text(preset.title).font(.headline)
                Text(preset.blurb).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center).lineLimit(2, reservesSpace: true)
                Divider().padding(.vertical, 2)
                Text(changes).font(.caption2).foregroundStyle(.tertiary).multilineTextAlignment(.center).lineLimit(5, reservesSpace: true)
            }
            .frame(maxWidth: .infinity).padding(10)
            .background(.quaternary.opacity(0.5), in: .rect(cornerRadius: 10))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help("Changes: " + changes)
    }

    /// Wat dit profiel aanpast, in een paar woorden ("Liquid Glass · large text · no graphs").
    private var changes: String {
        let v = preset.values
        var out: [String] = []
        if let t = v["theme"] as? String { out.append(Theme.named(t).name) }
        if let x = v["textScale"] as? Double, x > 1 { out.append(x >= 1.3 ? "extra large text" : "large text") }
        if let m = v["metrics"] as? String { out.append(Metric.enabled(m).map(\.abbr).joined(separator: " ")) }
        if v["graphs"] as? String == "never" { out.append("no graphs") }
        if v["showAdvice"] as? Bool == true { out.append("advice line") }
        if v["absolute"] != nil { out.append("real values") }
        if v["netBits"] as? Bool == true { out.append("Mbit/s") }
        if let mb = (v["menubarStyle"] as? String).flatMap(MenubarStyle.init) {
            out.append(mb == .icon ? "menu bar icon" : "menu bar " + mb.title.split(separator: " ")[0].lowercased())
        }
        if v[Alert.battery.key("on")] as? Bool == true { out.append("battery alert") }
        return out.joined(separator: " · ")
    }
}

// MARK: - Tabs

struct ProfileSettings: View {
    @EnvironmentObject var meter: Meter
    @Environment(\.openWindow) private var openWindow
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var message: String?
    @State private var confirmReset = false

    var body: some View {
        Section("Start with a profile (you can still change everything afterwards)") {
            HStack(spacing: 8) {
                ForEach(Prefs.presets) { p in
                    PresetCard(preset: p) { Prefs.applyPreset(p); done("Profile \(p.title) is on") }
                }
            }
            if let message { Text(message).font(.callout).foregroundStyle(.secondary) }
        }
        Section("Share with friends") {
            HStack {
                Button("Share my settings…") { Prefs.shareDialog() }
                Button("Import…") { if let m = Prefs.importDialog() { done(m) } }
            }
            Text("Your ping server is not included.").font(.caption).foregroundStyle(.secondary)
        }
        Section {
            Button("Run setup again") { openWindow(id: "setup"); NSApp.activate() }
            Button("Reset everything…", role: .destructive) { confirmReset = true }
                .confirmationDialog("Reset all settings to default?", isPresented: $confirmReset) {
                    Button("Reset", role: .destructive) {
                        Prefs.reset(Prefs.defaults.keys.filter { $0 != "setupDone" })
                        done("Everything is back to default")
                    }
                } message: { Text("Your logs are kept.") }
        }
    }

    private func done(_ m: String) {
        message = m
        prefsChanged(meter, open: openWindow, dismiss: dismissWindow)
    }
}

struct LookSettings: View {
    @AppStorage("onTop") private var onTop = false
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system

    var body: some View {
        Section("Style") {
            Picker("Mode", selection: $mode) { ForEach(AppearanceMode.allCases) { Text($0.title).tag($0) } }
                .pickerStyle(.segmented)
            ThemeTiles()
        }
        Section("Text size") { TextSizeCards() }
        Section("Font") { FontCards() }
        Section {
            AccentPicker()
            NumberStylePicker()
        }
        Section {
            Toggle("Always on top", isOn: $onTop)
            KeepRunningToggle()
            HotkeyRecorder()
            DockToggle()
            LoginToggle()
        } header: {
            Text("Window")
        } footer: {
            Text("Fusebox always opens on Live, at the size you last gave that window.").font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct AccentPicker: View {
    @AppStorage("font") private var font = "style"   // lettertype gewijzigd → meteen opnieuw tekenen (gelezen in body)
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("accent") private var accent = ""
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @Environment(\.colorScheme) private var system
    var body: some View {
        let _ = font
        let t = mode.apply(Theme.resolved(themeID), system: system)
        HStack {
            ColorPicker("Accent color", selection: Binding(get: { Color(hex: accent) ?? Theme.named(themeID).accent }, set: { accent = $0.hex }),
                        supportsOpacity: false)
            // Voorbeeldje: waar de accentkleur terugkomt (label bij Instrument, grafiekjes).
            HStack(spacing: 6) {
                Text("CPU").font(t.label(11)).foregroundStyle(t.id == "instrument" ? t.accent : t.onRow(true))
                Spark(values: PreviewData.history.suffix(12).map(\.cpu), top: 60, fill: 1, closed: false)
                    .stroke(t.rowStyle == .blocks ? .white : t.accent, lineWidth: 1.5).frame(width: 44, height: 14)
            }
            .padding(.horizontal, 8).padding(.vertical, 4)
            .background(t.rowStyle == .blocks ? AnyShapeStyle(t.blocks[0]) : t.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), in: .capsule)
            Button("Default") { accent = "" }.disabled(accent.isEmpty)
        }
    }
}

struct LiveSettings: View {
    @AppStorage("graphs") private var graphs: GraphMode = .auto
    @AppStorage("shortMinutes") private var shortMinutes = 5
    @AppStorage("showAdvice") private var showAdvice = false
    @AppStorage("absolute") private var absoluteRaw = "ram,ssd"
    @AppStorage("netBits") private var netBits = false
    @AppStorage("perCore") private var perCore = false
    @AppStorage("warnCPU") private var warnCPU = 85.0
    @AppStorage("warnTemp") private var warnTemp = 95.0
    @AppStorage("warnPing") private var warnPing = 100.0

    var body: some View {
        Section("Preview") { LivePreview() }
        Section("Measurements (drag to change the order)") { MetricList("metrics") }
        Section("Larger window: graphs") {
            GraphCards()
            Picker("Graphs show the last", selection: $shortMinutes) {
                ForEach([1, 5, 10, 15, 30, 60], id: \.self) { Text($0 == 60 ? "hour" : "\($0) min").tag($0) }
            }
            .disabled(graphs == .never)
        }
        Section("Small window") {
            Toggle(isOn: $perCore) {
                Text("Per-core CPU")
                Text("A thin bar per processor core on the CPU card. Large windows always show them.")
            }
            Toggle("Show advice line (\"Your Mac is busy\", \"Memory is filling up\" …)", isOn: $showAdvice)
        }
        Section("Units (or click a number on the Live page)") {
            ForEach([Metric.cpu, .ram, .ssd]) { m in UnitPicker(metric: m) }
            LabeledContent("Internet (NET)") {
                Picker("Internet (NET)", selection: $netBits) { Text("MB/s").tag(false); Text("Mbit/s").tag(true) }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
            }
        }
        Section("Thresholds: orange from here, red 10 higher") {
            threshold("Processor", $warnCPU, 50...100, 5, "%")
            threshold("Temperature", $warnTemp, 70...110, 5, "°C")
            threshold("Latency", $warnPing, 20...300, 10, "ms")
        }
    }

    private func threshold(_ title: String, _ v: Binding<Double>, _ r: ClosedRange<Double>, _ step: Double, _ unit: String) -> some View {
        LabeledContent(title) {
            HStack { Slider(value: v, in: r, step: step); Text("\(Int(v.wrappedValue)) \(unit)").monospacedDigit().frame(width: 56, alignment: .trailing) }
        }
    }
}

/// Voorbeeld van de Live-pagina met vaste waarden: volgt stijl, metingen, eenheden, grafieken, drempels en adviesregel.
struct LivePreview: View {
    @AppStorage("font") private var font = "style"   // lettertype gewijzigd → meteen opnieuw tekenen (gelezen in body)
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("accent") private var accent = ""
    @AppStorage("textScale") private var textScale = 1.0
    @AppStorage("metrics") private var metricsRaw = Metric.defaultOrder
    @AppStorage("graphs") private var graphs: GraphMode = .auto
    @AppStorage("shortMinutes") private var shortMinutes = 5
    @AppStorage("showAdvice") private var showAdvice = false
    @AppStorage("absolute") private var absoluteRaw = "ram,ssd"
    @AppStorage("netBits") private var netBits = false            // Metric.row leest deze zelf; hier alleen om te verversen
    @AppStorage("warnCPU") private var warnCPU = 85.0
    @AppStorage("warnTemp") private var warnTemp = 95.0
    @AppStorage("warnPing") private var warnPing = 100.0
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @Environment(\.colorScheme) private var system

    var body: some View {
        let _ = font
        let t = mode.apply(Theme.resolved(themeID), system: system)
        let ms = Array(Metric.enabled(metricsRaw).prefix(4))
        let absolute = Set(absoluteRaw.split(separator: ",").map(String.init))
        let on = graphs != .never
        let rows = stride(from: 0, to: ms.count, by: 2).map { Array(ms[$0..<min($0 + 2, ms.count)]) }
        VStack(alignment: .leading, spacing: [.lines, .plain].contains(t.rowStyle) ? 0 : 6) {
            if showAdvice {
                HStack(spacing: 6) {
                    Circle().fill(.green).frame(width: 7, height: 7)
                    Text("All calm").font(t.label(12)).foregroundStyle(t.secondary)
                }
                .padding(.bottom, 4)
            }
            ForEach(rows.indices, id: \.self) { r in
                HStack(alignment: .top, spacing: 10) {
                    ForEach(rows[r]) { m in
                        MetricView(data: m.row(PreviewData.now, PreviewData.history, abs: absolute.contains(m.rawValue)),
                                   tier: on ? .m : .s, fill: 1, index: r * 2 + (rows[r].firstIndex(of: m) ?? 0), graphs: on)
                    }
                    if rows[r].count == 1 { Color.clear.frame(height: 1) }
                }
                .frame(height: on ? 84 * t.scale : nil)
            }
            if ms.isEmpty { Text("No measurements on").font(t.label(12)).foregroundStyle(t.secondary) }
            if on {
                HStack { Text(shortMinutes == 60 ? "−1 hour" : "−\(shortMinutes) min"); Spacer(); Text("now") }
                    .font(t.label(9)).foregroundStyle(t.onRow(true))
            }
        }
        .environment(\.theme, t)
        .padding(10)
        .background(t.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), in: .rect(cornerRadius: 10))
        .environment(\.colorScheme, t.scheme ?? system)
        .allowsHitTesting(false)
        Text(on ? "Graphs only appear when the window is big enough." : "Graphs are off: just the numbers.")
            .font(.caption).foregroundStyle(.secondary)
    }
}

struct MenubarSettings: View {
    @AppStorage("menubarSplit") private var split = false
    @AppStorage("menuStyle") private var menuStyle: MenuStyle = .menu
    @AppStorage("menuParts") private var parts = "status,values,top,links"

    var body: some View {
        Section("In the menu bar") {
            MenubarStyleCards()
            Toggle("Each measurement in its own spot (max. 3)", isOn: $split)
        }
        Section("Measurements in the menu bar (max. 4)") { MetricList("menubarMetrics", limit: 4) }
        Section("When you click it") {
            Picker("Type", selection: $menuStyle) { ForEach(MenuStyle.allCases) { Text($0.title).tag($0) } }.pickerStyle(.segmented)
            ForEach(MenuPart.allCases) { p in Toggle(p.title, isOn: member($parts, p.rawValue)) }
        }
    }
}

struct AlertSettings: View {
    @AppStorage("notify") private var notify = true
    @AppStorage("alertCooldown") private var cooldown = 30

    var body: some View {
        Section {
            Toggle("Show notifications", isOn: $notify).onChange(of: notify) { _, on in if on { Notify.ask() } }
            Stepper("Same kind at most once every \(cooldown) min", value: $cooldown, in: 5...240, step: 5).disabled(!notify)
            LabeledContent {
                Button("Send test notification") {
                    Notify.ask()
                    Notify.send(title: "Fusebox update available (test)", body: "This is how Fusebox tells you about a new version.")
                }
                .disabled(!notify)
            } label: {
                Text("Test")
                Text("No notification? Allow Fusebox in System Settings › Notifications.")
            }
        }
        Section("Per kind: limit and how many minutes in a row") {
            ForEach(Alert.allCases) { AlertRow(alert: $0) }
        }
        .disabled(!notify)
        Section {
            Text("If Focus or Do Not Disturb is on, macOS keeps these notifications quiet too.").font(.caption).foregroundStyle(.secondary)
            Link("Open Focus settings", destination: URL(string: "x-apple.systempreferences:com.apple.Focus-Settings.extension")!)
        }
    }
}

struct AlertRow: View {
    let alert: Alert
    @AppStorage private var on: Bool
    @AppStorage private var limit: Double
    @AppStorage private var minutes: Int
    var compact = false

    init(alert: Alert, compact: Bool = false) {
        self.alert = alert; self.compact = compact
        _on = AppStorage(wrappedValue: alert.start.on, alert.key("on"))
        _limit = AppStorage(wrappedValue: alert.start.limit, alert.key("limit"))
        _minutes = AppStorage(wrappedValue: alert.start.minutes, alert.key("min"))
    }

    var body: some View {
        Toggle(isOn: $on) {
            Text(alert.title)
            Text("\(alert.describe(limit)), \(minutes) min in a row")
        }
        .onChange(of: on) { _, v in if v { Notify.ask() } }
        if on && !compact {
            HStack {
                Stepper("Limit: \(alert.describe(limit))", value: $limit, in: alert.range, step: alert.step)
                Spacer()
                Stepper("\(minutes) min", value: $minutes, in: 1...60)
            }
            .font(.callout).padding(.leading, 20)
            NotificationExample(title: alert.title, text: example).padding(.leading, 20)
        }
    }

    /// Echte meldingstekst (Alert.check) met een meetwaarde net over de grens; "One app" heeft een voorbeeld-app nodig.
    private var example: String {
        var s = Sample()
        s.cpu = min(limit + 2, 100); s.temp = limit + 2; s.pressure = 4
        s.diskFreeGB = limit * 0.7; s.pingMs = limit + 40; s.battery = max(limit - 2, 1)
        return alert.check([s], top: nil, limit: limit, minutes: minutes, pingOn: true)
            ?? "Safari has been using \(Int(min(limit + 5, 100)))% processor for \(minutes) min"
    }
}

/// Klein namaak-bericht zoals macOS het toont.
struct NotificationExample: View {
    let title: String, text: String
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 20, height: 20)
            VStack(alignment: .leading, spacing: 1) {
                Text(title).font(.caption.weight(.semibold))
                Text(text).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
        }
        .padding(8)
        .background(.quaternary.opacity(0.5), in: .rect(cornerRadius: 10))
        .accessibilityElement(children: .combine)
        .accessibilityLabel("Example notification: \(title). \(text)")
    }
}

struct DataSettings: View {
    @EnvironmentObject var meter: Meter
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @AppStorage("netPerApp") private var netPerApp = false
    @State private var confirmWipe = false

    var body: some View {
        Section {
            KeepDaysPicker()
            LabeledContent("Folder", value: meter.logDir.path.replacingOccurrences(of: NSHomeDirectory(), with: "~"))
            HStack {
                Button("Open log folder") {
                    try? FileManager.default.createDirectory(at: meter.logDir, withIntermediateDirectories: true)
                    NSWorkspace.shared.open(meter.logDir)
                }
                Button("Delete all logs…", role: .destructive) { confirmWipe = true }
            }
            .confirmationDialog("Delete all logs?", isPresented: $confirmWipe) {
                Button("Delete", role: .destructive) { meter.deleteLogs(olderThan: nil) }
            } message: { Text("All CSV files in the log folder will be removed. This can't be undone.") }
        } header: {
            Text("Measurement history")
        } footer: {
            Text("Fusebox always saves a small file per day on your Mac, so Checkup can look back.").font(.caption).foregroundStyle(.secondary)
        }
        Section("Internet") {
            PingToggle()
            if !pingHost.isEmpty {
                TextField("Measure to", text: $pingHost)
                Text("One short connection to this server every 10 s, port 443. Nothing else goes to the internet.")
                    .font(.caption).foregroundStyle(.secondary)
            } else {
                Text("Off: Fusebox makes no internet connections at all.").font(.caption).foregroundStyle(.secondary)
            }
            Toggle(isOn: $netPerApp) {
                Text("Internet usage per app")
                Text("Download and upload per app on the Processes page, read on this Mac only while that page is open.")
            }
        }
    }
}

struct AboutSettings: View {
    var body: some View {
        Section {
            HStack(spacing: 14) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 56, height: 56)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Fusebox").font(.title3.weight(.semibold))
                    Text("Version \(Updater.currentVersion)").foregroundStyle(.secondary)
                }
            }
            Text("A lightweight activity monitor for your Mac: live measurements, apps and advice.").foregroundStyle(.secondary)
        }
        UpdateSection()
        ChangelogSection()
    }
}

/// Wat er nieuw is per versie: de release-notes van GitHub.
struct ChangelogSection: View {
    @ObservedObject private var updater = Updater.shared

    var body: some View {
        Section("What's new") {
            switch updater.changelog {
            case nil: Text("Loading…").foregroundStyle(.secondary)
            case let list? where list.isEmpty: Text("Couldn't load the changelog. Check your internet connection.").foregroundStyle(.secondary)
            case let list?:
                ForEach(list) { r in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack(spacing: 6) {
                            Text(r.title).font(.headline)
                            if Updater.buildNumber(in: r.id) == Updater.currentBuild {
                                Text("Installed").font(.caption.weight(.medium)).padding(.horizontal, 6).padding(.vertical, 1)
                                    .background(.tint.opacity(0.15), in: .capsule)
                            }
                            Spacer()
                            if let d = r.date { Text(d, format: .dateTime.day().month().year()).font(.caption).foregroundStyle(.secondary) }
                        }
                        if !r.notes.isEmpty { Text(.init(r.notes)).foregroundStyle(.secondary).textSelection(.enabled) }
                    }
                    .padding(.vertical, 2)
                }
            }
        }
        .task { updater.loadChangelog() }
    }
}

/// Updates via GitHub Releases: automatisch (standaard) of met de hand.
struct UpdateSection: View {
    @ObservedObject private var updater = Updater.shared
    @AppStorage("autoUpdate") private var auto = true

    var body: some View {
        Section("Updates") {
            LabeledContent("Installed", value: Updater.currentVersion)
            Toggle(isOn: $auto) {
                Text("Update automatically")
                Text("Fusebox checks GitHub every 15 minutes, tells you when there is a new version, installs it and restarts. Your settings stay.")
            }
            HStack {
                Button("Check for updates") { updater.check(userInitiated: true) }
                    .disabled(updater.state == .checking || updater.state == .installing || Updater.repo == nil)
                Spacer()
                Text(status).font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private var status: String {
        switch updater.state {
        case .idle: updater.lastCheck.map { "Last checked \($0.formatted(date: .omitted, time: .shortened))" } ?? ""
        case .checking: "Checking…"
        case .upToDate: "You're up to date"
        case .available(let tag): "Fusebox \(Updater.pretty(tag)) available"
        case .installing: "Installing…"
        case .failed(let why): why
        }
    }
}

struct NumberStylePicker: View {
    @AppStorage("font") private var font = "style"   // lettertype gewijzigd → meteen opnieuw tekenen (gelezen in body)
    @AppStorage("numberStyle") private var style: NumberStyle = .fade
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @Environment(\.colorScheme) private var system
    var body: some View {
        let _ = font
        let t = mode.apply(Theme.resolved(themeID), system: system)
        LabeledContent("Number changes") {
            HStack(spacing: 12) {
                Picker("Number changes", selection: $style) { ForEach(NumberStyle.allCases) { Text($0.title).tag($0) } }
                    .labelsHidden().pickerStyle(.segmented)
                // Demo: alleen zichtbaar = alleen actief; TimelineView tikt niet als Instellingen dicht is.
                TimelineView(.periodic(from: .now, by: 1.5)) { ctx in
                    let n = [17, 23, 9, 41, 34, 12][Int(ctx.date.timeIntervalSinceReferenceDate / 1.5) % 6]
                    ChangingText(text: "\(n)%").font(t.number(20)).foregroundStyle(t.onRow())
                        .frame(width: 56, alignment: .trailing)
                }
                .padding(.horizontal, 8).padding(.vertical, 2)
                .background(t.rowStyle == .blocks ? AnyShapeStyle(t.blocks[0]) : t.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), in: .rect(cornerRadius: 8))
                .accessibilityHidden(true)
            }
        }
        .help("Roll: digits flip from top to bottom · Fade: a short crossfade · Instant: no animation")
    }
}

/// Venster dicht → toch blijven meten (alleen ⌘Q stopt de app).
struct KeepRunningToggle: View {
    @AppStorage("keepRunning") private var keepRunning = true
    var body: some View {
        Toggle(isOn: $keepRunning) {
            Text("Keep measuring when the window is closed")
            Text("So Checkup always has data. Quit with ⌘Q to stop completely.")
        }
    }
}

/// Eenheid per meting als keuzeknop [ % | cores/GB ] i.p.v. een schakelaar, met een voorbeeld van beide.
struct UnitPicker: View {
    let metric: Metric
    @AppStorage("absolute") private var absoluteRaw = "ram,ssd"

    var body: some View {
        let cores = ProcessInfo.processInfo.activeProcessorCount
        let gb = Int((Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824).rounded())
        let (unit, pctEx, realEx): (String, String, String) = switch metric {
        case .cpu: ("cores", "23%", "\(one(0.23 * Double(cores))) /\(cores)")
        case .ram: ("GB", "61%", "\(one(0.61 * Double(gb))) GB")
        default: ("GB", "40%", "300 GB")
        }
        LabeledContent {
            Picker(metric.title, selection: member($absoluteRaw, metric.rawValue)) {
                Text("%").tag(false); Text(unit).tag(true)
            }
            .pickerStyle(.segmented).labelsHidden().fixedSize()
        } label: {
            Text(metric.title)
            Text("% shows \(pctEx) · \(unit) shows \(realEx)")
        }
    }
}
