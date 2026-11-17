import SwiftUI

// MARK: - Detail van één meting (klik op een kaart op de Live-pagina)
//
// Zelfde venster, zelfde grootte: grote waarde (procent én echte waarde), grafiek met periodekeuze,
// min/gem/max en alles wat er per meting bij hoort. Terug met de pijl of Esc.

struct MetricDetail: View {
    @EnvironmentObject var meter: Meter
    @Environment(\.theme) private var theme
    let metric: Metric
    let close: () -> Void
    @State private var range = TimeRange.fiveMin
    @State private var height: CGFloat = 400
    @State private var battery: Diag.Battery?          // gezondheid: één keer ophalen bij openen
    @AppStorage("shortMinutes") private var shortMinutes = 5   // zodat "5 min" meteen klopt na wijzigen

    var body: some View {
        let s = meter.latest, h = meter.samples(range)
        let r = metric.row(s, h, abs: false)
        ScrollView {
            VStack(alignment: .leading, spacing: 12) {
                header
                headline(s, r)
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
                .frame(height: max(110, height * 0.34))
                stats(r)
                extras(s, h)
            }
            .padding(.horizontal, 14).padding(.bottom, 14)
        }
        .clipped()   // niet onder de doorzichtige werkbalk doorschuiven (dan verdwijnen de tab-iconen)
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { height = $0 }
        .background { Button("", action: close).keyboardShortcut(.cancelAction).hidden() }   // Esc = terug
        .task { if metric == .battery { battery = await Task.detached { Diag.battery() }.value } }
    }

