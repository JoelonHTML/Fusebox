import SwiftUI
import CoreWLAN
import SystemConfiguration
import Darwin

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

struct SpeedResult: Codable, Equatable {
    let date: Date, down: Double, up: Double, ping: Double?   // Mbit/s, ms
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
        testing = false; speedPhase = nil; publicIPState = nil
    }

    // MARK: Snelheidstest (Cloudflare, alleen na een klik)

    func runSpeedTest() {
        guard speedTask == nil else { return }
        speedError = nil
        speedPhase = "Measuring delay…"
        speedTask = Task { [weak self] in
            let cfg = URLSessionConfiguration.ephemeral
            cfg.timeoutIntervalForRequest = 15
            cfg.timeoutIntervalForResource = 60
            cfg.urlCache = nil
            cfg.requestCachePolicy = .reloadIgnoringLocalCacheData
            let session = URLSession(configuration: cfg)
            defer { session.invalidateAndCancel() }
            do {
                // Vertraging: kleinste van 3 lege verzoeken (de eerste opent de verbinding en telt niet mee)
                let zero = URL(string: "https://speed.cloudflare.com/__down?bytes=0")!
                var pings: [Double] = []
                for i in 0..<4 {
                    let t0 = Date()
                    _ = try await session.data(from: zero)
                    if i > 0 { pings.append(-t0.timeIntervalSinceNow * 1000) }
                }
                self?.speedPhase = "Testing download…"
                let t1 = Date()
                let (file, resp) = try await session.download(from: URL(string: "https://speed.cloudflare.com/__down?bytes=25000000")!)
                let downSecs = -t1.timeIntervalSinceNow
                let bytes = (try? FileManager.default.attributesOfItem(atPath: file.path)[.size] as? Int) ?? 0
                try? FileManager.default.removeItem(at: file)
                guard (resp as? HTTPURLResponse)?.statusCode == 200, bytes > 1_000_000 else { throw URLError(.badServerResponse) }
                self?.speedPhase = "Testing upload…"
                var req = URLRequest(url: URL(string: "https://speed.cloudflare.com/__up")!)
                req.httpMethod = "POST"
                req.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
                let upBytes = 10_000_000
                let t2 = Date()
                let (_, upResp) = try await session.upload(for: req, from: Data(count: upBytes))
                let upSecs = -t2.timeIntervalSinceNow
                guard (upResp as? HTTPURLResponse)?.statusCode == 200 else { throw URLError(.badServerResponse) }
                guard let self else { return }
                let r = SpeedResult(date: Date(), down: Double(bytes) * 8 / max(downSecs, 0.001) / 1e6,
                                    up: Double(upBytes) * 8 / max(upSecs, 0.001) / 1e6, ping: pings.min())
                self.history = Array(([r] + self.history).prefix(10))
                if let data = try? JSONEncoder().encode(self.history) { UserDefaults.standard.set(data, forKey: "speedHistory") }
            } catch {
                if !Task.isCancelled { self?.speedError = "Speed test failed: \(error.localizedDescription)" }
            }
            if !Task.isCancelled { self?.speedPhase = nil; self?.speedTask = nil }   // bij annuleren al opgeruimd
        }
    }

    func cancelSpeedTest() {
        speedTask?.cancel(); speedTask = nil
        speedPhase = nil
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
                      fill: Double(n - 1) / Double(InternetStats.graphPoints - 1))
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
        if let phase = stats.speedPhase {
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text(phase).foregroundStyle(theme.onRow())
                Button { stats.cancelSpeedTest() } label: { NetPill(title: "Cancel", icon: "xmark") }.buttonStyle(.plain)
            }
        } else {
            Button { stats.runSpeedTest() } label: { NetPill(title: "Run speed test", icon: "speedometer") }
                .buttonStyle(.plain).help("Downloads 25 MB and uploads 10 MB via speed.cloudflare.com")
            Text("Uses ~35 MB of data (via Cloudflare)").font(theme.label(10))
        }
        if let e = stats.speedError { Text(e).foregroundStyle(theme.rowStyle == .blocks ? .white : theme.warn).fixedSize(horizontal: false, vertical: true) }
        if let last = stats.history.first {
            kv("Download", "\(Int(last.down.rounded())) Mbit/s")
            kv("Upload", "\(Int(last.up.rounded())) Mbit/s")
            kv("Delay", last.ping.map { "\(msText($0)) ms" } ?? "–")
            kv("Tested", last.date.formatted(date: .abbreviated, time: .shortened))
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

    /// Laatste 10 metingen als staafjes (download), oudste links.
    private var historyBars: some View {
        let list = Array(stats.history.reversed()), top = max(list.map(\.down).max() ?? 1, 1)
        let color = theme.rowStyle == .blocks ? Color.white : theme.accent
        return HStack(alignment: .bottom, spacing: 3) {
            ForEach(Array(list.enumerated()), id: \.offset) { _, r in
                Capsule().fill(color.opacity(r == stats.history.first ? 1 : 0.45))
                    .frame(width: 7, height: max(3, 24 * r.down / top))
                    .help("\(r.date.formatted(date: .abbreviated, time: .shortened)): ↓\(Int(r.down.rounded())) ↑\(Int(r.up.rounded())) Mbit/s")
            }
            Text("last \(list.count)").font(theme.label(9)).padding(.leading, 4)
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
