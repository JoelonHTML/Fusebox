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
        tier = xl ? .xl : c(.l) >= 150 ? .l : c(.m) >= 70 ? .m : c(.s) >= 28 ? .s : .xs
    }

    /// Zelftest (Meterkast --selftest): de banden kloppen met de echte rijhoogtes.
    static func selfTest() {
        for pad: CGFloat in [10, 15, 22] {
            func tier(_ h: CGFloat, w: CGFloat = 300) -> Tier { LiveLayout(CGSize(width: w, height: h), n: 6, pad: pad, advice: false).tier }
            precondition(tier(6 * (36 + pad) + 32) == .s, "S \(pad)")             // passend gemaakte S blijft S
            precondition(tier(6 * (19 + pad / 2) + 10) == .xs, "XS \(pad)")       // passend gemaakte XS blijft XS
            precondition(tier(6 * (100 + pad) + 58) == .m, "M \(pad)")
            precondition(tier(6 * (160 + pad) + 58) == .l, "L \(pad)")
        }
        let xl = LiveLayout(CGSize(width: 1000, height: 700), n: 6, pad: 15, advice: false)
        precondition(xl.tier == .xl && xl.cols == 3 && xl.rows == 2, "XL")   // zonder zijpaneel: hele breedte voor grafieken
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
    @State private var contentHeight: CGFloat = 0

    private var metrics: [Metric] { Metric.enabled(metricsRaw) }

    var body: some View {
        Group {
            if let layout { content(layout) } else { Color.clear }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .onGeometryChange(for: CGSize.self) { $0.size } action: { box.v = $0; relayout() }
        .onChange(of: metricsRaw) { relayout() }
        .onChange(of: showAdvice) { relayout() }
        .onChange(of: theme.rowPad) { relayout() }
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
                                    MetricView(data: m.row(meter.latest, h, abs: absolute.contains(m.rawValue)), tier: t, fill: fill, index: i,
                                               stats: "", axis: nil, graphs: graphs != .never, ready: !meter.recent.isEmpty)
                                        .modifier(IntroIn(index: i))
                                        .contentShape(.rect)
                                        .onTapGesture { toggleAbsolute(m) }
                                        .help("Click to switch between % and real values")
                                } else {
                                    Color.clear.frame(height: 1)
                                }
                            }
                        }
                        .frame(maxHeight: t >= .m ? .infinity : nil)
                    }
                }
                .frame(minHeight: t >= .m ? 0 : nil, alignment: .top)
            }
        }
        .padding(.horizontal, 12).padding(.bottom, 10)
        // Natuurlijke hoogte (alleen XS/S) → RootView maakt het venster passend. In M+ niet bijhouden: dat zou elke pixel verversen.
        .onGeometryChange(for: CGFloat.self) { $0.size.height } action: { if t <= .s { contentHeight = $0 } }
        .preference(key: FitHeightKey.self, value: t <= .s ? contentHeight : 0)
        .frame(minHeight: CGFloat(l.rows) * (17 + theme.rowPad / 2) + 10, alignment: .top) // ≈ XS-hoogte
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
    var graphs = true
    var ready = true            // false tot de eerste echte meting binnen is
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
                if !data.side.isEmpty {   // temperatuur / ms / laden: klein naast het label, zodat het getal echt rechts staat
                    ChangingText(text: data.side, delay: stagger, ready: ready).font(theme.number(xs ? 11 : 13)).foregroundStyle(data.sideLevel > 0 ? color(data.sideLevel) : theme.onRow(true))
                        .lineLimit(1).fixedSize()
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
            if tier < .m && perCore && !data.cores.isEmpty {
                CoreBars(values: data.cores, split: Sampler.efficiencyCores).frame(height: xs ? 6 : 8)
            }
            if tier >= .m {
                // Geen uitlegregels op de Live-pagina: alleen labels en getallen.
                if !stats.isEmpty {
                    Text(stats).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1).minimumScaleFactor(0.8)
                }
                // Groot venster (L/XL): altijd per kern, over de hele breedte. Kleiner: alleen met de instelling.
                if !data.cores.isEmpty && (tier >= .l || perCore) {
                    CoreBars(values: data.cores, split: Sampler.efficiencyCores, wide: tier >= .l).frame(height: tier >= .l ? 28 : 12)
                }
                if graphs && !data.values.isEmpty {
                    SparkView(values: data.values, second: data.second, top: data.top, bottom: data.bottom, fill: fill, axis: nil, order: index)
                        .frame(minHeight: 16, maxHeight: tier >= .l ? 320 : .infinity)
                }
            }
        }
        .padding(.vertical, xs ? -inner / 4 : 0)
        .frame(maxHeight: tier >= .m ? .infinity : nil, alignment: .top)
        .themedRow(theme, index: index)
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
    @State private var shift: CGFloat = 0
    @State private var count: CGFloat = 0
    @State private var drawn: CGFloat = 1        // 0…1: hoeveel van de lijn getekend is (opstart-animatie)

    var body: some View {
        // Uitdunnen gebeurt in Spark.path(in:), op basis van de getekende breedte.
        let scale = top ?? max(values.max() ?? 1, second.max() ?? 1, 1)
        let color = theme.rowStyle == .blocks ? Color.white : theme.accent
        ZStack {
            Spark(values: values, top: scale, fill: fill, closed: true, bottom: bottom, shift: shift, count: count).fill(color.opacity(0.15))
            Spark(values: values, top: scale, fill: fill, closed: false, bottom: bottom, shift: shift, count: count)
                .stroke(color, style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
            if !second.isEmpty {
                Spark(values: second, top: scale, fill: fill, closed: false, bottom: bottom, shift: shift, count: count)
                    .stroke(color.opacity(0.5), lineWidth: 1)
            }
        }
        .clipped()
        // Grafiek verschijnt (opstarten, tabwissel, venster groter): vanaf rechts in beeld schuiven, één voor één
        .mask(alignment: .trailing) { Rectangle().scaleEffect(x: drawn, anchor: .trailing) }
        // Bij het opstarten is er nog geen lijn (minstens 2 metingen nodig): dan pas inschuiven als hij er is
        .onAppear { drawn = 0; if values.count > 1 { reveal() } }
        .onChange(of: values.count > 1) { _, has in if has { reveal() } }
        // Glijden: na een nieuwe meting begint de lijn precies waar hij stond (nieuw punt nog rechts buiten beeld)
        // en schuift/rekt hij in ~2 s rustig naar de nieuwe stand. Werkt zowel bij een vol als bij een groeiend venster.
        .onAppear { count = CGFloat(values.count) }
        .onChange(of: values) { old, new in
            guard new.count > 1, old != new else { return }
            let grew = new.count == old.count + 1, slid = new.count == old.count
            var t = Transaction(); t.disablesAnimations = true
            guard grew || slid else { withTransaction(t) { shift = 0; count = CGFloat(new.count) }; return }
            withTransaction(t) { shift = 1; count = CGFloat(old.count) }
            DispatchQueue.main.async {
                withAnimation(.linear(duration: 1.9)) { shift = 0; count = CGFloat(new.count) }
            }
        }
        .overlay(alignment: .topLeading) {
            if axis != nil { Text(top == 100 ? "100%" : top == nil ? short(scale) : "\(Int(scale.rounded())) GB").padding(2) }
        }
        .overlay(alignment: .bottom) {
            if let axis { HStack { Text(axis); Spacer(); Text("now") }.padding(2) }
        }
        .font(theme.label(9)).foregroundStyle(theme.onRow(true))
    }
}

