import Foundation
import Combine
import IOKit

// MARK: - Poorten: wat zit er in welke USB-C-, Thunderbolt- en MagSafe-poort (alleen lezen, gewone gebruiker)
//
// Bronnen (IORegistry):
// - IOPort-vlak: per poort een AppleHPMInterfaceType* met daaronder CC (SOP = apparaat, SOP' = kabel-e-marker),
//   Power In (laadprofielen, winnaar), en de actieve transporten (USB2/USB3/CIO/DisplayPort) met het USB-apparaat erin.
// - IOUSB-vlak: de USB-boom (hubs en apparaten). IOThunderboltSwitch/-Port: Thunderbolt-apparaten en linksnelheid.
// - AppleSmartBattery: wat de lader nu echt levert (PowerTelemetryData).
// Al het ontleden zit in pure functies (zelftest met verzonnen data); alleen `PortReader.read()` praat met IOKit.

/// Eén registry-item met eigenschappen en kinderen, los van IOKit.
struct RegNode {
    var cls = "", name = "", id: UInt64 = 0
    var props: [String: Any] = [:]
    var children: [RegNode] = []
}

struct RawLine: Hashable { let key: String, value: String }
struct RawGroup: Hashable { let title: String, lines: [RawLine] }

/// Eén laadprofiel (PDO).
struct PDO: Hashable {
    var mV = 0, mA = 0, mW = 0
    var watts: Double { Double(mW > 0 ? mW : mV * mA / 1000) / 1000 }
    var text: String { "\(volt(mV)) × \(amp(mA))" }
}

struct Charger: Hashable {
    var source = ""            // "USB-PD", "TypeC", "Brick ID", "MagSafe" …
    var options: [PDO] = []
    var winning: PDO?
    var maxW: Double { options.map(\.watts).max() ?? winning?.watts ?? 0 }
}

/// Kabel volgens de e-marker (SOP' Discover Identity). Velden die de kabel niet meldt blijven nil.
struct Cable: Hashable {
    var vid = 0, pid = 0
    var type = "Cable"
    var active = false
    var mA: Int?, mV: Int?
    var speed: Int?             // 0 USB 2, 1 Gen 1, 2 Gen 2, 3 USB4 Gen 3, 4 USB4 Gen 4
    var epr = false
    var pd: Int?                // PD-specificatie volgens de kabel
    var latency: Int?           // bits 16..13: ruwe kabellengte-klasse
    var watts: Int? { mA.flatMap { a in mV.map { a * $0 / 1_000_000 } } }
}

struct PortLink: Hashable {
    var type = ""               // "USB3", "USB2", "CIO", "DisplayPort"
    var rate: String?, generation: String?
    var product: String?, maker: String?
    var tunneled = false
    var role: String?, security: String?
}

struct PortInfo: Identifiable, Hashable {
    var id = "", kind = "", number = 0
    var connected = false
    var supported: [String] = [], provisioned: [String] = [], active: [String] = [], unauthorized: [String] = []
    var links: [PortLink] = []
    var partnerVID: Int?, partnerPID: Int?, partnerKind: String?, partnerPD: Int?, partnerModal = false
    var cable: Cable?
    var activeCable = false, optical = false, superSpeed = false
    var plugEvents: Int?, orientation = 0
    var charger: Charger?            // het winnende profiel
    var sources: [Charger] = []      // alle stroombronnen (USB-PD, Type-C-stroom, Brick ID)
    var usbRoots: [UInt64] = []
    var liquid = false, overcurrent = 0
    var raw: [RawGroup] = []
    var isMagSafe: Bool { kind.hasPrefix("MagSafe") }
    var title: String { isMagSafe ? kind : "\(kind) port \(number)" }
}

struct USBDev: Identifiable, Hashable {
    var id: UInt64 = 0
    var name = "", maker = ""
    var vid = 0, pid = 0
    var bps: Double?
    var hub = false, billboard = false
    var powerMA: Int?
    var children: [USBDev] = []
    var raw: [RawLine] = []
    /// Alle apparaten eronder (zonder hubs).
    var leaves: [USBDev] { children.flatMap { $0.hub ? $0.leaves : [$0] + $0.leaves } }
}

struct TBDev: Identifiable, Hashable {
    var id: UInt64 = 0
    var name = "", vendor = ""
    var socket: String?
    var depth = 0
    var raw: [RawLine] = []
}

struct TBLink: Hashable {
    var socket = "", speed = 0, width = 0
    /// Lane-snelheid volgens USB4: 0x8 = Gen 2 (10), 0x4 = Gen 3 (20), 0x2 = Gen 4 (40 Gbit/s).
    var gbps: Int? {
        let per = [8: 10, 4: 20, 2: 40][speed]
        return per.map { $0 * max(width, 1) }
    }
}

/// Wat de Mac nu van de lader binnenkrijgt.
struct PowerNow: Hashable {
    var mV: Int?, mA: Int?, mW: Int?
    var adapterW: Int?, adapterMV: Int?, adapterMA: Int?
    var adapterName: String?
    var charging = false, full = false, external = false
}

struct PortsSnapshot: Hashable {
    var ports: [PortInfo] = []
    var usb: [USBDev] = []           // topniveau-apparaten (per controller)
    var tb: [TBDev] = []
    var tbLinks: [TBLink] = []
    var power: PowerNow?
}

struct PortNote: Hashable {
    enum Level { case good, info, warn }
    let text: String, level: Level
    var serious: Bool { level == .warn }
}

// MARK: - Ontleden (puur)

