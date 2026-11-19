// fusebox-fand — de enige plek die ventilatoren en het laden van de accu mag sturen (root, LaunchDaemon local.fusebox.fand).
// Los programma, NIET in Sources/: build.sh compileert dit apart naar Fusebox.app/Contents/Resources/fusebox-fand;
// Instellingen › Fans kopieert het naar /Library/PrivilegedHelperTools (root:wheel 0755) en start het via launchd.
//
// Protocol: Unix-socket /var/run/local.fusebox.fand.sock, één regel per verbinding:
//   status            → "ok fans=N forced=0|1 hot=0|1"
//   auto              → ventilatoren terug naar macOS
//   set <fan> <rpm>   → ventilator op dit toerental, altijd binnen [F{i}Mn, F{i}Mx] van macOS
//   charge limit <50-100> → laden tegenhouden, maar alleen als de accu (zelf gemeten) al rond die grens zit
//   charge pause      → laden tegenhouden omdat de accu warm is, alleen als hij (zelf gemeten) echt warm is
//   charge auto       → normaal laden
// Veiligheid (hier, niet in de app): nooit onder het minimum; ≥ 95 °C → alles vol; 30 s geen "set" → auto;
// 10 min geen "charge" → normaal laden; SIGTERM/start → auto en normaal laden. Alles wat niet exact klopt wordt geweigerd.

import Foundation
import IOKit
import Darwin
import os

let socketPath = "/var/run/local.fusebox.fand.sock"
let hotLimit = 95.0, coolAgain = 90.0, quietTimeout = 30.0
let chargeTimeout = 600.0          // 10 min niets van de app → weer normaal laden (een crash laat het laden nooit uit staan)
let chargeBand = 6.0               // tegenhouden mag vanaf grens − 6% (app: hysterese van 5%, plus 1% afronding)
let warmBattery = 35.0             // "pause" alleen vanaf 35 °C (app: aan vanaf 40, uit onder 37)
let log = Logger(subsystem: "local.fusebox.fand", category: "fans")

// MARK: - Pure functies (getest met --selftest, zonder root)

enum Command: Equatable { case status, auto, set(fan: Int, rpm: Int), chargeAuto, chargeLimit(Int), chargePause }

/// Strikt: alleen printbare ASCII, ≤ 32 tekens, precies één spatie tussen delen, alleen cijfers als getal.
func parse(_ line: String) -> Command? {
    let b = Array(line.utf8)
    guard !b.isEmpty, b.count <= 32, b.allSatisfy({ (0x20..<0x7f).contains($0) }) else { return nil }
    let parts = line.split(separator: " ", omittingEmptySubsequences: false).map(String.init)
    func num(_ s: String, _ maxLen: Int) -> Int? {
        (1...maxLen).contains(s.utf8.count) && s.utf8.allSatisfy({ (48...57).contains($0) }) ? Int(s) : nil
    }
    switch (parts.count, parts[0]) {
    case (1, "status"): return .status
    case (1, "auto"): return .auto
    case (3, "set"):
        guard let f = num(parts[1], 2), let r = num(parts[2], 5) else { return nil }
        return .set(fan: f, rpm: r)
    case (2, "charge") where parts[1] == "auto": return .chargeAuto
    case (2, "charge") where parts[1] == "pause": return .chargePause
    case (3, "charge") where parts[1] == "limit":
        guard let l = num(parts[2], 3), (50...100).contains(l) else { return nil }
        return .chargeLimit(l)
    default: return nil
    }
}

/// Toerental binnen de grenzen van macOS; onzin-grenzen (0, omgekeerd, absurd hoog) → weigeren.
func clampRPM(_ rpm: Int, min: Double, max: Double) -> Double? {
    guard min > 0, max >= min, max < 20_000 else { return nil }
    return Swift.min(Swift.max(Double(rpm), min), max)
}

/// Mag het laden nu tegengehouden worden? Alleen op eigen metingen, nooit op het woord van de app.
/// Geen meting → nee (dan laadt hij gewoon).
func mayInhibit(_ cmd: Command, battery: Double?, temp: Double?) -> Bool {
    switch cmd {
    case let .chargeLimit(l): return battery.map { $0 >= Double(l) - chargeBand } ?? false
    case .chargePause: return temp.map { $0 >= warmBattery && $0 < 90 } ?? false
    default: return false
    }
}

