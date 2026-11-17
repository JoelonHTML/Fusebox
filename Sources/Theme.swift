import SwiftUI
import UserNotifications

// MARK: - Stijlen (kiesbaar in Instellingen)
//
// Gebruik in views:
//   @Environment(\.theme) private var theme
//   Text("23%").font(theme.number(30)).foregroundStyle(theme.text)
//   VStack { ... }.themedRow(theme, index: 0)      // kaart/regel/blok in de stijl van het thema
//   .background(theme.background)                   // achtergrond van een pagina

struct Theme: Identifiable, Equatable {
    enum RowStyle { case lines, plain, cards, blocks, glass }
    let id: String, name: String
    var bg: Color?                 // nil = venster-glas (Liquid Glass)
    let text: Color, secondary: Color
    var accent: Color
    let warn: Color, danger: Color, good: Color
    let line: Color                // scheidingslijnen / kaartrand
    var card: Color                // kaartachtergrond
    var rowStyle: RowStyle
    let design: Font.Design
    let weight: Font.Weight
    let blocks: [Color]            // alleen voor .blocks
    let scheme: ColorScheme?       // donker/licht forceren
    var scale: CGFloat = 1         // tekstgrootte (instelling "textScale")
    var font = AppFont.style       // lettertype (instelling "font"); .style = wat bij de stijl hoort

    func number(_ size: CGFloat) -> Font { font.font(size * scale, weight: weight, style: design).monospacedDigit() }
    func label(_ size: CGFloat = 12) -> Font { font.font(size * scale, weight: .medium, style: design) }
    /// Tekstkleur op een rij (bij kleurblokken altijd wit).
    func onRow(_ secondaryText: Bool = false) -> Color {
        rowStyle == .blocks ? .white.opacity(secondaryText ? 0.8 : 1) : (secondaryText ? secondary : text)
    }
    var background: AnyShapeStyle { bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.clear) }
    static func == (a: Theme, b: Theme) -> Bool { a.id == b.id && a.accent == b.accent && a.scale == b.scale && a.font == b.font && a.rowStyle == b.rowStyle }
    /// Verticale ruimte per rij bovenop de inhoud (padding + tussenruimte), per rijstijl; voor layout-berekeningen.
    var rowPad: CGFloat {
        switch rowStyle { case .lines: 15; case .plain: 10; case .cards, .blocks, .glass: 22 }
    }

    static let instrument = Theme(id: "instrument", name: "Instrument",
        bg: Color(red: 0.122, green: 0.169, blue: 0.239), text: Color(red: 0.95, green: 0.96, blue: 0.97),
        secondary: Color(red: 0.60, green: 0.65, blue: 0.72), accent: Color(red: 0.96, green: 0.72, blue: 0.24),
        warn: Color(red: 1.0, green: 0.60, blue: 0.25), danger: Color(red: 1.0, green: 0.38, blue: 0.35), good: Color(red: 0.35, green: 0.85, blue: 0.6),
        line: Color(red: 0.17, green: 0.23, blue: 0.31), card: Color(red: 0.15, green: 0.20, blue: 0.28),
        rowStyle: .lines, design: .rounded, weight: .semibold, blocks: [], scheme: .dark)
    static let terminal = Theme(id: "terminal", name: "Terminal",
        bg: Color(red: 0.04, green: 0.06, blue: 0.05), text: Color(red: 0.49, green: 0.99, blue: 0.60),
        secondary: Color(red: 0.25, green: 0.69, blue: 0.37), accent: Color(red: 0.49, green: 0.99, blue: 0.60),
        warn: Color(red: 1.0, green: 0.80, blue: 0.25), danger: Color(red: 1.0, green: 0.35, blue: 0.35), good: Color(red: 0.49, green: 0.99, blue: 0.60),
        line: Color(red: 0.11, green: 0.16, blue: 0.13), card: Color(red: 0.07, green: 0.10, blue: 0.08),
        rowStyle: .plain, design: .monospaced, weight: .regular, blocks: [], scheme: .dark)
    static let minimal = Theme(id: "minimal", name: "Minimal light",
        // Contrast op wit én op de kaartkleur minstens 4,5 : 1 (WCAG AA): grijs 6,7, oranje 5,1, rood 5,9, groen 6,0
        // Eén rustige blauwe accentkleur (zwart gaf grijze blokken, en verdween in donker); dunne scheidingslijnen
        bg: .white, text: Color(white: 0.07), secondary: Color(white: 0.36), accent: Color(red: 0.0, green: 0.42, blue: 0.85),
        warn: Color(red: 0.69, green: 0.33, blue: 0.0), danger: Color(red: 0.77, green: 0.12, blue: 0.12), good: Color(red: 0.05, green: 0.45, blue: 0.24),
        line: Color(white: 0.9), card: Color(white: 0.97),
        rowStyle: .lines, design: .default, weight: .regular, blocks: [], scheme: .light)
    static let glass = Theme(id: "glas", name: "Glass",
        bg: Color(red: 0.36, green: 0.29, blue: 0.84), text: .white, secondary: .white.opacity(0.78), accent: .white,
        warn: Color(red: 1.0, green: 0.85, blue: 0.4), danger: Color(red: 1.0, green: 0.55, blue: 0.55), good: .white,
        line: .white.opacity(0.35), card: .white.opacity(0.16),
        rowStyle: .cards, design: .rounded, weight: .semibold, blocks: [], scheme: .dark)
    static let blocks = Theme(id: "blokken", name: "Color blocks",
        bg: Color(red: 0.96, green: 0.95, blue: 0.92), text: Color(white: 0.1), secondary: Color(red: 0.42, green: 0.40, blue: 0.37),
        accent: Color(red: 0.18, green: 0.49, blue: 0.84),
        warn: Color(red: 0.95, green: 0.55, blue: 0.1), danger: Color(red: 0.85, green: 0.2, blue: 0.2), good: Color(red: 0.11, green: 0.62, blue: 0.46),
        line: Color(red: 0.85, green: 0.83, blue: 0.77), card: .white,
        rowStyle: .blocks, design: .rounded, weight: .bold,
        blocks: [Color(red: 0.88, green: 0.34, blue: 0.24), Color(red: 0.18, green: 0.49, blue: 0.84),
                 Color(red: 0.11, green: 0.62, blue: 0.46), Color(red: 0.48, green: 0.33, blue: 0.78), Color(red: 0.85, green: 0.6, blue: 0.1)],
        scheme: .light)
    static let liquid = Theme(id: "liquid", name: "Liquid Glass",
        bg: nil, text: .primary, secondary: .secondary, accent: .accentColor,
        warn: .orange, danger: .red, good: .green,
        line: .primary.opacity(0.1), card: .clear,
        rowStyle: .glass, design: .rounded, weight: .semibold, blocks: [], scheme: nil)

    static let all: [Theme] = [.instrument, .terminal, .minimal, .glass, .blocks, .liquid]
    static func named(_ id: String) -> Theme { all.first { $0.id == id } ?? .instrument }
    /// Thema met de eigen accentkleur ("accent", "" = standaard) en tekstgrootte ("textScale") erin.
    static func resolved(_ id: String) -> Theme {
        var t = named(id)
        let d = UserDefaults.standard
        if let c = Color(hex: d.string(forKey: "accent") ?? "") { t.accent = c }
        t.scale = min(max(d.double(forKey: "textScale"), 0.8), 1.6)
        t.font = AppFont(rawValue: d.string(forKey: "font") ?? "") ?? .style
        return t
    }
}

