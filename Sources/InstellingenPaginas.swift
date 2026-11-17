import SwiftUI

// MARK: - Instellingen per pagina: Processes, Checkup, Network, Docked.
// Elke keuze is een korte vaste id onder een sleutel uit `Prefs.defaults`; de pagina's lezen die zelf.
// Kolommen/onderdelen worden bewaard als lijst van wat UIT staat, zodat iets nieuws vanzelf aan staat.

/// Aan/uit-knopjes (chips) naast elkaar; loopt door op de volgende regel.
struct Chips: View {
    let items: [(title: String, on: Binding<Bool>)]
    var body: some View {
        NetFlow(spacing: 6) {
            ForEach(items.indices, id: \.self) { i in
                let on = items[i].on
                Button { on.wrappedValue.toggle() } label: {
                    Label(items[i].title, systemImage: on.wrappedValue ? "checkmark" : "plus")
                        .font(.callout).padding(.horizontal, 10).padding(.vertical, 4)
                        .foregroundStyle(on.wrappedValue ? Color.accentColor : .secondary)
                        .background(on.wrappedValue ? Color.accentColor.opacity(0.15) : Color.secondary.opacity(0.1), in: .capsule)
                        .contentShape(.capsule)
                }
                .buttonStyle(.plain)
                .accessibilityAddTraits(on.wrappedValue ? .isSelected : [])
            }
        }
    }
}

/// Aan = niet in de "uit"-lijst `raw`.
func shown(_ raw: Binding<String>, _ id: String) -> Binding<Bool> {
    let off = member(raw, id)
    return Binding(get: { !off.wrappedValue }, set: { off.wrappedValue = !$0 })
}

/// Voorbeeld van een tabelkop met één regel, in de stijl van het venster (zoals OptionPreview).
struct TablePreview: View {
    let name: String
    let columns: [(title: String, sample: String)]
    var sorted: String? = nil
    @AppStorage("theme") private var themeID = "instrument"
    @AppStorage("appearanceMode") private var mode: AppearanceMode = .system
    @AppStorage("font") private var font = "style"
    @Environment(\.colorScheme) private var system

    var body: some View {
        let _ = font
        let t = mode.apply(Theme.resolved(themeID), system: system)
        Grid(alignment: .trailing, horizontalSpacing: 10, verticalSpacing: 4) {
            GridRow {
                Text("Name" + (sorted == "Name" ? " ▾" : "")).foregroundStyle(sorted == "Name" ? t.text : t.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                ForEach(columns.indices, id: \.self) { i in
                    Text(columns[i].title + (columns[i].title == sorted ? " ▾" : ""))
                        .foregroundStyle(columns[i].title == sorted ? t.text : t.secondary)
                }
            }
            .font(t.label(10)).foregroundStyle(t.secondary)
            GridRow {
                Text(name).font(t.label(12)).frame(maxWidth: .infinity, alignment: .leading)
                ForEach(columns.indices, id: \.self) { i in Text(columns[i].sample) }
            }
            .font(t.number(12)).foregroundStyle(t.onRow())
        }
        .lineLimit(1).minimumScaleFactor(0.75)   // veel kolommen: liever iets kleiner dan afgekapt
        .themedRow(t)
        .padding(8)
        .background(t.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial), in: .rect(cornerRadius: 10))
        .environment(\.theme, t)
        .animation(.snappy, value: columns.map(\.title))
        .accessibilityHidden(true)
    }
}

private func caption(_ s: String) -> some View { Text(s).font(.caption).foregroundStyle(.secondary) }

// MARK: - Processes

struct ProcessesSettings: View {
    @AppStorage("procFilter") private var filter = "all"
    @AppStorage("procSort") private var sort = "cpu"
    @AppStorage("procHide") private var hide = ""
    @AppStorage("procSmall") private var small = "idle"
    @AppStorage("netPerApp") private var netPerApp = false

    /// id, titel in de kop, voorbeeldwaarde.
    private static let columns = [("disk", "Disk", "1.2 MB/s"), ("net", "Network", "↓ 80 KB/s"), ("energy", "Energy", "1.3 W"),
                                  ("gpu", "GPU", "4%"), ("threads", "Threads", "32"), ("running", "Running", "2 h")]

    var body: some View {
        Section("When you open Processes") {
            Picker("Show", selection: $filter) {
                Text("All").tag("all"); Text("Apps").tag("apps"); Text("Background").tag("background"); Text("macOS").tag("macos")
            }
            .pickerStyle(.segmented)
            Picker("Sort by", selection: $sort) {
                Text("CPU").tag("cpu"); Text("Memory").tag("ram"); Text("Energy").tag("energy"); Text("GPU").tag("gpu"); Text("Name").tag("name")
            }
        }
        Section {
            Chips(items: Self.columns.map { c in (c.1, c.0 == "net" ? $netPerApp : shown($hide, c.0)) })
            TablePreview(name: "Safari", columns: [("CPU", "12%"), ("RAM", "1.4 GB")]
                         + Self.columns.filter { $0.0 == "net" ? netPerApp : !member($hide, $0.0).wrappedValue }.map { ($0.1, $0.2) },
                         sorted: ["cpu": "CPU", "ram": "RAM", "energy": "Energy", "gpu": "GPU", "name": "Name"][sort])
        } header: {
            Text("Columns")
        } footer: {
            caption("Columns appear when the window is wide enough. Network: download and upload per app, read on this Mac only while the page is open.")
        }
        Section("Small items") {
            Picker("Hide", selection: $small) {
                Text("Nothing").tag("all"); Text("Idle").tag("idle"); Text("Under 1% CPU").tag("light")
            }
            .pickerStyle(.segmented)
            caption(small == "all" ? "Every process is listed."
                    : small == "idle" ? "Hides items using almost no CPU (< 0.05%) and under 20 MB memory. \"Show all\" brings them back."
                    : "Hides items under 1% CPU and under 200 MB memory. \"Show all\" brings them back.")
        }
    }
}

