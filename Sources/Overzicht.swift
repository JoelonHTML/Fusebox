import SwiftUI

// MARK: - Live-pagina: hoeveel info past, hangt af van breedte én hoogte (tiers XS…XL)

enum Tier: Int, Comparable {
    case xs, s, m, l, xl
    static func < (a: Tier, b: Tier) -> Bool { a.rawValue < b.rawValue }
}

/// Indeling van de Live-pagina. Wordt bij elke pixel opnieuw berekend, maar de view ververst
/// alleen als tier/kolommen/rijen echt veranderen (Equatable).
struct LiveLayout: Equatable {
    var tier = Tier.s, cols = 1, rows = 1
    var side = false   // L/XL: kaart breed genoeg voor feitenkolom naast de grafiek (anders erboven)

    /// `size`: ruimte onder de tabknoppen; `pad`: theme.rowPad; `advice`: adviesregel aan.
    init(_ size: CGSize, n: Int, pad: CGFloat, advice: Bool) {
        let xl = size.width >= 960 && size.height >= 610
        let grid = size.width - 24   // geen tekst-zijpaneel meer: ook schermvullend alleen grafieken en getallen
        let maxCols = min(max(Int(grid / 250), 1), 4)
        rows = max((n + maxCols - 1) / maxCols, 1)
        cols = max((n + rows - 1) / rows, 1)             // gebalanceerd: geen lege cellen te veel
        let r = CGFloat(rows)
        // Ruimte per rij voor tier t, met de vaste onderdelen (chrome) van die tier eraf.
        func c(_ t: Tier) -> CGFloat {
            let chrome: CGFloat = 10 + (advice && t > .xs ? 24 : 0)   // geen periodeknoppen en geen voettekst meer
            return (size.height - chrome) / r - pad
        }
        // S pas als een S-rij (36 + pad, + kernstaafjes) echt past: het venster klikt niet meer op de inhoud vast,
        // dus de rijen moeten in de ruimte passen die je sleept (geen afgesneden onderste rij).
        tier = xl ? .xl : c(.l) >= 150 ? .l : c(.m) >= 70 ? .m : c(.s) >= 38 ? .s : .xs
        side = tier >= .l && (grid - 10 * CGFloat(cols - 1)) / CGFloat(cols) >= 440
    }

    /// Zelftest (Meterkast --selftest): de banden kloppen met de echte rijhoogtes.
    static func selfTest() {
        for pad: CGFloat in [10, 15, 22] {
            func tier(_ h: CGFloat, w: CGFloat = 300) -> Tier { LiveLayout(CGSize(width: w, height: h), n: 6, pad: pad, advice: false).tier }
            precondition(tier(6 * (36 + pad) + 32) == .s, "S \(pad)")             // passend gemaakte S blijft S
            precondition(tier(6 * (19 + pad / 2) + 10) == .xs, "XS \(pad)")       // passend gemaakte XS blijft XS
            precondition(tier(6 * (100 + pad) + 58) == .m, "M \(pad)")
            precondition(tier(6 * (160 + pad) + 58) == .l, "L \(pad)")
            precondition(tier(6 * (38 + pad) + 10) == .s && tier(6 * (38 + pad) + 9) == .xs, "S past precies \(pad)")
        }
        let xl = LiveLayout(CGSize(width: 1000, height: 700), n: 6, pad: 15, advice: false)
        precondition(xl.tier == .xl && xl.cols == 3 && xl.rows == 2 && !xl.side, "XL")   // zonder zijpaneel: hele breedte voor grafieken
        precondition(LiveLayout(CGSize(width: 1630, height: 730), n: 5, pad: 22, advice: false).side, "XL breed: feiten naast grafiek")
        precondition(FitRows.count([16, 16, 16], in: 40, spacing: 3) == 2 && FitRows.count([16], in: 15, spacing: 3) == 0
                     && FitRows.count([16, 16], in: 35, spacing: 3) == 2, "regels die passen")
        let five = LiveLayout(CGSize(width: 774, height: 400), n: 5, pad: 15, advice: false)
        precondition(five.cols == 3 && five.rows == 2, "kolommen")
        print("layout ok")
    }
}

/// Grootte zonder de view te verversen (onGeometryChange vuurt bij elke pixel).
final class SizeBox { var v = CGSize.zero }

struct OverviewView: View {
    @EnvironmentObject var meter: Meter
    @Environment(\.theme) private var theme
    @AppStorage("metrics") private var metricsRaw = Metric.defaultOrder
    @AppStorage("graphs") private var graphs: GraphMode = .auto
    @AppStorage("showAdvice") private var showAdvice = false
    private let range = TimeRange.fiveMin   // Live toont altijd de korte periode (Settings › Live page), over de volle breedte
    @AppStorage("shortMinutes") private var shortMinutes = 5   // alleen zodat de grafiek meteen bijwerkt bij wijzigen
    @AppStorage("absolute") private var absoluteRaw = "ram,ssd"   // metingen in echte waarden i.p.v. %
    @AppStorage("page") private var page: Page = .overview
    @State private var box = SizeBox()
    @State private var layout: LiveLayout?
    @State private var detail: Metric?          // aangeklikte kaart → detailweergave in hetzelfde venster
    @State private var focus: Peak?             // geopend via een piek: die piek in het detail aanwijzen
    @State private var thermal = ProcessInfo.processInfo.thermalState   // macOS remt af bij hitte: icoontje bij CPU