/// Lettertype voor getallen en labels. Montserrat en Ubuntu zitten in de app (Resources/Fonts, ATSApplicationFontsPath).
enum AppFont: String, CaseIterable, Identifiable {
    case style, sfPro, rounded, mono, serif, montserrat, ubuntu
    var id: String { rawValue }
    var title: String {
        switch self {
        case .style: "Matches style"
        case .sfPro: "SF Pro"
        case .rounded: "SF Rounded"
        case .mono: "SF Mono"
        case .serif: "New York"
        case .montserrat: "Montserrat"
        case .ubuntu: "Ubuntu"
        }
    }
    func font(_ size: CGFloat, weight: Font.Weight, style: Font.Design) -> Font {
        switch self {
        case .style: .system(size: size, weight: weight, design: style)
        case .sfPro: .system(size: size, weight: weight, design: .default)
        case .rounded: .system(size: size, weight: weight, design: .rounded)
        case .mono: .system(size: size, weight: weight, design: .monospaced)
        case .serif: .system(size: size, weight: weight, design: .serif)
        case .montserrat: .custom("Montserrat", size: size).weight(weight)
        case .ubuntu: .custom("Ubuntu", size: size).weight(weight)
        }
    }
}

/// Licht / donker / zoals macOS (instelling "appearanceMode").
enum AppearanceMode: String, CaseIterable, Identifiable {
    case system, light, dark
    var id: String { rawValue }
    /// Voor .preferredColorScheme: nil = zoals macOS
    var scheme: ColorScheme? { self == .light ? .light : self == .dark ? .dark : nil }
    var title: String {
        switch self {
        case .system: "System"
        case .light: "Light"
        case .dark: "Dark"
        }
    }
}

