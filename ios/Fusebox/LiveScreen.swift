import SwiftUI
import Darwin

// MARK: - Live: hoe gaat het nu met deze iPhone
//
// Alleen wat iOS een app eerlijk laat zien: accu, warmte (vier standen, geen graden), opslag en netwerkverkeer.
// Geen CPU, GPU, temperaturen of andere apps: daar mag een iPhone-app niet bij. Meet elke 2 s, alleen in beeld.

@MainActor
final class LiveModel: ObservableObject {
    static let points = 60
    @Published private(set) var battery: Double?            // 0…100, nil = onbekend (simulator)
    @Published private(set) var state = UIDevice.BatteryState.unknown
    @Published private(set) var lowPower = false
    @Published private(set) var thermal = ProcessInfo.ThermalState.nominal
    @Published private(set) var free: Double?               // bytes
    @Published private(set) var size: Double?
    @Published private(set) var down: [Double] = []         // bytes per seconde
    @Published private(set) var up: [Double] = []
    private var last: (rx: UInt32, tx: UInt32, at: Date)?
    private var diskAt = Date.distantPast

    func tick() {
        let dev = UIDevice.current
        dev.isBatteryMonitoringEnabled = true
        battery = dev.batteryLevel < 0 ? nil : Double(dev.batteryLevel) * 100
        state = dev.batteryState
        lowPower = ProcessInfo.processInfo.isLowPowerModeEnabled
        thermal = ProcessInfo.processInfo.thermalState
        if Date().timeIntervalSince(diskAt) > 60 {   // opslag verandert traag: eens per minuut
            diskAt = Date()
            let v = try? URL(fileURLWithPath: NSHomeDirectory()).resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeTotalCapacityKey])
            free = v?.volumeAvailableCapacityForImportantUsage.map(Double.init)
            size = v?.volumeTotalCapacity.map(Double.init)
        }
        let now = Date(), c = Self.counters()
        if let l = last {
            let dt = now.timeIntervalSince(l.at)
            // 32-bit tellers: aftrekken met omloop; onmogelijke sprongen (teller gereset) overslaan
            let rx = Double(c.rx &- l.rx) / dt, tx = Double(c.tx &- l.tx) / dt
            if dt > 0.5, rx < 1.25e9, tx < 1.25e9 { push(&down, rx); push(&up, tx) }
        }
        last = (c.rx, c.tx, now)
    }

    /// Na een pauze (ander tabblad, app op de achtergrond) niet het hele gat als één meting tellen.
    func pause() { last = nil }

    private func push(_ list: inout [Double], _ v: Double) {
        list.append(v)
        if list.count > Self.points { list.removeFirst(list.count - Self.points) }
    }

    /// Ontvangen en verzonden bytes van Wi-Fi/kabel (en*) en mobiel (pdp_ip*), opgeteld.
    private static func counters() -> (rx: UInt32, tx: UInt32) {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { return (0, 0) }
        defer { freeifaddrs(list) }
        var rx: UInt32 = 0, tx: UInt32 = 0
        var p = list
        while let a = p?.pointee {
            defer { p = a.ifa_next }
            let name = String(cString: a.ifa_name)
            guard name.hasPrefix("en") || name.hasPrefix("pdp_ip"), let sa = a.ifa_addr, sa.pointee.sa_family == UInt8(AF_LINK), let d = a.ifa_data else { continue }
            let data = d.assumingMemoryBound(to: if_data.self).pointee
            rx &+= data.ifi_ibytes; tx &+= data.ifi_obytes
        }
        return (rx, tx)
    }
}

struct LiveScreen: View {
    @StateObject private var model = LiveModel()
    @Environment(\.theme) private var theme
    @Environment(\.scenePhase) private var phase

    var body: some View {
        Screen {
            let tips = tips
            StatusLine(text: tips.first?.text ?? L("Your iPhone is fine"), ok: tips.first.map(\.ok) ?? true).font(theme.label(15))
            ForEach(tips.dropFirst(), id: \.text) { StatusLine(text: $0.text, ok: $0.ok) }
            batteryCard
            heatCard
            storageCard
            netCard
            Text("An iPhone app can't see the processor, graphics chip, temperatures in degrees or other apps. Fusebox shows what iOS does share.")
                .font(theme.label(11)).foregroundStyle(theme.secondary).fixedSize(horizontal: false, vertical: true)
        }
        // Meten alleen terwijl deze pagina in beeld is en de app voorop staat
        .task(id: phase) {
            guard phase == .active else { return }
            model.pause()
            while !Task.isCancelled {
                model.tick()
                // Energiebesparingsmodus: elke 5 s in plaats van 2 (zoals Low impact op de Mac)
                try? await Task.sleep(for: .seconds(ProcessInfo.processInfo.isLowPowerModeEnabled ? 5 : 2))
            }
        }
    }

    // MARK: Kaarten

    private var batteryCard: some View {
        metric(0, "BAT", L("Battery"), icon: batteryIcon,
               info: L("How full the battery is and whether the iPhone is charging. iOS doesn't tell apps the battery's condition, charge cycles or the charger's power; you find the condition in Settings › Battery."),
               value: model.battery.map { "\(Int($0.rounded()))" } ?? "–", unit: model.battery == nil ? "" : "%",
               level: model.state == .unplugged ? (model.battery.map { $0 <= 10 ? 2 : $0 <= 20 ? 1 : 0 } ?? 0) : 0) {
            KV(L("State"), stateText)
            KV(L("Low Power Mode"), info: L("Low Power Mode saves battery by doing less in the background. Fusebox then measures this iPhone less often and stops animating."),
               model.lowPower ? L("On") : L("Off"))
        }
    }