    private var metrics: [Metric] { Metric.enabled(metricsRaw) }

    var body: some View {
        Group {
            if let d = detail {
                MetricDetail(metric: d, focus: focus) { withAnimation(.snappy(duration: 0.3)) { detail = nil; focus = nil } }
                    .transition(.opacity.combined(with: .scale(scale: 0.97)))
            } else if let layout { content(layout).transition(.opacity) } else { Color.clear }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .onGeometryChange(for: CGSize.self) { $0.size } action: { box.v = $0; relayout() }
        .onChange(of: metricsRaw) { relayout() }
        .onChange(of: showAdvice) { relayout() }
        .onChange(of: theme.rowPad) { relayout() }
        // Geen polling: macOS meldt zelf wanneer de warmtetoestand verandert (op een willekeurige thread)
        .onReceive(NotificationCenter.default.publisher(for: ProcessInfo.thermalStateDidChangeNotification).receive(on: DispatchQueue.main)) { _ in
            thermal = ProcessInfo.processInfo.thermalState
        }
    }

    private func relayout() {
        let l = LiveLayout(box.v, n: metrics.count, pad: theme.rowPad, advice: showAdvice)
        if l != layout { layout = l }
    }

    private func content(_ l: LiveLayout) -> some View {
        let t = l.tier, metrics = metrics
        let absolute = Set(absoluteRaw.split(separator: ",").map(String.init))
        let h = t >= .m ? meter.samples(range) : [], fill = meter.fill(range)
        let tight = theme.rowStyle == .lines || theme.rowStyle == .plain
        let gap: CGFloat = tight ? 0 : t == .xs ? 3 : 6
        let apps = t >= .l ? meter.minuteApps.apps : []   // top-apps in grote kaarten (per minuut gemeten)
        let today = t >= .l && metrics.contains(.ssd) ? meter.samples(.today) : []   // SSD: geschreven vandaag
        let hot = switch thermal { case .nominal: 0; case .fair: 1; case .serious: 2; default: 3 }
        return HStack(alignment: .top, spacing: 12) {
            VStack(spacing: 0) {
                if showAdvice && t > .xs { advice.frame(minHeight: 20).padding(.bottom, 4) }
                VStack(spacing: gap) {
                    ForEach(0..<l.rows, id: \.self) { r in
                        HStack(alignment: .top, spacing: 10) {
                            ForEach(0..<l.cols, id: \.self) { c in
                                let i = r * l.cols + c
                                if i < metrics.count {
                                    let m = metrics[i]
                                    let row = { var r = m.row(meter.latest, h, abs: absolute.contains(m.rawValue), apps: apps, today: today); if m == .cpu { r.hot = hot }; return r }()
                                    MetricView(data: lastPeak(row, m, t), tier: t, fill: fill, index: i,
                                               side: l.side, graphs: graphs != .never, ready: !meter.recent.isEmpty,
                                               // Pieken op de kaart alleen in grote vensters (L/XL); klein en middel blijven rustig,
                                               // de detailweergave (klik) toont ze altijd
                                               peaks: t >= .l ? PeakContext(metric: m, watch: meter.peaks, times: h.map(\.time)) { p in
                                                   withAnimation(.snappy(duration: 0.3)) { focus = p; detail = m }
                                               } : nil,
                                               openApp: { ref in meter.openApp = ref; page = .programs })   // top-app: naar zijn app-pagina
                                        .id(m)   // kaart hoort bij de meting, niet bij de plek: anders toont NET even de I/O-waarde na een andere indeling
                                        .modifier(IntroIn(index: i))
                                        .contentShape(.rect)
                                        .onTapGesture { if !PeakHoverView.claimed() { withAnimation(.snappy(duration: 0.3)) { detail = m } } }   // klik op een piek: popover
                                        .help("Click for details")
                                } else if t < .m {   // klein: lege cel houdt de getallen in kolommen
                                    Color.clear.frame(height: 1)
                                }   // M+: geen lege cel, de laatste kaart(en) worden breder
                            }
                        }
                        .frame(maxHeight: .infinity)   // rijen verdelen de hoogte: nooit lege ruimte onderin
                    }
                }
                .frame(minHeight: t >= .m ? 0 : nil, alignment: .top)
            }
        }
        .padding(.horizontal, 12).padding(.bottom, 10)
        .frame(minHeight: CGFloat(l.rows) * (17 + theme.rowPad / 2) + 10, alignment: .top) // ≈ XS-hoogte
    }

    /// L/XL: "Last peak  14:32 · Xcode" als tweede feit; op BAT als laatste het leegste Bluetooth-apparaat.
    private func lastPeak(_ r: MetricRowData, _ m: Metric, _ t: Tier) -> MetricRowData {
        guard t >= .l else { return r }
        var r = r
        if let p = meter.peaks.peaks(m, since: Calendar.current.startOfDay(for: Date())).last {
            r.facts.insert(Fact(label: "Last peak", value: Peaks.clock.string(from: p.at) + (p.app.map { " · \($0)" } ?? "")), at: min(1, r.facts.count))
        }
        if m == .battery, let f = WirelessWatcher.lowestFact() { r.facts.append(f) }
        return r
    }

    private func toggleAbsolute(_ m: Metric) {
        var set = Set(absoluteRaw.split(separator: ",").map(String.init))
        if set.contains(m.rawValue) { set.remove(m.rawValue) } else { set.insert(m.rawValue) }
        absoluteRaw = set.sorted().joined(separator: ",")
    }

    private var advice: some View {
        let st = meter.status
        return HStack(spacing: 6) {
            Circle().fill(st.color).frame(width: 7, height: 7)
            Text(st.text).font(theme.label(12)).foregroundStyle(theme.secondary).lineLimit(1)
            Spacer(minLength: 0)
        }
    }
}

struct MetricView: View {
    @Environment(\.theme) private var theme
    let data: MetricRowData
    let tier: Tier
    let fill: Double, index: Int
    var stats = ""              // L+: min/gem/max over de periode
    var axis: String?           // L+: aslabel links onder ("−5 min")
    var side = false            // L+: feiten naast de grafiek i.p.v. erboven (brede kaart)
    var graphs = true
    var ready = true            // false tot de eerste echte meting binnen is
    var peaks: PeakContext?     // pieken: stipje (XS/S), hover op de grafiek (M+)
    var openApp: ((AppRef) -> Void)?   // klik op een top-app in de feiten
    @AppStorage("perCore") private var perCore = false   // per kern ook in kleine vensters