extension Theme {
    /// Dezelfde stijl (accent, lettertype, rijvorm, kleurblokken) in een lichte of donkere variant.
    /// Stijlen die al zo zijn (of Liquid Glass, dat met het systeem meegaat) blijven ongewijzigd.
    func adapted(to s: ColorScheme) -> Theme {
        guard let own = scheme, own != s else { return self }
        let dark = s == .dark
        var t = Theme(id: id, name: name,
                      bg: dark ? Color(white: 0.11) : .white,
                      text: dark ? Color(white: 0.95) : Color(white: 0.08),
                      secondary: dark ? Color(white: 0.62) : Color(white: 0.45),
                      accent: accent, warn: warn, danger: danger, good: good,
                      line: dark ? Color(white: 0.22) : Color(white: 0.9),
                      card: dark ? Color(white: 0.16) : Color(white: 0.96),
                      rowStyle: rowStyle, design: design, weight: weight, blocks: blocks, scheme: s)
        t.scale = scale
        t.font = font   // gekozen lettertype ook in de licht/donker-variant
        // Lichte accenten (zoals wit bij Glas) zijn onleesbaar op wit: dan de tekstkleur als accent
        if !dark && (id == "glas") { t.accent = Color(white: 0.08) }
        // En andersom: een donker accent (eigen keuze) verdwijnt op donker: dan iets lichter
        if dark, let c = NSColor(accent).usingColorSpace(.sRGB), Self.luminance(c) < 0.3 {
            t.accent = Color(hue: c.hueComponent, saturation: c.saturationComponent * 0.6, brightness: 1)
        }
        return t
    }
}

extension Theme {
    /// Hoe licht een kleur oogt (0 zwart … 1 wit), zoals WCAG het rekent.
    static func luminance(_ c: NSColor) -> CGFloat {
        func l(_ v: CGFloat) -> CGFloat { v <= 0.04045 ? v / 12.92 : pow((v + 0.055) / 1.055, 2.4) }
        return 0.2126 * l(c.redComponent) + 0.7152 * l(c.greenComponent) + 0.0722 * l(c.blueComponent)
    }
}

extension Theme {
    /// Low impact: dezelfde stijl zonder glas of vervaging (effen venster, effen kaarten). Alleen Liquid Glass verandert;
    /// de andere stijlen zijn al effen.
    var solid: Theme {
        guard rowStyle == .glass else { return self }
        var t = self
        t.bg = Color(nsColor: .windowBackgroundColor); t.card = .primary.opacity(0.05); t.rowStyle = .cards
        return t
    }
}

private struct ThemeKey: EnvironmentKey { static let defaultValue = Theme.instrument }
private struct OnScreenKey: EnvironmentKey { static let defaultValue = true }
extension EnvironmentValues {
    var theme: Theme { get { self[ThemeKey.self] } set { self[ThemeKey.self] = newValue } }
    /// false = het venster is niet te zien (dicht, geminimaliseerd, helemaal bedekt) of low impact staat aan: dan niets
    /// animeren (getallen wisselen direct, grafieken verspringen). SwiftUI rekent animaties ook voor een onzichtbaar
    /// venster elke frame door.
    var onScreen: Bool { get { self[OnScreenKey.self] } set { self[OnScreenKey.self] = newValue } }
}