/// Whitelist van laadsleutels (Apple Silicon): grootte, bytes voor "laden mag" en "niet laden". De eerste die bestaat wint.
/// CHTE (ui32, little-endian) = nieuwere firmware; CH0B + CH0C (1 byte, 0x02 = tegenhouden) = oudere firmware.
let chargeKeys: [(names: [String], size: UInt32, allow: [UInt8], inhibit: [UInt8])] = [
    (["CHTE"], 4, [0, 0, 0, 0], [1, 0, 0, 0]),
    (["CH0B", "CH0C"], 1, [0], [2]),
]

func fourCC(_ s: String) -> UInt32 { s.utf8.reduce(0) { $0 << 8 | UInt32($1) } }
func fourCCString(_ v: UInt32) -> String { String(decoding: [24, 16, 8, 0].map { UInt8(v >> $0 & 0xff) }, as: UTF8.self) }

/// SMC-bytes → getal. `flt ` little-endian (Apple Silicon); fpe2/ui16 big-endian.
func decode(_ type: String, _ b: [UInt8]) -> Double? {
    switch (type, b.count) {
    case ("flt ", 4): return Double(Float(bitPattern: UInt32(b[0]) | UInt32(b[1]) << 8 | UInt32(b[2]) << 16 | UInt32(b[3]) << 24))
    case ("fpe2", 2): return Double(UInt16(b[0]) << 8 | UInt16(b[1])) / 4
    case ("ui8 ", 1): return Double(b[0])
    case ("ui16", 2): return Double(UInt16(b[0]) << 8 | UInt16(b[1]))
    default: return nil
    }
}

/// Getal → SMC-bytes voor dit type; buiten bereik → nil.
func encode(_ type: String, _ v: Double) -> [UInt8]? {
    guard v.isFinite, v >= 0 else { return nil }
    switch type {
    case "flt ": let u = Float(v).bitPattern; return [UInt8(u & 0xff), UInt8(u >> 8 & 0xff), UInt8(u >> 16 & 0xff), UInt8(u >> 24)]
    case "fpe2": guard v * 4 <= 65_535 else { return nil }; let u = UInt16(v * 4); return [UInt8(u >> 8), UInt8(u & 0xff)]
    case "ui8 ": guard v <= 255 else { return nil }; return [UInt8(v)]
    case "ui16": guard v <= 65_535 else { return nil }; let u = UInt16(v); return [UInt8(u >> 8), UInt8(u & 0xff)]
    default: return nil
    }
}