    var body: some View {
        let xs = tier == .xs
        let stagger = Double(index % 6) * 0.3   // rij 0 meteen, rij 1 na 0,3 s, … : niet alles tegelijk
        let small = data.valueSize <= 16 // NET/IO
        // Verticale padding binnen de rij (rowPad min tussenruimte); XS halveert die.
        let inner = theme.rowPad - (theme.rowStyle == .lines || theme.rowStyle == .plain ? 0 : 6)
        VStack(alignment: .leading, spacing: 2) {
            HStack(alignment: .firstTextBaseline, spacing: 6) {
                Text(data.label).font(theme.label(xs ? 11 : 13)).foregroundStyle(labelColor).fixedSize()   // label valt nooit weg
                if tier < .m, let peaks { PeakDot(ctx: peaks) }
                if data.hot > 0 {   // macOS remt de processor af om af te koelen (1 = alleen warm)
                    Image(systemName: "thermometer.high").font(.system(size: xs ? 11 : 13, weight: .medium))
                        .foregroundStyle(data.hot >= 3 ? theme.danger : data.hot == 2 ? theme.warn : theme.onRow(true))
                        .help(data.hot >= 2 ? "Your Mac is slowing itself down to cool off" : "Your Mac is warm")
                }
                if let icon = data.sideIcon {   // batterij: laden (bliksem) of hoe vol, als icoontje
                    Image(systemName: icon).font(.system(size: xs ? 11 : 13, weight: .medium)).foregroundStyle(data.sideLevel > 0 ? color(data.sideLevel) : theme.onRow(true))
                }
                if !data.side.isEmpty {   // temperatuur / ms / tijd: klein naast het label, zodat het getal echt rechts staat
                    ChangingText(text: data.side, delay: stagger, ready: ready).font(theme.number(xs ? 11 : 13)).foregroundStyle(data.sideLevel > 0 ? color(data.sideLevel) : theme.onRow(true))
                        .lineLimit(1).fixedSize()
                }
                // Per kern: smalle staafjes direct naast de temperatuur (niet eronder: dat zit de grafiek in de weg).
                // Groot venster (L/XL) altijd, kleiner alleen met de instelling.
                if !data.cores.isEmpty && (tier >= .l || perCore) {
                    CoreBars(values: data.cores, split: Sampler.efficiencyCores, barWidth: tier >= .l ? 6 : 4)
                        .frame(height: xs ? 10 : tier >= .l ? 18 : 14).fixedSize(horizontal: true, vertical: false)
                        .padding(.leading, 4).alignmentGuide(.firstTextBaseline) { $0[.bottom] - 1 }
                        .help("Per core: \(Sampler.efficiencyCores) efficiency + \(data.cores.count - Sampler.efficiencyCores) performance")
                }
                if data.level > 0 && theme.rowStyle == .blocks { Image(systemName: "exclamationmark.triangle.fill").font(.caption).foregroundStyle(.white) }
                Spacer(minLength: 4)
                let valueFont = theme.number(xs ? (small ? 12 : 15) : data.valueSize)
                // Vaste breedte (onzichtbare "100%") → niets verspringt of schaalt bij 7% ↔ 17%
                // Past het niet (heel smal venster), dan krimpt het getal i.p.v. dat alle vakjes over de rand gaan
                ZStack(alignment: .trailing) {
                    Text(data.template).font(valueFont).hidden().lineLimit(1).minimumScaleFactor(0.5)
                    ChangingText(text: data.value, delay: stagger, ready: ready).font(valueFont).foregroundStyle(color(data.level))
                        .lineLimit(1).minimumScaleFactor(0.5)
                }
                .layoutPriority(1)
                // Eenheid in een vaste kolom (ook als hij leeg is), zodat de getallen rechts onder elkaar uitlijnen
                if !data.unit.isEmpty {
                    Text(data.unit).font(theme.number(xs ? 11 : 14)).foregroundStyle(theme.onRow(true)).lineLimit(1).fixedSize()
                }
            }
            .frame(minHeight: xs ? nil : 36) // gelijke rijhoogte, ongeacht valueSize
            if tier >= .m {
                // Geen uitlegregels op de Live-pagina: alleen labels en getallen.
                if !stats.isEmpty {
                    Text(stats).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1).minimumScaleFactor(0.8)
                }
                // L/XL: wat er meer te weten valt (feiten, top-apps, min/gem/max); zoveel regels als er passen
                if tier >= .l && !data.facts.isEmpty {
                    // Feiten boven de grafiek, naast elkaar in kolommen (brede kaart: 2–3); de grafiek houdt de volle
                    // breedte en vult de rest tot onderaan. L: 4 regels, XL: 9 (belangrijkste eerst).
                    VStack(alignment: .leading, spacing: 10) {
                        facts
                        graph
                    }
                    .frame(maxHeight: .infinity)
                } else {
                    graph
                }
            }
        }
        .padding(.vertical, xs ? -inner / 4 : 0)
        // Klein (XS/S): rij rekt mee met het venster, inhoud in het midden; groter: van boven af
        .frame(maxHeight: .infinity, alignment: tier >= .m ? .top : .center)
        .themedRow(theme, index: index)
    }