enum Ports {
    static func str(_ v: Any?) -> String? { (v as? String).flatMap { $0.isEmpty ? nil : $0 } }
    static func int(_ v: Any?) -> Int? { (v as? NSNumber)?.intValue }
    static func bool(_ v: Any?) -> Bool { (v as? NSNumber)?.boolValue ?? false }
    static func strs(_ v: Any?) -> [String] { v as? [String] ?? [] }

    /// VDO's staan als 4 bytes little-endian in het register.
    static func vdo(_ d: Data) -> UInt32? {
        guard d.count >= 4 else { return nil }
        let b = [UInt8](d.prefix(4))
        return UInt32(b[0]) | UInt32(b[1]) << 8 | UInt32(b[2]) << 16 | UInt32(b[3]) << 24
    }

    /// Kabel-VDO's: [ID-header, cert, product, kabel-VDO (, actieve-kabel-VDO 2)]. Stroom en snelheid staan in PD 2 en 3
    /// op dezelfde bits; de maximale spanning (bits 10..9) en EPR (bit 17) alleen vanaf PD 3.
    static func cable(vdos: [UInt32], productType: Int?, typeText: String?, specRev: Int, vid: Int?, pid: Int?) -> Cable {
        var c = Cable()
        let header = vdos.first ?? 0
        c.vid = vid ?? Int(header & 0xFFFF)
        c.pid = pid ?? (vdos.count > 2 ? Int(vdos[2] >> 16) : 0)
        let type = productType ?? Int(header >> 27 & 7)
        c.active = type == 4
        c.type = typeText ?? (type == 3 ? "Passive Cable" : type == 4 ? "Active Cable" : "Cable")
        c.pd = specRev
        guard vdos.count > 3 else { return c }
        let v = vdos[3]
        c.latency = Int(v >> 13 & 0xF)
        c.mA = [1: 3000, 2: 5000][Int(v >> 5 & 3)]
        let s = Int(v & 7)
        c.speed = s <= 4 ? s : nil
        if specRev >= 3 {
            c.mV = [0: 20000, 1: 30000, 2: 40000, 3: 50000][Int(v >> 9 & 3)]
            c.epr = v >> 17 & 1 == 1
        } else {
            c.mV = 20000
        }
        return c
    }

    /// Soort apparaat volgens de ID-header van SOP (UFP-bits 29..27, DFP-bits 25..23).
    static func partnerKind(_ header: UInt32) -> String? {
        let ufp = [1: "USB hub", 2: "USB device", 3: "power sink", 5: "adapter"][Int(header >> 27 & 7)]
        let dfp = [1: "USB hub", 2: "host", 3: "charger"][Int(header >> 23 & 7)]
        let all = [ufp, dfp].compactMap { $0 }
        return all.isEmpty ? nil : Array(NSOrderedSet(array: all)).compactMap { $0 as? String }.joined(separator: " + ")
    }

    static func pdos(_ v: Any?) -> [PDO] {
        ((v as? [[String: Any]]) ?? ((v as? NSSet)?.allObjects as? [[String: Any]]) ?? []).map { d in   // OSSet komt binnen als NSSet
            PDO(mV: int(d["Voltage (mV)"]) ?? int(d["Max Voltage (mV)"]) ?? 0, mA: int(d["Max Current (mA)"]) ?? 0, mW: int(d["Max Power (mW)"]) ?? 0)
        }
        .sorted { $0.mV < $1.mV }
    }

    /// Eén poort uit het IOPort-vlak.
    static func port(_ n: RegNode) -> PortInfo {
        let p = n.props
        var port = PortInfo(id: str(p["PortDescription"]) ?? str(p["Description"]) ?? n.name,
                            kind: str(p["PortTypeDescription"]) ?? "Port", number: int(p["PortNumber"]) ?? 0)
        port.connected = bool(p["ConnectionActive"])
        port.supported = strs(p["TransportsSupported"]).filter { $0 != "CC" }
        port.provisioned = strs(p["TransportsProvisioned"]); port.active = strs(p["TransportsActive"])
        port.unauthorized = strs(p["TransportsUnauthorized"])
        port.activeCable = bool(p["ActiveCable"]); port.optical = bool(p["OpticalCable"])
        port.superSpeed = bool(p["IOAccessoryUSBSuperSpeedActive"])
        port.plugEvents = int(p["Plug Event Count"]); port.orientation = int(p["PlugOrientation"]) ?? 0
        port.liquid = bool(p["LDCM_LiquidDetected"]); port.overcurrent = int(p["Overcurrent Count"]) ?? 0
        var raw = [RawGroup(title: "Port", lines: rawLines(p))]
        for c in n.children {
            let q = c.props
            let transport = str(q["TransportTypeDescription"]), feature = str(q["FeatureTypeDescription"])
            if transport == "CC" {
                for comp in c.children {
                    let m = comp.props["Metadata"] as? [String: Any] ?? [:]
                    let vdos = (m["VDOs"] as? [Data] ?? []).compactMap(vdo)
                    let addr = str(comp.props["AddressDescription"]) ?? comp.name
                    raw.append(RawGroup(title: addr == "SOP'" ? "Cable e-marker (SOP')" : addr == "SOP" ? "Device identity (SOP)" : addr,
                                        lines: rawLines(comp.props) + vdos.enumerated().map { RawLine(key: "VDO \($0)", value: String(format: "0x%08X", $1)) }))
                    if addr == "SOP" {
                        port.partnerVID = int(comp.props["Vendor ID"]); port.partnerPID = int(comp.props["Product ID"])
                        port.partnerKind = vdos.first.flatMap(partnerKind)
                        port.partnerModal = (vdos.first ?? 0) >> 26 & 1 == 1
                        port.partnerPD = int(comp.props["Specification Revision"])
                    } else if addr == "SOP'" {
                        port.cable = cable(vdos: vdos, productType: int(comp.props["Product Type"]) ?? int(m["Product Type"]),
                                           typeText: str(comp.props["Product Type Description"]) ?? str(m["Product Type Description"]),
                                           specRev: int(comp.props["Specification Revision"]) ?? 3,
                                           vid: int(comp.props["Vendor ID"]), pid: int(comp.props["Product ID"]))
                    }
                }
            } else if feature == "Power In" {
                for s in c.children {
                    raw.append(RawGroup(title: "Power source: " + (str(s.props["PowerSourceName"]) ?? s.name), lines: rawLines(s.props)))
                    var ch = Charger(source: str(s.props["PowerSourceName"]) ?? s.name, options: pdos(s.props["PowerSourceOptions"]))
                    ch.winning = s.props["WinningPowerSourceOption"].flatMap { pdos([$0]).first }
                    port.sources.append(ch)
                    if ch.winning != nil, (ch.winning?.watts ?? 0) >= (port.charger?.winning?.watts ?? 0) { port.charger = ch }
                }
            } else if let t = transport, bool(q["Active"]) {
                var l = PortLink(type: t, rate: str(q["DataRateDescription"]), generation: str(q["GenerationDescription"]))
                l.product = str(q["Product"]); l.maker = str(q["Manufacturer"]); l.tunneled = bool(q["Tunneled"])
                l.role = str(q["DataRoleDescription"]); l.security = str(q["TRM_ProfileDescription"])
                port.links.append(l)
                port.usbRoots += c.children.map(\.id)
                raw.append(RawGroup(title: "Link: \(t)", lines: rawLines(q)))
            }
        }
        port.raw = raw
        return port
    }

