import Foundation
import AppKit
import Combine
import IOKit
import CoreAudio

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
    var title: String { isMagSafe ? kind : L("\(kind) port \(number)") }
}

struct USBDev: Identifiable, Hashable {
    var id: UInt64 = 0
    var name = "", maker = ""
    var vid = 0, pid = 0
    var bps: Double?
    var hub = false, billboard = false
    var powerMA: Int?                // toegewezen stroom op 5 V ("Power Allocated" in Systeeminformatie)
    var canSinkMA: Int?              // wat het apparaat hoogstens kan opnemen (alleen Apple-apparaten melden dit)
    var failedMA: Int?               // vroeg meer dan de poort kon geven (1 = hoeveel is onbekend)
    var portMA: Int?                 // wat de poort waar het in zit hoogstens levert
    var poolMA: Int?                 // hub: wat hij in totaal te verdelen heeft
    var viaHub = false
    var ether: EtherMedia?           // USB-netwerkadapter: afgesproken snelheid en wat hij kan
    var bsd: String?                 // netwerkinterface erachter ("en7"), alleen om `ether` te lezen
    var bcd: Int?                    // USB-versie van het apparaat (0x0200 = 2.0, 0x0310 = 3.1)
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
    var percent: Double?, minutes: Int?   // batterij: procent en tijd tot vol/leeg (zoals Live)
}

struct PortsSnapshot: Hashable {
    var ports: [PortInfo] = []
    var usb: [USBDev] = []           // topniveau-apparaten (per controller)
    var tb: [TBDev] = []
    var tbLinks: [TBLink] = []
    var power: PowerNow?
    var displays: [DisplayInfo] = []   // apart bijgewerkt (bij openen en als schermen wijzigen)
}

/// Eén scherm. Serienummers lezen we bewust niet.
struct DisplayInfo: Identifiable, Hashable {
    enum Link: Hashable { case builtIn, displayLink, native }
    var id: UInt32 = 0
    var name = "Display"
    var vendor = 0, model = 0
    var link = Link.native
    var transport: String?                // "HDMI" / "DP" volgens de framebuffer (alleen eigen aansluiting)
    var main = false, mirrored = false
    var rotation = 0
    var w = 0, h = 0, lw = 0, lh = 0      // pixels nu en "ziet eruit als" (punten)
    var hz = 0.0
    var maxW = 0, maxH = 0, maxHz = 0.0   // eigen resolutie en hoogste verversing daarop
    var mmW = 0.0, mmH = 0.0
    var scale = 1.0, edr = 1.0
    var bits: Int?
    var inches: Double? { Displays.inches(mmW, mmH) }
}

struct PortNote: Hashable {
    enum Level { case good, info, warn }
    /// Waar het oordeel over gaat (welke schakel in de keten).
    enum Topic { case port, power, cable, data, devices }
    let text: String, level: Level
    var topic = Topic.port
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

    /// Stroomgrenzen die een controller of hub-driver meldt (IOUSBHostFamilyDefinitions.h).
    static let supplyKeys = ["kUSBWakePortCurrentLimit", "kUSBWakePowerSupply"]
    /// Van de poort waar het apparaat in zit (ouder in het IOService-vlak); de lezer zet ze met "port." ervoor bij het apparaat.
    /// Gezien op deze Mac: elke USB-poort meldt kUSBWakePortCurrentLimit = 3000.
    static let portKeys = ["kUSBWakePortCurrentLimit", "kUSBBusCurrentAllocation"]

    /// `portMA`: wat de poort hoogstens levert als de poort zelf het niet meldt (van de controller of de hub erboven).
    static func usb(_ n: RegNode, portMA: Int? = nil, viaHub: Bool = false) -> USBDev {
        let p = n.props
        var d = USBDev(id: n.id)
        d.name = str(p["USB Product Name"]) ?? str(p["kUSBProductString"]) ?? n.name.components(separatedBy: "@").first ?? n.name
        d.vid = int(p["idVendor"]) ?? 0; d.pid = int(p["idProduct"]) ?? 0
        d.maker = str(p["USB Vendor Name"]) ?? str(p["kUSBVendorString"]) ?? String(format: "vendor 0x%04X", d.vid)
        d.bps = (p["UsbLinkSpeed"] as? NSNumber)?.doubleValue
        d.hub = int(p["bDeviceClass"]) == 9
        d.billboard = int(p["bDeviceClass"]) == 0x11 || d.name.localizedCaseInsensitiveContains("billboard")
        d.powerMA = int(p["UsbPowerSinkAllocation"]) ?? int(p["kUSBBusCurrentAllocation"]) ?? int(p["port.kUSBBusCurrentAllocation"]); d.bcd = int(p["bcdUSB"])
        d.canSinkMA = int(p["UsbPowerSinkCapability"]).flatMap { $0 > 0 ? $0 : nil }
        d.failedMA = int(p["kUSBFailedRequestedPower"]).flatMap { $0 > 0 ? $0 : nil }
        d.portMA = int(p["port.kUSBWakePortCurrentLimit"]) ?? portMA; d.poolMA = int(p["kUSBWakePowerSupply"]); d.viaHub = viaHub
        d.bsd = str(p["BSD Name"]).flatMap { $0.hasPrefix("en") ? $0 : nil }
        d.raw = rawLines(p)
        let down = int(p["kUSBWakePortCurrentLimit"])
        d.children = n.children.filter { $0.props["idVendor"] != nil }.map { usb($0, portMA: down, viaHub: d.hub) }
        return d
    }

    // MARK: USB-stroom (5 V)

    /// "500 mA · 2.5 W": stroom bij de USB-busspanning van 5 V.
    static func powerText(_ mA: Int) -> String { "\(mA) mA · \(trim(Double(mA) * 5 / 1000)) W" }

    /// Wat de apparaten op één hub (of in één Mac-poort) samen toegewezen kregen, tegen wat die kan leveren.
    struct USBBudget: Hashable {
        var usedMA = 0, devices = 0
        var supplyMA: Int?
        var over: Bool { supplyMA.map { usedMA > $0 } ?? false }
        /// "900 mA · 4.5 W to 2 devices" (+ " of 3000 mA")
        var text: String {
            (devices == 1 ? L("\(powerText(usedMA)) to 1 device") : L("\(powerText(usedMA)) to \(devices) devices")) + (supplyMA.map { L(" of \($0) mA") } ?? "")
        }
    }

    /// nil als geen enkel apparaat een toewijzing meldt (dan tonen we niets).
    static func budget(_ devs: [USBDev], supplyMA: Int?) -> USBBudget? {
        let used = devs.compactMap(\.powerMA)
        return used.isEmpty ? nil : USBBudget(usedMA: used.reduce(0, +), devices: used.count, supplyMA: supplyMA)
    }

