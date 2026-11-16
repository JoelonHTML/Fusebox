import SwiftUI

// MARK: - Programma's: wat gebruikt er nu processor, geheugen en stroom

struct ProgramsView: View {
    @EnvironmentObject var meter: Meter
    var body: some View { ProgramsList(monitor: meter.ui, sample: meter.latest) }
}

private enum Filter: String, CaseIterable { case all = "All", apps = "Apps", background = "Background", macos = "macOS" }
private enum Column { case name, cpu, ram, disk, energy, threads, running }
/// Breedte-banden: < 460 Naam/CPU/RAM, + Disk, + Energie, + Threads/Proc. (bredere CPU-balk), + Running, ≥ 1100 lijst + detailpaneel.
private enum Width: Int, Comparable {
    case narrow, disk, energy, procs, running, split
    init(_ w: CGFloat) { self = w >= 1100 ? .split : w >= 820 ? .running : w >= 700 ? .procs : w >= 560 ? .energy : w >= 460 ? .disk : .narrow }
    static func < (a: Width, b: Width) -> Bool { a.rawValue < b.rawValue }
    var cpuW: CGFloat { self >= .procs ? 90 : 58 }
}

private struct ProgramsList: View {
    @ObservedObject var monitor: AppMonitor
    let sample: Sample
    @Environment(\.theme) private var theme
    @State private var filter = Filter.all
    @State private var search = ""
    @State private var sort = Column.cpu
    @State private var ascending = false
    @State private var showAll = false
    @State private var width = Width.energy
    @State private var confirm: AppUsage?
    @State private var failed: String?
    @State private var open: String?          // id van de uitgeklapte regel (maximaal één); in split-modus de geselecteerde

    static let ramW: CGFloat = 62, energyW: CGFloat = 50, procW: CGFloat = 40, stopW: CGFloat = 18
    static let diskW: CGFloat = 70, threadsW: CGFloat = 54, runW: CGFloat = 62

