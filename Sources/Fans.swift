import SwiftUI
import Darwin

// MARK: - Ventilatoren: meten (zonder root) en sturen via de helper `fusebox-fand` (root, Helper/FanHelper.swift).
// Alles is verborgen op Macs zonder ventilator (Fans.count == 0, bv. MacBook Air).
// De helper bewaakt zelf de veiligheid (nooit onder het minimum, ≥ 95 °C vol, 30 s stilte = terug naar macOS);
// de app stuurt alleen wensen.

enum Fans {
    /// FNum, één keer gelezen (lichte SMC-verbinding, zonder de ronde langs alle temperatuursleutels).
    static let count: Int = SMC(scanTemps: false).fanCount
}

// MARK: Curve: "°C:%,…" — % tussen het minimum en maximum toerental van macOS

enum FanCurve {
    typealias Point = (t: Double, p: Double)
    static let quiet = "40:0,65:10,80:40,90:75,95:100"
    static let balanced = "40:0,60:20,75:50,85:80,95:100"
    static let cool = "40:10,55:35,70:65,80:90,90:100"
    static let presets: [(name: String, value: String)] = [("Quiet", quiet), ("Balanced", balanced), ("Cool", cool)]
    static let range = 40.0...100.0

    /// Vertrouwensgrens (instelling kan uit een import komen): 2–8 punten, °C in `range`, % 0–100, °C oplopend.
    static func parse(_ raw: String) -> [Point]? {
        let parts = raw.split(separator: ",")
        guard (2...8).contains(parts.count) else { return nil }
        var out: [Point] = []
        for part in parts {
            let tp = part.split(separator: ":", omittingEmptySubsequences: false)
            guard tp.count == 2, let t = Double(tp[0]), let p = Double(tp[1]),
                  range.contains(t), (0...100).contains(p), t > (out.last?.t ?? -1) else { return nil }
            out.append((t, p))
        }
        return out
    }
    static func points(_ raw: String) -> [Point] { parse(raw) ?? parse(balanced)! }
    static func format(_ pts: [Point]) -> String { pts.map { "\(Int($0.t.rounded())):\(Int($0.p.rounded()))" }.joined(separator: ",") }

    /// % bij deze temperatuur: lineair tussen de punten, vlak buiten de randen. Onzin-temperatuur → het hoogste punt (veilig).
    static func percent(at temp: Double, _ pts: [Point]) -> Double {
        guard let f = pts.first, let l = pts.last else { return 100 }
        guard temp.isFinite else { return l.p }
        if temp <= f.t { return f.p }
        if temp >= l.t { return l.p }
        let i = pts.firstIndex { $0.t >= temp } ?? pts.count - 1
        let a = pts[i - 1], b = pts[i]
        return a.p + (b.p - a.p) * (temp - a.t) / (b.t - a.t)
    }
    static func rpm(percent: Double, min: Double, max: Double) -> Double {
        min + (max - min) * Swift.min(Swift.max(percent, 0), 100) / 100
    }
    /// Waar de curve op stuurt: het warmste van processor en grafische chip.
    static func input(_ s: Sample) -> Double? { [s.temp, s.gpuTemp].compactMap { $0 }.max() }
}

/// Tegen "jagen": sneller meteen (bij > 150 rpm verschil), langzamer in stapjes van 100 rpm per meting (2 s).
struct FanSmoother {
    private(set) var current: Double?
    mutating func next(_ target: Double) -> Double {
        guard let c = current else { current = target; return target }
        if target > c + 150 { current = target } else if target < c - 150 { current = Swift.max(target, c - 100) }
        return current!
    }
}

// MARK: Helper: installeren, verwijderen, praten (één regel per verbinding)

enum FanHelper {
    static let label = "local.fusebox.fand"
    static let binPath = "/Library/PrivilegedHelperTools/local.fusebox.fand"
    static let plistPath = "/Library/LaunchDaemons/local.fusebox.fand.plist"
    static let socketPath = "/var/run/local.fusebox.fand.sock"

