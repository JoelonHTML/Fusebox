import SwiftUI
import Darwin

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
    var threads = 0
    var started: Date?                    // vroegste start van de processen in deze groep
    var canStop: Bool { kind != .macos && pids.first.map { $0 > 0 } == true && id != AppUsage.selfID }
    var runningApp: NSRunningApplication? { pids.lazy.compactMap { NSRunningApplication(processIdentifier: $0) }.first { $0.activationPolicy == .regular } }

    static let selfID = Bundle.main.bundlePath
    static let csvHeader = "tijd,app,soort,cpu_pct,geheugen_gb,energie_w\n"
}

/// Meet alle processen via proc_pid_rusage en groepeert ze per verantwoordelijke app.
/// Processen van andere gebruikers (root) mag alleen macOS meten: die komen samen in de regel "macOS zelf".
/// `measure` (en alles wat het bijhoudt) draait op `Meter.work`; alleen `apps` is van de hoofdthread.
final class AppMonitor: ObservableObject {
    @Published var apps: [AppUsage] = []      // hoogste processor eerst; gezet door Meter
    private var last: [pid_t: (cpu: UInt64, energy: UInt64, rd: UInt64, wr: UInt64)] = [:]
    private var lastTime = DispatchTime.now()
    private var info: [pid_t: (key: String, path: String)] = [:]
    private var names: [String: (name: String, explanation: String, kind: AppUsage.Kind, icon: NSImage?)] = [:]
    private let cores = Double(ProcessInfo.processInfo.activeProcessorCount)
    private let tick: Double = { var t = mach_timebase_info(); mach_timebase_info(&t); return Double(t.numer) / Double(t.denom) }()
    private typealias Resp = @convention(c) (pid_t) -> pid_t
    private let responsible: Resp? = dlsym(dlopen(nil, RTLD_NOW), "responsibility_get_pid_responsible_for_pid").map { unsafeBitCast($0, to: Resp.self) }
    // Internet per proces via nettop: alleen voor de Programs-pagina (`network`), en alleen met de instelling aan
    private let network: Bool
    private var netBytes: [pid_t: (UInt64, UInt64)] = [:], netRate: [pid_t: (Double, Double)] = [:]
    private var netTime = DispatchTime.now(), netBusy = false

