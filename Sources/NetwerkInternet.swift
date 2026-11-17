import SwiftUI
import CoreWLAN
import SystemConfiguration
import Darwin
import Network
import os

// MARK: - Internet: verbinding, Wi-Fi, kwaliteit (pings), live grafiek, snelheidstest, publiek IP
//
// Draait alleen zolang het Netwerk-tabblad open is. Pings gaan naar de router en `pingHost` ("" = internetdeel uit).
// Cloudflare (snelheidstest, publiek IP) alleen na een klik.

struct NetConnection {
    let iface: String, kind: String
    let ipv4: String?, ipv6: [String], router: UInt32?, dns: [String], ipv6Internet: Bool

    static func current() -> NetConnection? {
        let v4 = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv4" as CFString) as? [String: Any]
        let v6 = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv6" as CFString) as? [String: Any]
        let dns = SCDynamicStoreCopyValue(nil, "State:/Network/Global/DNS" as CFString) as? [String: Any]
        guard let iface = v4?["PrimaryInterface"] as? String ?? v6?["PrimaryInterface"] as? String else { return nil }
        var ipv4: String?, ipv6: [String] = []
        var list: UnsafeMutablePointer<ifaddrs>?
        if getifaddrs(&list) == 0 {
            var p = list
            while let a = p?.pointee {
                defer { p = a.ifa_next }
                guard String(cString: a.ifa_name) == iface, let sa = a.ifa_addr else { continue }
                if sa.pointee.sa_family == UInt8(AF_INET) {
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
        return NetConnection(iface: iface, kind: wifi ? "Wi-Fi" : iface.hasPrefix("en") ? "Ethernet" : iface,
                             ipv4: ipv4, ipv6: ipv6, router: (v4?["Router"] as? String).flatMap(parseIP),
                             dns: dns?["ServerAddresses"] as? [String] ?? [],
                             ipv6Internet: !ipv6.isEmpty && v6?["Router"] != nil)
    }
}

struct WifiDetail {
    let rssi: Int, noise: Int, rate: Double
    let channel: Int?, band: String?, width: String?, phy: String?
    var snr: Int? { rssi < 0 && noise < 0 ? rssi - noise : nil }
    var word: String { rssi >= -60 ? "good" : rssi >= -70 ? "fair" : "weak" }

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
    var title: String { self == .quick ? "Quick" : "Full" }
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
        return cv < 0.1 ? "steady (\(pct))" : cv < 0.25 ? "varies a little (\(pct))" : "unsteady (\(pct))"
    }

    /// Vertraging onder belasting ("bufferbloat") in gewone woorden. ok: true goed, false probleem, nil let op.
    static func loadVerdict(idle: Double, loaded: Double) -> (text: String, ok: Bool?) {
        let extra = max(0, loaded - idle), ms = "+\(Int(extra.rounded())) ms"
        if extra < 30 { return ("Delay barely rises while the line is busy (\(ms)) → fine for calls and games", true) }
        if extra < 100 { return ("Delay rises while the line is busy (\(ms)) → calls may stutter during big downloads", nil) }
        return ("Delay rises a lot while the line is busy (\(ms), \"bufferbloat\") → calls and games suffer during downloads; a router with smart queueing (SQM) helps", false)
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
        speedPhase = "Measuring delay…"
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
                if !Task.isCancelled { self?.speedError = "Speed test failed: \(error.localizedDescription)" }
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
        let start = Date(), name = upload ? "Upload" : "Download"
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
            speedPhase = "\(kind.title) · \(name) · \(kind.streams) connections · \(max(0, Int(meter.deadline.timeIntervalSince(now).rounded(.up)))) s left"
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
        publicIPState = "Asking Cloudflare…"
        ipTask = Task { [weak self] in
            let cfg = URLSessionConfiguration.ephemeral
            cfg.timeoutIntervalForRequest = 8
            let session = URLSession(configuration: cfg)
            defer { session.invalidateAndCancel() }
            let result = try? await session.data(from: URL(string: "https://speed.cloudflare.com/meta")!)
            guard let self, !Task.isCancelled else { return }
            self.ipTask = nil
            guard let data = result?.0, data.count < 100_000, let ip = Self.parseMeta(data) else {
                self.publicIPState = "Couldn't reach Cloudflare"; return
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
        if rDead && iDead { return ("No answer from the router or the internet → check the connection", false) }
        if iDead { return ("The router answers but the internet doesn't → problem at your provider or modem", false) }
        let r = rDead ? nil : router, i = internetCheck ? internet : nil
        let rBad = r.map { $0.loss >= 0.02 || ($0.avg ?? 0) > 30 || ($0.jitter ?? 0) > 20 } ?? false
        let iBad = i.map { $0.loss >= 0.02 || ($0.avg ?? 0) > 100 || ($0.jitter ?? 0) > 30 } ?? false
        var parts: [String] = []
        if let r, rBad {
            let what = r.loss >= 0.02 ? "\(pct(r.loss)) packet loss to the router" : "slow replies from the router (\(ms(r.avg)))"
            parts.append(weak ? "Wi-Fi signal weak and \(what) → move closer to the router or use a cable"
                         : wired ? "\(what.prefix(1).uppercased() + what.dropFirst()) on a cable → check the cable or restart the router"
                         : "\(what.prefix(1).uppercased() + what.dropFirst()) → Wi-Fi interference or a busy router; restart it or use a cable")
        } else if let i, iBad {
            let what = i.loss >= 0.02 ? "\(pct(i.loss)) packet loss" : (i.jitter ?? 0) > 30 ? "unsteady replies (jitter \(ms(i.jitter)))" : "slow replies (\(ms(i.avg)))"
            parts.append("Your own network is fine, but the internet has \(what) → most likely your provider")
        } else if weak {
            parts.append("Wi-Fi signal is weak (\(rssi.map { "\($0) dBm" } ?? "low")) but the connection works for now; moving closer helps")
        }
        if internetCheck, let dnsMs, dnsMs > 150 { parts.append("Name lookups are slow (\(ms(dnsMs))) → try another DNS server") }
        if parts.isEmpty {
            var good = "All good: " + (r.map { "router \(ms($0.avg))" } ?? "router doesn't answer pings")
            if let i { good += ", internet \(ms(i.avg))" } else if !internetCheck { good += " (internet check is off in Settings)" }
            return (good + ", no packet loss", true)
        }
        return (parts.joined(separator: ". "), rBad || iBad ? false : nil)
    }
}

// MARK: - Weergave

struct InternetPanel: View {
    let wide: Bool
    @StateObject private var stats = InternetStats()
    @Environment(\.theme) private var theme
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @AppStorage("netPanelOpen") private var open = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(alignment: .top, spacing: 10) {
                VStack(alignment: .leading, spacing: 5) {
                    if let c = stats.conn {
                        line(c.kind == "Wi-Fi" ? "wifi" : "cable.connector", "This Mac \(c.ipv4 ?? "–") on \(c.kind)"
                             + (stats.wifi.map { " · signal \($0.word) (\($0.rssi) dBm)" } ?? ""))
                        HStack(spacing: 16) {
                            stat("Router", stats.router)
                            if !pingHost.isEmpty { stat("Internet", stats.internet) }
                        }
                        verdictLine
                    } else {
                        line("wifi.slash", "Not connected to a network")
                    }
                }
                Spacer(minLength: 0)
                if wide, stats.conn != nil { graph.frame(width: 170, height: 46) }
                Button {
                    withAnimation(.snappy(duration: 0.25)) { open.toggle() }
                } label: {
                    Image(systemName: "chevron.down").rotationEffect(.degrees(open ? 180 : 0))
                        .font(.system(size: 11, weight: .semibold)).frame(width: 20, height: 20).contentShape(.rect)
                }
                .buttonStyle(.plain).foregroundStyle(theme.onRow()).help(open ? "Fewer details" : "More details: connection, Wi-Fi, quality, speed test")
            }
            if open { details.transition(.opacity.combined(with: .move(edge: .top))) }
        }
        .font(theme.label(11)).foregroundStyle(theme.onRow(true))
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: 0)
        .clipped()
        .onAppear { stats.start() }
        .onDisappear { stats.stop() }
        .onChange(of: pingHost) { stats.start() }
    }

    private var verdictLine: some View {
        Group {
            if stats.testing {
                line("hourglass", "Testing the connection…")
            } else if stats.router != nil || stats.internet != nil {
                let v = InternetStats.verdict(wired: stats.conn?.kind != "Wi-Fi", rssi: stats.wifi?.rssi, snr: stats.wifi?.snr,
                                              router: stats.router, internet: stats.internet, dnsMs: stats.dnsMs, internetCheck: !pingHost.isEmpty)
                line(v.ok == false ? "exclamationmark.triangle" : v.ok == true ? "checkmark.circle" : "info.circle", v.text)
                    .foregroundStyle(theme.rowStyle == .blocks ? .white : v.ok == false ? theme.warn : v.ok == true ? theme.good : theme.onRow(true))
            }
        }
    }

    private func stat(_ title: String, _ s: PingStats?) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 5) {
            Text(title).foregroundStyle(theme.onRow())
            Text(s == nil ? "…" : msText(s?.avg)).font(theme.number(15)).foregroundStyle(theme.onRow())
            Text("ms").foregroundStyle(theme.onRow(true))
            if let s, s.loss > 0 { Text("\(Int((s.loss * 100).rounded()))% lost").foregroundStyle(theme.rowStyle == .blocks ? .white : theme.warn) }
        }
    }

    /// Live: internet (lijn) en router (dunne lijn), laatste ~2 minuten.
    private var graph: some View {
        let inet = stats.liveInternet, router = stats.liveRouter
        let main = pingHost.isEmpty || inet.isEmpty ? router : inet
        let n = max(main.count, 2)
        return VStack(alignment: .trailing, spacing: 2) {
            SparkView(values: main, second: main == router ? [] : router, top: max((main + router).max() ?? 1, 5) * 1.2,
                      fill: Double(n - 1) / Double(InternetStats.graphPoints - 1), glides: false)
            Text(main == router ? "router · live" : "internet — router ┄ · live").font(theme.label(9)).foregroundStyle(theme.onRow(true))
        }
        .help("Reply times over the last two minutes (max \(msText((main + router).max())) ms)")
    }

    // MARK: Details (uitgeklapt)

    private var details: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 230), spacing: 16, alignment: .top)], alignment: .leading, spacing: 12) {
            if let c = stats.conn {
                section("Connection") {
                    kv("Type", "\(c.kind) (\(c.iface))")
                    kv("IPv4", c.ipv4 ?? "–")
                    kv("IPv6", c.ipv6.first.map { $0 + (c.ipv6.count > 1 ? " (+\(c.ipv6.count - 1))" : "") } ?? "none")
                    kv("Router", c.router.map(ipString) ?? "unknown")
                    kv("DNS", c.dns.isEmpty ? "unknown" : c.dns.prefix(3).joined(separator: ", "))
                    kv("IPv6 internet", c.ipv6Internet ? "Yes" : "No")
                }
            }
            if let w = stats.wifi {
                section("Wi-Fi") {
                    kv("Signal", "\(w.rssi) dBm (\(w.word))")
                    if let snr = w.snr { kv("Noise", "\(w.noise) dBm · SNR \(snr) dB") }
                    if w.rate > 0 { kv("Link speed", "\(Int(w.rate)) Mbit/s") }
                    if let ch = w.channel { kv("Channel", ([String(ch)] + [w.band, w.width].compactMap { $0 }).joined(separator: " · ")) }
                    if let phy = w.phy { kv("Standard", phy) }
                }
            }
            section("Quality (10 pings)") {
                kv("Router", statsText(stats.router))
                if !pingHost.isEmpty {
                    kv("Internet", statsText(stats.internet) + (stats.internetViaTCP ? " (TCP)" : ""))
                    kv("DNS lookup", stats.dnsMs.map { "\(msText($0)) ms" } ?? "–")
                } else {
                    kv("Internet", "off (Settings)")
                }
                Button { stats.start() } label: { NetPill(title: stats.testing ? "Testing…" : "Test again", icon: "arrow.clockwise") }
                    .buttonStyle(.plain).disabled(stats.testing).padding(.top, 2)
            }
            section("Speed test") { speedTest }
        }
        .padding(.top, 2)
    }

    @ViewBuilder private var speedTest: some View {
        let warn = theme.rowStyle == .blocks ? Color.white : theme.warn
        if let phase = stats.speedPhase {
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text(phase).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
                Button { stats.cancelSpeedTest() } label: { NetPill(title: "Cancel", icon: "xmark") }.buttonStyle(.plain)
            }
            ProgressView(value: stats.speedProgress).controlSize(.small)
            if let live = stats.speedLive { kv("Now", mbitText(live)) }
        } else if let kind = stats.speedConfirm {
            Text("This connection is metered (mobile data, iPhone hotspot or Low Data Mode). The \(kind.title.lowercased()) test uses \(usage(kind)) of data.")
                .foregroundStyle(warn).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 6) {
                Button { stats.runSpeedTest(kind) } label: { NetPill(title: "Run anyway", icon: "speedometer") }.buttonStyle(.plain)
                Button { stats.dismissSpeedConfirm() } label: { NetPill(title: "Not now", icon: "xmark") }.buttonStyle(.plain)
            }
        } else {
            HStack(spacing: 6) {
                Button { stats.requestSpeedTest(.quick) } label: { NetPill(title: "Quick", icon: "hare") }
                    .buttonStyle(.plain).help("About 10 s, 4 connections at once. The first 2 s, while the line speeds up, don't count.")
                Button { stats.requestSpeedTest(.full) } label: { NetPill(title: "Full", icon: "gauge.with.dots.needle.67percent") }
                    .buttonStyle(.plain).help("About 30 s, 6 connections: typical and top speed, delay while the line is busy (bufferbloat) and how steady it is.")
            }
            Text("Quick uses \(usage(.quick)), Full \(usage(.full)) (via Cloudflare)").font(theme.label(10)).fixedSize(horizontal: false, vertical: true)
        }
        if let e = stats.speedError { Text(e).foregroundStyle(warn).fixedSize(horizontal: false, vertical: true) }
        if let last = stats.history.first {
            kv("Download", mbitText(last.down) + (last.downPeak.map { " · top \(mbitText($0))" } ?? ""))
            kv("Upload", mbitText(last.up) + (last.upPeak.map { " · top \(mbitText($0))" } ?? ""))
            kv("Delay", last.ping.map { "\(msText($0)) ms" + (last.loadedPing.map { " · busy \(msText($0)) ms" } ?? "") } ?? "–")
            if let v = last.variation { kv("Stability", SpeedMath.stabilityText(v)) }
            kv("Tested", last.date.formatted(date: .abbreviated, time: .shortened) + " · " + kindName(last.kind))
            if let idle = last.ping, let busy = last.loadedPing {
                let v = SpeedMath.loadVerdict(idle: idle, loaded: busy)
                line(v.ok == false ? "exclamationmark.triangle" : v.ok == true ? "checkmark.circle" : "info.circle", v.text)
                    .foregroundStyle(theme.rowStyle == .blocks ? .white : v.ok == false ? theme.warn : v.ok == true ? theme.good : theme.onRow(true))
            }
            if stats.history.count > 1 { historyBars }
        }
        Divider().opacity(0.4).padding(.vertical, 2)
        if let ip = stats.publicIP {
            kv("Public IP", ip.ip)
            if let isp = ip.isp { kv("Provider", isp) }
            if let place = ip.place { kv("Location", place) }
        } else {
            Button { stats.loadPublicIP() } label: { NetPill(title: stats.publicIPState ?? "Show public IP", icon: "globe") }
                .buttonStyle(.plain).help("Asks speed.cloudflare.com which address and provider you appear as")
        }
    }

    /// Geschat verbruik: op basis van de vorige uitslag, anders het plafond (beide richtingen).
    private func usage(_ kind: SpeedKind) -> String {
        SpeedMath.estimate(kind, last: stats.history.first).map { "about \(bytes(Double($0)))" } ?? "at most \(bytes(Double(kind.cap * 2)))"
    }

    private func kindName(_ k: SpeedKind?) -> String { k.map { "\($0.title) test" } ?? "single connection" }

    /// Laatste 10 metingen als staafjes (download), oudste links. Volledig = brede staaf.
    private var historyBars: some View {
        let list = Array(stats.history.reversed()), top = max(list.map(\.down).max() ?? 1, 1)
        let color = theme.rowStyle == .blocks ? Color.white : theme.accent
        return HStack(alignment: .bottom, spacing: 3) {
            ForEach(Array(list.enumerated()), id: \.offset) { _, r in
                Capsule().fill(color.opacity(r == stats.history.first ? 1 : 0.45))
                    .frame(width: r.kind == .full ? 9 : 5, height: max(3, 24 * r.down / top))
                    .help("\(r.date.formatted(date: .abbreviated, time: .shortened)) · \(kindName(r.kind)): ↓\(mbitText(r.down)) ↑\(mbitText(r.up))")
            }
            Text("last \(list.count) · wide = Full").font(theme.label(9)).padding(.leading, 4)
        }
        .frame(height: 26, alignment: .bottom)
    }

    private func statsText(_ s: PingStats?) -> String {
        guard let s else { return stats.testing ? "…" : "–" }
        guard s.got > 0, let avg = s.avg else { return "no answer" }
        var t = "\(msText(avg)) ms"
        if let lo = s.min, let hi = s.max { t += " (\(msText(lo))–\(msText(hi)))" }
        if let j = s.jitter { t += " · jitter \(msText(j))" }
        return t + " · \(Int((s.loss * 100).rounded()))% lost"
    }

    private func section<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            Text(title).font(theme.label(11)).foregroundStyle(theme.onRow())
            content()
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func kv(_ k: String, _ v: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Text(k).frame(width: 78, alignment: .leading)
            Text(v).foregroundStyle(theme.onRow()).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func line(_ icon: String, _ text: String) -> some View {
        Label { Text(text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon).frame(width: 14) }
    }
}

/// Snelheidstest-uitslag in de gekozen eenheid (Instellingen › Network): "412 Mbit/s" of "51.5 MB/s".
func mbitText(_ mbit: Double) -> String {
    UserDefaults.standard.bool(forKey: "speedMB") ? "\(one(mbit / 8)) MB/s" : "\(Int(mbit.rounded())) Mbit/s"
}
