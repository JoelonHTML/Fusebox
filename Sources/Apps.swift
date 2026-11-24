import SwiftUI
import Darwin
import IOKit

// MARK: - Programma's meten (per app, niet per proces)

/// Eén regel in de Programma's-lijst: een app met al zijn hulpprocessen samen, of een achtergrond-/systeemonderdeel.
struct AppUsage: Identifiable {
    enum Kind: String { case app = "app", background = "achtergrond", macos = "macos" }
    let id: String            // app-bundelpad of procesnaam
    let name: String          // wat de gebruiker ziet: "Chrome", "Spotlight (zoeken)"
    let explanation: String   // korte uitleg in gewone taal (leeg voor gewone apps)
    let kind: Kind
    var pids: [pid_t]
    var cpu: Double           // % van de hele Mac (alle kernen samen = 100%)
    var memGB: Double
    var energyW: Double       // geschat vermogen in watt
    let icon: NSImage?
    var diskRead = 0.0, diskWrite = 0.0   // bytes per seconde (lezen/schrijven van de SSD)
    var netIn = 0.0, netOut = 0.0         // bytes per seconde internet (alleen met "netPerApp" aan)
    var gpu = 0.0                         // % GPU-tijd (tellers van de GPU-driver per proces)
    var threads = 0
    var started: Date?                    // vroegste start van de processen in deze groep
    var efficient = false                 // efficiëntiemodus: alle processen in de achtergrondstand
    var canStop: Bool { kind != .macos && pids.first.map { $0 > 0 } == true && id != AppUsage.selfID }
    var runningApp: NSRunningApplication? { pids.lazy.compactMap { NSRunningApplication(processIdentifier: $0) }.first { $0.activationPolicy == .regular } }

    static let selfID = Bundle.main.bundlePath
    /// gpu_pct en mac_energie_w (som van alle processen die minuut) sinds de app-pagina; oudere logs missen ze.
    static let csvHeader = "tijd,app,soort,cpu_pct,geheugen_gb,energie_w,gpu_pct,mac_energie_w\n"
}

/// Efficiëntiemodus (zoals in Windows): de app blijft open, maar macOS zet zijn processen in de achtergrondstand
/// (PRIO_DARWIN_BG): alleen de zuinige kernen, schijf en netwerk op een laag pitje. Alleen processen van de gebruiker zelf.
/// macOS onthoudt het niet: start de app opnieuw, dan is het weer uit.
enum Efficiency {
    /// Nooit afremmen: wat het scherm, inloggen en het bureaublad draaiend houdt (Fusebox zelf valt al af via canStop).
    static let protected: Set<String> = ["WindowServer", "kernel_task", "loginwindow", "Finder", "Dock"]
    /// PROC_FLAG_EXT_DARWINBG: door een ander proces in de achtergrondstand gezet. getpriority(PRIO_DARWIN_PROCESS, pid)
    /// geeft voor andere processen altijd 0 (gemeten op macOS 26), en de prioriteit zelf (4) haalt App Nap ook: dus deze vlag.
    private static let extBG: UInt32 = 0x10000

    private static func info(_ pid: pid_t) -> proc_bsdinfo? {
        var bi = proc_bsdinfo()
        return proc_pidinfo(pid, PROC_PIDTBSDINFO, 0, &bi, Int32(MemoryLayout<proc_bsdinfo>.size)) > 0 ? bi : nil
    }

    static func isOn(_ pid: pid_t) -> Bool { info(pid).map { $0.pbi_flags & extBG != 0 } ?? false }

    static func allowed(_ app: AppUsage) -> Bool { allowed(id: app.id, name: app.name, canStop: app.canStop) }

    static func allowed(id: String, name: String, canStop: Bool) -> Bool {
        let base = ((id as NSString).lastPathComponent as NSString).deletingPathExtension
        return canStop && !protected.contains(base) && !protected.contains(name)
    }

