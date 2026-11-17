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
    let bg: Color?                 // nil = venster-glas (Liquid Glass)
    let text: Color, secondary: Color
    var accent: Color
    let warn: Color, danger: Color, good: Color
    let line: Color                // scheidingslijnen / kaartrand
    let card: Color                // kaartachtergrond
    let rowStyle: RowStyle
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
    static func == (a: Theme, b: Theme) -> Bool { a.id == b.id && a.accent == b.accent && a.scale == b.scale && a.font == b.font }
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
        bg: .white, text: Color(white: 0.07), secondary: Color(white: 0.54), accent: Color(white: 0.07),
        warn: Color(red: 0.90, green: 0.50, blue: 0.10), danger: Color(red: 0.85, green: 0.20, blue: 0.20), good: Color(red: 0.15, green: 0.6, blue: 0.35),
        line: Color(white: 0.92), card: Color(white: 0.97),
        rowStyle: .plain, design: .default, weight: .light, blocks: [], scheme: .light)
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
        return t
    }
}

private struct ThemeKey: EnvironmentKey { static let defaultValue = Theme.instrument }
extension EnvironmentValues {
    var theme: Theme { get { self[ThemeKey.self] } set { self[ThemeKey.self] = newValue } }
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
struct Spark: Shape {
    let values: [Double], top: Double, fill: Double, closed: Bool
    var bottom = 0.0
    /// Glijden na een nieuwe meting: `shift` (1 → 0) schuift de lijn een punt naar links,
    /// `count` (oud → nieuw aantal punten) rekt hem geleidelijk op als de reeks nog groeit. Beide animeerbaar.
    var shift: CGFloat = 0
    var count: CGFloat = 0
    var animatableData: AnimatablePair<CGFloat, CGFloat> {
        get { AnimatablePair(shift, count) }
        set { shift = newValue.first; count = newValue.second }
    }
    /// Hoe rond de hoeken zijn: 0 = recht, 0,5 = helemaal rond. Licht afgerond oogt rustig maar niet "bollig".
    static let roundness: CGFloat = 0.2

    func path(in r: CGRect) -> Path {
        var p = Path()
        let w = r.width * fill
        // Pas uitdunnen als er veel meer punten dan pixels zijn (lange periodes); dan niet glijden
        let thinned = self.values.count > Int(w * 2)
        let values = thinned ? downsample(self.values, to: max(Int(w), 2)) : self.values
        guard values.count > 1 else { return p }
        let n = CGFloat(values.count)
        let c = thinned || count < 2 ? n : count
        let step = w / (c - 1)
        let s = thinned ? 0 : shift
        let pts = values.enumerated().map { i, v in
            CGPoint(x: r.maxX - step * (n - 1 - CGFloat(i)) + step * s,
                    y: r.maxY - r.height * min(max((v - bottom) / (top - bottom), 0), 1))
        }
        // Rechte stukken met licht afgeronde hoeken
        p.move(to: pts[0])
        if pts.count > 2 {
            let k = Self.roundness
            func lerp(_ a: CGPoint, _ b: CGPoint, _ t: CGFloat) -> CGPoint { CGPoint(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t) }
            for i in 1..<(pts.count - 1) {
                p.addLine(to: lerp(pts[i], pts[i - 1], k))
                p.addQuadCurve(to: lerp(pts[i], pts[i + 1], k), control: pts[i])
            }
        }
        p.addLine(to: pts[pts.count - 1])
        if closed { p.addLine(to: CGPoint(x: pts[pts.count - 1].x, y: r.maxY)); p.addLine(to: CGPoint(x: pts[0].x, y: r.maxY)); p.closeSubpath() }
        return p
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
    static func send(title: String, body: String) {
        let c = UNMutableNotificationContent(); c.title = title; c.body = body
        UNUserNotificationCenter.current().add(UNNotificationRequest(identifier: UUID().uuidString, content: c, trigger: nil))
    }
}
