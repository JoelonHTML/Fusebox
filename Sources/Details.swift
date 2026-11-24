import SwiftUI
import IOKit

// MARK: - "More details" in de grote weergave (klik op een kaart op Live)
//
// Per meting een blok met de diepere hardwarewaarden, ingeklapt tot je het opent (per meting onthouden in "moreDetails").
// Licht: er wordt alleen gelezen zolang het blok open staat, hooguit op de bestaande meettik (zie `interval`), nooit
// via een subprocess. Elke waarde heeft een label met uitleg (zelfde zweef/klik-label als op Network).
// Het ontleden zit in pure functies met een zelftest; wat de Mac niet meldt, tonen we niet.

/// Eén regel: naam, waarde en de uitleg achter het label. `head` = tussenkopje (alleen de naam).
struct DetailRow: Equatable {
    let title: String, value: String, hint: String
    var head = false
}

enum MoreInfo {
    /// Metingen met een "More details"-blok.
    static func available(_ m: Metric) -> Bool { m == .battery }

    /// Om de hoeveel seconden opnieuw lezen zolang het blok open staat (nooit vaker dan de meettik zelf).
    static func interval(_ m: Metric) -> Double { 2 }

    @MainActor static func rows(_ m: Metric, _ s: Sample) async -> [DetailRow] {
        switch m {
        case .battery:
            let temp = s.batteryTemp
            return await Task.detached { battery(smartBattery(), tempC: temp) }.value
        default: return []
        }
    }

    // MARK: Batterij en lader (AppleSmartBattery in het IORegistry)

    /// Alle eigenschappen van de batterij; leeg op een Mac zonder batterij. Het serienummer zit erin maar wordt nergens getoond.
    static func smartBattery() -> [String: Any] {
        let svc = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
        guard svc != 0 else { return [:] }
        defer { IOObjectRelease(svc) }
        var props: Unmanaged<CFMutableDictionary>?
        guard IORegistryEntryCreateCFProperties(svc, &props, kCFAllocatorDefault, 0) == KERN_SUCCESS else { return [:] }
        return props?.takeRetainedValue() as? [String: Any] ?? [:]
    }

