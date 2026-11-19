import SwiftUI
import UserNotifications

// MARK: - Stappenplan: eerste start (setupDone == false) of via Instellingen › Profiel / menubalk.
// Eén vraag per stap. Elke keuze schrijft meteen naar UserDefaults, dus de echte vensters veranderen live mee.
// Sluiten met het rode knopje zet de momentopname van het begin terug; "Skip setup" houdt de keuzes.

struct SetupView: View {
    @Environment(\.openWindow) private var openWindow
    @Environment(\.dismissWindow) private var dismissWindow
    @AppStorage("setupDone") private var setupDone = false
    @AppStorage("notify") private var notify = true
    @State private var step = 0
    @State private var snapshot: Data?
    @State private var finished = false
    @State private var message: String?
    @State private var meter: Meter?      // via MeterHook, zodat dit venster niet elke 2 s opnieuw tekent

    private let steps: [(question: String, help: String)] = [
        ("Welcome to Fusebox", "Fusebox shows how busy your Mac is: processor, memory, storage, internet and battery.\nIn a small window, in your menu bar, or both."),
        ("Pick a style", "This is how the Fusebox window looks. You can switch any time."),
        ("How big should the text be?", "Pick what reads comfortably from where you sit."),
        ("What do you want to see?", "Turn measurements on or off. Drag them to change the order."),
        ("Percent or real values?", "Percent is quick to read. Real values tell you exactly how much."),
        ("Show graphs when you make the window bigger?", "Graphs show the last few minutes. They only appear when there's room."),
        ("What should the menu bar show?", "Something small next to the clock, so you can check at a glance."),
        ("How should Fusebox start?", "Fusebox uses very little power, so it's fine to keep it running."),
        ("Should Fusebox warn you?", "A notification when something needs attention, like your Mac getting too hot."),
        ("Data and privacy", "Everything stays on your Mac."),
        ("Permissions", "macOS asks you once for each of these. Allow what you want to use; you can change it later in System Settings."),
        ("You're all set!", "Here's what you picked."),
    ]
    private var last: Int { steps.count - 1 }

    var body: some View {
        VStack(spacing: 0) {
            VStack(alignment: .leading, spacing: 6) {
                HStack {
                    Text("Step \(step + 1) of \(steps.count)").font(.caption).foregroundStyle(.secondary)
                    ProgressView(value: Double(step + 1), total: Double(steps.count)).controlSize(.small)
                }
                HStack(spacing: 12) {
                    if step == 0 { Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 48, height: 48) }
                    VStack(alignment: .leading, spacing: 4) {
                        Text(steps[step].question).font(.title2.weight(.semibold))
                        Text(steps[step].help).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    }
                }
                .padding(.top, 6)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding([.horizontal, .top], 20)

            Form { page }.formStyle(.grouped)

            HStack {
                Button("Skip setup") { finish() }
                Spacer()
                if step > 0 { Button("Back") { step -= 1 } }
                Button(step == last ? "Start using Fusebox" : "Next") { step == last ? finish() : (step += 1) }
                    .keyboardShortcut(.defaultAction)
            }
            .controlSize(.large)
            .padding([.horizontal, .bottom], 20).padding(.top, 8)
        }
        .frame(width: 560, height: 480)
        .background(MeterHook { meter = $0 })
        .onAppear { snapshot = Prefs.export(); step = 0; finished = false; message = nil }
        .onDisappear {
            // Installer verplaatst/opent een andere kopie en sluit deze: dan is de setup níet gedaan (de nieuwe kopie
            // moet hem gewoon tonen). Eerder telde dat sluiten als "klaar", waardoor de setup bij een maat maar even flitste.
            if Installer.handingOver || meter?.quitting == true { return }
            // Rode knopje = annuleren: alles terug zoals het was, maar niet bij elke start opnieuw vragen.
            if !finished, let snapshot {
                Prefs.restore(snapshot)
                if let meter, !meter.quitting { prefsChanged(meter, open: openWindow, dismiss: dismissWindow) }
            }
            setupDone = true
        }
    }