    /// Stroom voor wat er direct in een Mac-poort zit.
    static func portBudget(_ roots: [USBDev]) -> USBBudget? { budget(roots, supplyMA: roots.compactMap(\.portMA).max()) }

    /// Eén apparaat: vroeg meer dan de poort kon geven, of krijgt duidelijk minder dan het kan opnemen.
    static func powerNote(_ d: USBDev) -> PortNote? {
        let fix = d.viaHub ? L(" Plug it straight into the Mac, or use a hub with its own power adapter.") : ""
        if let f = d.failedMA {
            return PortNote(text: (f > 1 ? L("\(d.name) asked for \(powerText(f)), more than this port can give, so it may not work.")
                                         : L("\(d.name) asked for more power than this port can give, so it may not work.")) + fix, level: .warn, topic: .devices)
        }
        if let can = d.canSinkMA, let got = d.powerMA, got > 0, can > got {
            // Duidelijk minder (de helft of minder) = waarschuwing; een klein verschil is alleen een weetje
            return PortNote(text: L("\(d.name) gets \(powerText(got)) here but can take \(powerText(can)), so it charges slower.") + fix,
                            level: can >= got * 2 ? .warn : .info, topic: .devices)
        }
        return nil
    }

    /// Voor de regel van de poort: hubs en de poort zelf die meer moeten leveren dan ze kunnen;
    /// het oordeel over één los apparaat alleen als dat het enige in de poort is (anders staat het op zijn eigen regel).
    static func powerNotes(_ roots: [USBDev]) -> [PortNote] {
        var out: [PortNote] = []
        func add(_ t: String) { out.append(PortNote(text: t, level: .warn, topic: .devices)) }
        if let b = portBudget(roots), b.over {
            add(L("What's plugged into this port asks for \(b.usedMA) mA together, but the port supplies up to \(b.supplyMA ?? 0) mA. Move a device to another port, or use a hub with its own power adapter."))
        }
        func walk(_ d: USBDev) {
            if d.hub, let b = budget(d.children, supplyMA: d.poolMA), b.over {
                add(L("The devices on \(d.name) ask for \(b.usedMA) mA together, but it can supply \(b.supplyMA ?? 0) mA. Move one to another port, or use a hub with its own power adapter."))
            }
            d.children.forEach(walk)
        }
        roots.forEach(walk)
        if roots.count == 1, !roots[0].hub, let n = powerNote(roots[0]) { out.append(n) }
        return out
    }

    // MARK: Teksten

    static func speedText(_ bps: Double) -> String {
        bps >= 1e9 ? "\(trim(bps / 1e9)) Gbit/s" : "\(trim(bps / 1e6)) Mbit/s"
    }

    static func cableSpeedText(_ s: Int) -> String {
        switch s {
        case 0: L("USB 2.0 only (480 Mbit/s)")
        case 1: "USB 3.2 Gen 1 (5 Gbit/s)"
        case 2: L("USB 3.2 Gen 2 (10 Gbit/s; 20 Gbit/s for Thunderbolt)")
        case 3: "USB4 / Thunderbolt (40 Gbit/s)"
        default: "USB4 Gen 4 (80 Gbit/s)"
        }
    }