func selfTest() {
    precondition(parse("status") == .status && parse("auto") == .auto, "basis")
    precondition(parse("set 0 2400") == .set(fan: 0, rpm: 2400) && parse("set 12 99999") == .set(fan: 12, rpm: 99999), "set")
    for bad in ["", "Status", "status ", " auto", "auto\n", "set", "set 0", "set 0 2400 1", "set  0 2400", "set -1 2400", "set 0 -5",
                "set 0 2400.5", "set 0 1e3", "set 123 100", "set 0 123456", "set 0 ２４００", "set 0 0x10", "set\t0 100",
                "status;rm -rf /", String(repeating: "a", count: 40), "set 0 2400\u{0}"] {
        precondition(parse(bad) == nil, "weigeren: \(bad.debugDescription)")
    }
    precondition(clampRPM(500, min: 1200, max: 5000) == 1200 && clampRPM(9000, min: 1200, max: 5000) == 5000
                 && clampRPM(2400, min: 1200, max: 5000) == 2400, "klemmen")
    precondition(clampRPM(2400, min: 0, max: 5000) == nil && clampRPM(2400, min: 3000, max: 2000) == nil
                 && clampRPM(2400, min: 1000, max: 50_000) == nil, "onzin-grenzen")
    for t in ["flt ", "fpe2", "ui16"] { precondition(decode(t, encode(t, 2400)!) == 2400, "heen en terug \(t)") }
    precondition(decode("fpe2", [0x25, 0x80]) == 2400 && encode("ui8 ", 1) == [1] && encode("ui8 ", 300) == nil
                 && encode("flt ", .nan) == nil && encode("sp78", 1) == nil, "coderen")
    precondition(fourCCString(fourCC("F0Md")) == "F0Md", "fourCC")
    // Laden
    precondition(parse("charge auto") == .chargeAuto && parse("charge pause") == .chargePause
                 && parse("charge limit 80") == .chargeLimit(80) && parse("charge limit 50") == .chargeLimit(50)
                 && parse("charge limit 100") == .chargeLimit(100), "charge")
    for bad in ["charge", "charge limit", "charge limit 49", "charge limit 101", "charge limit 0", "charge limit 080x", "charge limit -80",
                "charge limit 80 1", "charge limit 8O", "charge  auto", "charge auto ", "Charge auto", "charge limit 1000", "charge limit 80.5",
                "charge stop", "charge inhibit", "charge limit ８０", "charge limit\t80", "charge pause 1", "charge auto;reboot"] {
        precondition(parse(bad) == nil, "weigeren: \(bad.debugDescription)")
    }
    precondition(parse("charge limit 080") == .chargeLimit(80), "voorloopnul is nog steeds 80")
    precondition(mayInhibit(.chargeLimit(80), battery: 80, temp: nil) && mayInhibit(.chargeLimit(80), battery: 74, temp: nil)
                 && !mayInhibit(.chargeLimit(80), battery: 73, temp: nil) && !mayInhibit(.chargeLimit(80), battery: nil, temp: 50), "grens zelf gemeten")
    precondition(mayInhibit(.chargePause, battery: 10, temp: 41) && !mayInhibit(.chargePause, battery: 90, temp: 30)
                 && !mayInhibit(.chargePause, battery: 90, temp: nil) && !mayInhibit(.chargePause, battery: 90, temp: 200)
                 && !mayInhibit(.chargeAuto, battery: 100, temp: 50), "warmte zelf gemeten")
    precondition(chargeKeys.allSatisfy { $0.allow.count == Int($0.size) && $0.inhibit.count == Int($0.size) && $0.allow != $0.inhibit }, "laadsleutels")
    print("fand: ok")
}

// MARK: - SMC (lezen en schrijven)

final class SMC {
    private var conn: io_connect_t = 0
    private(set) var tempKeys: [(key: UInt32, size: UInt32)] = []

    init?() {
        let svc = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSMC"))
        defer { IOObjectRelease(svc) }
        guard svc != 0, IOServiceOpen(svc, mach_task_self_, 0, &conn) == KERN_SUCCESS else { return nil }
        // Temperatuursleutels: Tp* (snelle kernen), Te* (zuinige kernen), Tg* (grafische chip), type flt
        guard let info = call(9, key: fourCC("#KEY")), let count = call(5, key: fourCC("#KEY"), size: u32(info, 28)) else { return }
        let n = UInt32(count[48]) << 24 | UInt32(count[49]) << 16 | UInt32(count[50]) << 8 | UInt32(count[51])
        for i in 0..<Swift.min(n, 5000) {
            guard let k = call(8, index: i) else { continue }
            let key = u32(k, 0), a = UInt8(key >> 24), b = UInt8(key >> 16 & 0xff)
            guard a == UInt8(ascii: "T"), [UInt8(ascii: "p"), UInt8(ascii: "e"), UInt8(ascii: "g")].contains(b),
                  let ki = call(9, key: key), u32(ki, 32) == fourCC("flt ") else { continue }
            tempKeys.append((key, u32(ki, 28)))
        }
    }