    init(network: Bool = false) { self.network = network }

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
        var seen: [pid_t: (cpu: UInt64, energy: UInt64, rd: UInt64, wr: UInt64)] = [:]
        let nowAbs = mach_absolute_time(), nowDate = Date()
        if network { pollNet() }
        for pid in pids where pid > 0 {
            var ri = rusage_info_v6()
            let ok = withUnsafeMutablePointer(to: &ri) { p in
                p.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V6, $0) }
            }
            guard ok == 0 else { continue } // niet van ons: telt mee in "macOS zelf"
            let cpuNs = UInt64(Double(ri.ri_user_time + ri.ri_system_time) * tick)
            seen[pid] = (cpuNs, ri.ri_energy_nj, ri.ri_diskio_bytesread, ri.ri_diskio_byteswritten)
            let (key, _) = identity(pid)
            let meta = describe(key: key, pid: pid)
            let prev = last[pid]
            // Teller lager dan vorige keer (pid hergebruikt / exec): als nieuw proces behandelen, niet laten omslaan
            func delta(_ a: UInt64, _ b: UInt64) -> Double { a >= b ? Double(a - b) : 0 }
            let cpu = prev.map { dt > 0 ? delta(cpuNs, $0.cpu) / 1e9 / dt / cores * 100 : 0 } ?? 0
            let watt = prev.map { dt > 0 ? delta(ri.ri_energy_nj, $0.energy) / 1e9 / dt : 0 } ?? 0
            let rd = prev.map { dt > 0 ? delta(ri.ri_diskio_bytesread, $0.rd) / dt : 0 } ?? 0
            let wr = prev.map { dt > 0 ? delta(ri.ri_diskio_byteswritten, $0.wr) / dt : 0 } ?? 0
            var ti = proc_taskinfo()
            let threads = proc_pidinfo(pid, PROC_PIDTASKINFO, 0, &ti, Int32(MemoryLayout<proc_taskinfo>.size)) > 0 ? Int(ti.pti_threadnum) : 0
            let start = nowDate.addingTimeInterval(-Double(nowAbs &- ri.ri_proc_start_abstime) * tick / 1e9)
            let mem = Double(ri.ri_phys_footprint) / 1_073_741_824
            let net = netRate[pid] ?? (0, 0)
            if var g = groups[key] {
                g.pids.append(pid); g.cpu += cpu; g.memGB += mem; g.energyW += watt
                g.diskRead += rd; g.diskWrite += wr; g.netIn += net.0; g.netOut += net.1
                g.threads += threads; g.started = min(g.started ?? start, start)
                groups[key] = g
            } else {
                groups[key] = AppUsage(id: key, name: meta.name, explanation: meta.explanation, kind: meta.kind,
                                       pids: [pid], cpu: cpu, memGB: mem, energyW: watt, icon: meta.icon,
                                       diskRead: rd, diskWrite: wr, netIn: net.0, netOut: net.1, threads: threads, started: start)
            }
        }
        last = seen
        info = info.filter { seen[$0.key] != nil }

        // Wat we niet zelf konden meten (WindowServer, kernel, root-processen) = totaal min de som
        let measuredCPU = groups.values.reduce(0) { $0 + $1.cpu }, measuredMem = groups.values.reduce(0) { $0 + $1.memGB }
        groups["macos"] = AppUsage(id: "macos", name: "macOS itself", explanation: "Drawing the screen, the kernel and system services only macOS is allowed to measure",
                                   kind: .macos, pids: [], cpu: max(totalCPU - measuredCPU, 0), memGB: max(totalMemGB - measuredMem, 0),
                                   energyW: 0, icon: NSImage(systemSymbolName: "apple.logo", accessibilityDescription: nil))
        // nettop ziet ook root-processen: dat verkeer hoort bij "macOS zelf"
        for (pid, n) in netRate where seen[pid] == nil { groups["macos"]?.netIn += n.0; groups["macos"]?.netOut += n.1 }
        return groups.values.sorted { $0.cpu == $1.cpu ? $0.memGB > $1.memGB : $0.cpu > $1.cpu }
    }

    /// Start om de ~4 s één nettop-meting op de achtergrond; de snelheden komen in de volgende meting mee.
    /// Kost per run ~10 ms processor (gemeten op M-serie), dus ~0,3% van één kern.
    private func pollNet() {
        guard UserDefaults.standard.bool(forKey: "netPerApp") else { netBytes = [:]; netRate = [:]; return }
        let age = Double(DispatchTime.now().uptimeNanoseconds - netTime.uptimeNanoseconds) / 1e9
        if age > 10 { netRate = [:] }   // pagina was dicht: oude snelheden niet meer tonen
        guard !netBusy, age >= 3 || netBytes.isEmpty else { return }
        netBusy = true
        DispatchQueue.global(qos: .utility).async { [self] in
            let out = Diag.run("/usr/bin/nettop", ["-P", "-L", "1", "-n", "-x", "-J", "bytes_in,bytes_out"], timeout: 2)
            let bytes = Self.parseNettop(out), now = DispatchTime.now()
            Meter.work.async { [self] in
                let dt = Double(now.uptimeNanoseconds - netTime.uptimeNanoseconds) / 1e9
                netRate = dt <= 10 ? Self.netRates(netBytes, bytes, dt: dt) : [:]   // te oud = alleen nieuw nulpunt
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
            r = (friendly?.0 ?? proc, friendly?.1 ?? (system ? "Part of macOS" : "Background process"), system ? .macos : .background,
                 NSImage(systemSymbolName: system ? "gearshape" : "terminal", accessibilityDescription: nil))
        }
        names[key] = r
        return r
    }

    /// Gewone namen voor bekende macOS-processen.
    static let friendly: [String: (String, String)] = [
        "mds": ("Spotlight (search)", "Indexes your files so searching is fast"),
        "mdworker": ("Spotlight (search)", "Reads new files for Spotlight"),
        "corespotlightd": ("Spotlight (search)", "Indexes content from apps"),
        "photoanalysisd": ("Photo analysis", "Recognizes faces and objects in your photos"),
        "photolibraryd": ("Photo library", "Manages your Photos library"),
        "mediaanalysisd": ("Media analysis", "Analyzes photos and videos in the background"),
        "cloudd": ("iCloud", "Syncs with iCloud"),
        "bird": ("iCloud Drive", "Syncs your iCloud Drive files"),
        "fileproviderd": ("iCloud Drive", "Keeps track of files in the cloud"),
        "backupd": ("Time Machine", "Making a backup"),
        "softwareupdated": ("Software update", "Checks for or installs updates"),
        "coreaudiod": ("Sound", "Plays audio"),
        "bluetoothd": ("Bluetooth", "Connects to Bluetooth devices"),
        "nsurlsessiond": ("Background downloads", "Downloads for apps that aren't open"),
        "trustd": ("Security", "Checks certificates"),
        "syspolicyd": ("Security", "Checks whether apps are safe"),
        "XProtect": ("Virus scanner", "Apple's built-in malware protection"),
        "WindowManager": ("Window management", "Stage Manager and windows"),
        "sharingd": ("AirDrop and sharing", "AirDrop, Handoff and sharing"),
        "locationd": ("Location", "Determines your location for apps"),
        "Siri": ("Siri", "Siri and suggestions"),
        "suggestd": ("Siri suggestions", "Learns from your usage for suggestions"),
        "logd": ("Logging", "Writes system messages to the log"),
        "spindump": ("Hang analysis", "Investigates an app that froze"),
        "ReportCrash": ("Crash report", "Handles an app that crashed"),
        "Finder": ("Finder", "Files and desktop"),
        "Dock": ("Dock", "The Dock and Mission Control"),
        "SystemUIServer": ("Menu bar", "Icons in the menu bar"),
        "ControlCenter": ("Control Center", "Control Center and menu bar"),
        "NotificationCenter": ("Notification Center", "Notifications and widgets"),
    ]
}
