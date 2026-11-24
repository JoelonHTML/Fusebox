import SwiftUI
import Network
import Darwin
import os

// MARK: - iPhone-kant van de gedeelde code uit ../Sources
//
// De rekenregels, de scanner en de thema's komen uit de Mac-bestanden. Hier staat alleen wat iOS anders doet:
// welk netwerk, welke router, en een paar kleine vertalingen van Mac-typen. Geen MAC-adressen, geen netwerknaam:
// iOS geeft die niet (zie ios/PLAN.md).

// MARK: Instellingen (eigen, korte lijst; de Mac heeft Prefs.swift)

enum Prefs {
    static func register() {
        UserDefaults.standard.register(defaults: [
            "theme": "instrument", "appearanceMode": "system", "accent": "", "textScale": 1.0, "font": "style",
            "netAutoScan": true, "speedMB": false, "pingHost": "1.1.1.1",
        ])
    }
}

// MARK: Verbinding volgens iOS (NWPathMonitor loopt de hele tijd mee; kost niets zolang er niets verandert)

enum NetPath {
    /// Gaat af als de verbinding verandert (wifi ↔ mobiel, netwerk weg of terug).
    static let changed = Notification.Name("FuseboxNetPathChanged")
    nonisolated(unsafe) private static var ready = false
    private static let monitor: NWPathMonitor = {
        let m = NWPathMonitor()
        m.pathUpdateHandler = { _ in
            ready = true
            DispatchQueue.main.async { NotificationCenter.default.post(name: changed, object: nil) }
        }
        m.start(queue: .global(qos: .utility))
        return m
    }()
    static func start() { _ = monitor }
    static var path: NWPath { monitor.currentPath }
    /// Vlak na het starten weet iOS de verbinding nog niet: even wachten op het eerste bericht (hooguit 1 s),
    /// en de router opzoeken als dit een ander netwerk is dan de vorige keer.
    static func whenReady() async {
        for _ in 0..<20 where !ready { try? await Task.sleep(for: .milliseconds(50)) }
        guard !cellular, let me = lanInterface()?.ip, hop.withLock({ $0?.me }) != me else { return }
        let found = await Task.detached(priority: .utility) { firstHop() }.value
        hop.withLock { $0 = (me, found) }
    }

    /// Router van dit netwerk. nil = onbekend (dan meet Fusebox alleen het internet).
    static var router: UInt32? {
        for g in path.gateways {   // op iOS meestal leeg; als iOS het wel weet, is dit de snelste weg
            if case let .hostPort(host, _) = g, case let .ipv4(a) = host { return a.rawValue.reduce(UInt32(0)) { $0 << 8 | UInt32($1) } }
        }
        return hop.withLock { $0?.router }
    }

    private static let hop = OSAllocatedUnfairLock<(me: UInt32, router: UInt32?)?>(initialState: nil)

    /// Router vinden met één ping die maar één stap ver mag (TTL 1): de router stuurt "tijd verstreken" terug en
    /// verraadt zo zijn adres. Dit is de weg die Apple aanraadt; het pakje komt niet verder dan je eigen router.
    private static func firstHop() -> UInt32? {
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        var ttl: Int32 = 1, wait = timeval(tv_sec: 0, tv_usec: 600_000)
        setsockopt(fd, IPPROTO_IP, IP_TTL, &ttl, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &wait, socklen_t(MemoryLayout<timeval>.size))
        var to = NetProbe.sockaddrIn(0x0101_0101)   // richting 1.1.1.1; het pakje sterft bij de eerste stap
        for seq in 0..<2 {
            let packet = NetScanner.echoPacket(id: 0xF0B0, seq: UInt16(seq))
            let sent = withUnsafePointer(to: &to) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { sendto(fd, packet, packet.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
            guard sent == packet.count else { return nil }
            var buf = [UInt8](repeating: 0, count: 256), from = sockaddr_in(), len = socklen_t(MemoryLayout<sockaddr_in>.size)
            let n = withUnsafeMutablePointer(to: &from) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) } }
            // Antwoord met IP-kop (begint met 0x45): het ICMP-type staat erachter. 11 = tijd verstreken.
            guard n > 0, let first = buf.first else { continue }
            let off = first >> 4 == 4 ? Int(first & 0x0F) * 4 : 0
            let ip = UInt32(bigEndian: from.sin_addr.s_addr)
            if n > off, buf[off] == 11, NetProbe.isLAN(ip) { return ip }
        }
        return nil
    }
    static var cellular: Bool { path.usesInterfaceType(.cellular) && !path.usesInterfaceType(.wifi) && !path.usesInterfaceType(.wiredEthernet) }
}