// MARK: - Checkup

struct CheckupSettings: View {
    @AppStorage("checkupStart") private var start = "last"
    @AppStorage("checkupHide") private var hide = ""
    @AppStorage("restartDays") private var restartDays = 14

    /// Zelfde ids als ReportSections.Part in Analyse.swift.
    private static let parts = [("score", "Health score"), ("tops", "Top apps"), ("runtime", "Running time"), ("battery", "Battery"),
                                ("busy", "Busy moments"), ("crashes", "Crashes"), ("tips", "Tips"), ("log", "Log")]

    var body: some View {
        Section("When you open Checkup") {
            Picker("Period", selection: $start) {
                Text("Last used").tag("last"); Text("Today").tag("today"); Text("Yesterday").tag("yesterday"); Text("7 days").tag("week")
            }
            .pickerStyle(.segmented)
        }
        Section {
            Chips(items: Self.parts.map { ($0.1, shown($hide, $0.0)) })
        } header: {
            Text("Sections")
        } footer: {
            caption("\"Why is my Mac slow?\" is always there. Exported PDFs always contain everything.")
        }
        Section("Restart reminder") {
            Picker("Suggest a restart after", selection: $restartDays) {
                Text("7 days").tag(7); Text("14 days").tag(14); Text("30 days").tag(30); Text("Never").tag(0)
            }
            caption(restartDays == 0 ? "Checkup never suggests a restart."
                    : "A tip in Checkup: \"Not restarted for \(restartDays) days. A restart clears memory and finishes updates.\"")
        }
    }
}

// MARK: - Network

struct NetworkSettings: View {
    @AppStorage("netAutoScan") private var autoScan = true
    @AppStorage("notifyNewDevice") private var notifyNew = false
    @AppStorage("netHide") private var hide = ""
    @AppStorage("speedMB") private var speedMB = false
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @AppStorage("settingsTab") private var tab: SettingsTab = .profile

    private static let columns = [("mac", "MAC", "B8:27:EB:01:02:03"), ("vendor", "Vendor", "Raspberry Pi"), ("kind", "Type", "Computer")]

    var body: some View {
        Section("Devices") {
            Toggle(isOn: $autoScan) {
                Text("Scan when you open Network")
                Text("Off: Fusebox only looks for devices on your network when you press Scan.")
            }
            Toggle(isOn: $notifyNew) {
                Text("Notify me about new devices")
                Text("While Network scans. Same as the bell on the page.")
            }
            .onChange(of: notifyNew) { _, on in if on { Notify.ask() } }
        }
        Section {
            Chips(items: Self.columns.map { ($0.1, shown($hide, $0.0)) })
            TablePreview(name: "Living Room", columns: [("IP", "192.168.1.5")]
                         + Self.columns.filter { !member($hide, $0.0).wrappedValue }.map { ($0.1, $0.2) } + [("ms", "3")])
        } header: {
            Text("Columns")
        } footer: {
            caption("Columns appear when the window is wide enough.")
        }
        Section("Speed test") {
            LabeledContent {
                Picker("Unit", selection: $speedMB) { Text("Mbit/s").tag(false); Text("MB/s").tag(true) }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
            } label: {
                Text("Unit")
                Text("Mbit/s shows 412 Mbit/s · MB/s shows 51.5 MB/s")
            }
        }
        Section("Internet check") {
            LabeledContent {
                Button("Change in Privacy") { tab = .data }
            } label: {
                Text("Ping")
                Text(pingHost.isEmpty ? "Off: Fusebox makes no internet connections" : "On, to \(pingHost)")
            }
        }
    }
}

// MARK: - Docked

struct DockedSettings: View {
    @AppStorage("dockedInternals") private var internals = false
    @AppStorage("dockedEmpty") private var showEmpty = true
    @AppStorage("dockedBytes") private var bytes = false
    @AppStorage("dockedDisplays") private var displays = true
    @AppStorage("dockedWireless") private var wireless = true

    var body: some View {
        Section("Ports") {
            Toggle(isOn: $internals) {
                Text("Show hubs & internals")
                Text("Also the hubs inside a dock and its Billboard part. Same as the button on the page.")
            }
            Toggle(isOn: $showEmpty) {
                Text("List empty ports")
                Text("One line at the bottom, like \"USB-C 2 · USB-C 3 · empty\".")
            }
            Toggle(isOn: $displays) {
                Text("Show displays")
                Text("External monitors under the port or DisplayLink dock that drives them, with resolution and refresh rate.")
            }
        }
        Section("Wireless") {
            Toggle(isOn: $wireless) {
                Text("Show wireless")
                Text("Bluetooth devices with battery, and a row when your internet runs through a phone hotspot or mobile modem.")
            }
        }
        Section("Speed") {
            LabeledContent {
                Picker("Link speed", selection: $bytes) { Text("Gbit/s").tag(false); Text("GB/s").tag(true) }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
            } label: {
                Text("Link speed")
                Text("Gbit/s shows 40 Gbit/s · GB/s shows 5 GB/s (handy to compare with a drive)")
            }
        }
    }
}
