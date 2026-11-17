import SwiftUI
import ServiceManagement
import UserNotifications

// MARK: - App

/// Venster dicht ≠ app stoppen: zo blijft Meterkast meten (instelling "keepRunning", standaard aan). Alleen ⌘Q stopt echt.
final class AppDelegate: NSObject, NSApplicationDelegate, UNUserNotificationCenterDelegate {
    /// Meldingen ook tonen als Fusebox vooraan staat (anders laat macOS ze stil weg).
    func userNotificationCenter(_ center: UNUserNotificationCenter, willPresent notification: UNNotification) async -> UNNotificationPresentationOptions {
        [.banner, .sound]
    }

    /// Na de naamswijziging (Meterkast → Fusebox) "Open at login" eenmalig opnieuw aanmelden, zodat macOS de nieuwe app start.
    /// Nieuwe versie geopend terwijl er al één in Programma's staat? Dan bijwerken i.p.v. een tweede kopie (Installer.swift).
    func applicationWillFinishLaunching(_ notification: Notification) {
        MainActor.assumeIsolated { Installer.checkOnLaunch() }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        UNUserNotificationCenter.current().delegate = self
        Hotkey.apply()   // sneltoets om Fusebox te tonen/verbergen
        // Meldingen aan maar nog nooit toestemming gevraagd? Dan laat macOS ze stil vallen. Vraagt alleen de eerste keer echt.
        if UserDefaults.standard.bool(forKey: "notify") { Notify.ask() }
        MainActor.assumeIsolated { Updater.shared.start() }   // automatisch bijwerken via GitHub Releases
        let d = UserDefaults.standard
        guard !d.bool(forKey: "loginRefreshedFusebox") else { return }
        d.set(true, forKey: "loginRefreshedFusebox")
        if SMAppService.mainApp.status == .enabled { try? SMAppService.mainApp.unregister(); try? SMAppService.mainApp.register() }
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool {
        !UserDefaults.standard.bool(forKey: "keepRunning")
    }
}

struct MeterkastApp: App {
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate
    // @State, niet @StateObject: de App zelf hoeft niet elke 2 s opnieuw (anders worden alle vensters en menubalk-plekjes herbouwd)
    @State private var meter = Meter()
    @AppStorage("menubarStyle") private var style: MenubarStyle = .text
    @AppStorage("menubarMetrics") private var menubarMetrics = "cpu"
    @AppStorage("menubarSplit") private var split = false
    @AppStorage("menuStyle") private var menuStyle: MenuStyle = .menu
    @AppStorage("setupDone") private var setupDone = false
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system

    var body: some Scene {
        Window("Fusebox", id: "main") {
            RootView(meter: meter).environmentObject(meter)
        }
        .windowToolbarStyle(.unifiedCompact(showsTitle: false))
        .windowBackgroundDragBehavior(.enabled)
        .defaultSize(width: 300, height: 340)
        // Altijd openen bij de start (ook als het venster de vorige keer dicht was); grootte/plek regelt RootView zelf
        .defaultLaunchBehavior(.presented)
        .restorationBehavior(.disabled)

        Settings { SettingsView().environmentObject(meter) }

        Window("Set up Fusebox", id: "setup") { SetupView().environmentObject(meter).preferredColorScheme(mode.scheme) }
            .windowResizability(.contentSize)
            .defaultLaunchBehavior(setupDone ? .suppressed : .presented)
            .restorationBehavior(.disabled)

        extra(0)
        extra(1)
        extra(2)
    }

    /// Menubalk-plekje `i` (1 en 2 alleen bij "elke meting een eigen plekje"), als menu of als paneel.
    @SceneBuilder private func extra(_ i: Int) -> some Scene {
        // Verborgen plekjes volgen de meter niet (scheelt elke 2 s werk voor niets)
        let menu = shown(i, panel: false), panel = shown(i, panel: true)
        MenuBarExtra(isInserted: menu) {
            if menu.wrappedValue { MenuContent().environmentObject(meter) }
        } label: { MenuLabel(index: i, live: menu.wrappedValue).environmentObject(meter) }
        .menuBarExtraStyle(.menu)

        MenuBarExtra(isInserted: panel) {
            if panel.wrappedValue { MenuPanel().environmentObject(meter) }
        } label: { MenuLabel(index: i, live: panel.wrappedValue).environmentObject(meter) }
        .menuBarExtraStyle(.window)
    }