    static func usb(_ n: RegNode) -> USBDev {
        let p = n.props
        var d = USBDev(id: n.id)
        d.name = str(p["USB Product Name"]) ?? str(p["kUSBProductString"]) ?? n.name.components(separatedBy: "@").first ?? n.name
        d.vid = int(p["idVendor"]) ?? 0; d.pid = int(p["idProduct"]) ?? 0
        d.maker = str(p["USB Vendor Name"]) ?? str(p["kUSBVendorString"]) ?? String(format: "vendor 0x%04X", d.vid)
        d.bps = (p["UsbLinkSpeed"] as? NSNumber)?.doubleValue
        d.hub = int(p["bDeviceClass"]) == 9
        d.billboard = int(p["bDeviceClass"]) == 0x11 || d.name.localizedCaseInsensitiveContains("billboard")
        d.powerMA = int(p["UsbPowerSinkAllocation"])
        d.raw = rawLines(p)
        d.children = n.children.filter { $0.props["idVendor"] != nil }.map(usb)
        return d
    }

    // MARK: Teksten

    static func speedText(_ bps: Double) -> String {
        bps >= 1e9 ? "\(trim(bps / 1e9)) Gbit/s" : "\(trim(bps / 1e6)) Mbit/s"
    }

    static func cableSpeedText(_ s: Int) -> String {
        switch s {
        case 0: "USB 2.0 only (480 Mbit/s)"
        case 1: "USB 3.2 Gen 1 (5 Gbit/s)"
        case 2: "USB 3.2 Gen 2 (10 Gbit/s; 20 Gbit/s for Thunderbolt)"
        case 3: "USB4 / Thunderbolt (40 Gbit/s)"
        default: "USB4 Gen 4 (80 Gbit/s)"
        }
    }

    static func cableText(_ c: Cable, activeFlag: Bool, optical: Bool) -> String {
        var kind = c.type.replacingOccurrences(of: " Cable", with: "")
        if optical { kind = "Optical" } else if activeFlag && !c.active { kind = "Active" }
        var parts = ["\(kind) USB-C cable"]
        if let a = c.mA { parts.append("\(amp(a))" + (c.watts.map { " / \($0) W" } ?? "") + (c.epr ? " (EPR)" : "")) }
        if let s = c.speed { parts.append(cableSpeedText(s)) }
        parts.append(String(format: "e-marker %04X:%04X", c.vid, c.pid))
        return parts.joined(separator: " · ")
    }

    static func linkText(_ l: PortLink, tb: TBLink?) -> String {
        var s: String
        switch l.type {
        case "USB2": s = "USB 2.0"
        case "USB3": s = l.generation ?? "USB 3"
        case "CIO": s = "Thunderbolt / USB4"
        case "DisplayPort": s = "DisplayPort (video)"
        default: s = l.generation ?? l.type
        }
        if l.type == "CIO", let g = tb?.gbps { s += " at \(g) Gbit/s" + (tb!.width > 1 ? " (\(tb!.width) lanes)" : "") }
        else if let r = l.rate { s += " at " + r.replacingOccurrences(of: "Gbps", with: "Gbit/s").replacingOccurrences(of: "Mbps", with: "Mbit/s") }
        return s + (l.tunneled ? " (tunneled)" : "")
    }

    static func supportedText(_ s: [String]) -> String? {
        let names = s.filter { $0 != "CC" }.map(transportName)
        return names.isEmpty ? nil : "Supports " + names.joined(separator: ", ")
    }

    static func transportName(_ t: String) -> String {
        ["CC": "CC (power talk)", "USB2": "USB 2", "USB3": "USB 3", "CIO": "Thunderbolt/USB4", "DisplayPort": "DisplayPort"][t] ?? t
    }

    /// Naam van een USB-snelheid zoals mensen hem kennen.
    static func usbSpeedName(_ bps: Double) -> String {
        switch bps {
        case ..<2e6: "Low Speed"
        case ..<100e6: "Full Speed"
        case ..<1e9: "High Speed (USB 2)"
        case ..<6e9: "SuperSpeed (USB 3.2 Gen 1)"
        case ..<15e9: "SuperSpeed+ (USB 3.2 Gen 2)"
        default: "SuperSpeed+ (USB 3.2 Gen 2×2)"
        }
    }

