import Foundation
import Network
import SystemConfiguration
import Darwin

// MARK: - Draadloos op de Docked-pagina: Bluetooth-apparaten en mobiel internet (hotspot, USB-tethering, modem)
//
// Bronnen (geen toestemmingsvraag):
// - Bluetooth: `system_profiler SPBluetoothDataType -json` (naam, soort, batterij incl. L/R/hoesje, firmware, IDs, signaal).
//   IORegistry heeft alleen HID-apparaten en meestal geen batterij; IOBluetooth vraagt om toestemming → niet gebruikt.
//   IORegistry (AppleDeviceManagementHIDEventService "BatteryPercent") zou Magic-apparaten goedkoop geven, maar
//   system_profiler geeft die al als device_batteryLevelMain → niet nodig.
//   Buiten de hoofdthread, gedeelde cache: zolang de Docked-pagina open is hooguit eens per 2 min,
//   voor het feit op de grote BAT-kaart hooguit eens per 5 min.
// - Mobiel internet: NWPathMonitor (duur/beperkt, soort interface) + SystemConfiguration (naam "iPhone USB" e.d.)
//   + het vaste iPhone-hotspotsubnet 172.20.10.x. Geen SSID (die vraagt locatietoestemming).
// Adressen en serienummers van apparaten tonen we nergens.

struct BTBattery: Hashable { let label: String, pct: Int }   // label: "" (hoofd), "L", "R", "case"

struct BTDevice: Identifiable, Hashable {
    var id = "", name = "", minor = ""
    var connected = false
    var batteries: [BTBattery] = []
    var rssi: Int?
    var services: [String] = []         // "A2DP", "HFP", "HID", "BLE" …
    var firmware: String?, vendorID: Int?, productID: Int?, vendor: String?
    var raw: [RawLine] = []
    var kind: (name: String, icon: String) { Wireless.kind(minor, name: name, buds: batteries.contains { $0.label == "L" }) }
    /// Eén getal: hoofdbatterij, anders de leegste oortje (hoesje telt niet mee).
    var battery: Int? { batteries.first { $0.label.isEmpty }?.pct ?? batteries.filter { $0.label != "case" }.map(\.pct).min() }
}

struct BTState: Hashable {
    var on = true
    var chip: String?, firmware: String?, transport: String?
    var connected: [BTDevice] = [], paired: [BTDevice] = []
}

/// Internet loopt via een telefoon of modem.
struct Uplink: Hashable {
    var title = "", icon = "personalhotspot", via = "", iface = ""
    var metered = false, lowData = false
    var how = ""                         // waaraan we het zagen
}

enum Wireless {
    // MARK: Bluetooth (puur)

    static func bluetooth(_ data: Data) -> BTState? {
        guard let top = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let c = (top["SPBluetoothDataType"] as? [[String: Any]])?.first else { return nil }
        let ctrl = c["controller_properties"] as? [String: Any] ?? [:]
        func list(_ key: String, _ on: Bool) -> [BTDevice] {
            (c[key] as? [[String: Any]] ?? []).flatMap { $0.map { device($0.key, $0.value as? [String: Any] ?? [:], connected: on) } }
                .sorted { $0.name.localizedStandardCompare($1.name) == .orderedAscending }
        }
        return BTState(on: Ports.str(ctrl["controller_state"]).map { $0 != "attrib_off" } ?? true,
                       chip: Ports.str(ctrl["controller_chipset"]), firmware: Ports.str(ctrl["controller_firmwareVersion"]),
                       transport: Ports.str(ctrl["controller_transport"]),
                       connected: list("device_connected", true), paired: list("device_not_connected", false))
    }

    static func device(_ name: String, _ p: [String: Any], connected: Bool) -> BTDevice {
        var d = BTDevice(id: "bt:" + (Ports.str(p["device_address"]) ?? name), name: name, minor: Ports.str(p["device_minorType"]) ?? "", connected: connected)
        for (key, label) in [("Main", ""), ("Left", "L"), ("Right", "R"), ("Case", "case"), ("", "")] {
            if let s = Ports.str(p["device_batteryLevel" + key]), let v = Int(s.filter(\.isNumber)), !d.batteries.contains(where: { $0.label == label }) {
                d.batteries.append(BTBattery(label: label, pct: v))
            }
        }
        d.rssi = Ports.str(p["device_rssi"]).flatMap { Int($0) } ?? Ports.int(p["device_rssi"])
        d.services = services(Ports.str(p["device_services"]))
        d.firmware = Ports.str(p["device_firmwareVersion"])
        (d.vendorID, d.vendor) = hexAndName(Ports.str(p["device_vendorID"]))
        d.productID = hexAndName(Ports.str(p["device_productID"])).0
        // Ruwe waarden zonder adres en serienummers
        d.raw = Ports.rawLines(p.filter { !$0.key.localizedCaseInsensitiveContains("address") && !$0.key.localizedCaseInsensitiveContains("serial") })
            .map { RawLine(key: $0.key.replacingOccurrences(of: "device_", with: ""), value: $0.value) }
        return d
    }

