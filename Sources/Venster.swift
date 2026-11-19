import SwiftUI

// MARK: - Pagina's en venster

enum Page: String, CaseIterable, Identifiable {
    case overview = "live", programs = "programs", analysis = "advice", network = "network", ports = "ports"
    var id: String { rawValue }
    /// Tekst voor de gebruiker; rawValue is een vaste korte id (veilig om op te slaan en te delen).
    var title: String {
        switch self {
        case .overview: L("Live")
        case .programs: L("Processes")
        case .analysis: L("Checkup")
        case .network: L("Network")
        case .ports: L("Docked")
        }
    }
    var icon: String {
        switch self {
        case .overview: "gauge.with.needle"
        case .programs: "square.grid.2x2"
        case .analysis: "chart.bar.xaxis"
        case .network: "network"
        case .ports: "dock.rectangle"
        }
    }
    /// Eén vensterformaat voor alle tabbladen. Heeft een tabblad meer nodig, dan groeit het venster tot hier
    /// (alleen groter, nooit kleiner); zelf kleiner slepen mag altijd, elke pagina heeft een smalle indeling.
    var minSize: NSSize {
        switch self {
        case .overview: .zero
        case .programs: NSSize(width: 460, height: 420)   // vanaf 460 ook de schijfkolom
        case .analysis: NSSize(width: 420, height: 460)
        case .network: NSSize(width: 470, height: 440)
        case .ports: NSSize(width: 420, height: 460)
        }
    }
}

/// Geeft het NSWindow waar deze view in staat (voor het vergroten/verkleinen per tabblad).
struct WindowGrabber: NSViewRepresentable {
    let onWindow: (NSWindow) -> Void
    func makeNSView(context: Context) -> NSView {
        let v = NSView()
        DispatchQueue.main.async { if let w = v.window { onWindow(w) } }
        return v
    }
    func updateNSView(_ nsView: NSView, context: Context) {}
}

struct RootView: View {
    let meter: Meter   // niet observeren: alleen de pagina's zelf volgen de meter (scheelt herberekenen elke 2 s)
    @AppStorage("page") private var page: Page = .overview
    @AppStorage("theme") private var themeID = Theme.instrument.id
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @Environment(\.colorScheme) private var systemScheme
    @AppStorage("accent") private var accent = ""
    @AppStorage("textScale") private var textScale = 1.0
    @AppStorage("font") private var font = "style"   // lettertype gewijzigd → meteen opnieuw tekenen
    @AppStorage("onTop") private var onTop = false
    @State private var window: NSWindow?
    @State private var wide = false          // breed venster: tabknoppen met naam
    @State private var hover: Page?          // tabknop onder de muis
    @Namespace private var dropNS
    @State private var stretch: CGFloat = 1   // >1 tijdens het verschuiven: druppel rekt even uit
    @State private var stretchFrom: UnitPoint = .leading   // kant van het icoon dat je verlaat: daar "trekt" de druppel uit
    /// Vanaf deze breedte passen alle tabknoppen mét naam; smaller → alleen icoontjes (anders verstopt macOS ze achter ">>").
    /// Terug naar icoontjes pas onder `iconsWidth`: rond de grens niet heen en weer springen tijdens slepen.
    /// Met naam is ~660 pt nodig (incl. verkeerslichtjes, vijf tabbladen), dus ook bij 700 nooit afgekapt.
    static let namesWidth: CGFloat = 740, iconsWidth: CGFloat = 700
    @Environment(\.openWindow) private var openWindow
    @State private var baseBehavior: NSWindow.CollectionBehavior?
    @State private var onScreen = true       // venster te zien? Anders animeert er niets (kost processor voor niets)
    @State private var low = LowImpact.on    // low impact: effen kleuren, geen animaties

