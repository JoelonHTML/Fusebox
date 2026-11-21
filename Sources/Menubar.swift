import SwiftUI

// MARK: - Menubalk: wat er in de balk staat (MenuLabel) en wat er openklapt (MenuContent / MenuPanel).

enum MenubarStyle: String, CaseIterable, Identifiable {
    case text, compact, bars, graph, icon, off
    var id: String { rawValue }
    var title: String {
        switch self {
        case .text: L("Text (CPU 23%)")
        case .compact: L("Compact (small symbols)")
        case .bars: L("Bars")
        case .graph: L("Graphs")
        case .icon: L("Just an icon")
        case .off: L("Don't show")
        }
    }
}

enum MenuStyle: String, CaseIterable, Identifiable {
    case menu, panel
    var id: String { rawValue }
    var title: String { self == .menu ? L("Regular menu") : L("Panel with live values") }
}

enum MenuPart: String, CaseIterable, Identifiable {
    case status, values, top, links
    var id: String { rawValue }
    var title: String {
        switch self {
        case .status: L("How it's doing (one sentence)")
        case .values: L("All values")
        case .top: L("3 heaviest apps")
        case .links: L("Shortcuts")
        }
    }
}

extension Metric {
    var symbol: String {
        switch self {
        case .cpu: "cpu"
        case .gpu: "display"
        case .ram: "memorychip"
        case .net: "network"
        case .ssd: "internaldrive"
        case .io: "arrow.up.arrow.down"
        case .battery: "battery.75percent"
        case .fan: "fan"
        }
    }
    /// Korte waarde voor de menubalk.
    func menuValue(_ s: Sample, bits: Bool) -> String {
        switch self {
        case .net: "↓\(short(s.down, bits: bits))"
        case .io: short(s.diskRead + s.diskWrite, bits: bits)
        default:
            // Echte waarden krijgen hun eenheid erbij ("RAM 12.0 GB"), anders is het getal onduidelijk
            row(s, [], abs: prefersAbsolute).value + (!prefersAbsolute ? "" : self == .cpu ? L(" cores") : self == .ram || self == .ssd ? " GB" : "")
        }
    }
    /// 0…1 voor staafjes en grafiekjes (internet/SSD-activiteit: 100 MB/s = vol).
    func fraction(_ s: Sample) -> Double {
        let f: Double
        switch self {
        case .cpu: f = s.cpu / 100
        case .gpu: f = s.gpu / 100
        case .ram: f = s.ram / 100
        case .net: f = (s.down + s.up) / 100e6
        case .ssd: f = s.diskTotalGB > 0 ? 1 - s.diskFreeGB / s.diskTotalGB : 0
        case .io: f = (s.diskRead + s.diskWrite) / 100e6
        case .battery: f = (s.battery ?? 0) / 100
        case .fan: f = (s.fanRPM ?? 0) / max(s.fans.map(\.max).max() ?? 0, 1)
        }
        return min(max(f, 0), 1)
    }
}

/// Label van een menubalk-plekje; alleen een zichtbaar plekje volgt de live meter.
struct MenuLabel: View {
    let index: Int, live: Bool
    var body: some View { if live { LiveMenuLabel(index: index) } else { Image(systemName: "gauge.with.needle") } }
}

struct LiveMenuLabel: View {
    @EnvironmentObject var meter: Meter
    let index: Int   // welk plekje bij "elke meting apart"
    @AppStorage("menubarStyle") private var style: MenubarStyle = .text
    @AppStorage("menubarMetrics") private var raw = "cpu"
    @AppStorage("menubarSplit") private var split = false
    @AppStorage("netBits") private var bits = false

