import SwiftUI
import Network
import CoreWLAN
import SystemConfiguration
import Darwin

// MARK: - Netwerk: welke apparaten zitten er op je netwerk, en waar zit een vertraging (router of provider)
//
// Scant alleen als dit tabblad open is of na "Scan"; niets in de 2-s-tik. Alles blijft op de Mac:
// er gaan alleen ping-pakketjes het lokale netwerk op (+ één TCP-verbinding naar `pingHost`, als die aan staat).

struct NetworkView: View {
    @StateObject private var scanner = NetScanner()
    @Environment(\.theme) private var theme
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @State private var sort = NetColumn.ip
    @State private var ascending = true
    @State private var width = NetWidth.kind
    @State private var open: String?

    static let ipW: CGFloat = 104, vendorW: CGFloat = 118, kindW: CGFloat = 104, msW: CGFloat = 42

    var body: some View {
        let rows = sorted(scanner.devices)
        let tight = theme.rowStyle == .lines || theme.rowStyle == .plain
        VStack(spacing: 8) {
            summary
            HStack(spacing: 8) {
                Text(countText(rows)).font(theme.label(11)).foregroundStyle(theme.secondary)
                Spacer()
                if let start = scanner.scanStart {
                    ProgressView(timerInterval: start...start.addingTimeInterval(NetScanner.expected), countsDown: false,
                                 label: { EmptyView() }, currentValueLabel: { EmptyView() })
                        .progressViewStyle(.linear).frame(width: 90)
                }
                Button { scanner.scan() } label: {
                    Label(scanner.scanStart == nil ? "Scan" : "Scanning…", systemImage: "arrow.clockwise")
                        .font(theme.label(11)).foregroundStyle(theme.accent)
                        .padding(.horizontal, 7).padding(.vertical, 3)
                        .background(theme.accent.opacity(0.15), in: .rect(cornerRadius: 6))
                        .contentShape(.rect)
                }
                .buttonStyle(.plain).disabled(scanner.scanStart != nil).help("Look for devices on this network")
            }
            if scanner.localBlocked { blockedHint }
            columnHeader.padding(.horizontal, tight ? 0 : 10)
            ScrollView {
                LazyVStack(spacing: tight ? 0 : 6) {
                    ForEach(Array(rows.enumerated()), id: \.element.id) { i, d in
                        NetRow(device: d, width: width, index: i, isOpen: open == d.id) {
                            withAnimation(.snappy(duration: 0.25)) { open = open == d.id ? nil : d.id }
                        }
                    }
                    if rows.isEmpty {
                        Text(scanner.info == nil ? "Not connected to a network" : scanner.scanStart != nil ? "Looking for devices…" : "No devices found")
                            .font(theme.label(12)).foregroundStyle(theme.secondary).padding(.vertical, 20)
                    }
                }
            }
        }
        .padding(.horizontal, 12).padding(.bottom, 10)
        .onGeometryChange(for: NetWidth.self) { NetWidth($0.size.width) } action: { width = $0 }
        .onAppear { scanner.scan() }
        .onDisappear { scanner.stop() }
    }

    private func countText(_ rows: [NetDevice]) -> String {
        let new = rows.filter(\.isNew).count
        return "\(rows.count) device\(rows.count == 1 ? "" : "s")" + (new > 0 ? " · \(new) new" : "")
    }

    // MARK: Samenvatting bovenaan