    /// PD-revisie zoals Apple hem meldt (1 = 1.0, 2 = 2.0, 3 = 3.x).
    static func pdText(_ r: Int) -> String { r >= 3 ? "USB PD 3.x" : "USB PD \(r).0" }

    /// Hoogste datasnelheid die de kabel volgens zijn e-marker aankan (bit/s).
    static func cableMax(_ s: Int, thunderbolt: Bool) -> Double {
        [0: 480e6, 1: 5e9, 2: thunderbolt ? 20e9 : 10e9, 3: 40e9, 4: 80e9][s] ?? 80e9
    }

    /// Unieke apparaten (geen hubs) achter een poort.
    static func devices(_ roots: [USBDev]) -> [USBDev] {
        var seen = Set<UInt64>()
        return roots.flatMap { $0.hub ? $0.leaves : [$0] + $0.leaves }.filter { seen.insert($0.id).inserted }
    }

    /// Wat zit erin, in gewone woorden, met het laadvermogen erachter ("Hub or dock with 4 devices · 90 W charger").
    static func headline(_ p: PortInfo, roots: [USBDev], tb: [TBDev]) -> String {
        guard p.connected else { return "Nothing connected" }
        let what: String?
        if !tb.isEmpty { what = tb.map { $0.vendor.isEmpty ? $0.name : "\($0.name) (\($0.vendor))" }.joined(separator: ", ") }
        else if !roots.isEmpty {
            let n = devices(roots).count
            what = roots.contains(where: \.hub) ? (n == 0 ? "USB hub" : "Hub or dock with \(n) device\(n == 1 ? "" : "s")")
                : "\(roots[0].name) (\(roots[0].maker))"
        }
        else if p.links.contains(where: { $0.type == "DisplayPort" }) { what = "Display (DisplayPort)" }
        else if let k = p.partnerKind, p.charger == nil || k != "charger" { what = "USB-C \(k)" }
        else { what = nil }
        let charger = p.charger.map { "\(trim($0.maxW)) W charger" }
        switch (what, charger) {
        case let (w?, c?): return "\(w) · \(c)"
        case let (w?, nil): return w
        case let (nil, c?): return (p.isMagSafe ? "MagSafe " : "USB-C ") + c
        default: return "Something is connected"
        }
    }

    /// Eén regel over de data: "SuperSpeed data active · USB 3.x at 10 Gbit/s".
    static func dataLine(_ p: PortInfo, tb: TBLink?) -> String? {
        guard p.connected else { return nil }
        let types = Set(p.links.map(\.type))
        let label = types.contains("CIO") ? "Thunderbolt/USB4 data active"
            : types.contains("USB3") ? "SuperSpeed data active"
            : types.contains("USB2") ? "USB 2 data active"
            : types.contains("DisplayPort") ? "Video only (DisplayPort)" : "No data connection (power only)"
        let best = p.links.first { $0.type == "CIO" } ?? p.links.first { $0.type == "USB3" } ?? p.links.first
        return [label, best.map { linkText($0, tb: tb) }].compactMap { $0 }.joined(separator: " · ")
    }

    static func chargerText(_ c: Charger) -> String {
        var s = "Charger up to \(trim(c.maxW)) W"
        if let w = c.winning { s += " · agreed on \(w.text) (\(trim(w.watts)) W)" }
        return s
    }

    static func nowText(_ n: PowerNow) -> String? {
        guard let mW = n.mW, mW > 0 else { return nil }
        var s = "Drawing \(trim(Double(mW) / 1000)) W now"
        if let v = n.mV, let a = n.mA { s += " (\(volt(v)) × \(amp(a)))" }
        s += n.full ? " · battery full" : n.charging ? " · battery charging" : " · battery not charging"
        return s
    }

    /// Welke poort de lader levert die de batterij nu gebruikt: profiel gelijk aan AdapterDetails, anders de sterkste.
    static func livePort(_ ports: [PortInfo], _ n: PowerNow?) -> String? {
        guard let n, n.external else { return nil }
        let withCharger = ports.filter { $0.charger?.winning != nil }
        return (withCharger.first { $0.charger?.winning?.mV == n.adapterMV && $0.charger?.winning?.mA == n.adapterMA }
                ?? withCharger.max { ($0.charger?.winning?.watts ?? 0) < ($1.charger?.winning?.watts ?? 0) })?.id
    }

