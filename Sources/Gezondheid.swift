import SwiftUI

// MARK: - Gezondheid: geheugenlekken, crashlussen, vastlopers en wat macOS op de achtergrond doet
//
// Lekken, vastlopers en de huishouding van macOS zelf zitten al in Diagnose.swift; hier alleen wat erbij kwam.
// Niets draait op een timer: alleen bij het openen van Checkup, buiten de hoofdthread (Task.detached).

extension Diag {
    /// Crashlus: zoveel crashes van één app binnen een uur.
    static let loopCount = 5, loopWindow: TimeInterval = 3600

    /// Per app het grootste aantal crashes binnen één uur (schuivend venster); alleen apps vanaf `loopCount`.
    static func crashLoops(_ times: [String: [Date]]) -> [String: Int] {
        times.compactMapValues { t in
            let s = t.sorted()
            var best = 0, i = 0
            for j in s.indices {
                while s[j].timeIntervalSince(s[i]) > loopWindow { i += 1 }
                best = max(best, j - i + 1)
            }
            return best >= loopCount ? best : nil
        }
    }

    static let coolingDown = L("Your Mac is cooling down (kernel_task slows it on purpose)")
    /// kernel_task (pid 0) is zonder root niet te meten; als hij hoog staat is dat vrijwel altijd koelen, en dat meldt thermalState.
    static func isCooling(_ s: ProcessInfo.ThermalState) -> Bool { s == .serious || s == .critical }

    static func healthSelfTest() {
        let t0 = Date(timeIntervalSince1970: 1_790_000_000)
        func pts(_ n: Int, _ mem: (Int) -> Double) -> [Point] { (0..<n).map { Point(t: t0.addingTimeInterval(Double($0) * 60), cpu: 1, mem: mem($0)) } }

        // Lek: 6 uur van 1 naar 3,4 GB = +0,4 GB per uur
        let l = leaks(["Safari": pts(360) { 1 + 2.4 * Double($0) / 359 }])
        precondition(l.count == 1 && abs(l[0].perHour - 0.4) < 0.02 && l[0].hours == 6, "lek per uur \(l)")
        // Ruis rond 3 GB (±0,8 GB, pseudo-willekeurig) mag niet als lek tellen
        var seed: UInt64 = 42
        let noisy = pts(480) { _ in seed = seed &* 6364136223846793005 &+ 1442695040888963407; return 3 + Double(seed >> 40) / Double(1 << 24) * 1.6 - 0.8 }
        precondition(leaks(["Noisy": noisy]).isEmpty, "ruis is geen lek")

        // Crashlus: 6 crashes in 50 minuten wel, 6 verspreid over de dag niet
        let burst = (0..<6).map { t0.addingTimeInterval(Double($0) * 600) }
        let spread = (0..<6).map { t0.addingTimeInterval(Double($0) * 7200) }
        precondition(crashLoops(["Loop": burst, "Spread": spread, "Few": Array(burst.prefix(4))]) == ["Loop": 6], "crashlus")
        var c = Crashes(); c.crashes = ["Loop": 6]; c.times = ["Loop": burst]
        precondition(c.rows.first?.value == "crashed 6×, 6× in one hour", "crashregel \(c.rows)")

        // Vastlopers: spindump-kop met alleen "Process:", en een .spin-rapport
        let hang = "Date/Time:        2026-11-17 10:02:11 +0100\nProcess:          Microsoft Teams [812]\nEvent:            hang\n"
        precondition(parseHeader(hang, fileName: "Microsoft Teams_2026-11-17-100211_x.hang").entry == .hang("Microsoft Teams"), "hang via Process")
        precondition(parseHeader("Command: Foo\nEvent: Spin\n", fileName: "Foo_x.spin").entry == .hang("Foo"), "spin")

        // Proces → uitleg: elke klusnaam bestaat in de vriendelijke namen (zo staan ze in de apps-CSV)
        let friendly = Set(AppMonitor.friendly.values.map(\.0))
        for j in jobs { for n in j.names { precondition(friendly.contains(n), "onbekende naam \(n)") } }
        for (p, doing) in [("mds_stores", "Spotlight is indexing"), ("mdworker_shared", "Spotlight is indexing"), ("photoanalysisd", "Photos is analysing your library"),
                           ("cloudd", "iCloud is syncing"), ("backupd", "Time Machine is making a backup"), ("kernel_task", coolingDown)] {
            precondition(job(proc: p)?.doing == doing, "proces \(p)")
        }
        precondition(isCooling(.serious) && !isCooling(.nominal), "koelen")
        print("gezondheid ok")
    }
}

extension Diag.Leak { var perHour: Double { (to - from) / max(end.timeIntervalSince(start) / 3600, 1) } }
extension Diag.Crashes { var loops: [String: Int] { Diag.crashLoops(times) } }

/// "What macOS is doing": nu bezig (eenmalig bij openen) en wat in het gekozen tijdvak bezig was (uit de apps-log).
struct BackgroundSection: View {
    @Environment(\.theme) private var theme
    let apps: [(name: String, cpu: Double)]
    let seen: [String: Int]     // klus → minuten bezig
    let when: String
    let index: Int
    @State private var now: [String]?

    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            CardTitle(icon: "gearshape.2", title: L("What macOS is doing"))
            if let now {
                Text(now.isEmpty ? L("Nothing special in the background right now.") : now.joined(separator: ". ") + L(" — this passes by itself."))

                    .font(theme.label(11)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
            } else {
                ProgressView().controlSize(.small)
            }
            if !seen.isEmpty {
                Text("Busy \(when)").font(theme.label(11)).foregroundStyle(theme.onRow(true)).padding(.top, 2)
                ForEach(seen.sorted { $0.value > $1.value }, id: \.key) { k, v in
                    HStack(spacing: 6) {
                        Text(k).foregroundStyle(theme.onRow()).lineLimit(1)
                        Spacer(minLength: 4)
                        Text(duration(v)).font(theme.number(10)).foregroundStyle(theme.onRow(true))
                    }
                    .font(theme.label(11))
                }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .task {
            let a = apps, cores = Double(ProcessInfo.processInfo.activeProcessorCount)
            now = await Task.detached(priority: .utility) { Diag.housekeepingNow(apps: a, cores: cores) }.value
        }
    }
}