    var body: some View {
        let _ = (accent, textScale, font) // afhankelijkheid: kleur/tekstgrootte/lettertype wijzigen → opnieuw tekenen
        // Licht/donker: vast gekozen, of zoals macOS (dan bepaalt het systeem de variant van de stijl)
        let target: ColorScheme = mode == .light ? .light : mode == .dark ? .dark : systemScheme
        let styled = Theme.resolved(themeID).adapted(to: target), theme = low ? styled.solid : styled
        let pages = Page.allCases   // altijd alle pagina's
        VStack(spacing: 0) {
            Group {
                switch page {
                case .overview: OverviewView()
                case .programs: ProgramsView()
                case .analysis: AnalysisView()
                case .network: NetworkView()
                case .ports: PortsView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            // Tabwissel: nieuwe pagina vervaagt en schuift een stukje omhoog in beeld
            .id(page)
            .transition(.opacity.combined(with: .offset(y: 10)))
        }
        .animation(.easeOut(duration: 0.22), value: page)
        // Low impact: geen enkele animatie in het venster (tabwissel, detail openen, inloop)
        .transaction { if low { $0.disablesAnimations = true; $0.animation = nil } }
        // Alleen bij het passeren van een grens iets wijzigen (de werkbalk wordt dan opnieuw opgebouwd)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { w in
            let next = wide ? w >= Self.iconsWidth : w >= Self.namesWidth
            if next != wide { wide = next }
        }
        .frame(minWidth: 316, minHeight: 120)   // 6 tabknoppen (32 pt + 2 pt) + verkeerslichtjes passen altijd, nooit ">>"
        // Tabknoppen als echte werkbalk-items: in de titelbalk vangt macOS gewone klikken af (venster slepen).
        // .id(wide): bij wissel smal↔breed de knoppen opnieuw opbouwen, anders houdt de werkbalk de oude breedte (afgekapte namen)
        .toolbar { ToolbarItem(placement: .primaryAction) { header(theme, pages).id(wide) }.sharedBackgroundVisibility(.hidden) }
        .toolbar(removing: .title)
        .windowToolbarFullScreenVisibility(.visible)   // ook schermvullend de tabknoppen tonen
        .toolbarBackgroundVisibility(.hidden, for: .windowToolbar)
        .foregroundStyle(theme.text)
        .tint(theme.accent)
        .environment(\.theme, theme)
        .environment(\.onScreen, onScreen && !low)
        .onReceive(NotificationCenter.default.publisher(for: LowImpact.changed)) { _ in low = LowImpact.on }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didChangeOcclusionStateNotification)) { n in
            guard let w = window, n.object as? NSWindow === w else { return }
            let now = w.occlusionState.contains(.visible)
            if now != onScreen { onScreen = now }
        }
        .preferredColorScheme(mode == .system ? nil : target)
        .containerBackground(theme.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), for: .window)
        .background(WindowGrabber { w in
            window = w
            applyOnTop(w)
            // Bij het opstarten altijd zo openen: Live, in de onthouden venstergrootte (linksboven blijft staan)
            if !LaunchOnce.sized {
                LaunchOnce.sized = true
                place(w, size: savedSize(), animate: false, topLeft: savedTopLeft())
                // Opstart-animatie: venster rustig in beeld laten vervagen
                w.alphaValue = 0
                NSAnimationContext.runAnimationGroup { ctx in
                    ctx.duration = 0.18
                    ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
                    w.animator().alphaValue = 1
                }
            }
        })
        .onAppear {
            Hotkey.openMain = { [openWindow] in openWindow(id: "main") }
            if !pages.contains(page) { page = pages[0] }
            meter.programsVisible = page == .programs
            // Bij herstelde vensters negeert macOS `defaultLaunchBehavior`; daarom hier expliciet (eenmalig per start).
            if !LaunchOnce.done {
                LaunchOnce.done = true
                page = .overview   // altijd op Live openen
                let d = UserDefaults.standard
                if !d.bool(forKey: "setupDone") { openWindow(id: "setup") }
            }
        }
        // Het venster doet precies wat je sleept: niets klikt achteraf terug. Plek (linksboven) en grootte onthouden
        // voor de volgende start; tijdens slepen niet elke pixel opslaan, pas bij loslaten.
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didMoveNotification)) { n in
            guard let w = window, n.object as? NSWindow === w, !w.inLiveResize else { return }
            saveTopLeft(w)
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didEndLiveResizeNotification)) { n in
            guard let w = window, n.object as? NSWindow === w, !fullScreen(w) else { return }
            saveTopLeft(w)
            // Live heeft zijn eigen grootte; de andere tabbladen delen er samen één
            UserDefaults.standard.set(NSStringFromSize(w.frame.size), forKey: page == .overview ? "liveSize" : "windowSize")
        }
        .onChange(of: onTop) { if let window { applyOnTop(window) } }
        .onChange(of: page) { _, new in
            LaunchOnce.launched = Date()   // inloop-animatie (rijen, lijnen) opnieuw afspelen op de nieuwe pagina
            LaunchOnce.tabSwitch = true    // … maar sneller dan bij het opstarten
            meter.programsVisible = new == .programs
            resize(to: new)
        }
    }

    /// Tabknoppen in de werkbalk, rechts van de verkeerslichtjes; verborgen bij maar één tabblad.
    private func header(_ theme: Theme, _ pages: [Page]) -> some View {
        let shape = RoundedRectangle(cornerRadius: 8, style: .continuous)
        return HStack(spacing: 2) {
            if pages.count > 1 {
                ForEach(Array(pages.enumerated()), id: \.element) { i, p in
                    Button { switchTo(p) } label: {
                        tabLabel(p.title, p.icon)
                            .foregroundStyle(page == p ? theme.accent : hover == p ? theme.text : theme.secondary)
                            .background {
                                if page == p {   // de "druppel": SwiftUI schuift hem zelf naar de actieve knop
                                    shape.fill(theme.accent.opacity(0.15))
                                        .scaleEffect(x: stretch, y: 2 - stretch, anchor: stretchFrom)
                                        .matchedGeometryEffect(id: "drop", in: dropNS)
                                } else if hover == p {   // zachte markering onder de muis: je ziet dat het een knop is
                                    shape.fill(theme.secondary.opacity(0.12))
                                }
                            }
                            .contentShape(.rect)
                            .onHover { hover = $0 ? p : hover == p ? nil : hover }
                    }
                    .buttonStyle(.plain).help("\(p.title)  ⌘\(i + 1)").accessibilityLabel(p.title)
                    .keyboardShortcut(KeyEquivalent(Character("\(i + 1)")), modifiers: .command)
                }
            }
            SettingsLink {
                tabLabel(L("Settings"), "gearshape").foregroundStyle(theme.secondary).contentShape(.rect)
                    // Low impact aan: klein blaadje bij het tandwiel (geen extra knop: de werkbalk is precies breed genoeg)
                    .overlay(alignment: .topTrailing) {
                        if low { Image(systemName: "leaf.fill").font(.system(size: 8)).foregroundStyle(theme.good).padding(3) }
                    }
            }
            .buttonStyle(.plain).help(low ? "Settings  ⌘,  ·  Low impact is on" : "Settings  ⌘,").accessibilityLabel("Settings")
        }
        .padding(.horizontal, 4)
        // Liquid Glass: de tabbalk is het echte glas (bediening, verandert alleen bij een klik). Low impact: geen glas.
        .padding(.vertical, theme.rowStyle == .glass ? 2 : 0)
        .glassEffect(theme.rowStyle == .glass ? .regular : .identity, in: .capsule)
        .accessibilityElement(children: .contain)   // elke knop eigen naam (anders noemt de werkbalk ze allemaal "Live")
    }

    /// Vaste maat per knop (groot genoeg om makkelijk te raken); alleen de naam komt erbij als het venster breed is.
    private func tabLabel(_ title: String, _ icon: String) -> some View {
        Label(title, systemImage: icon)
            .labelStyle(wide ? AnyLabelStyle(.titleAndIcon) : AnyLabelStyle(.iconOnly))
            .font(.system(size: 13, weight: .medium))
            .frame(minWidth: 32, minHeight: 28)
            .padding(.horizontal, wide ? 8 : 0)
    }

    /// Tab wisselen met een "trekkende druppel": verschuiven met een veer, onderweg kort uitrekken en dan bijtrekken.
    private func switchTo(_ p: Page) {
        guard p != page else { return }
        let all = Page.allCases, right = (all.firstIndex(of: p) ?? 0) > (all.firstIndex(of: page) ?? 0)
        stretchFrom = right ? .leading : .trailing   // naar rechts: links vast, rekt uit naar rechts (en andersom)
        withAnimation(.easeOut(duration: 0.12)) { stretch = 1.35 }
        withAnimation(.spring(response: 0.38, dampingFraction: 0.7)) { page = p }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) {
            withAnimation(.spring(response: 0.3, dampingFraction: 0.55)) { stretch = 1 }
        }
    }

    /// "Altijd bovenop": zwevend en op elk bureaublad / naast schermvullende apps. Uit = oorspronkelijk gedrag terug.
    private func applyOnTop(_ w: NSWindow) {
        let base = baseBehavior ?? w.collectionBehavior
        if baseBehavior == nil { baseBehavior = base }
        w.level = onTop ? .floating : .normal
        // fullScreenPrimary en fullScreenAuxiliary sluiten elkaar uit; daarom niet [] maar de basis herstellen.
        w.collectionBehavior = onTop
            ? base.subtracting([.fullScreenPrimary, .fullScreenNone, .moveToActiveSpace]).union([.canJoinAllSpaces, .fullScreenAuxiliary])
            : base
    }

    private func fullScreen(_ w: NSWindow) -> Bool { w.styleMask.contains(.fullScreen) }

    /// Naar Live: terug naar de Live-grootte die je zelf gaf. Naar een ander tabblad: de gedeelde grootte van
    /// die tabbladen (nog nooit gesleept: de huidige), minstens zo groot als dat tabblad nodig heeft. Linksboven blijft staan.
    private func resize(to p: Page) {
        guard let w = window, !fullScreen(w), !w.inLiveResize else { return }
        let d = UserDefaults.standard
        let saved = d.string(forKey: p == .overview ? "liveSize" : "windowSize").map(NSSizeFromString) ?? w.frame.size
        let s = NSSize(width: max(saved.width, p.minSize.width), height: max(saved.height, p.minSize.height))
        if s != w.frame.size { place(w, size: s, animate: true) }
    }

    private func saveTopLeft(_ w: NSWindow) {
        guard !fullScreen(w) else { return }
        UserDefaults.standard.set(NSStringFromPoint(NSPoint(x: w.frame.minX, y: w.frame.maxY)), forKey: "windowTopLeft")
    }

    /// Laatst gebruikte plek (linksboven), alleen als die nog op een aangesloten scherm ligt.
    private func savedTopLeft() -> NSPoint? {
        guard let p = UserDefaults.standard.string(forKey: "windowTopLeft").map(NSPointFromString),
              NSScreen.screens.contains(where: { $0.visibleFrame.insetBy(dx: -1, dy: -1).contains(NSPoint(x: p.x + 20, y: p.y - 20)) }) else { return nil }
        return p
    }

    /// Onthouden Live-grootte (daarmee opent hij). Eenmalig overgezet van de oudere instellingen.
    private func savedSize() -> NSSize {
        let d = UserDefaults.standard
        if d.string(forKey: "liveSize") == nil, let old = d.string(forKey: "size.live") ?? d.string(forKey: "windowSize") { d.set(old, forKey: "liveSize") }
        for p in Page.allCases { d.removeObject(forKey: "size.\(p.rawValue)") }
        return d.string(forKey: "liveSize").map(NSSizeFromString) ?? NSSize(width: 300, height: 340)
    }

    /// Venster naar `size`, linksboven op zijn plek. Past het niet (kleine schermen, 13-inch): eerst verschuiven
    /// (omhoog/naar links), pas kleiner maken als het venster groter is dan het hele scherm. Eerder kromp het venster
    /// tot een strookje als het laag op het scherm stond.
    private func place(_ w: NSWindow, size: NSSize, animate: Bool, topLeft: NSPoint? = nil) {
        var size = size
        var top = topLeft?.y ?? w.frame.maxY, left = topLeft?.x ?? w.frame.minX
        if let vf = (w.screen ?? NSScreen.main)?.visibleFrame {
            (left, top, size) = Self.fit(left: left, top: top, size: size, in: vf)
        }
        let f = NSRect(x: left, y: top - size.height, width: size.width, height: size.height)
        // `wide` niet hier zetten: dan bouwt de werkbalk al met namen op terwijl het venster nog smal is (">>", scheve druppel)
        guard animate else { w.setFrame(f, display: true); return }
        NSAnimationContext.runAnimationGroup { ctx in      // soepel, ~0,2 s
            ctx.duration = 0.2
            ctx.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            w.animator().setFrame(f, display: true)
        }
    }
}