    /// Oordelen (groen/oranje) en tips: alleen wat we echt kunnen zien.
    static func notes(_ p: PortInfo, roots: [USBDev], tb: TBLink?) -> [PortNote] {
        var out: [PortNote] = []
        func add(_ t: String, _ l: PortNote.Level) { out.append(PortNote(text: t, level: l)) }
        if p.liquid { add("Liquid or dirt detected in this port. Unplug and let it dry.", .warn) }
        if !p.unauthorized.isEmpty {
            add("macOS is holding back \(p.unauthorized.map(transportName).joined(separator: ", ")) until you allow this accessory.", .warn)
        }
        if p.overcurrent > 0 { add("Over-current protection cut power on this port \(p.overcurrent)× (a device drew too much).", .warn) }
        guard p.connected else { return out }
        let types = Set(p.links.map(\.type)), c = p.cable
        // Laden
        if let w = p.charger?.winning {
            let capped = !p.isMagSafe && w.mA >= 3000 && w.mV >= 20000 && (c?.mA ?? 3000) <= 3000
            if capped {
                add(c == nil ? "No e-marker in this cable, so charging stops at 60 W (3 A). Above 60 W needs a 5 A cable (and a charger that can)."
                             : "This cable is rated 3 A, so charging stops at 60 W. Above 60 W needs a 5 A cable (and a charger that can).", .info)
            } else if let cw = c?.watts, Double(cw) < w.watts {
                add("The cable is rated \(cw) W but the charger agreed on \(trim(w.watts)) W. Use a cable rated for at least that.", .warn)
            } else {
                add("Charging OK: charger\(c == nil ? "" : " and cable") agree on up to \(trim(w.watts)) W; the Mac takes what it needs.", .good)
            }
        }
        // Data
        let measured = roots.compactMap(\.bps).max() ?? 0
        if let s = c?.speed, measured > cableMax(s, thunderbolt: false) * 1.01, !types.contains("CIO") {
            add("The cable's e-marker says up to \(speedText(cableMax(s, thunderbolt: false))), but the link runs at \(speedText(measured)). One of the two is wrong (e-markers are often mislabelled).", .info)
        }
        if types.contains("USB2"), types.isDisjoint(with: ["USB3", "CIO"]), measured >= 480e6 {
            if c?.speed == 0 { add("This cable is USB 2 only, so data runs at 480 Mbit/s. Use a USB 3 or Thunderbolt cable if the device can go faster.", .warn) }
            else { add("Data runs at USB 2 speed (480 Mbit/s). If this device can go faster, try a USB 3 or Thunderbolt cable.", .info) }
        }
        if types.contains("CIO"), let s = c?.speed, s <= 2 {
            add("This cable holds Thunderbolt/USB4 back to \(speedText(cableMax(s, thunderbolt: true))) or less. A 40 Gbit/s cable is faster.", .warn)
        }
        if types.contains("CIO") || types.contains("USB3") { add(types.contains("CIO") ? "Thunderbolt/USB4 link is up." : "Fast data (SuperSpeed) link is up.", .good) }
        if roots.contains(where: \.billboard) || devices(roots).contains(where: \.billboard) {
            add("Billboard device present: the dock or adapter reports that an extra mode (such as video over USB-C) is not active or not supported on this connection.", .info)
        }
        return out
    }

    // MARK: Ruwe waarden (voor "Advanced")

    static let skipKeys: Set<String> = ["IOGeneralInterest", "IOPowerManagement", "CFBundleIdentifier", "CFBundleIdentifierKernel",
        "IOPersonalityPublisher", "IOMatchCategory", "IOProbeScore", "IOProviderClass", "IOUserClientClass", "IOMatchedAtBoot",
        "IOReportLegend", "IOReportLegendPublic", "IOCFPlugInTypes", "Owner"]

    static func rawLines(_ p: [String: Any]) -> [RawLine] {
        p.keys.filter { !skipKeys.contains($0) && !$0.hasPrefix("IOFunctionParent") }.sorted().map { RawLine(key: $0, value: rawValue(p[$0] as Any)) }
    }

    static func rawValue(_ v: Any) -> String {
        let s: String
        switch v {
        case let n as NSNumber where CFGetTypeID(n) == CFBooleanGetTypeID(): s = n.boolValue ? "Yes" : "No"
        case let n as NSNumber: s = n.stringValue
        case let t as String: s = t
        case let d as Data: s = "<" + d.prefix(48).map { String(format: "%02x", $0) }.joined() + (d.count > 48 ? "…" : "") + ">"
        case let set as NSSet: s = "[" + set.allObjects.map(rawValue).sorted().joined(separator: ", ") + "]"
        case let a as [Any]: s = "(" + a.map(rawValue).joined(separator: ", ") + ")"
        case let d as [String: Any]: s = "{" + d.keys.sorted().map { "\($0)=\(rawValue(d[$0] as Any))" }.joined(separator: ", ") + "}"
        default: s = String(describing: v)
        }
        return s.count > 600 ? String(s.prefix(600)) + "…" : s
    }

    // MARK: Zelftest (verzonnen data)

