import SwiftUI

// MARK: - Fusebox voor iPhone
//
// Zelfde uitgangspunten als de Mac: licht, rustig, uitleg per waarde. Er loopt niets als de app dicht is, en een
// pagina meet alleen terwijl hij in beeld staat. De tabs heten zoals op de Mac (Live en Docked blijven onvertaald).

@main
struct FuseboxApp: App {
    init() {
        Prefs.register()
        NetPath.start()
        if CommandLine.arguments.contains("--selftest") { SelfTest.run(); exit(0) }
        if CommandLine.arguments.contains("--netinfo") {   // wat iOS over het netwerk meldt (voor het testen op een echt toestel)
            let done = DispatchSemaphore(value: 0)
            Task.detached { await NetPath.whenReady(); done.signal() }
            done.wait()
            let p = NetPath.path
            print("path:", p.status, "wifi", p.usesInterfaceType(.wifi), "cell", p.usesInterfaceType(.cellular), "wired", p.usesInterfaceType(.wiredEthernet))
            print("interfaces:", p.availableInterfaces.map { "\($0.name)/\($0.type)" }, "gateways:", p.gateways.count, "router:", NetPath.router.map(ipString) ?? "nil")
            print("netinfo:", NetInfo.current().map { "\($0.iface) \(ipString($0.ip)) mask \(ipString($0.mask)) router \($0.router.map(ipString) ?? "nil")" } ?? "nil")
            exit(0)
        }
    }
    var body: some Scene { WindowGroup { RootView() } }
}

struct RootView: View {
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("appearanceMode") private var modeID = "system"
    @AppStorage("accent") private var accent = ""
    @AppStorage("textScale") private var textScale = 1.0
    @AppStorage("font") private var font = "style"
    @Environment(\.colorScheme) private var system
    @Environment(\.scenePhase) private var phase
    @Environment(\.accessibilityReduceMotion) private var reduceMotion
    @State private var page = "network"

    var body: some View {
        let _ = (accent, textScale, font)   // opnieuw tekenen als een van deze instellingen verandert (zitten in Theme.resolved)
        let mode = AppearanceMode(rawValue: modeID) ?? .system
        // Zelfde volgorde als de Mac: thema → eigen accent/tekstgrootte/lettertype → licht of donker
        let theme = Theme.resolved(themeID).adapted(to: mode.scheme ?? system)
        TabView(selection: $page) {
            Tab(value: "network") { NetworkScreen() } label: { Label(L("Network"), systemImage: "network") }
            Tab(value: "live") { LiveScreen() } label: { Label { Text(verbatim: "Live") } icon: { Image(systemName: "gauge.with.needle") } }
            Tab(value: "docked") { DockedScreen() } label: { Label { Text(verbatim: "Docked") } icon: { Image(systemName: "dock.rectangle") } }
            Tab(value: "settings") { SettingsScreen() } label: { Label(L("Settings"), systemImage: "gearshape") }
        }
        .environment(\.theme, theme)
        // Energiebesparingsmodus of "Verminder beweging": geen animaties, net als Low impact op de Mac
        .environment(\.onScreen, phase == .active && !reduceMotion && !ProcessInfo.processInfo.isLowPowerModeEnabled)
        .tint(theme.accent)
        .preferredColorScheme(mode.scheme)
    }
}

/// Zelfde zelftests als `Fusebox --selftest` op de Mac, voor de gedeelde netwerkcode. Starten met het argument --selftest.
enum SelfTest {
    @MainActor static func run() {
        NetScanner.selfTest()
        SpeedMath.selfTest()
        InternetStats.findSelfTest()
        NetConnection.selfTest()
        // Bonjour-diensten moeten vooraf in Info.plist staan, anders weigert iOS het zoeken
        let declared = Bundle.main.object(forInfoDictionaryKey: "NSBonjourServices") as? [String] ?? []
        precondition(Set(declared) == Set(NetScanner.serviceTypes), "NSBonjourServices in project.yml loopt niet gelijk met NetScanner.serviceTypes")
        precondition(Diag.scrub("router 192.168.1.1 B8:27:EB:01:02:03") == "router x.x.x.x xx:xx:xx:xx:xx:xx", "scrub")
        precondition(L("Network") != "" && Theme.all.count == 6, "thema's")
        // Mobiele data: nooit over een router praten
        let ok = InternetStats.findMobile(internet: PingStats(Array(repeating: 30, count: 20), sent: 20), dnsMs: 20)
        precondition(ok.ok == true && ok.text == "No problem found: mobile data, DNS and internet are all fine" && ok.numbers == "internet 30 ms, DNS 20 ms", ok.text + ok.numbers)
        precondition(InternetStats.findMobile(internet: PingStats([], sent: 20), dnsMs: nil).text.hasPrefix("No answer from the internet over mobile data"), "mobiel dood")
        precondition(InternetStats.mobileVerdict(PingStats(Array(repeating: 300, count: 10), sent: 10))?.ok == nil && InternetStats.mobileVerdict(nil) == nil, "mobiel traag")
        precondition(InternetStats.findMobile(internet: PingStats(Array(repeating: 30, count: 20), sent: 20), dnsMs: nil).ok == false, "mobiel dns")
        print("iphone: ok")
    }
}