    static func cableText(_ c: Cable, activeFlag: Bool, optical: Bool) -> String {
        var kind = c.type.replacingOccurrences(of: " Cable", with: "")
        if optical { kind = "Optical" } else if activeFlag && !c.active { kind = "Active" }
        var parts = [L("\(kind) USB-C cable")]
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
        case "DisplayPort": s = L("DisplayPort (video)")
        default: s = l.generation ?? l.type
        }
        if l.type == "CIO", let g = tb?.gbps { s += L(" at \(g) Gbit/s") + (tb!.width > 1 ? L(" (\(tb!.width) lanes)") : "") }
        else if let r = l.rate { s += L(" at ") + r.replacingOccurrences(of: "Gbps", with: "Gbit/s").replacingOccurrences(of: "Mbps", with: "Mbit/s") }
        return s + (l.tunneled ? L(" (tunneled)") : "")
    }

    static func supportedText(_ s: [String]) -> String? {
        let names = s.filter { $0 != "CC" }.map(transportName)
        return names.isEmpty ? nil : "Supports " + names.joined(separator: ", ")
    }

    static func transportName(_ t: String) -> String {
        ["CC": L("CC (power talk)"), "USB2": "USB 2", "USB3": "USB 3", "CIO": "Thunderbolt/USB4", "DisplayPort": "DisplayPort"][t] ?? t
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

    /// Hoogste snelheid volgens de USB-versie, alleen waar die vaststaat: 3.1/3.2 zegt niets over Gen 1 of 2 (nil).
    static func usbMax(_ bcd: Int) -> Double? {
        bcd >= 0x310 ? nil : bcd >= 0x300 ? 5e9 : bcd >= 0x200 ? 480e6 : 12e6
    }

    /// Hoogste datasnelheid van de poort zelf, uit wat hij kan.
    /// ponytail: Thunderbolt 5 (80 Gbit/s) is hier niet te zien, dus CIO = 40; uitlezen zodra Apple het meldt.
    static func portMax(_ supported: [String]) -> Double? {
        supported.contains("CIO") ? 40e9 : supported.contains("USB3") ? 10e9 : supported.contains("USB2") ? 480e6 : nil
    }

    /// Unieke apparaten (geen hubs) achter een poort.
    static func devices(_ roots: [USBDev]) -> [USBDev] {
        var seen = Set<UInt64>()
        return roots.flatMap { $0.hub ? $0.leaves : [$0] + $0.leaves }.filter { seen.insert($0.id).inserted }
    }

    /// Wat zit erin, in gewone woorden, met het laadvermogen erachter ("Hub or dock with 4 devices · 90 W charger").
    static func headline(_ p: PortInfo, roots: [USBDev], tb: [TBDev]) -> String {
        guard p.connected else { return L("Nothing connected") }
        let what: String?
        if !tb.isEmpty { what = tb.map { $0.vendor.isEmpty ? $0.name : "\($0.name) (\($0.vendor))" }.joined(separator: ", ") }
        else if !roots.isEmpty {
            let n = devices(roots).count
            what = roots.contains(where: \.hub) ? (n == 0 ? L("USB hub") : n == 1 ? L("Hub or dock with 1 device") : L("Hub or dock with \(n) devices"))
                : "\(roots[0].name) (\(roots[0].maker))"
        }
        else if p.links.contains(where: { $0.type == "DisplayPort" }) { what = L("Display (DisplayPort)") }
        else if let k = p.partnerKind, p.charger == nil || k != "charger" { what = "USB-C \(k)" }
        else { what = nil }
        let charger = p.charger.map { L("\(trim($0.maxW)) W charger") }
        switch (what, charger) {
        case let (w?, c?): return "\(w) · \(c)"
        case let (w?, nil): return w
        case let (nil, c?): return (p.isMagSafe ? "MagSafe " : "USB-C ") + c
        default: return L("Something is connected")
        }
    }

    /// Eén regel over de data: "SuperSpeed data active · USB 3.x at 10 Gbit/s".
    static func dataLine(_ p: PortInfo, tb: TBLink?) -> String? {
        guard p.connected else { return nil }
        let types = Set(p.links.map(\.type))
        let label = types.contains("CIO") ? L("Thunderbolt/USB4 data active")
            : types.contains("USB3") ? L("SuperSpeed data active")
            : types.contains("USB2") ? L("USB 2 data active")
            : types.contains("DisplayPort") ? L("Video only (DisplayPort)") : L("No data connection (power only)")
        let best = p.links.first { $0.type == "CIO" } ?? p.links.first { $0.type == "USB3" } ?? p.links.first
        return [label, best.map { linkText($0, tb: tb) }].compactMap { $0 }.joined(separator: " · ")
    }

    static func chargerText(_ c: Charger) -> String {
        var s = L("Charger up to \(trim(c.maxW)) W")
        if let w = c.winning { s += L(" · agreed on \(w.text) (\(trim(w.watts)) W)") }
        return s
    }

    static func nowText(_ n: PowerNow) -> String? {
        guard let mW = n.mW, mW > 0 else { return nil }
        var s = L("Drawing \(trim(Double(mW) / 1000)) W now")
        if let v = n.mV, let a = n.mA { s += " (\(volt(v)) × \(amp(a)))" }
        s += n.full ? L(" · battery full") : n.charging ? L(" · battery charging") : L(" · battery not charging")
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
        func add(_ t: String, _ l: PortNote.Level, _ topic: PortNote.Topic = .port) { out.append(PortNote(text: t, level: l, topic: topic)) }
        if p.liquid { add(L("Liquid or dirt detected in this port. Unplug and let it dry."), .warn) }
        if !p.unauthorized.isEmpty {
            add(L("macOS is holding back \(p.unauthorized.map(transportName).joined(separator: ", ")) until you allow this accessory."), .warn)
        }
        if p.overcurrent > 0 { add(L("Over-current protection cut power on this port \(p.overcurrent)× (a device drew too much)."), .warn) }
        guard p.connected else { return out }
        let types = Set(p.links.map(\.type)), c = p.cable
        // Laden
        if let w = p.charger?.winning {
            let capped = !p.isMagSafe && w.mA >= 3000 && w.mV >= 20000 && (c?.mA ?? 3000) <= 3000
            if capped {
                add(c == nil ? L("No e-marker in this cable, so charging stops at 60 W (3 A). Above 60 W needs a 5 A cable (and a charger that can).")
                             : L("This cable is rated 3 A, so charging stops at 60 W. Above 60 W needs a 5 A cable (and a charger that can)."), .info, .cable)
            } else if let cw = c?.watts, Double(cw) < w.watts {
                add(L("The cable is rated \(cw) W but the charger agreed on \(trim(w.watts)) W. Use a cable rated for at least that."), .warn, .cable)
            } else {
                add(c == nil ? L("Charging OK: charger agree on up to \(trim(w.watts)) W; the Mac takes what it needs.") : L("Charging OK: charger and cable agree on up to \(trim(w.watts)) W; the Mac takes what it needs."), .good, .power)
            }
        }
        // Data
        let measured = roots.compactMap(\.bps).max() ?? 0
        if let s = c?.speed, measured > cableMax(s, thunderbolt: false) * 1.01, !types.contains("CIO") {
            add(L("The cable's e-marker says up to \(speedText(cableMax(s, thunderbolt: false))), but the link runs at \(speedText(measured)). One of the two is wrong (e-markers are often mislabelled)."), .info, .cable)
        }
        if types.contains("USB2"), types.isDisjoint(with: ["USB3", "CIO"]), measured >= 480e6 {
            if c?.speed == 0 { add(L("This cable is USB 2 only, so data runs at 480 Mbit/s. Use a USB 3 or Thunderbolt cable if the device can go faster."), .warn, .cable) }
            else { add(L("Data runs at USB 2 speed (480 Mbit/s). If this device can go faster, try a USB 3 or Thunderbolt cable."), .info, .data) }
        }
        if types.contains("CIO"), let s = c?.speed, s <= 2 {
            add(L("This cable holds Thunderbolt/USB4 back to \(speedText(cableMax(s, thunderbolt: true))) or less. A 40 Gbit/s cable is faster."), .warn, .cable)
        }
        if types.contains("CIO") || types.contains("USB3") { add(types.contains("CIO") ? L("Thunderbolt/USB4 link is up.") : L("Fast data (SuperSpeed) link is up."), .good, .data) }
        if roots.contains(where: \.billboard) || devices(roots).contains(where: \.billboard) {
            add(L("Billboard device present: the dock or adapter reports that an extra mode (such as video over USB-C) is not active or not supported on this connection."), .info, .devices)
        }
        out += powerNotes(roots)
        return out
    }

    /// USB-stroom met verzonnen registerwaarden (sleutels uit IOUSBHostFamilyDefinitions.h; niet op echte hardware getest).
    static func powerSelfTest() {
        precondition(powerText(500) == "500 mA · 2.5 W" && powerText(900) == "900 mA · 4.5 W" && powerText(2400) == "2400 mA · 12 W", "stroomtekst")
        // Hub zonder eigen voeding (500 mA te verdelen) in een Mac-poort (3 A): schijf 500, toetsenbord 100, iPad kan 2400 maar krijgt 500
        let hubNode = RegNode(name: "Hub@1", id: 70, props: ["USB Product Name": "Travel Hub", "idVendor": 1, "bDeviceClass": 9, "kUSBBusCurrentAllocation": 100,
                                                              "kUSBWakePortCurrentLimit": 500, "kUSBWakePowerSupply": 500],
                              children: [RegNode(name: "Disk@1", id: 71, props: ["USB Product Name": "Disk", "idVendor": 2, "UsbPowerSinkAllocation": 500, "BSD Name": "disk4"]),
                                         RegNode(name: "Keys@2", id: 72, props: ["USB Product Name": "Keys", "idVendor": 3, "port.kUSBBusCurrentAllocation": 100, "port.kUSBWakePortCurrentLimit": 500]),
                                         RegNode(name: "iPad@3", id: 73, props: ["USB Product Name": "iPad", "idVendor": 0x5AC, "UsbPowerSinkAllocation": 500, "UsbPowerSinkCapability": 2400]),
                                         RegNode(name: "LAN@4", id: 74, props: ["USB Product Name": "USB LAN", "idVendor": 4, "BSD Name": "en7"])])
        let hub = usb(hubNode, portMA: 3000)
        precondition(hub.powerMA == 100 && hub.portMA == 3000 && hub.poolMA == 500 && !hub.viaHub, "hub stroom")
        precondition(hub.children.map(\.powerMA) == [500, 100, 500, nil] && hub.children.allSatisfy { $0.portMA == 500 && $0.viaHub }, "apparaten stroom")
        precondition(usb(RegNode(name: "X@1", id: 77, props: ["idVendor": 7, "port.kUSBWakePortCurrentLimit": 3000]), portMA: 900).portMA == 3000, "poort zelf gaat voor")
        precondition(hub.children[0].bsd == nil && hub.children[3].bsd == "en7", "bsd alleen netwerk")
        let b = budget(hub.children, supplyMA: hub.poolMA)
        precondition(b?.usedMA == 1100 && b?.devices == 3 && b?.over == true && b?.text == "1100 mA · 5.5 W to 3 devices of 500 mA", "hub-budget \(b?.text ?? "")")
        precondition(portBudget([hub])?.over == false && portBudget([hub])?.text == "100 mA · 0.5 W to 1 device of 3000 mA", "poort-budget")
        precondition(budget([hub.children[3]], supplyMA: 500) == nil && budget([], supplyMA: nil) == nil, "zonder sleutels niets")
        let ipad = powerNote(hub.children[2])
        precondition(ipad?.level == .warn && ipad?.text == "iPad gets 500 mA · 2.5 W here but can take 2400 mA · 12 W, so it charges slower. Plug it straight into the Mac, or use a hub with its own power adapter.", "ipad \(ipad?.text ?? "")")
        precondition(powerNote(hub.children[0]) == nil && powerNote(hub.children[3]) == nil, "gewoon apparaat: geen oordeel")
        var near = hub.children[2]; near.powerMA = 2100; near.viaHub = false
        precondition(powerNote(near)?.level == .info && powerNote(near)?.text.hasSuffix("charges slower.") == true, "klein verschil = weetje")
        var failed = usb(RegNode(name: "Drive@1", id: 75, props: ["USB Product Name": "Drive", "idVendor": 5, "kUSBFailedRequestedPower": 900]), portMA: 500, viaHub: true)
        precondition(powerNote(failed)?.level == .warn && powerNote(failed)?.text.hasPrefix("Drive asked for 900 mA · 4.5 W, more than this port can give") == true, "geweigerd")
        failed.failedMA = 1
        precondition(powerNote(failed)?.text.hasPrefix("Drive asked for more power than this port can give") == true, "geweigerd zonder getal")
        // Voor de poortregel: de hub die te veel moet leveren; een los apparaat alleen als het het enige is
        let pn = powerNotes([hub])
        precondition(pn.count == 1 && pn[0].serious && pn[0].topic == .devices && pn[0].text.hasPrefix("The devices on Travel Hub ask for 1100 mA together, but it can supply 500 mA."), "hub te veel")
        precondition(powerNotes([failed]).count == 1 && powerNotes([usb(RegNode(name: "Plain@1", id: 76, props: ["idVendor": 6]))]).isEmpty, "los apparaat")
        var two = [hub.children[0], hub.children[2]]; two[0].portMA = 900; two[1].portMA = 900
        precondition(powerNotes(two).first?.text.hasPrefix("What's plugged into this port asks for 1000 mA together, but the port supplies up to 900 mA.") == true, "poort te veel")
    }

    // MARK: Ruwe waarden (voor "Advanced")

    static let skipKeys: Set<String> = ["IOGeneralInterest", "IOPowerManagement", "CFBundleIdentifier", "CFBundleIdentifierKernel",
        "IOPersonalityPublisher", "IOMatchCategory", "IOProbeScore", "IOProviderClass", "IOUserClientClass", "IOMatchedAtBoot",
        "IOReportLegend", "IOReportLegendPublic", "IOCFPlugInTypes", "Owner"]

    static func rawLines(_ p: [String: Any]) -> [RawLine] {
        // Serienummers en apparaat-ID's (UDID/ECID) tonen we nergens, ook niet tussen de ruwe waarden
        p.keys.filter { k in !skipKeys.contains(k) && !k.hasPrefix("IOFunctionParent") && !["serial", "udid", "ecid"].contains { k.localizedCaseInsensitiveContains($0) } }
            .sorted().map { RawLine(key: $0, value: rawValue(p[$0] as Any)) }
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
        precondition(trim(16.97) == "17" && trim(17.53) == "17.5", "afronden")
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
                                         RegNode(name: "Disk@3", id: 44, props: ["idVendor": 0xABCD, "UsbLinkSpeed": 480_000_000, "UsbPowerSinkAllocation": 500, "BSD Name": "disk4"]),
                                         RegNode(name: "NotADevice", id: 45)])
        let hub = usb(hubNode)
        precondition(usbMax(0x0310) == nil && usbMax(0x0300) == 5e9 && usbMax(0x0210) == 480e6 && usbMax(0x0110) == 12e6, "usb max")
        precondition(portMax(["USB2", "USB3", "CIO"]) == 40e9 && portMax(["USB2"]) == 480e6 && portMax([]) == nil, "poort max")
        precondition(hub.hub && hub.children.count == 2 && hub.leaves.count == 2, "usb boom")
        precondition(hub.children[1].name == "Disk" && hub.children[1].maker == "vendor 0xABCD" && hub.children[1].powerMA == 500, "usb naam")
        precondition(headline(p, roots: [hub], tb: []) == "Hub or dock with 2 devices · 60 W charger" && dataLine(p, tb: nil) == "USB 2 data active · USB 2.0 at 480 Mbit/s (High Speed)", "kop hub")
        let ns = notes(p, roots: [hub], tb: nil)
        precondition(ns.first { $0.text.hasPrefix("This cable is rated 3 A") }?.topic == .cable && ns.allSatisfy { $0.topic != .port }, "onderwerp")
        precondition(ns.contains { $0.text.hasPrefix("This cable is rated 3 A") } && ns.contains { $0.text.hasPrefix("This cable is USB 2 only") && $0.serious }, "tips")
        // e-marker zegt 5 Gbit/s, link loopt op 10; billboard achter een hub
        var fast = p; fast.cable?.speed = 1; fast.links = [PortLink(type: "USB3")]; fast.charger = nil
        var fastHub = USBDev(id: 50); fastHub.hub = true; fastHub.bps = 10e9
        var bb = USBDev(id: 51); bb.billboard = true; fastHub.children = [bb]
        let fn = notes(fast, roots: [fastHub], tb: nil)
        precondition(fn.contains { $0.text.contains("says up to 5 Gbit/s, but the link runs at 10 Gbit/s") } && fn.contains { $0.text.hasPrefix("Billboard") }
                     && fn.contains { $0.level == .good }, "verschil")
        precondition(usbSpeedName(480e6) == "High Speed (USB 2)" && usbSpeedName(10e9) == "SuperSpeed+ (USB 3.2 Gen 2)" && pdText(2) == "USB PD 2.0", "namen")
        powerSelfTest()

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
        precondition(rawLines(["USB Serial Number": "X1", "kUSBSerialNumberString": "X1", "UsbAppleDeviceUDID": "u", "idVendor": 5]).map(\.key) == ["idVendor"], "geen serienummers")
        Displays.selfTest()
        Wireless.selfTest()
        AudioJack.selfTest()
        print("poorten ok")
    }
}