    private var summary: some View {
        VStack(alignment: .leading, spacing: 5) {
            if let info = scanner.info {
                line("desktopcomputer", "This Mac \(ipString(info.ip)) on \(info.networkLabel) · \(info.wifi == nil ? "wired" : "Wi-Fi")")
                HStack(spacing: 18) {
                    stat("Router", info.router.map(ipString) ?? "unknown", scanner.routerMs)
                    stat("Internet", pingHost.isEmpty ? "check off" : pingHost, pingHost.isEmpty ? nil : scanner.internetMs)
                }
                if scanner.scanStart == nil, scanner.measured {
                    let v = NetScanner.verdict(router: scanner.routerMs, internet: scanner.internetMs, internetCheck: !pingHost.isEmpty)
                    line(v.ok == false ? "exclamationmark.triangle" : v.ok == true ? "checkmark.circle" : "info.circle", v.text)
                        .foregroundStyle(theme.rowStyle == .blocks ? .white : v.ok == false ? theme.warn : v.ok == true ? theme.good : theme.onRow(true))
                }
                if let w = info.wifi { line("wifi", w.text) }
            } else {
                line("wifi.slash", "Not connected to a network")
            }
        }
        .font(theme.label(11)).foregroundStyle(theme.onRow(true))
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 0)
    }

    private func stat(_ title: String, _ sub: String, _ ms: Double?) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            Text(title).font(theme.label(11)).foregroundStyle(theme.onRow())
            Text(scanner.scanStart != nil && ms == nil ? "…" : msText(ms)).font(theme.number(15)).foregroundStyle(theme.onRow())
            Text(sub).lineLimit(1).foregroundStyle(theme.onRow(true))
        }
    }

    private func line(_ icon: String, _ text: String) -> some View {
        Label { Text(text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon).frame(width: 14) }
    }

    private var blockedHint: some View {
        HStack(spacing: 6) {
            Image(systemName: "lock.shield")
            Text("macOS is blocking the scan. Allow Fusebox under Privacy & Security › Local Network, then press Scan.")
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button("Open") {
                if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork") { NSWorkspace.shared.open(u) }
            }
            .buttonStyle(.plain).foregroundStyle(theme.accent)
        }
        .font(theme.label(11)).foregroundStyle(theme.warn)
    }

    // MARK: Kolommen

    private var columnHeader: some View {
        HStack(spacing: 8) {
            head("Name", .name).frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 26)
            head("IP", .ip).frame(width: Self.ipW, alignment: .leading)
            if width >= .vendor { head("Vendor", .vendor).frame(width: Self.vendorW, alignment: .leading) }
            if width >= .kind { head("Type", .kind).frame(width: Self.kindW, alignment: .leading) }
            head("ms", .ms).frame(width: Self.msW, alignment: .trailing).help("Reply time in milliseconds")
        }
    }

    private func head(_ title: String, _ col: NetColumn) -> some View {
        Button {
            if sort == col { ascending.toggle() } else { sort = col; ascending = true }
        } label: {
            HStack(spacing: 2) {
                Text(title)
                if sort == col { Image(systemName: ascending ? "chevron.up" : "chevron.down").font(.system(size: 8, weight: .bold)) }
            }
            .font(theme.label(10)).foregroundStyle(sort == col ? theme.text : theme.secondary)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private func sorted(_ list: [NetDevice]) -> [NetDevice] {
        list.sorted { a, b in
            let less: Bool = switch sort {
            case .name: a.name.localizedStandardCompare(b.name) == .orderedAscending
            case .ip: a.ip < b.ip
            case .vendor: a.vendor.localizedStandardCompare(b.vendor) == .orderedAscending
            case .kind: a.kind.title < b.kind.title
            case .ms: (a.ms ?? .infinity) < (b.ms ?? .infinity)
            }
            return ascending ? less : !less
        }
    }
}

private enum NetColumn { case name, ip, vendor, kind, ms }
/// Breedte-banden: smal Naam/IP/ms, + Vendor, + Type.
private enum NetWidth: Int, Comparable {
    case narrow, vendor, kind
    init(_ w: CGFloat) { self = w >= 600 ? .kind : w >= 470 ? .vendor : .narrow }
    static func < (a: NetWidth, b: NetWidth) -> Bool { a.rawValue < b.rawValue }
}

private func msText(_ ms: Double?) -> String { ms.map { $0 < 1 ? "<1" : "\(Int($0.rounded()))" } ?? "–" }

private struct NetRow: View {
    let device: NetDevice, width: NetWidth, index: Int, isOpen: Bool, onTap: () -> Void
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            summary.contentShape(.rect).onTapGesture(perform: onTap)
            if isOpen { details.transition(.opacity.combined(with: .move(edge: .top))) }
        }
        .font(theme.number(12)).foregroundStyle(theme.onRow())
        .themedRow(theme, index: index)
        .clipped()
    }

    private var summary: some View {
        HStack(spacing: 8) {
            Image(systemName: device.kind.icon).font(.system(size: 12)).frame(width: 18).foregroundStyle(theme.onRow(true))
            HStack(spacing: 5) {
                Text(device.name).font(theme.label(12))
                if let tag = device.isMe ? "This Mac" : device.isRouter ? "Router" : nil {
                    Text(tag).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                }
                if device.isNew {
                    Text("New").font(theme.label(9)).padding(.horizontal, 4).padding(.vertical, 1)
                        .foregroundStyle(theme.rowStyle == .blocks ? .white : theme.accent)
                        .background((theme.rowStyle == .blocks ? Color.white : theme.accent).opacity(0.18), in: .capsule)
                }
            }
            .lineLimit(1).truncationMode(.tail)
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(ipString(device.ip)).frame(width: NetworkView.ipW, alignment: .leading)
            if width >= .vendor { Text(device.vendor).font(theme.label(11)).lineLimit(1).frame(width: NetworkView.vendorW, alignment: .leading) }
            if width >= .kind { Text(device.kind.title).font(theme.label(11)).lineLimit(1).frame(width: NetworkView.kindW, alignment: .leading) }
            Text(msText(device.ms)).frame(width: NetworkView.msW, alignment: .trailing)
        }
    }

    private var details: some View {
        VStack(alignment: .leading, spacing: 5) {
            line("network", "IP address \(ipString(device.ip))")
            line("barcode", device.mac.map { "Hardware address \(macString($0))" } ?? "Hardware address unknown")
            if let mac = device.mac, NetScanner.isPrivate(mac) {
                line("lock", "Private address: a phone or laptop that hides its real address (Wi-Fi privacy). The maker can't be seen.")
            } else {
                line("building.2", "Made by \(device.vendor == "–" ? "an unknown maker" : device.vendor)")
            }
            line(device.kind.icon, "Looks like: \(device.kind.title.lowercased())" + (device.model.map { " (\($0))" } ?? ""))
            if !device.services.isEmpty {
                line("dot.radiowaves.left.and.right", "Offers: " + device.services.map(NetScanner.serviceTitle).sorted().joined(separator: ", "))
            }
            line("timer", device.ms.map { "Answers in \(msText($0)) ms" } ?? "Doesn't answer pings (many phones and TVs don't; that's normal)")
            if let f = device.firstSeen { line("clock", "First seen \(f.formatted(date: .abbreviated, time: .shortened))") }
            if let l = device.lastSeen { line("clock.arrow.circlepath", "Last seen \(l.formatted(date: .abbreviated, time: .shortened))") }
        }
        .font(theme.label(11)).foregroundStyle(theme.onRow(true))
        .padding(.leading, 26).padding(.top, 6).padding(.bottom, 2)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func line(_ icon: String, _ text: String) -> some View {
        Label { Text(text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon).frame(width: 14) }
    }
}

// MARK: - Model

enum DeviceKind: String {
    case phone, computer, tv, speaker, printer, router, nas, smartHome, console, unknown
    var title: String {
        switch self {
        case .phone: "Phone or tablet"
        case .computer: "Computer"
        case .tv: "TV / streaming"
        case .speaker: "Speaker"
        case .printer: "Printer"
        case .router: "Router / Wi-Fi"
        case .nas: "Storage (NAS)"
        case .smartHome: "Smart home"
        case .console: "Game console"
        case .unknown: "Unknown"
        }
    }
    var icon: String {
        switch self {
        case .phone: "iphone"
        case .computer: "laptopcomputer"
        case .tv: "tv"
        case .speaker: "hifispeaker"
        case .printer: "printer"
        case .router: "wifi.router"
        case .nas: "externaldrive.connected.to.line.below"
        case .smartHome: "lightbulb"
        case .console: "gamecontroller"
        case .unknown: "questionmark.circle"
        }
    }
}

struct NetDevice: Identifiable {
    var id: String { ipString(ip) }
    let ip: UInt32
    var mac: [UInt8]?
    var ms: Double?
    var isMe = false, isRouter = false
    var name = "", vendor = "–", model: String?
    var services: Set<String> = []
    var kind = DeviceKind.unknown
    var firstSeen: Date?, lastSeen: Date?
    var isNew = false
}

/// Deze Mac op het netwerk: interface, adres, masker, router en (bij Wi-Fi) signaal.
struct NetInfo {
    let iface: String, ip: UInt32, mask: UInt32, mac: [UInt8]?, router: UInt32?, wifi: Wifi?
    struct Wifi {
        let rssi: Int, noise: Int, rate: Double
        var text: String {
            let word = rssi >= -60 ? "good" : rssi >= -70 ? "fair" : "weak"
            return "Signal: \(word) (\(minus(rssi)) dBm)" + (noise < 0 ? " · noise \(minus(noise)) dBm" : "") + (rate > 0 ? " · \(Int(rate)) Mbit/s" : "")
        }
        private func minus(_ v: Int) -> String { v < 0 ? "−\(-v)" : "\(v)" }
    }
    var hosts: [UInt32] { NetScanner.hosts(ip: ip, mask: mask).filter { $0 != ip } }
    var networkLabel: String {
        let real = "\(ipString(ip & mask))/\(mask.nonzeroBitCount)"
        return mask.nonzeroBitCount < 24 ? real + " (scanning \(ipString(ip & 0xFFFF_FF00))/24)" : real
    }