    /// Moet gelijk zijn aan `helperVersion` in Helper/FanHelper.swift. Ouder → "Reinstall" (updates van de app
    /// vervangen de geïnstalleerde root-helper niet; dat kan alleen met je wachtwoord).
    static let expectedVersion = 2
    static var installed: Bool { FileManager.default.fileExists(atPath: plistPath) && FileManager.default.fileExists(atPath: binPath) }

    /// Stuurt één opdracht ("status", "auto", "set 0 2400") en geeft het antwoord ("ok …" / "err …"), of nil als de helper er niet is.
    @discardableResult static func send(_ cmd: String) -> String? {
        let fd = socket(AF_UNIX, SOCK_STREAM, 0)
        guard fd >= 0 else { return nil }
        defer { close(fd) }
        var on: Int32 = 1, tv = timeval(tv_sec: 1, tv_usec: 0)
        setsockopt(fd, SOL_SOCKET, SO_NOSIGPIPE, &on, socklen_t(MemoryLayout<Int32>.size))
        setsockopt(fd, SOL_SOCKET, SO_RCVTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        setsockopt(fd, SOL_SOCKET, SO_SNDTIMEO, &tv, socklen_t(MemoryLayout<timeval>.size))
        var addr = sockaddr_un()
        addr.sun_family = sa_family_t(AF_UNIX)
        let path = Array(socketPath.utf8)
        withUnsafeMutableBytes(of: &addr.sun_path) { b in for (i, c) in path.enumerated() where i < b.count - 1 { b[i] = c } }
        let r = withUnsafePointer(to: &addr) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) { connect(fd, $0, socklen_t(MemoryLayout<sockaddr_un>.size)) } }
        guard r == 0 else { return nil }
        let line = Array((cmd + "\n").utf8)
        guard write(fd, line, line.count) == line.count else { return nil }
        var buf = [UInt8](repeating: 0, count: 128)
        let n = read(fd, &buf, buf.count)
        guard n > 0 else { return nil }
        return String(decoding: buf[..<n], as: UTF8.self).trimmingCharacters(in: .whitespacesAndNewlines)
    }

    /// Kopieert de helper uit de app naar een root-map (nooit root draaien vanuit de app zelf: die is door de gebruiker te wijzigen).
    /// nil = gelukt, anders een zin voor de gebruiker.
    @MainActor static func install() -> String? {
        guard let src = Bundle.main.resourceURL?.appendingPathComponent("fusebox-fand").path,
              FileManager.default.isExecutableFile(atPath: src) else { return "The helper is missing from this copy of Fusebox." }
        let plist = #"<?xml version="1.0" encoding="UTF-8"?><!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd"><plist version="1.0"><dict><key>Label</key><string>\#(label)</string><key>ProgramArguments</key><array><string>\#(binPath)</string></array><key>RunAtLoad</key><true/><key>KeepAlive</key><true/></dict></plist>"#
        return admin([
            "umask 022",
            "/bin/mkdir -p /Library/PrivilegedHelperTools",
            "(/bin/launchctl bootout system/\(label) 2>/dev/null && /bin/sleep 1 || true)",
            "/usr/bin/install -o root -g wheel -m 0755 \(q(src)) \(q(binPath))",
            "/bin/rm -f \(q(plistPath))",
            "/usr/bin/printf '%s' \(q(plist)) > \(q(plistPath))",
            "/usr/sbin/chown root:wheel \(q(plistPath))",
            "/bin/chmod 0644 \(q(plistPath))",
            "/bin/launchctl bootstrap system \(q(plistPath))",
        ].joined(separator: " && "))
    }

    /// Ventilatoren terug naar macOS, dienst stoppen (de helper zet bij SIGTERM ook zelf op auto), bestanden weg.
    @MainActor static func uninstall() -> String? {
        send("auto")
        return admin("/bin/launchctl bootout system/\(label) 2>/dev/null; /bin/rm -f \(q(plistPath)) \(q(binPath)) \(q(socketPath))")
    }

    /// Shell-quoting met enkele aanhalingstekens.
    static func q(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    @MainActor private static func admin(_ shell: String) -> String? {
        let esc = shell.replacingOccurrences(of: "\\", with: "\\\\").replacingOccurrences(of: "\"", with: "\\\"")
        var err: NSDictionary?
        NSAppleScript(source: "do shell script \"\(esc)\" with administrator privileges")?.executeAndReturnError(&err)
        guard let err else { return nil }
        if err[NSAppleScript.errorNumber] as? Int == -128 { return "Cancelled." }
        return "Didn't work: \(err[NSAppleScript.errorMessage] as? String ?? "unknown error")"
    }
}

