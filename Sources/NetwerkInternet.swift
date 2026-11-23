import SwiftUI
import CoreWLAN
import SystemConfiguration
import Darwin
import Network
import os
import UniformTypeIdentifiers

// MARK: - Internet: verbinding, Wi-Fi, kwaliteit (pings), live grafiek, snelheidstest, publiek IP
//
// Draait alleen zolang het Netwerk-tabblad open is. Pings gaan naar de router en `pingHost` ("" = internetdeel uit).
// Cloudflare (snelheidstest, publiek IP) alleen na een klik.

struct NetConnection {
    let iface: String, kind: String
    let ipv4: String?, ipv6: [String], router: UInt32?, dns: [String], ipv6Internet: Bool
    var vpn = false   // VPN aan: als hoofdroute (kind "VPN") of naast Wi-Fi/kabel (alleen sommige verkeer)
    var name: String? = nil       // naam van macOS, bv. "USB 10/100/1000 LAN" of "Thunderbolt Ethernet"
    var linkMbit: Double? = nil   // kabelsnelheid naar switch/router (if_data), nil = onbekend

    static func current() -> NetConnection? {
        let v4 = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv4" as CFString) as? [String: Any]
        let v6 = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv6" as CFString) as? [String: Any]
        let dns = SCDynamicStoreCopyValue(nil, "State:/Network/Global/DNS" as CFString) as? [String: Any]
        guard let iface = v4?["PrimaryInterface"] as? String ?? v6?["PrimaryInterface"] as? String else { return nil }
        var ipv4: String?, ipv6: [String] = [], baud: UInt32 = 0
        var list: UnsafeMutablePointer<ifaddrs>?
        if getifaddrs(&list) == 0 {
            var p = list
            while let a = p?.pointee {
                defer { p = a.ifa_next }
                guard String(cString: a.ifa_name) == iface, let sa = a.ifa_addr else { continue }
                if sa.pointee.sa_family == UInt8(AF_LINK), let d = a.ifa_data {
                    baud = d.assumingMemoryBound(to: if_data.self).pointee.ifi_baudrate
                } else if sa.pointee.sa_family == UInt8(AF_INET) {
                    ipv4 = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { ipString(UInt32(bigEndian: $0.pointee.sin_addr.s_addr)) }
                } else if sa.pointee.sa_family == UInt8(AF_INET6) {
                    var addr = sa.withMemoryRebound(to: sockaddr_in6.self, capacity: 1) { $0.pointee.sin6_addr }
                    let first = withUnsafeBytes(of: &addr) { $0[0] }
                    guard first & 0xE0 == 0x20 else { continue }   // alleen wereldwijde adressen (2000::/3)
                    var buf = [CChar](repeating: 0, count: Int(INET6_ADDRSTRLEN))
                    if inet_ntop(AF_INET6, &addr, &buf, socklen_t(buf.count)) != nil { ipv6.append(String(cString: buf)) }
                }
            }
            freeifaddrs(list)
        }
        let wifi = CWWiFiClient.shared().interfaceNames()?.contains(iface) == true
        // Diensten met een adres (VPN-apps melden hun utun hier, ook als Wi-Fi de hoofdroute blijft)
        let services = (SCDynamicStoreCopyKeyList(nil, "State:/Network/Service/.*/IPv4" as CFString) as? [String] ?? [])
            .compactMap { (SCDynamicStoreCopyValue(nil, $0 as CFString) as? [String: Any])?["InterfaceName"] as? String }
        let name = (SCNetworkInterfaceCopyAll() as? [SCNetworkInterface])?
            .first { SCNetworkInterfaceGetBSDName($0) as String? == iface }.flatMap { SCNetworkInterfaceGetLocalizedDisplayName($0) as String? }
        return NetConnection(iface: iface, kind: isVPN(iface) ? "VPN" : wifi ? "Wi-Fi" : iface.hasPrefix("en") ? "Ethernet" : iface,
                             ipv4: ipv4, ipv6: ipv6, router: (v4?["Router"] as? String).flatMap(parseIP),
                             dns: dns?["ServerAddresses"] as? [String] ?? [],
                             ipv6Internet: !ipv6.isEmpty && v6?["Router"] != nil, vpn: vpnOn(primary: iface, services: services),
                             name: name, linkMbit: !wifi && baud > 0 ? Double(baud) / 1e6 : nil)
    }

    /// Tunnel-interfaces van VPN's (WireGuard/IKEv2-apps: utun, oude IPsec: ipsec, L2TP/PPTP: ppp). macOS heeft zelf ook
    /// altijd een paar utun's (iCloud, Continuïteit), maar die hebben geen dienst met een adres: daarom alleen hoofdroute of dienst.
    static func isVPN(_ iface: String) -> Bool { ["utun", "ipsec", "ppp"].contains { iface.hasPrefix($0) } }
    static func vpnOn(primary: String, services: [String]) -> Bool { isVPN(primary) || services.contains(where: isVPN) }

    static func selfTest() {
        precondition(isVPN("utun4") && isVPN("ipsec0") && isVPN("ppp0") && !isVPN("en0") && !isVPN("bridge0"), "vpn-interface")
        precondition(vpnOn(primary: "utun4", services: []) && vpnOn(primary: "en0", services: ["en0", "utun6"]), "vpn aan")
        precondition(!vpnOn(primary: "en0", services: ["en0", "en7"]), "geen vpn")
        print("vpn herkennen: ok")
    }
}

struct WifiDetail {
    let rssi: Int, noise: Int, rate: Double
    let channel: Int?, band: String?, width: String?, phy: String?
    var snr: Int? { rssi < 0 && noise < 0 ? rssi - noise : nil }
    var word: String { rssi >= -60 ? L("good") : rssi >= -70 ? L("fair") : L("weak") }

    static func current(_ iface: String) -> WifiDetail? {
        guard let w = CWWiFiClient.shared().interface(withName: iface), w.rssiValue() != 0 else { return nil }
        let ch = w.wlanChannel()   // kan nil zijn zonder locatietoestemming: dan weglaten
        let band: String? = switch ch?.channelBand.rawValue { case 1: "2.4 GHz"; case 2: "5 GHz"; case 3: "6 GHz"; default: nil }
        let width: String? = switch ch?.channelWidth.rawValue { case 1: "20 MHz"; case 2: "40 MHz"; case 3: "80 MHz"; case 4: "160 MHz"; case 5: "320 MHz"; default: nil }
        let phy: String? = switch w.activePHYMode().rawValue {
        case 1: "802.11a"; case 2: "802.11b"; case 3: "802.11g"; case 4: "Wi-Fi 4 (802.11n)"
        case 5: "Wi-Fi 5 (802.11ac)"; case 6: "Wi-Fi 6 (802.11ax)"; case 7: "Wi-Fi 7 (802.11be)"; default: nil
        }
        return WifiDetail(rssi: w.rssiValue(), noise: w.noiseMeasurement(), rate: w.transmitRate(),
                          channel: ch.map(\.channelNumber), band: band, width: width, phy: phy)
    }
}