    /// "0x400020 < HID BLE >" → ["HID", "BLE"]
    static func services(_ s: String?) -> [String] {
        guard let s, let a = s.firstIndex(of: "<"), let b = s.lastIndex(of: ">"), a < b else { return [] }
        return s[s.index(after: a)..<b].split(separator: " ").map(String.init)
    }

    /// "0x004C (Apple)" → (0x4C, "Apple")
    static func hexAndName(_ s: String?) -> (Int?, String?) {
        guard let s else { return (nil, nil) }
        let hex = s.split(separator: " ").first.map { $0.hasPrefix("0x") ? String($0.dropFirst(2)) : String($0) }
        let name = s.firstIndex(of: "(").map { String(s[s.index(after: $0)...].prefix { $0 != ")" }) }
        return (hex.flatMap { Int($0, radix: 16) }, name?.isEmpty == false ? name : nil)
    }

    /// Soort en icoon uit minorType; zonder minorType grof op naam.
    static func kind(_ minor: String, name: String, buds: Bool) -> (name: String, icon: String) {
        let m = minor.lowercased(), n = name.lowercased()
        let table: [([String], String, String)] = [
            (["keyboard"], "Keyboard", "keyboard"), (["mouse"], "Mouse", "computermouse"),
            (["trackpad", "touchpad"], "Trackpad", "rectangle.and.hand.point.up.left"),
            (["headphone", "headset", "earbud"], buds ? "Earbuds" : "Headphones", buds ? "airpods" : "headphones"),
            (["speaker", "audio"], "Speaker", "hifispeaker"), (["gamepad", "joystick", "game"], "Controller", "gamecontroller"),
            (["phone"], "Phone", "iphone"), (["watch", "wearable"], "Watch", "applewatch"),
            (["computer", "laptop", "desktop"], "Computer", "laptopcomputer"), (["pen", "digitizer"], "Pen", "applepencil")]
        if let t = table.first(where: { $0.0.contains(where: m.contains) }) { return (t.1, t.2) }
        if m.isEmpty {
            for (k, v) in [("airpods", ("Earbuds", "airpods")), ("watch", ("Watch", "applewatch")), ("iphone", ("Phone", "iphone")),
                           ("ipad", ("Tablet", "ipad")), ("mac", ("Computer", "laptopcomputer"))] where n.contains(k) { return v }
            if buds { return ("Earbuds", "airpods") }
        }
        return (m.isEmpty ? "Device" : minor, "dot.radiowaves.left.and.right")
    }

    static func signal(_ rssi: Int) -> String { rssi >= -60 ? "strong" : rssi >= -75 ? "fair" : "weak" }

    /// Leegste verbonden apparaat voor de BAT-kaart: ("AirPods", "L 80% · R 75%") of ("Mouse", "64%"). Hoesje telt niet mee.
    static func lowest(_ s: BTState?) -> (name: String, value: String)? {
        guard let d = s?.connected.compactMap({ d in d.battery.map { (d, $0) } }).min(by: { $0.1 < $1.1 }) else { return nil }
        return (d.0.name, batteryText(d.0.batteries.filter { $0.label != "case" }) ?? "\(d.1)%")
    }

    /// "L 81% · R 79% · case 40%" (alleen als er meer dan één is)
    static func batteryText(_ b: [BTBattery]) -> String? {
        b.count > 1 ? b.map { ($0.label.isEmpty ? "" : $0.label + " ") + "\($0.pct)%" }.joined(separator: " · ") : nil
    }

    // MARK: Mobiel internet (puur)