    @ViewBuilder private var graph: some View {
        if graphs && !data.values.isEmpty {
            SparkView(values: data.values, second: data.second, top: data.top, bottom: data.bottom, fill: fill, axis: nil, order: index, glides: tier < .l)
                .padding(.top, peaks != nil ? PeakHover.band : 0)   // vaste strook boven de grafiek voor het label
                .overlay { if let peaks { PeakHover(ctx: peaks, values: data.values, second: data.second, full: tier >= .l, temp: data.graphIsTemp, color: theme.rowStyle == .blocks ? .white : theme.accent) } }
                .frame(minHeight: 16, maxHeight: .infinity)
        }
    }

    /// Rustige lijst: label links, waarde rechts, in kolommen naast elkaar als de kaart breed is.
    private var facts: some View {
        let size: CGFloat = tier >= .xl ? 13.5 : 12.5
        let shown = Array(data.facts.prefix(tier >= .xl ? 9 : 4))
        return LazyVGrid(columns: [GridItem(.adaptive(minimum: 230), spacing: 28, alignment: .top)], alignment: .leading, spacing: tier >= .xl ? 8 : 6) {
            ForEach(shown.indices, id: \.self) { i in
                let f = shown[i]
                let line = HStack(spacing: 6) {
                    if let icon = f.icon { Image(nsImage: icon).resizable().frame(width: size + 3, height: size + 3) }
                    Text(f.label).font(theme.label(size)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                    Spacer(minLength: 8)
                    Text(f.value).font(theme.number(size)).foregroundStyle(theme.onRow()).lineLimit(1).fixedSize()
                }
                if let app = f.app, let openApp {   // top-app: klik = zijn app-pagina (de knop wint van de tik op de kaart)
                    Button { openApp(app) } label: { line.contentShape(.rect) }.buttonStyle(.plain).pointerStyle(.link).help("Open \(app.name)")
                } else { line }
            }
        }
    }

    private var labelColor: Color { theme.id == "instrument" ? theme.accent : theme.onRow(true) }
    private func color(_ level: Int) -> Color {
        if theme.rowStyle == .blocks { return theme.onRow() }
        return level == 2 ? theme.danger : level == 1 ? theme.warn : theme.onRow()
    }
}

struct SparkView: View {
    @Environment(\.theme) private var theme
    let values: [Double]
    var second: [Double] = []
    let top: Double?
    var bottom = 0.0
    let fill: Double
    var axis: String?           // nil = geen aslabels
    var order = 0               // volgorde bij het inschuiven: grafiekjes komen één voor één, net als de rijen
    /// Glijden alleen in kleine grafiekjes. Een grote grafiek (detail, groot venster) die 15× per seconde verschuift,
    /// laat macOS het hele (glazen) venster steeds opnieuw opbouwen: veel GPU voor niets. Die springt elke meting.
    var glides = true
    @AppStorage("smoothBig") private var smoothBig = false   // instelling: grote grafieken toch laten glijden
    @State private var drawn: CGFloat = 1        // 0…1: hoeveel van de lijn getekend is (opstart-animatie)
    @State private var waiting = false           // opgestart zonder lijn: inschuiven zodra de 2e meting er is

