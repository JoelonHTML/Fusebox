import SwiftUI

// MARK: - Instellingen: uiterlijk, taal, netwerk, over
//
// Zelfde sleutels als op de Mac ("theme", "appearanceMode", "textScale", "font", "netAutoScan", "speedMB", "pingHost"),
// zodat de gedeelde code ze zonder omweg leest.

struct SettingsScreen: View {
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("appearanceMode") private var mode = "system"
    @AppStorage("textScale") private var textScale = 1.0
    @AppStorage("font") private var font = "style"
    @AppStorage("netAutoScan") private var autoScan = true
    @AppStorage("speedMB") private var speedMB = false
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @Environment(\.theme) private var theme

    var body: some View {
        Screen {
            PageTitle(title: L("Settings"))
            section(L("Appearance"), "paintbrush", 0) {
                ScrollView(.horizontal) {
                    HStack(spacing: 8) { ForEach(Theme.all) { tile($0) } }.padding(.vertical, 2)
                }
                .scrollIndicators(.hidden)
                Picker(selection: $mode) {
                    ForEach(AppearanceMode.allCases) { Text(verbatim: $0.title).tag($0.rawValue) }
                } label: { Text("Appearance") }
                .pickerStyle(.segmented)
                row(L("Text size")) {
                    Picker("Text size", selection: $textScale) {
                        ForEach([0.9, 1.0, 1.15, 1.3], id: \.self) { Text(verbatim: "\(Int(($0 * 100).rounded()))%").tag($0) }
                    }
                    .pickerStyle(.segmented).frame(maxWidth: 230)
                }
                row(L("Font")) {
                    Picker("Font", selection: $font) { ForEach(AppFont.allCases) { Text(verbatim: $0.title).tag($0.rawValue) } }
                        .pickerStyle(.menu).labelsHidden()
                }
            }
            section(L("Language"), "globe", 1) {
                note(L("Fusebox follows the language of your iPhone. You can pick another language just for Fusebox in the Settings app."))
                Button { if let u = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(u) } } label: {
                    NetPill(title: L("Open Settings"), icon: "arrow.up.forward.app")
                }
                .buttonStyle(.plain)
            }
            section(L("Network"), "network", 2) {
                Toggle(isOn: $autoScan) { Text("Scan when you open Network").font(theme.label(13)).foregroundStyle(theme.onRow()) }
                    .tint(theme.onBlocks ? .white.opacity(0.5) : theme.accent)
                note(L("Off: Fusebox only looks for devices on your network when you press Scan."))
                row(L("Speed test unit")) {
                    Picker("Unit", selection: $speedMB) { Text(verbatim: "Mbit/s").tag(false); Text(verbatim: "MB/s").tag(true) }
                        .pickerStyle(.segmented).frame(maxWidth: 170)
                }
                row(L("Measure to")) {
                    TextField(text: $pingHost, prompt: Text(verbatim: "1.1.1.1")) { Text("Measure to") }
                        .textInputAutocapitalization(.never).autocorrectionDisabled().keyboardType(.URL).submitLabel(.done)
                        .multilineTextAlignment(.trailing).font(theme.number(13)).foregroundStyle(theme.onRow()).frame(maxWidth: 170)
                }
                note(pingHost.isEmpty ? L("Empty: Fusebox makes no internet connections until you start a speed test or Find the problem.")
                     : L("The server Fusebox pings to measure your internet. Leave empty to turn the internet check off."))
            }
            section(L("About"), "info.circle", 3) {
                let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "–"
                row("Fusebox") { Text(verbatim: L("Version \(v)") + " beta").font(theme.number(13)).foregroundStyle(theme.onRow()) }
                note(L("The iPhone version of the Mac app. Processes, Checkup and most of Docked need access that iOS doesn't give apps, so they are not here."))
            }
        }
    }

    // MARK: Bouwstenen

    private func section<C: View>(_ title: String, _ icon: String, _ index: Int, @ViewBuilder content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeader(title: title, icon: icon)
            content()
        }
        .card(theme, index: index)
    }

    private func row<C: View>(_ title: String, @ViewBuilder control: () -> C) -> some View {
        HStack(spacing: 8) {
            Text(verbatim: title).font(theme.label(13)).foregroundStyle(theme.onRow())
            Spacer(minLength: 8)
            control()
        }
    }

    private func note(_ text: String) -> some View {
        Text(verbatim: text).font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
    }

    /// Themategel: achtergrond, twee tekstkleuren en het accent van het thema zelf; de naam blijft onvertaald.
    private func tile(_ t: Theme) -> some View {
        let on = t.id == themeID, bg = t.bg ?? Color(uiColor: .secondarySystemGroupedBackground)
        return Button { themeID = t.id } label: {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 4) {
                    ForEach(Array((t.blocks.isEmpty ? [t.accent, t.good, t.warn] : Array(t.blocks.prefix(3))).enumerated()), id: \.offset) { _, c in
                        Capsule().fill(c).frame(width: 18, height: 5)
                    }
                }
                Text(verbatim: t.name).font(.system(size: 12, weight: .medium, design: t.design)).foregroundStyle(t.text).lineLimit(1)
            }
            .padding(10).frame(width: 112, alignment: .leading)
            .background(bg, in: .rect(cornerRadius: 12))
            .overlay(RoundedRectangle(cornerRadius: 12).stroke(on ? theme.onRowAccent : theme.line, lineWidth: on ? 2 : 1))
        }
        .buttonStyle(.plain)
        .accessibilityAddTraits(on ? .isSelected : [])
    }
}