    static func selfTest() {
        precondition(vdo(Data([0x34, 0x12, 0x00, 0x18])) == 0x1800_1234 && vdo(Data([1, 2])) == nil, "vdo")
        // Passieve kabel, 5 A, 20 V, Gen 2
        let header: UInt32 = 3 << 27 | 0x1234, product: UInt32 = 0x5678 << 16 | 0x0100
        let c5 = cable(vdos: [header, 0, product, 2 << 18 | 2 << 5 | 2], productType: nil, typeText: nil, specRev: 3, vid: nil, pid: nil)
        precondition(c5.vid == 0x1234 && c5.pid == 0x5678 && !c5.active && c5.type == "Passive Cable", "kabel id")
        precondition(c5.mA == 5000 && c5.mV == 20000 && c5.watts == 100 && c5.speed == 2 && !c5.epr, "kabel 5A")
        // Actieve EPR-kabel, 50 V, Gen 3
        let epr = cable(vdos: [4 << 27 | 0x1234, 0, product, 1 << 17 | 3 << 9 | 2 << 5 | 3], productType: nil, typeText: nil, specRev: 3, vid: nil, pid: nil)
        precondition(epr.active && epr.epr && epr.mV == 50000 && epr.watts == 250 && epr.speed == 3, "kabel epr")
        // PD 2: bits 10..9 betekenen iets anders, dus 20 V
        let old = cable(vdos: [header, 0, product, 3 << 9 | 1 << 5], productType: 3, typeText: nil, specRev: 2, vid: 7, pid: 8)
        precondition(old.mV == 20000 && old.mA == 3000 && old.watts == 60 && old.speed == 0 && old.vid == 7, "kabel pd2")
        precondition(cableText(c5, activeFlag: false, optical: false) == "Passive USB-C cable · 5 A / 100 W · USB 3.2 Gen 2 (10 Gbit/s; 20 Gbit/s for Thunderbolt) · e-marker 1234:5678", "kabeltekst")
        precondition(partnerKind(2 << 27) == "USB device" && partnerKind(3 << 23) == "charger" && partnerKind(0) == nil, "partner")
        precondition(speedText(480e6) == "480 Mbit/s" && speedText(1.5e6) == "1.5 Mbit/s" && speedText(1e10) == "10 Gbit/s", "snelheid")
        precondition(TBLink(socket: "1", speed: 4, width: 2).gbps == 40 && TBLink(speed: 3).gbps == nil, "tb")

        // Verzonnen poort: e-marker 3 A / USB 2, lader 20 V 3 A, USB 2-hub met twee apparaten
        func le(_ v: UInt32) -> Data { Data([UInt8(v & 0xFF), UInt8(v >> 8 & 0xFF), UInt8(v >> 16 & 0xFF), UInt8(v >> 24)]) }
        let sopP = RegNode(cls: "IOPortTransportComponentCCUSBPDSOPp", name: "SOP'", props: [
            "AddressDescription": "SOP'", "Product Type": 3, "Product Type Description": "Passive Cable", "Specification Revision": 3,
            "Vendor ID": 0x1234, "Product ID": 0x5678, "Metadata": ["VDOs": [le(header), le(0), le(product), le(2 << 18 | 1 << 5)]]])
        let sop = RegNode(name: "SOP", props: ["AddressDescription": "SOP", "Vendor ID": 0x4321, "Metadata": ["VDOs": [le(1 << 27 | 0x4321)]]])
        let opts: [[String: Any]] = [["Voltage (mV)": 20000, "Max Current (mA)": 3000, "Max Power (mW)": 60000],
                                     ["Voltage (mV)": 5000, "Max Current (mA)": 3000, "Max Power (mW)": 15000]]
        let node = RegNode(cls: "AppleHPMInterfaceType10", name: "Port-USB-C@3", props: [
            "PortDescription": "Port-USB-C@3", "PortTypeDescription": "USB-C", "PortNumber": 3, "ConnectionActive": true,
            "TransportsSupported": ["CC", "USB2", "USB3"], "TransportsUnauthorized": [String](), "Overcurrent Count": 0, "LDCM_LiquidDetected": false],
            children: [
                RegNode(name: "CC", props: ["TransportTypeDescription": "CC", "Active": true], children: [sop, sopP]),
                RegNode(name: "Power In", props: ["FeatureTypeDescription": "Power In"], children: [
                    RegNode(name: "TypeC", props: ["PowerSourceName": "TypeC", "PowerSourceOptions": [opts[1]]]),
                    RegNode(name: "USB-PD", props: ["PowerSourceName": "USB-PD", "PowerSourceOptions": NSSet(array: opts), "WinningPowerSourceOption": opts[0]])]),
                RegNode(name: "USB2", props: ["TransportTypeDescription": "USB2", "Active": true, "DataRateDescription": "480 Mbps (High Speed)"],
                        children: [RegNode(cls: "IOUSBHostDevice", name: "Hub@1", id: 42)]),
                RegNode(name: "USB3", props: ["TransportTypeDescription": "USB3", "Active": false])])
        let p = port(node)
        precondition(p.title == "USB-C port 3" && p.connected && p.supported == ["USB2", "USB3"], "poort")
        precondition(p.cable?.mA == 3000 && p.cable?.speed == 0 && p.partnerKind == "USB hub" && p.partnerVID == 0x4321, "poort kabel")
        precondition(p.charger?.source == "USB-PD" && p.charger?.winning?.mV == 20000 && p.charger?.maxW == 60 && p.charger?.options.first?.mV == 5000, "poort lader")
        precondition(p.links.map(\.type) == ["USB2"] && p.usbRoots == [42], "poort links")
        precondition(p.raw.contains { $0.title == "Cable e-marker (SOP')" && $0.lines.contains(RawLine(key: "VDO 3", value: "0x00080020")) }, "poort ruw")
        precondition(linkText(p.links[0], tb: nil) == "USB 2.0 at 480 Mbit/s (High Speed)", "linktekst")

        let hubNode = RegNode(name: "Hub@1", id: 42, props: ["USB Product Name": "Test Hub", "idVendor": 1, "bDeviceClass": 9, "UsbLinkSpeed": 480_000_000],
                              children: [RegNode(name: "Key@2", id: 43, props: ["kUSBProductString": "Test Keys", "idVendor": 2, "UsbLinkSpeed": 1_500_000]),
                                         RegNode(name: "Disk@3", id: 44, props: ["idVendor": 0xABCD, "UsbLinkSpeed": 480_000_000, "UsbPowerSinkAllocation": 500]),
                                         RegNode(name: "NotADevice", id: 45)])
        let hub = usb(hubNode)
        precondition(hub.hub && hub.children.count == 2 && hub.leaves.count == 2, "usb boom")
        precondition(hub.children[1].name == "Disk" && hub.children[1].maker == "vendor 0xABCD" && hub.children[1].powerMA == 500, "usb naam")
        precondition(headline(p, roots: [hub], tb: []) == "Hub or dock with 2 devices · 60 W charger" && dataLine(p, tb: nil) == "USB 2 data active · USB 2.0 at 480 Mbit/s (High Speed)", "kop hub")
        let ns = notes(p, roots: [hub], tb: nil)
        precondition(ns.contains { $0.text.hasPrefix("This cable is rated 3 A") } && ns.contains { $0.text.hasPrefix("This cable is USB 2 only") && $0.serious }, "tips")
        // e-marker zegt 5 Gbit/s, link loopt op 10; billboard achter een hub
        var fast = p; fast.cable?.speed = 1; fast.links = [PortLink(type: "USB3")]; fast.charger = nil
        var fastHub = USBDev(id: 50); fastHub.hub = true; fastHub.bps = 10e9
        var bb = USBDev(id: 51); bb.billboard = true; fastHub.children = [bb]
        let fn = notes(fast, roots: [fastHub], tb: nil)
        precondition(fn.contains { $0.text.contains("says up to 5 Gbit/s, but the link runs at 10 Gbit/s") } && fn.contains { $0.text.hasPrefix("Billboard") }
                     && fn.contains { $0.level == .good }, "verschil")
        precondition(usbSpeedName(480e6) == "High Speed (USB 2)" && usbSpeedName(10e9) == "SuperSpeed+ (USB 3.2 Gen 2)" && pdText(2) == "USB PD 2.0", "namen")

        var empty = PortInfo(id: "x", kind: "USB-C", number: 1)
        precondition(headline(empty, roots: [], tb: []) == "Nothing connected" && notes(empty, roots: [], tb: nil).isEmpty, "leeg")
        empty.liquid = true
        precondition(notes(empty, roots: [], tb: nil).first?.serious == true, "vocht")
        var mag = PortInfo(id: "m", kind: "MagSafe 3", number: 1, connected: true)
        mag.charger = Charger(source: "MagSafe", options: [PDO(mV: 20000, mA: 4700, mW: 94000)], winning: PDO(mV: 20000, mA: 4700, mW: 94000))
        precondition(mag.title == "MagSafe 3" && headline(mag, roots: [], tb: []) == "MagSafe 94 W charger" && notes(mag, roots: [], tb: nil).map(\.level) == [.good], "magsafe")
        let now = PowerNow(mV: 20400, mA: 860, mW: 17529, adapterMV: 20000, adapterMA: 4700, charging: true, external: true)
        precondition(livePort([p, mag], now) == "m" && livePort([p, mag], nil) == nil, "lader poort")
        precondition(nowText(now) == "Drawing 17.5 W now (20.4 V × 0.86 A) · battery charging", "nu \(nowText(now) ?? "")")
        precondition(chargerText(mag.charger!) == "Charger up to 94 W · agreed on 20 V × 4.7 A (94 W)", "lader")
        precondition(rawValue(true as NSNumber) == "Yes" && rawValue(Data([0xAB, 1])) == "<ab01>" && rawValue(["b": 1, "a": "x"] as [String: Any]) == "{a=x, b=1}", "ruw")
        print("poorten ok")
    }
}