    var body: some View {
        let s = meter.latest
        let all = Array(Metric.enabled(raw).prefix(4))
        let ms = split ? Array(all.dropFirst(index).prefix(1)) : all
        switch style {
        case .text:
            Text(ms.map { "\($0.abbr) \($0.menuValue(s, bits: bits))" }.joined(separator: "  ")).monospacedDigit()
        case .compact:
            // Icoontjes midden in menubalk-tekst laat macOS weg → icoon + getal samen als één plaatje tekenen
            render(HStack(spacing: 7) {
                ForEach(ms) { m in
                    HStack(spacing: 3) {
                        Image(systemName: m.symbol).font(.system(size: 11, weight: .medium))
                        Text(m.menuValue(s, bits: bits)).font(.system(size: 12, weight: .medium).monospacedDigit())
                    }
                }
            })
        case .bars:
            render(HStack(spacing: 2) { ForEach(ms) { m in Bar(f: m.fraction(s)) } })
        case .graph:
            let h = meter.recent.suffix(30)
            render(HStack(spacing: 3) { ForEach(ms) { m in Line(values: h.map(m.fraction)).stroke(lineWidth: 1.2).frame(width: 22) } })
        case .icon, .off:
            Image(systemName: meter.status.color != .green ? "exclamationmark.circle.fill" : "gauge.with.needle")
        }
    }

    /// Menubalk toont alleen Text/Image: tekenen naar een klein sjabloon-plaatje.
    /// ponytail: één ImageRenderer per 2 s (±16×60 pt); bij compact, staafjes en grafiekjes. Cache per waarde als dit ooit telt.
    @MainActor private func render(_ v: some View) -> Image {
        let r = ImageRenderer(content: v.frame(height: 14).foregroundStyle(.black))
        r.scale = NSScreen.main?.backingScaleFactor ?? 2
        guard let img = r.nsImage else { return Image(systemName: "gauge.with.needle") }
        img.isTemplate = true
        return Image(nsImage: img)
    }

    private struct Bar: View {
        let f: Double
        var body: some View {
            ZStack(alignment: .bottom) {
                RoundedRectangle(cornerRadius: 1.5).stroke(lineWidth: 1)
                RoundedRectangle(cornerRadius: 1.5).frame(height: max(1, 14 * f))
            }
            .frame(width: 6)
        }
    }

    private struct Line: Shape {
        let values: [Double]
        func path(in r: CGRect) -> Path {
            Path { p in
                guard values.count > 1 else { return }
                for (i, v) in values.enumerated() {
                    let pt = CGPoint(x: r.width * Double(i) / Double(values.count - 1), y: r.maxY - r.height * v)
                    i == 0 ? p.move(to: pt) : p.addLine(to: pt)
                }
            }
        }
    }
}

/// Knoppen die zowel in het menu als in het paneel staan.
struct MenuButtons: View {
    @EnvironmentObject var meter: Meter
    @Environment(\.openWindow) private var openWindow
    @Environment(\.dismissWindow) private var dismissWindow
    @AppStorage("page") private var page = "live"
    @AppStorage("menuParts") private var parts = "status,values,top,links"

    var body: some View {
        let p = parts.split(separator: ",")
        if p.contains("links") {
            Button("Why is my Mac slow?") { page = "advice"; show("main") }
            Button("Open Fusebox") { show("main") }
            Button("Setup…") { show("setup") }
        }
        SettingsLink { Text("Settings…") }.keyboardShortcut(",")
        Button("Quit Fusebox") { NSApp.terminate(nil) }.keyboardShortcut("q")
    }

    private func show(_ id: String) { openWindow(id: id); NSApp.activate() }
}

/// Uitklapmenu met gewone menu-items.
struct MenuContent: View {
    @EnvironmentObject var meter: Meter
    @AppStorage("menuParts") private var parts = "status,values,top,links"
    @AppStorage("metrics") private var metricsRaw = Metric.defaultOrder

    var body: some View {
        let s = meter.latest, p = parts.split(separator: ",")
        if p.contains("status") { Text(meter.status.text) }
        if p.contains("values") {
            ForEach(Metric.enabled(metricsRaw)) { m in
                let r = m.row(s, [], abs: m.prefersAbsolute)
                Text("\(r.label)   \(r.value)\(r.unit.isEmpty ? "" : " " + r.unit)  \(r.side)")
            }
        }
        if p.contains("top"), !meter.minuteApps.apps.isEmpty {
            Divider()
            Text("Heaviest (last minute)")
            ForEach(meter.minuteApps.apps.prefix(3)) { a in Text("\(a.name)   \(pct(a.cpu))") }
        }
        Divider()
        MenuButtons()
    }
}

/// Uitklappaneel (`.window`-stijl): het rijke paneel in Checkup-stijl, of (instelling uit) de oude compacte lijst.
struct MenuPanel: View {
    @AppStorage("richPanel") private var rich = true
    var body: some View { if rich { RichPanel() } else { ClassicPanel() } }
}