    @ViewBuilder private var page: some View {
        switch step {
        case 0:
            Section {
                Label("This takes about a minute.", systemImage: "clock")
                Button("Import settings from a friend…") {
                    guard let m = Prefs.importDialog() else { return }
                    message = m
                    if let meter { prefsChanged(meter, open: openWindow, dismiss: dismissWindow) }
                    if m.hasSuffix("imported") { step = last }
                }
                .buttonStyle(.link)
                if let message { Text(message).foregroundStyle(.secondary) }
            }
        case 1: Section { ThemeTiles() }
        case 2: TextSizeStep()
        case 3: Section { MetricPicker() }
        case 4: UnitsStep()
        case 5: GraphsStep()
        case 6: MenubarStep()
        case 7:
            Section {
                LoginToggle()
                DockToggle()
            } footer: {
                Text("Without the Dock icon, you'll find Fusebox in the menu bar.").font(.caption).foregroundStyle(.secondary)
            }
        case 8:
            Section {
                Toggle("Show notifications", isOn: $notify).onChange(of: notify) { _, on in if on { Notify.ask() } }
            }
            Section {
                ForEach(Alert.allCases) { AlertRow(alert: $0, compact: true) }
            } footer: {
                Text("You can change the limits later in Settings › Notifications.").font(.caption).foregroundStyle(.secondary)
            }
            .disabled(!notify)
        case 9: PrivacyStep()
        case 10: PermissionsStep()
        default: Summary(message: message)
        }
    }

    private func finish() {
        if notify { Notify.ask() }
        finished = true
        setupDone = true
        dismissWindow(id: "setup")
    }
}

/// Haalt de Meter op zonder te observeren: alleen dit lege viewtje tekent elke 2 s opnieuw.
private struct MeterHook: View {
    @EnvironmentObject var meter: Meter
    let found: (Meter) -> Void
    var body: some View { Color.clear.onAppear { found(meter) } }
}

// MARK: - Bouwstenen (ook gebruikt in Instellingen.swift)

/// Grote klikbare kaart; gekozen = rand in de accentkleur.
struct Card<Content: View>: View {
    let selected: Bool
    let action: () -> Void
    @ViewBuilder var content: Content
    var body: some View {
        Button(action: action) {
            content
                .frame(maxWidth: .infinity).padding(10)
                .background(.quaternary.opacity(0.35), in: .rect(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).strokeBorder(selected ? Color.accentColor : .secondary.opacity(0.25), lineWidth: selected ? 2.5 : 1))
                .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(selected ? .isSelected : [])
    }
}

/// Kaart met symbool, titel en één regel uitleg.
func option(_ title: String, _ detail: String, _ symbol: String, selected: Bool, action: @escaping () -> Void) -> some View {
    Card(selected: selected, action: action) {
        VStack(spacing: 5) {
            Image(systemName: symbol).font(.title2).frame(height: 28)
            Text(title).font(.headline)
            Text(detail).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.center).lineLimit(2, reservesSpace: true)
        }
    }
}

extension AppearanceMode {
    /// Thema zoals het er in deze modus uitziet (System = de huidige macOS-modus).
    func apply(_ t: Theme, system: ColorScheme) -> Theme { t.adapted(to: self == .light ? .light : self == .dark ? .dark : system) }
}

func grid(_ n: Int) -> [GridItem] { Array(repeating: GridItem(.flexible(), spacing: 10), count: n) }

/// Vaste voorbeeldwaarden voor previews (geen live Meter nodig): CPU 17% 47°, RAM 74% van dit geheugen, SSD 300 van 500 GB vrij.
enum PreviewData {
    static let history: [Sample] = (0..<24).map { i in
        let x = Double(i), total = Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824
        var s = Sample()
        s.cpu = 22 + 14 * sin(x / 2.3) + 6 * sin(x * 1.7)
        s.temp = 47; s.gpu = 8 + 5 * sin(x / 3)
        s.ram = 72 + 2 * sin(x / 4); s.ramUsedGB = total * s.ram / 100
        s.down = 900_000 + 500_000 * sin(x / 1.8); s.up = 150_000; s.pingMs = 18
        s.diskTotalGB = 500; s.diskFreeGB = 300; s.diskRead = 2_400_000; s.diskWrite = 800_000
        s.battery = 80 - x / 4
        return s
    }
    static var now: Sample {
        var s = history[history.count - 1]
        s.cpu = 17; s.ram = 74; s.ramUsedGB = Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824 * 0.74
        return s
    }
}