/// Zet een rij/kaart in de vorm van het thema. `index` kiest de kleur bij kleurblokken.
struct ThemedRow: ViewModifier {
    let theme: Theme, index: Int
    func body(content: Content) -> some View {
        switch theme.rowStyle {
        case .lines:
            content.padding(.vertical, 7)
                .overlay(alignment: .bottom) { Rectangle().fill(theme.line).frame(height: 1) }
        case .plain:
            content.padding(.vertical, 5)
        case .cards:
            content.padding(.horizontal, 10).padding(.vertical, 8)
                .background(theme.card, in: .rect(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(theme.line, lineWidth: 1))
        case .blocks:
            content.padding(.horizontal, 10).padding(.vertical, 8)
                .background(theme.blocks[index % max(theme.blocks.count, 1)], in: .rect(cornerRadius: 10))
        case .glass:
            content.padding(.horizontal, 10).padding(.vertical, 8)
                .glassEffect(.regular, in: .rect(cornerRadius: 14))
        }
    }
}
extension View {
    func themedRow(_ theme: Theme, index: Int = 0) -> some View { modifier(ThemedRow(theme: theme, index: index)) }
}

/// Goedkope sparkline: één Path, rechts uitgelijnd, hoogstens 1 punt per 2 pt breedte.
/// Stilstaand; de Live-grafieken glijden via `SparkLayers` (Overzicht.swift) en delen de rekenregels hieronder.
struct Spark: Shape {
    let values: [Double], top: Double, fill: Double, closed: Bool
    var bottom = 0.0
    /// Hoe rond de hoeken zijn: 0 = recht, 0,5 = helemaal rond. Licht afgerond oogt rustig maar niet "bollig".
    static let roundness: CGFloat = 0.2

    func path(in r: CGRect) -> Path {
        let w = r.width * fill
        let v = Self.thinned(values.count, width: w) ? downsample(values, to: max(Int(w), 2)) : values
        return Self.path(Self.points(v, in: CGRect(x: r.maxX - w, y: r.minY, width: w, height: r.height), top: top, bottom: bottom), closed: closed, floor: r.maxY)
    }

    /// Pas uitdunnen als er veel meer punten dan pixels zijn (lange periodes); dan niet glijden.
    static func thinned(_ count: Int, width: CGFloat) -> Bool { count > Int(width * 2) }

    /// Wat er tussen twee metingen met de reeks gebeurde.
    enum Glide { case none, slide, grow }
    /// `slide`: oudste punt eraf en een nieuw erbij (vol venster); `grow`: alleen een punt erbij; anders `none` (niet glijden).
    static func glide(_ old: [Double], _ new: [Double]) -> Glide {
        guard old.count > 1 else { return .none }   // eerste lijn: geen glijden, alleen het inschuiven
        if new.count == old.count, new.dropLast().elementsEqual(old.dropFirst()) { return .slide }
        if new.count == old.count + 1, new.dropLast().elementsEqual(old) { return .grow }
        return .none
    }

    /// Plek van elk punt, nieuwste rechts tegen de rand. `lead`: punten die net uit het venster vielen en links
    /// uit beeld schuiven (oudste eerst). `shift`: 1 = waar de lijn stond vóór de nieuwe meting (nieuw punt nog rechts
    /// buiten beeld), 0 = eindstand. `stretch`: de reeks groeit nog, dan rekt de lijn uit i.p.v. te schuiven.
    static func points(_ values: [Double], lead: [Double] = [], in r: CGRect, top: Double, bottom: Double,
                       shift s: CGFloat = 0, stretch: Bool = false) -> [CGPoint] {
        let n = CGFloat(values.count)
        let step = r.width / max((stretch ? max(n - s, 2) : n) - 1, 1)
        return (lead + values).enumerated().map { i, v in
            CGPoint(x: r.maxX - step * (n - 1 - CGFloat(i - lead.count) - s),
                    y: r.maxY - r.height * min(max((v - bottom) / (top - bottom), 0), 1))
        }
    }

    /// Rechte stukken met licht afgeronde hoeken; `closed` vult tot de onderrand (`floor`).
    static func path(_ pts: [CGPoint], closed: Bool, floor: CGFloat) -> Path {
        var p = Path()
        guard pts.count > 1 else { return p }
        p.move(to: pts[0])
        if pts.count > 2 {
            let k = roundness
            func lerp(_ a: CGPoint, _ b: CGPoint, _ t: CGFloat) -> CGPoint { CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t) }
            for i in 1..<(pts.count - 1) {
                p.addLine(to: lerp(pts[i], pts[i - 1], k))
                p.addQuadCurve(to: lerp(pts[i], pts[i + 1], k), control: pts[i])
            }
        }
        p.addLine(to: pts[pts.count - 1])
        if closed { p.addLine(to: CGPoint(x: pts[pts.count - 1].x, y: floor)); p.addLine(to: CGPoint(x: pts[0].x, y: floor)); p.closeSubpath() }
        return p
    }