// MARK: Sturen: elke meting (2 s) de wens naar de helper

final class FanController {
    private let queue = DispatchQueue(label: "local.fusebox.fans")
    private var smooth: [FanSmoother] = []
    private var lastMode = ""
    private var busy = false   // vorige ronde nog bezig (helper traag) → deze overslaan

    init() {
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: nil) { [weak self] _ in
            guard let self, self.lastMode == "curve" || self.lastMode == "max" else { return }
            self.queue.sync { _ = FanHelper.send("auto") }   // meteen terug naar macOS i.p.v. na 30 s
        }
    }

    func update(_ s: Sample) {
        guard !s.fans.isEmpty, !busy, FanHelper.installed else { return }
        let mode = UserDefaults.standard.string(forKey: "fanMode") ?? "auto"
        let cmds = Self.commands(mode: mode, changed: mode != lastMode, fans: s.fans, temp: FanCurve.input(s),
                                 curve: UserDefaults.standard.string(forKey: "fanCurve") ?? "", smooth: &smooth)
        lastMode = mode
        guard !cmds.isEmpty else { return }
        busy = true
        queue.async { [weak self] in
            cmds.forEach { _ = FanHelper.send($0) }
            DispatchQueue.main.async { self?.busy = false }
        }
    }

    /// Welke opdrachten deze ronde. "auto" alleen bij wisselen; curve/vol elke ronde (de helper wil een teken van leven).
    /// Geen temperatuur → niets sturen: de helper valt na 30 s vanzelf terug op macOS.
    static func commands(mode: String, changed: Bool, fans: [FanReading], temp: Double?, curve: String, smooth: inout [FanSmoother]) -> [String] {
        if changed || smooth.count != fans.count { smooth = Array(repeating: FanSmoother(), count: fans.count) }
        switch mode {
        case "max":
            return fans.indices.map { "set \($0) \(Int(fans[$0].max.rounded()))" }
        case "curve":
            guard let temp else { return [] }
            let pct = FanCurve.percent(at: temp, FanCurve.points(curve))
            return fans.indices.map { i in
                let v = smooth[i].next(FanCurve.rpm(percent: pct, min: fans[i].min, max: fans[i].max))
                return "set \(i) \(Int(v.rounded()))"
            }
        default:
            return changed ? ["auto"] : []
        }
    }

    // MARK: Zelftest

    static func selfTest() {
        func near(_ a: Double?, _ b: Double) -> Bool { a.map { Swift.abs($0 - b) < 0.01 } ?? false }
        // SMC-getallen
        let f = Float(2400).bitPattern
        precondition(near(SMC.decode(type: "flt ", [UInt8(f & 0xff), UInt8(f >> 8 & 0xff), UInt8(f >> 16 & 0xff), UInt8(f >> 24)]), 2400), "flt")
        precondition(near(SMC.decode(type: "fpe2", [0x25, 0x80]), 2400), "fpe2")   // 9600 / 4
        precondition(near(SMC.decode(type: "ui8 ", [1]), 1) && near(SMC.decode(type: "ui16", [0x09, 0x60]), 2400), "ui8/ui16")
        precondition(SMC.decode(type: "flt ", [1, 2]) == nil && SMC.decode(type: "sp78", [1, 2]) == nil, "onbekend/verkeerde maat")
        // Curve
        let b = FanCurve.points(FanCurve.balanced)
        precondition(FanCurve.presets.allSatisfy { FanCurve.parse($0.value) != nil } && FanCurve.format(b) == FanCurve.balanced, "presets/format")
        for bad in ["40:0", "60:0,40:10", "40:0,60:200", "abc", "40:0,nan:5", "30:0,60:20", "40:0,60:20:1", "40:0,inf:20", "40:-1,60:20"] {
            precondition(FanCurve.parse(bad) == nil, "curve \(bad)")
        }
        precondition(FanCurve.points("rommel").count == 5, "terugval op Balanced")
        precondition(FanCurve.percent(at: 30, b) == 0 && FanCurve.percent(at: 50, b) == 10 && FanCurve.percent(at: 67.5, b) == 35, "interpolatie")
        precondition(FanCurve.percent(at: 120, b) == 100 && FanCurve.percent(at: .nan, b) == 100, "boven/onzin = vol")
        precondition(FanCurve.rpm(percent: 50, min: 1200, max: 5000) == 3100 && FanCurve.rpm(percent: 150, min: 1200, max: 5000) == 5000
                     && FanCurve.rpm(percent: -10, min: 1200, max: 5000) == 1200, "rpm-grenzen")
        // Rustig regelen
        var sm = FanSmoother()
        precondition(sm.next(2000) == 2000 && sm.next(2100) == 2000 && sm.next(3000) == 3000, "omhoog meteen, kleine stap genegeerd")
        precondition(sm.next(2000) == 2900 && sm.next(2000) == 2800, "omlaag langzaam")
        // Opdrachten
        let fans = [FanReading(rpm: 1500, min: 1200, max: 5000), FanReading(rpm: 1500, min: 1000, max: 6000)]
        var st: [FanSmoother] = []
        precondition(commands(mode: "auto", changed: true, fans: fans, temp: 70, curve: "", smooth: &st) == ["auto"], "auto bij wissel")
        precondition(commands(mode: "auto", changed: false, fans: fans, temp: 70, curve: "", smooth: &st).isEmpty, "auto één keer")
        precondition(commands(mode: "max", changed: true, fans: fans, temp: nil, curve: "", smooth: &st) == ["set 0 5000", "set 1 6000"], "vol")
        precondition(commands(mode: "curve", changed: true, fans: fans, temp: 75, curve: FanCurve.balanced, smooth: &st) == ["set 0 3100", "set 1 3500"], "curve")
        precondition(commands(mode: "curve", changed: false, fans: fans, temp: nil, curve: FanCurve.balanced, smooth: &st).isEmpty, "geen temp = niets")
        // Live-rij
        var s = Sample(); s.fans = fans; s.fanRPM = 2400
        let row = Metric.fan.row(s, [s], abs: false)
        precondition(row.value == "2,400" && row.unit == "rpm" && row.top == 6000 && row.side == "auto", "FAN-rij")
        precondition(Metric.enabled("cpu,fan").contains(.fan) == (Fans.count > 0) && Metric.offered.contains(.fan) == (Fans.count > 0), "FAN alleen met ventilator")
        precondition(FanHelper.q("a'b") == #"'a'\''b'"#, "quoting")
        print("ventilatoren: ok (\(Fans.count) op deze Mac)")
    }
}