// MARK: - Stappen

struct ThemeTiles: View {
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @Environment(\.colorScheme) private var system
    var body: some View {
        LazyVGrid(columns: grid(3), spacing: 10) {
            ForEach(Theme.all) { t in
                Card(selected: themeID == t.id, action: { themeID = t.id }) {
                    VStack(spacing: 6) { ThemePreview(theme: mode.apply(t, system: system)); Text(t.name).font(.callout) }
                }
            }
        }
        .padding(.vertical, 4)
    }
}

/// Vast voorbeeldje in een stijl (geen live gegevens).
struct ThemePreview: View {
    let theme: Theme
    var body: some View {
        VStack(spacing: [.lines, .plain].contains(theme.rowStyle) ? 0 : 4) {
            row(0, "CPU", "47°", "17%", "")
            row(1, "RAM", "", "11.8", "GB")
        }
        .padding(8)
        .frame(maxWidth: .infinity, minHeight: 86)
        .background(theme.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), in: .rect(cornerRadius: 8))
    }
    /// Zoals een Live-rij: label + klein de temperatuur links, getal rechts.
    private func row(_ i: Int, _ label: String, _ side: String, _ value: String, _ unit: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 3) {
            Text(label).font(theme.label(9)).foregroundStyle(theme.id == "instrument" ? theme.accent : theme.onRow(true))
            Text(side).font(theme.number(9)).foregroundStyle(theme.onRow(true))
            Spacer(minLength: 2)
            Text(value).font(theme.number(14)).foregroundStyle(theme.onRow())
            Text(unit).font(theme.number(9)).foregroundStyle(theme.onRow(true))
        }
        .lineLimit(1)
        .themedRow(theme, index: i)
    }
}

private struct TextSizeStep: View {
    var body: some View {
        Section { TextSizeCards() }
        Section {
            AccentPicker()
        } footer: {
            Text("Optional: your own accent color. \"Default\" uses the style's color.").font(.caption).foregroundStyle(.secondary)
        }
    }
}

/// Drie kaarten met "Aa" + een voorbeeldrij in de gekozen stijl en grootte.
struct TextSizeCards: View {
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("textScale") private var textScale = 1.0
    @AppStorage("accent") private var accent = ""   // zodat het voorbeeld meteen meekleurt
    @AppStorage("font") private var font = "style"   // idem bij een ander lettertype
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @Environment(\.colorScheme) private var system
    var body: some View {
            LazyVGrid(columns: grid(3), spacing: 10) {
                ForEach([(1.0, "Normal"), (1.15, "Large"), (1.3, "Extra large")], id: \.0) { scale, name in
                    Card(selected: textScale == scale, action: { textScale = scale }) {
                        VStack(spacing: 4) {
                            Text("Aa").font(.system(size: 22 * scale, weight: .semibold, design: .rounded)).frame(height: 36)
                            Text(name).font(.callout)
                        }
                    }
                }
            }
            let _ = font
            let t = mode.apply(Theme.resolved(themeID), system: system)
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text("CPU").font(t.label(13)).foregroundStyle(t.id == "instrument" ? t.accent : t.onRow(true))
                Text("47°").font(t.number(13)).foregroundStyle(t.onRow(true))
                Spacer()
                Text("17%").font(t.number(30)).foregroundStyle(t.onRow())
            }
            .themedRow(t)
            .padding(10)
            .background(t.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), in: .rect(cornerRadius: 10))
    }
}

