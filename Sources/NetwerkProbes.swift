import Foundation
import Darwin
import Synchronization

// MARK: - Netwerk-proeven: kleine, losse BSD-socketfuncties (blokkerend, dus altijd los van de main thread aanroepen)
//
// Alles met harde tijdslimieten en groottegrenzen; antwoorden van het netwerk zijn onbetrouwbaar en worden voorzichtig ontleed.

enum NetProbe {
    static func now() -> UInt64 { DispatchTime.now().uptimeNanoseconds }
    static func ms(since t: UInt64) -> Double { Double(now() - t) / 1e6 }

    static func sockaddrIn(_ ip: UInt32, port: UInt16 = 0) -> sockaddr_in {
        var a = sockaddr_in()
        a.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
        a.sin_family = sa_family_t(AF_INET)
        a.sin_port = port.bigEndian
        a.sin_addr.s_addr = ip.bigEndian
        return a
    }

    /// Privé/LAN-adres (10/8, 172.16/12, 192.168/16, 169.254/16): alleen daar halen we UPnP-beschrijvingen op.
    static func isLAN(_ ip: UInt32) -> Bool {
        ip >> 24 == 10 || ip >> 20 == 0xAC1 || ip >> 16 == 0xC0A8 || ip >> 16 == 0xA9FE
    }

    // MARK: ICMP-echo (onbevoegde datagram-socket, geen root)