// MARK: - Instellingen › Fans

struct FanSettings: View {
    @EnvironmentObject var meter: Meter
    @AppStorage("fanMode") private var mode = "auto"
    @AppStorage("fanCurve") private var curve = FanCurve.balanced
    @State private var installed = FanHelper.installed
    @State private var outdated = false
    @State private var running: Bool?
    @State private var note: String?

    var body: some View {
        if Fans.count == 0 {
            Section { Text("This Mac has no fans.").foregroundStyle(.secondary) }
        } else {
            let s = meter.latest
            Section("Fans now") {
                ForEach(s.fans.indices, id: \.self) { i in fanRow(i, s.fans[i]) }
                if s.fans.isEmpty { Text("Reading the fans…").foregroundStyle(.secondary) }
            }
            Section {
                Picker("Control", selection: $mode) {
                    Text("Automatic (macOS)").tag("auto"); Text("Custom curve").tag("curve"); Text("Full speed").tag("max")
                }
                .pickerStyle(.segmented)
                if mode != "auto" && !installed {
                    Text("Install the helper below, then Fusebox can set the fans.").font(.caption).foregroundStyle(.orange)
                }
            } footer: {
                Text("Fusebox never runs the fans slower than macOS's minimum. Above 95 °C the fans go to full speed automatically.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            if mode == "curve" {
                Section("Fan speed by temperature (drag the points)") {
                    let temp = FanCurve.input(s)
                    CurveEditor(raw: $curve, temp: temp)
                    HStack {
                        ForEach(FanCurve.presets, id: \.name) { p in
                            Button(p.name) { curve = p.value }.disabled(curve == p.value)
                        }
                        Spacer()
                        if let temp {
                            Text("Now \(Int(temp.rounded())) °C → \(Int(FanCurve.percent(at: temp, FanCurve.points(curve)).rounded()))%")
                                .monospacedDigit().foregroundStyle(.secondary)
                        }
                    }
                }
            }
            helperSection
        }
    }

    private func fanRow(_ i: Int, _ f: FanReading) -> some View {
        LabeledContent {
            HStack(spacing: 10) {
                Spark(values: meter.recent.suffix(150).compactMap { $0.fans.indices.contains(i) ? $0.fans[i].rpm : nil },
                      top: Swift.max(f.max, 1), fill: 1, closed: false)
                    .stroke(Color.accentColor, lineWidth: 1.5).frame(width: 140, height: 22)
                Text("\(Int(f.rpm.rounded()).formatted(.number.locale(Locale(identifier: "en_US")))) rpm")
                    .monospacedDigit().frame(width: 80, alignment: .trailing)
            }
        } label: {
            Text(meter.latest.fans.count > 1 ? "Fan \(i + 1)" : "Fan")
            Text("macOS range \(Int(f.min))–\(Int(f.max)) rpm" + (f.forced ? " · set by Fusebox" : ""))
        }
    }

    @ViewBuilder private var helperSection: some View {
        Section {
            if installed {
                LabeledContent("Helper", value: running == false ? "Installed, not responding" : outdated ? "Update needed" : "Installed")
                if outdated {
                    Text("This Fusebox has a newer fan helper. Click Reinstall… to update it (asks for your password).")
                        .font(.caption).foregroundStyle(.orange)
                }
                HStack {
                    Button("Reinstall…") { act(FanHelper.install) }
                    Button("Uninstall helper…", role: .destructive) { if act(FanHelper.uninstall) { mode = "auto" } }
                }
            } else {
                LabeledContent("Helper") { Button("Install…") { act(FanHelper.install) } }
                Text("Not installed. Installing asks for your password once.").font(.caption).foregroundStyle(.secondary)
            }
            if let note { Text(note).font(.caption).foregroundStyle(.secondary) }
        } header: {
            Text("Fan helper")
        } footer: {
            Text("A small background program that runs as administrator. It is the only part that can change fan speed, and it gives the fans back to macOS if Fusebox stops talking to it for 30 s.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .task(id: installed) { await check() }
    }

    /// Waar = gelukt.
    @discardableResult private func act(_ f: @MainActor () -> String?) -> Bool {
        note = f()
        installed = FanHelper.installed
        defer { Task { try? await Task.sleep(for: .seconds(1)); await check() } }
        return note == nil
    }

    private func check() async {
        guard installed else { running = nil; return }
        let reply = await Task.detached { FanHelper.send("status") }.value
        running = reply?.hasPrefix("ok") == true
        let v = reply?.split(separator: " ").first { $0.hasPrefix("v=") }.flatMap { Int($0.dropFirst(2)) } ?? 1
        outdated = running == true && v < FanHelper.expectedVersion
    }
}

/// Curve-editor: x = 40–100 °C, y = % tussen minimum en maximum van macOS. Punten verslepen; oranje stip = nu.
struct CurveEditor: View {
    @Binding var raw: String
    let temp: Double?

    var body: some View {
        let pts = FanCurve.points(raw), r = FanCurve.range
        VStack(spacing: 4) {
            GeometryReader { g in
                let w = g.size.width, h = g.size.height
                let pos = { (t: Double, p: Double) in CGPoint(x: (t - r.lowerBound) / (r.upperBound - r.lowerBound) * w, y: h - p / 100 * h) }
                ZStack(alignment: .topLeading) {
                    Path { p in
                        for i in 0...6 { let x = w * CGFloat(i) / 6; p.move(to: CGPoint(x: x, y: 0)); p.addLine(to: CGPoint(x: x, y: h)) }
                        for j in 0...4 { let y = h * CGFloat(j) / 4; p.move(to: CGPoint(x: 0, y: y)); p.addLine(to: CGPoint(x: w, y: y)) }
                    }
                    .stroke(.quaternary, lineWidth: 0.5)
                    // Vanaf 95 °C zet de helper zelf alles op vol
                    Path { p in let x = pos(95, 0).x; p.move(to: CGPoint(x: x, y: 0)); p.addLine(to: CGPoint(x: x, y: h)) }
                        .stroke(.red.opacity(0.5), style: StrokeStyle(lineWidth: 1, dash: [3, 3]))
                    Path { p in
                        p.move(to: CGPoint(x: 0, y: pos(r.lowerBound, pts[0].p).y))
                        for pt in pts { p.addLine(to: pos(pt.t, pt.p)) }
                        p.addLine(to: CGPoint(x: w, y: pos(r.upperBound, pts[pts.count - 1].p).y))
                    }
                    .stroke(Color.accentColor, lineWidth: 2)
                    ForEach(pts.indices, id: \.self) { i in
                        Circle().fill(Color.accentColor).frame(width: 12, height: 12)
                            .overlay(Circle().stroke(.background, lineWidth: 2))
                            .frame(width: 24, height: 24).contentShape(.circle)
                            .gesture(DragGesture(minimumDistance: 0, coordinateSpace: .named("curve")).onChanged { v in move(i, v.location, w, h) })
                            .position(pos(pts[i].t, pts[i].p))
                            .accessibilityLabel("Point \(i + 1): \(Int(pts[i].t)) °C, \(Int(pts[i].p))%")
                    }
                    if let temp {
                        Circle().fill(.orange).frame(width: 10, height: 10)
                            .position(pos(Swift.min(Swift.max(temp, r.lowerBound), r.upperBound), FanCurve.percent(at: temp, pts)))
                            .allowsHitTesting(false)
                    }
                }
                .coordinateSpace(.named("curve"))
            }
            .frame(height: 170)
            HStack { Text("40 °C"); Spacer(); Text("70 °C"); Spacer(); Text("95 °C: full speed").foregroundStyle(.red.opacity(0.8)) }
                .font(.caption2).foregroundStyle(.secondary)
            Text("Bottom = macOS's minimum speed, top = full speed.").font(.caption).foregroundStyle(.secondary)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }

    /// Punt `i` verslepen: blijft tussen zijn buren (minstens 1 °C ertussen), afgerond op hele graden en procenten.
    private func move(_ i: Int, _ loc: CGPoint, _ w: CGFloat, _ h: CGFloat) {
        guard w > 0, h > 0 else { return }
        var pts = FanCurve.points(raw)
        let r = FanCurve.range
        let lo = i == 0 ? r.lowerBound : pts[i - 1].t + 1, hi = i == pts.count - 1 ? r.upperBound : pts[i + 1].t - 1
        let t = (r.lowerBound + Double(loc.x / w) * (r.upperBound - r.lowerBound)).rounded()
        if lo <= hi { pts[i].t = Swift.min(Swift.max(t, lo), hi) }
        pts[i].p = Swift.min(Swift.max((100 * (1 - Double(loc.y / h))).rounded(), 0), 100)
        raw = FanCurve.format(pts)
    }
}

// MARK: - Stof: ventilator hard terwijl de Mac bijna niets doet (kolom ventilator_pct in de meterkast-log)

enum FanDust {
    static let fastPct = 60.0, calm = 20.0, needMinutes = 60
    static let tip = "Your fan spins hard while your Mac is barely busy — the vents may be dusty. Clean them or have them cleaned."

    /// Afgelopen 7 dagen samen minstens een uur verdacht? Macs zonder ventilator: nooit. Afgesloten dagen veranderen niet: één keer lezen.
    static func check(dir: URL, now: Date = Date()) -> Bool {
        guard Fans.count > 0 else { return false }
        let f = DateFormatter(); f.locale = Locale(identifier: "en_US_POSIX"); f.calendar = Calendar(identifier: .gregorian); f.dateFormat = "yyyy-MM-dd"
        var total = 0
        for back in 0..<7 {
            guard let d = Calendar.current.date(byAdding: .day, value: -back, to: now) else { continue }
            let name = f.string(from: d)
            lock.lock(); let hit = back > 0 ? cache[name] : nil; lock.unlock()
            if let hit { total += hit; continue }
            let n = (try? String(contentsOf: dir.appendingPathComponent("meterkast-\(name).csv"), encoding: .utf8)).map(minutes) ?? 0
            if back > 0 { lock.lock(); cache[name] = n; lock.unlock() }   // vandaag groeit nog
            total += n
        }
        return total >= needMinutes
    }
    private static let lock = NSLock()
    nonisolated(unsafe) private static var cache: [String: Int] = [:]

    /// Eén dag-log → minuten met de ventilator boven `fastPct` terwijl CPU én GPU al minstens 3 minuten onder `calm` zitten
    /// (zo telt nadraaien na zwaar werk niet). Kolommen op naam; oude logs zonder ventilator_pct geven 0.
    static func minutes(_ text: String) -> Int {
        let lines = text.split(separator: "\n")
        var col: [Substring: Int] = [:]
        for (i, c) in (lines.first ?? "").split(separator: ",").enumerated() { col[c] = i }
        guard let iF = col["ventilator_pct"], let iC = col["cpu_pct"] else { return 0 }
        let iG = col["gpu_pct"]
        var key: Substring = "", n = 0.0, cpu = 0.0, gpu = 0.0, fan = 0.0, fanN = 0.0, streak = 0, out = 0
        func close() {
            guard n > 0 else { return }
            streak = cpu / n < calm && gpu / n < calm ? streak + 1 : 0
            if streak >= 3, fanN > n / 2, fan / fanN > fastPct { out += 1 }
        }
        for line in lines.dropFirst() {
            let v = line.split(separator: ",", omittingEmptySubsequences: false)
            guard v.count > max(iF, iC), v[0].count >= 16, let c = Double(v[iC]) else { continue }
            let k = v[0].prefix(16)
            if k != key { close(); key = k; n = 0; cpu = 0; gpu = 0; fan = 0; fanN = 0 }
            n += 1; cpu += c
            gpu += iG.flatMap { $0 < v.count ? Double(v[$0]) : nil } ?? 0
            if let x = Double(v[iF]) { fan += x; fanN += 1 }
        }
        close()
        return out
    }

    static func selfTest() {
        let head = Sample.csvHeader
        precondition(head.contains(",ventilator_pct,"), "ventilator-kolom")
        // 90 minuten, elke 30 s een regel; `cpu` en `fan` per minuut
        func log(cpu: (Int) -> Double, fan: (Int) -> Double?) -> String {
            var s = head
            for m in 0..<90 { for sec in ["00", "30"] {
                var x = Sample(); x.cpu = cpu(m)
                x.time = Sample.stamp.date(from: String(format: "2026-11-16 %02d:%02d:%@", 10 + m / 60, m % 60, sec))!
                x.fans = fan(m).map { [FanReading(rpm: $0 * 57, min: 1200, max: 5700)] } ?? []
                s += x.csv
            } }
            return s
        }
        let dusty = minutes(log(cpu: { _ in 5 }, fan: { _ in 80 }))
        precondition(dusty == 88, "stof: \(dusty)")                                         // eerste 2 minuten: nog geen 3 rustige op rij
        precondition(minutes(log(cpu: { _ in 70 }, fan: { _ in 90 })) == 0, "zwaar werk = geen stof")
        precondition(minutes(log(cpu: { _ in 5 }, fan: { _ in 30 })) == 0, "rustige ventilator")
        precondition(minutes(log(cpu: { $0 < 45 ? 80 : 5 }, fan: { _ in 80 })) == 43, "nadraaien telt niet")   // 45..89 = 45, min 2 voor de reeks
        precondition(minutes(log(cpu: { _ in 5 }, fan: { _ in nil })) == 0, "geen ventilator")
        precondition(minutes("tijd,cpu_pct\n2026-11-16 10:00:00,5\n") == 0 && minutes("") == 0, "oude log")
        var forced = Sample(); forced.fans = [FanReading(rpm: 5000, min: 1200, max: 5700, forced: true)]
        precondition(forced.fanPct == nil && Sample().fanPct == nil, "helper stuurt = niet meetellen")
        print("stof ok")
    }
}