    /// Capaciteiten staan los in het register of (nieuwere macOS) onder "BatteryData". Celspanningen en een
    /// productiedatum meldt macOS 26/27 niet meer aan gewone apps: staan ze er wel (oudere macOS), dan tonen we de cellen.
    static func battery(_ smart: [String: Any], tempC: Double?) -> [DetailRow] {
        let data = smart["BatteryData"] as? [String: Any] ?? [:]
        func num(_ k: String) -> NSNumber? { (smart[k] as? NSNumber) ?? (data[k] as? NSNumber) }
        func int(_ k: String) -> Int? { num(k)?.intValue }
        func volts(_ mV: Int) -> String { String(format: "%.2f V", Double(mV) / 1000) }
        var r: [DetailRow] = []
        let mV = int("Voltage").flatMap { $0 > 0 ? $0 : nil }
        if let mV { r.append(DetailRow(title: L("Voltage"), value: volts(mV), hint: L("The battery's voltage right now. It rises while charging and falls as the battery empties."))) }
        // Stroom staat als UInt64-bitpatroon in het register: int64Value geeft het teken terug (− = ontladen)
        if let mA = num("Amperage")?.int64Value, mA != 0, abs(mA) < 100_000 {
            let amps = String(format: "%.2f A", Double(abs(mA)) / 1000)
            r.append(DetailRow(title: L("Current"), value: mA < 0 ? L("\(amps) out") : L("\(amps) in"),
                               hint: L("How much current flows out of the battery (when your Mac runs on it) or into it (when charging) right now.")))
            if let mV {
                r.append(DetailRow(title: L("Battery power"), value: "\(one(Double(abs(mA)) * Double(mV) / 1e6)) W",
                                   hint: L("Voltage × current: what the battery delivers or takes in at this moment.")))
            }
        }
        if let t = tempC {
            r.append(DetailRow(title: L("Temperature"), value: "\(Int(t.rounded())) °C",
                               hint: L("The warmest sensor in the battery. Batteries age slowest between about 10 and 35 °C; when it gets hot, macOS pauses charging.")))
        }
        if let now = int("AppleRawCurrentCapacity"), now > 0 {
            r.append(DetailRow(title: L("Charge now"), value: "\(now) mAh",
                               hint: L("The charge in the battery right now, as the battery itself measures it. macOS smooths the percentage it shows, so the two can differ a little.")))
        }
        let design = int("DesignCapacity").flatMap { $0 > 0 ? $0 : nil }
        if let full = int("AppleRawMaxCapacity"), full > 0 {
            r.append(DetailRow(title: L("Full charge"), value: "\(full) mAh",
                               hint: L("What the battery can hold today when it is full. This slowly drops as the battery ages.")))
        }
        if let design {
            r.append(DetailRow(title: L("Design capacity"), value: "\(design) mAh", hint: L("What the battery could hold when it was new.")))
        }
        if let cells = (data["CellVoltage"] ?? smart["CellVoltage"]) as? [NSNumber], cells.count > 1, cells.allSatisfy({ $0.intValue > 0 }) {
            r.append(DetailRow(title: L("Cell voltages"), value: cells.map { String(format: "%.2f", $0.doubleValue / 1000) }.joined(separator: " · ") + " V",
                               hint: L("The voltage of each cell in the battery. They should stay within about 0.1 V of each other.")))
        }
        // Lader: alleen als er een hangt
        let a = smart["AdapterDetails"] as? [String: Any] ?? [:]
        func adapter(_ k: String) -> Int? { (a[k] as? NSNumber)?.intValue }
        if (smart["ExternalConnected"] as? NSNumber)?.boolValue == true, let w = adapter("Watts"), w > 0 {
            let name = ((a["Name"] as? String) ?? (a["Description"] as? String)).flatMap { $0.isEmpty ? nil : $0 }
            r.append(DetailRow(title: L("Charger"), value: [name, "\(w) W"].compactMap { $0 }.joined(separator: " · "),
                               hint: L("The name and the maximum power the connected charger reports.")))
            if let v = adapter("AdapterVoltage"), let c = adapter("Current"), v > 0, c > 0 {
                r.append(DetailRow(title: L("Charger agreed on"), value: "\(volt(v)) × \(amp(c))",
                                   hint: L("The voltage and current your Mac and the charger settled on. Your Mac only takes what it needs.")))
            }
            let t = smart["PowerTelemetryData"] as? [String: Any] ?? [:]
            if let p = (t["SystemPowerIn"] as? NSNumber)?.intValue, p > 0 {
                var text = "\(trim(Double(p) / 1000)) W"
                if let v = (t["SystemVoltageIn"] as? NSNumber)?.intValue, let c = (t["SystemCurrentIn"] as? NSNumber)?.intValue, v > 0, c > 0 { text += " (\(volt(v)) × \(amp(c)))" }
                r.append(DetailRow(title: L("Coming in now"), value: text,
                                   hint: L("The power your Mac draws from the charger at this moment, for the system and the battery together.")))
            }
        }
        return r
    }

    // MARK: Zelftest

