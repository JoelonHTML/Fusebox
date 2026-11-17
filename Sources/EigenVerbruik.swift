import AppKit
import Darwin
import IOKit.ps

// MARK: - Beta: eigen verbruik van Fusebox
//
// Per minuut één regel in Documents/Fusebox/self-YYYY-MM-DD.csv: wat Fusebox zelf kost (CPU, geheugen, energie,
// GPU, wake-ups) én in welke situatie (pagina, venstergrootte, zichtbaar, thema, lettertype, low impact, stroom).
// Niets gaat automatisch het internet op: "Share performance report" maakt een leesbaar tekstbestand om te sturen.

enum SelfLog {
    static let header = "tijd,versie,cpu_pct,geheugen_mb,energie_w,gpu_pct,ws_gpu_pct,wakeups_s,pagina,venster,zichtbaar,thema,lettertype,low_impact,stroom\n"

    private struct Mark { let t: TimeInterval, cpuNs: Double, energyNj: UInt64, wakeups: UInt64, gpuNs: UInt64, wsNs: UInt64 }
    nonisolated(unsafe) private static var last: Mark?
    private static let tick: Double = { var t = mach_timebase_info(); mach_timebase_info(&t); return Double(t.numer) / Double(t.denom) }()

    /// Eén meting; de eerste keer alleen een nulpunt. Kost < 5 ms per minuut.
    @MainActor static func record(_ s: Sample, to file: URL) {
        var ri = rusage_info_v6()
        let ok = withUnsafeMutablePointer(to: &ri) { p in
            p.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(getpid(), RUSAGE_INFO_V6, $0) }
        }
        guard ok == 0 else { return }
        let gpu = AppMonitor.gpuTimes()
        let ws = gpu.first { AppMonitor.gpuNames[$0.key] == "WindowServer" }?.value ?? 0
        let now = Mark(t: ProcessInfo.processInfo.systemUptime, cpuNs: Double(ri.ri_user_time + ri.ri_system_time) * tick,
                       energyNj: ri.ri_energy_nj, wakeups: ri.ri_interrupt_wkups + ri.ri_pkg_idle_wkups,
                       gpuNs: gpu[getpid()] ?? 0, wsNs: ws)
        defer { last = now }
        guard let l = last, now.t - l.t > 5 else { return }
        let dt = now.t - l.t
        func d(_ a: UInt64, _ b: UInt64) -> Double { a >= b ? Double(a - b) : 0 }
        let cpu = max(now.cpuNs - l.cpuNs, 0) / 1e9 / dt * 100
        let watt = d(now.energyNj, l.energyNj) / 1e9 / dt
        let gpuPct = d(now.gpuNs, l.gpuNs) / 1e9 / dt * 100, wsPct = d(now.wsNs, l.wsNs) / 1e9 / dt * 100
        let wake = d(now.wakeups, l.wakeups) / dt