    /// `type`: soort volgens NWPathMonitor ("wifi", "wired", "cellular", "other"). nil = gewoon internet, niets tonen.
    static func uplink(iface: String, type: String, scType: String?, scName: String?, expensive: Bool, constrained: Bool, router: String?) -> Uplink? {
        let name = scName ?? "", lower = name.lowercased()
        var u = Uplink(iface: iface, metered: expensive, lowData: constrained)
        if let dev = ["iPhone", "iPad"].first(where: { lower.contains($0.lowercased()) }) {
            (u.title, u.via, u.how) = ("Internet via \(dev) (USB)", "USB", "interface \"\(name)\"")
        } else if scType == "Bluetooth" || lower.contains("bluetooth") {
            (u.title, u.via, u.icon, u.how) = ("Internet via Bluetooth tethering", "Bluetooth", "personalhotspot", "interface \"\(name)\"")
        } else if type == "cellular" || scType == "WWAN" || iface.hasPrefix("pdp_ip") || iface.hasPrefix("wwan")
                    || ["modem", "lte", "4g", "5g", "cellular", "mobile broadband"].contains(where: lower.contains) {
            (u.title, u.via, u.icon, u.how) = ("Internet via mobile modem", name.isEmpty ? "modem" : name, "antenna.radiowaves.left.and.right",
                                               "cellular interface" + (name.isEmpty ? "" : " \"\(name)\""))
        } else if type == "wifi", router?.hasPrefix("172.20.10.") == true {
            (u.title, u.via, u.how) = ("Internet via iPhone hotspot", "Wi-Fi", "router at 172.20.10.1 (Personal Hotspot address)")
        } else if type == "wifi", expensive {
            (u.title, u.via, u.how) = ("Internet via phone hotspot", "Wi-Fi", "macOS marks this Wi-Fi as a hotspot (metered)")
        } else if expensive {
            (u.title, u.via, u.icon, u.how) = ("Internet via a metered connection", name.isEmpty ? iface : name, "gauge.with.dots.needle.33percent", "macOS marks it metered")
        } else {
            return nil
        }
        return u
    }

    // MARK: Zelftest (verzonnen data)