/// Zoals `MetricList("metrics")`, maar met één regel uitleg per meting.
private struct MetricPicker: View {
    @AppStorage("metrics") private var raw = Metric.defaultOrder
    var body: some View {
        let on = Metric.enabled(raw)
        let order = on + Metric.offered.filter { !on.contains($0) }
        List {
            ForEach(order) { m in
                Toggle(isOn: member($raw, m.rawValue)) { Text(m.title); Text(explain(m)) }
            }
            .onMove { from, to in
                var all = order
                all.move(fromOffsets: from, toOffset: to)
                raw = all.filter(on.contains).map(\.rawValue).joined(separator: ",")
            }
        }
        .frame(height: CGFloat(Metric.offered.count) * 40 + 12)
    }
    private func explain(_ m: Metric) -> String {
        switch m {
        case .cpu: "How hard your Mac is working, and how warm it is"
        case .gpu: "How hard the graphics chip is working"
        case .ram: "How full the working memory is"
        case .net: "Download and upload speed"
        case .ssd: "How full your storage is"
        case .io: "How much is being read from and written to storage"
        case .battery: "Charge and time left"
        case .fan: "How fast the fans spin"
        }
    }
}

private struct UnitsStep: View {
    @AppStorage("absolute") private var absolute = "ram,ssd"
    var body: some View {
        Section {
            ForEach([Metric.cpu, .ram, .ssd]) { m in UnitPicker(metric: m) }
        } footer: {
            Text("Tip: you can also click a number on the Live page to switch.").font(.caption).foregroundStyle(.secondary)
        }
    }
}

private struct GraphsStep: View {
    var body: some View { Section { GraphCards() } }
}

struct GraphCards: View {
    @AppStorage("graphs") private var graphs: GraphMode = .auto
    var body: some View {
            LazyVGrid(columns: grid(2), spacing: 10) {
                option("Automatic", "Graphs appear when the window is big enough", "chart.xyaxis.line", selected: graphs != .never) { graphs = .auto }
                option("Never", "Always just the numbers", "textformat.123", selected: graphs == .never) { graphs = .never }
            }
    }
}

private struct MenubarStep: View {
    @AppStorage("menubarStyle") private var style: MenubarStyle = .text
    var body: some View {
        Section { MenubarStyleCards() }
        if style != .off && style != .icon {
            Section("Which measurements? (max. 4)") { MetricList("menubarMetrics", limit: 4) }
        }
    }
}

/// Kaarten per menubalk-stijl, elk met een voorbeeldje van de gekozen metingen.
struct MenubarStyleCards: View {
    @AppStorage("menubarStyle") private var style: MenubarStyle = .text
    @AppStorage("menubarMetrics") private var raw = "cpu"
    @AppStorage("dockIcon") private var dock = true
    @AppStorage("absolute") private var absolute = "ram,ssd"   // voorbeeld volgt % / echte waarden
    @AppStorage("netBits") private var bits = false
    var body: some View {
        let ms = Array(Metric.enabled(raw).prefix(4))
        LazyVGrid(columns: grid(3), spacing: 10) {
            ForEach(MenubarStyle.allCases) { s in
                Card(selected: style == s, action: { style = s }) {
                    VStack(spacing: 6) {
                        MenubarPreview(style: s, metrics: ms.isEmpty ? [.cpu] : ms, bits: bits)
                        Text(s.title).font(.caption).multilineTextAlignment(.center).lineLimit(2, reservesSpace: true)
                    }
                }
                .disabled(s == .off && !dock)
                .opacity(s == .off && !dock ? 0.4 : 1)
            }
        }
        if !dock { Text("\"Don't show\" needs the Dock icon on, otherwise you can't find Fusebox.").font(.caption).foregroundStyle(.secondary) }
    }
}