    /// Zet of wist de achtergrondstand voor de processen van deze gebruiker (andere slaat het over). true = allemaal gelukt.
    static func set(_ pids: [pid_t], on: Bool) -> Bool {
        let own = pids.filter { info($0)?.pbi_uid == getuid() }
        return !own.isEmpty && own.allSatisfy { setpriority(PRIO_DARWIN_PROCESS, id_t($0), on ? PRIO_DARWIN_BG : 0) == 0 }
    }

    static func selfTest() {
        precondition(allowed(id: "/Applications/Safari.app", name: "Safari", canStop: true), "gewone app")
        precondition(!allowed(id: "/System/Library/CoreServices/Finder.app", name: "Finder", canStop: true), "Finder")
        precondition(!allowed(id: "/System/Library/PrivateFrameworks/SkyLight.framework/Resources/WindowServer", name: "WindowServer", canStop: true), "WindowServer")
        precondition(!allowed(id: "/Applications/Safari.app", name: "Safari", canStop: false), "niet stopbaar = niet afremmen")
        precondition(!set([], on: true) && !set([1], on: true), "launchd (root) wordt overgeslagen")
        print("efficiëntiemodus: ok")
    }
}

/// Meet alle processen via proc_pid_rusage en groepeert ze per verantwoordelijke app.
/// Processen van andere gebruikers (root) mag alleen macOS meten: die komen samen in de regel "macOS zelf".
/// `measure` (en alles wat het bijhoudt) draait op `Meter.work`; alleen `apps` is van de hoofdthread.
final class AppMonitor: ObservableObject {
    @Published var apps: [AppUsage] = []      // hoogste processor eerst; gezet door Meter
    private var last: [pid_t: (cpu: UInt64, energy: UInt64, rd: UInt64, wr: UInt64, gpu: UInt64)] = [:]
    private var lastTime = DispatchTime.now()
    private var info: [pid_t: (key: String, path: String)] = [:]
    private var names: [String: (name: String, explanation: String, kind: AppUsage.Kind, icon: NSImage?)] = [:]
    private let cores = Double(ProcessInfo.processInfo.activeProcessorCount)
    private let tick: Double = { var t = mach_timebase_info(); mach_timebase_info(&t); return Double(t.numer) / Double(t.denom) }()
    private typealias Resp = @convention(c) (pid_t) -> pid_t
    private let responsible: Resp? = dlsym(dlopen(nil, RTLD_NOW), "responsibility_get_pid_responsible_for_pid").map { unsafeBitCast($0, to: Resp.self) }
    // Internet per proces via nettop: Programs-pagina (elke meting) en Live (minuutmeting, zolang iemand kijkt),
    // alleen met de instelling aan. `netMaxAge`: hoe oud de vorige nettop-telling mag zijn om een snelheid te geven.
    var network: Bool
    private let netMaxAge: Double
    private var netBytes: [pid_t: (UInt64, UInt64)] = [:], netRate: [pid_t: (Double, Double)] = [:]
    private var netTime = DispatchTime.now(), netBusy = false

    init(network: Bool = false, netMaxAge: Double = 10) { self.network = network; self.netMaxAge = netMaxAge }