    static func current() -> NetInfo? {
        let global = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv4" as CFString) as? [String: Any]
        guard var iface = global?["PrimaryInterface"] as? String else { return nil }
        var router = (global?["Router"] as? String).flatMap(parseIP)
        // ponytail: VPN als hoofdroute → terug naar en0 zonder router; precieze router per dienst opzoeken als dat vaak voorkomt.
        if ["utun", "ipsec", "ppp"].contains(where: iface.hasPrefix) { iface = "en0"; router = nil }
        var ip: UInt32?, mask: UInt32 = 0, mac: [UInt8]?
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { return nil }
        defer { freeifaddrs(list) }
        var p = list
        while let a = p?.pointee {
            defer { p = a.ifa_next }
            guard String(cString: a.ifa_name) == iface, let sa = a.ifa_addr else { continue }
            if sa.pointee.sa_family == UInt8(AF_INET), let m = a.ifa_netmask {
                ip = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
                mask = m.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            } else if sa.pointee.sa_family == UInt8(AF_LINK) {
                mac = sa.withMemoryRebound(to: sockaddr_dl.self, capacity: 1) { dl in
                    let n = Int(dl.pointee.sdl_nlen), len = Int(dl.pointee.sdl_alen)
                    guard len == 6 else { return nil }
                    return UnsafeRawPointer(dl).withMemoryRebound(to: UInt8.self, capacity: 8 + n + len) { Array(UnsafeBufferPointer(start: $0 + 8 + n, count: len)) }
                }
            }
        }
        guard let ip, ip != 0 else { return nil }
        var wifi: Wifi?
        if CWWiFiClient.shared().interfaceNames()?.contains(iface) == true, let w = CWWiFiClient.shared().interface(withName: iface), w.rssiValue() != 0 {
            wifi = Wifi(rssi: w.rssiValue(), noise: w.noiseMeasurement(), rate: w.transmitRate())
        }
        return NetInfo(iface: iface, ip: ip, mask: mask, mac: mac, router: router, wifi: wifi)
    }
}

func ipString(_ ip: UInt32) -> String { "\(ip >> 24).\(ip >> 16 & 255).\(ip >> 8 & 255).\(ip & 255)" }
func parseIP(_ s: String) -> UInt32? {
    let p = s.split(separator: ".").compactMap { UInt32($0) }
    guard p.count == 4, p.allSatisfy({ $0 < 256 }) else { return nil }
    return p[0] << 24 | p[1] << 16 | p[2] << 8 | p[3]
}
func macString(_ m: [UInt8]) -> String { m.map { String(format: "%02X", $0) }.joined(separator: ":") }

// MARK: - Scanner

@MainActor
final class NetScanner: ObservableObject {
    /// Verwachte duur van een scan (voor de voortgangsbalk).
    static let expected: TimeInterval = 4.5
    @Published private(set) var info: NetInfo?
    @Published private(set) var scanStart: Date?
    @Published private(set) var routerMs: Double?
    @Published private(set) var internetMs: Double?
    @Published private(set) var measured = false
    @Published private(set) var localBlocked = false
    @Published private var found: [UInt32: (mac: [UInt8]?, ms: Double?)] = [:]
    @Published private var bonjour: [UInt32: Bonjour] = [:]
    @Published private var rdns: [UInt32: String] = [:]
    private var store = NetDeviceStore.load()
    private var work: Task<Void, Never>?
    private var browsers: [NWBrowser] = []
    private var resolving: [NWEndpoint: NWConnection] = [:]

    struct Bonjour { var name: String?; var types: Set<String> = []; var model: String? }

    var devices: [NetDevice] {
        guard let info else { return [] }
        let subnet = Set(info.hosts)
        var ips = Set(found.keys)
        ips.formUnion(bonjour.keys.filter(subnet.contains))
        if !ips.isEmpty || scanStart == nil { ips.insert(info.ip) }
        return ips.map { ip in
            var d = NetDevice(ip: ip)
            d.isMe = ip == info.ip
            d.isRouter = ip == info.router
            d.mac = d.isMe ? info.mac : found[ip]?.mac
            d.ms = d.isMe ? nil : found[ip]?.ms
            let bj = bonjour[ip]
            d.services = bj?.types ?? []
            d.model = bj?.model
            let privateMAC = d.mac.map(Self.isPrivate) ?? false
            d.vendor = privateMAC ? "Private address" : d.mac.flatMap(Self.vendor) ?? "–"
            let known = d.mac.flatMap { store.devices[macString($0)] }
            d.name = d.isMe ? (SCDynamicStoreCopyComputerName(nil, nil) as String? ?? "This Mac")
                : bj?.name ?? rdns[ip] ?? known?.name ?? (d.isRouter ? "Router" : d.vendor != "–" && !privateMAC ? "\(d.vendor) device" : "Unknown device")
            d.kind = Self.guess(name: d.name, vendor: d.vendor, services: d.services, model: d.model, isRouter: d.isRouter, isMe: d.isMe, privateMAC: privateMAC)
            d.firstSeen = known?.first
            d.lastSeen = known?.last
            d.isNew = known.map { Calendar.current.isDateInToday($0.first) && $0.first > store.created.addingTimeInterval(60) } ?? false
            return d
        }
    }

    func scan() {
        stop()
        info = NetInfo.current()
        found = [:]; bonjour = [:]; rdns = [:]; routerMs = nil; internetMs = nil; measured = false; localBlocked = false
        guard let info else { return }
        scanStart = Date()
        browse()
        let hosts = info.hosts, router = info.router
        let pingHost = UserDefaults.standard.string(forKey: "pingHost") ?? ""
        let ifindex = UInt16(truncatingIfNeeded: if_nametoindex(info.iface))
        work = Task { [weak self] in
            async let internet = Self.tcpPing(pingHost)
            let sweep = await Self.offMain { Self.sweep(hosts) }
            guard !Task.isCancelled, let self else { return }
            let subnet = Set(hosts)
            let arp = await Self.offMain { Self.arpTable() }.filter { $0.ifindex == ifindex && subnet.contains($0.ip) }
            var f: [UInt32: (mac: [UInt8]?, ms: Double?)] = [:]
            for e in arp { f[e.ip] = (e.mac, sweep.ms[e.ip]) }
            for (ip, ms) in sweep.ms where f[ip] == nil { f[ip] = (nil, ms) }
            self.found = f
            // Antwoorden maar lege ARP-tabel: macOS verbergt die zonder Lokaal netwerk-toestemming
            self.localBlocked = self.localBlocked || sweep.blocked || (arp.isEmpty && !sweep.ms.isEmpty)
            self.routerMs = router.flatMap { sweep.ms[$0] }
            self.internetMs = await internet
            self.measured = true
            let names = await Self.reverseNames(Array(f.keys), timeout: 1.5)
            guard !Task.isCancelled else { return }
            self.rdns = names
            self.remember()
            self.stopBrowsing()
            self.scanStart = nil
        }
    }