/// Vast voorbeeldje van een menubalk-stijl met vaste waarden (PreviewData).
struct MenubarPreview: View {
    let style: MenubarStyle
    var metrics: [Metric] = [.cpu, .ram]
    var bits = false
    var body: some View {
        let s = PreviewData.now
        Group {
            switch style {
            case .text: Text(metrics.map { "\($0.abbr) \($0.menuValue(s, bits: bits))" }.joined(separator: "  "))
            case .compact: metrics.reduce(Text("")) { t, m in Text("\(t)\(Image(systemName: m.symbol)) \(m.menuValue(s, bits: bits)) ") }
            case .bars: HStack(spacing: 2) { ForEach(metrics) { bar($0.fraction(s)) } }
            case .graph:
                HStack(spacing: 3) {
                    ForEach(metrics) { m in
                        Spark(values: PreviewData.history.suffix(12).map(m.fraction), top: 1, fill: 1, closed: false).stroke(lineWidth: 1.2).frame(width: 22)
                    }
                }
            case .icon: Image(systemName: "gauge.with.needle")
            case .off: Text("—").foregroundStyle(.secondary)
            }
        }
        .font(.system(size: 11).monospacedDigit())
        .lineLimit(1).minimumScaleFactor(0.5)
        .frame(height: 14)
        .padding(.horizontal, 8).padding(.vertical, 4)
        .background(.quaternary, in: .capsule)
    }
    private func bar(_ f: Double) -> some View {
        ZStack(alignment: .bottom) {
            RoundedRectangle(cornerRadius: 1.5).stroke(lineWidth: 1)
            RoundedRectangle(cornerRadius: 1.5).frame(height: 14 * f)
        }
        .frame(width: 6, height: 14)
    }
}

private struct PrivacyStep: View {
    @AppStorage("keepDays") private var keepDays = 30
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    var body: some View {
        Section {
            Picker("Keep measurement history for", selection: $keepDays) {
                Text("7 days").tag(7); Text("30 days").tag(30); Text("90 days").tag(90); Text("Forever").tag(0)
            }
            .pickerStyle(.segmented)
        }
        Section {
            Toggle(isOn: Binding(get: { !pingHost.isEmpty }, set: { pingHost = $0 ? "1.1.1.1" : "" })) {
                Text("Measure internet response time")
                Text("Every 10 seconds Fusebox makes one tiny connection to 1.1.1.1 to time it. Nothing about you is sent. Off means no internet connections at all.")
            }
        }
    }
}


private struct Summary: View {
    let message: String?
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("textScale") private var textScale = 1.0
    @AppStorage("metrics") private var metrics = Metric.defaultOrder
    @AppStorage("menubarStyle") private var menubar: MenubarStyle = .text
    @AppStorage("notify") private var notify = true
    @AppStorage("keepDays") private var keepDays = 30
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"

    var body: some View {
        Section {
            if let message { Text(message) }
            LabeledContent("Look", value: "\(Theme.named(themeID).name), \(([1.0: "normal", 1.15: "large", 1.3: "extra large"][textScale] ?? "custom")) text")
            LabeledContent("Measurements", value: Metric.enabled(metrics).map(\.abbr).joined(separator: ", "))
            LabeledContent("Menu bar", value: menubar.title)
            LabeledContent("Notifications", value: notify ? "\(Alert.allCases.filter { UserDefaults.standard.bool(forKey: $0.key("on")) }.count) kinds on" : "Off")
            LabeledContent("Data", value: (keepDays == 0 ? "History kept forever" : "History kept \(keepDays) days")
                           + " · internet check " + (pingHost.isEmpty ? "off" : "on"))
        } footer: {
            // Deelknop in de voetregel: zo past de hele samenvatting zonder scrollen.
            HStack {
                Text("You can change everything later in Settings (⌘,).").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Share my settings…") { Prefs.shareDialog() }.controlSize(.small)
            }
        }
    }
}

/// Lettertype kiezen: elk kaartje toont "17%" en "CPU" in dat lettertype, in het gewicht van de gekozen stijl.
struct FontCards: View {
    @AppStorage("font") private var font: AppFont = .style
    @AppStorage("theme") private var themeID = "instrument"
    var body: some View {
        let t = Theme.resolved(themeID)
        LazyVGrid(columns: grid(4), spacing: 10) {
            ForEach(AppFont.allCases) { f in
                Card(selected: font == f, action: { font = f }) {
                    VStack(spacing: 2) {
                        Text("17%").font(f.font(22, weight: t.weight, style: t.design).monospacedDigit()).frame(height: 30)
                        Text("CPU 47°").font(f.font(11, weight: .medium, style: t.design)).foregroundStyle(.secondary)
                        Text(f.title).font(.caption).padding(.top, 2)
                    }
                }
            }
        }
    }
}