// MARK: - Schermen (puur)

enum Displays {
    static let displayLinkVID = 0x17E9
    static let displayLinkInfo = L("DisplayLink sends the picture compressed over USB, and the Mac's processor does the work. Fine for office work; not for games or video (motion can stutter, some protected video won't play).")

    struct Mode: Hashable { var w = 0, h = 0, pw = 0, ph = 0; var hz = 0.0; var native = false }
    struct Framebuffer: Hashable { var vendor = 0, model = 0; var transport: String? }

    static func inches(_ w: Double, _ h: Double) -> Double? { w > 0 && h > 0 ? (w * w + h * h).squareRoot() / 25.4 : nil }
    static func res(_ w: Int, _ h: Int) -> String { "\(w) × \(h)" }
    static func hz(_ x: Double) -> String { "\(trim(x)) Hz" }

    /// "4K60", "1440p75", anders "3440 × 1440 at 100 Hz".
    static func modeName(_ w: Int, _ h: Int, _ f: Double) -> String {
        let n = ["3840x2160": "4K", "4096x2160": "4K", "5120x2880": "5K", "6016x3384": "6K", "2560x1440": "1440p", "1920x1080": "1080p"]["\(w)x\(h)"]
        return n.map { "\($0)\(Int(f.rounded()))" } ?? L("\(res(w, h)) at \(hz(f))")
    }