    func stop() {
        work?.cancel(); work = nil
        stopBrowsing()
        scanStart = nil
    }

    private func stopBrowsing() {
        browsers.forEach { $0.cancel() }; browsers = []
        resolving.values.forEach { $0.cancel() }; resolving = [:]
    }

    /// Gezien-lijst bijwerken (MAC → naam, eerst/laatst gezien) en opslaan.
    private func remember() {
        let now = Date()
        for d in devices {
            guard let mac = d.mac else { continue }
            let key = macString(mac)
            var k = store.devices[key] ?? NetDeviceStore.Known(name: nil, first: now, last: now)
            if !["Unknown device", "Router"].contains(d.name), !d.name.hasSuffix(" device") { k.name = d.name }
            k.last = now
            store.devices[key] = k
        }
        store.save()
    }

    // MARK: Bonjour

    static let serviceTypes = ["_companion-link._tcp", "_device-info._tcp", "_airplay._tcp", "_raop._tcp", "_googlecast._tcp",
                               "_sonos._tcp", "_spotify-connect._tcp", "_ipp._tcp", "_printer._tcp", "_smb._tcp",
                               "_hap._tcp", "_homekit._tcp", "_http._tcp"]

    static func serviceTitle(_ type: String) -> String {
        switch type {
        case "_companion-link._tcp", "_device-info._tcp": "Apple device"
        case "_airplay._tcp": "AirPlay"
        case "_raop._tcp": "AirPlay audio"
        case "_googlecast._tcp": "Chromecast"
        case "_sonos._tcp": "Sonos"
        case "_spotify-connect._tcp": "Spotify Connect"
        case "_ipp._tcp", "_printer._tcp": "Printing"
        case "_smb._tcp": "File sharing"
        case "_hap._tcp", "_homekit._tcp": "HomeKit"
        case "_http._tcp": "Web page"
        default: type
        }
    }

    private func browse() {
        for type in Self.serviceTypes {
            let b = NWBrowser(for: .bonjourWithTXTRecord(type: type, domain: "local."), using: .tcp)
            b.browseResultsChangedHandler = { [weak self] results, _ in
                MainActor.assumeIsolated { self?.resolve(results, type: type) }
            }
            b.stateUpdateHandler = { [weak self] state in
                if case .waiting(let e) = state, case .dns(let code) = e, code == -65570 {   // kDNSServiceErr_PolicyDenied
                    MainActor.assumeIsolated { self?.localBlocked = true }
                }
            }
            b.start(queue: .main)
            browsers.append(b)
        }
    }

    /// Bonjour-dienst → IPv4 via een UDP-"verbinding" (stuurt niets naar het apparaat, alleen mDNS-opzoeken).
    private func resolve(_ results: Set<NWBrowser.Result>, type: String) {
        for r in results where resolving[r.endpoint] == nil {
            guard case let .service(rawName, _, _, _) = r.endpoint else { continue }
            var txt: [String: String] = [:]
            if case let .bonjour(t) = r.metadata { txt = t.dictionary }
            let params = NWParameters.udp
            (params.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options)?.version = .v4
            let c = NWConnection(to: r.endpoint, using: params)
            c.stateUpdateHandler = { [weak self, weak c] state in
                guard case .ready = state, let c else { if case .failed = state { c?.cancel() }; return }
                if case let .hostPort(host, _)? = c.currentPath?.remoteEndpoint, case let .ipv4(a) = host {
                    let ip = a.rawValue.reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
                    MainActor.assumeIsolated { self?.add(ip, name: Self.friendlyName(rawName, type: type, txt: txt), type: type, txt: txt) }
                }
                c.cancel()
            }
            c.start(queue: .main)
            resolving[r.endpoint] = c
        }
    }

    private func add(_ ip: UInt32, name: String?, type: String, txt: [String: String]) {
        var b = bonjour[ip] ?? Bonjour()
        b.types.insert(type)
        // Naam: eerste dienst in `serviceTypes`-volgorde wint (Apple-namen > AirPlay > ... > http)
        let rank = { (t: String) in Self.serviceTypes.firstIndex(of: t) ?? 99 }
        if let name, b.name == nil || b.types.filter({ $0 != type }).allSatisfy({ rank($0) > rank(type) }) { b.name = name }
        if b.model == nil { b.model = txt["model"] ?? txt["md"] ?? txt["rpMd"] }
        bonjour[ip] = b
    }

    nonisolated static func friendlyName(_ raw: String, type: String, txt: [String: String]) -> String? {
        if type == "_googlecast._tcp", let fn = txt["fn"], !fn.isEmpty { return fn }
        // RAOP: "A1B2C3D4E5F6@Woonkamer" → "Woonkamer"
        let name = type == "_raop._tcp" ? String(raw.split(separator: "@", maxSplits: 1).last ?? "") : raw
        return name.isEmpty ? nil : name
    }

    // MARK: Pings, ARP, namen (los van de main thread)

    nonisolated static func offMain<T: Sendable>(_ body: @escaping @Sendable () -> T) async -> T {
        let t = Task.detached(priority: .utility, operation: body)
        return await withTaskCancellationHandler { await t.value } onCancel: { t.cancel() }
    }

    /// Reactietijd naar het internet: TCP-verbinding naar poort 443 (zoals de Live-ping), beste van 2. "" = uit.
    nonisolated static func tcpPing(_ host: String) async -> Double? {
        guard !host.isEmpty else { return nil }
        var best: Double?
        for _ in 0..<2 {
            guard !Task.isCancelled else { return nil }
            let ms: Double? = await withCheckedContinuation { cont in
                DispatchQueue.main.async {
                    let start = DispatchTime.now()
                    let c = NWConnection(host: NWEndpoint.Host(host), port: 443, using: .tcp)
                    var done = false
                    func finish(_ ms: Double?) { guard !done else { return }; done = true; c.cancel(); cont.resume(returning: ms) }
                    c.stateUpdateHandler = { state in
                        switch state {
                        case .ready: finish(Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1e6)
                        case .failed, .waiting: finish(nil)
                        default: break
                        }
                    }
                    c.start(queue: .main)
                    DispatchQueue.main.asyncAfter(deadline: .now() + 2) { finish(nil) }
                }
            }
            if let ms { best = min(best ?? ms, ms) } else if best == nil { return nil }
        }
        return best
    }