    var body: some View {
        // Uitdunnen en glijden gebeuren in SparkLayers, op basis van de getekende breedte.
        let scale = top ?? max(values.max() ?? 1, second.max() ?? 1, 1)
        SparkLayers(values: values, second: second, top: scale, bottom: bottom, fill: fill,
                    color: theme.rowStyle == .blocks ? Color.white : theme.accent, glides: glides || smoothBig)
        // Grafiek verschijnt (opstarten, tabwissel): vanaf rechts in beeld schuiven, één voor één
        .clipShape(Reveal(drawn: drawn))
        // Alleen bij opstarten/tabwissel; bij venster slepen (andere indeling → nieuwe views) gewoon meteen tonen.
        // Bij het opstarten is er nog geen lijn (minstens 2 metingen nodig): dan pas inschuiven als hij er is.
        .onAppear {
            guard LaunchOnce.intro else { drawn = 1; return }
            drawn = 0
            if values.count > 1 { reveal() } else { waiting = true }
        }
        .onChange(of: values.count > 1) { _, has in if has && waiting { waiting = false; reveal() } }
        .overlay(alignment: .topLeading) {
            if axis != nil { Text(top == 100 ? "100%" : top == nil ? short(scale) : "\(Int(scale.rounded())) GB").padding(2) }
        }
        .overlay(alignment: .bottom) {
            if let axis { HStack { Text(axis); Spacer(); Text("now") }.padding(2) }
        }
        .font(theme.label(9)).foregroundStyle(theme.onRow(true))
    }
}

/// De lijnen zelf, als Core Animation-lagen buiten SwiftUI om. (Als SwiftUI-animatie rekende de app elke frame het hele
/// venster door: ~40% processor.) Glijden na een nieuwe meting gaat in kleine stappen, zie `SparkNSView.glide`.
private struct SparkLayers: NSViewRepresentable {
    let values: [Double], second: [Double], top: Double, bottom: Double, fill: Double, color: Color, glides: Bool
    func makeNSView(context: Context) -> SparkNSView { SparkNSView() }
    func updateNSView(_ v: SparkNSView, context: Context) {
        v.set(values, second, top: top, bottom: bottom, fill: fill, color: color.resolve(in: context.environment).cgColor,
              // "Verminder beweging" of venster niet te zien: lijn verspringt gewoon
              glide: glides && !context.environment.accessibilityReduceMotion && context.environment.onScreen)
    }
}

final class SparkNSView: NSView {
    private let area = CAShapeLayer(), line = CAShapeLayer(), line2 = CAShapeLayer()
    private var a: [Double] = [], b: [Double] = []
    private var leadA: [Double] = [], leadB: [Double] = []   // punten die net uit het venster vielen (schuiven links uit beeld)
    private var top = 1.0, bottom = 0.0, fill = 1.0, stretch = false
    private var drawn = CGSize.zero
    private var from: CGFloat = 0, fromTop = 1.0, at: CFTimeInterval = 0    // lopende glide: beginstand (verschuiving, schaal) en starttijd
    private var shownPx: CGFloat = 0, pxPerShift: CGFloat = 0               // laatst getekende stand, in schermpixels
    /// Iets korter dan de 2 s tussen metingen: de lijn staat heel even stil en loopt nooit achter.
    static let duration: CFTimeInterval = 1.9

    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }   // klikken gaan naar de kaart eronder

    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        layer?.masksToBounds = true
        area.strokeColor = nil
        line.fillColor = nil; line.lineWidth = 1.5; line.lineCap = .round; line.lineJoin = .round
        line2.fillColor = nil; line2.lineWidth = 1
        for l in [area, line, line2] { layer?.addSublayer(l) }
    }
    required init?(coder: NSCoder) { fatalError("init(coder:)") }

    override func viewDidChangeBackingProperties() {
        super.viewDidChangeBackingProperties()
        for l in [area, line, line2] { l.contentsScale = window?.backingScaleFactor ?? 2 }
    }

    override func layout() {
        super.layout()
        if bounds.size != drawn { draw(from: nil) }   // andere maat: meteen goed tekenen
    }

    func set(_ na: [Double], _ nb: [Double], top nt: Double, bottom nbot: Double, fill nf: Double, color: CGColor, glide: Bool) {
        CATransaction.begin(); CATransaction.setDisableActions(true)
        area.fillColor = color.copy(alpha: color.alpha * 0.15); line.strokeColor = color; line2.strokeColor = color.copy(alpha: color.alpha * 0.5)
        CATransaction.commit()
        guard na != a || nb != b || nt != top || nbot != bottom || nf != fill else { return }
        let kind = Spark.glide(a, na), same = (b.isEmpty && nb.isEmpty) || Spark.glide(b, nb) == kind
        // Kwam de meting iets te vroeg, dan is de vorige glide nog niet klaar: verder vanaf waar de lijn nu staat
        let rest = from * CGFloat(max(0, 1 - (CACurrentMediaTime() - at) / Self.duration)), oldTop = top
        if kind == .slide && same {
            leadA = Array((leadA + a.prefix(1)).suffix(2)); leadB = Array((leadB + b.prefix(1)).suffix(2))
        } else { leadA = []; leadB = [] }
        stretch = kind == .grow && nf >= 1   // smaller dan de volle breedte: de lijn groeit naar links aan en schuift gewoon
        (a, b, top, bottom, fill) = (na, nb, nt, nbot, nf)
        draw(from: glide && same && kind != .none ? (1 + rest, oldTop) : nil)
    }

    /// Tekent de eindstand. Met `from` (beginstand en de schaal van toen) glijdt de lijn daar rustig naartoe:
    /// begin = precies waar hij stond, nieuw punt nog rechts buiten beeld, het oudste schuift links uit beeld.
    private func draw(from start: (shift: CGFloat, top: Double)?) {
        drawn = bounds.size
        let w = bounds.width * fill
        guard let start, !Spark.thinned(a.count, width: w), w > 0 else { from = 0; render(0, top); return }
        // Beginstand = wat er al staat (de vorige eindstand): niets te tekenen tot de eerste stap
        (from, fromTop, at) = (start.shift, start.top, CACurrentMediaTime())
        pxPerShift = w / CGFloat(max(a.count - 1, 1)) * (window?.backingScaleFactor ?? 2)
        shownPx = from * pxPerShift
        Self.glide(self)
    }

    /// Zet de lijnen op stand `shift` (1 = vóór de nieuwe meting, 0 = eindstand) met schaal `top`, zonder animatie.
    private func render(_ shift: CGFloat, _ top: Double) {
        let w = bounds.width * fill, r = CGRect(x: bounds.maxX - w, y: 0, width: w, height: bounds.height)
        let thin = Spark.thinned(a.count, width: w), n = max(Int(w), 2)
        let pa = Spark.points(thin ? downsample(a, to: n) : a, lead: thin ? [] : leadA, in: r, top: top, bottom: bottom, shift: shift, stretch: stretch)
        let pb = Spark.points(thin ? downsample(b, to: n) : b, lead: thin ? [] : leadB, in: r, top: top, bottom: bottom, shift: shift, stretch: stretch)
        CATransaction.begin(); CATransaction.setDisableActions(true)
        for l in [area, line, line2] where l.frame != bounds { l.frame = bounds }
        area.path = Spark.path(pa, closed: true, floor: r.maxY).cgPath
        line.path = Spark.path(pa, closed: false, floor: r.maxY).cgPath
        line2.path = pb.count > 1 ? Spark.path(pb, closed: false, floor: r.maxY).cgPath : nil
        CATransaction.commit()
        shownPx = shift * pxPerShift
    }

    /// Tijd tussen twee stappen van deze glide: één stap per halve pixel, hoogstens 15 per seconde. Beweegt de schaal
    /// mee, dan altijd 15 per seconde (de hele lijn verandert dan van vorm).
    private var stepInterval: TimeInterval {
        let steps = fromTop != top ? .infinity : Double(from * pxPerShift) / 0.5
        return min(max(Self.duration / max(steps, 1), 1.0 / 15), 0.5)
    }

    /// Eén stap van de glide; false = klaar. Tekent alleen als de lijn minstens een halve pixel opschoof (of de schaal
    /// meebeweegt): in een klein venster zijn dat een paar beeldjes per seconde, niet 120.
    private func step() -> Bool {
        guard from > 0 else { return false }
        let p = (CACurrentMediaTime() - at) / Self.duration
        if p >= 1 { from = 0; render(0, top); return false }
        let s = from * CGFloat(1 - p)
        if fromTop != top || abs(shownPx - s * pxPerShift) >= 0.5 { render(s, fromTop + (top - fromTop) * p) }
        return true
    }

    // Glijden in stappen van de app zelf, niet als CAAnimation: die laat de render-server het hele grafiekvlak op de
    // beeldfrequentie (75–120×/s) opnieuw tekenen terwijl de lijn maar een paar pixels per seconde opschuift. Dat kostte
    // tot een kwart van de GPU bij een groot venster. Eén gedeelde klok voor alle grafieken = één tekenbeurt per stap.
    private static let gliding = NSHashTable<SparkNSView>.weakObjects()
    private static var clock: Timer?
    private static func glide(_ v: SparkNSView) {
        gliding.add(v)
        // Niet vaker wakker worden dan er te tekenen valt: in een klein venster schuift de lijn maar een paar pixels per
        // meting, dan volstaan een paar stappen per seconde (was altijd 15 wake-ups per seconde).
        let every = v.stepInterval
        if let c = clock, c.timeInterval <= every + 0.001 { return }
        clock?.invalidate()
        let t = Timer(timeInterval: every, repeats: true) { _ in
            MainActor.assumeIsolated {
                for v in gliding.allObjects where !v.step() { gliding.remove(v) }
                if gliding.allObjects.isEmpty { clock?.invalidate(); clock = nil }
            }
        }
        t.tolerance = 0.02
        RunLoop.main.add(t, forMode: .common)
        clock = t
    }
}