extension RootView {
    /// Venster binnen het zichtbare scherm: maat hoogstens het scherm (minimaal 316 × 160), daarna schuiven tot het past.
    static func fit(left: CGFloat, top: CGFloat, size: NSSize, in vf: NSRect) -> (CGFloat, CGFloat, NSSize) {
        let s = NSSize(width: max(316, min(size.width, vf.width)), height: max(160, min(size.height, vf.height)))
        let l = min(max(left, vf.minX), vf.maxX - s.width)
        let t = max(min(top, vf.maxY), vf.minY + s.height)
        return (l, t, s)
    }

    static func fitSelfTest() {
        let vf = NSRect(x: 0, y: 0, width: 1440, height: 800)      // 13-inch: 1440 × 900 min menubalk en Dock
        let (l, t, s) = fit(left: 1300, top: 300, size: NSSize(width: 600, height: 700), in: vf)
        precondition(s == NSSize(width: 600, height: 700) && l == 840 && t == 700, "schuiven i.p.v. krimpen")
        let (_, t2, s2) = fit(left: 10, top: 790, size: NSSize(width: 2000, height: 1200), in: vf)
        precondition(s2 == NSSize(width: 1440, height: 800) && t2 == 800, "groter dan scherm")
        print("vensterplek ok")
    }
}

/// Eenmalige acties bij het starten van de app.
enum LaunchOnce {
    @MainActor static var done = false, sized = false
    /// Moment van starten of van een tabwissel; de inloop-animaties spelen alleen vlak daarna.
    nonisolated(unsafe) static var launched = Date()
    static var intro: Bool { !LowImpact.on && Date().timeIntervalSince(launched) < 1.2 }   // low impact: geen inloop
    /// Na een tabwissel: dezelfde inloop, maar ruim twee keer zo snel (alleen het opstarten krijgt de rustige versie).
    nonisolated(unsafe) static var tabSwitch = false
    static var pace: Double { tabSwitch ? 0.4 : 1 }
}

/// Wisselen tussen labelstijlen (alleen icoon / icoon + naam) zonder twee views te schrijven.
struct AnyLabelStyle: LabelStyle {
    private let make: (Configuration) -> AnyView
    init(_ style: some LabelStyle) { make = { AnyView(style.makeBody(configuration: $0)) } }
    func makeBody(configuration: Configuration) -> some View { make(configuration) }
}