    /// Eigen resolutie (vlag "native", anders de grootste niet-geschaalde) en de hoogste verversing daarop.
    static func best(_ modes: [Mode]) -> (w: Int, h: Int, hz: Double)? {
        let nat = modes.filter(\.native)
        guard let top = (nat.isEmpty ? modes.filter { $0.w == $0.pw } : nat).max(by: { $0.pw * $0.ph < $1.pw * $1.ph }) else { return nil }
        return (top.pw, top.ph, modes.filter { $0.pw == top.pw && $0.ph == top.ph }.map(\.hz).max() ?? top.hz)
    }

    /// Ingebouwd, eigen framebuffer (DisplayPort/HDMI uit de Mac zelf), of zonder framebuffer met een DisplayLink-apparaat aan de Mac.
    /// ponytail: zonder DisplayLink-apparaat geldt een scherm zonder framebuffer ook als "eigen" (AirPlay/Sidecar vallen daar ook onder).
    static func link(builtIn: Bool, vendor: Int, model: Int, framebuffers fbs: inout [Framebuffer], displayLink: Bool) -> (DisplayInfo.Link, String?) {
        if builtIn { return (.builtIn, nil) }
        // Model eerst; ProductID van de framebuffer is niet altijd het CG-modelnummer, dan alleen de maker
        if let i = fbs.firstIndex(where: { $0.vendor == vendor && $0.model == model }) ?? fbs.firstIndex(where: { $0.vendor == vendor }) {
            return (.native, fbs.remove(at: i).transport)
        }
        return (displayLink ? .displayLink : .native, nil)
    }

    /// "3840 × 2160 · 27″ · looks like 2560 × 1440"
    static func facts(_ d: DisplayInfo) -> [String] {
        [res(d.w, d.h), d.inches.map { "\(trim($0))″" }, d.lw > 0 && (d.lw, d.lh) != (d.w, d.h) ? L("looks like \(res(d.lw, d.lh))") : nil].compactMap { $0 }
    }

    /// Alleen als het echt zo is: lagere verversing of minder pixels dan het scherm kan.
    static func warning(_ d: DisplayInfo) -> String? {
        if d.hz > 0, d.maxHz > d.hz + 1 {
            return L("Runs at \(hz(d.hz)) but can do \(hz(d.maxHz)). The cable, adapter or port may limit \(modeName(d.maxW, d.maxH, d.maxHz)).")
        }
        if d.w > 0, d.maxW * d.maxH > d.w * d.h {
            return L("Runs at \(res(d.w, d.h)) pixels but the display has \(res(d.maxW, d.maxH)), so text looks soft. Check System Settings › Displays, the cable or the port.")
        }
        return nil
    }

    static func linkText(_ d: DisplayInfo) -> String {
        switch d.link {
        case .builtIn: L("Built in")
        case .displayLink: L("DisplayLink (compressed video over USB)")
        case .native: d.transport.map { L("\($0 == "DP" ? "DisplayPort" : $0) (straight from the Mac's graphics)") } ?? L("DisplayPort or HDMI (straight from the Mac's graphics)")
        }
    }

    /// Eén regel voor de tooltip in de kop: "Built-in: 2880 × 1864 · 60 Hz".
    static func line(_ d: DisplayInfo) -> String {
        let (w, h) = d.link == .builtIn && d.maxW > 0 ? (d.maxW, d.maxH) : (d.w, d.h)
        return (d.link == .builtIn ? L("Built-in") : d.name) + ": " + res(w, h) + (d.hz > 0 ? " · " + hz(d.hz) : "")
    }