/// Paneel in de stijl van de Checkup: score, live-rijtjes, belangrijkste tip en snelknoppen.
/// Bestaat alleen zolang het open is; de score komt uit Analysis.gist (rapport ≤ 10 min oud, anders één keer inlezen).
struct RichPanel: View {
    @EnvironmentObject var meter: Meter
    @Environment(\.openWindow) private var openWindow
    @Environment(\.colorScheme) private var system
    @AppStorage("page") private var page: Page = .overview
    @AppStorage("netFindNow") private var findNow = false
    @AppStorage("metrics") private var metricsRaw = Metric.defaultOrder
    @AppStorage("theme") private var themeID = Theme.instrument.id
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @AppStorage("netBits") private var bits = false
    @State private var gist: CheckupGist?

    var body: some View {
        let target: ColorScheme = mode == .light ? .light : mode == .dark ? .dark : system
        let styled = Theme.resolved(themeID).adapted(to: target), theme = LowImpact.on ? styled.solid : styled
        VStack(alignment: .leading, spacing: 10) {
            score(theme)
            metrics(theme)
            if let g = gist, let tip = g.tip { tipRow(theme, g, tip) }
            buttons(theme)
        }
        .padding(12)
        .frame(width: 336)
        .foregroundStyle(theme.text)
        .tint(theme.accent)
        .environment(\.theme, theme)
        .background(theme.bg ?? .clear)
        .preferredColorScheme(mode.scheme)
        .task { gist = await Analysis.gist(meter) }
    }

    private func score(_ theme: Theme) -> some View {
        let s = gist?.score
        let color: Color = theme.rowStyle == .blocks ? .white : s.map { $0 >= 8 ? theme.good : $0 >= 5 ? theme.warn : theme.danger } ?? theme.secondary
        return HStack(alignment: .center, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 1) {
                Text(s.map(String.init) ?? "–").font(theme.number(32)).foregroundStyle(color)
                Text("/10").font(theme.number(12)).foregroundStyle(theme.onRow(true))
            }
            VStack(alignment: .leading, spacing: 2) {
                Text("Health").font(theme.label(13).weight(.semibold)).foregroundStyle(theme.onRow())
                Text(gist?.reason ?? L("Checking…")).font(theme.label(11)).foregroundStyle(theme.onRow(true))
                    .lineLimit(2).fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            Button { NSApp.terminate(nil) } label: { Image(systemName: "power").font(.system(size: 11, weight: .semibold)) }
                .buttonStyle(.plain).foregroundStyle(theme.onRow(true)).help("Quit Fusebox")
        }
        .themedRow(theme, index: 0)
    }

    /// Live-rijtjes: alleen CPU, GPU, RAM, NET en BAT, voor zover aan op de Live-pagina.
    private func metrics(_ theme: Theme) -> some View {
        let s = meter.latest, h = meter.recent.suffix(30)
        let ms = Metric.enabled(metricsRaw).filter { [.cpu, .gpu, .ram, .net, .battery].contains($0) }
        return VStack(spacing: 5) {
            ForEach(ms) { m in
                let r = m.row(s, [], abs: m.prefersAbsolute)
                HStack(spacing: 6) {
                    Image(systemName: m.symbol).font(.system(size: 10 * theme.scale, weight: .semibold)).foregroundStyle(theme.tone(0)).frame(width: 16)
                    Text(m.abbr).font(theme.label(11)).foregroundStyle(theme.onRow(true)).frame(width: 34, alignment: .leading)
                    // Klein grafiekje van de laatste minuut; internet zonder vaste schaal (anders altijd plat)
                    SparkView(values: h.map(m.fraction), top: m == .net ? nil : 1, fill: 1, axis: nil, glides: false)
                        .frame(height: 14)
                    Text(m == .net ? m.menuValue(s, bits: bits) : r.value + (r.unit.isEmpty ? "" : " " + r.unit))
                        .font(theme.number(12)).lineLimit(1)
                        .foregroundStyle(r.level == 2 ? theme.tone(2) : r.level == 1 ? theme.tone(1) : theme.onRow())
                        .frame(width: 78, alignment: .trailing)
                }
            }
        }
        .themedRow(theme, index: 1)
    }

