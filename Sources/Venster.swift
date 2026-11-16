import SwiftUI

// MARK: - Pagina's en venster

enum Page: String, CaseIterable, Identifiable {
    case overview = "live", programs = "programs", analysis = "advice", network = "network"
    var id: String { rawValue }
    /// Tekst voor de gebruiker; rawValue is een vaste korte id (veilig om op te slaan en te delen).
    var title: String {
        switch self {
        case .overview: "Live"
        case .programs: "Processes"
        case .analysis: "Checkup"
        case .network: "Network"
        }
    }
    var icon: String {
        switch self {
        case .overview: "gauge.with.needle"
        case .programs: "square.grid.2x2"
        case .analysis: "chart.bar.xaxis"
        case .network: "network"
        }
    }
    /// Standaardgrootte per tabblad: overzicht klein, de rest groter. Zelf aanpassen wordt per tabblad onthouden.
    var defaultSize: NSSize {
        switch self {
        case .overview: NSSize(width: 300, height: 340)
        case .programs: NSSize(width: 640, height: 600)
        case .analysis: NSSize(width: 760, height: 720)   // opent in 2 kolommen
        case .network: NSSize(width: 820, height: 780)
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
    @AppStorage("metrics") private var metricsRaw = Metric.defaultOrder
    @AppStorage("showAdvice") private var showAdvice = false
    @AppStorage("onTop") private var onTop = false
    @State private var window: NSWindow?
    @State private var wide = false          // breed venster: tabknoppen met naam
    @Namespace private var dropNS
    @State private var stretch: CGFloat = 1   // >1 tijdens het verschuiven: druppel rekt even uit
    @State private var stretchFrom: UnitPoint = .leading   // kant van het icoon dat je verlaat: daar "trekt" de druppel uit
    /// Vanaf deze breedte passen alle tabknoppen mét naam; smaller → alleen icoontjes (anders verstopt macOS ze achter ">>").
    static let namesWidth: CGFloat = 660
    @Environment(\.openWindow) private var openWindow
    @State private var baseBehavior: NSWindow.CollectionBehavior?
    @State private var lastFit: CGFloat?           // passende vensterhoogte als Live in XS/S staat, anders nil
    @State private var fitUntil = Date.distantFuture // tot dan volgt het venster de inhoud (start, stijl-/metingwissel)

    var body: some View {
        let _ = (accent, textScale) // afhankelijkheid: kleur/tekstgrootte wijzigen → opnieuw tekenen
        // Licht/donker: vast gekozen, of zoals macOS (dan bepaalt het systeem de variant van de stijl)
        let target: ColorScheme = mode == .light ? .light : mode == .dark ? .dark : systemScheme
        let theme = Theme.resolved(themeID).adapted(to: target)
        let pages = Page.allCases   // altijd alle pagina's
        VStack(spacing: 0) {
            Group {
                switch page {
                case .overview: OverviewView()
                case .programs: ProgramsView()
                case .analysis: AnalysisView()
                case .network: NetworkView()
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            // Tabwissel: nieuwe pagina vervaagt en schuift een stukje omhoog in beeld
            .id(page)
            .transition(.opacity.combined(with: .offset(y: 10)))
        }
        .animation(.easeOut(duration: 0.22), value: page)
        .onGeometryChange(for: Bool.self) { $0.size.width >= RootView.namesWidth } action: { wide = $0 }
        .frame(minWidth: 260, minHeight: 120)
        // Tabknoppen als echte werkbalk-items: in de titelbalk vangt macOS gewone klikken af (venster slepen).
        // .id(wide): bij wissel smal↔breed de knoppen opnieuw opbouwen, anders houdt de werkbalk de oude breedte (afgekapte namen)
        .toolbar { ToolbarItem(placement: .primaryAction) { header(theme, pages).id(wide) }.sharedBackgroundVisibility(.hidden) }
        .toolbar(removing: .title)
        .windowToolbarFullScreenVisibility(.visible)   // ook schermvullend de tabknoppen tonen
        .toolbarBackgroundVisibility(.hidden, for: .windowToolbar)
        .foregroundStyle(theme.text)
        .tint(theme.accent)
        .environment(\.theme, theme)
        .preferredColorScheme(mode == .system ? nil : target)
        .containerBackground(theme.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), for: .window)
        .background(WindowGrabber { w in
            window = w
            applyOnTop(w)
            // Bij het opstarten altijd zo openen: Live, in de onthouden Live-grootte (linksboven blijft staan)
            if !LaunchOnce.sized {
                LaunchOnce.sized = true
                place(w, size: savedSize(.overview), animate: false, topLeft: savedTopLeft())
                // Opstart-animatie: venster rustig in beeld laten vervagen
                w.alphaValue = 0
                NSAnimationContext.runAnimationGroup { ctx in
                    ctx.duration = 0.18
                    ctx.timingFunction = CAMediaTimingFunction(name: .easeOut)
                    w.animator().alphaValue = 1
                }
            }
            requestFit()
        })
        .onAppear {
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
        .onPreferenceChange(FitHeightKey.self) { h in
            // vensterhoogte = inhoud + werkbalk (echte hoogte van titel-/werkbalk uit het venster)
            let chrome = window.map { $0.frame.height - $0.contentLayoutRect.height } ?? 38
            lastFit = h > 0 ? h + chrome : nil
            if let lastFit, Date() < fitUntil { fit(lastFit) }
        }
        // Na slepen: XS/S klikt op de inhoud vast (M+ meldt geen hoogte, dus blijft zoals gesleept).
        // Plek onthouden (linksboven), zodat hij de volgende keer op dezelfde plek opent
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didMoveNotification)) { n in
            guard let w = window, n.object as? NSWindow === w, !fullScreen(w) else { return }
            UserDefaults.standard.set(NSStringFromPoint(NSPoint(x: w.frame.minX, y: w.frame.maxY)), forKey: "windowTopLeft")
        }
        .onReceive(NotificationCenter.default.publisher(for: NSWindow.didEndLiveResizeNotification)) { n in
            guard let w = window, n.object as? NSWindow === w else { return }
            if let h = lastFit, abs(w.frame.height - h) > 8 { fit(h) }
            // Grootte van deze pagina onthouden (ook voor het openen bij de volgende start)
            let key = "size.\(page.rawValue)"
            DispatchQueue.main.async { UserDefaults.standard.set(NSStringFromSize(w.frame.size), forKey: key) }
        }
        .onChange(of: themeID) { requestFit() }
        .onChange(of: textScale) { requestFit() }
        .onChange(of: metricsRaw) { requestFit() }
        .onChange(of: showAdvice) { requestFit() }
        .onChange(of: onTop) { if let window { applyOnTop(window) } }
        .onChange(of: page) { old, new in
            LaunchOnce.launched = Date()   // inloop-animatie (rijen, lijnen) opnieuw afspelen op de nieuwe pagina
            meter.programsVisible = new == .programs
            resize(from: old, to: new)
        }
    }

    /// Tabknoppen in de werkbalk, rechts van de verkeerslichtjes; verborgen bij maar één tabblad.
    private func header(_ theme: Theme, _ pages: [Page]) -> some View {
        HStack(spacing: 2) {
            if pages.count > 1 {
                ForEach(pages) { p in
                    Button { switchTo(p) } label: {
                        Label(p.title, systemImage: p.icon)
                            .labelStyle(wide ? AnyLabelStyle(.titleAndIcon) : AnyLabelStyle(.iconOnly))
                            .font(.system(size: wide ? 13 : 13, weight: .medium))
                            .foregroundStyle(page == p ? theme.accent : theme.secondary)
                            .frame(minWidth: 28, minHeight: wide ? 26 : 22)
                            .padding(.horizontal, wide ? 8 : 0)
                            .background {
                                if page == p {   // de "druppel": SwiftUI schuift hem zelf naar de actieve knop
                                    RoundedRectangle(cornerRadius: wide ? 8 : 6, style: .continuous)
                                        .fill(theme.accent.opacity(0.15))
                                        .scaleEffect(x: stretch, y: 2 - stretch, anchor: stretchFrom)
                                        .matchedGeometryEffect(id: "drop", in: dropNS)
                                }
                            }
                            .contentShape(.rect)
                    }
                    .buttonStyle(.plain).help(p.title).accessibilityLabel(p.title)
                }
            }
            SettingsLink {
                Label("Settings", systemImage: "gearshape")
                    .labelStyle(wide ? AnyLabelStyle(.titleAndIcon) : AnyLabelStyle(.iconOnly))
                    .font(.system(size: 13, weight: .medium)).foregroundStyle(theme.secondary)
                    .frame(minWidth: 28, minHeight: wide ? 26 : 22)
                    .padding(.horizontal, wide ? 8 : 0)
                    .contentShape(.rect)
            }
            .buttonStyle(.plain).help("Settings").accessibilityLabel("Settings")
        }
        .padding(.horizontal, 4)
        .accessibilityElement(children: .contain)   // elke knop eigen naam (anders noemt de werkbalk ze allemaal "Live")
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

    /// Even de inhoud volgen: layout-passes na een wissel melden soms eerst een tussenhoogte.
    private func requestFit() {
        fitUntil = Date().addingTimeInterval(0.6)
        if let lastFit { fit(lastFit) }
    }

    /// Alleen de hoogte aanpassen (linksboven blijft staan). Nooit tijdens slepen of schermvullend.
    private func fit(_ height: CGFloat) {
        guard let w = window, !w.inLiveResize, !fullScreen(w), abs(w.frame.height - height) > 0.5 else { return }
        var f = w.frame
        f.origin.y += f.height - height
        f.size.height = height
        w.setFrame(f, display: true, animate: false)
    }

    /// Grootte per tabblad bewaren/terugzetten. Linksboven blijft precies staan (niets schuift opzij);
    /// is er rechts/onder te weinig ruimte op het scherm, dan wordt het venster kleiner i.p.v. dat het verschuift.
    private func resize(from old: Page, to new: Page) {
        guard let w = window, !fullScreen(w) else { return }
        let d = UserDefaults.standard
        d.set(NSStringFromSize(w.frame.size), forKey: "size.\(old.rawValue)")
        place(w, size: savedSize(new), animate: true)
        if new == .overview { requestFit() }
    }

    /// Laatst gebruikte plek (linksboven), alleen als die nog op een aangesloten scherm ligt.
    private func savedTopLeft() -> NSPoint? {
        guard let p = UserDefaults.standard.string(forKey: "windowTopLeft").map(NSPointFromString),
              NSScreen.screens.contains(where: { $0.visibleFrame.insetBy(dx: -1, dy: -1).contains(NSPoint(x: p.x + 20, y: p.y - 20)) }) else { return nil }
        return p
    }

    private func savedSize(_ p: Page) -> NSSize {
        UserDefaults.standard.string(forKey: "size.\(p.rawValue)").map(NSSizeFromString) ?? p.defaultSize
    }

    /// Venster naar `size`, linksboven op zijn plek, nooit voorbij de rand van het scherm (dan kleiner i.p.v. verschuiven).
    private func place(_ w: NSWindow, size: NSSize, animate: Bool, topLeft: NSPoint? = nil) {
        var size = size
        let top = topLeft?.y ?? w.frame.maxY, left = topLeft?.x ?? w.frame.minX
        if let vf = w.screen?.visibleFrame {
            size.width = max(260, min(size.width, vf.maxX - left))   // niet voorbij de rechterrand
            size.height = max(160, min(size.height, top - vf.minY))  // niet onder het Dock/de onderrand
        }
        let f = NSRect(x: left, y: top - size.height, width: size.width, height: size.height)
        wide = f.width >= Self.namesWidth   // meteen de juiste knopstand, niet pas na de volgende meting van de breedte
        guard animate else { w.setFrame(f, display: true); return }
        NSAnimationContext.runAnimationGroup { ctx in      // soepel, ~0,2 s
            ctx.duration = 0.2
            ctx.timingFunction = CAMediaTimingFunction(name: .easeInEaseOut)
            w.animator().setFrame(f, display: true)
        }
    }
}

/// Eenmalige acties bij het starten van de app.
enum LaunchOnce {
    @MainActor static var done = false, sized = false
    /// Moment van starten of van een tabwissel; de inloop-animaties spelen alleen vlak daarna.
    nonisolated(unsafe) static var launched = Date()
    static var intro: Bool { Date().timeIntervalSince(launched) < 1.2 }
}

/// Wisselen tussen labelstijlen (alleen icoon / icoon + naam) zonder twee views te schrijven.
struct AnyLabelStyle: LabelStyle {
    private let make: (Configuration) -> AnyView
    init(_ style: some LabelStyle) { make = { AnyView(style.makeBody(configuration: $0)) } }
    func makeBody(configuration: Configuration) -> some View { make(configuration) }
}
