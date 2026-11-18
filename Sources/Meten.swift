import SwiftUI
import IOKit
import IOKit.ps
import Darwin
import Network

// MARK: - Meting

struct Sample {
    var time = Date()
    var cpu = 0.0, ram = 0.0, gpu = 0.0       // procent
    var ramUsedGB = 0.0
    var pressure = 1                           // geheugendruk: 1 normaal, 2 hoog, 4 kritiek
    var temp: Double?                          // warmste processorkern °C
    var gpuTemp: Double?                       // grafische chip °C
    var down = 0.0, up = 0.0                   // bytes per seconde
    var pingMs: Double?                        // reactietijd internet
    var pingPending = false                    // nog geen antwoord op de eerste ping (net opgestart): niets tonen i.p.v. rood "–"
    var diskFreeGB = 0.0, diskTotalGB = 0.0
    var diskRead = 0.0, diskWrite = 0.0        // bytes per seconde (SSD-activiteit)
    var swapGB = 0.0
    var wifiDbm: Int?                          // Wi-Fi-signaal; 0 = weggevallen, nil = geen Wi-Fi (WifiWatch)
    var battery: Double?                       // procent, nil op desktop
    var batteryMinutes: Int?                   // tot leeg (of vol bij laden)
    var charging = false
    var batteryW: Double?                      // vermogen van de accu in watt: + laden, − ontladen
    var adapterW: Int?                         // vermogen van de lader (watt), nil zonder lader
    var onAC = false                           // aan de lader (ook als hij niet laadt: vol, of oplaadlimiet)
    var fans: [FanReading] = []                // per ventilator; leeg op Macs zonder ventilator
    var fanRPM: Double?                        // gemiddelde toeren van alle ventilatoren
    var cores: [Double] = []                   // procent per kern (leeg bij de eerste meting)

    static let stamp: DateFormatter = { let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.dateFormat = "yyyy-MM-dd HH:mm:ss"; return f }()
    static let csvHeader = "tijd,cpu_pct,ram_pct,ram_gb,geheugendruk,gpu_pct,temp_c,gpu_temp_c,down_Bps,up_Bps,ping_ms,schijf_vrij_gb,batterij_pct,schijf_totaal_gb,lezen_Bps,schrijven_Bps,swap_gb,wifi_dbm\n"
    var csv: String {
        let f = { (v: Double) in String(format: "%.1f", v.isFinite ? v : 0) }
        let Int = { (v: Double) in v.isFinite ? Swift.Int(min(max(v, 0), 1e15)) : 0 }   // nooit crashen op een rare waarde
        return "\(Self.stamp.string(from: time)),\(f(cpu)),\(f(ram)),\(f(ramUsedGB)),\(pressure),\(f(gpu)),\(temp.map(f) ?? ""),\(gpuTemp.map(f) ?? ""),\(Int(down)),\(Int(up)),\(pingMs.map(f) ?? ""),\(f(diskFreeGB)),\(battery.map(f) ?? ""),\(f(diskTotalGB)),\(Int(diskRead)),\(Int(diskWrite)),\(String(format: "%.2f", swapGB)),\(wifiDbm.map(String.init) ?? "")\n"
    }

    /// Gemiddelde van een reeks (voor minuutpunten); niet-gemiddelde velden komen van de laatste.
    static func average(_ a: [Sample]) -> Sample {
        var s = a.last!
        let n = Double(a.count)
        func avg(_ k: KeyPath<Sample, Double>) -> Double { a.reduce(0) { $0 + $1[keyPath: k] } / n }
        s.time = a.first!.time
        s.cpu = avg(\.cpu); s.ram = avg(\.ram); s.gpu = avg(\.gpu); s.ramUsedGB = avg(\.ramUsedGB)
        s.down = avg(\.down); s.up = avg(\.up); s.diskRead = avg(\.diskRead); s.diskWrite = avg(\.diskWrite)
        func avgOpt(_ k: KeyPath<Sample, Double?>) -> Double? { let t = a.compactMap { $0[keyPath: k] }; return t.isEmpty ? nil : t.reduce(0, +) / Double(t.count) }
        s.temp = avgOpt(\.temp); s.gpuTemp = avgOpt(\.gpuTemp); s.pingMs = avgOpt(\.pingMs); s.fanRPM = avgOpt(\.fanRPM)
        s.pressure = a.map(\.pressure).max() ?? 1
        return s
    }
}