/// "20 V", "4.5 A", "17.5": hooguit één decimaal, zonder ",0".
func trim(_ x: Double) -> String { x == x.rounded() ? String(Int(x)) : String(format: "%.1f", x) }
func volt(_ mV: Int) -> String { trim((Double(mV) / 100).rounded() / 10) + " V" }
func amp(_ mA: Int) -> String { (mA % 1000 == 0 || mA % 100 == 0 ? trim(Double(mA) / 1000) : String(format: "%.2f", Double(mA) / 1000)) + " A" }

// MARK: - Lezen uit IOKit

enum PortReader {
    static func read() -> PortsSnapshot {
        var s = PortsSnapshot()
        let root = IORegistryGetRootEntry(kIOMainPortDefault)
        defer { IOObjectRelease(root) }
        s.ports = children(root, "IOPort").map { Ports.port(node($0, "IOPort", depth: 3)) }
            .sorted { ($0.isMagSafe ? 1 : 0, $0.number) < ($1.isMagSafe ? 1 : 0, $1.number) }
        // USB-boom: wortel → controllers → apparaten
        s.usb = children(root, "IOUSB").flatMap { ctrl in children(ctrl, "IOUSB").map { Ports.usb(node($0, "IOUSB", depth: 8)) } }
        s.tb = services("IOThunderboltSwitch").compactMap(tbDevice)
        s.tbLinks = services("IOThunderboltPort").compactMap { e in
            defer { IOObjectRelease(e) }
            guard let socket = prop(e, "Socket ID") as? String, let w = Ports.int(prop(e, "Current Link Width")) else { return nil }
            return TBLink(socket: socket, speed: Ports.int(prop(e, "Current Link Speed")) ?? 0, width: w)
        }
        s.power = battery()
        return s
    }

    private static func tbDevice(_ e: io_registry_entry_t) -> TBDev? {
        defer { IOObjectRelease(e) }
        guard let depth = Ports.int(prop(e, "Depth")), depth > 0 else { return nil }
        let n = node(e, kIOServicePlane, depth: 0)
        var d = TBDev(id: n.id, name: Ports.str(n.props["Device Model Name"]) ?? "Thunderbolt device",
                      vendor: Ports.str(n.props["Device Vendor Name"]) ?? "", depth: depth, raw: Ports.rawLines(n.props.filter { $0.key != "DROM" }))
        // Poort: omhoog tot een Thunderbolt-poort met "Socket ID"
        var cur = e
        IOObjectRetain(cur)
        for _ in 0..<12 {
            var parent: io_registry_entry_t = 0
            guard IORegistryEntryGetParentEntry(cur, kIOServicePlane, &parent) == KERN_SUCCESS else { break }
            IOObjectRelease(cur); cur = parent
            if let sock = prop(cur, "Socket ID") as? String { d.socket = sock; break }
        }
        IOObjectRelease(cur)
        return d
    }