/// Snel of Volledig. Oude metingen (één stroom, vaste bytes) hebben geen soort.
enum SpeedKind: String, Codable {
    case quick, full
    var streams: Int { self == .quick ? 4 : 6 }
    var secs: Double { self == .quick ? 5 : 15 }               // per richting
    var skip: Int { self == .quick ? 2 : 3 }                     // aanloop (s) die niet meetelt
    var cap: Int64 { self == .quick ? 600_000_000 : 2_000_000_000 }   // max bytes per richting
    var title: String { self == .quick ? L("Quick") : L("Full") }
}

struct SpeedResult: Codable, Equatable {
    let date: Date, down: Double, up: Double, ping: Double?   // Mbit/s, ms
    // Nieuw (optioneel, oude opslag blijft leesbaar)
    var kind: SpeedKind? = nil
    var downPeak: Double? = nil, upPeak: Double? = nil
    var loadedPing: Double? = nil      // ms tijdens de overdracht (slechtste richting)
    var variation: Double? = nil       // spreiding per seconde, 0.1 = ±10% (slechtste richting)
}

/// Rekenwerk van de snelheidstest, los van het netwerk (zelftest op verzonnen data).
enum SpeedMath {
    /// Per-seconde-metingen zonder de aanloop; houdt er altijd minstens één over.
    static func trimmed(_ s: [Double], skip: Int) -> [Double] { Array(s.dropFirst(min(skip, max(s.count - 1, 0)))) }

    static func median(_ v: [Double]) -> Double? {
        guard !v.isEmpty else { return nil }
        let s = v.sorted(), m = s.count / 2
        return s.count % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2
    }

    static func mean(_ v: [Double]) -> Double? { v.isEmpty ? nil : v.reduce(0, +) / Double(v.count) }

    /// Variatiecoëfficiënt (standaardafwijking / gemiddelde).
    static func variation(_ v: [Double]) -> Double? {
        guard v.count > 1, let m = mean(v), m > 0 else { return nil }
        return (v.map { ($0 - m) * ($0 - m) }.reduce(0, +) / Double(v.count)).squareRoot() / m
    }

    static func stabilityText(_ cv: Double) -> String {
        let pct = "±\(Int((cv * 100).rounded()))%"
        return cv < 0.1 ? L("steady (\(pct))") : cv < 0.25 ? L("varies a little (\(pct))") : L("unsteady (\(pct))")
    }

    /// Vertraging onder belasting ("bufferbloat") in gewone woorden. ok: true goed, false probleem, nil let op.
    static func loadVerdict(idle: Double, loaded: Double) -> (text: String, ok: Bool?) {
        let extra = max(0, loaded - idle), ms = "+\(Int(extra.rounded())) ms"
        if extra < 30 { return (L("Delay barely rises while the line is busy (\(ms)) → fine for calls and games"), true) }
        if extra < 100 { return (L("Delay rises while the line is busy (\(ms)) → calls may stutter during big downloads"), nil) }
        return (L("Delay rises a lot while the line is busy (\(ms), \"bufferbloat\") → calls and games suffer during downloads; a router with smart queueing (SQM) helps"), false)
    }

    /// Geschat dataverbruik in bytes op basis van de vorige uitslag; nil = onbekend (dan geldt het plafond).
    static func estimate(_ kind: SpeedKind, last: SpeedResult?) -> Int64? {
        guard let last else { return nil }
        func dir(_ mbit: Double) -> Int64 { min(kind.cap, Int64(mbit * 1.2 * 1e6 / 8 * kind.secs)) }   // 20% marge: oude metingen lazen laag
        return dir(max(last.down, last.downPeak ?? 0)) + dir(max(last.up, last.upPeak ?? 0))
    }

