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

/// Uitklappaneel (`.window`-stijl) met een compacte live-lijst.
struct MenuPanel: View {
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
