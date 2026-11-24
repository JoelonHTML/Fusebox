import SwiftUI

// MARK: - Checkup › Stability: wat erbij kwam naast de Crashes-kaart
//
// Niets draait op een timer: de kaarten lezen alleen terwijl de groep Stability open staat, buiten de hoofdthread.

/// Crashes, vastlopers en kernel panics per dag over de laatste 30 dagen, los van de gekozen periode.
/// Dezelfde rapporten van macOS als de Crashes-kaart (alleen de kop van elk bestand); geen eigen score.
struct StabilityStrip: View {
    @Environment(\.theme) private var theme
    let index: Int
    static let days = 30
    @MainActor private static var cache: [Int]?
    @State private var counts: [Int]? = MainActor.assumeIsolated { StabilityStrip.cache }

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            CardTitle(icon: "calendar", title: L("Last 30 days"))
            if let counts {
                let n = counts.reduce(0, +), cal = Calendar.current
                let day = { (i: Int) in cal.date(byAdding: .day, value: i - (counts.count - 1), to: Date()) ?? Date() }
                Headline(big: n == 0 ? L("None") : "\(n)", small: L("Crashes and hangs per day, from macOS' own reports"), tone: n == 0 ? 3 : 0)
                // Dag van de maand onder elke zevende dag, geteld vanaf vandaag (rechts)
                Bars(values: counts, labels: counts.indices.map { ($0 - (counts.count - 1)) % 7 == 0 ? "\(cal.component(.day, from: day($0)))" : "" }) {
                    day($0).formatted(Date.FormatStyle().weekday(.abbreviated).day().month(.abbreviated).locale(uiLocale)) + " · \(counts[$0])"
                }
            } else {
                ProgressView().controlSize(.small)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme, index: index)
        .task {
            let days = Self.days
            let r = await Task.detached(priority: .utility) { () -> [Int] in
                let now = Date()
                let from = Calendar.current.startOfDay(for: now.addingTimeInterval(-Double(days - 1) * 86_400))
                // ponytail: crashes() leest hoogstens de nieuwste 300 rapporten; bij meer in 30 dagen tellen de oudste dagen te laag (grens verhogen als dat speelt)
                return Diag.perDay(Diag.crashes(from: from, to: .distantFuture).stamps, days: days, now: now)
            }.value
            Self.cache = r
            counts = r
        }
    }
}