    var body: some View {
        let (rows, hidden) = visible()
        let tight = theme.rowStyle == .lines || theme.rowStyle == .plain
        let split = width == .split
        let selected = split ? (rows.first(where: { $0.id == open }) ?? rows.first) : nil
        HStack(alignment: .top, spacing: 12) {
            VStack(spacing: 8) {
                Text("CPU \(pct(sample.cpu)) · RAM \(one(sample.ramUsedGB)) GB · \(monitor.apps.count) items")
                    .font(theme.label(11)).foregroundStyle(theme.secondary)
                    .frame(maxWidth: .infinity, alignment: .leading)
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 8) { filters; searchField.frame(minWidth: 110) }
                    VStack(alignment: .leading, spacing: 6) { filters; searchField }
                }
                columnHeader.padding(.horizontal, tight ? 0 : 10)
                ScrollView {
                    LazyVStack(spacing: tight ? 0 : 6) {
                        ForEach(Array(rows.enumerated()), id: \.element.id) { i, app in
                            ProgramRow(app: app, width: width, index: i, isOpen: !split && open == app.id, isSelected: selected?.id == app.id,
                                       onTap: {
                                           if split { open = app.id } else { withAnimation(.snappy(duration: 0.25)) { open = open == app.id ? nil : app.id } }
                                       },
                                       onStop: { confirm = app })
                        }
                        if rows.isEmpty {
                            Text("Nothing found").font(theme.label(12)).foregroundStyle(theme.secondary).padding(.vertical, 20)
                        }
                        if hidden > 0 || showAll {
                            Button(showAll ? "Hide small items" : "Show all (\(hidden))") { showAll.toggle() }
                                .buttonStyle(.plain).font(theme.label(11)).foregroundStyle(theme.accent).padding(.vertical, 8)
                        }
                    }
                }
            }
            if let selected { detailPane(selected).frame(width: 320) }
        }
        .padding(.horizontal, 12).padding(.bottom, 10)
        .onGeometryChange(for: Width.self) { Width($0.size.width) } action: { width = $0 }
        .alert("Stop \(confirm?.name ?? "")?", isPresented: Binding(get: { confirm != nil }, set: { if !$0 { confirm = nil } }), presenting: confirm) { app in
            Button("Stop", role: .destructive) { if !monitor.stop(app) { failed = app.name } }
            Button("Cancel", role: .cancel) {}
        } message: { app in
            Text("Unsaved work in \(app.name) may be lost.")
        }
        .alert("\(failed ?? "") couldn't be stopped", isPresented: Binding(get: { failed != nil }, set: { if !$0 { failed = nil } })) {
            Button("OK", role: .cancel) {}
        } message: { Text("macOS doesn't allow it, or the app isn't responding.") }
    }

    /// Filteren + sorteren (~300 regels, goedkoop). Geeft ook het aantal verborgen kleine onderdelen.
    private func visible() -> ([AppUsage], Int) {
        let q = search.trimmingCharacters(in: .whitespaces)
        var list = monitor.apps.filter { a in
            switch filter {
            case .all: true
            case .apps: a.kind == .app
            case .background: a.kind == .background
            case .macos: a.kind == .macos
            }
        }
        if !q.isEmpty { list = list.filter { $0.name.localizedCaseInsensitiveContains(q) || $0.explanation.localizedCaseInsensitiveContains(q) } }
        var hidden = 0
        if !showAll && q.isEmpty { // bij zoeken altijd alles tonen
            let before = list.count
            list = list.filter { $0.cpu >= 0.05 || $0.memGB >= 20.0 / 1024 }
            hidden = before - list.count
        }
        if !(sort == .cpu && !ascending) { // monitor levert al CPU aflopend
            list.sort { a, b in
                let less: Bool = switch sort {
                case .name: a.name.localizedStandardCompare(b.name) == .orderedAscending
                case .cpu: a.cpu < b.cpu
                case .ram: a.memGB < b.memGB
                case .energy: a.energyW < b.energyW
                case .disk: a.diskRead + a.diskWrite < b.diskRead + b.diskWrite
                case .threads: a.threads < b.threads
                case .running: (a.started ?? .distantFuture) > (b.started ?? .distantFuture)   // langer actief = "meer"
                }
                return ascending ? less : !less
            }
        }
        return (list, hidden)
    }

    private var filters: some View {
        HStack(spacing: 2) {
            ForEach(Filter.allCases, id: \.self) { f in
                Button { filter = f } label: {
                    Text(f.rawValue).font(theme.label(11))
                        .foregroundStyle(filter == f ? theme.accent : theme.secondary)
                        .padding(.horizontal, 7).padding(.vertical, 3)
                        .background(filter == f ? theme.accent.opacity(0.15) : .clear, in: .rect(cornerRadius: 6))
                        .contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
        }
        .fixedSize()
    }

    private var searchField: some View {
        HStack(spacing: 4) {
            Image(systemName: "magnifyingglass").foregroundStyle(theme.secondary)
            TextField("Search", text: $search).textFieldStyle(.plain).foregroundStyle(theme.text)
            if !search.isEmpty {
                Button { search = "" } label: { Image(systemName: "xmark.circle.fill").foregroundStyle(theme.secondary) }
                    .buttonStyle(.plain).help("Clear search")
            }
        }
        .font(theme.label(12))
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(theme.line.opacity(0.5), in: .rect(cornerRadius: 6))
    }

    /// Split-modus: de uitleg van de geselecteerde regel rechts (zelfde inhoud als uitklappen).
    private func detailPane(_ app: AppUsage) -> some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 8) {
                    Group { if let icon = app.icon { Image(nsImage: icon).resizable().interpolation(.high) } else { Color.clear } }
                        .frame(width: 28, height: 28)
                    Text(app.name).font(theme.label(14)).foregroundStyle(theme.onRow()).lineLimit(2)
                }
                ProgramDetails(app: app, onStop: { confirm = app })
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .themedRow(theme, index: 0)
        }
        .scrollIndicators(.never)
    }

    private var columnHeader: some View {
        HStack(spacing: 8) {
            head("Name", .name).frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 26)
            head("CPU", .cpu).frame(width: width.cpuW, alignment: .trailing)
            head("RAM", .ram).frame(width: Self.ramW, alignment: .trailing)
            if width >= .disk { head("Disk", .disk).frame(width: Self.diskW, alignment: .trailing).help("Reading + writing to the SSD, per second") }
            if width >= .energy { head("Energy", .energy).frame(width: Self.energyW, alignment: .trailing) }
            if width >= .procs { head("Threads", .threads).frame(width: Self.threadsW, alignment: .trailing) }
            if width >= .procs { Text("Proc.").font(theme.label(10)).foregroundStyle(theme.secondary).frame(width: Self.procW, alignment: .trailing) }
            if width >= .running { head("Running", .running).frame(width: Self.runW, alignment: .trailing) }
            Color.clear.frame(width: Self.stopW, height: 1)
        }
    }

    private func head(_ title: String, _ col: Column) -> some View {
        Button {
            if sort == col { ascending.toggle() } else { sort = col; ascending = col == .name }
        } label: {
            HStack(spacing: 2) {
                Text(title)
                if sort == col { Image(systemName: ascending ? "chevron.up" : "chevron.down").font(.system(size: 8, weight: .bold)) }
            }
            .font(theme.label(10)).foregroundStyle(sort == col ? theme.text : theme.secondary)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
        .help("Sort by \(title == "CPU" || title == "RAM" ? title : title.lowercased())")
    }
}