    private static func battery() -> PowerNow? {
        let e = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
        guard e != 0 else { return nil }
        defer { IOObjectRelease(e) }
        let t = prop(e, "PowerTelemetryData") as? [String: Any] ?? [:]
        let a = prop(e, "AdapterDetails") as? [String: Any] ?? [:]
        return PowerNow(mV: Ports.int(t["SystemVoltageIn"]), mA: Ports.int(t["SystemCurrentIn"]), mW: Ports.int(t["SystemPowerIn"]),
                        adapterW: Ports.int(a["Watts"]), adapterMV: Ports.int(a["AdapterVoltage"]), adapterMA: Ports.int(a["Current"]),
                        adapterName: Ports.str(a["Name"]) ?? Ports.str(a["Description"]),
                        charging: Ports.bool(prop(e, "IsCharging")), full: Ports.bool(prop(e, "FullyCharged")),
                        external: Ports.bool(prop(e, "ExternalConnected")))
    }

    private static func prop(_ e: io_registry_entry_t, _ key: String) -> Any? {
        IORegistryEntryCreateCFProperty(e, key as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue()
    }

    private static func node(_ e: io_registry_entry_t, _ plane: String, depth: Int) -> RegNode {
        var n = RegNode()
        var buf = [CChar](repeating: 0, count: 128)
        if IOObjectGetClass(e, &buf) == KERN_SUCCESS { n.cls = String(cString: buf) }
        if IORegistryEntryGetName(e, &buf) == KERN_SUCCESS { n.name = String(cString: buf) }
        IORegistryEntryGetRegistryEntryID(e, &n.id)
        var p: Unmanaged<CFMutableDictionary>?
        if IORegistryEntryCreateCFProperties(e, &p, kCFAllocatorDefault, 0) == KERN_SUCCESS { n.props = p?.takeRetainedValue() as? [String: Any] ?? [:] }
        if depth > 0 { n.children = children(e, plane).map { c in defer { IOObjectRelease(c) }; return node(c, plane, depth: depth - 1) } }
        return n
    }

    /// Kinderen in een vlak; de aanroeper geeft ze vrij (bij `node` gebeurt dat daar).
    private static func children(_ e: io_registry_entry_t, _ plane: String) -> [io_registry_entry_t] {
        var it: io_iterator_t = 0
        guard IORegistryEntryGetChildIterator(e, plane, &it) == KERN_SUCCESS else { return [] }
        defer { IOObjectRelease(it) }
        var out: [io_registry_entry_t] = []
        while case let c = IOIteratorNext(it), c != 0 { out.append(c) }
        return out
    }

    private static func services(_ cls: String) -> [io_registry_entry_t] {
        var it: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching(cls), &it) == KERN_SUCCESS else { return [] }
        defer { IOObjectRelease(it) }
        var out: [io_registry_entry_t] = []
        while case let c = IOIteratorNext(it), c != 0 { out.append(c) }
        return out
    }
}

// MARK: - Bijhouden zolang de pagina open is

/// Leest bij openen, bij in-/uitpluggen (IOKit-meldingen, even gebundeld) en elke 5 s (laadvermogen). Stopt bij sluiten.
@MainActor
final class PortWatcher: ObservableObject {
    @Published private(set) var snap = PortsSnapshot()
    @Published private(set) var loaded = false
    private var notify: IONotificationPortRef?
    private var iterators: [io_iterator_t] = []
    private var timer: Timer?
    private var pending = false
    private let queue = DispatchQueue(label: "fusebox.ports", qos: .utility)

    func start() {
        guard notify == nil else { return }
        refresh()
        let port = IONotificationPortCreate(kIOMainPortDefault)
        IONotificationPortSetDispatchQueue(port, .main)
        notify = port
        let me = Unmanaged.passUnretained(self).toOpaque()
        let callback: IOServiceMatchingCallback = { ref, it in
            while case let e = IOIteratorNext(it), e != 0 { IOObjectRelease(e) }   // leeglezen = opnieuw "gewapend"
            guard let ref else { return }
            MainActor.assumeIsolated { Unmanaged<PortWatcher>.fromOpaque(ref).takeUnretainedValue().soon() }
        }
        for cls in ["IOPortTransportState", "IOPortTransportComponent", "IOPortFeaturePowerSource", "IOUSBHostDevice", "IOThunderboltSwitch"] {
            for type in [kIOFirstMatchNotification, kIOTerminatedNotification] {
                var it: io_iterator_t = 0
                if IOServiceAddMatchingNotification(port, type, IOServiceMatching(cls), callback, me, &it) == KERN_SUCCESS {
                    while case let e = IOIteratorNext(it), e != 0 { IOObjectRelease(e) }
                    iterators.append(it)
                }
            }
        }
        timer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in MainActor.assumeIsolated { self?.refresh() } }
    }

    deinit {   // vangnet als onDisappear niet kwam: geen IOKit-melding naar een opgeruimd object
        timer?.invalidate()
        iterators.forEach { IOObjectRelease($0) }
        if let notify { IONotificationPortDestroy(notify) }
    }

    func stop() {
        timer?.invalidate(); timer = nil
        iterators.forEach { IOObjectRelease($0) }; iterators = []
        if let notify { IONotificationPortDestroy(notify) }
        notify = nil
    }

    /// Bij in-/uitpluggen komen meldingen in een golf; één keer lezen als het even stil is.
    private func soon() {
        guard !pending else { return }
        pending = true
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { [weak self] in
            self?.pending = false
            self?.refresh()
        }
    }

    private func refresh() {
        queue.async { [weak self] in
            let s = PortReader.read()
            DispatchQueue.main.async {
                guard let self, self.notify != nil || !self.loaded else { return }   // na stop niets meer bijwerken
                if s != self.snap { self.snap = s }
                self.loaded = true
            }
        }
    }
}