    static func selfTest() {
        let json = """
        {"SPBluetoothDataType":[{"controller_properties":{"controller_address":"00:11:22:33:44:55","controller_chipset":"TEST_1","controller_state":"attrib_on",
          "controller_firmwareVersion":"1.2.3","controller_transport":"PCIe"},
          "device_connected":[
            {"Test Mouse":{"device_address":"AA:BB:CC:DD:EE:01","device_batteryLevelMain":"7%","device_minorType":"Mouse","device_vendorID":"0x1234",
              "device_productID":"0xB000","device_services":"0x400020 < HID BLE >","device_firmwareVersion":"T1"}},
            {"Test Buds":{"device_address":"AA:BB:CC:DD:EE:02","device_batteryLevelLeft":"81%","device_batteryLevelRight":"64%","device_batteryLevelCase":"40%",
              "device_minorType":"Headphones","device_vendorID":"0x004C (Apple)","device_serialNumber":"SERIAL1","device_rssi":"-58",
              "device_services":"0x1 < A2DP AVRCP HFP >"}}],
          "device_not_connected":[{"Test Speaker":{"device_address":"AA:BB:CC:DD:EE:03","device_minorType":"Speaker","device_rssi":"-80"}},
            {"Someone's Watch":{"device_address":"AA:BB:CC:DD:EE:04"}}]}]}
        """
        let s = bluetooth(Data(json.utf8))!
        let rows = WirelessTree.build(bt: s, uplink: nil, down: 0, up: 0).groups.first?.children ?? []
        let (wt0, wt1) = (rows[0], rows[1])   // Test Buds (64%), Test Mouse (7%)
        precondition(s.on && s.chip == "TEST_1" && s.connected.map(\.name) == ["Test Buds", "Test Mouse"] && s.paired.count == 2, "bt lijst")
        let buds = s.connected[0], mouse = s.connected[1]
        precondition(buds.kind.name == "Earbuds" && buds.battery == 64 && buds.rssi == -58 && buds.services == ["A2DP", "AVRCP", "HFP"], "bt oordopjes")
        precondition(batteryText(buds.batteries) == "L 81% · R 64% · case 40%" && buds.vendorID == 0x4C && buds.vendor == "Apple", "bt batterij")
        precondition(!buds.raw.contains { $0.value.contains("AA:BB") || $0.value.contains("SERIAL") || $0.key.contains("serial") } && buds.raw.contains { $0.key == "minorType" }, "bt geen adres")
        precondition(mouse.kind == ("Mouse", "computermouse") && mouse.battery == 7 && batteryText(mouse.batteries) == nil && mouse.productID == 0xB000 && mouse.vendor == nil, "bt muis")
        precondition(s.paired[0].kind.name == "Watch" && s.paired[1].kind.name == "Speaker" && signal(-80) == "weak" && signal(-58) == "strong", "bt soort")
        precondition(lowest(s).map { [$0.name, $0.value] } == ["Test Mouse", "7%"] && lowest(nil) == nil && lowest(BTState()) == nil, "bt leegste")
        precondition(lowest(BTState(connected: [buds])).map { [$0.name, $0.value] } == ["Test Buds", "L 81% · R 64%"], "bt leegste oordopjes")
        precondition(wt0.main?.unit == "%" && wt0.main?.slow == false && wt0.main?.icon == "battery.75percent", "bt rij batterij")
        precondition(wt1.main?.slow == true && wt1.main?.icon == "battery.0percent" && wt1.warn != nil, "bt rij laag")
        precondition(bluetooth(Data("{}".utf8)) == nil && bluetooth(Data("x".utf8)) == nil && services("none") == [], "bt leeg")
        var off = json.replacingOccurrences(of: "attrib_on", with: "attrib_off")
        off = off.replacingOccurrences(of: "\"device_connected\"", with: "\"device_x\"")
        precondition(bluetooth(Data(off.utf8)).map { !$0.on && $0.connected.isEmpty } == true, "bt uit")
        let wt = WirelessTree.build(bt: s, uplink: nil, down: 0, up: 0)
        precondition(wt.groups.first?.children.map(\.title) == ["Test Buds", "Test Mouse"] && wt.groups.first?.children[1].warn != nil && wt.paired.count == 2
                     && wt.paired.allSatisfy(\.dim), "bt boom")
        precondition(!wt.groups[0].children.flatMap { $0.pairs.map(\.value) + $0.facts }.contains { $0.contains("AA:BB") }, "bt boom geen adres")
        precondition(WirelessTree.build(bt: BTState(), uplink: nil, down: 0, up: 0).groups.isEmpty, "bt niets aan")

        // Mobiel internet
        func u(_ iface: String, _ type: String, _ scType: String?, _ scName: String?, exp: Bool = false, con: Bool = false, router: String? = nil) -> Uplink? {
            uplink(iface: iface, type: type, scType: scType, scName: scName, expensive: exp, constrained: con, router: router)
        }
        precondition(u("en0", "wifi", "IEEE80211", "Wi-Fi", router: "192.168.1.1") == nil && u("en5", "wired", "Ethernet", "USB LAN") == nil, "gewoon")
        precondition(u("en0", "wifi", "IEEE80211", "Wi-Fi", exp: true, router: "172.20.10.1")?.title == "Internet via iPhone hotspot", "wifi hotspot")
        precondition(u("en0", "wifi", "IEEE80211", "Wi-Fi", exp: true, con: true, router: "10.0.0.1").map { [$0.title, $0.via] } == ["Internet via phone hotspot", "Wi-Fi"]
                     && u("en0", "wifi", nil, nil, exp: true, con: true)?.lowData == true, "android hotspot")
        precondition(u("en7", "wired", "Ethernet", "iPhone USB", exp: true).map { [$0.title, $0.via] } == ["Internet via iPhone (USB)", "USB"], "iphone usb")
        precondition(u("en8", "wired", "Ethernet", "Bluetooth PAN")?.title == "Internet via Bluetooth tethering", "bt pan")
        precondition(u("en9", "wired", "WWAN", "Test LTE Modem")?.title == "Internet via mobile modem" && u("pdp_ip0", "cellular", nil, nil)?.via == "modem", "modem")
        precondition(u("en6", "wired", "Ethernet", "Test Phone", exp: true)?.title == "Internet via a metered connection"
                     && u("bridge0", "wired", "Bridge", "Thunderbolt Bridge") == nil, "overig")
        let hot = WirelessTree.build(bt: nil, uplink: u("en7", "wired", "Ethernet", "iPhone USB", exp: true), down: 1_500_000, up: 80_000, bits: false).groups
        precondition(hot.count == 1 && hot[0].facts.contains("metered") && hot[0].main?.now == 1.5 && hot[0].main?.unit == "MB/s ↓", "hotspot regel \(hot.first?.facts ?? [])")
    }
}

// MARK: - Regels voor de Docked-lijst