    private func tipRow(_ theme: Theme, _ g: CheckupGist, _ tip: String) -> some View {
        HStack(alignment: .top, spacing: 8) {
            Image(systemName: g.serious ? "exclamationmark.triangle.fill" : "lightbulb.fill")
                .font(.system(size: 12 * theme.scale)).foregroundStyle(theme.tone(g.serious ? 1 : 0)).frame(width: 16)
            VStack(alignment: .leading, spacing: 2) {
                Text(tip).font(theme.label(12).weight(.semibold)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
                if let rest = g.tipRest {
                    Text(rest).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(2).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 0)
        }
        .contentShape(.rect)
        .onTapGesture { open(.analysis) }
        .themedRow(theme, index: 2)
    }

    private func buttons(_ theme: Theme) -> some View {
        HStack(spacing: 4) {
            quick(theme, "macwindow", L("Fusebox"), help: L("Open Fusebox")) { open(nil) }
            quick(theme, Page.analysis.icon, L("Checkup")) { open(.analysis) }
            quick(theme, Page.programs.icon, L("Processes")) { open(.programs) }
            quick(theme, "stethoscope", L("Network"), help: L("Find the problem")) { findNow = true; open(.network) }
            SettingsLink { tile(theme, "gearshape", L("Settings")) }.buttonStyle(.plain).simultaneousGesture(TapGesture().onEnded { NSApp.activate() })
        }
    }

    private func quick(_ theme: Theme, _ icon: String, _ title: String, help: String? = nil, _ action: @escaping () -> Void) -> some View {
        Button(action: action) { tile(theme, icon, title) }.buttonStyle(.plain).help(help ?? title)
    }

    private func tile(_ theme: Theme, _ icon: String, _ title: String) -> some View {
        VStack(spacing: 3) {
            Image(systemName: icon).font(.system(size: 13 * theme.scale, weight: .medium)).foregroundStyle(theme.tone(0)).frame(height: 17 * theme.scale)
            Text(title).font(theme.label(10)).foregroundStyle(theme.onRow()).lineLimit(1).fixedSize()
        }
        .frame(maxWidth: .infinity).padding(.vertical, 7)
        .background(theme.onRow().opacity(0.07), in: .rect(cornerRadius: 9))
        .contentShape(.rect)
    }

    /// Hoofdvenster openen, eventueel op een bepaalde pagina.
    private func open(_ p: Page?) {
        if let p { page = p }
        openWindow(id: "main"); NSApp.activate()
    }
}

/// Het oude compacte paneel (instelling "Rich menu bar panel" uit).
struct ClassicPanel: View {
    @EnvironmentObject var meter: Meter
    @AppStorage("menuParts") private var parts = "status,values,top,links"
    @AppStorage("metrics") private var metricsRaw = Metric.defaultOrder

    var body: some View {
        let s = meter.latest, p = parts.split(separator: ",")
        VStack(alignment: .leading, spacing: 8) {
            if p.contains("status") {
                HStack(spacing: 6) { Circle().fill(meter.status.color).frame(width: 8, height: 8); Text(meter.status.text) }
                    .font(.callout.weight(.medium))
            }
            if p.contains("values") {
                Grid(alignment: .leading, verticalSpacing: 4) {
                    ForEach(Metric.enabled(metricsRaw)) { m in
                        let r = m.row(s, [], abs: m.prefersAbsolute)
                        GridRow {
                            Text(r.label).foregroundStyle(.secondary)
                            Text(r.value).monospacedDigit().foregroundStyle(r.level == 2 ? .red : r.level == 1 ? .orange : .primary)
                            Text([r.unit, r.side].filter { !$0.isEmpty }.joined(separator: " ")).foregroundStyle(.secondary)
                        }
                    }
                }
            }
            if p.contains("top"), !meter.minuteApps.apps.isEmpty {
                Divider()
                ForEach(meter.minuteApps.apps.prefix(3)) { a in
                    HStack { Text(a.name).lineLimit(1); Spacer(); Text(pct(a.cpu)).monospacedDigit() }
                }
            }
            Divider()
            MenuButtons().buttonStyle(.borderless)
        }
        .padding(12)
        .frame(width: 260)
    }
}