    static func selfTest() {
        precondition(trimmed([1, 2, 3, 4, 5], skip: 2) == [3, 4, 5] && trimmed([7], skip: 3) == [7] && trimmed([], skip: 2).isEmpty, "aanloop")
        precondition(median([5, 1, 3]) == 3 && median([4, 1, 3, 2]) == 2.5 && median([]) == nil, "mediaan")
        precondition(variation([100, 100, 100]) == 0 && variation([5]) == nil, "spreiding vlak")
        precondition(abs(variation([90, 110])! - 0.1) < 1e-9, "spreiding ±10%")
        // Verzonnen meting: traag begin (aanloop), daarna rond 500 met één piek
        let run = trimmed([40, 220, 480, 510, 495, 620, 505], skip: 2)
        precondition(median(run) == 505 && run.max() == 620 && mean(run)! > 480, "uitslag")
        precondition(stabilityText(0.04).hasPrefix("steady") && stabilityText(0.3).hasPrefix("unsteady"), "stabiliteit")
        precondition(loadVerdict(idle: 12, loaded: 20).ok == true && loadVerdict(idle: 12, loaded: 70).ok == nil, "bufferbloat licht")
        let bad = loadVerdict(idle: 12, loaded: 300)
        precondition(bad.ok == false && bad.text.contains("+288 ms") && bad.text.contains("bufferbloat"), "bufferbloat: \(bad.text)")
        let last = SpeedResult(date: Date(), down: 100, up: 20, ping: 10)
        precondition(estimate(.quick, last: last) == 75_000_000 + 15_000_000 && estimate(.full, last: nil) == nil, "schatting")
        precondition(estimate(.quick, last: SpeedResult(date: Date(), down: 10_000, up: 10_000, ping: nil)) == 1_200_000_000, "plafond")
        // Oude opslag (zonder soort) blijft leesbaar
        let old = try? JSONDecoder().decode([SpeedResult].self, from: Data(#"[{"date":700000000,"down":90.5,"up":20,"ping":11}]"#.utf8))
        precondition(old?.first?.down == 90.5 && old?.first?.kind == nil, "oude geschiedenis")
        let full = SpeedResult(date: Date(timeIntervalSinceReferenceDate: 1), down: 1, up: 1, ping: nil, kind: .full, loadedPing: 40)
        precondition((try? JSONDecoder().decode(SpeedResult.self, from: JSONEncoder().encode(full))) == full, "nieuwe velden")
    }
}

/// Telt de bytes van alle stromen van één richting. Callbacks komen binnen op achtergrondwachtrijen (niets per stukje op main).
final class SpeedMeter: NSObject, URLSessionDataDelegate, @unchecked Sendable {
    static let upChunk = Data(count: 10_000_000)
    static let downChunk = 50_000_000   // Cloudflare weigert vanaf 100 MB per verzoek
    let upload: Bool, cap: Int64, deadline: Date
    private let lock = NSLock()
    private var total: Int64 = 0, stopped = false, alive = 0
    private var lastError: Error?

    init(upload: Bool, cap: Int64, secs: Double) {
        self.upload = upload; self.cap = cap; deadline = Date().addingTimeInterval(secs)
    }

    var bytes: Int64 { lock.withLock { total } }
    var finished: Bool { lock.withLock { stopped || alive == 0 } }
    var error: Error? { lock.withLock { lastError } }
    func stop() { lock.withLock { stopped = true } }
    func shutdown(_ sessions: [URLSession]) { lock.withLock { stopped = true; sessions.forEach { $0.invalidateAndCancel() } } }

    /// Eén stroom = één eigen sessie (eigen TCP-verbinding; HTTP/2 zou alles over één verbinding sturen).
    func start(_ s: URLSession) {
        lock.withLock { alive += 1 }
        if !next(s) { lock.withLock { alive -= 1 } }
    }

    /// Onder het slot: zo kan `shutdown` nooit tussen de controle en het nieuwe verzoek vallen (taak op een opgeheven sessie = crash).
    private func next(_ s: URLSession) -> Bool {
        lock.withLock {
            guard !stopped, total < cap, Date() < deadline else { return false }
            if upload {
                var req = URLRequest(url: URL(string: "https://speed.cloudflare.com/__up")!)
                req.httpMethod = "POST"
                req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                s.uploadTask(with: req, from: Self.upChunk).resume()
            } else {
                s.dataTask(with: URL(string: "https://speed.cloudflare.com/__down?bytes=\(Self.downChunk)")!).resume()
            }
            return true
        }
    }

    private func add(_ n: Int64, _ task: URLSessionTask) {
        let over = lock.withLock { total += n; return total >= cap }
        if over { stop(); task.cancel() }
    }

    func urlSession(_ s: URLSession, dataTask: URLSessionDataTask, didReceive data: Data) { add(Int64(data.count), dataTask) }
    func urlSession(_ s: URLSession, task: URLSessionTask, didSendBodyData sent: Int64, totalBytesSent: Int64, totalBytesExpectedToSend: Int64) { add(sent, task) }
    func urlSession(_ s: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        let status = (task.response as? HTTPURLResponse)?.statusCode
        if error == nil, status == 200, next(s) { return }   // volgende stuk op dezelfde verbinding
        lock.withLock {
            alive -= 1
            if !stopped, Date() < deadline, (error as? URLError)?.code != .cancelled {
                lastError = error ?? URLError(.badServerResponse)   // fout of geweigerd: deze stroom stopt
            }
        }
    }
}

struct PublicIP { let ip: String, isp: String?, place: String? }

@MainActor
final class InternetStats: ObservableObject {
    static let graphPoints = 60
    @Published private(set) var conn: NetConnection?
    @Published private(set) var wifi: WifiDetail?
    @Published private(set) var metered: Bool?   // NWPathMonitor: duur (hotspot/mobiel) of Weinig-data-modus
    @Published private(set) var router: PingStats?
    @Published private(set) var internet: PingStats?
    @Published private(set) var internetViaTCP = false
    @Published private(set) var dnsMs: Double?
    @Published private(set) var testing = false
    @Published private(set) var liveRouter: [Double] = []
    @Published private(set) var liveInternet: [Double] = []
    @Published private(set) var speedPhase: String?
    @Published private(set) var speedError: String?
    @Published private(set) var speedLive: Double?        // Mbit/s, laatste seconde
    @Published private(set) var speedProgress = 0.0       // 0…1
    @Published private(set) var speedConfirm: SpeedKind?  // dure verbinding: wacht op ja
    @Published private(set) var history: [SpeedResult] = InternetStats.loadHistory()
    @Published private(set) var publicIP: PublicIP?
    @Published private(set) var publicIPState: String?
    private var task: Task<Void, Never>?, speedTask: Task<Void, Never>?, ipTask: Task<Void, Never>?
    private var target: UInt32?

    var host: String { UserDefaults.standard.string(forKey: "pingHost") ?? "" }

    /// Kwaliteitstest (10 pings) en daarna elke ~2 s een ping naar router en internet voor de grafiek.
    func start() {
        task?.cancel()
        conn = NetConnection.current()
        wifi = conn.flatMap { $0.kind == "Wi-Fi" ? WifiDetail.current($0.iface) : nil }
        Task { metered = await Self.isMetered() }
        guard let conn else { return }
        let host = host, routerIP = conn.router
        testing = true
        task = Task { [weak self] in
            async let dns: Double? = host.isEmpty ? nil : NetScanner.offMain { NetProbe.resolve("apple.com").ms }
            let target: UInt32? = host.isEmpty ? nil : await NetScanner.offMain { NetProbe.resolve(host).ip }
            let ips = [routerIP, target].compactMap { $0 }
            let r = await NetScanner.offMain { NetProbe.ping(ips, rounds: 10, every: 0.2, linger: 1.0) }
            var inet = target.map { PingStats(r.rtt[$0] ?? [], sent: 10) }
            var viaTCP = false
            if let target, inet?.got == 0 {   // ICMP geblokkeerd? Dan TCP-verbinding naar 443
                let tcp = await NetScanner.offMain { (0..<10).compactMap { _ in NetProbe.tcpProbe([(target, 443)], timeout: 1)[0]?.ms } }
                if !tcp.isEmpty { inet = PingStats(tcp, sent: 10); viaTCP = true }
            }
            let dnsTime = await dns
            guard !Task.isCancelled, let self else { return }
            self.target = target
            self.router = routerIP.map { PingStats(r.rtt[$0] ?? [], sent: 10) }
            self.internet = inet
            self.internetViaTCP = viaTCP
            self.dnsMs = dnsTime
            self.testing = false
            while !Task.isCancelled {
                try? await Task.sleep(for: .seconds(1))
                await self.liveTick(router: routerIP, target: target, viaTCP: viaTCP)
            }
        }
    }

    private func liveTick(router: UInt32?, target: UInt32?, viaTCP: Bool) async {
        if let c = conn, c.kind == "Wi-Fi" { wifi = WifiDetail.current(c.iface) }
        let icmp = [router, viaTCP ? nil : target].compactMap { $0 }
        async let r = NetScanner.offMain { NetProbe.ping(icmp, rounds: 1, every: 0, linger: 1.0) }
        async let t: Double? = viaTCP && target != nil ? NetScanner.offMain { NetProbe.tcpProbe([(target!, 443)], timeout: 1)[0]?.ms } : nil
        let (res, tcp) = await (r, t)
        guard !Task.isCancelled else { return }
        func push(_ list: inout [Double], _ v: Double?) {
            list.append(v ?? list.last ?? 0)   // gemiste ping: lijn blijft vlak (verlies staat in de cijfers)
            if list.count > Self.graphPoints { list.removeFirst(list.count - Self.graphPoints) }
        }
        if let router { push(&liveRouter, res.rtt[router]?.first) }
        if let target { push(&liveInternet, viaTCP ? tcp : res.rtt[target]?.first) }
    }

    func stop() {
        task?.cancel(); task = nil
        speedTask?.cancel(); speedTask = nil
        ipTask?.cancel(); ipTask = nil
        findTask?.cancel(); findTask = nil; findBusy = false
        testing = false; speedPhase = nil; speedLive = nil; speedConfirm = nil; publicIPState = nil
    }

    // MARK: Snelheidstest (Cloudflare, alleen na een klik)

    /// Eerst kijken of de verbinding duur is (mobiel, iPhone-hotspot, Weinig-data-modus): dan eerst vragen.
    func requestSpeedTest(_ kind: SpeedKind) {
        guard speedTask == nil, speedConfirm == nil else { return }
        speedError = nil
        speedTask = Task { [weak self] in
            let metered = await Self.isMetered()
            guard let self, !Task.isCancelled else { return }
            self.speedTask = nil
            if metered { self.speedConfirm = kind } else { self.runSpeedTest(kind) }
        }
    }

    func dismissSpeedConfirm() { speedConfirm = nil }

    func runSpeedTest(_ kind: SpeedKind) {
        guard speedTask == nil else { return }
        speedConfirm = nil; speedError = nil; speedLive = nil; speedProgress = 0
        speedPhase = L("Measuring delay…")
        speedTask = Task { [weak self] in
            let probe = Self.speedSession(timeout: 5)
            defer { probe.invalidateAndCancel() }
            do {
                // Vertraging in rust: mediaan van 5 lege verzoeken (het eerste opent de verbinding en telt niet mee)
                var idle: [Double] = []
                for i in 0..<6 { let ms = try await Self.ping(probe); if i > 0 { idle.append(ms) } }
                guard let down = try await self?.transfer(kind, upload: false, probe: probe),
                      let up = try await self?.transfer(kind, upload: true, probe: probe), let self else { return }
                let d = SpeedMath.trimmed(down.samples, skip: kind.skip), u = SpeedMath.trimmed(up.samples, skip: kind.skip)
                let full = kind == .full
                var r = SpeedResult(date: Date(), down: (full ? SpeedMath.median(d) : SpeedMath.mean(d)) ?? 0,
                                    up: (full ? SpeedMath.median(u) : SpeedMath.mean(u)) ?? 0, ping: SpeedMath.median(idle), kind: kind)
                if full {
                    r.downPeak = d.max(); r.upPeak = u.max()
                    r.loadedPing = [SpeedMath.median(down.pings), SpeedMath.median(up.pings)].compactMap { $0 }.max()
                    r.variation = [SpeedMath.variation(d), SpeedMath.variation(u)].compactMap { $0 }.max()
                }
                self.history = Array(([r] + self.history).prefix(10))
                if let data = try? JSONEncoder().encode(self.history) { UserDefaults.standard.set(data, forKey: "speedHistory") }
            } catch {
                if !Task.isCancelled { self?.speedError = L("Speed test failed: \(error.localizedDescription)") }
            }
            if !Task.isCancelled { self?.speedPhase = nil; self?.speedLive = nil; self?.speedTask = nil }   // bij annuleren al opgeruimd
        }
    }

    /// Eén richting: `kind.streams` verbindingen tegelijk, op tijd (niet op bytes), tot het plafond.
    /// Meet elke seconde de doorvoer; bij Volledig ook de vertraging tijdens de overdracht (na de aanloop).
    private func transfer(_ kind: SpeedKind, upload: Bool, probe: URLSession) async throws -> (samples: [Double], pings: [Double]) {
        let meter = SpeedMeter(upload: upload, cap: kind.cap, secs: kind.secs)
        let sessions = (0..<kind.streams).map { _ in Self.speedSession(timeout: 15, delegate: meter) }
        let pinger: Task<[Double], Never>? = kind == .full ? Task {
            var out: [Double] = []
            try? await Task.sleep(for: .seconds(kind.skip))
            while !Task.isCancelled {
                if let ms = try? await Self.ping(probe) { out.append(ms) }
                try? await Task.sleep(for: .milliseconds(400))
            }
            return out
        } : nil
        defer { pinger?.cancel(); meter.shutdown(sessions) }
        sessions.forEach(meter.start)
        let start = Date(), name = upload ? L("Upload") : L("Download")
        var samples: [Double] = [], lastBytes: Int64 = 0, lastT = start
        while true {
            try await Task.sleep(for: .milliseconds(250))   // vaak kijken (plafond, einde), per seconde meten
            let now = Date(), dt = now.timeIntervalSince(lastT)
            let done = meter.finished || now >= meter.deadline
            if dt >= 1 || (done && dt >= 0.3) {
                let b = meter.bytes
                samples.append(Double(b - lastBytes) * 8 / dt / 1e6)
                lastBytes = b; lastT = now
                speedLive = samples.last
            }
            speedProgress = (upload ? 0.5 : 0) + 0.5 * min(1, now.timeIntervalSince(start) / kind.secs)
            speedPhase = L("\(kind.title) · \(name) · \(kind.streams) connections · \(max(0, Int(meter.deadline.timeIntervalSince(now).rounded(.up)))) s left")
            if done { break }
        }
        meter.shutdown(sessions)
        pinger?.cancel()
        let pings = await pinger?.value ?? []
        if samples.isEmpty || meter.bytes < 1_000_000 { throw meter.error ?? URLError(.badServerResponse) }
        return (samples, pings)
    }

    nonisolated private static func speedSession(timeout: Double, delegate: URLSessionDelegate? = nil) -> URLSession {
        let cfg = URLSessionConfiguration.ephemeral
        cfg.timeoutIntervalForRequest = timeout
        cfg.waitsForConnectivity = false
        cfg.urlCache = nil
        cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
        return URLSession(configuration: cfg, delegate: delegate, delegateQueue: nil)
    }

    /// Eén leeg verzoek, in ms.
    nonisolated private static func ping(_ s: URLSession) async throws -> Double {
        let t0 = Date()
        _ = try await s.data(from: URL(string: "https://speed.cloudflare.com/__down?bytes=0")!)
        return -t0.timeIntervalSinceNow * 1000
    }

    /// Dure verbinding: mobiel netwerk, iPhone-hotspot of Weinig-data-modus.
    nonisolated static func isMetered() async -> Bool {
        await withCheckedContinuation { c in
            let m = NWPathMonitor(), once = OSAllocatedUnfairLock(initialState: false)
            m.pathUpdateHandler = { p in
                guard !once.withLock({ let was = $0; $0 = true; return was }) else { return }
                m.cancel()
                c.resume(returning: p.isExpensive || p.isConstrained)
            }
            m.start(queue: .global(qos: .userInitiated))
        }
    }

    /// Alle bewaarde snelheidstests weg: snelste en gemiddelde beginnen opnieuw.
    func clearHistory() {
        history = []
        UserDefaults.standard.removeObject(forKey: "speedHistory")
    }

    /// Momentopname voor het PDF-rapport.
    func report(internetCheck: Bool) -> NetReport {
        NetReport(kind: conn?.kind, wifi: wifi, router: router, internet: internetCheck ? internet : nil, viaTCP: internetViaTCP,
                  dnsMs: internetCheck ? dnsMs : nil, finding: finding, history: history)
    }

    func cancelSpeedTest() {
        speedTask?.cancel(); speedTask = nil
        speedPhase = nil; speedLive = nil
    }

    /// Alleen opnieuw decoderen als de opgeslagen data veranderd is (wordt bij elke meting gelezen, ook buiten de main actor).
    nonisolated(unsafe) private static var decoded: (data: Data, list: [SpeedResult])?
    nonisolated static func loadHistory() -> [SpeedResult] {
        guard let data = UserDefaults.standard.data(forKey: "speedHistory") else { return [] }
        if decoded?.data != data { decoded = (data, (try? JSONDecoder().decode([SpeedResult].self, from: data)) ?? []) }
        return decoded?.list ?? []
    }

    // MARK: Publiek IP (Cloudflare, alleen na een klik; niet opgeslagen)

    func loadPublicIP() {
        guard ipTask == nil else { return }
        publicIPState = L("Asking Cloudflare…")
        ipTask = Task { [weak self] in
            let cfg = URLSessionConfiguration.ephemeral
            cfg.timeoutIntervalForRequest = 8
            let session = URLSession(configuration: cfg)
            defer { session.invalidateAndCancel() }
            let result = try? await session.data(from: URL(string: "https://speed.cloudflare.com/meta")!)
            guard let self, !Task.isCancelled else { return }
            self.ipTask = nil
            guard let data = result?.0, data.count < 100_000, let ip = Self.parseMeta(data) else {
                self.publicIPState = L("Couldn't reach Cloudflare"); return
            }
            self.publicIP = ip
            self.publicIPState = nil
        }
    }

    nonisolated static func parseMeta(_ data: Data) -> PublicIP? {
        guard let j = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let ip = j["clientIp"] as? String, ip.count < 64 else { return nil }
        let place = [j["city"] as? String, j["country"] as? String].compactMap { $0 }.joined(separator: ", ")
        let colo = (j["colo"] as? String).map { " (Cloudflare \($0))" } ?? ""
        return PublicIP(ip: ip, isp: j["asOrganization"] as? String, place: place.isEmpty ? nil : place + colo)
    }

    // MARK: Oordeel in gewone woorden

    /// ok: true = goed, false = probleem, nil = let op.
    nonisolated static func verdict(wired: Bool, rssi: Int?, snr: Int?, router: PingStats?, internet: PingStats?,
                                    dnsMs: Double?, internetCheck: Bool) -> (text: String, ok: Bool?) {
        func pct(_ v: Double) -> String { "\(Int((v * 100).rounded()))%" }
        func ms(_ v: Double?) -> String { v.map { $0 < 1 ? "<1 ms" : "\(Int($0.rounded())) ms" } ?? "–" }
        let weak = !wired && ((rssi.map { $0 < -70 } ?? false) || (snr.map { $0 < 20 } ?? false))
        let rDead = router.map { $0.got == 0 } ?? false
        let iDead = internetCheck && (internet.map { $0.got == 0 } ?? false)
        if rDead && iDead { return (L("No answer from the router or the internet → check the connection"), false) }
        if iDead { return (L("The router answers but the internet doesn't → problem at your provider or modem"), false) }
        let r = rDead ? nil : router, i = internetCheck ? internet : nil
        // Wi-Fi schommelt van zichzelf (stroombesparing van de chip): pas boven 40 ms schommeling iets zeggen, op een kabel boven 20
        let rJitter = wired ? 20.0 : 40.0
        let rBad = r.map { $0.loss >= 0.02 || ($0.avg ?? 0) > 30 || ($0.jitter ?? 0) > rJitter } ?? false
        let iBad = i.map { $0.loss >= 0.02 || ($0.avg ?? 0) > 100 || ($0.jitter ?? 0) > 30 } ?? false
        var parts: [String] = []
        if let r, rBad {
            let what = r.loss >= 0.02 ? L("\(pct(r.loss)) packet loss to the router")
                : (r.avg ?? 0) > 30 ? L("slow replies from the router (\(ms(r.avg)))") : L("unsteady replies from the router (jitter \(ms(r.jitter)))")
            let What = what.prefix(1).uppercased() + what.dropFirst()
            parts.append(weak ? L("Wi-Fi signal weak and \(what) → move closer to the router or use a cable")
                         : wired ? L("\(What) on a cable → check the cable or restart the router")
                         : L("\(What) → Wi-Fi interference or a busy router; restart it or use a cable"))
        } else if let i, iBad {
            let what = i.loss >= 0.02 ? L("\(pct(i.loss)) packet loss") : (i.jitter ?? 0) > 30 ? L("unsteady replies (jitter \(ms(i.jitter)))") : L("slow replies (\(ms(i.avg)))")
            parts.append(L("Your own network is fine, but the internet has \(what) → most likely your provider"))
        } else if weak {
            parts.append(L("Wi-Fi signal is weak (\(rssi.map { "\($0) dBm" } ?? L("low"))) but the connection works for now; moving closer helps"))
        }
        if internetCheck, let dnsMs, dnsMs > 150 { parts.append(L("Name lookups are slow (\(ms(dnsMs))) → try another DNS server")) }
        if parts.isEmpty {
            var good = L("All good: ") + (r.map { L("router \(ms($0.avg))") } ?? L("router doesn't answer pings"))
            if let i { good += L(", internet \(ms(i.avg))") } else if !internetCheck { good += L(" (internet check is off in Settings)") }
            return (good + L(", no packet loss"), true)
        }
        return (parts.joined(separator: ". "), rBad || iBad ? false : nil)
    }

    // MARK: Probleem zoeken: Wi-Fi, router, DNS of internet? (alleen na een klik, ~6 s)

    struct Finding: Equatable { let text: String; let ok: Bool?; let numbers: String }
    @Published private(set) var finding: Finding?
    @Published private(set) var findBusy = false
    private var findTask: Task<Void, Never>?

    /// 20 pings naar router en 1.1.1.1 (TCP 443 als ICMP geblokkeerd is), tegelijk 3 verse DNS-opzoekingen, plus het Wi-Fi-signaal.
    func findProblem() {
        guard findTask == nil else { return }
        finding = nil
        guard let c = NetConnection.current() else {
            finding = Finding(text: L("Not connected to a network: turn on Wi-Fi or plug in a cable"), ok: false, numbers: ""); return
        }
        findBusy = true
        findTask = Task { [weak self] in
            let w = c.kind == "Wi-Fi" ? WifiDetail.current(c.iface) : nil
            let inetIP: UInt32 = 0x0101_0101   // 1.1.1.1
            async let dns = NetScanner.offMain { Self.freshDNS() }
            let r = await NetScanner.offMain { NetProbe.ping([c.router, inetIP].compactMap { $0 }, rounds: 20, every: 0.25, linger: 1.0) }
            var inet = PingStats(r.rtt[inetIP] ?? [], sent: 20)
            if inet.got == 0 {
                let tcp = await NetScanner.offMain { (0..<5).compactMap { _ in NetProbe.tcpProbe([(inetIP, 443)], timeout: 1)[0]?.ms } }
                if !tcp.isEmpty { inet = PingStats(tcp, sent: 5) }
            }
            let d = await dns
            guard let self, !Task.isCancelled else { return }
            self.finding = Self.find(wired: c.kind != "Wi-Fi", rssi: w?.rssi, snr: w?.snr, band: w?.band, channel: w?.channel,
                                     router: c.router.map { PingStats(r.rtt[$0] ?? [], sent: 20) }, internet: inet, dnsMs: d)
            self.findBusy = false
            self.findTask = nil
        }
    }

    /// Mediaan van 3 opzoekingen van willekeurige namen (staan nergens in een cache, dus de DNS-server moet echt antwoorden). nil = geen antwoord.
    nonisolated static func freshDNS() -> Double? {
        let names = ["apple.com", "cloudflare.com", "google.com"].map { "fb-\(UInt32.random(in: 0 ... .max)).\($0)" }
        let out = OSAllocatedUnfairLock(initialState: [Double]())
        DispatchQueue.concurrentPerform(iterations: names.count) { i in
            if let ms = lookup(names[i], timeout: 3) { out.withLock { $0.append(ms) } }
        }
        return SpeedMath.median(out.withLock { $0 })
    }

    /// getaddrinfo met een tijdslimiet; "bestaat niet" (NXDOMAIN) telt als antwoord.
    nonisolated static func lookup(_ name: String, timeout: Double) -> Double? {
        let result = OSAllocatedUnfairLock<Double?>(initialState: nil), done = DispatchSemaphore(value: 0)
        let t0 = Date()
        DispatchQueue.global(qos: .utility).async {
            var hints = addrinfo(); hints.ai_family = AF_INET; hints.ai_socktype = SOCK_STREAM
            var res: UnsafeMutablePointer<addrinfo>?
            let r = getaddrinfo(name, nil, &hints, &res)
            if let res { freeaddrinfo(res) }
            if r == 0 || r == EAI_NONAME { let ms = -t0.timeIntervalSinceNow * 1000; result.withLock { $0 = ms } }
            done.signal()
        }
        _ = done.wait(timeout: .now() + timeout)
        return result.withLock { $0 }
    }

    /// Eén oordeel in gewone woorden, met de getallen eronder. router nil = geen router bekend; got 0 = geen antwoord.
    nonisolated static func find(wired: Bool, rssi: Int?, snr: Int?, band: String?, channel: Int?,
                                 router: PingStats?, internet: PingStats, dnsMs: Double?) -> Finding {
        func ms(_ v: Double?) -> String { v.map { $0 < 1 ? "<1 ms" : "\(Int($0.rounded())) ms" } ?? L("no reply") }
        func lost(_ s: PingStats) -> String { s.got > 0 && s.loss > 0 ? L(" (\(Int((s.loss * 100).rounded()))% lost)") : "" }
        var nums = [L("router \(router.map { ms($0.avg) + lost($0) } ?? L("unknown"))"), L("internet \(ms(internet.avg) + lost(internet))"), "DNS " + ms(dnsMs)]
        if let rssi { nums.append("Wi-Fi \(rssi) dBm" + (snr.map { " · SNR \($0) dB" } ?? "") + (channel.map { " · ch \($0)" } ?? "") + (band.map { " (\($0))" } ?? "")) }
        func say(_ text: String, _ ok: Bool?) -> Finding { Finding(text: text, ok: ok, numbers: nums.joined(separator: ", ")) }

        let local = wired ? L("network") : "Wi-Fi"
        let weak = !wired && ((rssi.map { $0 < -72 } ?? false) || (snr.map { $0 < 20 } ?? false))
        let weakText = L("Weak Wi-Fi signal\(rssi.map { " (\($0) dBm)" } ?? ""): move closer to the router or use a cable")
        let rDead = router?.got == 0, iDead = internet.got == 0
        if router == nil && iDead { return say(L("No router found: reconnect to your network"), false) }
        if rDead && iDead {
            return say(weak ? weakText : wired ? L("Router not responding: check the cable, then restart the router") : L("Router not responding: restart the router"), false)
        }
        // Router negeert pings maar internet werkt: dan zegt de router-meting niets
        if let r = router, r.got > 0, r.loss >= 0.1 || (r.avg ?? 0) > 50 || (r.jitter ?? 0) > 30 {
            if weak { return say(weakText, false) }
            if wired { return say(L("The cable link to your router is unstable: check the cable, then restart the router"), false) }
            return say(L("Your Wi-Fi is unstable despite a good signal: interference or a busy router; restart the router")
                       + (band == "2.4 GHz" ? L(" or switch to 5 GHz") : ""), false)
        }
        if iDead { return say(L("Your \(local) is fine, but the internet doesn't answer: restart the modem, or your provider has an outage"), false) }
        if internet.loss >= 0.05 || (internet.avg ?? 0) > 100 || (internet.jitter ?? 0) > 30 {
            let what = internet.loss >= 0.05 ? L("loses data") : (internet.avg ?? 0) > 100 ? L("is slow") : L("is unsteady")
            return say(L("Your \(local) is fine; your internet provider \(what) (router \(ms(router?.avg)), internet \(ms(internet.avg)))"), false)
        }
        guard let dnsMs else { return say(L("Name lookups (DNS) fail: try 1.1.1.1 as DNS server in Network settings"), false) }
        if dnsMs > 300 { return say(L("DNS is slow (\(ms(dnsMs))): try 1.1.1.1 as DNS server in Network settings"), false) }
        if weak { return say(L("Everything works, but the Wi-Fi signal is weak\(rssi.map { " (\($0) dBm)" } ?? ""): moving closer to the router helps"), nil) }
        return say(L("No problem found: \(local), router, DNS and internet are all fine"), true)
    }

    static func findSelfTest() {
        let good = PingStats(Array(repeating: 3, count: 20), sent: 20), net = PingStats(Array(repeating: 15, count: 20), sent: 20)
        let none = PingStats([], sent: 20)
        func f(wired: Bool = false, rssi: Int? = -55, snr: Int? = 35, band: String? = "5 GHz", router: PingStats? = good, internet: PingStats = net, dns: Double? = 20) -> Finding {
            find(wired: wired, rssi: wired ? nil : rssi, snr: wired ? nil : snr, band: wired ? nil : band, channel: wired ? nil : 36, router: router, internet: internet, dnsMs: dns)
        }
        let ok = f()
        precondition(ok.ok == true && ok.text == "No problem found: Wi-Fi, router, DNS and internet are all fine", ok.text)
        precondition(ok.numbers == "router 3 ms, internet 15 ms, DNS 20 ms, Wi-Fi -55 dBm · SNR 35 dB · ch 36 (5 GHz)", ok.numbers)
        let slow = f(internet: PingStats(Array(repeating: 180, count: 20), sent: 20))
        precondition(slow.text == "Your Wi-Fi is fine; your internet provider is slow (router 3 ms, internet 180 ms)", slow.text)
        precondition(f(wired: true, internet: PingStats(Array(repeating: 15, count: 18), sent: 20)).text.hasPrefix("Your network is fine; your internet provider loses data"), "verlies")
        let lossy = PingStats(Array(repeating: 4, count: 16), sent: 20)
        precondition(f(rssi: -80, snr: 12, router: lossy).text == "Weak Wi-Fi signal (-80 dBm): move closer to the router or use a cable", "zwak")
        precondition(f(band: "2.4 GHz", router: lossy).text.hasSuffix("restart the router or switch to 5 GHz"), "storing 2.4")
        precondition(f(wired: true, router: lossy).text.hasPrefix("The cable link"), "kabel")
        precondition(f(router: none, internet: none, dns: nil).text == "Router not responding: restart the router", "router weg")
        precondition(f(wired: true, router: none, internet: none).text.contains("check the cable"), "router weg kabel")
        precondition(f(internet: none, dns: nil).text.hasPrefix("Your Wi-Fi is fine, but the internet doesn't answer"), "internet weg")
        precondition(f(router: none).ok == true, "router negeert pings")
        precondition(f(router: nil, internet: none).text.hasPrefix("No router found"), "geen router")
        precondition(f(dns: 900).text == "DNS is slow (900 ms): try 1.1.1.1 as DNS server in Network settings", "dns traag")
        precondition(f(dns: nil).text.hasPrefix("Name lookups (DNS) fail"), "dns weg")
        let meh = f(rssi: -76, snr: 25)
        precondition(meh.ok == nil && meh.text.hasPrefix("Everything works, but the Wi-Fi signal is weak (-76 dBm)"), meh.text)
        precondition(f(wired: true).text.hasPrefix("No problem found: network,") && !f(wired: true).numbers.contains("Wi-Fi"), "kabel goed")
        let spread = PingStats((1...20).map(Double.init), sent: 20)
        precondition(spread.p95 == 19 && PingStats([1, 2, 3], sent: 3).p95 == nil, "95e percentiel")
        let rep = NetReport(kind: "Wi-Fi", wifi: nil, router: good, internet: net, viaTCP: false, dnsMs: 20, finding: nil,
                            history: [SpeedResult(date: Date(), down: 100, up: 10, ping: 9), SpeedResult(date: Date(), down: 300, up: 30, ping: 8)])
        precondition(rep.fastest?.down == 300 && rep.average?.down == 200 && rep.average?.up == 20, "snelste/gemiddelde")
    }
}

/// Snelheidstest-uitslag in de gekozen eenheid (Instellingen › Network): "412 Mbit/s" of "51.5 MB/s".
func mbitText(_ mbit: Double) -> String {
    UserDefaults.standard.bool(forKey: "speedMB") ? "\(one(mbit / 8)) MB/s" : "\(Int(mbit.rounded())) Mbit/s"
}

// MARK: - Netwerkrapport (PDF voor een helpdesk of provider)
//
// Bewust zonder netwerknaam, BSSID, IP-adressen, gebruikers- of computernaam; vrije tekst gaat door Diag.scrub.

struct NetReport {
    let kind: String?                  // "Wi-Fi", "Ethernet" of de interfacenaam; nil = niet verbonden
    let wifi: WifiDetail?
    let router: PingStats?, internet: PingStats?, viaTCP: Bool, dnsMs: Double?
    let finding: InternetStats.Finding?
    let history: [SpeedResult]
    var date = Date()

    var fastest: (down: Double, up: Double)? {
        history.isEmpty ? nil : (history.map(\.down).max()!, history.map(\.up).max()!)
    }
    var average: (down: Double, up: Double)? {
        guard let d = SpeedMath.mean(history.map(\.down)), let u = SpeedMath.mean(history.map(\.up)) else { return nil }
        return (d, u)
    }

    /// Twee A4-pagina's: de cijfers, en wat ze betekenen. false = schrijven mislukt.
    @MainActor func writePDF(to url: URL) -> Bool {
        var box = CGRect(x: 0, y: 0, width: 595, height: 842)
        guard let pdf = CGContext(url as CFURL, mediaBox: &box, nil) else { return false }
        for page in 0..<2 {
            let v = NetReportPage(r: self, page: page).environment(\.theme, .minimal).environment(\.colorScheme, .light)
            ImageRenderer(content: v).render { size, draw in   // A4, of langer als het er niet op past
                var page = CGRect(origin: .zero, size: size)
                pdf.beginPDFPage([kCGPDFContextMediaBox: Data(bytes: &page, count: MemoryLayout<CGRect>.size)] as CFDictionary)
                draw(pdf); pdf.endPDFPage()
            }
        }
        pdf.closePDF()
        return true
    }
}

private struct NetReportPage: View {
    let r: NetReport, page: Int
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            VStack(alignment: .leading, spacing: 3) {
                Text(page == 0 ? L("Network report") : L("What the numbers mean")).font(.system(size: 20, weight: .semibold))
                Text(verbatim: "Fusebox · " + r.date.formatted(date: .long, time: .shortened)).font(.system(size: 11)).foregroundStyle(theme.secondary)
            }
            if page == 0 { numbers } else { glossary }
            Spacer(minLength: 0)
            Text("Contains no network name, addresses, serial numbers or user names.").font(.system(size: 9)).foregroundStyle(theme.secondary)
        }
        .font(.system(size: 11))
        .padding(.horizontal, 44).padding(.vertical, 28).frame(width: 595, alignment: .topLeading).frame(minHeight: 842, alignment: .topLeading)
        .background(.white).foregroundStyle(theme.text)
    }