/// Eén dun staafje per kern; zuinige kernen (vooraan) en snelle kernen met een kleine tussenruimte.
struct CoreBars: View {
    @Environment(\.theme) private var theme
    let values: [Double]
    var split = 0
    var wide = false            // detailweergave: staafjes vullen de hele breedte
    var barWidth: CGFloat = 4   // anders: vaste smalle staafjes (kaartkop)
    @Environment(\.onScreen) private var onScreen
    @State private var shown: [Double] = []   // wat er nu getekend staat (onderweg naar `values`)

    var body: some View {
        let color = theme.rowStyle == .blocks ? Color.white : theme.accent
        let v = shown.count == values.count ? shown : values
        HStack(alignment: .bottom, spacing: wide ? 3 : 2) {
            ForEach(values.indices, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1).fill(color.opacity(0.15))
                    .overlay {
                        RoundedRectangle(cornerRadius: 1).fill(color)
                            .scaleEffect(x: 1, y: min(max(v[i], 0), 100) / 100, anchor: .bottom)
                    }
                    .frame(width: wide ? nil : barWidth)
                    .frame(maxWidth: wide ? .infinity : nil)
                    .padding(.leading, i == split && split > 0 ? (wide ? 8 : 4) : 0)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        // Meebewegen in zes stappen van de app zelf, op hetzelfde raster als de getallen (zie `stepTick`). Als
        // SwiftUI-animatie hield dit de app 0,8 s per meting op de beeldfrequentie wakker.
        .task(id: values) {
            guard onScreen, shown.count == values.count else { shown = values; return }
            let start = shown
            for i in 1...6 {
                await stepTick()
                if Task.isCancelled { return }
                let p = Double(i) / 6, e = p * p * (3 - 2 * p)
                shown = zip(start, values).map { $0 + ($1 - $0) * e }
            }
        }
    }
}

/// Wacht tot de volgende tik van een vast raster van 1/16 s. Alles wat in stappen beweegt (getallen, staafjes) deelt zo
/// zijn tekenbeurten: zes per meting voor het hele venster, in plaats van elk onderdeel zijn eigen reeks.
func stepTick() async {
    let now = CACurrentMediaTime(), next = (now / 0.0625).rounded(.down) * 0.0625 + 0.0625
    try? await Task.sleep(for: .seconds(next - now))
}

extension SparkView {
    /// Zachte curve: snel in, rustig uitlopen; grafiekjes één voor één
    fileprivate func reveal() {
        let p = LaunchOnce.pace
        withAnimation(.timingCurve(0.22, 1, 0.36, 1, duration: 0.8 * p).delay((0.05 + Double(order) * 0.25) * p)) { drawn = 1 }
    }
}

/// Regels onder elkaar; wat niet meer in de hoogte past valt weg (hele regels, van onderen). Zo groeit de
/// hoeveelheid info vanzelf mee met het venster, zonder afgesneden tekst.
struct FitRows: Layout {
    var spacing: CGFloat = 4