/// IPv4-adressen per interface die aan staat (getifaddrs), plus de wereldwijde IPv6-adressen.
private func interfaces() -> [(name: String, ip: UInt32, mask: UInt32, v6: [String])] {
    var list: UnsafeMutablePointer<ifaddrs>?
    guard getifaddrs(&list) == 0 else { return [] }
    defer { freeifaddrs(list) }
    var v4: [(String, UInt32, UInt32)] = [], v6: [String: [String]] = [:]
    var p = list
    while let a = p?.pointee {
        defer { p = a.ifa_next }
        let flags = Int32(a.ifa_flags)
        guard flags & IFF_UP != 0, flags & IFF_RUNNING != 0, flags & IFF_LOOPBACK == 0, let sa = a.ifa_addr else { continue }
        let name = String(cString: a.ifa_name)
        if sa.pointee.sa_family == UInt8(AF_INET), let m = a.ifa_netmask {
            let ip = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            let mask = m.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            if ip != 0, ip >> 16 != 0xA9FE { v4.append((name, ip, mask)) }   // 169.254.x.x = geen echt netwerk
        } else if sa.pointee.sa_family == UInt8(AF_INET6) {
            var addr = sa.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { $0.pointee.sin6_addr }
            guard withUnsafeBytes(of: &addr, { $0[0] }) & 0xE0 == 0x20 else { continue }   // alleen wereldwijde adressen (2000::/3)
            var buf = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
            if inet_ntop(AF_INET6, &addr, &buf, socklen_t(buf.count)) != nil { v6[name, default: []].append(String(cString: buf)) }
        }
    }
    return v4.map { ($0.0, $0.1, $0.2, v6[$0.0] ?? []) }
}

/// Wi-Fi of een kabeladapter: de "en"-interface met een adres (en0 = Wi-Fi op de iPhone gaat voor).
private func lanInterface() -> (name: String, ip: UInt32, mask: UInt32, v6: [String])? {
    let en = interfaces().filter { $0.name.hasPrefix("en") }
    return en.first { $0.name == "en0" } ?? en.first
}

extension NetInfo {
    /// Het eigen netwerk voor de LAN-scan. nil = niet op Wi-Fi of kabel (mobiele data heeft geen lokaal netwerk).
    static func current() -> NetInfo? {
        guard let i = lanInterface() else { return nil }
        let r = NetPath.router.flatMap { $0 & i.mask == i.ip & i.mask ? $0 : nil }   // alleen een router in dit subnet
        return NetInfo(iface: i.name, ip: i.ip, mask: i.mask, mac: nil, router: r)
    }
}

extension NetConnection {
    /// `kind` voor mobiele data (naast "Wi-Fi" en "Ethernet" van de Mac). Intern Engels; in beeld vertaald.
    static let mobile = "Mobile data"

    static func current() -> NetConnection? {
        let path = NetPath.path
        if NetPath.cellular {
            let cell = interfaces().first { $0.name.hasPrefix("pdp_ip") }
            return NetConnection(iface: cell?.name ?? "pdp_ip0", kind: mobile, ipv4: cell.map { ipString($0.ip) }, ipv6: cell?.v6 ?? [],
                                 router: nil, dns: [], ipv6Internet: path.supportsIPv6)
        }
        guard let i = lanInterface() else { return nil }
        let router = NetPath.router.flatMap { $0 & i.mask == i.ip & i.mask ? $0 : nil }
        return NetConnection(iface: i.name, kind: path.usesInterfaceType(.wiredEthernet) && !path.usesInterfaceType(.wifi) ? "Ethernet" : "Wi-Fi",
                             ipv4: ipString(i.ip), ipv6: i.v6, router: router, dns: [], ipv6Internet: !i.v6.isEmpty && path.supportsIPv6)
    }