    static func selfTest() {
        precondition(trim(inches(597.7, 336.2) ?? 0) == "27" && inches(0, 10) == nil, "inch")
        precondition(modeName(3840, 2160, 59.99) == "4K60" && modeName(3440, 1440, 100) == "3440 × 1440 at 100 Hz", "modusnaam")
        let modes = [Mode(w: 1920, h: 1080, pw: 3840, ph: 2160, hz: 60), Mode(w: 3840, h: 2160, pw: 3840, ph: 2160, hz: 30, native: true),
                     Mode(w: 3840, h: 2160, pw: 3840, ph: 2160, hz: 60, native: true), Mode(w: 2560, h: 1440, pw: 5120, ph: 2880, hz: 60),
                     Mode(w: 1920, h: 1080, pw: 1920, ph: 1080, hz: 144)]
        precondition(best(modes).map { [$0.w, $0.h, Int($0.hz)] } == [3840, 2160, 60], "beste modus")
        precondition(best(modes.map { var m = $0; m.native = false; return m }).map { [$0.w, Int($0.hz)] } == [3840, 60] && best([]) == nil, "zonder vlag")
        var fbs = [Framebuffer(vendor: 0x1234, model: 7, transport: "HDMI")]
        precondition(link(builtIn: true, vendor: 1, model: 1, framebuffers: &fbs, displayLink: true).0 == .builtIn, "ingebouwd")
        precondition(link(builtIn: false, vendor: 0x1234, model: 7, framebuffers: &fbs, displayLink: true) == (.native, "HDMI") && fbs.isEmpty, "eigen")
        precondition(link(builtIn: false, vendor: 0x1234, model: 7, framebuffers: &fbs, displayLink: true).0 == .displayLink
                     && link(builtIn: false, vendor: 9, model: 9, framebuffers: &fbs, displayLink: false).0 == .native, "displaylink")
        fbs = [Framebuffer(vendor: 0x1234, model: 999)]
        precondition(link(builtIn: false, vendor: 0x1234, model: 7, framebuffers: &fbs, displayLink: true).0 == .native && fbs.isEmpty, "alleen maker")
        var d = DisplayInfo(id: 5, name: "Test Monitor", vendor: 0x1234, model: 7, w: 3840, h: 2160, lw: 1920, lh: 1080, hz: 30,
                            maxW: 3840, maxH: 2160, maxHz: 60, mmW: 597.7, mmH: 336.2)
        precondition(facts(d) == ["3840 × 2160", "27″", "looks like 1920 × 1080"], "feiten \(facts(d))")
        precondition(warning(d)?.hasSuffix("may limit 4K60.") == true, "te traag")
        d.hz = 60; d.w = 1920; d.h = 1080; d.lw = 1920; d.lh = 1080
        precondition(warning(d)?.hasPrefix("Runs at 1920 × 1080 pixels") == true && facts(d).count == 2, "te weinig pixels")
        d.w = 5120; d.h = 2880
        precondition(warning(d) == nil && linkText(d) == "DisplayPort or HDMI (straight from the Mac's graphics)", "in orde")
        precondition(line(DisplayInfo(link: .builtIn, w: 3420, h: 2214, hz: 120, maxW: 3024, maxH: 1964)) == "Built-in: 3024 × 1964 · 120 Hz", "kopregel")

        // Boom: twee DisplayLink-schermen onder het DisplayLink-apparaat achter een hub; een eigen scherm zonder videopoort apart
        var dl = USBDev(id: 61); dl.name = "Test Dock Video"; dl.vid = displayLinkVID; dl.maker = "DisplayLink"
        var hub = USBDev(id: 60); hub.hub = true; hub.children = [dl]
        let port = PortInfo(id: "p2", kind: "USB-C", number: 2, connected: true)
        let sum = PortSum(port: port, roots: [hub], tb: [], tbLink: nil, notes: [], now: nil)
        let shown = [DisplayInfo(id: 1, link: .builtIn), DisplayInfo(id: 2, link: .displayLink), DisplayInfo(id: 3, link: .displayLink), DisplayInfo(id: 4, link: .native)]
        for internals in [false, true] {
            let t = PortTree.build([sum], internals: internals, displays: shown)
            let dev = PortTree.find("usb:61", in: t)
            precondition(dev?.children.map(\.id) == ["disp:2", "disp:3"] && dev?.facts.first == "2 displays via DisplayLink" && dev?.info != nil, "dl boom")
            precondition(PortTree.find("disp:2", in: t)?.portID == "p2" && t.last?.id == "displays" && t.last?.children.map(\.id) == ["disp:4"]
                         && PortTree.find("disp:1", in: t) == nil, "eigen apart")
        }
        // Eén poort met video: het eigen scherm hangt daaronder
        var vp = port; vp.links = [PortLink(type: "DisplayPort")]
        let t2 = PortTree.build([PortSum(port: vp, roots: [], tb: [], tbLink: nil, notes: [], now: nil)], internals: false, displays: [shown[3]])
        precondition(PortTree.find("src:p2", in: t2)?.children.map(\.id) == ["disp:4"] && t2.count == 1, "eigen onder poort")
    }
}