    /// Hoeveel regels (van boven af) passen in `h`.
    static func count(_ heights: [CGFloat], in h: CGFloat, spacing: CGFloat) -> Int {
        var y: CGFloat = 0, n = 0
        for r in heights {
            if y + r > h + 0.5 { break }
            y += r + spacing; n += 1
        }
        return n
    }

    private func heights(_ subviews: Subviews, _ width: CGFloat?) -> [CGFloat] {
        subviews.map { $0.sizeThatFits(ProposedViewSize(width: width, height: nil)).height }
    }

    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        let hs = heights(subviews, proposal.width)
        let n = Self.count(hs, in: proposal.height ?? .infinity, spacing: spacing)
        let used = hs.prefix(n).reduce(0, +) + spacing * CGFloat(max(n - 1, 0))
        return CGSize(width: proposal.width ?? 200, height: used)
    }

    func placeSubviews(in bounds: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        let hs = heights(subviews, bounds.width)
        let n = Self.count(hs, in: bounds.height, spacing: spacing)
        var y = bounds.minY
        for (i, v) in subviews.enumerated() {
            // Past niet: ver buiten beeld zetten (de lijst knipt af), dan blijft de rest van de layout stabiel
            let at = i < n ? CGPoint(x: bounds.minX, y: y) : CGPoint(x: bounds.minX, y: bounds.maxY + 10_000)
            v.place(at: at, proposal: ProposedViewSize(width: bounds.width, height: hs[i]))
            if i < n { y += hs[i] + spacing }
        }
    }
}