/// Eén ventilator: toeren per minuut (rpm) en de grenzen die macOS zelf aanhoudt.
struct FanReading {
    var rpm = 0.0, min = 0.0, max = 0.0, target = 0.0
    var forced = false                         // F{i}Md = 1: iemand (de helper) stuurt hem
}

/// Temperatuur via de SMC-chip (zelfde bron als iStat/Stats, geen sudo nodig).
/// Tp* = snelle kernen, Te* = zuinige kernen, Tg* = grafische chip.
final class SMC {
    private var conn: io_connect_t = 0
    private var cpuKeys: [(key: UInt32, size: UInt32)] = [], gpuKeys: [(key: UInt32, size: UInt32)] = []
    private var keyInfo: [UInt32: (size: UInt32, type: UInt32)] = [:]
    /// FNum: aantal ventilatoren (0 op o.a. MacBook Air).
    private(set) var fanCount = 0

    // SMCKeyData_t is 80 bytes; velden op vaste offsets (key 0, dataSize 28, dataType 32, data8 42, data32 44, bytes 48)
    private func call(_ cmd: UInt8, key: UInt32 = 0, index: UInt32 = 0, size: UInt32 = 0) -> [UInt8]? {
        var input = [UInt8](repeating: 0, count: 80), output = [UInt8](repeating: 0, count: 80)
        input.withUnsafeMutableBytes { b in
            b.storeBytes(of: key, toByteOffset: 0, as: UInt32.self)
            b.storeBytes(of: size, toByteOffset: 28, as: UInt32.self)
            b.storeBytes(of: cmd, toByteOffset: 42, as: UInt8.self)
            b.storeBytes(of: index, toByteOffset: 44, as: UInt32.self)
        }
        var outSize = 80
        let r = IOConnectCallStructMethod(conn, 2, input, 80, &output, &outSize)
        return r == KERN_SUCCESS && output[40] == 0 ? output : nil
    }
    private func u32(_ b: [UInt8], _ o: Int) -> UInt32 { b.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: o, as: UInt32.self) } }
    private static func code(_ s: String) -> UInt32 { s.utf8.reduce(0) { $0 << 8 | UInt32($1) } }

    /// `scanTemps: false` = alleen ventilatoren (snel; geen ronde langs alle sleutels).
    init(scanTemps: Bool = true) {
        let svc = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSMC"))
        defer { IOObjectRelease(svc) }
        guard svc != 0, IOServiceOpen(svc, mach_task_self_, 0, &conn) == KERN_SUCCESS else { return }
        fanCount = Swift.min(Int(value("FNum") ?? 0), 10)
        guard scanTemps else { return }
        // Eén keer alle sleutels doorlopen en de temperatuursleutels (type "flt ") onthouden
        guard let info = call(9, key: Self.code("#KEY")), let count = call(5, key: Self.code("#KEY"), size: u32(info, 28)) else { return }
        let n = UInt32(count[48]) << 24 | UInt32(count[49]) << 16 | UInt32(count[50]) << 8 | UInt32(count[51])
        let flt = Self.code("flt ")
        for i in 0..<n {
            guard let k = call(8, index: i) else { continue }
            let key = u32(k, 0)
            let a = UInt8(key >> 24), b = UInt8(key >> 16 & 0xff)
            guard a == UInt8(ascii: "T"), [UInt8(ascii: "p"), UInt8(ascii: "e"), UInt8(ascii: "g")].contains(b),
                  let ki = call(9, key: key), u32(ki, 32) == flt else { continue }
            let entry = (key: key, size: u32(ki, 28))
            if b == UInt8(ascii: "g") { gpuKeys.append(entry) } else { cpuKeys.append(entry) }
        }
    }

    private func maxTemp(_ keys: [(key: UInt32, size: UInt32)]) -> Double? {
        var best: Double?
        for k in keys {
            guard let o = call(5, key: k.key, size: k.size) else { continue }
            let v = Double(o.withUnsafeBytes { $0.loadUnaligned(fromByteOffset: 48, as: Float.self) })
            if (10...130).contains(v) { best = max(best ?? v, v) }
        }
        return best
    }

    deinit { if conn != 0 { IOServiceClose(conn) } }

    /// Eén sleutel lezen en omzetten naar een getal volgens zijn eigen type (flt/fpe2/ui8/ui16/ui32).
    func value(_ name: String) -> Double? {
        let key = Self.code(name)
        // Maat en type van een sleutel veranderen nooit: één keer opvragen (scheelt de helft van de SMC-aanroepen)
        if keyInfo[key] == nil, let i = call(9, key: key) { keyInfo[key] = (u32(i, 28), u32(i, 32)) }
        guard let info = keyInfo[key], let o = call(5, key: key, size: info.size) else { return nil }
        return Self.decode(type: info.type, Array(o[48..<48 + Int(Swift.min(info.size, 32))]))
    }

    /// SMC-bytes → getal. `flt ` is little-endian (Apple Silicon); fpe2/ui16/ui32 zijn big-endian (Intel-erfenis).
    static func decode(type: UInt32, _ b: [UInt8]) -> Double? {
        switch type {
        case code("flt ") where b.count == 4:
            return Double(Float(bitPattern: UInt32(b[0]) | UInt32(b[1]) << 8 | UInt32(b[2]) << 16 | UInt32(b[3]) << 24))
        case code("fpe2") where b.count == 2: return Double(UInt16(b[0]) << 8 | UInt16(b[1])) / 4
        case code("ui8 ") where b.count == 1: return Double(b[0])
        case code("ui16") where b.count == 2: return Double(UInt16(b[0]) << 8 | UInt16(b[1]))
        case code("ui32") where b.count == 4: return Double(UInt32(b[0]) << 24 | UInt32(b[1]) << 16 | UInt32(b[2]) << 8 | UInt32(b[3]))
        default: return nil
        }
    }
    static func decode(type: String, _ b: [UInt8]) -> Double? { decode(type: code(type), b) }

    /// Alle ventilatoren (een paar sleutels per stuk; goedkoop genoeg voor elke 2 s).
    func fans() -> [FanReading] {
        (0..<fanCount).compactMap { i in
            guard let rpm = value("F\(i)Ac") else { return nil }
            return FanReading(rpm: Swift.max(rpm, 0), min: value("F\(i)Mn") ?? 0, max: value("F\(i)Mx") ?? 0,
                              target: value("F\(i)Tg") ?? 0, forced: value("F\(i)Md") == 1)
        }
    }

    /// Warmste processorkern en warmste punt van de grafische chip, in °C.
    func read() -> (cpu: Double?, gpu: Double?) { (maxTemp(cpuKeys), maxTemp(gpuKeys)) }
    var keyCount: Int { cpuKeys.count + gpuKeys.count }
}