    // SMCKeyData_t (80 bytes): key 0, keyInfo.dataSize 28, dataType 32, result 40, data8 (opdracht) 42, data32 44, bytes 48
    private func call(_ cmd: UInt8, key: UInt32 = 0, index: UInt32 = 0, size: UInt32 = 0, bytes: [UInt8] = []) -> [UInt8]? {
        var input = [UInt8](repeating: 0, count: 80), output = [UInt8](repeating: 0, count: 80)
        input.withUnsafeMutableBytes { b in
            b.storeBytes(of: key, toByteOffset: 0, as: UInt32.self)
            b.storeBytes(of: size, toByteOffset: 28, as: UInt32.self)
            b.storeBytes(of: cmd, toByteOffset: 42, as: UInt8.self)
            b.storeBytes(of: index, toByteOffset: 44, as: UInt32.self)
        }
        for (i, v) in bytes.prefix(32).enumerated() { input[48 + i] = v }
        var outSize = 80
        let r = IOConnectCallStructMethod(conn, 2, input, 80, &output, &outSize)
        return r == KERN_SUCCESS && output[40] == 0 ? output : nil
    }
    private func u32(_ b: [UInt8], _ o: Int) -> UInt32 { b.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: o, as: UInt32.self) } }

    func info(_ name: String) -> (size: UInt32, type: String)? {
        guard let i = call(9, key: fourCC(name)) else { return nil }
        return (u32(i, 28), fourCCString(u32(i, 32)))
    }
    func read(_ name: String) -> Double? {
        guard let i = info(name), i.size <= 32, let o = call(5, key: fourCC(name), size: i.size) else { return nil }
        return decode(i.type, Array(o[48..<48 + Int(i.size)]))
    }
    /// Opdracht 6 = schrijven; alleen als type en grootte van de sleutel bij de waarde passen.
    func write(_ name: String, _ v: Double) -> Bool {
        guard let i = info(name), let b = encode(i.type, v), b.count == Int(i.size) else { return false }
        return call(6, key: fourCC(name), size: i.size, bytes: b) != nil
    }
    /// Ruwe bytes (voor laadsleutels: vaste maat uit de whitelist).
    func readBytes(_ name: String, size: UInt32) -> [UInt8]? {
        guard info(name)?.size == size, let o = call(5, key: fourCC(name), size: size) else { return nil }
        return Array(o[48..<48 + Int(size)])
    }
    func writeBytes(_ name: String, _ b: [UInt8]) -> Bool {
        guard let i = info(name), i.size == UInt32(b.count) else { return false }
        return call(6, key: fourCC(name), size: i.size, bytes: b) != nil
    }
    /// Warmste accusensor (TB0T…TB2T), of nil.
    func batteryTemp() -> Double? { ["TB0T", "TB1T", "TB2T"].compactMap { read($0) }.filter { (-20...120).contains($0) }.max() }
    /// Warmste processor/grafische sensor, of nil als niets leesbaar is.
    func hottest() -> Double? {
        var best: Double?
        for k in tempKeys {
            guard let o = call(5, key: k.key, size: k.size) else { continue }
            let v = Double(o.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 48, as: Float.self) })
            if (10...130).contains(v) { best = Swift.max(best ?? v, v) }
        }
        return best
    }
}

// MARK: - Sturen

final class Fans {
    let smc: SMC
    let count: Int
    private(set) var forced = false, hot = false
    private var lastSet = 0.0

    init(_ smc: SMC) { self.smc = smc; count = Swift.min(Int(smc.read("FNum") ?? 0), 10) }

    /// Tijd die ook tijdens slaap doortelt (dus na wakker worden: meteen auto als de app stil was).
    static func now() -> Double { var ts = timespec(); clock_gettime(CLOCK_MONOTONIC, &ts); return Double(ts.tv_sec) + Double(ts.tv_nsec) / 1e9 }

    func auto() {
        for i in 0..<count { _ = smc.write("F\(i)Md", 0) }
        if smc.info("Ftst") != nil { _ = smc.write("Ftst", 0) }
        if forced { log.notice("fans back to macOS") }
        forced = false; hot = false
    }

    /// Handmatige stand aanzetten. Apple Silicon: lukt F{i}Md niet, dan eerst Ftst=1 (ontgrendelen) en even opnieuw proberen.
    private func manual(_ i: Int) -> Bool {
        if smc.read("F\(i)Md") == 1 || smc.write("F\(i)Md", 1) { return true }
        guard smc.write("Ftst", 1) else { return false }
        for _ in 0..<10 { usleep(300_000); if smc.write("F\(i)Md", 1) { return true } }
        return false
    }

    private func force(_ i: Int, _ rpm: Double) -> Bool {
        guard manual(i) else { return false }
        forced = true
        return smc.write("F\(i)Tg", rpm)
    }

    private func allMax() {
        for i in 0..<count { if let mx = smc.read("F\(i)Mx"), mx > 0 { _ = force(i, mx) } }
    }

