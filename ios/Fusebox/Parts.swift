import SwiftUI

// MARK: - Bouwstenen die elke pagina gebruikt: pagina-achtergrond, kaart, uitleg-label, sleutel/waarde, grafieklijn

extension Theme {
    /// Pagina-achtergrond. Liquid Glass heeft geen eigen kleur: daar de grijze iOS-achtergrond, zodat de lichte kaarten opvallen.
    var page: Color { bg ?? Color(uiColor: .systemGroupedBackground) }
    var onBlocks: Bool { rowStyle == .blocks }
    /// Accent op een kaart (wit op kleurblokken).
    var onRowAccent: Color { onBlocks ? .white : accent }
    /// Kleur van een oordeel: goed, let op, probleem.
    func tone(_ ok: Bool?) -> Color { onBlocks ? .white : ok == true ? good : ok == false ? warn : onRow(true) }
}

/// Eén pagina: scrollt, themakleur tot achter de statusbalk en de tabbalk.
struct Screen<Content: View>: View {
    @Environment(\.theme) private var theme
    @ViewBuilder var content: Content
    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 12) { content }
                .padding(.horizontal, 16).padding(.top, 8).padding(.bottom, 24)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
        .scrollIndicators(.hidden)
        // Effen strook achter de klok en de batterij, zodat de lijst daar niet onderdoor schuift
        .safeAreaInset(edge: .top, spacing: 0) { Color.clear.frame(height: 0).background(theme.page) }
        .foregroundStyle(theme.text)
        .background(theme.page.ignoresSafeArea())
    }
}

/// Paginatitel bovenaan (rustig, geen grote iOS-kop).
struct PageTitle: View {
    let title: String
    var note: String? = nil
    @Environment(\.theme) private var theme
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(verbatim: title).font(theme.label(22)).foregroundStyle(theme.text)
            if let note { Text(verbatim: note).font(theme.label(12)).foregroundStyle(theme.secondary).fixedSize(horizontal: false, vertical: true) }
        }
        .padding(.bottom, 2)
    }
}

/// Kaartvorm per thema (zoals de Mac): bij lijnen/kaal een zacht kaartvlak, anders de themarij.
private struct Card: ViewModifier {
    let theme: Theme, index: Int
    func body(content: Content) -> some View {
        switch theme.rowStyle {
        case .lines, .plain:
            content.padding(14)
                .background(theme.card, in: .rect(cornerRadius: 14))
                .overlay(RoundedRectangle(cornerRadius: 14).stroke(theme.line, lineWidth: 1))
        default:
            content.padding(.horizontal, 4).padding(.vertical, 6).themedRow(theme, index: index)
        }
    }
}
extension View {
    func card(_ theme: Theme, index: Int = 0) -> some View {
        frame(maxWidth: .infinity, alignment: .leading).modifier(Card(theme: theme, index: index))
    }
}