enum WirelessTree {
    /// `groups`: hotspot en "Bluetooth" met verbonden apparaten; `paired`: gekoppeld maar niet verbonden (ingeklapt getoond).
    static func build(bt: BTState?, uplink: Uplink?, down: Double, up: Double,
                      bits: Bool = UserDefaults.standard.bool(forKey: "netBits")) -> (groups: [TNode], paired: [TNode]) {
        var groups: [TNode] = []
        if let u = uplink {
            let r = ratePair(down, up, bits: bits)
            groups.append(TNode(id: "uplink", kind: .port, icon: u.icon, title: u.title,
                                main: LinkLoad(now: Double(r.0), max: nil, unit: r.2 + " ↓"),
                                facts: [u.via, u.metered ? "metered" : nil, u.lowData ? "Low Data Mode" : nil, "↑ \(r.1) \(r.2)"].compactMap { $0 },
                                pairs: [RawLine(key: "Interface", value: u.iface), RawLine(key: "Through", value: u.via),
                                        RawLine(key: "Metered", value: u.metered ? "Yes: macOS and apps may hold back big downloads and backups" : "No"),
                                        RawLine(key: "Low Data", value: u.lowData ? "On" : "Off"), RawLine(key: "Seen from", value: u.how),
                                        RawLine(key: "Now", value: "↓ \(r.0) · ↑ \(r.1) \(r.2)")]))
        }
        guard let bt else { return (groups, []) }
        if !bt.on || !bt.connected.isEmpty {
            let n = bt.connected.count
            groups.append(TNode(id: "bt", kind: .port, icon: "antenna.radiowaves.left.and.right", title: "Bluetooth",
                                facts: [bt.on ? "\(n) connected" : "off"],
                                children: bt.connected.map(device),
                                pairs: [("State", bt.on ? "On" : "Off"), ("Chip", bt.chip), ("Firmware", bt.firmware), ("Bus", bt.transport)]
                                    .compactMap { k, v in v.map { RawLine(key: k, value: $0) } }))
        }
        return (groups, bt.paired.map { var d = device($0); d.dim = true; d.main = nil; return d })
    }

    private static func device(_ d: BTDevice) -> TNode {
        let k = d.kind, low = d.connected && (d.battery ?? 100) <= 10, lowish = d.connected && (d.battery ?? 100) <= 20
        var pairs = [RawLine(key: "Type", value: k.name)]
        pairs += d.batteries.map { RawLine(key: $0.label.isEmpty ? "Battery" : $0.label == "case" ? "Case" : $0.label == "L" ? "Left" : "Right", value: "\($0.pct)%") }
        if let r = d.rssi { pairs.append(RawLine(key: "Signal", value: "\(r) dBm (\(Wireless.signal(r)))")) }
        if !d.services.isEmpty { pairs.append(RawLine(key: "Profiles", value: d.services.joined(separator: ", "))) }
        if let f = d.firmware { pairs.append(RawLine(key: "Firmware", value: f)) }
        if let v = d.vendorID {
            pairs.append(RawLine(key: "IDs", value: String(format: "vendor %04X", v) + (d.vendor.map { " (\($0))" } ?? "") + (d.productID.map { String(format: " · product %04X", $0) } ?? "")))
        }
        let facts: [String?] = d.connected ? [k.name, Wireless.batteryText(d.batteries), d.rssi.map { "signal " + Wireless.signal($0) }]
                                           : [k.name, d.rssi != nil ? "in range" : nil]
        return TNode(id: d.id, kind: .device, icon: k.icon, title: d.name,
                     main: d.battery.map { LinkLoad(now: Double($0), max: nil, slow: lowish, unit: "%", icon: batteryIcon(Double($0))) },
                     facts: facts.compactMap { $0 }, warn: low ? "Battery low. Charge it soon." : nil,
                     pairs: pairs, raw: d.raw)
    }
}

// MARK: - Lezen

enum WirelessReader {
    /// ~0,1–0,3 s; nooit op de hoofdthread.
    static func bluetooth() -> BTState? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/sbin/system_profiler")
        p.arguments = ["-json", "-timeout", "10", "SPBluetoothDataType"]
        let out = Pipe()
        p.standardOutput = out; p.standardError = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return nil }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return Wireless.bluetooth(data)
    }

    /// Soort en naam van een interface volgens SystemConfiguration ("Ethernet", "iPhone USB").
    static func scInterface(_ bsd: String) -> (type: String?, name: String?)? {
        guard let i = (SCNetworkInterfaceCopyAll() as? [SCNetworkInterface])?.first(where: { SCNetworkInterfaceGetBSDName($0) as String? == bsd }) else { return nil }
        return (SCNetworkInterfaceGetInterfaceType(i) as String?, SCNetworkInterfaceGetLocalizedDisplayName(i) as String?)
    }

    /// Router, alleen als deze interface de hoofdroute is.
    static func router(_ iface: String) -> String? {
        let g = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv4" as CFString) as? [String: Any]
        return g?["PrimaryInterface"] as? String == iface ? g?["Router"] as? String : nil
    }

    /// Bytes in/uit van één interface (32-bit tellers, zoals in Meten.swift).
    static func bytes(_ iface: String) -> (i: UInt32, o: UInt32)? {
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { return nil }
        defer { freeifaddrs(list) }
        var p = list
        while let a = p?.pointee {
            defer { p = a.ifa_next }
            guard a.ifa_addr?.pointee.sa_family == UInt8(AF_LINK), String(cString: a.ifa_name) == iface,
                  let d = a.ifa_data?.assumingMemoryBound(to: if_data.self) else { continue }
            return (d.pointee.ifi_ibytes, d.pointee.ifi_obytes)
        }
        return nil
    }
}