    /// Naam van de soort verbinding in beeld.
    var kindTitle: String { kind == Self.mobile ? L("Mobile data") : kind }
}

// MARK: Mobiele data: oordeel zonder router en zonder wifi

extension InternetStats {
    /// Zoek het probleem op mobiele data: alleen internet en DNS (er is geen eigen router om te meten).
    nonisolated static func findMobile(internet: PingStats, dnsMs: Double?) -> Finding {
        let nums = [L("internet \(internet.got == 0 ? L("no reply") : msText(internet.avg) + " ms")"), "DNS " + (dnsMs.map { msText($0) + " ms" } ?? L("no reply"))]
        func say(_ text: String, _ ok: Bool?) -> Finding { Finding(text: text, ok: ok, numbers: nums.joined(separator: ", ")) }
        if let v = mobileVerdict(internet), v.ok != true { return say(v.text, v.ok) }
        guard let dnsMs else { return say(L("Name lookups (DNS) fail over mobile data: turn Airplane Mode on and off, then try again"), false) }
        if dnsMs > 300 { return say(L("Name lookups are slow over mobile data (\(msText(dnsMs)) ms): websites take a while to start loading"), nil) }
        return say(L("No problem found: mobile data, DNS and internet are all fine"), true)
    }

    /// Snel oordeel op mobiele data. nil = nog niets gemeten.
    nonisolated static func mobileVerdict(_ i: PingStats?) -> (text: String, ok: Bool?)? {
        guard let i else { return nil }
        if i.got == 0 { return (L("No answer from the internet over mobile data: check your reception, and whether mobile data is on for Fusebox in Settings"), false) }
        if i.loss >= 0.05 || (i.avg ?? 0) > 150 || (i.jitter ?? 0) > 40 {
            return (L("Mobile data is slow or unsteady here (internet \(msText(i.avg)) ms): a spot with better reception, or Wi-Fi, helps"), nil)
        }
        return (L("All good on mobile data: internet \(msText(i.avg)) ms, no packet loss"), true)
    }
}

// MARK: Rapport en gedeelde tekst: adressen eruit (de Mac heeft Diag.scrub met ook gebruikers- en computernamen;
// de iPhone-app kent die namen niet eens)

enum Diag {
    static func scrub(_ text: String) -> String {
        text.replacingOccurrences(of: #"\b(?:\d{1,3}\.){3}\d{1,3}\b"#, with: "x.x.x.x", options: .regularExpression)
            .replacingOccurrences(of: #"\b(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}\b"#, with: "xx:xx:xx:xx:xx:xx", options: .regularExpression)
    }
}

// MARK: Kleuren: Theme.swift rekent met NSColor; op iOS is dat UIColor met dezelfde vragen

typealias NSColor = UIColor
enum SRGBSpace { case sRGB }
extension UIColor {
    func usingColorSpace(_ s: SRGBSpace) -> UIColor? { self }
    private var rgba: (r: CGFloat, g: CGFloat, b: CGFloat) {
        var r: CGFloat = 0, g: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        getRed(&r, green: &g, blue: &b, alpha: &a)
        return (r, g, b)
    }
    private var hsb: (h: CGFloat, s: CGFloat) {
        var h: CGFloat = 0, s: CGFloat = 0, b: CGFloat = 0, a: CGFloat = 0
        getHue(&h, saturation: &s, brightness: &b, alpha: &a)
        return (h, s)
    }
    var redComponent: CGFloat { rgba.r }
    var greenComponent: CGFloat { rgba.g }
    var blueComponent: CGFloat { rgba.b }
    var hueComponent: CGFloat { hsb.h }
    var saturationComponent: CGFloat { hsb.s }
    static var windowBackgroundColor: UIColor { .systemGroupedBackground }
}
extension Color {
    init(nsColor: UIColor) { self.init(uiColor: nsColor) }
}