final class Sampler {
    private let host = mach_host_self()
    private let smc = SMC()
    private var lastTicks: (UInt32, UInt32, UInt32, UInt32)?
    private var lastCores: [UInt32]?
    private var lastNet: [String: (i: UInt32, o: UInt32)] = [:]
    private var lastTime = clock_gettime_nsec_np(CLOCK_MONOTONIC)   // telt slaap mee, springt niet als de klok verzet wordt
    private var diskFreeGB = 0.0, diskTotalGB = 0.0, diskAt = Date.distantPast
    private var lastIO: (r: UInt64, w: UInt64)?
    private var temp: (cpu: Double?, gpu: Double?) = (nil, nil), tempAt = Date.distantPast

    func sample() -> Sample {
        var s = Sample()
        let now = clock_gettime_nsec_np(CLOCK_MONOTONIC)
        let dt = max(Double(now &- lastTime) / 1e9, 0.001)
        lastTime = now

        // Processor: delta van de tick-tellers sinds vorige meting
        var load = host_cpu_load_info()
        var n = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info>.stride / MemoryLayout<integer_t>.stride)
        _ = withUnsafeMutablePointer(to: &load) { $0.withMemoryRebound(to: integer_t.self, capacity: Int(n)) { host_statistics(host, HOST_CPU_LOAD_INFO, $0, &n) } }
        let t = load.cpu_ticks // (user, system, idle, nice)
        if let l = lastTicks {
            let busy = Double((t.0 &- l.0) &+ (t.1 &- l.1) &+ (t.3 &- l.3))
            let total = busy + Double(t.2 &- l.2)
            s.cpu = total > 0 ? busy / total * 100 : 0
        }
        lastTicks = t