/// Eén dun staafje per kern; zuinige kernen (vooraan) en snelle kernen met een kleine tussenruimte.
struct CoreBars: View {
    @Environment(\.theme) private var theme
    let values: [Double]
    var split = 0
    var wide = false            // groot venster: staafjes vullen de hele breedte

    var body: some View {
        let color = theme.rowStyle == .blocks ? Color.white : theme.accent
        HStack(alignment: .bottom, spacing: wide ? 3 : 2) {
            ForEach(values.indices, id: \.self) { i in
                RoundedRectangle(cornerRadius: 1).fill(color.opacity(0.15))
                    .overlay {
                        RoundedRectangle(cornerRadius: 1).fill(color)
                            .scaleEffect(x: 1, y: min(max(values[i], 0), 100) / 100, anchor: .bottom)
                            .animation(.easeInOut(duration: 0.8), value: values[i])
                    }
                    .frame(width: wide ? nil : 4)
                    .frame(maxWidth: wide ? .infinity : nil)
                    .padding(.leading, i == split && split > 0 ? (wide ? 8 : 4) : 0)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

extension SparkView {
    /// Zachte curve: snel in, rustig uitlopen; grafiekjes één voor één
    fileprivate func reveal() {
        withAnimation(.timingCurve(0.22, 1, 0.36, 1, duration: 0.9).delay(0.1 + Double(order) * 0.12)) { drawn = 1 }
    }
}

/// Hoogte die de Live-pagina in XS/S nodig heeft (0 = M of groter, niet passend maken).
struct FitHeightKey: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
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
    @State private var shown: String?
    @State private var flip = false   // eerste echte waarde komt eenmalig binnen met een flip, daarna het gekozen effect

    var body: some View {
        let t = shown ?? ""
        Group {
            if flip || style == .roll {
                // countsDown: nieuwe cijfers schuiven van boven naar beneden in beeld
                Text(t).contentTransition(.numericText(countsDown: true)).animation(.snappy(duration: 0.35), value: t)
            } else if style == .fade {
                // Zacht overvloeien van getal naar getal, op exact dezelfde plek (rechts uitgelijnd), geen dip naar zwart
                ZStack(alignment: .trailing) {
                    Text(t).id(t).transition(.opacity)
                }
                .animation(.easeInOut(duration: 0.5), value: t)
            } else {
                Text(t)
            }
        }
        .onAppear { if ready { shown = text } }
        .onChange(of: ready ? text : nil) { _, new in
            guard let new else { return }
            let first = shown == nil
            if first { flip = true }
            DispatchQueue.main.asyncAfter(deadline: .now() + (first ? 0.1 + delay / 3 : delay)) {
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
                withAnimation(.spring(duration: 0.35, bounce: 0.2).delay(0.03 + Double(index) * 0.04)) { shown = true }
            }
    }
}