    func handle(_ cmd: Command) -> String {
        switch cmd {
        case .status:
            return "ok fans=\(count) forced=\(forced ? 1 : 0) hot=\(hot ? 1 : 0) v=\(helperVersion)"
        case .auto:
            auto()
            return "ok"
        case let .set(fan, rpm):
            guard fan < count else { return "err fan" }
            guard !smc.tempKeys.isEmpty else { return "err no-sensors" }   // zonder temperatuur geen bewaking → niet sturen
            guard let mn = smc.read("F\(fan)Mn"), let mx = smc.read("F\(fan)Mx"), let v = clampRPM(rpm, min: mn, max: mx) else { return "err range" }
            lastSet = Self.now()
            if hot { return "ok hot" }   // te warm: blijft vol tot onder 90 °C
            guard force(fan, v) else { auto(); return "err write" }
            return "ok \(Int(v))"
        case .chargeAuto, .chargeLimit, .chargePause:
            return "err command"   // hoort bij Charge
        }
    }

    /// Elke seconde (alleen als wij sturen): stilte-wacht en hittebeveiliging.
    func tick() {
        guard forced else { return }
        if Self.now() - lastSet > quietTimeout { log.notice("no word from Fusebox for 30 s"); auto(); return }
        guard let t = smc.hottest() else { auto(); return }
        if t >= hotLimit {
            if !hot { log.notice("hot (\(t, format: .fixed(precision: 0)) °C): full speed") }
            hot = true
            allMax()
        } else if hot && t < coolAgain {
            hot = false   // de volgende "set" van de app zet de curve terug
        }
    }
}

// MARK: - Laden

final class Charge {
    let smc: SMC
    /// Welke whitelist-regel op deze Mac bestaat; nil = niet ondersteund.
    let key: (names: [String], size: UInt32, allow: [UInt8], inhibit: [UInt8])?
    private(set) var held = false
    private var lastWord = 0.0

    init(_ smc: SMC) {
        self.smc = smc
        key = chargeKeys.first { k in k.names.allSatisfy { smc.readBytes($0, size: k.size) != nil } }
    }
    var name: String { key?.names.joined(separator: "+") ?? "none" }