/// Alle toestemmingen op één plek, met de stand en een knop. Niets wordt stiekem gevraagd: elke vraag na een klik.
private struct PermissionsStep: View {
    @State private var notif: UNAuthorizationStatus = .notDetermined
    @State private var fda = DiskScan.fullDiskAccess
    @State private var lanAsked = UserDefaults.standard.bool(forKey: "lanAsked")
    @State private var helper = FanHelper.installed
    @State private var helperNote: String?
    @AppStorage("notify") private var notify = true

    var body: some View {
        Section {
            row("bell.badge", "Notifications", "Updates, peaks, heat and battery warnings",
                notif == .authorized || notif == .provisional ? "Allowed" : notif == .denied ? "Off" : nil,
                notif == .denied ? "Open Settings" : "Allow") {
                if notif == .denied { open("x-apple.systempreferences:com.apple.Notifications-Settings.extension?id=\(Bundle.main.bundleIdentifier ?? "")") }
                else { notify = true; UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in Task { @MainActor in refresh() } } }
            }
            row("network", "Local network", "To find the devices on your network (Network tab)",
                lanAsked ? "Asked" : nil, lanAsked ? "Open Settings" : "Allow") {
                if lanAsked { open("x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_LocalNetwork") }
                else { LocalNetworkNudge.ask(); lanAsked = true; UserDefaults.standard.set(true, forKey: "lanAsked") }
            }
            row("internaldrive", "Full Disk Access", "So Checkup can measure all your folders (Photos, Mail, app data)",
                fda ? "Allowed" : nil, "Open Settings") {
                open("x-apple.systempreferences:com.apple.settings.PrivacySecurity.extension?Privacy_AllFiles")
            }
            row("fan", "Fusebox helper", helperNote ?? "Optional: fan control and charge limit. Asks for your password once",
                helper ? "Installed" : nil, "Install") {
                helperNote = FanHelper.install(); helper = FanHelper.installed
            }
        } footer: {
            Text("Full Disk Access: in the list, turn on Fusebox (or add it with +). Fusebox only measures; it never reads, sends or deletes your files.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task {   // stand bijhouden zolang deze stap open is (ook als je in Systeeminstellingen iets aanzet)
            while !Task.isCancelled { refresh(); try? await Task.sleep(for: .seconds(2)) }
        }
    }

    private func refresh() {
        fda = DiskScan.fullDiskAccess; helper = FanHelper.installed
        UNUserNotificationCenter.current().getNotificationSettings { s in let st = s.authorizationStatus; Task { @MainActor in notif = st } }
    }

    private func open(_ s: String) { if let u = URL(string: s) { NSWorkspace.shared.open(u) } }

    private func row(_ icon: String, _ title: String, _ help: String, _ state: String?, _ button: String, action: @escaping () -> Void) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon).frame(width: 22).foregroundStyle(.secondary)
            VStack(alignment: .leading, spacing: 1) {
                Text(title)
                Text(help).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            Spacer()
            if let state { Label(state, systemImage: "checkmark.circle.fill").labelStyle(.titleAndIcon).foregroundStyle(.green).font(.callout) }
            if state == nil || button == "Open Settings" && state != "Allowed" { Button(button, action: action) }
        }
    }
}

/// macOS vraagt pas om "lokaal netwerk" als een app er echt iets naartoe stuurt: één klein mDNS-pakketje.
import Network
enum LocalNetworkNudge {
    static func ask() {
        let c = NWConnection(host: "224.0.0.251", port: 5353, using: .udp)
        c.start(queue: .global(qos: .utility))
        c.send(content: Data([0]), completion: .contentProcessed { _ in c.cancel() })
    }
}