    private var heatCard: some View {
        let t = model.thermal
        let word = switch t { case .nominal: L("Normal"); case .fair: L("Warm"); case .serious: L("Hot"); case .critical: L("Very hot"); @unknown default: "–" }
        let text = switch t {
        case .nominal: L("Cool enough to run at full speed.")
        case .fair: L("A bit warm. Nothing to do; the iPhone still runs at full speed.")
        case .serious: L("Hot: iOS slows the iPhone down and may dim the screen. Take it out of the sun or off the charger.")
        case .critical: L("Very hot: iOS slows down hard to cool off. Let the iPhone rest somewhere cool.")
        @unknown default: ""
        }
        return metric(1, "HEAT", L("Heat"), icon: t == .nominal ? "thermometer.low" : t == .fair ? "thermometer.medium" : "thermometer.high",
                      info: L("How warm the iPhone is, in the four steps iOS gives apps: normal, warm, hot and very hot. iOS doesn't share degrees."),
                      value: word, unit: "", level: t == .critical ? 2 : t == .serious ? 1 : 0) {
            Text(verbatim: text).font(theme.label(12)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
        }
    }

    private var storageCard: some View {
        let used = (model.size ?? 0) - (model.free ?? 0), share = model.size.map { $0 > 0 ? used / $0 : 0 } ?? 0
        return metric(2, "SSD", L("Storage"), icon: "internaldrive",
                      info: L("Free space on this iPhone, counted the way iOS does for important things (it can clear caches to make room). Below about 10% free, updates and the camera can run into trouble."),
                      value: model.free.map { $0 >= 1e10 ? "\(Int($0 / 1e9))" : one($0 / 1e9) } ?? "–", unit: L("GB free"), level: share >= 0.95 ? 2 : share >= 0.9 ? 1 : 0) {
            if let size = model.size {
                ProgressView(value: min(max(share, 0), 1)).tint(theme.onRowAccent)
                KV(L("In use"), L("\(bytes(used)) of \(bytes(size))"))
            }
        }
    }

    private var netCard: some View {
        metric(3, "NET", L("Network traffic"), icon: "arrow.up.arrow.down",
               info: L("What the whole iPhone downloads (↓) and uploads (↑) right now, over Wi-Fi and mobile data together. iOS doesn't show apps which app uses it."),
               value: model.down.last.map { "↓ " + rate($0) } ?? "–", unit: model.up.last.map { "↑ " + rate($0) } ?? "", level: 0) {
            if model.down.count > 1 {
                SparkLine(main: model.down, second: model.up, points: LiveModel.points, floor: 50_000).frame(height: 40)
                Text("download — upload ┄ · last two minutes").font(theme.label(10)).foregroundStyle(theme.onRow(true))
                    .frame(maxWidth: .infinity, alignment: .trailing)
            }
        }
    }

    /// Eén meetkaart: kort label (zoals op de Mac), volle naam met uitleg, grote waarde, en eronder de feiten.
    private func metric<Facts: View>(_ index: Int, _ label: String, _ title: String, icon: String, info: String, value: String, unit: String,
                                     level: Int, @ViewBuilder facts: () -> Facts) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .firstTextBaseline, spacing: 8) {
                Image(systemName: icon).font(.system(size: 13)).foregroundStyle(theme.onRow(true)).frame(width: 18)
                Text(verbatim: label).font(theme.label(13)).foregroundStyle(theme.id == "instrument" ? theme.accent : theme.onRow(true))
                InfoTap(title: title, text: info).font(theme.label(12)).foregroundStyle(theme.onRow(true))
                Spacer(minLength: 6)
                Text(verbatim: value).font(theme.number(24)).foregroundStyle(theme.onBlocks ? .white : level == 2 ? theme.danger : level == 1 ? theme.warn : theme.onRow())
                    .lineLimit(1).minimumScaleFactor(0.7)
                if !unit.isEmpty { Text(verbatim: unit).font(theme.label(12)).foregroundStyle(theme.onRow(true)).lineLimit(1) }
            }
            facts()
        }
        .card(theme, index: index)
    }

    // MARK: Teksten

    private var stateText: String {
        switch model.state {
        case .charging: L("Charging")
        case .full: L("Plugged in, full")
        case .unplugged: L("On battery")
        default: "–"
        }
    }

    private var batteryIcon: String {
        if model.state == .charging || model.state == .full { return "battery.100percent.bolt" }
        guard let b = model.battery else { return "battery.50percent" }
        return b > 87 ? "battery.100percent" : b > 62 ? "battery.75percent" : b > 37 ? "battery.50percent" : b > 12 ? "battery.25percent" : "battery.0percent"
    }

    /// Wat nu aandacht vraagt, ergste eerst. Leeg = alles in orde.
    private var tips: [(text: String, ok: Bool?)] {
        var out: [(String, Bool?)] = []
        if model.thermal == .critical || model.thermal == .serious { out.append((L("Your iPhone is hot: take it out of the sun or off the charger and let it cool down"), false)) }
        if let free = model.free, let size = model.size, size > 0, free / size < 0.1 {
            out.append((L("Storage is almost full (\(bytes(free)) free): clear space in Settings › General › iPhone Storage"), false))
        }
        if model.state == .unplugged, let b = model.battery, b <= 20 { out.append((L("Battery is low (\(Int(b.rounded()))%): charge soon"), b <= 10 ? false : nil)) }
        if model.lowPower { out.append((L("Low Power Mode is on: the iPhone does less in the background to save battery"), nil)) }
        return out
    }
}