// MARK: - Bijhouden zolang de pagina open is

@MainActor
final class WirelessWatcher: ObservableObject {
    @Published private(set) var bt: BTState?
    @Published private(set) var uplink: Uplink?
    @Published private(set) var down = 0.0
    @Published private(set) var up = 0.0
    private var monitor: NWPathMonitor?
    private var timer: Timer?
    private var last: (i: UInt32, o: UInt32, t: Date)?
    private static var reading = false
    private static var cache: (at: Date, state: BTState?)?   // gedeeld door pagina en BAT-kaart, ook over pagina-wissels heen
    private static let queue = DispatchQueue(label: "fusebox.wireless", qos: .utility)

    func start() {
        guard monitor == nil else { return }
        let m = NWPathMonitor()
        m.pathUpdateHandler = { [weak self] p in MainActor.assumeIsolated { self?.path(p) } }
        m.start(queue: .main)
        monitor = m
        readBT()
        timer = Timer.scheduledTimer(withTimeInterval: 2, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.readBT(); self?.readRate() }
        }
    }

    func stop() {
        monitor?.cancel(); monitor = nil
        timer?.invalidate(); timer = nil
        last = nil
    }

    deinit { monitor?.cancel(); timer?.invalidate() }

    private func readBT() {
        Self.load(maxAge: 120) { [weak self] s in
            if let self, self.monitor != nil, self.bt != s { self.bt = s }   // na stop niets meer bijwerken
        }
    }

    /// Cache jonger dan `maxAge` → meteen; anders system_profiler op de achtergrond (één tegelijk).
    private static func load(maxAge: TimeInterval, _ done: @escaping @MainActor (BTState?) -> Void = { _ in }) {
        if let c = cache, Date().timeIntervalSince(c.at) < maxAge { done(c.state); return }
        guard !reading else { return }
        reading = true
        queue.async {
            let s = WirelessReader.bluetooth()
            DispatchQueue.main.async { MainActor.assumeIsolated { cache = (Date(), s); reading = false; done(s) } }
        }
    }

    /// Feit voor de grote BAT-kaart (alleen L/XL op de Live-pagina): het leegste Bluetooth-apparaat, hooguit eens per 5 min gelezen.
    static func lowestFact() -> Fact? {
        load(maxAge: 300)
        return Wireless.lowest(cache?.state).map { Fact(label: $0.name, value: $0.value) }
    }

    private func path(_ p: NWPath) {
        guard monitor != nil else { return }
        var u: Uplink?
        // Eerste echte interface (VPN = .other overslaan)
        if p.status == .satisfied, let i = p.availableInterfaces.first(where: { $0.type != .other && $0.type != .loopback }) {
            let type = switch i.type { case .wifi: "wifi"; case .cellular: "cellular"; case .wiredEthernet: "wired"; default: "other" }
            let sc = WirelessReader.scInterface(i.name)
            u = Wireless.uplink(iface: i.name, type: type, scType: sc?.type, scName: sc?.name,
                                expensive: p.isExpensive, constrained: p.isConstrained, router: WirelessReader.router(i.name))
        }
        if u != uplink { uplink = u; last = nil; down = 0; up = 0; readRate() }
    }

    private func readRate() {
        guard let u = uplink, let c = WirelessReader.bytes(u.iface) else { return }
        let now = Date()
        if let l = last, case let dt = now.timeIntervalSince(l.t), dt > 0.5 {
            down = Double(c.i &- l.i) / dt; up = Double(c.o &- l.o) / dt   // &-: één wrap van de 32-bit teller
        }
        last = (c.i, c.o, now)
    }
}