    // MARK: Pagina 1: de cijfers

    @ViewBuilder private var numbers: some View {
        section(L("Connection")) {
            row(L("Type"), r.kind.map { $0 == "Wi-Fi" || $0 == "Ethernet" ? $0 : L("Other (\($0))") } ?? L("Not connected"))
        }
        if let w = r.wifi {
            section("Wi-Fi") {
                row(L("Signal"), "\(w.rssi) dBm (\(w.word))")
                if let snr = w.snr { row(L("Noise"), "\(w.noise) dBm · SNR \(snr) dB") }
                if w.rate > 0 { row(L("Link speed"), "\(Int(w.rate)) Mbit/s") }
                if let ch = w.channel { row(L("Channel"), ([String(ch)] + [w.band, w.width].compactMap { $0 }).joined(separator: " · ")) }
                if let phy = w.phy { row(L("Standard"), phy) }
            }
        }
        section(L("Quality")) {
            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 2) {
                GridRow {
                    Text(verbatim: "").frame(width: 130, alignment: .leading)
                    ForEach([L("Average"), L("Jitter"), L("Lost"), "95%"], id: \.self) { Text($0).foregroundStyle(theme.secondary) }
                }
                pingRow(L("Router"), r.router)
                if r.internet != nil { pingRow(r.viaTCP ? L("Internet (TCP)") : "Internet", r.internet) }
            }
            if let d = r.dnsMs { row(L("DNS lookup"), "\(msText(d)) ms").padding(.top, 2) }
        }
        if let f = r.finding {
            section(L("Find the problem")) {
                Label { Text(Diag.scrub(f.text)).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon(f.ok)).foregroundStyle(color(f.ok)) }
                if !f.numbers.isEmpty { Text(Diag.scrub(f.numbers)).foregroundStyle(theme.secondary).padding(.leading, 22).fixedSize(horizontal: false, vertical: true) }
            }
        }
        section(L("Speed test")) {
            if let last = r.history.first {
                Text("At a glance").font(.system(size: 11, weight: .semibold)).padding(.bottom, 1)
                row(L("Download"), mbitText(last.down) + (last.downPeak.map { L(" · top \(mbitText($0))") } ?? ""))
                row(L("Upload"), mbitText(last.up) + (last.upPeak.map { L(" · top \(mbitText($0))") } ?? ""))
                if let p = last.ping { row(L("Delay"), "\(msText(p)) ms") }
                if let b = last.loadedPing { row(L("Busy delay"), "\(msText(b)) ms") }
                if let v = last.variation { row(L("Stability"), SpeedMath.stabilityText(v)) }
                row(L("Tested"), last.date.formatted(date: .abbreviated, time: .shortened) + " · " + (last.kind.map { L("\($0.title) test") } ?? L("single connection")))
                if let f = r.fastest { row(L("Fastest"), "↓ \(mbitText(f.down)) · ↑ \(mbitText(f.up))") }
                if let a = r.average { row(L("Average (\(r.history.count) tests)"), "↓ \(mbitText(a.down)) · ↑ \(mbitText(a.up))") }
                if let idle = last.ping, let busy = last.loadedPing {
                    let v = SpeedMath.loadVerdict(idle: idle, loaded: busy)
                    Label { Text(v.text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon(v.ok)).foregroundStyle(color(v.ok)) }.padding(.top, 2)
                }
                if r.history.count > 1 { history }
            } else {
                Text("No speed test yet").foregroundStyle(theme.secondary)
            }
        }
    }

    private var history: some View {
        VStack(alignment: .leading, spacing: 4) {
            Text("History").font(.system(size: 11, weight: .semibold))
            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 1) {
                GridRow {
                    ForEach([L("Tested"), L("Type"), L("Download"), L("Upload"), L("Delay")], id: \.self) { Text($0).foregroundStyle(theme.secondary) }
                }
                ForEach(Array(r.history.enumerated()), id: \.offset) { _, h in
                    GridRow {
                        Text(h.date.formatted(date: .abbreviated, time: .shortened))
                        Text(h.kind?.title ?? L("single connection"))
                        Text(mbitText(h.down)).monospacedDigit()
                        Text(mbitText(h.up)).monospacedDigit()
                        Text(verbatim: h.ping.map { "\(msText($0)) ms" } ?? "–").monospacedDigit()
                    }
                }
            }
        }
        .padding(.top, 6)
    }

    private func pingRow(_ name: String, _ s: PingStats?) -> some View {
        let ok = s.map { $0.got > 0 } ?? false
        func ms(_ v: Double?) -> String { ok ? v.map { "\(msText($0)) ms" } ?? "–" : "–" }
        return GridRow {
            Text(name).foregroundStyle(theme.secondary).frame(width: 130, alignment: .leading)
            Text(verbatim: ok ? "\(msText(s?.avg)) ms" : s == nil ? "–" : L("no answer")).monospacedDigit()
            Text(ms(s?.jitter)).monospacedDigit()
            Text(s.map { "\(Int(($0.loss * 100).rounded()))%" } ?? "–").monospacedDigit()
            Text(ms(s?.p95)).monospacedDigit()
        }
    }

    // MARK: Pagina 2: uitleg

    private var glossary: some View {
        VStack(alignment: .leading, spacing: 13) {
            ForEach(NetTerm.report) { term($0.title, $0.text) }   // dezelfde uitleg als de ⓘ en de labels in de app
        }
    }

    private func term(_ name: String, _ text: String) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(name).font(.system(size: 12, weight: .semibold))
            Text(text).foregroundStyle(theme.text.opacity(0.8)).fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: Bouwstenen

    private func section<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title.uppercased()).font(.system(size: 10, weight: .semibold)).foregroundStyle(theme.accent).padding(.bottom, 2)
            content()
        }
        .padding(.top, 2)
    }

    private func row(_ k: String, _ v: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 18) {
            Text(k).foregroundStyle(theme.secondary).frame(width: 130, alignment: .leading)
            Text(v).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func icon(_ ok: Bool?) -> String { ok == false ? "exclamationmark.triangle" : ok == true ? "checkmark.circle" : "info.circle" }
    private func color(_ ok: Bool?) -> Color { ok == false ? theme.warn : ok == true ? theme.good : theme.secondary }
}