    /// Accu in % (AppleSmartBattery, zelf gelezen).
    static func battery() -> Double? {
        let b = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
        guard b != 0 else { return nil }
        defer { IOObjectRelease(b) }
        func n(_ k: String) -> Double? { (IORegistryEntryCreateCFProperty(b, k as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? NSNumber)?.doubleValue }
        guard let c = n("CurrentCapacity"), let m = n("MaxCapacity"), m > 0, c >= 0 else { return nil }
        return Swift.min(c / m * 100, 100)
    }

    /// Schrijven en teruglezen: pas "gelukt" als de SMC het ook echt zo heeft staan.
    private func put(_ inhibit: Bool) -> Bool {
        guard let k = key else { return false }
        let b = inhibit ? k.inhibit : k.allow
        return k.names.allSatisfy { smc.writeBytes($0, b) && smc.readBytes($0, size: k.size) == b }
    }

    /// Normaal laden (start, stoppen, stilte, "charge auto").
    func allow() {
        guard key != nil else { return }
        _ = put(false)
        if held { log.notice("charging back to normal") }
        held = false
    }

    func handle(_ cmd: Command) -> String {
        guard key != nil else { return "err unsupported" }
        lastWord = Fans.now()
        guard cmd != .chargeAuto else { allow(); return "ok charging" }
        guard mayInhibit(cmd, battery: Self.battery(), temp: smc.batteryTemp()) else { allow(); return "ok charging" }
        guard put(true) else { allow(); return "err write" }
        if !held { log.notice("charging held") }
        held = true
        return "ok held"
    }

    /// Elke seconde: 10 min niets gehoord → weer normaal laden.
    func tick() {
        if held && Fans.now() - lastWord > chargeTimeout { log.notice("no word from Fusebox for 10 min"); allow() }
    }
}

// MARK: - Socket

/// Versie van het protocol/de helper; de app vergelijkt dit en vraagt om "Reinstall" als hij ouder is.
let helperVersion = 3

/// Alleen root of wie er nu achter het scherm zit (eigenaar van /dev/console) mag de helper aansturen.
func allowed(_ c: Int32) -> Bool {
    var uid: uid_t = 0, gid: gid_t = 0
    guard getpeereid(c, &uid, &gid) == 0 else { return false }
    var st = stat()
    return uid == 0 || (stat("/dev/console", &st) == 0 && st.st_uid == uid)
}

/// Eén regel lezen (≤ 64 bytes, samen hooguit 0,3 s), afhandelen, antwoorden, dicht.
/// De totale limiet houdt de veiligheidslus (95 °C-check, 30 s-terugval) draaiende, ook bij een trage verbinding.
func serve(_ c: Int32, _ fans: Fans, _ charge: Charge) {
    guard allowed(c) else { let out = Array("err denied\n".utf8); _ = write(c, out, out.count); return }
    let start = Fans.now()
    var on: Int32 = 1, tv = timeval(tv_sec: 0, tv_usec: 100_000)
    setsockopt(c, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
    setsockopt(c, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    setsockopt(c, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
    var buf: [UInt8] = [], chunk = [UInt8](repeating: 0, count: 64)
    while buf.count <= 64, !buf.contains(10), Fans.now() - start < 0.3 {
        let n = read(c, &chunk, chunk.count)
        guard n > 0 else { break }
        buf += chunk[..<n]
    }
    var reply = "err command"
    if buf.count <= 64, let nl = buf.firstIndex(of: 10) {
        var line = Array(buf[..<nl])
        if line.last == 13 { line.removeLast() }
        if let cmd = parse(String(decoding: line, as: UTF8.self)) {
            switch cmd {
            case .chargeAuto, .chargeLimit, .chargePause: reply = charge.handle(cmd)
            case .status: reply = fans.handle(cmd).replacingOccurrences(of: " v=", with: " charge=\(charge.name) held=\(charge.held ? 1 : 0) v=")
            default: reply = fans.handle(cmd)
            }
        }
    }
    let out = Array((reply + "\n").utf8)
    _ = write(c, out, out.count)
}

var stop: Int32 = 0

func main() -> Int32 {
    if CommandLine.arguments.contains("--selftest") { selfTest(); return 0 }
    guard getuid() == 0 else { print("fusebox-fand runs as root via launchd (local.fusebox.fand)."); return 1 }
    guard let smc = SMC() else { log.error("no SMC"); return 1 }
    let fans = Fans(smc), charge = Charge(smc)
    fans.auto()   // schone start (ook na een crash: launchd KeepAlive start ons opnieuw)
    charge.allow()
    guard fans.count > 0 || charge.key != nil else { log.notice("no fans, no charge control: nothing to do"); while stop == 0 { sleep(3600) }; return 0 }

    signal(SIGPIPE, SIG_IGN)
    for s in [SIGTERM, SIGINT, SIGHUP] { signal(s) { _ in stop = 1 } }

    unlink(socketPath)
    let fd = socket(AF_UNIX, SOCK_STREAM, 0)
    guard fd >= 0 else { return 1 }
    var addr = sockaddr_un()
    addr.sun_family = sa_family_t(AF_UNIX)
    let path = Array(socketPath.utf8)
    withUnsafeMutableBytes(of: &addr.sun_path) { b in for (i, c) in path.enumerated() where i < b.count - 1 { b[i] = c } }
    let bound = withUnsafePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { bind(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
    guard bound == 0, chmod(socketPath, 0o666) == 0, listen(fd, 4) == 0 else { log.error("socket failed: \(errno)"); return 1 }
    log.notice("ready, \(fans.count) fan(s), \(smc.tempKeys.count) temperature sensors, charge control: \(charge.name)")

    var lastTick = 0.0
    while stop == 0 {
        var p = pollfd(fd: fd, events: Int16(POLLIN), revents: 0)
        if poll(&p, 1, 1000) > 0 {
            let c = accept(fd, nil, nil)
            if c >= 0 { serve(c, fans, charge); close(c) }
        }
        if Fans.now() - lastTick >= 1 { fans.tick(); charge.tick(); lastTick = Fans.now() }
    }
    fans.auto()
    charge.allow()
    close(fd)
    unlink(socketPath)
    return 0
}

exit(main())