    /// Eén meting; percentages gelden voor de tijd sinds de vorige aanroep.
    /// `totalCPU`/`totalMemGB` (van de hele Mac) bepalen de restpost "macOS zelf".
    func measure(totalCPU: Double, totalMemGB: Double) -> [AppUsage] {
        let now = DispatchTime.now()
        let dt = Double(now.uptimeNanoseconds - lastTime.uptimeNanoseconds) / 1e9
        lastTime = now
        var count = proc_listallpids(nil, 0)
        var pids = [pid_t](repeating: 0, count: Int(count) + 64)
        count = proc_listallpids(&pids, Int32(pids.count * MemoryLayout<pid_t>.size))
        pids = Array(pids.prefix(Int(max(count, 0))))

        var groups: [String: AppUsage] = [:]
        var seen: [pid_t: (cpu: UInt64, energy: UInt64, rd: UInt64, wr: UInt64, gpu: UInt64)] = [:]
        let nowAbs = mach_absolute_time(), nowDate = Date()
        if network { pollNet() }
        let gpuNs = Self.gpuTimes()
        for pid in pids where pid > 0 {
            var ri = rusage_info_v6()
            let ok = withUnsafeMutablePointer(to: &ri) { p in
                p.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V6, $0) }
            }
            guard ok == 0 else { continue } // niet van ons: telt mee in "macOS zelf"
            let cpuNs = UInt64(Double(ri.ri_user_time + ri.ri_system_time) * tick)
            let gpuTotal = gpuNs[pid] ?? 0
            seen[pid] = (cpuNs, ri.ri_energy_nj, ri.ri_diskio_bytesread, ri.ri_diskio_byteswritten, gpuTotal)
            let (key, _) = identity(pid)
            let meta = describe(key: key, pid: pid)
            let prev = last[pid]
            // Teller lager dan vorige keer (pid hergebruikt / exec): als nieuw proces behandelen, niet laten omslaan
            func delta(_ a: UInt64, _ b: UInt64) -> Double { a >= b ? Double(a - b) : 0 }
            let cpu = prev.map { dt > 0 ? delta(cpuNs, $0.cpu) / 1e9 / dt / cores * 100 : 0 } ?? 0
            let watt = prev.map { dt > 0 ? delta(ri.ri_energy_nj, $0.energy) / 1e9 / dt : 0 } ?? 0
            let rd = prev.map { dt > 0 ? delta(ri.ri_diskio_bytesread, $0.rd) / dt : 0 } ?? 0
            let wr = prev.map { dt > 0 ? delta(ri.ri_diskio_byteswritten, $0.wr) / dt : 0 } ?? 0
            let gpu = prev.map { dt > 0 ? delta(gpuTotal, $0.gpu) / 1e9 / dt * 100 : 0 } ?? 0
            var ti = proc_taskinfo()
            let threads = proc_pidinfo(pid, PROC_PIDTASKINFO, 0, &ti, Int32(MemoryLayout<proc_taskinfo>.size)) > 0 ? Int(ti.pti_threadnum) : 0
            let start = nowDate.addingTimeInterval(-Double(nowAbs &- ri.ri_proc_start_abstime) * tick / 1e9)
            let mem = Double(ri.ri_phys_footprint) / 1_073_741_824
            let net = netRate[pid] ?? (0, 0)
            let bg = Efficiency.isOn(pid)
            if var g = groups[key] {
                g.pids.append(pid); g.efficient = g.efficient && bg; g.cpu += cpu; g.memGB += mem; g.energyW += watt
                g.diskRead += rd; g.diskWrite += wr; g.netIn += net.0; g.netOut += net.1; g.gpu += gpu
                g.threads += threads; g.started = min(g.started ?? start, start)
                groups[key] = g
            } else {
                groups[key] = AppUsage(id: key, name: meta.name, explanation: meta.explanation, kind: meta.kind,
                                       pids: [pid], cpu: cpu, memGB: mem, energyW: watt, icon: meta.icon,
                                       diskRead: rd, diskWrite: wr, netIn: net.0, netOut: net.1, gpu: gpu, threads: threads, started: start, efficient: bg)
            }
        }
        last = seen
        info = info.filter { seen[$0.key] != nil }

        // Wat we niet zelf konden meten (WindowServer, kernel, root-processen) = totaal min de som
        let measuredCPU = groups.values.reduce(0) { $0 + $1.cpu }, measuredMem = groups.values.reduce(0) { $0 + $1.memGB }
        groups["macos"] = AppUsage(id: "macos", name: "macOS itself", explanation: L("Drawing the screen, the kernel and system services only macOS is allowed to measure"),
                                   kind: .macos, pids: [], cpu: max(totalCPU - measuredCPU, 0), memGB: max(totalMemGB - measuredMem, 0),
                                   energyW: 0, icon: NSImage(systemSymbolName: "apple.logo", accessibilityDescription: nil))
        // nettop ziet ook root-processen: dat verkeer hoort bij "macOS zelf"
        for (pid, n) in netRate where seen[pid] == nil { groups["macos"]?.netIn += n.0; groups["macos"]?.netOut += n.1 }
        return groups.values.sorted { $0.cpu == $1.cpu ? $0.memGB > $1.memGB : $0.cpu > $1.cpu }
    }

    /// GPU-tijd (ns) per pid: elke GPU-client in de IORegistry (kind van een IOAccelerator) heeft "AppUsage"
    /// met "accumulatedGPUTime", en "IOUserClientCreator" = "pid 123, naam". Zelfde bron als `ioreg`, geen toestemming nodig.
    /// Niet gedocumenteerd: ontbreekt het (andere GPU, ander macOS), dan is alles 0. ~3 ms per keer.
    /// Geeft tijden én namen terug, zonder gedeelde tabel: de meetdraad en de hoofddraad (eigen-verbruik-log) roepen
    /// dit allebei aan, en een gedeelde `gpuNames` werd dan tegelijk beschreven (crash in 0.13.1).
    static func gpuTimes() -> [pid_t: UInt64] { gpuUsage().times }
    static func gpuUsage() -> (times: [pid_t: UInt64], names: [pid_t: String]) {
        var out: [pid_t: UInt64] = [:], gpuNames: [pid_t: String] = [:], it: io_iterator_t = 0
        guard IOServiceGetMatchingServices(kIOMainPortDefault, IOServiceMatching("IOAccelerator"), &it) == KERN_SUCCESS else { return (out, gpuNames) }
        defer { IOObjectRelease(it) }
        while case let acc = IOIteratorNext(it), acc != 0 {
            defer { IOObjectRelease(acc) }
            var ch: io_iterator_t = 0
            guard IORegistryEntryGetChildIterator(acc, kIOServicePlane, &ch) == KERN_SUCCESS else { continue }
            defer { IOObjectRelease(ch) }
            while case let c = IOIteratorNext(ch), c != 0 {
                defer { IOObjectRelease(c) }
                func prop(_ k: String) -> Any? { IORegistryEntryCreateCFProperty(c, k as CFString, kCFAllocatorDefault, 0)?.takeRetainedValue() }
                guard let usage = prop("AppUsage") as? [[String: Any]], !usage.isEmpty,
                      let pid = (prop("IOUserClientCreator") as? String).flatMap(creatorPid) else { continue }
                out[pid, default: 0] += usage.reduce(0) { $0 + (($1["accumulatedGPUTime"] as? NSNumber)?.uint64Value ?? 0) }
                if let c = prop("IOUserClientCreator") as? String, let n = c.split(separator: ",", maxSplits: 1).last {
                    gpuNames[pid] = n.trimmingCharacters(in: .whitespaces)   // "pid 411, WindowServer" → naam (proc_name mag niet bij root-processen)
                }
            }
        }
        return (out, gpuNames)
    }

    /// "pid 35649, WindowServer" → 35649
    static func creatorPid(_ s: String) -> pid_t? {
        guard s.hasPrefix("pid ") else { return nil }
        return pid_t(s.dropFirst(4).prefix { $0.isNumber })
    }

    /// Start om de ~4 s één nettop-meting op de achtergrond; de snelheden komen in de volgende meting mee.
    /// Kost per run ~10 ms processor (gemeten op M-serie), dus ~0,3% van één kern.
    private func pollNet() {
        // Low impact: geen nettop (elke paar seconden een los proces starten is het duurste van deze pagina)
        guard UserDefaults.standard.bool(forKey: "netPerApp"), !LowImpact.on else { netBytes = [:]; netRate = [:]; return }
        let age = Double(DispatchTime.now().uptimeNanoseconds - netTime.uptimeNanoseconds) / 1e9
        if age > netMaxAge { netRate = [:] }   // pagina was dicht: oude snelheden niet meer tonen
        guard !netBusy, age >= 3 || netBytes.isEmpty else { return }
        netBusy = true
        DispatchQueue.global(qos: .utility).async { [self] in
            let out = Diag.run("/usr/bin/nettop", ["-P", "-L", "1", "-n", "-x", "-J", "bytes_in,bytes_out"], timeout: 2)
            let bytes = Self.parseNettop(out), now = DispatchTime.now()
            Meter.work.async { [self] in
                let dt = Double(now.uptimeNanoseconds - netTime.uptimeNanoseconds) / 1e9
                netRate = dt <= netMaxAge ? Self.netRates(netBytes, bytes, dt: dt) : [:]   // te oud = alleen nieuw nulpunt
                netBytes = bytes; netTime = now; netBusy = false
            }
        }
    }

    /// nettop -L (CSV): kopregel ",bytes_in,bytes_out," en daarna "naam.pid,in,uit," (totaal sinds start van het proces).
    static func parseNettop(_ out: String) -> [pid_t: (UInt64, UInt64)] {
        var r: [pid_t: (UInt64, UInt64)] = [:]
        for line in out.split(separator: "\n") {
            let f = line.split(separator: ",")   // van achteren lezen: de naam kan zelf een komma bevatten
            guard f.count >= 3, let i = UInt64(f[f.count - 2]), let o = UInt64(f[f.count - 1]),
                  let pid = f[f.count - 3].split(separator: ".").last.flatMap({ pid_t($0) }) else { continue }
            r[pid] = (i, o)
        }
        return r
    }

    /// Bytes per seconde per pid; nieuwe of hergebruikte pids (teller lager dan eerst) tellen niet mee.
    static func netRates(_ old: [pid_t: (UInt64, UInt64)], _ new: [pid_t: (UInt64, UInt64)], dt: Double) -> [pid_t: (Double, Double)] {
        guard dt > 0 else { return [:] }
        return new.reduce(into: [:]) { r, e in
            guard let p = old[e.key], e.value.0 >= p.0, e.value.1 >= p.1 else { return }
            r[e.key] = (Double(e.value.0 - p.0) / dt, Double(e.value.1 - p.1) / dt)
        }
    }

    /// Stopt een programma netjes (zoals ⌘Q); achtergrondprocessen krijgen SIGTERM. Geeft false als het niet mag.
    @discardableResult
    func stop(_ app: AppUsage) -> Bool {
        guard app.canStop else { return false }
        if let r = app.runningApp { return r.terminate() }
        return app.pids.allSatisfy { kill($0, SIGTERM) == 0 }
    }

    /// Geforceerd stoppen (zoals Forceer stoppen in macOS): de app krijgt geen kans om te bewaren.
    @discardableResult
    func forceStop(_ app: AppUsage) -> Bool {
        guard app.canStop else { return false }
        if let r = app.runningApp { return r.forceTerminate() }
        return app.pids.allSatisfy { kill($0, SIGKILL) == 0 }
    }

    /// Efficiëntiemodus aan/uit voor alle eigen processen van de app; de lijst toont het meteen (niet pas bij de volgende meting).
    func setEfficient(_ app: AppUsage, _ on: Bool) {
        guard Efficiency.allowed(app), Efficiency.set(app.pids, on: on), let i = apps.firstIndex(where: { $0.id == app.id }) else { return }
        apps[i].efficient = on
    }

    // Groepeer op de app die verantwoordelijk is (WebContent → Safari, Chrome Helper → Chrome)
    private func identity(_ pid: pid_t) -> (String, String) {
        if let i = info[pid] { return (i.key, i.path) }
        let owner = responsible?(pid) ?? pid
        let path = Self.path(owner > 0 ? owner : pid).isEmpty ? Self.path(pid) : Self.path(owner > 0 ? owner : pid)
        let key: String
        if let r = path.range(of: ".app/") { key = String(path[..<r.lowerBound]) + ".app" }
        else { key = path.isEmpty ? "pid\(pid)" : path }
        info[pid] = (key, path)
        return (key, path)
    }

    private static func path(_ pid: pid_t) -> String {
        var buf = [CChar](repeating: 0, count: 4096)
        return proc_pidpath(pid, &buf, UInt32(buf.count)) > 0 ? String(cString: buf) : ""
    }

    private func describe(key: String, pid: pid_t) -> (name: String, explanation: String, kind: AppUsage.Kind, icon: NSImage?) {
        if let n = names[key] { return n }
        let system = ["/System/", "/usr/", "/sbin/", "/bin/", "/Library/Apple/"].contains { key.hasPrefix($0) }
        let r: (String, String, AppUsage.Kind, NSImage?)
        if key.hasSuffix(".app") {
            let name = FileManager.default.displayName(atPath: key).replacingOccurrences(of: ".app", with: "")
            let isRegular = Bundle(path: key)?.object(forInfoDictionaryKey: "LSUIElement") == nil && !key.contains("/CoreServices/") && !key.contains("/Library/Frameworks/")
            r = (name, "", system && !isRegular ? .macos : (isRegular ? .app : .background), NSWorkspace.shared.icon(forFile: key))
        } else {
            // procesnaam van de kernel; padnaam kan "(deleted)" zijn als het bestand vervangen is
            var buf = [CChar](repeating: 0, count: 256)
            let proc = proc_name(pid, &buf, UInt32(buf.count)) > 0 ? String(cString: buf) : (key as NSString).lastPathComponent
            let friendly = Self.friendly.first { proc.hasPrefix($0.key) }?.value
            r = (friendly?.0 ?? proc, friendly?.1 ?? (system ? L("Part of macOS") : L("Background process")), system ? .macos : .background,
                 NSImage(systemSymbolName: system ? "gearshape" : "terminal", accessibilityDescription: nil))
        }
        names[key] = r
        return r
    }

    /// Gewone namen voor bekende macOS-processen.
    static let friendly: [String: (String, String)] = [
        "mds": ("Spotlight (search)", L("Indexes your files so searching is fast")),
        "mdworker": ("Spotlight (search)", L("Reads new files for Spotlight")),
        "corespotlightd": ("Spotlight (search)", L("Indexes content from apps")),
        "photoanalysisd": ("Photo analysis", L("Recognizes faces and objects in your photos")),
        "photolibraryd": ("Photo library", L("Manages your Photos library")),
        "mediaanalysisd": ("Media analysis", L("Analyzes photos and videos in the background")),
        "cloudd": ("iCloud", L("Syncs with iCloud")),
        "bird": ("iCloud Drive", L("Syncs your iCloud Drive files")),
        "fileproviderd": ("iCloud Drive", L("Keeps track of files in the cloud")),
        "backupd": ("Time Machine", L("Making a backup")),
        "softwareupdated": ("Software update", L("Checks for or installs updates")),
        "coreaudiod": ("Sound", L("Plays audio")),
        "bluetoothd": ("Bluetooth", L("Connects to Bluetooth devices")),
        "nsurlsessiond": ("Background downloads", L("Downloads for apps that aren't open")),
        "trustd": ("Security", L("Checks certificates")),
        "syspolicyd": ("Security", L("Checks whether apps are safe")),
        "XProtect": ("Virus scanner", L("Apple's built-in malware protection")),
        "WindowManager": ("Window management", L("Stage Manager and windows")),
        "sharingd": ("AirDrop and sharing", L("AirDrop, Handoff and sharing")),
        "locationd": ("Location", L("Determines your location for apps")),
        "Siri": ("Siri", L("Siri and suggestions")),
        "suggestd": ("Siri suggestions", L("Learns from your usage for suggestions")),
        "logd": ("Logging", L("Writes system messages to the log")),
        "spindump": ("Hang analysis", L("Investigates an app that froze")),
        "ReportCrash": ("Crash report", L("Handles an app that crashed")),
        "Finder": ("Finder", L("Files and desktop")),
        "Dock": ("Dock", L("The Dock and Mission Control")),
        "SystemUIServer": ("Menu bar", L("Icons in the menu bar")),
        "ControlCenter": ("Control Center", L("Control Center and menu bar")),
        "NotificationCenter": ("Notification Center", L("Notifications and widgets")),
    ]
}