    private func shown(_ i: Int, panel: Bool) -> Binding<Bool> {
        let count = min(Metric.enabled(menubarMetrics).count, 3)
        return Binding(
            get: { style != .off && (menuStyle == .panel) == panel && (i == 0 || (split && style != .icon && i < count)) },
            // macOS zet dit bij het aanmaken soms even op false; dat is geen keuze van de gebruiker, dus niets opslaan.
            set: { _ in })   // weggesleept met ⌘
    }
}

// MARK: - Start (+ zelftest: Meterkast --selftest)

@main enum Entry {
    static func main() {
        Prefs.register()
        LaunchOnce.launched = Date()
        guard CommandLine.arguments.contains("--selftest") else {
            NotificationCenter.default.addObserver(forName: NSApplication.didFinishLaunchingNotification, object: nil, queue: .main) { _ in
                MainActor.assumeIsolated { Prefs.applyDock() }
            }
            MeterkastApp.main()
            return
        }
        Prefs.selfTest()
        Alert.selfTest()
        LiveLayout.selfTest()
        Analysis.selfTest()
        Diag.selfTest()
        FanController.selfTest()
        NetScanner.selfTest()
        Installer.selfTest()
        Updater.selfTest()
        Hotkey.selfTest()
        // Uitgeworpen schijf / herstarte netwerkteller: geen crash, geen absurde waarde in de log
        precondition(Sampler.plausible(1_000_000, 2) && !Sampler.plausible(UInt64(UInt32.max), 2), "herstarte teller")
        var weird = Sample(); weird.diskRead = 9.2e18; weird.down = .infinity
        precondition(weird.csv.contains(",1000000000000000,"), "csv begrensd")
        Ports.selfTest()
        let s = Sampler()
        _ = s.sample()
        let m = AppMonitor()
        m.refresh(totalCPU: 0, totalMemGB: 0)
        sleep(1)
        let x = s.sample()
        precondition((0...100).contains(x.cpu), "cpu \(x.cpu)")   // 0 kan bij een stille seconde
        precondition((1...100).contains(x.ram), "ram \(x.ram)")
        precondition([1, 2, 4].contains(x.pressure), "druk \(x.pressure)")
        precondition((0...100).contains(x.gpu), "gpu \(x.gpu)")
        precondition(x.temp.map { (10...130).contains($0) } ?? true, "temp \(String(describing: x.temp))")
        precondition(x.down >= 0 && x.up >= 0 && x.diskFreeGB > 0, "net/schijf")
        let avg = Sample.average([x, x])
        precondition(abs(avg.cpu - x.cpu) < 0.001, "gemiddelde")
        precondition(Sampler.coreLoad([30, 10, 60, 0, 5, 0, 15, 0], [10, 0, 40, 0, UInt32.max, 0, 5, 0]) == [60, 37.5]
                     && Sampler.coreLoad([1, 2, 3, 4], []) == [], "per kern")   // incl. teller-wrap en lege nulmeting
        precondition(downsample(Array(repeating: 2.0, count: 1000), to: 10) == Array(repeating: 2.0, count: 10), "downsample")
        precondition(short(1_000_000, bits: true) == "\(one(8)) Mbit/s" && Color(hex: "#FF8000")?.hex == "#FF8000", "eenheden/kleur")
        precondition(Metric.allCases.allSatisfy { (0...1).contains($0.fraction(x)) }, "menubalk-fractie")
        let t0 = Date()
        m.refresh(totalCPU: x.cpu, totalMemGB: x.ramUsedGB)
        let ms = Int(-t0.timeIntervalSinceNow * 1000)
        precondition(m.apps.count > 5 && m.apps.contains { $0.id == "macos" }, "apps \(m.apps.count)")
        precondition(m.apps.allSatisfy { $0.cpu >= 0 && $0.memGB >= 0 }, "apps negatief")
        let nt = AppMonitor.parseNettop(",bytes_in,bytes_out,\nlaunchd.1,0,0,\nSome App, Helper.v2.4321,1500,200,\nkapot.x,1,2,\n")
        precondition(nt.count == 2 && nt[4321].map { $0 == (1500, 200) } == true && nt[1].map { $0 == (0, 0) } == true, "nettop \(nt)")
        let nr = AppMonitor.netRates([4321: (500, 100), 7: (900, 9)], [4321: (1500, 200), 7: (10, 9), 8: (5, 5)], dt: 2)
        precondition(nr.count == 1 && nr[4321].map { $0 == (500, 50) } == true, "nettop-delta \(nr)")
        let top = m.apps.prefix(5).map { "\($0.name) \(String(format: "%.1f", $0.cpu))% \(String(format: "%.2f", $0.memGB)) GB" }
        print("apps: \(m.apps.count) groepen in \(ms) ms; top: \(top.joined(separator: " | "))")
        print("ok:", x.csv, terminator: "")
    }
}