    /// Zelftest: de eindstand van een glij-animatie is exact de beginstand van de volgende (niets springt terug).
    static func selfTest() {
        let r = CGRect(x: 10, y: 0, width: 298, height: 80)
        func pts(_ v: [Double], lead: [Double] = [], s: CGFloat = 0, stretch: Bool = false) -> [CGPoint] {
            points(v, lead: lead, in: r, top: 100, bottom: 0, shift: s, stretch: stretch)
        }
        func same(_ a: some Collection<CGPoint>, _ b: some Collection<CGPoint>) -> Bool {
            a.count == b.count && zip(a, b).allSatisfy { abs($0.x - $1.x) < 1e-6 && abs($0.y - $1.y) < 1e-6 }
        }
        let v0 = (0..<150).map { Double($0 % 37) * 2.5 }, v1 = Array(v0.dropFirst()) + [42], v2 = Array(v1.dropFirst()) + [7]
        precondition(glide(v0, v1) == .slide && glide(v1, v2) == .slide && glide(v0, v0 + [1]) == .grow, "soort")
        precondition(glide(v0, v0.dropLast() + [99]) == .none && glide([1], [1, 2]) == .none && glide(v0, []) == .none, "niet glijden")
        // Vol venster: eind van glide 1 (shift 0) == begin van glide 2 (shift 1), voor elk punt dat blijft; links begint de lijn op de rand
        let end1 = pts(v1, lead: [v0[0]]), start2 = pts(v2, lead: [v0[0], v1[0]], s: 1), end2 = pts(v2, lead: [v0[0], v1[0]])
        precondition(same(end1, start2.dropLast()) && abs(start2[1].x - r.minX) < 1e-6 && abs(end1[1].x - r.minX) < 1e-6, "eind = begin (schuiven)")
        precondition(start2[0].x < r.minX && end2[1].x < r.minX && abs(end2[2].x - r.minX) < 1e-6 && start2.last!.x > r.maxX
                     && abs(end2.last!.x - r.maxX) < 1e-6, "oud punt schuift links uit beeld, nieuw komt rechts binnen")
        // Te vroege meting (vorige glide nog op 0,2): verder vanaf precies die stand
        precondition(same(pts(v1, lead: [v0[0]], s: 0.2), pts(v2, lead: [v0[0], v1[0]], s: 1.2).dropLast()), "onderbroken glide")
        // Groeiende reeks: lijn blijft over de hele breedte en rekt uit; eind = begin van de volgende
        let g0 = Array(v0.prefix(40)), g1 = g0 + [5], g2 = g1 + [60]
        precondition(same(pts(g0), pts(g1, s: 1, stretch: true).dropLast()) && same(pts(g1), pts(g2, s: 1, stretch: true).dropLast()), "eind = begin (groeien)")
        precondition((0...10).allSatisfy { abs(pts(g1, s: CGFloat($0) / 10, stretch: true)[0].x - r.minX) < 1e-6 }, "groeien: links vast")
        // Laatste keer groeien → eerste keer schuiven
        let full = Array(v0.prefix(41)), next = Array(full.dropFirst()) + [3]
        precondition(same(pts(full), pts(next, lead: [full[0]], s: 1).dropLast()), "groeien → schuiven")
        print("grafiek: ok")
    }
}

extension Color {
    /// "#RRGGBB" → kleur; nil bij lege of ongeldige tekst.
    init?(hex: String) {
        let h = hex.hasPrefix("#") ? String(hex.dropFirst()) : hex
        guard h.count == 6, let v = UInt32(h, radix: 16) else { return nil }
        self.init(red: Double(v >> 16 & 0xFF) / 255, green: Double(v >> 8 & 0xFF) / 255, blue: Double(v & 0xFF) / 255)
    }
    var hex: String {
        let c = NSColor(self).usingColorSpace(.sRGB) ?? .gray
        return String(format: "#%02X%02X%02X", Int((c.redComponent * 255).rounded()), Int((c.greenComponent * 255).rounded()), Int((c.blueComponent * 255).rounded()))
    }
}

/// Meldingen (macOS Berichtgeving).
enum Notify {
    static func ask() { UNUserNotificationCenter.current().requestAuthorization(options: [.alert, .sound]) { _, _ in } }
    static let whatsNew = "whatsnew"   // categorie met knop "What's new" → changelog in Settings › About
    static func registerCategories() {
        let open = UNNotificationAction(identifier: "open", title: "What's new", options: [.foreground])
        UNUserNotificationCenter.current().setNotificationCategories([UNNotificationCategory(identifier: whatsNew, actions: [open], intentIdentifiers: [])])
    }
    static func send(title: String, body: String, category: String? = nil) {
        let c = UNMutableNotificationContent(); c.title = title; c.body = body
        if let category { c.categoryIdentifier = category }
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
    }
}