    /// ICMP-echo naar elk adres (onbevoegde datagram-socket, geen root). Dat vult meteen de ARP-tabel.
    /// Twee rondes (de tweede zonder ARP-vertraging); per adres de snelste reactie in ms.
    /// `blocked`: niets kon verstuurd worden (meestal: Lokaal netwerk-toestemming ontbreekt).
    nonisolated static func sweep(_ hosts: [UInt32], wait: Double = 2.6) -> (ms: [UInt32: Double], blocked: Bool) {
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)
        guard fd >= 0, !hosts.isEmpty else { return ([:], false) }
        defer { close(fd) }
        _ = fcntl(fd, F_SETFL, O_NONBLOCK)
        let ident = UInt16.random(in: 1...0xFFFF)
        let gap: UInt64 = 3_000_000, round2: UInt64 = 1_000_000_000   // 3 ms tussen pakketjes, 2e ronde na 1 s
        let start = DispatchTime.now().uptimeNanoseconds, end = start + UInt64(wait * 1e9)
        var sentAt = [Int: UInt64](), best = [UInt32: Double](), ok = 0, failed = 0, next = 0
        var buf = [UInt8](repeating: 0, count: 1500)
        while !Task.isCancelled {
            let now = DispatchTime.now().uptimeNanoseconds
            if now >= end { break }
            while next < hosts.count * 2 {
                let i = next % hosts.count
                guard start + (next < hosts.count ? 0 : round2) + UInt64(i) * gap <= now else { break }
                let packet = echoPacket(id: ident, seq: UInt16(next))
                var to = sockaddr_in()
                to.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                to.sin_family = sa_family_t(AF_INET)
                to.sin_addr.s_addr = hosts[i].bigEndian
                let n = withUnsafePointer(to: &to) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, packet, packet.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
                if n == packet.count { ok += 1; sentAt[next] = DispatchTime.now().uptimeNanoseconds } else { failed += 1 }
                next += 1
            }
            var p = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            _ = poll(&p, 1, 2)
            while true {
                var from = sockaddr_in(), len = socklen_t(MemoryLayout<sockaddr_in>.size)
                let n = withUnsafeMutablePointer(to: &from) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) } }
                guard n > 0 else { break }
                let t = DispatchTime.now().uptimeNanoseconds
                guard let r = parseEchoReply(buf[0..<n]), r.id == ident, let s = sentAt[Int(r.seq)] else { continue }
                let ip = UInt32(bigEndian: from.sin_addr.s_addr)
                best[ip] = min(best[ip] ?? .infinity, Double(t - s) / 1e6)
            }
        }
        return (best, ok == 0 && failed > 0)
    }

    nonisolated static func echoPacket(id: UInt16, seq: UInt16) -> [UInt8] {
        var p: [UInt8] = [8, 0, 0, 0, UInt8(id >> 8), UInt8(id & 255), UInt8(seq >> 8), UInt8(seq & 255)] + Array("Fusebox!".utf8)
        let c = checksum(p)
        p[2] = UInt8(c >> 8); p[3] = UInt8(c & 255)
        return p
    }

    nonisolated static func checksum(_ b: [UInt8]) -> UInt16 {
        var sum: UInt32 = 0
        for i in stride(from: 0, to: b.count, by: 2) { sum += UInt32(b[i]) << 8 | (i + 1 < b.count ? UInt32(b[i + 1]) : 0) }
        while sum >> 16 != 0 { sum = (sum & 0xFFFF) + (sum >> 16) }
        return ~UInt16(sum)
    }

    /// Echo-antwoord ontleden; macOS levert bij datagram-ICMP de IP-kop mee (of niet): beide kunnen.
    nonisolated static func parseEchoReply(_ s: ArraySlice<UInt8>) -> (id: UInt16, seq: UInt16)? {
        let b = Array(s)
        guard let first = b.first else { return nil }
        let off = first >> 4 == 4 ? Int(first & 0x0F) * 4 : 0
        guard b.count >= off + 8, b[off] == 0 else { return nil }   // type 0 = echo reply
        return (UInt16(b[off + 4]) << 8 | UInt16(b[off + 5]), UInt16(b[off + 6]) << 8 | UInt16(b[off + 7]))
    }

    struct ArpEntry { let ip: UInt32, mac: [UInt8], ifindex: UInt16 }

    /// ARP-tabel van de kernel (sysctl, geen root). Zonder Lokaal netwerk-toestemming geeft macOS hier een lege lijst.
    nonisolated static func arpTable() -> [ArpEntry] {
        var mib: [Int32] = [CTL_NET, PF_ROUTE, 0, AF_INET, NET_RT_FLAGS, RTF_LLINFO]
        var len = 0
        guard sysctl(&mib, 6, nil, &len, nil, 0) == 0, len > 0 else { return [] }
        len += len / 2 + 1024   // tabel kan intussen groeien
        var buf = [UInt8](repeating: 0, count: len)
        guard sysctl(&mib, 6, &buf, &len, nil, 0) == 0 else { return [] }
        return parseARP(Array(buf[0..<len]))
    }

    /// Reeks rt_msghdr + sockaddr_in (doel) + sockaddr_dl (MAC). Onvolledige regels (geen MAC) en broadcast overslaan.
    nonisolated static func parseARP(_ b: [UInt8]) -> [ArpEntry] {
        let header = MemoryLayout<rt_msghdr>.size
        func roundup(_ a: Int) -> Int { a > 0 ? 1 + ((a - 1) | 3) : 4 }
        var out: [ArpEntry] = [], off = 0
        while off + 2 <= b.count {
            let msglen = Int(b[off]) | Int(b[off + 1]) << 8
            guard msglen > 0, off + msglen <= b.count else { break }
            defer { off += msglen }
            var sa = off + header
            guard sa + 8 <= off + msglen, b[sa + 1] == AF_INET else { continue }
            let ip = UInt32(b[sa + 4]) << 24 | UInt32(b[sa + 5]) << 16 | UInt32(b[sa + 6]) << 8 | UInt32(b[sa + 7])
            sa += roundup(Int(b[sa]))
            guard sa + 8 <= off + msglen, b[sa + 1] == AF_LINK else { continue }
            let index = UInt16(b[sa + 2]) | UInt16(b[sa + 3]) << 8, nlen = Int(b[sa + 5]), alen = Int(b[sa + 6])
            let m = sa + 8 + nlen
            guard alen == 6, m + 6 <= off + msglen else { continue }
            let mac = Array(b[m..<m + 6])
            guard mac != [255, 255, 255, 255, 255, 255], mac[0] & 1 == 0 else { continue }   // broadcast / multicast
            out.append(ArpEntry(ip: ip, mac: mac, ifindex: index))
        }
        return out
    }

    /// Omgekeerde DNS (vaak de naam die de router via DHCP kent), alle tegelijk, hooguit `timeout` s.
    nonisolated static func reverseNames(_ ips: [UInt32], timeout: Double) async -> [UInt32: String] {
        final class Box: @unchecked Sendable { let lock = NSLock(); var names: [UInt32: String] = [:] }
        let box = Box()
        let group = DispatchGroup()
        for ip in ips {
            group.enter()
            DispatchQueue.global(qos: .utility).async {
                defer { group.leave() }
                var a = sockaddr_in()
                a.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
                a.sin_family = sa_family_t(AF_INET)
                a.sin_addr.s_addr = ip.bigEndian
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                let r = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    getnameinfo($0, socklen_t(MemoryLayout<sockaddr_in>.size), &host, socklen_t(host.count), nil, 0, NI_NAMEREQD) } }
                guard r == 0, let name = cleanHostname(String(cString: host)) else { return }
                box.lock.withLock { box.names[ip] = name }
            }
        }
        return await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .utility).async {
                _ = group.wait(timeout: .now() + timeout)
                cont.resume(returning: box.lock.withLock { box.names })
            }
        }
    }

    /// "alex-iphone.fritz.box" → "alex-iphone"; IP-adressen als naam weg.
    nonisolated static func cleanHostname(_ s: String) -> String? {
        guard !s.isEmpty, parseIP(s) == nil else { return nil }
        let first = s.split(separator: ".").first.map(String.init) ?? s
        return first.isEmpty ? nil : first
    }

    // MARK: Reine logica

    /// Te scannen adressen: het hele subnet tot /24; groter → de /24 rond ons eigen adres (max 254).
    nonisolated static func hosts(ip: UInt32, mask: UInt32) -> [UInt32] {
        let m = mask.nonzeroBitCount < 24 ? 0xFFFF_FF00 : mask
        let net = ip & m, broadcast = net | ~m
        guard broadcast > net + 1 else { return [] }   // /31, /32: geen buren
        return Array(net + 1..<broadcast)
    }

    /// Lokaal beheerd adres (bit 2 van de eerste byte): willekeurig MAC-adres van iPhone/Android/Mac met Wi-Fi-privacy.
    nonisolated static func isPrivate(_ mac: [UInt8]) -> Bool { (mac.first ?? 0) & 0x02 != 0 }

    nonisolated static func vendor(_ mac: [UInt8]) -> String? {
        guard mac.count >= 3 else { return nil }
        return ouiTable[UInt32(mac[0]) << 16 | UInt32(mac[1]) << 8 | UInt32(mac[2])]
    }

    /// Router en internet vergelijken → wie is de boosdoener. ok: true = goed, false = probleem, nil = weet niet.
    nonisolated static func verdict(router r: Double?, internet i: Double?, internetCheck: Bool) -> (text: String, ok: Bool?) {
        let rSlow = (r ?? 0) > 30, iSlow = (i ?? 0) > 100   // ms; thuis-Wi-Fi naar router is meestal < 10
        guard internetCheck else {
            guard r != nil else { return ("Router doesn't answer pings; internet check is off", nil) }
            return rSlow ? ("Router slow → Wi-Fi signal or router", false) : ("Router fast (internet check is off in Settings)", true)
        }
        switch (r, i) {
        case (nil, nil): return ("No answer from router or internet → check the connection", false)
        case (_?, nil): return ("Router answers, internet doesn't → your provider or modem", false)
        case (nil, _?): return iSlow ? ("Internet slow (router doesn't answer pings)", false) : ("Internet fast (router doesn't answer pings)", true)
        case (_?, _?):
            if rSlow && iSlow { return ("Both slow → Wi-Fi or router", false) }
            if rSlow { return ("Router slow → Wi-Fi signal or router", false) }
            if iSlow { return ("Router fast, internet slow → your provider", false) }
            return ("Router fast, internet fast → all good", true)
        }
    }

    /// Soort apparaat raden uit diensten, model, naam en maker. ponytail: simpele trefwoorden; volgorde bepaalt de winnaar.
    nonisolated static func guess(name: String, vendor: String, services: Set<String>, model: String?,
                                  isRouter: Bool, isMe: Bool, privateMAC: Bool) -> DeviceKind {
        if isRouter { return .router }
        if isMe { return .computer }
        let n = (name + " " + (model ?? "")).lowercased()
        func has(_ words: String...) -> Bool { words.contains { n.contains($0) } }
        func offers(_ types: String...) -> Bool { types.contains { services.contains($0) } }
        if offers("_ipp._tcp", "_printer._tcp") || ["HP", "Canon", "Epson", "Brother"].contains(vendor) { return .printer }
        if has("xbox", "playstation", "nintendo") || vendor == "Nintendo" { return .console }
        if has("iphone", "ipad", "galaxy", "pixel", "android", "phone") { return .phone }
        if has("mac", "laptop", "desktop", "thinkpad", "-pc", "windows") { return .computer }
        if has("homepod", "audioaccessory", "sonos", "speaker", "echo", "nest mini", "home mini", "soundbar") || ["Sonos", "Bose"].contains(vendor) { return .speaker }
        if has("appletv", "apple tv", "tv", "chromecast", "roku", "bravia", "fire") || offers("_googlecast._tcp") || vendor == "Roku" { return .tv }
        if offers("_sonos._tcp", "_spotify-connect._tcp", "_raop._tcp") { return .speaker }
        if offers("_hap._tcp", "_homekit._tcp") || has("hue", "plug", "bulb", "thermostat", "camera", "doorbell", "esp")
            || ["Signify (Philips Hue)", "Espressif", "Ring", "Google Nest", "Tado"].contains(vendor) { return .smartHome }
        if has("nas", "diskstation", "synology", "qnap") || ["Synology", "QNAP"].contains(vendor) { return .nas }
        if ["Intel", "ASUS", "Microsoft", "Raspberry Pi"].contains(vendor) { return .computer }
        if ["TP-Link", "Ubiquiti", "Netgear", "AVM (Fritz!)"].contains(vendor) { return .router }
        if privateMAC { return .phone }
        return .unknown
    }

    // MARK: Makers (OUI = eerste 3 bytes van het MAC-adres). Beste poging, geen volledige lijst.

    nonisolated static let ouiTable: [UInt32: String] = {
        var t: [UInt32: String] = [:]
        for line in ouiText.split(separator: "\n") {
            let parts = line.split(separator: ":", maxSplits: 1)
            guard parts.count == 2 else { continue }
            for hex in parts[1].split(separator: " ") { if let v = UInt32(hex, radix: 16) { t[v] = String(parts[0]) } }
        }
        return t
    }()

    nonisolated static let ouiText = """
    Apple:000393 000A27 000A95 000D93 0010FA 001124 001451 0016CB 0017F2 0019E3 001B63 001CB3 001D4F 001E52 001EC2 001F5B 001FF3 0021E9 002241 002312 002332 00236C 0023DF 002436 002500 00254B 0025BC 002608 00264A 0026B0 0026BB 040CCE 041552 042665 04DB56 04E536 04F13E 04F7E4 086698 086D41 0C74C2 1040F3 109ADD 10DDB1 14109F 18AF61 1CABA7 20C9D0 24A074 28CFDA 28E02C 34159E 3C0754 406C8F 40A6D9 442A60 4860BC 542696 5855CA 5C5948 60334B 64B9E8 685B35 705681 70CD60 7831C1 78CA39 7C6D62 7CC3A1 80E650 843835 8863DF 8C5877 9027E4 98FE94 9C207B A4B197 A82066 AC87A3 B817C2 B8C75D BC52B7 C82A14 C8BCC8 D023DB D49A20 D83062 DC2B61 E0F847 E4CE8F F0B479 F41BA1 F81EDF ACBC32 F01898 A483E7 3C22FB 147DDA BCD074 F84D89 88665A 00C610
    Samsung:0000F0 0007AB 001247 001599 001632 001D25 002119 002339 002637 08D42B 3423BA 38AA3C 5C0A5B 78BDBC 8C7712 94350A A00BBA BC1485 C4576E E8508B F025B7 FCA13E 8425DB 90F1AA D022BE 508569 CC07AB
    Sonos:000E58 347E5C 48A6B8 542A1B 5CAAFD 7828CA 949F3E B8E937 F0F6C1 38420B
    Google:3C5AB4 546009 F4F5D8 F4F5E8 6CADF8 A47733 1CF29A D86C63 001A11 48D6D5 30FD38 E4F042
    Google Nest:18B430 641666
    Amazon:00FC8B 0C47C9 34D270 38F73D 40B4CD 44650D 50DCE7 6837E9 74C246 84D6D0 A002DC AC63BE F0272D FC65DE 6854FD 747548 B47C9C F0D2F1 0CEE99
    TP-Link:002719 14CC20 18A6F7 50C7BF 54C80F 60E327 647002 98DAC4 A42BB0 B04E26 C04A00 E8DE27 F4F26D EC086B 30B5C2 1C3BF3 5C628B AC84C6 98254A
    Ubiquiti:00156D 002722 0418D6 24A43C 44D9E7 687251 7483C2 788A20 802AA8 B4FBE4 DC9FDB F09FC2 FCECDA E063DA 18E829 245A4C 74ACB9 68D79A
    Netgear:00095B 000FB5 00146C 001B2F 001E2A 00223F 0024B2 0026F2 204E7F 28C68E 2CB05D 30469A 4494FC 6CB0CE 841B5E A040A0 A06391 C03F0E E0469A 9C3DCF B07FB9 08BD43
    AVM (Fritz!):00040E 00150C 001C4A 001F3F 0024FE 0896D7 246511 2C3AFD 2C91AB 3431C4 3810D5 3CA62F 444E6D 5C4979 7CFF4D 989BCB 9CC7A6 BC0543 C02506 C80E14 CCCE1E DC396F E0286D E8DF70 F0B014 74427F 485D35 3C3712 04B4FE B0F208
    Signify (Philips Hue):001788 ECB5FA
    Raspberry Pi:B827EB DCA632 E45F01 D83ADD 28CDC1 2CCF67
    Espressif:240AC4 246F28 2462AB 30AEA4 3C71BF 4C11AE 5CCF7F 600194 68C63A 807D3A 840D8E 84CCA8 84F3EB 8CAAB5 98F4AB A020A6 A4CF12 AC67B2 B4E62D BCDDC2 C44F33 C82B96 CC50E3 D8A01D DC4F22 ECFABC 18FE34 2CF432 483FDA 7C9EBD 94B97E 083AF2 40F520 E8DB84 349454 C8C9A3 10521C 3C6105 70039F A848FA 24A160
    Intel:001B21 001E67 00216A 0024D7 3CA9F4 5891CF 606720 7C5CF8 8C8D28 A088B4 ACED5C B49691 DC5360 E4B318 F81654 3413E8 40A3CC 4851B7 5C879C 94659C 0013E8 001500 0016EA 001CBF 001F3B 7CB27D
    Xiaomi:009EC8 04CF8C 0C1DAF 14F65A 185936 286C07 34CE00 50642B 584498 640980 64B473 742344 7811DC 7C1DD9 8CBEBE 98FAE3 9C99A0 A086C6 ACF7F3 B0E235 C46AB7 D4970B F0B429 F8A45F FC64BA 5448E6 047A0B
    Huawei:001882 001E10 00259E 00464B 00E0FC 04BD70 0819A6 104780 20F3A3 283152 30D17E 4846FB 548998 70723C 80FB06 8853D4 ACE215 C81479 E0247F F4C714 009ACD 48DB50 582AF7
    Sony:00014A 0013A9 001DBA 0024BE 045D4B 104FA8 3017C8 544249 584822 78843C AC9B0A FCF152 00041F 00D9D1 280DFC 709E29 BC60A7 F8461C 0CFE45 2CCC44 00E421 A8E3EE
    LG:001C62 001E75 001F6B 001FE3 0022A9 002483 0026E2 10683F 344DF7 388C50 58A2B5 64995D 785DC8 88C9D0 A816B2 A823FE C4366C CCFA00 E85B5B F80CF3 2C54CF 64BC0C 14C913 7440BE 00AA70
    HP:0001E6 000802 000F20 00110A 001438 0017A4 001A4B 001E0B 00215A 00237D 0025B3 101F74 1458D0 2C44FD 30E171 3CD92B 705A0F 80CE62 9457A5 9C8E99 A0D3C1 B4B686 C8D9D2 D4C9EF ECB1D7 F43909 FC15B4 645106 3863BB 10E7C6 84A93E 6C02E0
    Canon:000085 001E8F 00BBC1 180CAC 2C9EFC 60128B 74BFC0 888717 F48139 9C32CE
    Epson:000048 0026AB 44D244 64EB8C 9CAED3 A4EE57 AC1826 B0E892 DCCD2F E0BB9E F8D027 381A52 50579C 64C6D2
    Brother:001BA9 008077 30055C 3C2AF4 B42200
    Nintendo:0009BF 001656 0017AB 00191D 001AE9 001B7A 001CBE 001E35 001F32 002147 00224C 00241E 0024F3 0025A0 0403D6 2C10C1 34AF2C 40F407 58BDA3 5C521E 7CBB8A 8C56C5 98415C 98B6E9 9CE635 A438CC B8AE6E CC9E00 D86BF7 DC68EB E00C7F E84ECE 64B5C6 9458CB 606BFF
    Microsoft:0003FF 000D3A 00125A 00155D 0017FA 001DD8 002248 0025AE 0050F2 281878 3059B7 501AC5 5882A8 6045BD 7C1E52 985FD3 B4AE2B C83F26 DCB4C4 7CED8D 9CAA1B C49DED
    ASUS:000C6E 000EA6 00112F 0013D4 0015F2 001731 0018F3 001A92 001BFC 001D60 001E8C 002215 002354 00248C 002618 049226 08606E 107B44 10BF48 14DAE9 1C872C 2C56DC 305A3A 382C4A 40167E 50465D 5404A6 6045CB 704D7B 74D02B 88D7F6 90E6BA AC220B AC9E17 B06EBF BCEE7B C86000 D850E6 E03F49 F07959 F46D04 FC3497 04D9F5 244BFE A85E45
    Synology:001132 9009D0
    QNAP:00089B 245EBE
    Ring:343EA4 54E019 9C7613
    Roku:000D4B B0A737 B83E59 C83A6B CC6DA0 D83134 DC3A5E 080581 105932 20EFBD 84EAED AC3A7A 88DEA9 B0EE7B D04D2C
    Bose:000C8A 0452C7 08DF1F 2C41A1 4C875D 60ABD2 782B64 BC87FA
    """

    // MARK: Zelftest (Fusebox --selftest)

    nonisolated static func selfTest() {
        // OUI-tabel: geen dubbele prefixen over makers heen, en opzoeken werkt
        var seen = [String: String]()
        for line in ouiText.split(separator: "\n") {
            let parts = line.split(separator: ":", maxSplits: 1)
            for hex in parts[1].split(separator: " ") {
                precondition(hex.count == 6 && UInt32(hex, radix: 16) != nil, "oui \(hex)")
                precondition(seen.updateValue(String(parts[0]), forKey: String(hex)) == nil, "oui dubbel \(hex)")
            }
        }
        precondition(vendor([0xB8, 0x27, 0xEB, 1, 2, 3]) == "Raspberry Pi" && vendor([0x00, 0x17, 0x88, 0, 0, 0]) == "Signify (Philips Hue)", "oui")
        precondition(vendor([0x12, 0x34, 0x56, 0, 0, 0]) == nil, "oui onbekend")
        precondition(isPrivate([0xDA, 0, 0, 0, 0, 0]) && isPrivate([0x02, 0, 0, 0, 0, 0]) && !isPrivate([0xB8, 0x27, 0xEB, 0, 0, 0]), "lokaal beheerd")
        // Subnet
        let ip = parseIP("192.168.1.23")!
        precondition(ipString(ip) == "192.168.1.23" && parseIP("1.2.3") == nil && parseIP("1.2.3.256") == nil, "ip")
        let h24 = hosts(ip: ip, mask: 0xFFFF_FF00)
        precondition(h24.count == 254 && h24.first == parseIP("192.168.1.1") && h24.last == parseIP("192.168.1.254"), "/24")
        let h16 = hosts(ip: parseIP("10.0.5.9")!, mask: 0xFFFF_0000)
        precondition(h16.count == 254 && h16.first == parseIP("10.0.5.1"), "/16 → /24 rond eigen adres")
        precondition(hosts(ip: ip, mask: 0xFFFF_FFF0).count == 14 && hosts(ip: ip, mask: 0xFFFF_FFFF).isEmpty, "/28, /32")
        // ICMP
        let pkt = echoPacket(id: 0xBEEF, seq: 7)
        precondition(checksum(pkt) == 0, "icmp checksum")
        var reply = pkt; reply[0] = 0
        precondition(parseEchoReply(reply[...])! == (0xBEEF, 7), "echo zonder ip-kop")
        let withIP = [0x45] + [UInt8](repeating: 0, count: 19) + reply
        precondition(parseEchoReply(withIP[...])! == (0xBEEF, 7) && parseEchoReply(pkt[...]) == nil, "echo met ip-kop / request")
        // ARP: één volledige regel + één zonder MAC
        func msg(_ ipBytes: [UInt8], mac: [UInt8]) -> [UInt8] {
            let sin: [UInt8] = [16, UInt8(AF_INET), 0, 0] + ipBytes + [UInt8](repeating: 0, count: 8)
            let sdl: [UInt8] = [20, UInt8(AF_LINK), 4, 0, 6, 0, UInt8(mac.count), 0] + mac + [UInt8](repeating: 0, count: 12 - mac.count)
            var m = [UInt8](repeating: 0, count: MemoryLayout<rt_msghdr>.size) + sin + sdl
            m[0] = UInt8(m.count & 255); m[1] = UInt8(m.count >> 8)
            return m
        }
        let arp = parseARP(msg([192, 168, 1, 5], mac: [0xB8, 0x27, 0xEB, 1, 2, 3]) + msg([192, 168, 1, 6], mac: []))
        precondition(arp.count == 1 && arp[0].ip == parseIP("192.168.1.5") && arp[0].mac == [0xB8, 0x27, 0xEB, 1, 2, 3] && arp[0].ifindex == 4, "arp")
        // Namen, soort, oordeel
        precondition(friendlyName("A1B2C3D4E5F6@Living Room", type: "_raop._tcp", txt: [:]) == "Living Room", "raop-naam")
        precondition(friendlyName("Chromecast-abc", type: "_googlecast._tcp", txt: ["fn": "Bedroom TV"]) == "Bedroom TV", "cast-naam")
        precondition(cleanHostname("alex-iphone.fritz.box") == "alex-iphone" && cleanHostname("192.168.1.5") == nil, "hostnaam")
        precondition(guess(name: "Living Room", vendor: "Apple", services: ["_airplay._tcp", "_raop._tcp"], model: "AppleTV14,1", isRouter: false, isMe: false, privateMAC: false) == .tv, "apple tv")
        precondition(guess(name: "Alex's MacBook Pro", vendor: "Apple", services: ["_raop._tcp"], model: "Mac15,3", isRouter: false, isMe: false, privateMAC: false) == .computer, "mac")
        precondition(guess(name: "Unknown device", vendor: "Private address", services: [], model: nil, isRouter: false, isMe: false, privateMAC: true) == .phone, "privé")
        precondition(guess(name: "HL-L2350DW", vendor: "Brother", services: ["_ipp._tcp"], model: nil, isRouter: false, isMe: false, privateMAC: false) == .printer, "printer")
        precondition(verdict(router: 3, internet: 15, internetCheck: true).ok == true, "alles goed")
        precondition(verdict(router: 3, internet: 300, internetCheck: true).text.contains("provider"), "provider")
        precondition(verdict(router: 80, internet: 300, internetCheck: true).text.hasPrefix("Both slow"), "beide traag")
        print("netwerk: ok (\(ouiTable.count) OUI-prefixen)")
    }
}

// MARK: - Onthouden apparaten (~/Documents/Fusebox/network-devices.json)

struct NetDeviceStore: Codable {
    struct Known: Codable { var name: String?; var first: Date; var last: Date }
    var created = Date()
    var devices: [String: Known] = [:]   // MAC "AA:BB:…" → naam, eerst/laatst gezien

    static var url: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Fusebox/network-devices.json")
    }
    static func load() -> NetDeviceStore {
        let dec = JSONDecoder(); dec.dateDecodingStrategy = .iso8601
        return (try? Data(contentsOf: url)).flatMap { try? dec.decode(NetDeviceStore.self, from: $0) } ?? NetDeviceStore()
    }
    func save() {
        let enc = JSONEncoder(); enc.dateEncodingStrategy = .iso8601; enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? FileManager.default.createDirectory(at: Self.url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? enc.encode(self).write(to: Self.url, options: .atomic)
    }
}