    // MARK: Kop en hoofdwaarde

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
        }
    }

    /// Groot getal + de andere vorm eronder (procent én echte waarde, geen keuze nodig).
    private func headline(_ s: Sample, _ r: MetricRowData) -> some View {
        let (main, unit, other) = values(s, r)
        return VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(main).font(theme.number(r.valueSize <= 16 ? 26 : 44)).foregroundStyle(theme.onRow())
                    .contentTransition(.numericText()).animation(.snappy, value: main)
                    .lineLimit(1).minimumScaleFactor(0.5)
                if !unit.isEmpty { Text(unit).font(theme.number(18)).foregroundStyle(theme.onRow(true)) }
                Spacer(minLength: 0)
                if !r.side.isEmpty { Text(r.side).font(theme.number(18)).foregroundStyle(theme.onRow(true)) }
            }
            if !other.isEmpty { Text(other).font(theme.number(14)).foregroundStyle(theme.onRow(true)) }
        }
    }

    private func values(_ s: Sample, _ r: MetricRowData) -> (String, String, String) {
        let n = ProcessInfo.processInfo.activeProcessorCount
        switch metric {
        case .cpu: return (pct(s.cpu), "", "\(one(s.cpu / 100 * Double(n))) of \(n) cores busy")
        case .ram: return (one(s.ramUsedGB), "GB", "\(pct(s.ram)) of \(Int(ramTotal.rounded())) GB")
        case .ssd:
            let used = s.diskTotalGB > 0 ? (1 - s.diskFreeGB / s.diskTotalGB) * 100 : 0
            return ("\(Int(s.diskFreeGB))", "GB free", "\(pct(used)) used of \(Int(s.diskTotalGB)) GB")
        default: return (r.value, r.unit, r.detail)
        }
    }

    // MARK: Statistiek over de periode

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

    // MARK: Per meting

    @ViewBuilder private func extras(_ s: Sample, _ h: [Sample]) -> some View {
        let step = range == .today ? 60.0 : 2.0   // seconden per punt (vandaag = minuutgemiddelden)
        let period = range.title.lowercased()
        switch metric {
        case .cpu:
            if !s.cores.isEmpty {
                card {
                    VStack(alignment: .leading, spacing: 6) {
                        Text(coreText).font(theme.label(11)).foregroundStyle(theme.onRow(true))
                        CoreBars(values: s.cores, split: Sampler.efficiencyCores, wide: true).frame(height: 54)
                        HStack(spacing: 3) {   // procent per kern onder de staafjes
                            ForEach(s.cores.indices, id: \.self) { i in
                                Text("\(Int(s.cores[i].rounded()))").font(theme.number(9)).foregroundStyle(theme.onRow(true))
                                    .frame(maxWidth: .infinity).lineLimit(1).minimumScaleFactor(0.5)
                                    .padding(.leading, i == Sampler.efficiencyCores && i > 0 ? 8 : 0)
                            }
                        }
                    }
                }
            }
            let procs = meter.minuteApps.apps.reduce(0) { $0 + $1.pids.count }
            facts([("Hottest core", deg(s.temp).ifEmpty("–")),
                   ("Load average (1 · 5 · 15 min)", loadAverage),
                   ("Running since restart", uptime),
                   ("Processes", procs > 0 ? "\(procs)" : "–")])
            topApps("Busiest apps (last minute)", sort: { $0.cpu > $1.cpu }) { pct($0.cpu) }
        case .gpu:
            facts([("Load", pct(s.gpu)), ("Temperature", deg(s.gpuTemp).ifEmpty("–")),
                   ("Processor temperature", deg(s.temp).ifEmpty("–"))])
            topApps("Most power-hungry apps (last minute)", sort: { $0.energyW > $1.energyW }) { String(format: "%.1f W", $0.energyW) }
        case .ram:
            facts([("In use", "\(one(s.ramUsedGB)) GB · \(pct(s.ram))"),
                   ("Free", "\(one(max(ramTotal - s.ramUsedGB, 0))) GB · \(pct(100 - s.ram))"),
                   ("Installed", "\(Int(ramTotal.rounded())) GB"),
                   ("Memory pressure", s.pressure >= 4 ? "Critical" : s.pressure == 2 ? "High" : "Normal"),
                   ("Swap (memory on the SSD)", "\(one(s.swapGB)) GB")])
            topApps("Biggest apps (last minute)", sort: { $0.memGB > $1.memGB }) { "\(one($0.memGB)) GB" }
        case .net:
            facts([("Download now", rate(s.down)), ("Upload now", rate(s.up)),
                   ("Response time", s.pingMs.map { "\(Int($0.rounded())) ms" } ?? "–"),
                   ("Downloaded (\(period))", bytes(h.reduce(0) { $0 + $1.down } * step)),
                   ("Uploaded (\(period))", bytes(h.reduce(0) { $0 + $1.up } * step)),
                   ("Fastest download", rate(h.map(\.down).max() ?? 0)),
                   ("Fastest upload", rate(h.map(\.up).max() ?? 0))])
        case .ssd:
            let used = max(s.diskTotalGB - s.diskFreeGB, 0), frac = s.diskTotalGB > 0 ? used / s.diskTotalGB : 0
            card {
                VStack(alignment: .leading, spacing: 6) {
                    Text("\(Int(used)) GB used · \(Int(s.diskFreeGB)) GB free").font(theme.label(11)).foregroundStyle(theme.onRow(true))
                    Capsule().fill(theme.accent.opacity(0.15)).frame(height: 10)
                        .overlay(alignment: .leading) { GeometryReader { g in Capsule().fill(theme.accent).frame(width: g.size.width * frac) } }
                }
            }
            facts([("Size", "\(Int(s.diskTotalGB)) GB"), ("Used", "\(Int(used)) GB · \(pct(frac * 100))"),
                   ("Free", "\(Int(s.diskFreeGB)) GB · \(pct(100 - frac * 100))"),
                   ("Reading now", rate(s.diskRead)), ("Writing now", rate(s.diskWrite)),
                   ("Swap", "\(one(s.swapGB)) GB")])
        case .io:
            facts([("Reading now", rate(s.diskRead)), ("Writing now", rate(s.diskWrite)),
                   ("Read (\(period))", bytes(h.reduce(0) { $0 + $1.diskRead } * step)),
                   ("Written (\(period))", bytes(h.reduce(0) { $0 + $1.diskWrite } * step)),
                   ("Fastest read", rate(h.map(\.diskRead).max() ?? 0)),
                   ("Fastest write", rate(h.map(\.diskWrite).max() ?? 0))])
            topApps("Busiest on the SSD (last minute)", sort: { $0.diskRead + $0.diskWrite > $1.diskRead + $1.diskWrite }) { rate($0.diskRead + $0.diskWrite) }
        case .battery:
            facts([("Charge", s.battery.map(pct) ?? "–"), ("Power", s.charging ? "Charging" : "On battery"),
                   (s.charging ? "Full in" : "Time left", s.batteryMinutes.map(duration) ?? "–"),
                   ("Health", battery?.condition ?? battery?.health ?? "–"),
                   ("Charge cycles", battery?.cycles.map { "\($0) of ~\(battery?.designCycles ?? 1000)" } ?? "–"),
                   ("Capacity", battery?.capacity.map { "\($0)% of new" } ?? "–")])
            topApps("Most power-hungry apps (last minute)", sort: { $0.energyW > $1.energyW }) { String(format: "%.1f W", $0.energyW) }
        case .fan:
            facts(s.fans.enumerated().map { i, f in ("Fan \(i + 1)", "\(Int(f.rpm.rounded())) rpm (\(Int(f.min))–\(Int(f.max)))") }
                  + [("Control", s.fans.contains(where: \.forced) ? "Fusebox" : "macOS (automatic)"),
                     ("Processor temperature", deg(s.temp).ifEmpty("–"))])
        }
    }

    private func facts(_ rows: [(String, String)]) -> some View { Facts(rows: rows) }

    private func topApps(_ title: String, sort: (AppUsage, AppUsage) -> Bool, _ value: @escaping (AppUsage) -> String) -> some View {
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

    // MARK: Hulpjes

    private var ramTotal: Double { Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824 }

    private var coreText: String {
        let n = ProcessInfo.processInfo.activeProcessorCount, e = Sampler.efficiencyCores
        return e > 0 ? "Per core · \(e) efficiency + \(n - e) performance" : "Per core · \(n) cores"
    }

    /// Gemiddeld aantal wachtende taken over 1, 5 en 15 minuten (zoals `uptime` in Terminal).
    private var loadAverage: String {
        var l = [Double](repeating: 0, count: 3)
        guard getloadavg(&l, 3) == 3 else { return "–" }
        return l.map { String(format: "%.1f", $0) }.joined(separator: " · ")
    }

    private var uptime: String {
        var tv = timeval(), size = MemoryLayout<timeval>.size
        guard sysctlbyname("kern.boottime", &tv, &size, nil, 0) == 0 else { return "–" }
        let m = Int(Date().timeIntervalSince1970 - Double(tv.tv_sec)) / 60
        return m >= 1440 ? "\(m / 1440) d \(m % 1440 / 60) h" : "\(m / 60) h \(m % 60) min"
    }

    private func card(@ViewBuilder _ c: @escaping () -> some View) -> some View { DetailCard(content: c) }

    private func fmt(_ v: Double) -> String {
        switch metric {
        case .cpu, .gpu, .battery: pct(v)
        case .ram, .ssd: "\(one(v)) GB"
        case .net, .io: rate(v)
        case .fan: "\(Int(v.rounded())) rpm"
        }
    }
}

extension String {
    func ifEmpty(_ s: String) -> String { isEmpty ? s : self }
}

/// Kaartje in de stijl van het thema (zelfde als de Live-rijen); ook op de app-pagina.
struct DetailCard<C: View>: View {
    @Environment(\.theme) private var theme
    @ViewBuilder let content: () -> C
    var body: some View { content().padding(10).frame(maxWidth: .infinity, alignment: .leading).themedRow(theme) }
}

/// Rijtje "naam … waarde" in een kaartje.
struct Facts: View {
    @Environment(\.theme) private var theme
    let rows: [(String, String)]
    var body: some View {
        DetailCard {
            VStack(spacing: 7) {
                ForEach(rows.indices, id: \.self) { i in
                    HStack {
                        Text(rows[i].0).font(theme.label(12)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                        Spacer()
                        Text(rows[i].1).font(theme.number(13)).foregroundStyle(theme.onRow()).lineLimit(1).truncationMode(.middle)
                    }
                }
            }
        }
    }
}