    /// `rounds` rondes, `every` s uit elkaar, 3 ms tussen pakketjes; daarna nog `linger` s wachten.
    /// Per adres de reactietijden in ms (in verzendvolgorde). `blocked`: niets kon verstuurd worden (Lokaal netwerk-toestemming?).
    static func ping(_ targets: [UInt32], rounds: Int, every: Double, linger: Double, gap: Double = 0.003) -> (rtt: [UInt32: [Double]], blocked: Bool) {
        guard !targets.isEmpty, rounds > 0, targets.count * rounds < 65_000 else { return ([:], false) }
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_ICMP)
        guard fd >= 0 else { return ([:], false) }
        defer { close(fd) }
        _ = fcntl(fd, F_SETFL, O_NONBLOCK)
        let ident = UInt16.random(in: 1...0xFFFF)
        let n = targets.count, total = n * rounds
        let gapNs = UInt64(gap * 1e9), everyNs = UInt64(every * 1e9)
        let start = now(), end = start + UInt64(rounds - 1) * everyNs + UInt64(n) * gapNs + UInt64(linger * 1e9)
        var sentAt = [UInt64](repeating: 0, count: total), got = Set<Int>(), rtt = [UInt32: [(Int, Double)]]()
        var ok = 0, failed = 0, next = 0
        var buf = [UInt8](repeating: 0, count: 1500)
        while !Task.isCancelled {
            let t = now()
            if t >= end { break }
            while next < total, start + UInt64(next / n) * everyNs + UInt64(next % n) * gapNs <= t {
                let packet = NetScanner.echoPacket(id: ident, seq: UInt16(next))
                var to = sockaddrIn(targets[next % n])
                let sent = withUnsafePointer(to: &to) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, packet, packet.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
                if sent == packet.count { ok += 1; sentAt[next] = now() } else { failed += 1 }
                next += 1
            }
            var p = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            _ = poll(&p, 1, 2)
            while true {
                var from = sockaddr_in(), len = socklen_t(MemoryLayout<sockaddr_in>.size)
                let got_n = withUnsafeMutablePointer(to: &from) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) } }
                guard got_n > 0 else { break }
                let arrived = now()
                guard let r = NetScanner.parseEchoReply(buf[0..<got_n]), r.id == ident else { continue }
                let k = Int(r.seq), ip = UInt32(bigEndian: from.sin_addr.s_addr)
                guard k < total, sentAt[k] > 0, targets[k % n] == ip, got.insert(k).inserted else { continue }
                rtt[ip, default: []].append((k, Double(arrived - sentAt[k]) / 1e6))
            }
        }
        return (rtt.mapValues { $0.sorted { $0.0 < $1.0 }.map(\.1) }, ok == 0 && failed > 0)
    }

    // MARK: TCP-verbinding (poorten, en reactietijd als een apparaat niet op ping antwoordt)

    enum TCPAnswer: Equatable {
        case open(Double), refused(Double)   // ms tot SYN-ACK of RST; beide = apparaat is er
        var ms: Double { switch self { case .open(let m), .refused(let m): m } }
        var isOpen: Bool { if case .open = self { true } else { false } }
    }

    /// Niet-blokkerende connects, hooguit `batch` tegelijk, elk hooguit `timeout` s. Index in `targets` → antwoord (geen antwoord = ontbreekt).
    static func tcpProbe(_ targets: [(ip: UInt32, port: UInt16)], timeout: Double, batch: Int = 48) -> [Int: TCPAnswer] {
        var out: [Int: TCPAnswer] = [:], i = 0
        while i < targets.count, !Task.isCancelled {
            let range = i..<min(i + batch, targets.count)
            i = range.upperBound
            var open: [(idx: Int, fd: Int32, t0: UInt64)] = []
            for k in range {
                let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
                guard fd >= 0 else { continue }
                _ = fcntl(fd, F_SETFL, O_NONBLOCK)
                var on: Int32 = 1
                setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
                var a = sockaddrIn(targets[k].ip, port: targets[k].port)
                let t0 = now()
                let r = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
                if r == 0 { out[k] = .open(ms(since: t0)); close(fd); continue }
                if errno == ECONNREFUSED { out[k] = .refused(ms(since: t0)); close(fd); continue }
                guard errno == EINPROGRESS else { close(fd); continue }
                open.append((k, fd, t0))
            }
            let deadline = now() + UInt64(timeout * 1e9)
            while !open.isEmpty, now() < deadline, !Task.isCancelled {
                var pfds = open.map { pollfd(fd: $0.fd, events: Int16(POLLOUT), revents: 0) }
                let left = Int32(max(1, min(50, (deadline - min(now(), deadline)) / 1_000_000)))
                guard poll(&pfds, nfds_t(pfds.count), left) > 0 else { continue }
                var keep: [(idx: Int, fd: Int32, t0: UInt64)] = []
                for (j, p) in pfds.enumerated() {
                    let e = open[j]
                    guard p.revents != 0 else { keep.append(e); continue }
                    var err: Int32 = 0, len = socklen_t(MemoryLayout<Int32>.size)
                    getsockopt(e.fd, SOL_SOCKET, SO_ERROR, &err, &len)
                    if err == 0 { out[e.idx] = .open(ms(since: e.t0)) } else if err == ECONNREFUSED { out[e.idx] = .refused(ms(since: e.t0)) }
                    close(e.fd)
                }
                open = keep
            }
            open.forEach { close($0.fd) }
        }
        return out
    }

    /// Poorten die we nakijken: poort → wat het meestal is.
    static let ports: [(port: UInt16, name: String)] = [
        (22, "SSH"), (80, "Web"), (443, "Web (secure)"), (445, "File sharing"), (548, "File sharing (AFP)"),
        (554, "Camera stream"), (631, "Printing"), (1883, "MQTT"), (3389, "Remote desktop"), (5000, "NAS / web"),
        (5001, "NAS (secure)"), (8080, "Web (alt)"), (9100, "Printer"), (32400, "Plex"), (62078, "iPhone sync"),
    ]
    static func portName(_ p: UInt16) -> String { ports.first { $0.port == p }?.name ?? "Port \(p)" }
    /// Klikbare webpagina voor een open poort, anders nil.
    static func webURL(_ ip: UInt32, _ port: UInt16) -> URL? {
        let h = ipString(ip)
        switch port {
        case 80: return URL(string: "http://\(h)/")
        case 443: return URL(string: "https://\(h)/")
        case 631, 5000, 8080: return URL(string: "http://\(h):\(port)/")
        case 5001: return URL(string: "https://\(h):5001/")
        case 32400: return URL(string: "http://\(h):32400/web")
        default: return nil
        }
    }
    /// Poorten om reactietijd te meten als ICMP niet terugkomt (iPhone-sync, web, AirPlay).
    static let latencyPorts: [UInt16] = [62078, 80, 443, 7000]

    // MARK: UDP: één socket, pakketjes versturen, antwoorden verzamelen

    /// Verstuurt `packets` (met `gap` s ertussen) en verzamelt antwoorden tot `wait` s na het laatste pakket.
    static func udp(_ packets: [(ip: UInt32, port: UInt16, data: [UInt8])], wait: Double, gap: Double = 0.004,
                    iface: UInt32? = nil, ttl: UInt8 = 1, broadcast: Bool = false) -> (replies: [(ip: UInt32, data: [UInt8])], sent: Int) {
        let fd = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP)
        guard fd >= 0 else { return ([], 0) }
        defer { close(fd) }
        _ = fcntl(fd, F_SETFL, O_NONBLOCK)
        var on: Int32 = 1
        if broadcast { setsockopt(fd, SOL_SOCKET, SO_BROADCAST, &on, socklen_t(MemoryLayout<Int32>.size)) }
        if let iface {
            var a = in_addr(s_addr: iface.bigEndian)
            setsockopt(fd, IPPROTO_IP, IP_MULTICAST_IF, &a, socklen_t(MemoryLayout<in_addr>.size))
        }
        var t = ttl
        setsockopt(fd, IPPROTO_IP, IP_MULTICAST_TTL, &t, socklen_t(1))
        let gapNs = UInt64(gap * 1e9), start = now()
        let end = start + UInt64(max(packets.count - 1, 0)) * gapNs + UInt64(wait * 1e9)
        var replies: [(ip: UInt32, data: [UInt8])] = [], sent = 0, next = 0
        var buf = [UInt8](repeating: 0, count: 9000)
        while !Task.isCancelled {
            let tNow = now()
            while next < packets.count, start + UInt64(next) * gapNs <= tNow {
                let p = packets[next]
                var to = sockaddrIn(p.ip, port: p.port)
                let n = withUnsafePointer(to: &to) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    sendto(fd, p.data, p.data.count, 0, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
                if n == p.data.count { sent += 1 }
                next += 1
            }
            if tNow >= end || wait <= 0 && next >= packets.count { break }
            var pf = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
            _ = poll(&pf, 1, 5)
            while replies.count < 1024 {
                var from = sockaddr_in(), len = socklen_t(MemoryLayout<sockaddr_in>.size)
                let n = withUnsafeMutablePointer(to: &from) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { recvfrom(fd, &buf, buf.count, 0, $0, &len) } }
                guard n > 0 else { break }
                replies.append((UInt32(bigEndian: from.sin_addr.s_addr), Array(buf[0..<n])))
            }
        }
        return (replies, sent)
    }

    // MARK: mDNS (omgekeerd opzoeken: IP → "Alex-iPhone.local")

    static let mdnsGroup: UInt32 = 0xE000_00FB   // 224.0.0.251

    static func reverseName(_ ip: UInt32) -> String { "\(ip & 255).\(ip >> 8 & 255).\(ip >> 16 & 255).\(ip >> 24).in-addr.arpa" }

    /// DNS-vraag; `unicastResponse` zet de QU-bit (antwoord rechtstreeks naar ons).
    static func dnsQuery(_ name: String, type: UInt16, id: UInt16 = 0, unicastResponse: Bool = true) -> [UInt8] {
        var p: [UInt8] = [UInt8(id >> 8), UInt8(id & 255), 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]
        for label in name.split(separator: ".") {
            let b = Array(label.utf8.prefix(63))
            p.append(UInt8(b.count)); p += b
        }
        p.append(0)
        p += [UInt8(type >> 8), UInt8(type & 255), unicastResponse ? 0x80 : 0, 1]
        return p
    }

    /// DNS-naam lezen (met compressie-verwijzingen; hooguit 16 sprongen). `off` komt achter de naam te staan.
    static func readName(_ b: [UInt8], _ off: inout Int) -> String? {
        var labels: [String] = [], i = off, jumped = false, hops = 0
        while i < b.count {
            let len = Int(b[i])
            if len == 0 {
                if !jumped { off = i + 1 }
                return labels.joined(separator: ".")
            }
            if len & 0xC0 == 0xC0 {
                guard i + 1 < b.count, hops < 16 else { return nil }
                if !jumped { off = i + 2 }
                jumped = true; hops += 1
                i = (len & 0x3F) << 8 | Int(b[i + 1])
                continue
            }
            guard len < 64, i + 1 + len <= b.count, labels.count < 64 else { return nil }
            labels.append(String(decoding: b[i + 1 ..< i + 1 + len], as: UTF8.self))
            i += 1 + len
        }
        return nil
    }

    /// PTR-antwoorden voor `x.x.x.x.in-addr.arpa` → [IP: hostnaam zonder ".local"].
    static func reverseAnswers(_ b: [UInt8]) -> [UInt32: String] {
        guard b.count >= 12, b.count <= 9000 else { return [:] }
        let qd = Int(b[4]) << 8 | Int(b[5])
        let rr = (Int(b[6]) << 8 | Int(b[7])) + (Int(b[8]) << 8 | Int(b[9])) + (Int(b[10]) << 8 | Int(b[11]))
        var off = 12, out: [UInt32: String] = [:]
        for _ in 0..<min(qd, 16) {
            guard readName(b, &off) != nil else { return out }
            off += 4
        }
        for _ in 0..<min(rr, 64) {
            guard let owner = readName(b, &off), off + 10 <= b.count else { break }
            let type = Int(b[off]) << 8 | Int(b[off + 1]), len = Int(b[off + 8]) << 8 | Int(b[off + 9])
            let start = off + 10
            guard start + len <= b.count else { break }
            off = start + len
            let lower = owner.lowercased()
            guard type == 12, lower.hasSuffix(".in-addr.arpa") else { continue }
            var o = start
            let parts = lower.dropLast(".in-addr.arpa".count).split(separator: ".")
            guard parts.count == 4, let ip = parseIP(parts.reversed().joined(separator: ".")),
                  let host = readName(b, &o), let name = NetScanner.cleanHostname(host) else { continue }
            out[ip] = name
        }
        return out
    }

    /// Eén mDNS-vraag per adres naar 224.0.0.251:5353; apparaten antwoorden rechtstreeks met hun .local-naam.
    static func mdnsReverse(_ ips: [UInt32], iface: UInt32, wait: Double) -> [UInt32: String] {
        let packets = ips.enumerated().map { (mdnsGroup, UInt16(5353), dnsQuery(reverseName($1), type: 12, id: UInt16(truncatingIfNeeded: $0 + 1))) }
        var out: [UInt32: String] = [:]
        for r in udp(packets, wait: wait, gap: 0.005, iface: iface, ttl: 255).replies {
            out.merge(reverseAnswers(r.data)) { a, _ in a }
        }
        return out
    }

    // MARK: NetBIOS (Windows, NAS, printers: computernaam via UDP 137)

    static let netbiosQuery: [UInt8] = {
        var p: [UInt8] = [0x46, 0x42, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0x20]
        for c in [UInt8(0x2A)] + [UInt8](repeating: 0, count: 15) { p.append(0x41 + (c >> 4)); p.append(0x41 + (c & 0x0F)) }
        return p + [0, 0, 0x21, 0, 1]   // NBSTAT, IN
    }()

    /// Antwoord op de status-vraag → eerste unieke naam met achtervoegsel 0x00 (de computernaam).
    static func netbiosName(_ b: [UInt8]) -> String? {
        guard b.count >= 12, b.count <= 2048 else { return nil }
        var off = 12
        guard readName(b, &off) != nil, off + 11 <= b.count, b[off] == 0, b[off + 1] == 0x21 else { return nil }
        let count = Int(b[off + 10])
        var o = off + 11
        for _ in 0..<count {
            guard o + 18 <= b.count else { break }
            let suffix = b[o + 15], group = b[o + 16] & 0x80 != 0
            if suffix == 0, !group {
                let name = String(decoding: b[o..<o + 15], as: UTF8.self).trimmingCharacters(in: .whitespaces.union(.controlCharacters))
                if !name.isEmpty, name.allSatisfy({ $0.isASCII && !$0.isNewline }) { return name }
            }
            o += 18
        }
        return nil
    }

    static func netbios(_ ips: [UInt32], wait: Double) -> [UInt32: String] {
        var out: [UInt32: String] = [:]
        for r in udp(ips.map { ($0, UInt16(137), netbiosQuery) }, wait: wait, gap: 0.003).replies {
            if let n = netbiosName(r.data) { out[r.ip] = n }
        }
        return out
    }

    // MARK: SSDP / UPnP (TV's, routers, Sonos, Hue, Chromecast, consoles)

    static let ssdpGroup: UInt32 = 0xEFFF_FFFA   // 239.255.255.250
    static let ssdpSearch = Array("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: ssdp:all\r\n\r\n".utf8)

    /// SSDP-antwoord → kopregels (sleutels in kleine letters). Leeg als het geen geldig antwoord is.
    static func ssdpHeaders(_ b: [UInt8]) -> [String: String] {
        guard b.count <= 4096 else { return [:] }
        let lines = String(decoding: b, as: UTF8.self).components(separatedBy: "\n").map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard let first = lines.first, first.hasPrefix("HTTP/1.1 200") || first.hasPrefix("NOTIFY") else { return [:] }
        var h: [String: String] = [:]
        for line in lines.dropFirst().prefix(40) {
            guard let c = line.firstIndex(of: ":") else { continue }
            h[line[..<c].trimmingCharacters(in: .whitespaces).lowercased()] = line[line.index(after: c)...].trimmingCharacters(in: .whitespaces)
        }
        return h
    }

    /// LOCATION alleen volgen als het http is en naar hetzelfde LAN-adres wijst als de afzender.
    static func lanURL(_ s: String, from ip: UInt32) -> URL? {
        guard s.count < 300, let u = URL(string: s), u.scheme?.lowercased() == "http", let h = u.host(), let hip = parseIP(h),
              hip == ip, isLAN(hip) else { return nil }
        return u
    }

    /// M-SEARCH (2×) en ~`wait` s luisteren → per IP hooguit 3 beschrijvings-URL's.
    static func ssdp(iface: UInt32, wait: Double) -> [UInt32: [URL]] {
        let r = udp([(ssdpGroup, 1900, ssdpSearch), (ssdpGroup, 1900, ssdpSearch)], wait: wait, gap: 0.3, iface: iface, ttl: 2)
        var out: [UInt32: [URL]] = [:]
        for reply in r.replies {
            guard let loc = ssdpHeaders(reply.data)["location"], let u = lanURL(loc, from: reply.ip) else { continue }
            if out[reply.ip, default: []].count < 3, !(out[reply.ip]?.contains(u) ?? false) { out[reply.ip, default: []].append(u) }
        }
        return out
    }

    /// Minimale HTTP/1.0 GET over een eigen socket (geen redirects, geen TLS), hooguit `limit` bytes. Alleen naar LAN-adressen.
    static func httpGet(_ url: URL, limit: Int = 65_536, timeout: Double = 2) -> [UInt8]? {
        guard let host = url.host(), let ip = parseIP(host), isLAN(ip) else { return nil }
        let port = UInt16(clamping: url.port ?? 80)
        var path = url.path(percentEncoded: true)
        if path.isEmpty { path = "/" }
        if let q = url.query(percentEncoded: true) { path += "?" + q }
        guard path.count < 512, !path.contains(where: { $0 == "\r" || $0 == "\n" || $0 == " " }) else { return nil }
        let request = Array("GET \(path) HTTP/1.0\r\nHost: \(host):\(port)\r\nUser-Agent: Fusebox\r\nAccept: text/xml\r\nConnection: close\r\n\r\n".utf8)

        let fd = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        _ = fcntl(fd, F_SETFL, O_NONBLOCK)
        var on: Int32 = 1
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        let deadline = now() + UInt64(timeout * 1e9)
        func waitFor(_ ev: Int32) -> Bool {
            guard now() < deadline else { return false }
            var p = pollfd(fd: fd, events: Int16(ev), revents: 0)
            return poll(&p, 1, Int32(max(1, (deadline - now()) / 1_000_000))) > 0
        }
        var a = sockaddrIn(ip, port: port)
        let r = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size)) } }
        guard r == 0 || errno == EINPROGRESS, waitFor(POLLOUT) else { return nil }
        var err: Int32 = 0, len = socklen_t(MemoryLayout<Int32>.size)
        getsockopt(fd, SOL_SOCKET, SO_ERROR, &err, &len)
        guard err == 0, send(fd, request, request.count, 0) == request.count else { return nil }
        var data: [UInt8] = [], buf = [UInt8](repeating: 0, count: 8192)
        while data.count < limit + 4096, waitFor(POLLIN) {
            let n = recv(fd, &buf, buf.count, 0)
            if n <= 0 { break }
            data += buf[0..<n]
        }
        // Kop scheiden van de inhoud; alleen 200 OK
        guard let sep = data.firstRange(of: [13, 10, 13, 10]) else { return nil }
        let head = String(decoding: data[..<sep.lowerBound], as: UTF8.self)
        guard head.hasPrefix("HTTP/1.") , head.split(separator: " ").dropFirst().first == "200",
              !head.lowercased().contains("transfer-encoding: chunked") else { return nil }   // ponytail: chunked niet ondersteund; HTTP/1.0 hoort dat niet te sturen
        return Array(data[sep.upperBound...].prefix(limit))
    }

    struct UPnPInfo: Equatable {
        var friendlyName: String?, manufacturer: String?, modelName: String?, deviceType: String?
    }

    /// Apparaatbeschrijving (XML) → naam, maker, model. Geen DTD/entiteiten (geen XML-bommen), hooguit 64 KB.
    static func parseUPnP(_ data: [UInt8]) -> UPnPInfo? {
        guard data.count <= 65_536 else { return nil }
        let text = String(decoding: data.prefix(2048), as: UTF8.self)
        guard !text.contains("<!DOCTYPE"), !text.contains("<!ENTITY") else { return nil }
        final class Reader: NSObject, XMLParserDelegate {
            var info = UPnPInfo(), element: String?, text = ""
            func parser(_ p: XMLParser, didStartElement name: String, namespaceURI: String?, qualifiedName: String?, attributes: [String: String] = [:]) {
                element = name.split(separator: ":").last.map(String.init); text = ""
            }
            func parser(_ p: XMLParser, foundCharacters s: String) { if element != nil, text.count < 200 { text += s } }
            func parser(_ p: XMLParser, didEndElement name: String, namespaceURI: String?, qualifiedName: String?) {
                let v = text.trimmingCharacters(in: .whitespacesAndNewlines)
                defer { element = nil }
                guard !v.isEmpty, v.count <= 100 else { return }
                switch element {   // eerste voorkomen = het hoofdapparaat
                case "friendlyName" where info.friendlyName == nil: info.friendlyName = v
                case "manufacturer" where info.manufacturer == nil: info.manufacturer = v
                case "modelName" where info.modelName == nil: info.modelName = v
                case "deviceType" where info.deviceType == nil: info.deviceType = v
                default: break
                }
            }
        }
        let parser = XMLParser(data: Data(data))
        parser.shouldResolveExternalEntities = false
        let reader = Reader()
        parser.delegate = reader
        parser.parse()
        return reader.info == UPnPInfo() ? nil : reader.info
    }

    /// Per IP de eerste beschrijving die lukt; hooguit 6 tegelijk, elk ≤ 2 s.
    static func upnp(_ urls: [UInt32: [URL]]) -> [UInt32: UPnPInfo] {
        final class Box: @unchecked Sendable { let lock = NSLock(); var out: [UInt32: UPnPInfo] = [:] }
        let box = Box(), list = Array(urls.prefix(64)), sem = DispatchSemaphore(value: 6)
        DispatchQueue.concurrentPerform(iterations: list.count) { i in
            sem.wait(); defer { sem.signal() }
            for u in list[i].value {
                if let body = httpGet(u), let info = parseUPnP(body) { box.lock.withLock { box.out[list[i].key] = info }; break }
            }
        }
        return box.out
    }

    // MARK: Wake on LAN

    static func magicPacket(_ mac: [UInt8]) -> [UInt8]? {
        guard mac.count == 6 else { return nil }
        return [UInt8](repeating: 0xFF, count: 6) + (0..<16).flatMap { _ in mac }
    }

    /// Magic packet naar het subnet-broadcastadres en 255.255.255.255, poort 9. true = verstuurd.
    static func wake(_ mac: [UInt8], broadcast: UInt32) -> Bool {
        guard let p = magicPacket(mac) else { return false }
        return udp([(broadcast, 9, p), (0xFFFF_FFFF, 9, p)], wait: 0, gap: 0, broadcast: true).sent > 0
    }

    // MARK: DNS-opzoektijd

    /// getaddrinfo (IPv4), getimed. Blokkeert; altijd los van de main thread.
    static func resolve(_ host: String) -> (ip: UInt32?, ms: Double) {
        if let ip = parseIP(host) { return (ip, 0) }
        var hints = addrinfo()
        hints.ai_family = AF_INET
        hints.ai_socktype = SOCK_STREAM
        var res: UnsafeMutablePointer<addrinfo>?
        let t0 = now()
        let r = getaddrinfo(host, nil, &hints, &res)
        let took = ms(since: t0)
        defer { if let res { freeaddrinfo(res) } }
        guard r == 0, let sa = res?.pointee.ai_addr, sa.pointee.sa_family == UInt8(AF_INET) else { return (nil, took) }
        return (sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }, took)
    }

    // MARK: Fabrikanten: officiële IEEE-lijst (Resources/oui.tsv), één keer los van de main thread geladen

    private static let ieee = Mutex<[UInt32: String]?>(nil)
    private static let ieeeStarted = Atomic<Bool>(false)

    /// Laadt de lijst (eenmalig). Blokkeert ~50 ms; niet op de main thread aanroepen.
    static func loadOUI() {
        guard !ieeeStarted.exchange(true, ordering: .acquiringAndReleasing),
              let url = Bundle.main.url(forResource: "oui", withExtension: "tsv"),
              let data = try? Data(contentsOf: url), data.count < 8_000_000 else { return }
        let table = parseOUI(String(decoding: data, as: UTF8.self))
        ieee.withLock { $0 = table }
    }
    static func ieeeVendor(_ prefix: UInt32) -> String? { ieee.withLock { $0?[prefix] } }

    /// "AABBCC<TAB>Maker" per regel → tabel. Onleesbare regels overslaan.
    static func parseOUI(_ text: String) -> [UInt32: String] {
        var t: [UInt32: String] = [:]
        t.reserveCapacity(42_000)
        for line in text.split(whereSeparator: \.isNewline) {
            guard let tab = line.firstIndex(of: "\t"), line.distance(from: line.startIndex, to: tab) == 6,
                  let v = UInt32(line[..<tab], radix: 16), let name = tidyVendor(String(line[line.index(after: tab)...])) else { continue }
            t[v] = name
        }
        return t
    }

    /// "NETGEAR" → "Netgear", "HUAWEI TECHNOLOGIES" → "Huawei Technologies"; registratie-blokken (geen maker) → nil.
    static func tidyVendor(_ s: String) -> String? {
        let v = s.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !v.isEmpty, v != "IEEE Registration Authority", v != "Private" else { return nil }
        let words = v.split(separator: " ").map { w -> String in
            w.count >= 4 && w.allSatisfy({ $0.isLetter && $0.isUppercase }) ? w.prefix(1) + w.dropFirst().lowercased() : String(w)
        }
        return String(words.joined(separator: " ").prefix(40))
    }

    // MARK: Apple-modelnamen ("MacBookPro18,3" → "MacBook Pro")

    static func appleModel(_ id: String) -> String? {
        let map: [(String, String)] = [("MacBookPro", "MacBook Pro"), ("MacBookAir", "MacBook Air"), ("MacBook", "MacBook"),
                                       ("iMac", "iMac"), ("Macmini", "Mac mini"), ("MacPro", "Mac Pro"), ("Mac", "Mac"),
                                       ("iPhone", "iPhone"), ("iPad", "iPad"), ("iPod", "iPod touch"), ("AppleTV", "Apple TV"),
                                       ("AudioAccessory5", "HomePod mini"), ("AudioAccessory", "HomePod"), ("Watch", "Apple Watch"),
                                       ("RealityDevice", "Apple Vision Pro")]
        for (prefix, name) in map where id.hasPrefix(prefix) && id.dropFirst(prefix.count).first?.isNumber == true { return name }
        return nil
    }
}

// MARK: - Statistiek van een reeks pings

struct PingStats: Equatable, Sendable {
    let sent: Int, got: Int
    let avg: Double?, min: Double?, max: Double?, jitter: Double?
    /// 0…1
    var loss: Double { sent > 0 ? Double(sent - got) / Double(sent) : 0 }

    /// `rtts` in verzendvolgorde; jitter = gemiddeld verschil tussen opeenvolgende antwoorden.
    init(_ rtts: [Double], sent: Int) {
        self.sent = sent; got = Swift.min(rtts.count, sent)
        avg = rtts.isEmpty ? nil : rtts.reduce(0, +) / Double(rtts.count)
        min = rtts.min(); max = rtts.max()
        jitter = rtts.count > 1 ? zip(rtts, rtts.dropFirst()).map { abs($1 - $0) }.reduce(0, +) / Double(rtts.count - 1) : nil
    }
}