/// Inschuiven vanaf rechts: alleen het rechterdeel (`drawn` 0…1) is zichtbaar.
/// Knippen i.p.v. maskeren: geen extra tekenlaag per grafiek bij elke frame van het slepen.
private struct Reveal: Shape {
    var drawn: CGFloat
    var animatableData: CGFloat { get { drawn } set { drawn = newValue } }
    func path(in r: CGRect) -> Path { Path(CGRect(x: r.maxX - r.width * drawn, y: r.minY, width: r.width * drawn, height: r.height)) }
}

/// Hoe een getal verandert: rollen (van boven naar beneden), kort vervagen, of direct.
enum NumberStyle: String, CaseIterable, Identifiable {
    case roll, fade, instant
    var id: String { rawValue }
    var title: String {
        switch self {
        case .roll: "Roll"
        case .fade: "Fade"
        case .instant: "Instant"
        }
    }
}

/// Tekst die bij verandering rolt, overvloeit van oud naar nieuw getal, of direct wisselt.
struct ChangingText: View {
    let text: String
    /// Wachttijd voor deze tekst de nieuwe waarde toont: zo verspringen niet alle getallen tegelijk.
    var delay: Double = 0
    /// false = nog geen echte meting (net opgestart): leeg laten i.p.v. "0%".
    var ready = true
    @AppStorage("numberStyle") private var style: NumberStyle = .fade
    @Environment(\.onScreen) private var onScreen
    @State private var shown: String?
    @State private var flip = false   // eerste echte waarde komt eenmalig binnen met een flip, daarna het gekozen effect
    @State private var old: String?   // overvloeien: het vorige getal, zolang het nog te zien is
    @State private var fade = 1.0     // overvloeien: 0 = nog het oude getal, 1 = het nieuwe

    var body: some View {
        let t = shown ?? ""
        Group {
            if flip || style == .roll {
                // countsDown: nieuwe cijfers schuiven van boven naar beneden in beeld
                Text(t).contentTransition(.numericText(countsDown: true)).animation(onScreen ? .snappy(duration: 0.35) : nil, value: t)
            } else if style == .fade {
                // Zacht overvloeien van getal naar getal, op exact dezelfde plek (rechts uitgelijnd), geen dip naar zwart.
                // In 6 stappen van de app zelf, niet als SwiftUI-animatie: die hield de app een halve seconde per getal op de
                // beeldfrequentie wakker (60–120×/s) en liet macOS het venster even vaak opnieuw samenstellen; met zes
                // getallen na elkaar stond het nooit stil (gemeten: 55–68 wake-ups per seconde, nu rond de 20).
                ZStack(alignment: .trailing) {
                    // Het oude getal blijft (onzichtbaar) staan: zo verandert tijdens het overvloeien alleen de dekking en
                    // hoeft SwiftUI de indeling van het venster niet opnieuw uit te rekenen.
                    Text(old ?? "").opacity(1 - fade).accessibilityHidden(true)
                    Text(t).opacity(fade)
                }
                .task(id: t) {
                    guard fade < 1 else { return }
                    for i in 1...6 {
                        await stepTick()
                        if Task.isCancelled { return }   // alweer een nieuw getal: dat neemt het over
                        let p = Double(i) / 6
                        fade = p * p * (3 - 2 * p)   // rustig in en uit, zoals easeInOut
                    }
                }
            } else {
                Text(t)
            }
        }
        .onAppear { if ready { shown = text } }
        .onChange(of: ready ? text : nil) { _, new in
            guard let new else { return }
            guard onScreen else { old = nil; fade = 1; shown = new; return }   // venster niet te zien of low impact: meteen, zonder animatie
            let first = shown == nil
            if first { flip = true }
            // Overvloeien: alle getallen tegelijk in plaats van 0,3 s na elkaar. Dan delen ze hun zes tekenbeurten en staat
            // het venster de rest van de twee seconden stil; elke losse tekenbeurt kost de app ruim een milliseconde.
            let wait = first ? 0.1 + delay / 3 : style == .fade ? 0 : delay
            DispatchQueue.main.asyncAfter(deadline: .now() + wait) {
                // Overvloeien vanaf het getal dat er nu staat (alleen bij "Fade", en niet bij de eerste waarde: die flipt)
                old = style == .fade && !first ? shown : nil
                fade = old == nil ? 1 : 0
                shown = new
                if first { DispatchQueue.main.asyncAfter(deadline: .now() + 0.6) { flip = false } }
            }
        }
    }
}

/// Opstart-animatie: rijen glijden één voor één omhoog in beeld (alleen in de eerste seconden na het starten).
struct IntroIn: ViewModifier {
    let index: Int
    @State private var shown = !LaunchOnce.intro
    func body(content: Content) -> some View {
        content
            .opacity(shown ? 1 : 0)
            .offset(y: shown ? 0 : 14)
            .onAppear {
                guard !shown else { return }
                withAnimation(.spring(duration: 0.35 * LaunchOnce.pace, bounce: 0.2).delay((0.03 + Double(index) * 0.04) * LaunchOnce.pace)) { shown = true }
            }
    }
}
