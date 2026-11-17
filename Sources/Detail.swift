import SwiftUI

// MARK: - Detail van één meting (klik op een kaart op de Live-pagina)
//
// Zelfde venster, zelfde grootte: grote waarde, grafiek met periodekeuze, min/gem/max en wat er per meting bij hoort.
// Terug met de pijl of Esc.

struct MetricDetail: View {
    @EnvironmentObject var meter: Meter
    @Environment(\.theme) private var theme
    let metric: Metric
    let close: () -> Void
    @State private var range = TimeRange.fiveMin
    @State private var height: CGFloat = 400
    @AppStorage("absolute") private var absoluteRaw = "ram,ssd"
    @AppStorage("shortMinutes") private var shortMinutes = 5   // zodat "5 min" meteen klopt na wijzigen

    private var abs: Bool { absoluteRaw.split(separator: ",").contains(Substring(metric.rawValue)) }
    private var canSwitch: Bool { [.cpu, .ram, .ssd].contains(metric) }

    var body: some View {
        let s = meter.latest, h = meter.samples(range)
        let r = metric.row(s, h, abs: abs)
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                header
                HStack(alignment: .firstTextBaseline, spacing: 6) {
                    Text(r.value).font(theme.number(r.valueSize <= 16 ? 26 : 44)).foregroundStyle(theme.onRow())
                        .contentTransition(.numericText()).animation(.snappy, value: r.value)
                    if !r.unit.isEmpty { Text(r.unit).font(theme.number(18)).foregroundStyle(theme.onRow(true)) }
                    Spacer()
                    if !r.side.isEmpty { Text(r.side).font(theme.number(18)).foregroundStyle(theme.onRow(true)) }
                }
                Picker("", selection: $range) {
                    ForEach(TimeRange.allCases) { Text($0.title).tag($0) }
                }
                .pickerStyle(.segmented).labelsHidden()
                card {
                    if r.values.count > 1 {
                        SparkView(values: r.values, second: r.second, top: r.top, bottom: r.bottom, fill: 1, axis: nil)
                            .id(range)   // andere periode: niet laten glijden maar meteen tekenen
                    } else {
                        Text("Collecting data…").font(theme.label(12)).foregroundStyle(theme.secondary).frame(maxWidth: .infinity, maxHeight: .infinity)
                    }
                }
                .frame(height: max(110, height * 0.36))
                stats(r)
                extras(s)
            }
            .padding(.horizontal, 14).padding(.bottom, 14)
        }
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { height = $0 }
        .background { Button("", action: close).keyboardShortcut(.cancelAction).hidden() }   // Esc = terug
    }

    // MARK: Onderdelen

    private var header: some View {
        HStack(spacing: 8) {
            Button(action: close) {
                Label("Back", systemImage: "chevron.left").labelStyle(.iconOnly).font(.system(size: 13, weight: .semibold))
                    .frame(width: 26, height: 26).contentShape(.rect)
            }
            .buttonStyle(.borderless).help("Back to Live (Esc)")
            // Smal venster: korte naam ("CPU") i.p.v. een titel over twee regels
            ViewThatFits(in: .horizontal) {
                Text(metric.title).lineLimit(1)
                Text(metric.abbr).lineLimit(1)
            }
            .font(theme.label(15).weight(.semibold)).foregroundStyle(theme.text)
            Spacer()
            if canSwitch {
                Picker("", selection: Binding(get: { abs }, set: { setAbs($0) })) {
                    Text("%").tag(false)
                    Text(metric == .cpu ? "cores" : "GB").tag(true)
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
            }
        }
    }

    private func stats(_ r: MetricRowData) -> some View {
        let v = r.values
        let avg = v.isEmpty ? 0 : v.reduce(0, +) / Double(v.count)
        return HStack(spacing: 8) {
            stat("Min", v.min().map(fmt) ?? "–")
            stat("Average", v.isEmpty ? "–" : fmt(avg))
            stat("Max", v.max().map(fmt) ?? "–")
        }
    }

    private func stat(_ label: String, _ value: String) -> some View {
        card {
            VStack(alignment: .leading, spacing: 2) {
                Text(label).font(theme.label(11)).foregroundStyle(theme.onRow(true))
                Text(value).font(theme.number(16)).foregroundStyle(theme.onRow()).lineLimit(1).minimumScaleFactor(0.6)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    /// Wat er per meting extra bij hoort.
    @ViewBuilder private func extras(_ s: Sample) -> some View {
        let total = Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824
        switch metric {
        case .cpu:
            if !s.cores.isEmpty {
                card {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(coreText).font(theme.label(11)).foregroundStyle(theme.onRow(true))
                        CoreBars(values: s.cores, split: Sampler.efficiencyCores, wide: true).frame(height: 44)
                    }
                }
            }
            facts([("Hottest core", deg(s.temp)), ("In use", "\(one(s.cpu / 100 * Double(ProcessInfo.processInfo.activeProcessorCount))) of \(ProcessInfo.processInfo.activeProcessorCount) cores")])
            topApps("Busiest apps (last minute)") { pct($0.cpu) }
        case .gpu:
            facts([("Temperature", deg(s.gpuTemp)), ("Load", pct(s.gpu))])
        case .ram:
            facts([("In use", "\(one(s.ramUsedGB)) of \(Int(total.rounded())) GB"), ("Pressure", s.pressure >= 4 ? "Critical" : s.pressure == 2 ? "High" : "Normal"),
                   ("Swap", "\(one(s.swapGB)) GB")])
            topApps("Biggest apps (last minute)", sort: { $0.memGB > $1.memGB }) { "\(one($0.memGB)) GB" }
        case .net:
            facts([("Download", rate(s.down)), ("Upload", rate(s.up)),
                   ("Response time", s.pingMs.map { "\(Int($0.rounded())) ms" } ?? "–")])
        case .ssd:
            facts([("Free", "\(Int(s.diskFreeGB)) GB"), ("Used", "\(Int(s.diskTotalGB - s.diskFreeGB)) GB"), ("Size", "\(Int(s.diskTotalGB)) GB")])
        case .io:
            facts([("Reading", rate(s.diskRead)), ("Writing", rate(s.diskWrite))])
        case .battery:
            facts([("Charge", s.battery.map(pct) ?? "–"), ("Power", s.charging ? "Charging" : "On battery"),
                   (s.charging ? "Full in" : "Time left", s.batteryMinutes.map(duration) ?? "–")])
        case .fan:
            facts(s.fans.enumerated().map { i, f in ("Fan \(i + 1)", "\(Int(f.rpm.rounded())) rpm") })
        }
    }

    private func facts(_ rows: [(String, String)]) -> some View {
        card {
            VStack(spacing: 6) {
                ForEach(rows.indices, id: \.self) { i in
                    HStack {
                        Text(rows[i].0).font(theme.label(12)).foregroundStyle(theme.onRow(true))
                        Spacer()
                        Text(rows[i].1).font(theme.number(13)).foregroundStyle(theme.onRow())
                    }
                }
            }
        }
    }

    private func topApps(_ title: String, sort: (AppUsage, AppUsage) -> Bool = { $0.cpu > $1.cpu }, _ value: @escaping (AppUsage) -> String) -> some View {
        let apps = Array(meter.minuteApps.apps.sorted(by: sort).prefix(5))
        return card {
            VStack(alignment: .leading, spacing: 6) {
                Text(title).font(theme.label(11)).foregroundStyle(theme.onRow(true))
                if apps.isEmpty { Text("Available after the first minute").font(theme.label(12)).foregroundStyle(theme.secondary) }
                ForEach(apps) { a in
                    HStack(spacing: 6) {
                        if let i = a.icon { Image(nsImage: i).resizable().frame(width: 16, height: 16) }
                        Text(a.name).font(theme.label(12)).foregroundStyle(theme.onRow()).lineLimit(1)
                        Spacer()
                        Text(value(a)).font(theme.number(12)).foregroundStyle(theme.onRow(true))
                    }
                }
            }
        }
    }

    private var coreText: String {
        let n = ProcessInfo.processInfo.activeProcessorCount, e = Sampler.efficiencyCores
        return e > 0 ? "Per core · \(e) efficiency + \(n - e) performance" : "Per core · \(n) cores"
    }

    /// Kaartje in de stijl van het thema (zelfde als de Live-rijen).
    private func card(@ViewBuilder _ c: () -> some View) -> some View {
        c().padding(10).frame(maxWidth: .infinity, alignment: .leading).themedRow(theme)
    }

    private func fmt(_ v: Double) -> String {
        switch metric {
        case .cpu, .gpu, .battery: pct(v)
        case .ram, .ssd: "\(one(v)) GB"
        case .net, .io: rate(v)
        case .fan: "\(Int(v.rounded())) rpm"
        }
    }

    private func setAbs(_ on: Bool) {
        var set = Set(absoluteRaw.split(separator: ",").map(String.init))
        if on { set.insert(metric.rawValue) } else { set.remove(metric.rawValue) }
        absoluteRaw = set.sorted().joined(separator: ",")
    }
}