        let d0 = UserDefaults.standard
        let win = NSApp.windows.first { $0.identifier?.rawValue.hasPrefix("main") == true }
        let visible = win.map { $0.isVisible && $0.occlusionState.contains(.visible) } ?? false
        let size = win.map { "\(Int($0.frame.width))x\(Int($0.frame.height))" } ?? "-"
        // Aan de lader (ook bij 100 % als hij niet meer laadt) of echt op de accu
        let adapter = IOPSCopyExternalPowerAdapterDetails()?.takeRetainedValue() != nil
        let power = s.battery == nil ? "ac" : s.charging ? "charging" : adapter ? "ac" : "battery"
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let line = [Sample.stamp.string(from: Date()), version, String(format: "%.2f", cpu), String(format: "%.0f", Double(ri.ri_phys_footprint) / 1_048_576),
                    String(format: "%.3f", watt), String(format: "%.2f", gpuPct), String(format: "%.2f", wsPct), String(format: "%.1f", wake),
                    d0.string(forKey: "page") ?? "live", size, visible ? "1" : "0", d0.string(forKey: "theme") ?? "",
                    d0.string(forKey: "font") ?? "style", d0.string(forKey: "lowImpact") ?? "off", power].joined(separator: ",") + "\n"
        let fm = FileManager.default
        if !fm.fileExists(atPath: file.path) { try? header.write(to: file, atomically: true, encoding: .utf8) }
        _ = Meter.append(line, to: file)
    }

    private static func name(_ pid: pid_t) -> String {
        var buf = [CChar](repeating: 0, count: 64)
        return proc_name(pid, &buf, UInt32(buf.count)) > 0 ? String(cString: buf) : ""
    }

    // MARK: Rapport

    struct Row { let visible: Bool, page: String, size: String, theme: String, low: String, cpu: Double, mem: Double, watt: Double, gpu: Double, ws: Double, wake: Double }

    /// CSV-regels → rijen (kolommen op naam, zodat latere kolommen oude bestanden niet breken).
    static func parse(_ text: String) -> [Row] {
        var lines = text.split(separator: "\n")
        guard !lines.isEmpty else { return [] }
        let cols = Dictionary(lines.removeFirst().split(separator: ",").enumerated().map { (String($1), $0) }, uniquingKeysWith: { a, _ in a })
        return lines.compactMap { l in
            let f = l.split(separator: ",", omittingEmptySubsequences: false).map(String.init)
            func s(_ k: String) -> String { cols[k].flatMap { $0 < f.count ? f[$0] : nil } ?? "" }
            func n(_ k: String) -> Double { Double(s(k)) ?? 0 }
            guard cols["cpu_pct"] != nil, f.count >= cols.count - 1 else { return nil }
            return Row(visible: s("zichtbaar") == "1", page: s("pagina"), size: bucket(s("venster")), theme: s("thema"), low: s("low_impact"),
                       cpu: n("cpu_pct"), mem: n("geheugen_mb"), watt: n("energie_w"), gpu: n("gpu_pct"), ws: n("ws_gpu_pct"), wake: n("wakeups_s"))
        }
    }

    /// "1400x780" → "large" (op oppervlakte), zodat situaties te groeperen zijn.
    static func bucket(_ s: String) -> String {
        let p = s.split(separator: "x").compactMap { Double($0) }
        guard p.count == 2 else { return "-" }
        let a = p[0] * p[1]
        return a < 250_000 ? "small" : a < 700_000 ? "medium" : "large"
    }

    /// Leesbaar overzicht: per situatie aantal minuten, gemiddeld en 95e percentiel CPU, geheugen, GPU, energie.
    static func report(rows: [Row], days: Int) -> String {
        var out = "Fusebox performance report\n"
        let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"
        let b = Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String ?? "?"
        var model = [CChar](repeating: 0, count: 64), len = model.count
        sysctlbyname("hw.model", &model, &len, nil, 0)
        let os = ProcessInfo.processInfo.operatingSystemVersion
        out += "Version \(v) (\(b)) · \(String(cString: model)) · macOS \(os.majorVersion).\(os.minorVersion).\(os.patchVersion) · \(ProcessInfo.processInfo.activeProcessorCount) cores · \(Int((Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824).rounded())) GB · \(NSScreen.screens.count) display(s)\n"
        out += "Last \(days) day(s), \(rows.count) minutes measured. Fusebox's own use; ws_gpu = WindowServer GPU (whole screen, for context).\n\n"
        let groups = Dictionary(grouping: rows) { "\($0.visible ? "visible" : "hidden ")|\($0.page)|\($0.size)|\($0.theme)|low:\($0.low)" }
        out += "situation (window|page|size|theme|low impact)                minutes  cpu avg  cpu p95  mem MB  gpu %  ws_gpu %  watt   wakeups/s\n"
        for (k, g) in groups.sorted(by: { $0.value.count > $1.value.count }) {
            let cpu = g.map(\.cpu).sorted()
            func avg(_ x: [Double]) -> Double { x.isEmpty ? 0 : x.reduce(0, +) / Double(x.count) }
            let p95 = cpu[min(cpu.count - 1, Int(Double(cpu.count) * 0.95))]
            out += k.padding(toLength: 60, withPad: " ", startingAt: 0)
                + String(format: "%7d  %7.2f  %7.2f  %6.0f  %5.2f  %8.2f  %5.3f  %9.1f\n", g.count, avg(cpu), p95, avg(g.map(\.mem)), avg(g.map(\.gpu)), avg(g.map(\.ws)), avg(g.map(\.watt)), avg(g.map(\.wake)))
        }
        return out
    }

    /// Rapport over de laatste `days` dagen maken, in Downloads zetten, kopiëren en in de Finder tonen.
    @MainActor static func share(logDir: URL, days: Int = 7) {
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.calendar = Calendar(identifier: .gregorian); f.dateFormat = "yyyy-MM-dd"
        var rows: [Row] = []
        for i in 0..<days {
            guard let day = Calendar.current.date(byAdding: .day, value: -i, to: Date()) else { continue }
            if let t = try? String(contentsOf: logDir.appendingPathComponent("self-\(f.string(from: day)).csv"), encoding: .utf8) { rows += parse(t) }
        }
        let text = report(rows: rows, days: days)
        let dl = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
        let url = dl.appendingPathComponent("Fusebox-performance-\(f.string(from: Date())).txt")
        try? text.write(to: url, atomically: true, encoding: .utf8)
        NSPasteboard.general.clearContents(); NSPasteboard.general.setString(text, forType: .string)
        NSWorkspace.shared.activateFileViewerSelecting([url])
    }

    static func selfTest() {
        let csv = header + "2026-11-17 10:00:00,0.7,2.10,45,0.050,0.20,9.00,12.0,live,323x567,1,liquid,style,off,ac\n"
            + "2026-11-17 10:01:00,0.7,0.40,46,0.010,0.00,1.00,3.0,live,323x567,0,liquid,style,off,ac\n"
        let r = parse(csv)
        precondition(r.count == 2 && r[0].visible && !r[1].visible && r[0].size == "small" && abs(r[0].cpu - 2.1) < 0.001, "eigen verbruik parse")
        precondition(bucket("1400x780") == "large" && bucket("700x600") == "medium" && bucket("x") == "-", "venstermaat")
        precondition(report(rows: r, days: 1).contains("visible|live|small|liquid|low:off"), "rapport")
        print("eigen verbruik ok")
    }
}