private struct ProgramRow: View {
    let app: AppUsage, width: Width, index: Int, isOpen: Bool, isSelected: Bool, onTap: () -> Void, onStop: () -> Void
    @Environment(\.theme) private var theme
    @State private var hover = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            summary.contentShape(.rect).onTapGesture(perform: onTap)
            if isOpen { ProgramDetails(app: app, onStop: onStop).transition(.opacity.combined(with: .move(edge: .top))) }
        }
        .font(theme.number(12)).foregroundStyle(theme.onRow())
        .onHover { hover = $0 }
        .background(isSelected ? theme.accent.opacity(0.12) : .clear, in: .rect(cornerRadius: 6))
        .themedRow(theme, index: index)
        .clipped()
    }

    private var summary: some View {
        HStack(spacing: 8) {
            Group {
                if let icon = app.icon { Image(nsImage: icon).resizable().interpolation(.high) } else { Color.clear }
            }
            .frame(width: 18, height: 18)
            VStack(alignment: .leading, spacing: 1) {
                Text(app.name).font(theme.label(12)).foregroundStyle(theme.onRow())
                if !app.explanation.isEmpty {
                    Text(app.explanation).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                }
            }
            .lineLimit(1).truncationMode(.tail)
            .frame(maxWidth: .infinity, alignment: .leading)
            VStack(alignment: .trailing, spacing: 3) {
                Text(pct(app.cpu))
                Capsule().fill(barColor.opacity(0.2)).frame(height: 3)
                    .overlay(alignment: .leading) {
                        GeometryReader { g in Capsule().fill(barColor).frame(width: g.size.width * min(app.cpu / 100, 1)) }
                    }
            }
            .frame(width: width.cpuW)
            Text(mem(app.memGB)).frame(width: ProgramsList.ramW, alignment: .trailing)
            if width >= .disk {
                let io = app.diskRead + app.diskWrite
                Text(io >= 1000 ? rate(io) : "–").lineLimit(1).minimumScaleFactor(0.8).frame(width: ProgramsList.diskW, alignment: .trailing)
            }
            if width >= .energy { Text(app.energyW >= 0.05 ? "\(one(app.energyW)) W" : "–").frame(width: ProgramsList.energyW, alignment: .trailing) }
            if width >= .procs { Text(app.threads > 0 ? "\(app.threads)" : "–").frame(width: ProgramsList.threadsW, alignment: .trailing) }
            if width >= .procs { Text(app.pids.isEmpty ? "–" : "\(app.pids.count)").frame(width: ProgramsList.procW, alignment: .trailing) }
            if width >= .running {
                Text(app.started.map { duration(max(Int(Date().timeIntervalSince($0) / 60), 1)) } ?? "–")
                    .lineLimit(1).frame(width: ProgramsList.runW, alignment: .trailing)
            }
            Group {
                if app.canStop {
                    Button(action: onStop) { Image(systemName: "xmark.circle").font(.system(size: 13)) }
                        .buttonStyle(.plain)
                        .foregroundStyle(hover ? (theme.rowStyle == .blocks ? .white : theme.danger) : theme.onRow(true))
                        .opacity(hover ? 1 : 0.35)
                        .help("Stop \(app.name)")
                        .accessibilityLabel("Stop \(app.name)")
                } else { Color.clear }
            }
            .frame(width: ProgramsList.stopW)
        }
    }

    private var barColor: Color { theme.rowStyle == .blocks ? .white : app.cpu >= 50 ? theme.warn : theme.accent }
}

func mem(_ gb: Double) -> String { gb >= 1 ? "\(one(gb)) GB" : "\(Int((gb * 1024).rounded())) MB" }

