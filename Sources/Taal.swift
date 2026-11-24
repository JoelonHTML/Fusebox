import SwiftUI

// MARK: - Vertalingen
// De teksten staan in Resources/<taal>.lproj/Localizable.strings; de sleutel is altijd de Engelse tekst.
// SwiftUI-letterlijke teksten (Text("…"), Button("…"), …) zoeken zelf; tekst die als gewone String
// wordt gebouwd gaat door L("…"). Zo vindt `swiftc -emit-localized-strings` (scripts/strings-check.sh) alles.
// Let op: in L() geen kale Double interpoleren (wordt "%lf" → 2,500000); eerst zelf opmaken tot String.

/// Vertaalde tekst voor een gewone String (tips, meldingen, opgebouwde zinnen).
/// POSIX-locale: getallen zoals vroeger in "\(n)" (geen 1.000-groepering), de taal komt uit de bundel.
func L(_ v: String.LocalizationValue) -> String { String(localized: v, locale: Locale(identifier: "en_US_POSIX")) }

/// Taalkeuze in Settings › Look. Leeg = taal van de Mac. Werkt na een herstart (macOS leest AppleLanguages bij het starten).
enum AppLanguage: String, CaseIterable, Identifiable {
    case system = "", en, nl, de, fr, es
    var id: String { rawValue }
    /// Eigen naam van de taal (niet vertalen: zo vind je je taal altijd terug).
    var name: String {
        switch self {
        case .system: L("System")
        case .en: "English"
        case .nl: "Nederlands"
        case .de: "Deutsch"
        case .fr: "Français"
        case .es: "Español"
        }
    }

    static func apply(_ l: AppLanguage) {
        if l == .system { UserDefaults.standard.removeObject(forKey: "AppleLanguages") }
        else { UserDefaults.standard.set([l.rawValue], forKey: "AppleLanguages") }
    }

    #if os(macOS)   // iPhone: taal kiezen gaat via Instellingen › Fusebox van iOS zelf
    /// Fusebox opnieuw starten (zelfde truc als na een update: wachten tot dit proces weg is, dan openen).
    static func relaunch() {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; /usr/bin/open \"$0\"", Bundle.main.bundleURL.path, String(getpid())]
        try? p.run()
        NSApp.terminate(nil)
    }
    #endif
}

#if os(macOS)
struct LanguagePicker: View {
    @AppStorage("appLanguage") private var lang = ""
    @State private var changed = false

    var body: some View {
        Picker("Language", selection: Binding(get: { AppLanguage(rawValue: lang) ?? .system },
                                              set: { lang = $0.rawValue; AppLanguage.apply($0); changed = true })) {
            ForEach(AppLanguage.allCases) { Text(verbatim: $0.name).tag($0) }
        }
        if changed {
            HStack {
                Text("Applies after a restart of Fusebox.").font(.caption).foregroundStyle(.secondary)
                Spacer()
                Button("Restart Fusebox") { AppLanguage.relaunch() }
            }
        }
    }
}
#endif

/// Taal van de schermteksten, voor dag- en maandnamen op het scherm ("do 16:31" i.p.v. "Thu 16:31").
/// Niet voor bestanden of logs: die houden en_US_POSIX.
let uiLocale = Locale(identifier: Bundle.main.preferredLocalizations.first ?? "en")