/// Label dat uitleg geeft als je erop tikt (op de Mac: zweven). Elke waarde heeft er een.
struct InfoTap: View {
    let title: String, text: String
    @Environment(\.theme) private var theme
    @State private var show = false
    var body: some View {
        Button { show = true } label: {
            Text(verbatim: title).underline(pattern: .dot, color: theme.onRow(true).opacity(0.6))
        }
        .buttonStyle(.plain)
        .popover(isPresented: $show) {
            VStack(alignment: .leading, spacing: 4) {
                Text(verbatim: title).font(theme.label(13)).foregroundStyle(theme.text)
                Text(verbatim: text).font(theme.font.font(13 * theme.scale, weight: .regular, style: theme.design)).foregroundStyle(theme.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(14).frame(width: 290, alignment: .leading)
            .presentationCompactAdaptation(.popover)
            .presentationBackground(theme.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.regularMaterial))
        }
        .accessibilityHint(text)
    }
}
extension InfoTap {
    /// Uitleg uit de gedeelde lijst van de Mac; drie teksten noemen "this Mac" en hebben een iPhone-versie.
    init(_ term: NetTerm, title: String? = nil) {
        let text: String = switch term {
        case .type: L("How this iPhone is connected: Wi-Fi, mobile data or a cable adapter. Wi-Fi is usually faster and steadier than mobile data.")
        case .ipv4: L("This iPhone's address on your own network, handed out by the router. The internet doesn't see it.")
        case .ipv6: L("This iPhone's newer, longer address, if your network supports IPv6. \"none\" is common and not a problem.")
        default: term.text
        }
        self.init(title: title ?? term.title, text: text)
    }
}

/// Sleutel links (met uitleg), waarde rechts.
struct KV<K: View>: View {
    let value: String
    var tone: Color? = nil
    @ViewBuilder var key: K
    @Environment(\.theme) private var theme
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            key.font(theme.label(13)).foregroundStyle(theme.onRow(true))
            Spacer(minLength: 8)
            Text(verbatim: value).font(theme.number(13)).foregroundStyle(tone ?? theme.onRow()).multilineTextAlignment(.trailing)
        }
    }
}
extension KV where K == Text {
    init(_ key: String, _ value: String, tone: Color? = nil) { self.init(value: value, tone: tone) { Text(verbatim: key) } }
}
extension KV where K == InfoTap {
    init(_ term: NetTerm, _ value: String, tone: Color? = nil) { self.init(value: value, tone: tone) { InfoTap(term) } }
    init(_ title: String, info: String, _ value: String, tone: Color? = nil) { self.init(value: value, tone: tone) { InfoTap(title: title, text: info) } }
}

/// Kopje binnen een kaart.
struct CardHeader: View {
    let title: String, icon: String
    @Environment(\.theme) private var theme
    var body: some View {
        Label { Text(verbatim: title) } icon: { Image(systemName: icon) }
            .font(theme.label(13)).foregroundStyle(theme.id == "instrument" ? theme.accent : theme.onRow(true))
    }
}

/// Oordeel in gewone taal met een icoon (goed, let op, probleem).
struct StatusLine: View {
    let text: String, ok: Bool?
    @Environment(\.theme) private var theme
    var body: some View {
        Label {
            Text(verbatim: text).fixedSize(horizontal: false, vertical: true)
        } icon: {
            Image(systemName: ok == true ? "checkmark.circle" : ok == false ? "exclamationmark.triangle" : "info.circle")
        }
        .font(theme.label(13)).foregroundStyle(theme.tone(ok))
    }
}

/// Grafieklijn met de gedeelde rekenregels van de Mac (`Spark`); tekent alleen opnieuw bij nieuwe punten.
struct SparkLine: View {
    let main: [Double]
    var second: [Double] = []
    var points = 60
    var floor = 5.0
    @Environment(\.theme) private var theme
    var body: some View {
        let top = max((main + second).max() ?? 0, floor) * 1.2
        let fill = Double(max(main.count - 1, 1)) / Double(points - 1)
        let c = theme.onRowAccent
        ZStack {
            Spark(values: main, top: top, fill: fill, closed: true).fill(c.opacity(0.15))
            Spark(values: main, top: top, fill: fill, closed: false).stroke(c, style: StrokeStyle(lineWidth: 1.5, lineCap: .round, lineJoin: .round))
            if second.count > 1 {
                Spark(values: second, top: top, fill: Double(second.count - 1) / Double(points - 1), closed: false)
                    .stroke(c.opacity(0.5), style: StrokeStyle(lineWidth: 1, lineCap: .round, lineJoin: .round))
            }
        }
        .accessibilityHidden(true)
    }
}

/// Deelmenu van iOS voor een bestand (het netwerkrapport).
struct ShareFile: Identifiable { let url: URL; var id: URL { url } }
struct ShareSheet: UIViewControllerRepresentable {
    let url: URL
    func makeUIViewController(context: Context) -> UIActivityViewController { UIActivityViewController(activityItems: [url], applicationActivities: nil) }
    func updateUIViewController(_ c: UIActivityViewController, context: Context) {}
}