/// Uitgeklapte uitleg in gewone taal. Alleen opgebouwd voor de ene open regel, dus proc_name hier is goedkoop.
private struct ProgramDetails: View {
    let app: AppUsage, onStop: () -> Void
    @Environment(\.theme) private var theme
    private static let cores = Double(ProcessInfo.processInfo.activeProcessorCount)
    private static let ramGB = Double(ProcessInfo.processInfo.physicalMemory) / 1_073_741_824

    var body: some View {
        VStack(alignment: .leading, spacing: 5) {
            if !app.explanation.isEmpty { line("questionmark.circle", "What is this? \(app.explanation).") }
            line("cpu", "\(app.name) is using \(pct(app.cpu)) of your processor right now (≈ \(one(app.cpu / 100 * Self.cores)) of the \(Int(Self.cores)) cores) and \(mem(app.memGB)) of memory (\(pct(app.memGB / Self.ramGB * 100)) of your \(Int(Self.ramGB.rounded())) GB).")
            if app.kind == .macos && app.pids.isEmpty {
                line("lock", "macOS doesn't let apps see exactly which system parts these are. That's normal: this covers everything only macOS itself is allowed to measure.")
            } else if !app.pids.isEmpty {
                line("square.stack.3d.up", parts)
            }
            if app.energyW >= 0.05 { line("bolt", "Uses about \(one(app.energyW)) W — \(energyWord).") }
            if app.diskRead + app.diskWrite >= 1000 {
                line("internaldrive", "Reading \(rate(app.diskRead)) and writing \(rate(app.diskWrite)) on the SSD.")
            }
            if app.threads > 0 { line("arrow.triangle.branch", "Runs \(app.threads) threads (tasks at the same time).") }
            if let s = app.started { line("clock", "Running for \(duration(max(Int(Date().timeIntervalSince(s) / 60), 1))).") }
            line(verdict.icon, verdict.text).foregroundStyle(verdict.color)
            HStack(spacing: 12) {
                if app.id.hasPrefix("/") {
                    Button { NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: app.id)]) } label: {
                        Label("Show in Finder", systemImage: "folder")
                    }
                }
                if app.canStop {
                    Button(action: onStop) { Label("Stop \(app.name)", systemImage: "xmark.circle") }
                        .foregroundStyle(theme.rowStyle == .blocks ? .white : theme.danger)
                }
            }
            .buttonStyle(.plain).font(theme.label(11)).padding(.top, 2)
        }
        .font(theme.label(11)).foregroundStyle(theme.onRow(true))
        .padding(.leading, 26).padding(.top, 6).padding(.bottom, 2)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func line(_ icon: String, _ text: String) -> some View {
        Label { Text(text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon).frame(width: 14) }
    }

    /// Aantal processen + tot drie namen. ponytail: geen CPU per proces (AppUsage telt ze samen op); voeg per-pid cpu toe aan AppUsage als dat nodig is.
    private var parts: String {
        let n = app.pids.count
        guard n > 1 else { return "It's a single process." }
        var seen = Set<String>(), names: [String] = []
        for pid in app.pids where names.count < 3 {
            var buf = [CChar](repeating: 0, count: 256)
            if proc_name(pid, &buf, UInt32(buf.count)) > 0 {
                let name = String(cString: buf)
                if seen.insert(name).inserted { names.append(name) }
            }
        }
        return "It consists of \(n) parts (processes)" + (names.isEmpty ? "." : ", including \(names.joined(separator: ", ")).")
    }

    // Drempels energie: < 1 W licht, < 5 W gemiddeld, daarboven zwaar (een Mac in rust zit rond 2–5 W totaal).
    private var energyWord: String { app.energyW < 1 ? "light" : app.energyW < 5 ? "moderate" : "heavy" }

    // Oordeel: geheugen ≥ 2 GB of ≥ 15% van het RAM = veel; achtergrond/macOS ≥ 10% CPU = zwaar; ≥ 50% CPU = zwaar voor iedereen.
    private var verdict: (text: String, icon: String, color: Color) {
        let warn = theme.rowStyle == .blocks ? Color.white : theme.warn
        let ok = theme.rowStyle == .blocks ? Color.white : theme.good
        if app.cpu >= 50 { return ("Heavy: uses a lot of processor", "exclamationmark.triangle", warn) }
        if app.kind != .app && app.cpu >= 10 { return ("Heavy for a background task", "exclamationmark.triangle", warn) }
        if app.memGB >= 2 || app.memGB / Self.ramGB >= 0.15 { return ("Uses a lot of memory", "memorychip", warn) }
        return ("Normal", "checkmark.circle", ok)
    }
}