        // Per kern: zelfde delta, maar per processor (één goedkope mach-call; groot venster toont het altijd)
        do {
            var info: processor_info_array_t?, count = mach_msg_type_number_t(0), ncpu = natural_t(0)
            if host_processor_info(host, PROCESSOR_CPU_LOAD_INFO, &ncpu, &info, &count) == KERN_SUCCESS, let info {
                let now = (0..<Int(count)).map { UInt32(bitPattern: info[$0]) }
                vm_deallocate(mach_task_self_, vm_address_t(bitPattern: info), vm_size_t(Int(count) * MemoryLayout<integer_t>.stride))
                if let l = lastCores { s.cores = Self.coreLoad(now, l) }
                lastCores = now
            }
        }

        // Geheugen: zelfde formule als "Gebruikt geheugen" in Activiteitenweergave
        var vm = vm_statistics64()
        n = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.stride / MemoryLayout<integer_t>.stride)
        _ = withUnsafeMutablePointer(to: &vm) { $0.withMemoryRebound(to: integer_t.self, capacity: Int(n)) { host_statistics64(host, HOST_VM_INFO64, $0, &n) } }
        let pages = max(Int64(vm.internal_page_count) - Int64(vm.purgeable_count), 0) + Int64(vm.wire_count) + Int64(vm.compressor_page_count)
        let used = Double(pages) * Double(vm_kernel_page_size)
        s.ramUsedGB = used / 1_073_741_824
        s.ram = used / Double(ProcessInfo.processInfo.physicalMemory) * 100
        var level: Int32 = 1, size = MemoryLayout<Int32>.size
        if sysctlbyname("kern.memorystatus_vm_pressure_level", &level, &size, nil, 0) == 0 { s.pressure = Int(level) }

        s.gpu = Self.gpuUtilization()

        // Internet: bytes van fysieke interfaces (en*), delta per interface
        var ifap: UnsafeMutablePointer<ifaddrs>?
        if getifaddrs(&ifap) == 0 {
            var p = ifap
            var down: UInt64 = 0, up: UInt64 = 0
            var seen: [String: (i: UInt32, o: UInt32)] = [:]   // interfaces die verdwenen zijn vergeten
            while let a = p {
                p = a.pointee.ifa_next
                guard a.pointee.ifa_addr?.pointee.sa_family == UInt8(AF_LINK),
                      let d = a.pointee.ifa_data?.assumingMemoryBound(to: if_data.self) else { continue }
                let name = String(cString: a.pointee.ifa_name)
                guard name.hasPrefix("en") else { continue }
                let cur = (i: d.pointee.ifi_ibytes, o: d.pointee.ifi_obytes)
                // 32-bit tellers: &- vangt één wrap op. Een interface die opnieuw begint (USB-ethernet, tethering)
                // telt vanaf 0: dat zou een sprong van GB's lijken → dan deze keer niets meetellen.
                if let l = lastNet[name] {
                    let di = UInt64(cur.i &- l.i), dout = UInt64(cur.o &- l.o)
                    if Self.plausible(di, dt) { down += di }
                    if Self.plausible(dout, dt) { up += dout }
                }
                seen[name] = cur
            }
            lastNet = seen
            freeifaddrs(ifap)
            s.down = Double(down) / dt
            s.up = Double(up) / dt
        }

        // Temperatuur: elke 10 s is genoeg (verandert traag), in low impact elke 30 s. ponytail: leest alle ~73 SMC-sensoren (~14 ms), alleen Tp/Tg-subset als dat ooit te zwaar is
        if s.time.timeIntervalSince(tempAt) >= (LowImpact.on ? 30 : 10) { temp = smc.read(); tempAt = s.time }
        s.temp = temp.cpu
        s.gpuTemp = temp.gpu

        // Ventilatoren: elke 2 s (een handvol sleutels); Macs zonder ventilator slaan dit over
        if smc.fanCount > 0 {
            s.fans = smc.fans()
            if !s.fans.isEmpty { s.fanRPM = s.fans.reduce(0) { $0 + $1.rpm } / Double(s.fans.count) }
        }

        // Schijf: deze aanroep is duur (~10 ms), dus één keer per minuut
        if s.time.timeIntervalSince(diskAt) >= 60,
           let v = try? URL(fileURLWithPath: "/").resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey, .volumeTotalCapacityKey]),
           let free = v.volumeAvailableCapacityForImportantUsage {
            diskFreeGB = Double(free) / 1_000_000_000
            diskTotalGB = Double(v.volumeTotalCapacity ?? 0) / 1_000_000_000
            diskAt = s.time
        }
        s.diskFreeGB = diskFreeGB; s.diskTotalGB = diskTotalGB

        // SSD-activiteit: bytes gelezen/geschreven door alle schijven (delta)
        let io = Self.diskBytes()
        // Schijf uitgeworpen (DMG, USB, Time Machine): de som zakt → geen negatieve/enorme waarde, deze keer 0
        if let l = lastIO { s.diskRead = io.r >= l.r ? Double(io.r - l.r) / dt : 0; s.diskWrite = io.w >= l.w ? Double(io.w - l.w) / dt : 0 }
        lastIO = io

        // Swap: geheugen dat naar de SSD is uitgeweken
        var sw = xsw_usage(), swSize = MemoryLayout<xsw_usage>.size
        if sysctlbyname("vm.swapusage", &sw, &swSize, nil, 0) == 0 { s.swapGB = Double(sw.xsu_used) / 1_073_741_824 }
        Self.battery(into: &s)
        return s
    }

    /// Meer dan 10 Gbit/s over het netwerk is een herstarte teller, geen echt verkeer.
    static func plausible(_ bytes: UInt64, _ dt: Double) -> Bool { Double(bytes) / dt < 1.25e9 }

    /// Tick-tellers (user, system, idle, nice per kern, plat achter elkaar) → procent bezet per kern.
    static func coreLoad(_ now: [UInt32], _ last: [UInt32]) -> [Double] {
        guard now.count == last.count else { return [] }
        let st = Int(CPU_STATE_MAX)
        return stride(from: 0, to: now.count - st + 1, by: st).map { i in
            let d = (0..<st).map { Double(now[i + $0] &- last[i + $0]) }
            let total = d.reduce(0, +)
            return total > 0 ? (total - d[Int(CPU_STATE_IDLE)]) / total * 100 : 0
        }
    }

    /// Aantal zuinige kernen; die staan vooraan in de processorlijst (gecontroleerd via ioreg cluster-type). 0 = geen P/E-indeling.
    static let efficiencyCores: Int = {
        var n: Int32 = 0, size = MemoryLayout<Int32>.size
        return sysctlbyname("hw.perflevel1.logicalcpu", &n, &size, nil, 0) == 0 ? Int(n) : 0
    }()

    static func diskBytes() -> (r: UInt64, w: UInt64) {
        var it: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOBlockStorageDriver"), &it) == KERN_SUCCESS else { return (0, 0) }
        defer { IOObjectRelease(it) }
        var r: UInt64 = 0, w: UInt64 = 0
        while case let e = IOIteratorNext(it), e != 0 {
            if let st = IORegistryEntryCreateCFProperty(e, "Statistics" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? [String: Any] {
                r += (st["Bytes (Read)"] as? NSNumber)?.uint64Value ?? 0
                w += (st["Bytes (Write)"] as? NSNumber)?.uint64Value ?? 0
            }
            IOObjectRelease(e)
        }
        return (r, w)
    }

    static func gpuUtilization() -> Double {
        var it: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOAccelerator"), &it) == KERN_SUCCESS else { return 0 }
        defer { IOObjectRelease(it) }
        var best = 0.0
        while case let e = IOIteratorNext(it), e != 0 {
            if let stats = IORegistryEntryCreateCFProperty(e, "PerformanceStatistics" as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? [String: Any],
               let u = stats["Device Utilization %"] as? NSNumber {
                best = max(best, u.doubleValue)
            }
            IOObjectRelease(e)
        }
        return best
    }

    static func battery(into s: inout Sample) {
        let info = IOPSCopyPowerSourcesInfo().takeRetainedValue()
        let list = IOPSCopyPowerSourcesList(info).takeRetainedValue() as [CFTypeRef]
        for ps in list {
            guard let d = IOPSGetPowerSourceDescription(info, ps)?.takeUnretainedValue() as? [String: Any],
                  let c = d[kIOPSCurrentCapacityKey] as? Int, let m = d[kIOPSMaxCapacityKey] as? Int, m > 0 else { continue }
            s.battery = Double(c) / Double(m) * 100
            s.charging = d[kIOPSIsChargingKey] as? Bool ?? false
            let mins = d[s.charging ? kIOPSTimeToFullChargeKey : kIOPSTimeToEmptyKey] as? Int ?? -1
            s.batteryMinutes = mins > 0 ? mins : nil
            s.onAC = d[kIOPSPowerSourceStateKey] as? String == kIOPSACPowerValue
            if s.onAC { s.adapterW = (IOPSCopyExternalPowerAdapterDetails()?.takeRetainedValue() as? [String: Any])?[kIOPSPowerAdapterWattsKey] as? Int }
            // Accuvermogen: stroom (mA, met teken) × spanning (mV) uit AppleSmartBattery; één registry-opzoeking
            let b = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
            if b != 0 {
                defer { IOObjectRelease(b) }
                func n(_ k: String) -> NSNumber? { IORegistryEntryCreateCFProperty(b, k as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() as? NSNumber }
                // macOS meldt de stroom soms een tijdje als 0 (meter nog niet bijgewerkt): dan geen getal i.p.v. "0 W"
                if let a = n("Amperage"), a.int64Value != 0, let v = n("Voltage") { s.batteryW = Double(a.int64Value) * v.doubleValue / 1e6 }   // UInt64-bitpatroon → Int64 = negatief bij ontladen
            }
            return
        }
    }
}

// MARK: - Opmaak

func rate(_ bps: Double) -> String { bytes(bps) + "/s" }
func pct(_ v: Double) -> String { "\(Int(v.rounded()))%" }
func one(_ v: Double) -> String { v.formatted(.number.precision(.fractionLength(1)).locale(Locale(identifier: "en_US"))) }
/// Bytes decimaal (1000), altijd met punt: 640 KB, 12.3 MB, 1.2 GB. (ByteCountFormatter volgt de systeemtaal.)
func bytes(_ b: Double) -> String {
    b < 1e6 ? "\(Int(b / 1e3)) KB" : b < 1e9 ? "\(one(b / 1e6)) MB" : "\(one(b / 1e9)) GB"
}
func duration(_ minutes: Int) -> String {
    if minutes >= 1440 { return "\(minutes / 1440)d \(minutes % 1440 / 60)h" }
    return minutes >= 60 ? "\(minutes / 60)h \(minutes % 60)m" : "\(minutes)m"
}

/// Gemiddelt een reeks terug naar hoogstens n punten, zodat tekenen goedkoop blijft.
func downsample(_ v: [Double], to n: Int = 240) -> [Double] {
    guard v.count > n else { return v }
    let size = Double(v.count) / Double(n)
    return (0..<n).map { i in
        let slice = v[Int(Double(i) * size)..<min(Int(Double(i + 1) * size), v.count)]
        return slice.reduce(0, +) / Double(max(slice.count, 1))
    }
}


/// Reactietijd (ms) via een TCP-verbinding naar een vaste server: zelfde orde als "ping", zonder root.
final class Pinger {
    private(set) var lastMs: Double?
    private(set) var answered = false   // eerste ping klaar (gelukt of niet)
    private var busy = false
    func ping(host: String) {
        guard !busy else { return }
        busy = true
        let start = DispatchTime.now()
        let c = NWConnection(host: NWEndpoint.Host(host), port: 443, using: .tcp)
        var done = false
        func finish(_ ms: Double?) { guard !done else { return }; done = true; lastMs = ms; answered = true; busy = false; c.cancel() }
        c.stateUpdateHandler = { state in
            switch state {
            case .ready: finish(Double(DispatchTime.now().uptimeNanoseconds - start.uptimeNanoseconds) / 1e6)
            case .failed, .waiting: finish(nil)
            default: break
            }
        }
        c.start(queue: .main)
        DispatchQueue.main.asyncAfter(deadline: .now() + 3) { finish(nil) } // geen internet → "–"
    }
}