    static func selfTest() {
        // Zelfde opbouw als AppleSmartBattery op deze Mac (macOS 27): capaciteiten onder BatteryData, stroom als UInt64-bitpatroon
        var smart: [String: Any] = ["Voltage": 12693, "Amperage": NSNumber(value: UInt64(bitPattern: -1553)), "ExternalConnected": false,
                                    "AdapterDetails": ["FamilyCode": 0], "Serial": "GEHEIM",
                                    "BatteryData": ["AppleRawCurrentCapacity": 5078, "AppleRawMaxCapacity": 5722, "DesignCapacity": 5760]]
        var b = battery(smart, tempC: 30.6)
        func value(_ rows: [DetailRow], _ title: String) -> String? { rows.first { $0.title == title }?.value }
        precondition(value(b, "Voltage") == "12.69 V" && value(b, "Current") == "1.55 A out" && value(b, "Battery power") == "19.7 W", "accu nu \(b.map(\.value))")
        precondition(value(b, "Temperature") == "31 °C" && value(b, "Charge now") == "5078 mAh" && value(b, "Full charge") == "5722 mAh"
                     && value(b, "Design capacity") == "5760 mAh", "accu capaciteit")
        precondition(value(b, "Cell voltages") == nil && value(b, "Charger") == nil && !b.contains { $0.value.contains("GEHEIM") }, "wat er niet is tonen we niet")
        // Aan de lader, met celspanningen (oudere macOS) en capaciteiten los in het register
        smart = ["Voltage": 12900, "Amperage": 2100, "ExternalConnected": true, "AppleRawMaxCapacity": 5722, "DesignCapacity": 0,
                 "AdapterDetails": ["Watts": 94, "AdapterVoltage": 20000, "Current": 4700, "Name": "96W USB-C Power Adapter"],
                 "PowerTelemetryData": ["SystemPowerIn": 17529, "SystemVoltageIn": 20400, "SystemCurrentIn": 860],
                 "BatteryData": ["CellVoltage": [4301, 4299, 4300]]]
        b = battery(smart, tempC: nil)
        precondition(value(b, "Current") == "2.10 A in" && value(b, "Full charge") == "5722 mAh" && value(b, "Temperature") == nil, "accu laden")
        precondition(value(b, "Cell voltages") == "4.30 · 4.30 · 4.30 V", "cellen \(value(b, "Cell voltages") ?? "")")
        precondition(value(b, "Charger") == "96W USB-C Power Adapter · 94 W" && value(b, "Charger agreed on") == "20 V × 4.7 A"
                     && value(b, "Coming in now") == "17.5 W (20.4 V × 0.86 A)", "lader \(b.map(\.value))")
        precondition(battery([:], tempC: nil).isEmpty, "geen batterij")
        // Echte waarden van deze Mac, ter controle (geen serienummers)
        print("meer details: batterij " + battery(smartBattery(), tempC: nil).map { "\($0.title) \($0.value)" }.joined(separator: " | "))
    }
}

// MARK: - Het blok zelf

struct MoreDetails: View {
    let metric: Metric, sample: Sample
    @AppStorage("moreDetails") private var openRaw = ""   // komma-lijst van metingen waar het blok open staat
    @State private var rows: [DetailRow]?
    @Environment(\.theme) private var theme

    private var open: Bool { openRaw.split(separator: ",").contains(Substring(metric.rawValue)) }

    var body: some View {
        DetailCard {
            VStack(alignment: .leading, spacing: 7) {
                Button { withAnimation(.snappy(duration: 0.25)) { toggle() } } label: {
                    HStack(spacing: 5) {
                        Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold)).rotationEffect(.degrees(open ? 90 : 0))
                        Text("More details")
                        Spacer(minLength: 0)
                    }
                    .font(theme.label(11)).foregroundStyle(theme.onRow(true)).contentShape(.rect)
                }
                .buttonStyle(.plain)
                if open, let rows {
                    if rows.isEmpty { Text("Nothing more to show on this Mac.").font(theme.label(12)).foregroundStyle(theme.onRow(true)) }
                    ForEach(rows.indices, id: \.self) { row(rows[$0]) }
                }
            }
        }
        // Lezen alleen zolang het open staat: bij openen en daarna op de meettik (of trager, zie MoreInfo.interval)
        .task(id: open ? Int(sample.time.timeIntervalSinceReferenceDate / MoreInfo.interval(metric)) : -1) {
            guard open else { return }
            let new = await MoreInfo.rows(metric, sample)
            if new != rows { rows = new }
        }
    }

    @ViewBuilder private func row(_ r: DetailRow) -> some View {
        if r.head {
            Text(r.title).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1).padding(.top, 5)
        } else {
            HStack(alignment: .firstTextBaseline) {
                TermLabel(r.title, hint: r.hint).font(theme.label(12)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                Spacer(minLength: 8)
                Text(r.value).font(theme.number(13)).foregroundStyle(theme.onRow())
                    .multilineTextAlignment(.trailing).fixedSize(horizontal: false, vertical: true)
            }
        }
    }

    private func toggle() {
        var ids = openRaw.split(separator: ",").map(String.init)
        if open { ids.removeAll { $0 == metric.rawValue } } else { ids.append(metric.rawValue) }
        openRaw = ids.joined(separator: ",")
    }
}
