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
    static func available(_ m: Metric) -> Bool { [.cpu, .gpu, .ram, .net, .ssd, .battery].contains(m) }

    /// Om de hoeveel seconden opnieuw lezen zolang het blok open staat (nooit vaker dan de meettik zelf).
    /// Batterij en geheugen: elke meting (één goedkope aanroep). De rest verandert traag of kost meer: eens per 10 s.
    static func interval(_ m: Metric) -> Double { m == .battery || m == .ram ? 2 : 10 }

    @MainActor static func rows(_ m: Metric, _ s: Sample) async -> [DetailRow] {
        switch m {
        case .battery:
            let temp = s.batteryTemp
            return await Task.detached { battery(smartBattery(), tempC: temp) }.value
        case .net:
            return await Task.detached {
                let c = NetConnection.current()
                return network(c, wifi: c.flatMap { $0.kind == "Wi-Fi" ? WifiDetail.current($0.iface) : nil })
            }.value
        case .ram: return vmStats().map { memory($0, page: Double(vm_kernel_page_size)) } ?? []
        case .gpu: return graphics() + displays(PortReader.displays())
        case .cpu: return processor(chip: sysctlString("machdep.cpu.brand_string"), thermal: ProcessInfo.processInfo.thermalState, lowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)
        case .ssd:
            SSDHealth.refresh()   // hooguit eens per uur echt lezen (achtergrond); hier alleen wat al bekend is
            return storage(SSDHealth.cached)
        default: return []
        }
    }

    // MARK: Geheugen (zelfde bron en indeling als Activiteitenweergave)

    private static let host = mach_host_self()

    static func vmStats() -> vm_statistics64? {
        var vm = vm_statistics64()
        var n = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.stride / MemoryLayout<integer_t>.stride)
        let ok = withUnsafeMutablePointer(to: &vm) { $0.withMemoryRebound(to: integer_t.self, capacity: Int(n)) { host_statistics64(host, HOST_VM_INFO64, $0, &n) } }
        return ok == KERN_SUCCESS ? vm : nil
    }

    /// "3.7 GB" (1024-tallig, zoals de rest van de RAM-weergave), onder 1 GB in MB.
    static func gib(_ bytes: Double) -> String { bytes >= 1_073_741_824 ? "\(one(bytes / 1_073_741_824)) GB" : "\(Int(bytes / 1_048_576)) MB" }

    /// Appgeheugen + wired + gecomprimeerd = "in gebruik"; bestanden in cache tellen niet mee (macOS geeft ze meteen vrij).
    static func memory(_ vm: vm_statistics64, page: Double) -> [DetailRow] {
        func gb(_ pages: Double) -> String { gib(pages * page) }
        let app = max(Double(vm.internal_page_count) - Double(vm.purgeable_count), 0)
        let packed = Double(vm.compressor_page_count), holds = Double(vm.total_uncompressed_pages_in_compressor)
        var r = [DetailRow(title: L("App memory"), value: gb(app), hint: L("Memory that your apps and their data are using right now.")),
                 DetailRow(title: L("Wired memory"), value: gb(Double(vm.wire_count)), hint: L("Memory that macOS itself needs. It can't be compressed or moved to the SSD.")),
                 DetailRow(title: L("Compressed"), value: holds > packed ? L("\(gb(packed)) (holds \(gb(holds)))") : gb(packed),
                           hint: L("Memory that macOS has squeezed together to make room. It holds more than the space it takes; unpacking it costs a little time.")),
                 DetailRow(title: L("Cached files"), value: gb(Double(vm.external_page_count) + Double(vm.purgeable_count)),
                           hint: L("Files kept in memory so they open faster next time. macOS frees this the moment apps need the room, so it doesn't count as used."))]
        if vm.swapouts > 0 {
            r.append(DetailRow(title: L("Swapped out since restart"), value: gb(Double(vm.swapouts)),
                               hint: L("How much memory macOS has moved to the SSD since the last restart because memory was full. If this keeps growing fast, more memory or fewer open apps would help.")))
            r.append(DetailRow(title: L("Swapped in since restart"), value: gb(Double(vm.swapins)),
                               hint: L("How much was read back from the SSD into memory. While that happens, apps feel slow.")))
        }
        return r
    }

    // MARK: Grafische chip en schermen (ingebouwd en extern; geen serienummers)

    /// "Apple M4 · 10 cores" uit de IOAccelerator; leeg als de Mac het niet meldt.
    static func graphics() -> [DetailRow] {
        let e = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("IOAccelerator"))
        guard e != 0 else { return [] }
        defer { IOObjectRelease(e) }
        func prop(_ k: String) -> Any? { IORegistryEntryCreateCFProperty(e, k as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() }
        return graphics(model: prop("model") as? String, cores: (prop("gpu-core-count") as? NSNumber)?.intValue)
    }

    static func graphics(model: String?, cores: Int?) -> [DetailRow] {
        guard let model, !model.isEmpty else { return [] }
        return [DetailRow(title: L("Graphics chip"), value: model + (cores.map { " · " + L("\($0) cores") } ?? ""),
                          hint: L("The graphics chip in this Mac and its number of cores. On Apple silicon it sits in the same chip as the processor and shares the memory."))]
    }

    static func displays(_ ds: [DisplayInfo]) -> [DetailRow] {
        ds.flatMap { d -> [DetailRow] in
            var r = [DetailRow(title: d.name, value: "", hint: "", head: true)]
            if d.w > 0 {
                // Geschaalde Retina-stand: macOS tekent op meer pixels dan het paneel heeft; dan telt wat het paneel toont
                let full = d.maxW > 0 && d.w * d.h >= d.maxW * d.maxH
                r.append(DetailRow(title: L("Resolution"), value: d.maxW == 0 ? Displays.res(d.w, d.h) : full ? Displays.res(d.maxW, d.maxH) + L(" (native)") : Displays.res(d.w, d.h) + L(" (native \(Displays.res(d.maxW, d.maxH)))"),
                                   hint: L("The number of pixels the display shows now. Native is the panel's own pixel count, where the picture is sharpest.")))
                if d.lw > 0, (d.lw, d.lh) != (full ? (d.maxW, d.maxH) : (d.w, d.h)) {
                    r.append(DetailRow(title: L("Looks like"), value: Displays.res(d.lw, d.lh),
                                       hint: L("The size macOS draws everything at. A smaller size here makes text and windows bigger; the display still uses all its pixels.")))
                }
            }
            if d.hz > 0 {
                r.append(DetailRow(title: L("Refresh rate"), value: Displays.hz(d.hz) + (d.maxHz > d.hz + 1 ? L(" (can do \(Displays.hz(d.maxHz)))") : ""),
                                   hint: L("How many times per second the picture is redrawn; higher is smoother. If the display can do more than it runs at, the cable, an adapter or a setting is holding it back.")))
            }
            r.append(DetailRow(title: L("HDR"), value: d.edr > 1 ? L("Up to \(trim(d.edr))× SDR brightness (EDR)") : L("No"),
                               hint: L("Whether the display can show highlights brighter than normal white, and how much brighter.")))
            r.append(DetailRow(title: L("Connection"), value: Displays.linkText(d),
                               hint: L("How the display is connected: built in, straight from the Mac's graphics (DisplayPort or HDMI), or through DisplayLink over USB.")))
            return r
        }
    }

    // MARK: Processor

    static func sysctlString(_ name: String) -> String? {
        var size = 0
        guard sysctlbyname(name, nil, &size, nil, 0) == 0, size > 1 else { return nil }
        var buf = [CChar](repeating: 0, count: size)
        return sysctlbyname(name, &buf, &size, nil, 0) == 0 ? String(cString: buf) : nil
    }

    static func processor(chip: String?, thermal: ProcessInfo.ThermalState, lowPower: Bool) -> [DetailRow] {
        var r: [DetailRow] = []
        if let chip, !chip.isEmpty { r.append(DetailRow(title: L("Chip"), value: chip, hint: L("The processor chip in this Mac."))) }
        let heat: String = switch thermal { case .nominal: L("No"); case .fair: L("A little"); case .serious: L("Yes, clearly"); default: L("Yes, heavily") }
        r.append(DetailRow(title: L("Slowed down by heat"), value: heat,
                           hint: L("When your Mac gets hot, macOS slows the processor down to cool it. Give it air, or close what keeps it busy.")))
        r.append(DetailRow(title: L("Low Power Mode"), value: lowPower ? L("On") : L("Off"),
                           hint: L("With Low Power Mode on, macOS keeps the processor slower to save energy. You set it in System Settings › Battery.")))
        return r
    }

    // MARK: SSD (wat de SSD zelf bijhoudt; al gelezen voor Checkup)

    static func storage(_ i: SSDHealth.Info?) -> [DetailRow] {
        guard let i else { return [] }
        let tb = i.writtenTB < 1 ? "\(Int((i.writtenTB * 1000).rounded())) GB" : i.writtenTB < 10 ? "\(one(i.writtenTB)) TB" : "\(Int(i.writtenTB.rounded())) TB"
        return [DetailRow(title: L("Wear"), value: "\(i.used)%", hint: L("How much of its expected life the SSD has used, according to the SSD itself. Above 80% it's wise to keep a good backup.")),
                DetailRow(title: L("Written in total"), value: tb, hint: L("Everything ever written to this SSD. Writing is what wears an SSD out.")),
                DetailRow(title: L("Switched on"), value: L("\(i.hours) hours"), hint: L("How long the SSD has been switched on in its whole life.")),
                DetailRow(title: L("Spare blocks"), value: "\(i.spare)%", hint: L("Reserve space the SSD keeps to replace worn-out parts. 100% means none of it has been needed yet."))]
    }

    // MARK: Verbinding: Wi-Fi of kabel (zelfde waarden en uitleg als op Network; nooit de netwerknaam of het BSSID)

    static func network(_ c: NetConnection?, wifi w: WifiDetail?) -> [DetailRow] {
        guard let c else { return [] }
        func row(_ t: NetTerm, _ v: String, title: String? = nil) -> DetailRow { DetailRow(title: title ?? t.title, value: v, hint: t.text) }
        var r = [row(.type, [c.kind, c.kind == "Wi-Fi" ? nil : c.name].compactMap { $0 }.joined(separator: " · "))]
        if let w {
            r.append(row(.signal, "\(w.rssi) dBm (\(w.word))"))
            if let snr = w.snr { r.append(row(.noise, "\(w.noise) dBm · SNR \(snr) dB", title: L("Noise"))) }
            if let ch = w.channel { r.append(row(.channel, ([String(ch)] + [w.band, w.width].compactMap { $0 }).joined(separator: " · "))) }
            if let phy = w.phy { r.append(row(.standard, phy)) }
            if let sec = w.security { r.append(row(.security, sec)) }
        } else if let m = c.media {
            if let now = c.linkMbit { r.append(row(.linkSpeed, EtherMedia.speed(Int(now)))) }
            if let top = m.maxMbit { r.append(row(.adapterMax, EtherMedia.speed(top))) }
            if let d = m.duplex { r.append(row(.duplex, d)) }
        }
        return r
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
        // Geheugen: de tellers van deze Mac op een gewoon moment (vm_stat), pagina's van 16 KB
        var vm = vm_statistics64()
        vm.internal_page_count = 245_192; vm.purgeable_count = 4; vm.wire_count = 207_959; vm.compressor_page_count = 427_137
        vm.total_uncompressed_pages_in_compressor = 752_693; vm.external_page_count = 122_431; vm.swapins = 17_560_347; vm.swapouts = 24_789_876
        let m = memory(vm, page: 16384)
        precondition(m.map(\.value) == ["3.7 GB", "3.2 GB", "6.5 GB (holds 11.5 GB)", "1.9 GB", "378.3 GB", "267.9 GB"], "geheugen \(m.map(\.value))")
        precondition(m.map(\.title) == ["App memory", "Wired memory", "Compressed", "Cached files", "Swapped out since restart", "Swapped in since restart"], "geheugen-titels")
        vm.swapouts = 0; vm.total_uncompressed_pages_in_compressor = 0; vm.compressor_page_count = 10
        precondition(memory(vm, page: 16384).map(\.value) == ["3.7 GB", "3.2 GB", "0 MB", "1.9 GB"] && gib(512 * 1_048_576) == "512 MB", "geen swap: regels weg")
        // Schermen: ingebouwd (geschaald) en extern op 30 Hz terwijl 60 kan
        let inside = DisplayInfo(id: 1, name: "Built-in Retina Display", link: .builtIn, w: 3420, h: 2214, lw: 1710, lh: 1107, hz: 120, maxW: 3024, maxH: 1964, maxHz: 120, edr: 16)
        let outside = DisplayInfo(id: 2, name: "Test Monitor", link: .native, transport: "HDMI", w: 2560, h: 1440, hz: 30, maxW: 3840, maxH: 2160, maxHz: 60, edr: 1)
        let dr = displays([inside, outside])
        precondition(dr.count == 11 && dr[0].head && dr[0].title == "Built-in Retina Display" && dr[6].title == "Test Monitor", "schermen koppen")
        precondition(dr[1...5].map(\.value) == ["3024 × 1964 (native)", "1710 × 1107", "120 Hz", "Up to 16× SDR brightness (EDR)", "Built in"], "ingebouwd \(dr[1...5].map(\.value))")
        precondition(dr[7...10].map(\.value) == ["2560 × 1440 (native 3840 × 2160)", "30 Hz (can do 60 Hz)", "No", "HDMI (straight from the Mac's graphics)"], "extern \(dr[7...10].map(\.value))")
        precondition(displays([]).isEmpty && graphics(model: "Apple M4", cores: 10).first?.value == "Apple M4 · 10 cores" && graphics(model: nil, cores: 10).isEmpty, "grafische chip")
        // Processor en SSD
        let p = processor(chip: "Apple M4", thermal: .serious, lowPower: true)
        precondition(p.map(\.value) == ["Apple M4", "Yes, clearly", "On"] && processor(chip: nil, thermal: .nominal, lowPower: false).map(\.value) == ["No", "Off"], "processor")
        let ssd = storage(SSDHealth.Info(used: 3, spare: 100, spareMin: 10, writtenTB: 41.2, hours: 10_000))
        precondition(ssd.map(\.value) == ["3%", "41 TB", "10000 hours", "100%"] && storage(nil).isEmpty, "ssd \(ssd.map(\.value))")
        // Wi-Fi: geen netwerknaam of BSSID; kabel: wat de adapter kan
        precondition(WifiDetail.securityName(11) == "WPA3 Personal" && WifiDetail.securityName(4) == "WPA2 Personal" && WifiDetail.securityName(0) == "None (open network)"
                     && WifiDetail.securityName(13) == "WPA2/WPA3 Personal" && WifiDetail.securityName(Int.max) == nil, "wifi-beveiliging")
        func conn(_ kind: String, media: EtherMedia? = nil) -> NetConnection {
            NetConnection(iface: "en9", kind: kind, ipv4: nil, ipv6: [], router: nil, dns: [], ipv6Internet: false, name: "USB LAN", linkMbit: media == nil ? nil : 100, media: media)
        }
        let w = WifiDetail(rssi: -64, noise: -95, rate: 864, channel: 44, band: "5 GHz", width: "160 MHz", phy: "Wi-Fi 6 (802.11ax)", security: "WPA3 Personal")
        let wr = network(conn("Wi-Fi"), wifi: w)
        precondition(wr.map(\.value) == ["Wi-Fi", "-64 dBm (fair)", "-95 dBm · SNR 31 dB", "44 · 5 GHz · 160 MHz", "Wi-Fi 6 (802.11ax)", "WPA3 Personal"], "wifi-regels \(wr.map(\.value))")
        precondition(wr.map(\.title) == ["Type", "Signal", "Noise", "Channel", "Standard", "Security"] && wr.allSatisfy { !$0.hint.isEmpty }, "wifi-titels")
        let bare = network(conn("Wi-Fi"), wifi: WifiDetail(rssi: -50, noise: 0, rate: 0, channel: nil, band: nil, width: nil, phy: nil))
        precondition(bare.map(\.title) == ["Type", "Signal"], "wifi zonder kanaal: niets verzinnen")
        let cr = network(conn("Ethernet", media: EtherMedia(activeMbit: 100, maxMbit: 1000, fullDuplex: true, linked: true)), wifi: nil)
        precondition(cr.map(\.value) == ["Ethernet · USB LAN", "100 Mbit/s", "1 Gbit/s", "Full duplex"] && network(nil, wifi: nil).isEmpty, "kabel-regels \(cr.map(\.value))")
        // Echte waarden van deze Mac, ter controle (geen serienummers)
        print("meer details: geheugen " + (vmStats().map { memory($0, page: Double(vm_kernel_page_size)) } ?? []).map { "\($0.title) \($0.value)" }.joined(separator: " | "))
        print("meer details: chip " + (graphics() + processor(chip: sysctlString("machdep.cpu.brand_string"), thermal: ProcessInfo.processInfo.thermalState, lowPower: ProcessInfo.processInfo.isLowPowerModeEnabled)).map { "\($0.title) \($0.value)" }.joined(separator: " | "))
        print("meer details: schermen " + MainActor.assumeIsolated { displays(PortReader.displays()) }.map { $0.head ? "[\($0.title)]" : "\($0.title) \($0.value)" }.joined(separator: " | "))
        let c = NetConnection.current()
        print("meer details: verbinding " + network(c, wifi: c.flatMap { $0.kind == "Wi-Fi" ? WifiDetail.current($0.iface) : nil }).map { "\($0.title) \($0.value)" }.joined(separator: " | "))
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