/// "20 V", "4.5 A", "17.5": hooguit één decimaal, zonder ",0".
func trim(_ x: Double) -> String { let r = (x * 10).rounded() / 10; return r == r.rounded() ? String(Int(r)) : String(format: "%.1f", r) }
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
        s.usb = children(root, "IOUSB").flatMap { ctrl -> [USBDev] in
            defer { IOObjectRelease(ctrl) }
            let limit = Ports.int(prop(ctrl, "kUSBWakePortCurrentLimit"))   // wat een poort van de Mac hoogstens levert
            return children(ctrl, "IOUSB").map { e in defer { IOObjectRelease(e) }; return ether(Ports.usb(node(e, "IOUSB", depth: 8), portMA: limit)) }
        }
        s.tb = services("IOThunderboltSwitch").compactMap(tbDevice)
        s.tbLinks = services("IOThunderboltPort").compactMap { e in
            defer { IOObjectRelease(e) }
            guard let socket = prop(e, "Socket ID") as? String, let w = Ports.int(prop(e, "Current Link Width")) else { return nil }
            return TBLink(socket: socket, speed: Ports.int(prop(e, "Current Link Speed")) ?? 0, width: w)
        }
        s.power = battery()
        var b = Sample()
        Sampler.battery(into: &b)   // zelfde bron als Live
        s.power?.percent = b.battery; s.power?.minutes = b.batteryMinutes
        return s
    }

    /// USB-netwerkadapters: afgesproken snelheid en wat de adapter kan (zelfde ioctl als `ifconfig -m`).
    private static func ether(_ d: USBDev) -> USBDev {
        var d = d
        if let b = d.bsd { d.ether = EtherMedia.read(b) }
        d.children = d.children.map(ether)
        return d
    }

    private static func tbDevice(_ e: io_registry_entry_t) -> TBDev? {
        defer { IOObjectRelease(e) }
        guard let depth = Ports.int(prop(e, "Depth")), depth > 0 else { return nil }
        let n = node(e, kIOServicePlane, depth: 0)
        var d = TBDev(id: n.id, name: Ports.str(n.props["Device Model Name"]) ?? L("Thunderbolt device"),
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
        if plane == "IOUSB", n.props["idVendor"] != nil {
            // De poort waar het apparaat in zit (ouder in het IOService-vlak) meldt wat hij levert en heeft toegewezen
            var port: io_registry_entry_t = 0
            if IORegistryEntryGetParentEntry(e, kIOServicePlane, &port) == KERN_SUCCESS {
                for k in Ports.portKeys { if let v = prop(port, k) { n.props["port." + k] = v } }
                IOObjectRelease(port)
            }
            if Ports.int(n.props["bDeviceClass"]) == 9 {
                // Hub: de stroomgrenzen staan op de hub-driver (kind in het IOService-vlak), niet op het apparaat zelf
                for c in children(e, kIOServicePlane) {
                    for k in Ports.supplyKeys where n.props[k] == nil { if let v = prop(c, k) { n.props[k] = v } }
                    IOObjectRelease(c)
                }
            } else if let b = IORegistryEntrySearchCFProperty(e, kIOServicePlane, "BSD Name" as CFString, kCFAllocatorDefault, IOOptionBits(kIORegistryIterateRecursively)) as? String {
                n.props["BSD Name"] = b   // netwerkadapter ("en7") of schijf ("disk4"); Ports.usb gebruikt alleen "en…"
            }
        }
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

// MARK: - Controle op een Mac met dock (scripts/check-docked.sh): wat Fusebox uit het register haalt, als tekst

extension PortReader {
    /// `Fusebox --check-docked`: alleen lezen, geen venster. Geen serienummers of adressen.
    static func check() {
        let s = read()
        func tree(_ d: USBDev, _ pad: String) {
            var parts = [d.hub ? "hub" : "device", d.maker]
            if let b = d.bps { parts.append(Ports.speedText(b)) }
            if let a = d.powerMA { parts.append("allocated \(Ports.powerText(a))") }
            if let c = d.canSinkMA { parts.append("can take \(Ports.powerText(c))") }
            if let f = d.failedMA { parts.append("asked in vain for \(f) mA") }
            if let p = d.portMA { parts.append("port gives up to \(Ports.powerText(p))") }
            if d.hub, let b = Ports.budget(d.children, supplyMA: d.poolMA) { parts.append("shares \(b.text)" + (b.over ? " (OVER)" : "")) }
            if let e = d.ether { parts.append("network adapter \(d.bsd ?? "") now \(e.activeMbit.map(EtherMedia.speed) ?? "–") of \(e.maxMbit.map(EtherMedia.speed) ?? "–")") }
            print("\(pad)- \(d.name) [\(parts.joined(separator: "; "))]")
            if let n = d.ether?.note() { print("\(pad)  \(n.serious ? "!" : "i") \(n.text)") }
            if let n = Ports.powerNote(d) { print("\(pad)  \(n.serious ? "!" : "i") \(n.text)") }
            d.children.forEach { tree($0, pad + "  ") }
        }
        print("== What Fusebox shows on Docked ==")
        for p in PortSum.all(s) {
            print("\(p.port.title): \(Ports.headline(p.port, roots: p.roots, tb: p.tb))")
            for n in p.notes { print("  \(n.level == .warn ? "!" : n.level == .info ? "i" : "+") \(n.text)") }
            if let b = Ports.portBudget(p.roots) { print("  USB power: \(b.text)" + (b.over ? " (OVER)" : "")) }
            p.roots.forEach { tree($0, "  ") }
        }
        let claimed = Set(s.ports.flatMap(\.usbRoots)), other = s.usb.filter { !claimed.contains($0.id) }
        if !other.isEmpty { print("Other USB devices (not linked to a port):"); other.forEach { tree($0, "  ") } }
        if s.usb.isEmpty { print("No USB devices connected.") }
        print("== Network adapters (cable check) ==")
        for i in 0..<16 {
            guard let m = EtherMedia.read("en\(i)"), m.maxMbit != nil || m.activeMbit != nil else { continue }   // Wi-Fi en Thunderbolt-bruggen melden geen snelheid
            print("en\(i): \(m.linked ? "linked" : "no link") · now \(m.activeMbit.map(EtherMedia.speed) ?? "–") · adapter can do \(m.maxMbit.map(EtherMedia.speed) ?? "–") · duplex \(m.duplex ?? "–")")
            if let n = m.note() { print("  \(n.serious ? "!" : "i") \(n.text)") }
        }
    }
}

// MARK: - Schermen lezen (CoreGraphics + NSScreen; alleen voorkeur, maker- en modelnummer uit de framebuffer)

extension PortReader {
    /// Hoofdthread (NSScreen). Alleen bij openen en als schermen wijzigen, niet elke 5 s.
    @MainActor static func displays() -> [DisplayInfo] {
        var ids = [CGDirectDisplayID](repeating: 0, count: 16), n: UInt32 = 0
        guard CGGetOnlineDisplayList(16, &ids, &n) == .success else { return [] }
        let key = NSDeviceDescriptionKey("NSScreenNumber")
        let screens = Dictionary(NSScreen.screens.compactMap { s in (s.deviceDescription[key] as? NSNumber).map { ($0.uint32Value, s) } }, uniquingKeysWith: { a, _ in a })
        var fbs = framebuffers()
        let dl = hasDisplayLink()
        return ids.prefix(Int(n)).map { id in
            var d = DisplayInfo(id: id, vendor: Int(CGDisplayVendorNumber(id)), model: Int(CGDisplayModelNumber(id)))
            (d.link, d.transport) = Displays.link(builtIn: CGDisplayIsBuiltin(id) != 0, vendor: d.vendor, model: d.model, framebuffers: &fbs, displayLink: dl)
            d.main = CGDisplayIsMain(id) != 0; d.mirrored = CGDisplayIsInMirrorSet(id) != 0
            d.rotation = Int(CGDisplayRotation(id))
            let mm = CGDisplayScreenSize(id); d.mmW = mm.width; d.mmH = mm.height
            if let m = CGDisplayCopyDisplayMode(id) { d.w = m.pixelWidth; d.h = m.pixelHeight; d.lw = m.width; d.lh = m.height; d.hz = m.refreshRate }
            let all = CGDisplayCopyAllDisplayModes(id, [kCGDisplayShowDuplicateLowResolutionModes: true] as CFDictionary) as? [CGDisplayMode] ?? []
            // 0x02000000 = kDisplayModeNativeFlag (IOGraphicsTypes.h)
            if let b = Displays.best(all.map { Displays.Mode(w: $0.width, h: $0.height, pw: $0.pixelWidth, ph: $0.pixelHeight, hz: $0.refreshRate, native: $0.ioFlags & 0x0200_0000 != 0) }) {
                (d.maxW, d.maxH, d.maxHz) = b
            }
            if let s = screens[id] {
                d.name = s.localizedName; d.scale = s.backingScaleFactor; d.edr = s.maximumPotentialExtendedDynamicRangeColorComponentValue
                d.bits = s.depth.bitsPerSample
                if d.hz == 0 { d.hz = Double(s.maximumFramesPerSecond) }   // sommige panelen melden 0 Hz in de modus
            }
            return d
        }
    }

    /// Externe framebuffers met een aangesloten scherm (DisplayAttributes): maker, model en HDMI/DP. DisplayLink heeft er geen.
    private static func framebuffers() -> [Displays.Framebuffer] {
        services("IOMobileFramebuffer").compactMap { e in
            defer { IOObjectRelease(e) }
            guard Ports.bool(prop(e, "external")), let a = prop(e, "DisplayAttributes") as? [String: Any],
                  let p = a["ProductAttributes"] as? [String: Any] else { return nil }
            return Displays.Framebuffer(vendor: Ports.int(p["LegacyManufacturerID"]) ?? -1, model: Ports.int(p["ProductID"]) ?? -1,
                                        transport: (prop(e, "Transport") as? [String: Any]).flatMap { Ports.str($0["Downstream"]) })
        }
    }

    /// Hangt er een DisplayLink-chip (USB 17E9) aan de Mac?
    private static func hasDisplayLink() -> Bool {
        guard let m = IOServiceMatching("IOUSBHostDevice") as NSMutableDictionary? else { return false }
        m["IOPropertyMatch"] = ["idVendor": Displays.displayLinkVID]
        let e = IOServiceGetMatchingService(kIOMainPortDefault, m)
        if e != 0 { IOObjectRelease(e) }
        return e != 0
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
    private var screensChanged: NSObjectProtocol?
    private let queue = DispatchQueue(label: "fusebox.ports", qos: .utility)

    func start() {
        guard notify == nil else { return }
        refresh()
        readDisplays()
        screensChanged = NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            MainActor.assumeIsolated { self?.readDisplays() }
        }
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
        if let o = screensChanged { NotificationCenter.default.removeObserver(o) }
        screensChanged = nil
    }

    private func readDisplays() {
        let d = PortReader.displays()
        if d != snap.displays { snap.displays = d }
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
            var s = PortReader.read()
            DispatchQueue.main.async {
                guard let self, self.notify != nil || !self.loaded else { return }   // na stop niets meer bijwerken
                s.displays = self.snap.displays
                if s != self.snap { self.snap = s }
                self.loaded = true
            }
        }
    }
}

// MARK: - Koptelefoonaansluiting (3,5 mm mini-jack)
//
// Op Apple Silicon verschijnt de ingebouwde koptelefoonuitgang als eigen geluidsapparaat (bron 'hdpn', "Externe
// koptelefoon") zodra er iets in de jack zit; een headset met microfoon geeft daarnaast een ingebouwde invoer met
// bron 'emic'. CoreAudio, geen toestemming nodig, een paar microseconden: gewoon bij elke vernieuwing van Docked.

struct AudioJack: Hashable {
    var plugged = false, mic = false, inUse = false
    var name = "", rateKHz: Double?

    static func read() -> AudioJack {
        var j = AudioJack()
        let def = value(AudioObjectID(kAudioObjectSystemObject), kAudioHardwarePropertyDefaultOutputDevice, kAudioObjectPropertyScopeGlobal) ?? 0
        for id in devices() where value(id, kAudioDevicePropertyTransportType, kAudioObjectPropertyScopeGlobal) == kAudioDeviceTransportTypeBuiltIn {
            if value(id, kAudioDevicePropertyDataSource, kAudioDevicePropertyScopeOutput) == code("hdpn") {
                j.plugged = true; j.inUse = id == def; j.name = name(id)
                var rate = 0.0, size = UInt32(MemoryLayout<Double>.size)
                var a = AudioObjectPropertyAddress(mSelector: kAudioDevicePropertyNominalSampleRate, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
                if AudioObjectGetPropertyData(id, &a, 0, nil, &size, &rate) == noErr, rate > 0 { j.rateKHz = rate / 1000 }
            }
            if value(id, kAudioDevicePropertyDataSource, kAudioDevicePropertyScopeInput) == code("emic") { j.mic = true }
        }
        return j
    }

    /// Wat er op de regel staat: wat er in zit, en of het geluid er nu heen gaat.
    var facts: [String] {
        guard plugged else { return [] }
        return [mic ? L("Headset with microphone") : L("Headphones or speakers"),
                inUse ? L("sound plays here") : L("not used for sound"),
                rateKHz.map { "\(trim($0)) kHz" }].compactMap { $0 }
    }

    private static func code(_ s: String) -> UInt32 { s.utf8.reduce(0) { $0 << 8 | UInt32($1) } }
    private static func value(_ id: AudioObjectID, _ sel: AudioObjectPropertySelector, _ scope: AudioObjectPropertyScope) -> UInt32? {
        var a = AudioObjectPropertyAddress(mSelector: sel, mScope: scope, mElement: kAudioObjectPropertyElementMain)
        var v: UInt32 = 0, size = UInt32(MemoryLayout<UInt32>.size)
        return AudioObjectGetPropertyData(id, &a, 0, nil, &size, &v) == noErr ? v : nil
    }
    private static func devices() -> [AudioObjectID] {
        var a = AudioObjectPropertyAddress(mSelector: kAudioHardwarePropertyDevices, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var size: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size) == noErr else { return [] }
        var ids = [AudioObjectID](repeating: 0, count: Int(size) / MemoryLayout<AudioObjectID>.size)
        return AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &a, 0, nil, &size, &ids) == noErr ? ids : []
    }
    private static func name(_ id: AudioObjectID) -> String {
        var a = AudioObjectPropertyAddress(mSelector: kAudioObjectPropertyName, mScope: kAudioObjectPropertyScopeGlobal, mElement: kAudioObjectPropertyElementMain)
        var n: Unmanaged<CFString>?, size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        return AudioObjectGetPropertyData(id, &a, 0, nil, &size, &n) == noErr ? (n?.takeRetainedValue() as String? ?? "") : ""
    }

    static func selfTest() {
        precondition(AudioJack().facts.isEmpty, "jack leeg")
        precondition(AudioJack(plugged: true, mic: true, inUse: true, rateKHz: 48).facts == ["Headset with microphone", "sound plays here", "48 kHz"], "jack headset")
        print("mini-jack ok")
    }
}
