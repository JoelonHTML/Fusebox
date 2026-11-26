import SwiftUI
import Network
#if os(macOS)
import CoreWLAN
import SystemConfiguration
#endif
import Darwin

// MARK: - Netwerk: welke apparaten zitten er op je netwerk, hoe heten ze, en hoe goed is je internet
//
// Scant alleen als dit tabblad open is of na "Scan"; niets in de 2-s-tik. Op het lokale netwerk gaan alleen kleine
// vragen rond (ping, ARP, Bonjour/mDNS, NetBIOS, SSDP/UPnP, poortcheck op verzoek). Internet: zie NetwerkInternet.swift.

#if os(macOS)   // Mac-scherm; de iPhone-app (ios/) deelt het model en de scanner verderop
struct NetworkView: View {
    @StateObject private var scanner = NetScanner()
    @Environment(\.theme) private var theme
    @AppStorage("notifyNewDevice") private var notifyNew = false
    @AppStorage("netAutoScan") private var autoScan = true
    @AppStorage("netHide") private var hide = ""   // kolommen die uit staan: mac, vendor, kind
    @State private var sort = NetColumn.ip
    @State private var ascending = true
    @State private var width = NetWidth.kind
    @State private var stacked = false   // smal venster: kaarten onder elkaar
    @State private var open: String?
    @State private var filter = NetFilter.all
    @State private var renaming: NetDevice?
    @State private var newName = ""
    @State private var search = ""
    @State private var rangeText: String?          // nil = dit netwerk (auto); geldt zolang de pagina open is
    @State private var rangeOpen = false
    @State private var draft = ""
    @AppStorage("netRecentRanges") private var recentRaw = ""   // laatste 5, één per regel

    static let ipW: CGFloat = 104, macW: CGFloat = 124, vendorW: CGFloat = 118, kindW: CGFloat = 104, msW: CGFloat = 42

    var body: some View {
        let all = scanner.devices
        let rows = sorted(all.filter { filter.matches($0) && $0.matches(search) })
        let tight = theme.rowStyle == .lines || theme.rowStyle == .plain
        // Eén scrollvlak: kaarten bovenaan, daaronder de apparaten; hun kop (werkbalk, kolommen) blijft bovenaan staan
        ScrollView {
            VStack(spacing: 14) {
                InternetCards(stacked: stacked)
                LazyVStack(spacing: tight ? 0 : 6, pinnedViews: [.sectionHeaders]) {
                    Section {
                        ForEach(Array(rows.enumerated()), id: \.element.id) { i, d in
                            NetRow(device: d, width: width, index: i + 1, isOpen: open == d.id, scanner: scanner) {
                                withAnimation(.snappy(duration: 0.25)) { open = open == d.id ? nil : d.id }
                            } onRename: {
                                newName = d.custom ?? d.name
                                renaming = d
                            }
                            .saturation(scanner.stale ? 0 : 1).opacity(scanner.stale ? 0.3 : 1)
                        }
                        if rows.isEmpty {
                            Text(scanner.info == nil ? (autoScan ? "Not connected to a network" : "Press Scan to look for devices") : !search.trimmingCharacters(in: .whitespaces).isEmpty ? "Nothing matches “\(search)”"
                                 : scanner.scanStart != nil ? "Looking for devices…"
                                 : filter != .all ? "No \(filter.title.lowercased()) devices" : rangeText.map { "Nothing answered in \($0)" } ?? "No devices found")
                                .font(theme.label(12)).foregroundStyle(theme.secondary).padding(.vertical, 20)
                        }
                    } header: {
                        VStack(alignment: .leading, spacing: 8) {
                            HStack(alignment: .firstTextBaseline, spacing: 8) {
                                Text("Devices").font(theme.label(13)).foregroundStyle(theme.text)
                                Text(countText(all)).font(theme.label(11)).foregroundStyle(theme.secondary).lineLimit(1)
                            }
                            toolbar
                            if scanner.localBlocked { blockedHint }
                            if scanner.stale {
                                Label("Found on the previous network. Press Scan to look on this one.", systemImage: "clock.arrow.circlepath")
                                    .font(theme.label(11)).foregroundStyle(theme.secondary)
                            }
                            columnHeader.padding(.horizontal, tight ? 0 : 10)
                        }
                        .padding(.top, 4).padding(.bottom, 6)
                        // Vaste kop boven de rijen: effen achtergrond, tot aan de vensterranden
                        .background { Rectangle().fill(theme.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.thinMaterial)).padding(.horizontal, -14) }
                    }
                }
            }
            .padding(.leading, 12).padding(.trailing, 14).padding(.bottom, 10)
        }
        .clipped()   // niet onder de doorzichtige werkbalk doorschuiven (anders staan de rijen achter de tabknoppen)
        .onGeometryChange(for: CGFloat.self) { $0.size.width } action: { stacked = $0 < 700 }
        .onGeometryChange(for: NetWidth.self) { NetWidth($0.size.width - 24) } action: { width = $0 }
        .onAppear { scanner.watch(); if autoScan { scanner.scan() } }
        .onDisappear { scanner.stop(); scanner.unwatch() }
        .alert("Rename device", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField("Name", text: $newName)
            Button("Save") { if let d = renaming { scanner.rename(d, to: newName) }; renaming = nil }
            Button("Cancel", role: .cancel) { renaming = nil }
        } message: {
            Text("Leave empty to use the name Fusebox finds itself.")
        }
    }

    private var toolbar: some View {
        HStack(spacing: 8) {
            // Smal venster (13-inch, kleine helft): filter als compact menu, anders past de werkbalk niet (scanknop viel weg)
            if width >= .kind {
                Picker("", selection: $filter) { ForEach(NetFilter.allCases) { Text($0.title).tag($0) } }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
            } else {
                Picker("", selection: $filter) { ForEach(NetFilter.allCases) { Text($0.title).tag($0) } }
                    .pickerStyle(.menu).labelsHidden().fixedSize()
            }
            searchField.frame(minWidth: 70, maxWidth: 170)
            Spacer(minLength: 4)
            if let start = scanner.scanStart {
                if width >= .kind { Text(scanner.phase).font(theme.label(10)).foregroundStyle(theme.secondary).lineLimit(1) }
                ProgressView(timerInterval: start...start.addingTimeInterval(scanner.expected), countsDown: false,
                             label: { EmptyView() }, currentValueLabel: { EmptyView() })
                    .progressViewStyle(.linear).frame(width: width >= .kind ? 70 : 36)
            }
            Button { draft = rangeText ?? ""; rangeOpen.toggle() } label: {
                if width >= .kind { NetPill(title: rangeText ?? L("This network"), icon: "scope", onPage: true) }
                else { NetPill(title: L("Range"), icon: "scope", onPage: true).labelStyle(.iconOnly) }
            }
            .buttonStyle(.plain)
            .help("Scan range: \(rangeText ?? "this network (found automatically)"). Click to type a range.")
            .popover(isPresented: $rangeOpen, arrowEdge: .bottom) { rangeEditor }
            Button {
                notifyNew.toggle()
                if notifyNew { Notify.ask() }
            } label: {
                Image(systemName: notifyNew ? "bell.fill" : "bell.slash").font(.system(size: 12))
                    .foregroundStyle(notifyNew ? theme.accent : theme.secondary).frame(width: 22, height: 20).contentShape(.rect)
            }
            .buttonStyle(.plain)
            .help(notifyNew ? "Notifying you when a new device shows up (while Fusebox scans). Click to turn off."
                  : "Get a notification when a new device shows up on your network (while this page scans)")
            Button { if scanner.scanStart == nil { scanner.scan() } else { scanner.stop() } } label: {
                NetPill(title: scanner.scanStart == nil ? L("Scan") : L("Stop"), icon: scanner.scanStart == nil ? "arrow.clockwise" : "stop.fill", onPage: true)
            }
            .buttonStyle(.plain).help(scanner.scanStart == nil ? "Look for devices in the scan range" : "Stop scanning (keeps what was found so far)")
        }
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
        .help("Filter on name, IP, MAC address or maker")
    }

    // MARK: Scanbereik

    private var recent: [String] { recentRaw.split(separator: "\n").map(String.init) }

    private var rangeEditor: some View {
        let typed = !draft.trimmingCharacters(in: .whitespaces).isEmpty
        let parsed = NetScanner.parseRange(draft)
        let ok = typed && (try? parsed.get()) != nil
        return VStack(alignment: .leading, spacing: 8) {
            Text("Scan range").font(theme.label(13))
            Button { apply(nil) } label: {
                Label {
                    VStack(alignment: .leading, spacing: 1) {
                        Text("This network (auto)")
                        if let h = scanner.info?.hosts, let a = h.first, let b = h.last {
                            Text("\(ipString(a)) – \(ipString(b))").font(theme.label(10)).foregroundStyle(.secondary)
                        }
                    }
                } icon: { Image(systemName: rangeText == nil ? "checkmark.circle.fill" : "circle") }
                .contentShape(.rect)
            }
            .buttonStyle(.plain)
            HStack(spacing: 6) {
                TextField("192.168.1.0/24 or 192.168.1.10-80", text: $draft)
                    .textFieldStyle(.roundedBorder)
                    .onSubmit { if ok { apply(draft) } }
                Button("Scan") { apply(draft) }.disabled(!ok)
            }
            Group {
                switch parsed {
                case _ where !typed:
                    Text("A block (192.168.1.0/24), a range (192.168.1.10-192.168.1.80 or 192.168.1.10-80) or one address. Up to \(NetScanner.maxRange) addresses.")
                        .foregroundStyle(.secondary)
                case .failure(let e): Text(e.message).foregroundStyle(theme.danger)
                case .success(let r): Text(rangeNote(r)).foregroundStyle(.secondary)
                }
            }
            .font(theme.label(10)).fixedSize(horizontal: false, vertical: true)
            if !recent.isEmpty {
                Text("Recent").font(theme.label(10)).foregroundStyle(.secondary).padding(.top, 2)
                ForEach(recent, id: \.self) { r in
                    Button { apply(r) } label: {
                        Label(r, systemImage: rangeText == r ? "checkmark.circle.fill" : "clock.arrow.circlepath").contentShape(.rect)
                    }
                    .buttonStyle(.plain)
                }
            }
        }
        .font(theme.label(12))
        .padding(12).frame(width: 320)
    }

    private func rangeNote(_ r: ClosedRange<UInt32>) -> String {
        let n = r.count == 1 ? L("1 address") : L("\(r.count) addresses")
        guard let info = scanner.info, !(info.isLocal(r.lowerBound) && info.isLocal(r.upperBound)) else { return n }
        return n + L(". Outside your own network: Fusebox can find devices and their names, but not hardware addresses or makers (routers don't pass those on).")
    }

    /// nil = terug naar dit netwerk. Geldig bereik bovenaan de recente lijst; daarna meteen scannen.
    private func apply(_ text: String?) {
        rangeOpen = false
        let t = text?.trimmingCharacters(in: .whitespaces) ?? ""
        if case .success(let r) = NetScanner.parseRange(t), !t.isEmpty {
            rangeText = t; scanner.range = r
            recentRaw = ([t] + recent.filter { $0 != t }).prefix(5).joined(separator: "\n")
        } else {
            rangeText = nil; scanner.range = nil
        }
        scanner.scan()
    }

    private func countText(_ all: [NetDevice]) -> String {
        let on = all.filter(\.online).count, new = all.filter(\.isNew).count, off = all.count - on
        return L("\(on) online") + (new > 0 ? L(" · \(new) new") : "") + (off > 0 ? L(" · \(off) offline") : "")
    }

    private var blockedHint: some View {
        HStack(spacing: 6) {
            Image(systemName: "lock.shield")
            Text("macOS is blocking the scan. Allow Fusebox under Privacy & Security › Local Network, then press Scan.")
                .fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
            Button("Open") {
                if let u = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork") { NSWorkspace.shared.open(u) }
            }
            .buttonStyle(.plain).foregroundStyle(theme.accent)
        }
        .font(theme.label(11)).foregroundStyle(theme.warn)
    }

    // MARK: Kolommen

    private var columnHeader: some View {
        HStack(spacing: 8) {
            head(L("Name"), .name).frame(maxWidth: .infinity, alignment: .leading).padding(.leading, 26)
            head("IP", .ip).frame(width: Self.ipW, alignment: .leading)
            if width >= .mac && !hide.contains("mac") { head("MAC", .mac).frame(width: Self.macW, alignment: .leading).help("Hardware address") }
            if width >= .vendor && !hide.contains("vendor") { head(L("Vendor"), .vendor).frame(width: Self.vendorW, alignment: .leading) }
            if width >= .kind && !hide.contains("kind") { head(L("Type"), .kind).frame(width: Self.kindW, alignment: .leading) }
            head("ms", .ms).frame(width: Self.msW, alignment: .trailing).help("Reply time in milliseconds")
        }
    }

    private func head(_ title: String, _ col: NetColumn) -> some View {
        Button {
            if sort == col { ascending.toggle() } else { sort = col; ascending = true }
        } label: {
            HStack(spacing: 2) {
                Text(title)
                if sort == col { Image(systemName: ascending ? "chevron.up" : "chevron.down").font(.system(size: 8, weight: .bold)) }
            }
            .font(theme.label(10)).foregroundStyle(sort == col ? theme.text : theme.secondary)
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    /// Online eerst; daarbinnen op de gekozen kolom.
    private func sorted(_ list: [NetDevice]) -> [NetDevice] {
        list.sorted { a, b in
            if a.online != b.online { return a.online }
            let less: Bool = switch sort {
            case .name: a.name.localizedStandardCompare(b.name) == .orderedAscending
            case .ip: a.ip < b.ip
            case .mac: (a.mac.map(macString) ?? "~") < (b.mac.map(macString) ?? "~")
            case .vendor: a.vendor.localizedStandardCompare(b.vendor) == .orderedAscending
            case .kind: a.kind.title < b.kind.title
            case .ms: (a.ms ?? .infinity) < (b.ms ?? .infinity)
            }
            return ascending ? less : !less
        }
    }
}
#endif

private enum NetColumn { case name, ip, mac, vendor, kind, ms }
/// Breedte-banden: smal Naam/IP/ms, + Vendor, + Type, + MAC.
enum NetWidth: Int, Comparable {
    case narrow, vendor, kind, mac
    init(_ w: CGFloat) { self = w >= 760 ? .mac : w >= 600 ? .kind : w >= 470 ? .vendor : .narrow }
    static func < (a: NetWidth, b: NetWidth) -> Bool { a.rawValue < b.rawValue }
}

private enum NetFilter: String, CaseIterable, Identifiable {
    case all, online, new, offline
    var id: String { rawValue }
    var title: String {
        switch self { case .all: L("All"); case .online: L("Online"); case .new: L("New"); case .offline: L("Offline") }
    }
    func matches(_ d: NetDevice) -> Bool {
        switch self {
        case .all: true
        case .online: d.online
        case .new: d.isNew
        case .offline: !d.online
        }
    }
}

func msText(_ ms: Double?) -> String { ms.map { $0 < 1 ? "<1" : "\(Int($0.rounded()))" } ?? "–" }
func agoText(_ d: Date) -> String {
    let s = max(0, -d.timeIntervalSinceNow)
    return s < 3600 ? L("\(max(1, Int(s / 60))) min ago") : s < 86_400 ? L("\(Int(s / 3600)) h ago") : L("\(Int(s / 86_400)) d ago")
}

/// Knop-label in de stijl van de pagina (accentkleur, licht vlak).
struct NetPill: View {
    let title: String, icon: String
    var onPage = false   // op de pagina zelf (niet op een kleurblok): ook bij kleurblokken de accentkleur
    @Environment(\.theme) private var theme
    var body: some View {
        let c = theme.rowStyle == .blocks && !onPage ? Color.white : theme.accent
        Label(title, systemImage: icon)
            .font(theme.label(11)).foregroundStyle(c).lineLimit(1)
            .padding(.horizontal, 7).padding(.vertical, 3)
            .background(c.opacity(0.15), in: .rect(cornerRadius: 6))
            .contentShape(.rect)
    }
}

/// Eenvoudige regel-omloop voor chips.
struct NetFlow: Layout {
    var spacing: CGFloat = 4
    func sizeThatFits(proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) -> CGSize {
        arrange(proposal.width ?? .infinity, subviews).size
    }
    func placeSubviews(in b: CGRect, proposal: ProposedViewSize, subviews: Subviews, cache: inout ()) {
        for (s, p) in zip(subviews, arrange(b.width, subviews).points) { s.place(at: CGPoint(x: b.minX + p.x, y: b.minY + p.y), proposal: .unspecified) }
    }
    private func arrange(_ maxW: CGFloat, _ subviews: Subviews) -> (size: CGSize, points: [CGPoint]) {
        var x: CGFloat = 0, y: CGFloat = 0, rowH: CGFloat = 0, widest: CGFloat = 0, pts: [CGPoint] = []
        for s in subviews {
            let sz = s.sizeThatFits(.unspecified)
            if x > 0, x + sz.width > maxW { y += rowH + spacing; x = 0; rowH = 0 }
            pts.append(CGPoint(x: x, y: y))
            x += sz.width + spacing; rowH = max(rowH, sz.height); widest = max(widest, x - spacing)
        }
        return (CGSize(width: widest, height: y + rowH), pts)
    }
}

#if os(macOS)
private struct NetRow: View {
    let device: NetDevice, width: NetWidth, index: Int, isOpen: Bool
    let scanner: NetScanner
    let onTap: () -> Void, onRename: () -> Void
    @Environment(\.theme) private var theme
    @AppStorage("netHide") private var hide = ""
    @State private var feedback: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            summary.contentShape(.rect).onTapGesture(perform: onTap)
                .opacity(device.online ? 1 : 0.5)
            if isOpen { details.transition(.opacity.combined(with: .move(edge: .top))) }
        }
        .font(theme.number(12)).foregroundStyle(theme.onRow())
        .themedRow(theme, index: index)
        .clipped()
    }

    private var summary: some View {
        HStack(spacing: 8) {
            Image(systemName: device.kind.icon).font(.system(size: 12)).frame(width: 18).foregroundStyle(theme.onRow(true))
            HStack(spacing: 5) {
                Text(device.name).font(theme.label(12))
                if let tag = device.isMe ? L("This Mac") : device.isRouter ? L("Router") : nil {
                    Text(tag).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                }
                if device.isNew {
                    Text("New").font(theme.label(9)).padding(.horizontal, 4).padding(.vertical, 1)
                        .foregroundStyle(theme.rowStyle == .blocks ? .white : theme.accent)
                        .background((theme.rowStyle == .blocks ? Color.white : theme.accent).opacity(0.18), in: .capsule)
                }
                if !device.online, let l = device.lastSeen {
                    Text("last seen \(agoText(l))").font(theme.label(10)).foregroundStyle(theme.onRow(true))
                }
            }
            .lineLimit(1).truncationMode(.tail)
            .frame(maxWidth: .infinity, alignment: .leading)
            Text(device.ip == 0 ? "–" : ipString(device.ip)).frame(width: NetworkView.ipW, alignment: .leading)
            if width >= .mac && !hide.contains("mac") {
                Text(device.mac.map(macString) ?? "–").font(.system(size: 11 * theme.scale, design: .monospaced))
                    .lineLimit(1).frame(width: NetworkView.macW, alignment: .leading)
                    .help(device.remote && device.mac == nil ? NetDevice.remoteHelp : "")
            }
            if width >= .vendor && !hide.contains("vendor") {
                Text(device.vendorTitle).font(theme.label(11)).lineLimit(1).frame(width: NetworkView.vendorW, alignment: .leading)
                    .help(device.remote && device.mac == nil ? NetDevice.remoteHelp : "")
            }
            if width >= .kind && !hide.contains("kind") { Text(device.kind.title).font(theme.label(11)).lineLimit(1).frame(width: NetworkView.kindW, alignment: .leading) }
            Text(device.online ? msText(device.ms) : L("off"))
                .foregroundStyle((device.loss ?? 0) > 0 && theme.rowStyle != .blocks ? theme.warn : theme.onRow())
                .frame(width: NetworkView.msW, alignment: .trailing)
                .help((device.loss ?? 0) > 0 ? "Some pings got no answer" : device.viaTCP ? "Measured with a TCP connection (no ping answer)" : "")
        }
    }

    private var details: some View {
        VStack(alignment: .leading, spacing: 5) {
            if let first = device.names.first {
                line("tag", (first.source == "Your name" ? L("Name from you") : L("Name from \(Self.sourceTitle(first.source))"))
                     + (device.names.count > 1 ? L(" · also: ") + device.names.dropFirst().map { "\($0.name) (\(Self.sourceTitle($0.source)))" }.joined(separator: ", ") : ""))
            } else {
                line("tag", L("No name found; this is a guess from the maker"))
            }
            if device.ip != 0 { line("network", device.online ? L("IP address \(ipString(device.ip))") : L("Last IP address \(ipString(device.ip))")) }
            if device.remote, device.mac == nil {
                line("barcode", L("No hardware address or maker: this address is outside your own network, and routers don't pass those on"))
            } else {
                line("barcode", device.mac.map { L("Hardware address \(macString($0))") } ?? L("Hardware address unknown"))
                if let mac = device.mac, NetScanner.isPrivate(mac) {
                    line("lock", L("Private address: a phone or laptop that hides its real address (Wi-Fi privacy). The maker can't be seen."))
                } else {
                    line("building.2", device.vendor == "–" ? L("Made by an unknown maker") : L("Made by \(device.vendor)"))
                }
            }
            if let m = device.model { line("info.circle", L("Model: \(m)")) }
            line(device.kind.icon, L("Looks like: \(device.kind.title.lowercased())"))
            if !device.services.isEmpty {
                chips("dot.radiowaves.left.and.right", L("Offers"), Array(Set(device.services.map(NetScanner.serviceTitle))).sorted().map { ($0, nil) })
            }
            if let ports = device.ports {
                if ports.isEmpty { line("door.left.hand.closed", L("No common ports open")) }
                else { chips("door.left.hand.open", L("Open ports"), ports.map { ("\(NetProbe.portName($0)) \($0)", NetProbe.webURL(device.ip, $0)) }) }
            }
            if device.online, !device.isMe { line("timer", latencyText) }
            if let f = device.firstSeen { line("clock", L("First seen \(f.formatted(date: .abbreviated, time: .shortened))")) }
            if let l = device.lastSeen { line("clock.arrow.circlepath", L("Last seen \(l.formatted(date: .abbreviated, time: .shortened))")) }
            actions.padding(.top, 3)
        }
        .font(theme.label(11)).foregroundStyle(theme.onRow(true))
        .padding(.leading, 26).padding(.top, 6).padding(.bottom, 2)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private var latencyText: String {
        guard let ms = device.ms else { return L("Doesn't answer (many phones and TVs sleep or ignore pings; that's normal)") }
        if device.viaTCP { return L("Answers in \(msText(ms)) ms (via a TCP connection; it ignores pings)") }
        guard let loss = device.loss else { return L("Answers in \(msText(ms)) ms") }
        let lost = Int((loss * 3).rounded())
        return L("Answers in \(msText(ms)) ms · ") + (lost == 0 ? L("no packet loss (3 pings)") : L("\(lost) of 3 pings lost"))
    }

    private var actions: some View {
        HStack(spacing: 6) {
            if device.online, !device.isMe, device.ip != 0 {
                Button { scanner.checkPorts(device.ip) } label: {
                    NetPill(title: device.checkingPorts ? L("Checking…") : device.ports == nil ? L("Check ports") : L("Check again"), icon: "door.left.hand.open")
                }
                .buttonStyle(.plain).disabled(device.checkingPorts).help("Try \(NetProbe.ports.count) common ports (web, file sharing, printing, …)")
            }
            if let mac = device.mac, !device.isMe, !NetScanner.isPrivate(mac) {
                Button {
                    Task { feedback = await scanner.wake(mac) ? L("Wake-up packet sent") : L("Couldn't send the wake-up packet") }
                } label: { NetPill(title: L("Wake on LAN"), icon: "power") }
                .buttonStyle(.plain).help("Send a wake-up packet; works for computers and NAS that have Wake on LAN turned on")
            }
            if device.mac != nil {
                Button(action: onRename) { NetPill(title: L("Rename…"), icon: "pencil") }.buttonStyle(.plain)
                if !device.isMe {
                    Button { scanner.forget(device) } label: { NetPill(title: L("Forget"), icon: "trash") }
                        .buttonStyle(.plain).help("Remove from the remembered devices")
                }
            }
            if let feedback { Text(feedback).font(theme.label(10)) }
        }
    }

    private func chips(_ icon: String, _ title: String, _ items: [(String, URL?)]) -> some View {
        let c = theme.rowStyle == .blocks ? Color.white : theme.accent
        return Label {
            NetFlow {
                Text(title + ":")
                ForEach(items, id: \.0) { text, url in
                    HStack(spacing: 4) {
                        Text(text)
                        if let url {
                            Button("Open") { NSWorkspace.shared.open(url) }.buttonStyle(.plain).foregroundStyle(c).underline()
                                .help(url.absoluteString)
                        }
                    }
                    .font(theme.label(10)).foregroundStyle(theme.onRow())
                    .padding(.horizontal, 6).padding(.vertical, 2)
                    .background(c.opacity(0.15), in: .capsule)
                }
            }
        } icon: { Image(systemName: icon).frame(width: 14) }
    }

    /// Bron van een naam in beeld; de bron zelf blijft Engels (wordt vergeleken in remember()).
    static func sourceTitle(_ s: String) -> String {
        switch s { case "Remembered": L("Remembered"); case "This Mac": L("This Mac"); case "Your name": L("Your name"); default: s }
    }

    private func line(_ icon: String, _ text: String) -> some View {
        Label { Text(text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon).frame(width: 14) }
    }
}
#endif

// MARK: - Model

enum DeviceKind: String {
    case phone, computer, tv, speaker, printer, router, nas, smartHome, console, unknown
    var title: String {
        switch self {
        case .phone: L("Phone or tablet")
        case .computer: L("Computer")
        case .tv: L("TV / streaming")
        case .speaker: L("Speaker")
        case .printer: L("Printer")
        case .router: L("Router / Wi-Fi")
        case .nas: L("Storage (NAS)")
        case .smartHome: L("Smart home")
        case .console: L("Game console")
        case .unknown: L("Unknown")
        }
    }
    /// Voor namen als "Sonos speaker".
    var noun: String {
        switch self {
        case .phone: L("phone")
        case .computer: L("computer")
        case .tv: "TV"
        case .speaker: L("speaker")
        case .printer: L("printer")
        case .router: L("router")
        case .nas: "NAS"
        case .smartHome: L("smart device")
        case .console: L("console")
        case .unknown: L("device")
        }
    }
    var icon: String {
        switch self {
        case .phone: "iphone"
        case .computer: "laptopcomputer"
        case .tv: "tv"
        case .speaker: "hifispeaker"
        case .printer: "printer"
        case .router: "wifi.router"
        case .nas: "externaldrive.connected.to.line.below"
        case .smartHome: "lightbulb"
        case .console: "gamecontroller"
        case .unknown: "questionmark.circle"
        }
    }
}

struct NetDevice: Identifiable {
    let id: String
    var ip: UInt32                       // 0 = onbekend (offline zonder bekend adres)
    var mac: [UInt8]?
    var online = true
    var ms: Double?, loss: Double?       // loss 0…1 over 3 pings
    var viaTCP = false
    var isMe = false, isRouter = false
    var name = "", custom: String?
    var names: [(source: String, name: String)] = []   // alle gevonden namen, beste eerst
    var vendor = "–", model: String?
    var services: Set<String> = []
    var kind = DeviceKind.unknown
    var ports: [UInt16]?, checkingPorts = false
    var firstSeen: Date?, lastSeen: Date?
    var isNew = false
    var remote = false                   // buiten het eigen subnet: geen ARP, dus geen MAC/maker
    var key: String?                     // sleutel in de onthouden-lijst (NetScanner.storeKey); nil = wordt niet onthouden

    static var remoteHelp: String { L("Outside your own network: routers don't pass on hardware addresses, so the MAC address and maker can't be seen") }
    /// Voor in beeld: "Private address" blijft intern Engels (guess() en zoeken), alleen de weergave vertaald.
    var vendorTitle: String { vendor == "Private address" ? L("Private address") : vendor }

    /// Zoekveld: deel van naam (ook andere gevonden namen), IP, MAC, maker of model; hoofdletters maakt niet uit.
    func matches(_ query: String) -> Bool {
        let q = query.trimmingCharacters(in: .whitespaces)
        guard !q.isEmpty else { return true }
        let fields = names.map(\.name) + [name, ip == 0 ? "" : ipString(ip), mac.map(macString) ?? "", vendor, model ?? ""]
        return fields.contains { $0.localizedCaseInsensitiveContains(q) }
    }
}

/// Deze Mac op het netwerk: interface, adres, masker, router en (bij Wi-Fi) signaal.
struct NetInfo {
    let iface: String, ip: UInt32, mask: UInt32, mac: [UInt8]?, router: UInt32?
    var hosts: [UInt32] { NetScanner.hosts(ip: ip, mask: mask).filter { $0 != ip } }
    /// Zelfde subnet als deze Mac (alleen daar werken ARP, Bonjour, mDNS en SSDP).
    func isLocal(_ a: UInt32) -> Bool { a & mask == ip & mask }
    /// Herkent "hetzelfde netwerk" voor offline apparaten (thuis vs. kantoor).
    var networkKey: String { "\(ipString(ip & mask))/\(mask.nonzeroBitCount) via \(router.map(ipString) ?? "-")" }

    #if os(macOS)   // iPhone-versie: ios/Fusebox/Platform.swift
    static func current() -> NetInfo? {
        let global = SCDynamicStoreCopyValue(nil, "State:/Network/Global/IPv4" as CFString) as? [String: Any]
        guard var iface = global?["PrimaryInterface"] as? String else { return nil }
        var router = (global?["Router"] as? String).flatMap(parseIP)
        // ponytail: VPN als hoofdroute → terug naar en0 zonder router; precieze router per dienst opzoeken als dat vaak voorkomt.
        if ["utun", "ipsec", "ppp"].contains(where: iface.hasPrefix) { iface = "en0"; router = nil }
        var ip: UInt32?, mask: UInt32 = 0, mac: [UInt8]?
        var list: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&list) == 0 else { return nil }
        defer { freeifaddrs(list) }
        var p = list
        while let a = p?.pointee {
            defer { p = a.ifa_next }
            guard String(cString: a.ifa_name) == iface, let sa = a.ifa_addr else { continue }
            if sa.pointee.sa_family == UInt8(AF_INET), let m = a.ifa_netmask {
                ip = sa.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
                mask = m.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { UInt32(bigEndian: $0.pointee.sin_addr.s_addr) }
            } else if sa.pointee.sa_family == UInt8(AF_LINK) {
                mac = sa.withMemoryRebound(to: sockaddr_dl.self, capacity: 1) { dl in
                    let n = Int(dl.pointee.sdl_nlen), len = Int(dl.pointee.sdl_alen)
                    guard len == 6 else { return nil }
                    return UnsafeRawPointer(dl).withMemoryRebound(to: UInt8.self, capacity: 8 + n + len) { Array(UnsafeBufferPointer(start: $0 + 8 + n, count: len)) }
                }
            }
        }
        guard let ip, ip != 0 else { return nil }
        return NetInfo(iface: iface, ip: ip, mask: mask, mac: mac, router: router)
    }
    #endif
}

func ipString(_ ip: UInt32) -> String { "\(ip >> 24).\(ip >> 16 & 255).\(ip >> 8 & 255).\(ip & 255)" }
func parseIP(_ s: String) -> UInt32? {
    let p = s.split(separator: ".", omittingEmptySubsequences: false).map { UInt32($0) }
    guard p.count == 4, p.allSatisfy({ ($0 ?? 256) < 256 }) else { return nil }
    return p[0]! << 24 | p[1]! << 16 | p[2]! << 8 | p[3]!
}
func macString(_ m: [UInt8]) -> String { m.map { String(format: "%02X", $0) }.joined(separator: ":") }
func parseMAC(_ s: String) -> [UInt8]? {
    let b = s.split(separator: ":").compactMap { UInt8($0, radix: 16) }
    return b.count == 6 ? b : nil
}

// MARK: - Scanner

@MainActor
final class NetScanner: ObservableObject {
    /// Verwachte duur van de lopende scan (voor de voortgangsbalk).
    private(set) var expected: TimeInterval = 8
    /// Eigen bereik (uit `parseRange`); nil = het eigen subnet.
    var range: ClosedRange<UInt32>?
    private var targets: Set<UInt32> = []
    @Published private(set) var info: NetInfo?
    @Published private(set) var scanStart: Date?
    @Published private(set) var phase = ""
    @Published private(set) var localBlocked = false
    /// Deze Mac zit nu op een ander netwerk dan bij de laatste scan: de lijst is oud.
    @Published private(set) var stale = false
    private var path: NWPathMonitor?
    @Published private var found: [UInt32: Found] = [:]
    @Published private var arpDone = false
    @Published private var bonjour: [UInt32: Bonjour] = [:]
    @Published private var names: [UInt32: [String: String]] = [:]   // bron (NetBIOS, mDNS, DNS) → naam
    @Published private var upnp: [UInt32: NetProbe.UPnPInfo] = [:]
    @Published private var ports: [UInt32: [UInt16]] = [:]
    @Published private var checking: Set<UInt32> = []
    @Published private var ouiReady = false
    @Published private var store = NetDeviceStore.load()
    private var work: Task<Void, Never>?
    private var side: [Task<Void, Never>] = []
    private var browsers: [NWBrowser] = []
    private var resolving: [NWEndpoint: NWConnection] = [:]
    #if os(macOS)
    private lazy var computerName = SCDynamicStoreCopyComputerName(nil, nil) as String? ?? L("This Mac")
    #else
    private lazy var computerName = L("This iPhone")   // iOS geeft apps de toestelnaam niet meer
    #endif

    struct Found { var mac: [UInt8]?; var ms: Double?; var loss: Double?; var viaTCP = false }
    struct Bonjour { var name: String?; var types: Set<String> = []; var model: String? }

    var devices: [NetDevice] {
        guard let info else { return [] }
        _ = ouiReady   // opnieuw tekenen zodra de IEEE-lijst er is
        let subnet = targets, net = info.networkKey
        var ips = Set(found.keys)
        ips.formUnion(bonjour.keys.filter(subnet.contains))
        ips.formUnion(upnp.keys.filter(subnet.contains))
        if range?.contains(info.ip) ?? true, !ips.isEmpty || scanStart == nil { ips.insert(info.ip) }
        var out = ips.map { device($0, info) }
        // Onthouden apparaten die nu niet te zien zijn (alleen van dit netwerk, en alleen als we de ARP-tabel echt konden lezen)
        if range == nil, arpDone, !localBlocked {
            let seen = Set(out.compactMap(\.key))
            for (key, k) in store.devices where !seen.contains(key) && (k.net == nil || k.net == net) {
                var d = NetDevice(id: "off " + key, ip: k.ip.flatMap(parseIP) ?? 0)
                d.mac = parseMAC(key)
                d.key = key
                d.online = false
                let privateMAC = d.mac.map(Self.isPrivate) ?? false
                d.vendor = privateMAC ? "Private address" : d.mac.flatMap(Self.vendor) ?? "–"
                d.custom = k.custom
                d.names = [("Your name", k.custom), ("Remembered", k.name)].compactMap { s, n in n.map { (s, $0) } }
                d.kind = Self.guess(name: d.names.map(\.name).joined(separator: " "), vendor: d.vendor, services: [], model: nil,
                                    isRouter: false, isMe: false, privateMAC: privateMAC)
                d.name = d.names.first?.name ?? Self.fallbackName(vendor: d.vendor, kind: d.kind, isRouter: false, privateMAC: privateMAC)
                d.firstSeen = k.first; d.lastSeen = k.last
                out.append(d)
            }
        }
        return out
    }

    private func device(_ ip: UInt32, _ info: NetInfo) -> NetDevice {
        var d = NetDevice(id: ipString(ip), ip: ip)
        d.isMe = ip == info.ip
        d.isRouter = ip == info.router
        d.remote = !info.isLocal(ip)
        let f = found[ip], bj = bonjour[ip], up = upnp[ip]
        d.mac = d.isMe ? info.mac : f?.mac
        if !d.isMe { d.ms = f?.ms; d.loss = f?.loss; d.viaTCP = f?.viaTCP ?? false }
        d.services = bj?.types ?? []
        let privateMAC = d.mac.map(Self.isPrivate) ?? false
        d.vendor = privateMAC ? "Private address" : d.mac.flatMap(Self.vendor) ?? up?.manufacturer.map { String($0.prefix(40)) } ?? "–"
        d.model = Self.modelText(bonjour: bj?.model, upnp: up)
        d.key = Self.storeKey(mac: d.mac, ip: ip)
        let known = d.key.flatMap { store.devices[$0] }
        d.custom = known?.custom
        // Namen op volgorde van betrouwbaarheid; dubbele weg
        var cands: [(String, String?)] = [("Your name", known?.custom), ("This Mac", d.isMe ? computerName : nil), ("Bonjour", bj?.name)]
        cands += ["NetBIOS", "mDNS", "DNS"].map { ($0, names[ip]?[$0]) }
        cands += [("UPnP", up?.friendlyName), ("Remembered", known?.name)]
        var seen = Set<String>()
        d.names = cands.compactMap { s, n in n.flatMap { seen.insert($0.lowercased()).inserted ? (s, $0) : nil } }
        let text = (d.names.map(\.name) + [up?.modelName, up?.deviceType, up?.manufacturer].compactMap { $0 }).joined(separator: " ")
        d.kind = Self.guess(name: text, vendor: d.vendor, services: d.services, model: bj?.model, isRouter: d.isRouter, isMe: d.isMe, privateMAC: privateMAC)
        #if os(iOS)
        if d.isMe { d.kind = .phone }   // de iPhone-app zelf
        #endif
        d.name = d.names.first?.name ?? Self.fallbackName(vendor: d.vendor, kind: d.kind, isRouter: d.isRouter, privateMAC: privateMAC)
        d.ports = ports[ip]
        d.checkingPorts = checking.contains(ip)
        d.firstSeen = known?.first
        d.lastSeen = known?.last
        d.isNew = known.map { Calendar.current.isDateInToday($0.first) && $0.first > store.created.addingTimeInterval(60) } ?? false
        return d
    }

    /// Sleutel in de onthouden-lijst: het MAC-adres. iOS geeft apps geen MAC-adressen; daar het IP-adres. Namen zijn geen
    /// goede sleutel: per scan komt soms de Bonjour-naam binnen ("iPad (4)") en soms alleen de DNS-naam ("iPad-4").
    /// Een IP blijft thuis meestal lang hetzelfde; krijgt een apparaat een nieuw, dan lijkt het nieuw. nil = niet onthouden.
    nonisolated static func storeKey(mac: [UInt8]?, ip: UInt32) -> String? {
        if let mac { return macString(mac) }
        #if os(iOS)
        return ip == 0 ? nil : "ip " + ipString(ip)
        #else
        return nil
        #endif
    }

    nonisolated static func fallbackName(vendor: String, kind: DeviceKind, isRouter: Bool, privateMAC: Bool) -> String {
        if isRouter { return L("Router") }
        guard vendor != "–", !privateMAC else { return L("Unknown device") }
        let short = vendor.count <= 16 ? vendor : String(vendor.split(separator: " ").first ?? "")
        return "\(short) \(kind.noun)"
    }

    nonisolated static func modelText(bonjour: String?, upnp: NetProbe.UPnPInfo?) -> String? {
        var parts: [String] = []
        if let m = bonjour { parts.append(NetProbe.appleModel(m).map { "\($0) (\(m))" } ?? m) }
        if let m = upnp?.modelName, !parts.contains(where: { $0.localizedCaseInsensitiveContains(m) }) {
            parts.append(m + (upnp?.manufacturer.map { L(" by \($0)") } ?? ""))
        }
        return parts.isEmpty ? nil : parts.joined(separator: " · ")
    }

    // MARK: Scannen

    func scan() {
        stop()
        info = NetInfo.current()
        found = [:]; bonjour = [:]; names = [:]; upnp = [:]; ports = [:]; checking = []
        arpDone = false; localBlocked = false; stale = false
        guard let info else { return }
        let start = Date(), me = info.ip
        // Eigen bereik of het eigen subnet; ARP, Bonjour, mDNS en SSDP alleen voor het deel binnen het eigen subnet
        let hosts = range.map { Array($0).filter { $0 != me } } ?? info.hosts
        let subnet = Set(hosts), local = Set(hosts.filter(info.isLocal)), remote = hosts.count - local.count
        targets = subnet
        // Ping-tussenruimte (3 ms per adres) + TCP-zoektocht buiten het subnet (2 poorten, 64 tegelijk, ≤ 0,4 s)
        expected = range == nil ? 8 : max(8, 4 + Double(hosts.count) * 0.003 + Double(remote) / 32 * 0.4)
        scanStart = start
        phase = L("Pinging…")
        if !local.isEmpty { browse() }
        let ifindex = UInt16(truncatingIfNeeded: if_nametoindex(info.iface))
        work = Task { [weak self] in
            async let oui: Void = Self.offMain { NetProbe.loadOUI() }
            async let ssdp = Self.offMain { local.isEmpty ? [:] : NetProbe.ssdp(iface: me, wait: 3) }
            // 1. Iedereen pingen (vult ook de ARP-tabel), daarna ARP lezen
            let sweep = await Self.offMain { NetProbe.ping(hosts, rounds: 2, every: 1.0, linger: 0.85) }
            await oui
            guard !Task.isCancelled, let self else { return }
            self.ouiReady = true
            let arp = await Self.offMain { Self.arpTable() }.filter { $0.ifindex == ifindex && local.contains($0.ip) }   // iOS: altijd leeg
            var f: [UInt32: Found] = [:]
            for e in arp { f[e.ip] = Found(mac: e.mac) }
            for (ip, r) in sweep.rtt { f[ip, default: Found()].ms = r.min() }
            for ip in self.bonjour.keys where subnet.contains(ip) && f[ip] == nil { f[ip] = Found() }
            self.found = f
            self.arpDone = true
            // Lokale antwoorden maar lege ARP-tabel: macOS verbergt die zonder Lokaal netwerk-toestemming
            #if os(macOS)
            self.localBlocked = self.localBlocked || sweep.blocked || (arp.isEmpty && sweep.rtt.keys.contains(where: local.contains))
            #else
            self.localBlocked = self.localBlocked || sweep.blocked
            #endif
            // 1b. Buiten het subnet geen ARP: wie niet op ping antwoordt, zoeken met een TCP-verbinding (SYN-ACK of RST = er is iemand)
            let quiet = hosts.filter { !local.contains($0) && f[$0] == nil }
            if !quiet.isEmpty {
                self.phase = L("Trying quiet addresses…")
                let knock = quiet.flatMap { ip in NetProbe.discoveryPorts.map { (ip: ip, port: $0) } }
                let r = await Self.offMain { NetProbe.tcpProbe(knock, timeout: 0.4, batch: 64) }
                guard !Task.isCancelled else { return }
                for (i, a) in r where a.ms < self.found[knock[i].ip]?.ms ?? .infinity { self.found[knock[i].ip] = Found(ms: a.ms, viaTCP: true) }
            }
            // 2. Namen (mDNS, NetBIOS, DNS) en reactietijd (3 pings per apparaat) tegelijk; mDNS alleen binnen het subnet
            self.phase = L("Looking up names…")
            let alive = Array(self.found.keys).filter { $0 != me }, near = alive.filter(local.contains)
            async let mdns = Self.offMain { near.isEmpty ? [:] : NetProbe.mdnsReverse(near, iface: me, wait: 1.5) }
            async let nb = Self.offMain { NetProbe.netbios(alive, wait: 1.5) }
            async let dns = Self.reverseNames(alive, timeout: 1.5)
            let lat = await Self.offMain { NetProbe.ping(alive, rounds: 3, every: 0.3, linger: 1.0) }
            guard !Task.isCancelled else { return }
            for (ip, r) in lat.rtt where !r.isEmpty {
                self.found[ip]?.ms = r.reduce(0, +) / Double(r.count)
                self.found[ip]?.loss = Double(3 - min(r.count, 3)) / 3
            }
            // Geen ping-antwoord: reactietijd via een TCP-verbinding (SYN-ACK of RST telt allebei)
            let silent = alive.filter { lat.rtt[$0] == nil && self.found[$0]?.viaTCP != true }
            let targets = silent.flatMap { ip in NetProbe.latencyPorts.map { (ip: ip, port: $0) } }
            let tcp = await Self.offMain { NetProbe.tcpProbe(targets, timeout: 0.5, batch: 64) }
            guard !Task.isCancelled else { return }
            for (i, a) in tcp where a.ms < (self.found[targets[i].ip]?.viaTCP == true ? self.found[targets[i].ip]?.ms ?? .infinity : .infinity) {
                self.found[targets[i].ip]?.ms = a.ms
                self.found[targets[i].ip]?.viaTCP = true
            }
            for (key, list) in [("mDNS", await mdns), ("NetBIOS", await nb), ("DNS", await dns)] {
                guard !Task.isCancelled else { return }
                for (ip, n) in list { self.names[ip, default: [:]][key] = String(n.prefix(64)) }
            }
            // 3. UPnP-beschrijvingen van wat op SSDP antwoordde
            self.phase = L("Asking devices…")
            let urls = await ssdp
            let up = await Self.offMain { NetProbe.upnp(urls.filter { subnet.contains($0.key) }) }
            guard !Task.isCancelled else { return }
            self.upnp = up
            // 4. Bonjour nog even laten lopen (namen komen druppelsgewijs binnen), dan klaar
            let left = 7.5 - Date().timeIntervalSince(start)
            if !local.isEmpty, left > 0 { try? await Task.sleep(for: .seconds(left)) }
            guard !Task.isCancelled else { return }
            self.stopBrowsing()
            self.remember()
            self.scanStart = nil
            self.phase = ""
        }
    }

    func stop() {
        work?.cancel(); work = nil
        side.forEach { $0.cancel() }; side = []
        checking = []
        stopBrowsing()
        scanStart = nil
    }

    /// Zolang de pagina open is: bij een netwerkwissel kijken of de lijst nog bij dit netwerk hoort.
    /// Nog eens na 3 s, want het nieuwe IP-adres (DHCP) komt vaak pas na de melding.
    func watch() {
        guard path == nil else { return }
        let m = NWPathMonitor()
        m.pathUpdateHandler = { [weak self] _ in
            MainActor.assumeIsolated {
                self?.checkNetwork()
                DispatchQueue.main.asyncAfter(deadline: .now() + 3) { self?.checkNetwork() }
            }
        }
        m.start(queue: .main)
        path = m
    }

    func unwatch() { path?.cancel(); path = nil }

    private func checkNetwork() {
        guard let info else { return }
        let now = NetInfo.current()?.networkKey != info.networkKey
        if now != stale { stale = now }
    }

    private func stopBrowsing() {
        browsers.forEach { $0.cancel() }; browsers = []
        resolving.values.forEach { $0.cancel() }; resolving = [:]
    }

    // MARK: Acties per apparaat

    func checkPorts(_ ip: UInt32) {
        guard !checking.contains(ip) else { return }
        checking.insert(ip)
        let targets = NetProbe.ports.map { (ip: ip, port: $0.port) }
        side.append(Task { [weak self] in
            let r = await Self.offMain { NetProbe.tcpProbe(targets, timeout: 0.3) }
            guard let self else { return }
            self.checking.remove(ip)
            guard !Task.isCancelled else { return }
            self.ports[ip] = r.filter { $0.value.isOpen }.map { targets[$0.key].port }.sorted()
            if self.found[ip]?.ms == nil, let best = r.values.map(\.ms).min() {
                self.found[ip]?.ms = best
                self.found[ip]?.viaTCP = true
            }
        })
    }

    func wake(_ mac: [UInt8]) async -> Bool {
        guard let info else { return false }
        let bc = info.ip | ~info.mask
        return await Self.offMain { NetProbe.wake(mac, broadcast: bc) }
    }

    func rename(_ d: NetDevice, to name: String) {
        guard let key = d.key else { return }
        let now = Date()
        let clean = String(name.trimmingCharacters(in: .whitespacesAndNewlines).prefix(64))
        var k = store.devices[key] ?? NetDeviceStore.Known(first: now, last: now)
        k.custom = clean.isEmpty ? nil : clean
        store.devices[key] = k
        store.save()
    }

    func forget(_ d: NetDevice) {
        guard let key = d.key else { return }
        store.devices[key] = nil
        store.save()
    }

    /// Gezien-lijst bijwerken (MAC → naam, IP, netwerk, eerst/laatst gezien), opslaan en eventueel melden.
    private func remember() {
        guard let info else { return }
        let now = Date(), net = info.networkKey
        var fresh: [NetDevice] = []
        for d in devices where d.online {
            guard let key = d.key else { continue }
            var k = store.devices[key] ?? NetDeviceStore.Known(first: now, last: now)
            if store.devices[key] == nil, !d.isMe { fresh.append(d) }
            if let n = d.names.first(where: { !["Your name", "Remembered", "This Mac"].contains($0.source) }) { k.name = n.name }
            k.last = now; k.ip = ipString(d.ip); k.net = net
            store.devices[key] = k
        }
        store.save()
        // Melding alleen als het aan staat en dit niet de allereerste scan is (dan is alles "nieuw")
        if UserDefaults.standard.bool(forKey: "notifyNewDevice"), store.created < now.addingTimeInterval(-60) {
            for d in fresh.prefix(3) {
                Notify.send(title: L("New device on your network"), body: "\(d.name) · \(ipString(d.ip))" + (d.vendor == "–" ? "" : " · \(d.vendorTitle)"))
            }
        }
    }

    // MARK: Bonjour

    /// Volgorde = voorrang voor de naam (Apple-apparaatnaam > AirPlay > … > http).
    static let serviceTypes = ["_device-info._tcp", "_companion-link._tcp", "_airplay._tcp", "_raop._tcp", "_googlecast._tcp",
                               "_sonos._tcp", "_spotify-connect._tcp", "_ipp._tcp", "_printer._tcp", "_scanner._tcp",
                               "_smb._tcp", "_afpovertcp._tcp", "_workstation._tcp", "_ssh._tcp", "_sftp-ssh._tcp", "_rfb._tcp",
                               "_hap._tcp", "_hap._udp", "_homekit._tcp", "_http._tcp", "_meshcop._udp", "_matter._tcp"]

    static func serviceTitle(_ type: String) -> String {
        switch type {
        case "_companion-link._tcp", "_device-info._tcp": L("Apple device")
        case "_airplay._tcp": "AirPlay"
        case "_raop._tcp": L("AirPlay audio")
        case "_googlecast._tcp": "Chromecast"
        case "_sonos._tcp": "Sonos"
        case "_spotify-connect._tcp": "Spotify Connect"
        case "_ipp._tcp", "_printer._tcp": L("Printing")
        case "_scanner._tcp": L("Scanning")
        case "_smb._tcp", "_afpovertcp._tcp": L("File sharing")
        case "_workstation._tcp": L("Computer")
        case "_ssh._tcp", "_sftp-ssh._tcp": L("Remote login (SSH)")
        case "_rfb._tcp": L("Screen sharing")
        case "_hap._tcp", "_hap._udp", "_homekit._tcp": "HomeKit"
        case "_http._tcp": L("Web page")
        case "_matter._tcp": "Matter"
        case "_meshcop._udp": L("Thread border router")
        default: type
        }
    }

    private func browse() {
        for type in Self.serviceTypes {
            let b = NWBrowser(for: .bonjourWithTXTRecord(type: type, domain: "local."), using: type.hasSuffix("._udp") ? .udp : .tcp)
            b.browseResultsChangedHandler = { [weak self] results, _ in
                MainActor.assumeIsolated { self?.resolve(results, type: type) }
            }
            b.stateUpdateHandler = { [weak self] state in
                if case .waiting(let e) = state, case .dns(let code) = e, code == -65570 {   // kDNSServiceErr_PolicyDenied
                    MainActor.assumeIsolated { self?.localBlocked = true }
                }
            }
            b.start(queue: .main)
            browsers.append(b)
        }
    }

    /// Bonjour-dienst → IPv4 via een UDP-"verbinding" (stuurt niets naar het apparaat, alleen mDNS-opzoeken).
    private func resolve(_ results: Set<NWBrowser.Result>, type: String) {
        for r in results where resolving[r.endpoint] == nil && resolving.count < 400 {
            guard case let .service(rawName, _, _, _) = r.endpoint else { continue }
            var txt: [String: String] = [:]
            if case let .bonjour(t) = r.metadata { txt = t.dictionary }
            let params = NWParameters.udp
            (params.defaultProtocolStack.internetProtocol as? NWProtocolIP.Options)?.version = .v4
            let c = NWConnection(to: r.endpoint, using: params)
            c.stateUpdateHandler = { [weak self, weak c] state in
                guard case .ready = state, let c else { if case .failed = state { c?.cancel() }; return }
                if case let .hostPort(host, _)? = c.currentPath?.remoteEndpoint, case let .ipv4(a) = host {
                    let ip = a.rawValue.reduce(UInt32(0)) { $0 << 8 | UInt32($1) }
                    MainActor.assumeIsolated { self?.add(ip, name: Self.friendlyName(rawName, type: type, txt: txt), type: type, txt: txt) }
                }
                c.cancel()
            }
            c.start(queue: .main)
            resolving[r.endpoint] = c
        }
    }

    private func add(_ ip: UInt32, name: String?, type: String, txt: [String: String]) {
        var b = bonjour[ip] ?? Bonjour()
        b.types.insert(type)
        // Naam: eerste dienst in `serviceTypes`-volgorde wint
        let rank = { (t: String) in Self.serviceTypes.firstIndex(of: t) ?? 99 }
        if let name, b.name == nil || b.types.filter({ $0 != type }).allSatisfy({ rank($0) > rank(type) }) { b.name = String(name.prefix(64)) }
        let model = txt["model"] ?? txt["am"] ?? txt["rpMd"] ?? txt["md"] ?? txt["ty"] ?? txt["usb_MDL"]
        if let model, !model.isEmpty, model.count < 64, b.model == nil || type == "_device-info._tcp" { b.model = model }
        bonjour[ip] = b
    }

    nonisolated static func friendlyName(_ raw: String, type: String, txt: [String: String]) -> String? {
        if type == "_matter._tcp" { return nil }   // alleen een hex-id
        if type == "_googlecast._tcp", let fn = txt["fn"], !fn.isEmpty { return fn }
        var name = raw
        // RAOP/Sonos: "A1B2C3D4E5F6@Woonkamer" → "Woonkamer"
        if type == "_raop._tcp" || type == "_sonos._tcp" { name = String(raw.split(separator: "@", maxSplits: 1).last ?? "") }
        // Werkstation: "alex-pc [aa:bb:cc:dd:ee:ff]" → "alex-pc"
        if type == "_workstation._tcp", let b = name.firstIndex(of: "[") { name = name[..<b].trimmingCharacters(in: .whitespaces) }
        return name.isEmpty ? nil : name
    }

    // MARK: Hulp: los van de main thread, ARP, omgekeerde DNS

    nonisolated static func offMain<T: Sendable>(_ body: @escaping @Sendable () -> T) async -> T {
        let t = Task.detached(priority: .utility, operation: body)
        return await withTaskCancellationHandler { await t.value } onCancel: { t.cancel() }
    }

    nonisolated static func echoPacket(id: UInt16, seq: UInt16) -> [UInt8] {
        var p: [UInt8] = [8, 0, 0, 0, UInt8(id >> 8), UInt8(id & 255), UInt8(seq >> 8), UInt8(seq & 255)] + Array("Fusebox!".utf8)
        let c = checksum(p)
        p[2] = UInt8(c >> 8); p[3] = UInt8(c & 255)
        return p
    }

    nonisolated static func checksum(_ b: [UInt8]) -> UInt16 {
        var sum: UInt32 = 0
        for i in stride(from: 0, to: b.count, by: 2) { sum += UInt32(b[i]) << 8 | (i + 1 < b.count ? UInt32(b[i + 1]) : 0) }
        while sum >> 16 != 0 { sum = (sum & 0xFFFF) + (sum >> 16) }
        return ~UInt16(sum)
    }

    /// Echo-antwoord ontleden; macOS levert bij datagram-ICMP de IP-kop mee (of niet): beide kunnen.
    nonisolated static func parseEchoReply(_ s: ArraySlice<UInt8>) -> (id: UInt16, seq: UInt16)? {
        let b = Array(s)
        guard let first = b.first else { return nil }
        let off = first >> 4 == 4 ? Int(first & 0x0F) * 4 : 0
        guard b.count >= off + 8, b[off] == 0 else { return nil }   // type 0 = echo reply
        return (UInt16(b[off + 4]) << 8 | UInt16(b[off + 5]), UInt16(b[off + 6]) << 8 | UInt16(b[off + 7]))
    }

    struct ArpEntry { let ip: UInt32, mac: [UInt8], ifindex: UInt16 }

    /// ARP-tabel van de kernel (sysctl, geen root). Zonder Lokaal netwerk-toestemming geeft macOS hier een lege lijst.
    nonisolated static func arpTable() -> [ArpEntry] {
        #if os(macOS)
        var mib: [Int32] = [CTL_NET, PF_ROUTE, 0, AF_INET, NET_RT_FLAGS, RTF_LLINFO]
        var len = 0
        guard sysctl(&mib, 6, nil, &len, nil, 0) == 0, len > 0 else { return [] }
        len += len / 2 + 1024   // tabel kan intussen groeien
        var buf = [UInt8](repeating: 0, count: len)
        guard sysctl(&mib, 6, &buf, &len, nil, 0) == 0 else { return [] }
        return parseARP(Array(buf[0..<len]))
        #else
        return []   // iOS schermt de ARP-tabel af
        #endif
    }

    #if os(macOS)
    /// Reeks rt_msghdr + sockaddr_in (doel) + sockaddr_dl (MAC). Onvolledige regels (geen MAC) en broadcast overslaan.
    nonisolated static func parseARP(_ b: [UInt8]) -> [ArpEntry] {
        let header = MemoryLayout<rt_msghdr>.size
        func roundup(_ a: Int) -> Int { a > 0 ? 1 + ((a - 1) | 3) : 4 }
        var out: [ArpEntry] = [], off = 0
        while off + 2 <= b.count {
            let msglen = Int(b[off]) | Int(b[off + 1]) << 8
            guard msglen > 0, off + msglen <= b.count else { break }
            defer { off += msglen }
            var sa = off + header
            guard sa + 8 <= off + msglen, b[sa + 1] == AF_INET else { continue }
            let ip = UInt32(b[sa + 4]) << 24 | UInt32(b[sa + 5]) << 16 | UInt32(b[sa + 6]) << 8 | UInt32(b[sa + 7])
            sa += roundup(Int(b[sa]))
            guard sa + 8 <= off + msglen, b[sa + 1] == AF_LINK else { continue }
            let index = UInt16(b[sa + 2]) | UInt16(b[sa + 3]) << 8, nlen = Int(b[sa + 5]), alen = Int(b[sa + 6])
            let m = sa + 8 + nlen
            guard alen == 6, m + 6 <= off + msglen else { continue }
            let mac = Array(b[m..<m + 6])
            guard mac != [255, 255, 255, 255, 255, 255], mac[0] & 1 == 0 else { continue }   // broadcast / multicast
            out.append(ArpEntry(ip: ip, mac: mac, ifindex: index))
        }
        return out
    }
    #endif

    /// Omgekeerde DNS (vaak de naam die de router via DHCP kent), alle tegelijk (hooguit 16 tegelijk), hooguit `timeout` s.
    nonisolated static func reverseNames(_ ips: [UInt32], timeout: Double) async -> [UInt32: String] {
        final class Box: @unchecked Sendable { let lock = NSLock(); var names: [UInt32: String] = [:] }
        let box = Box()
        let group = DispatchGroup(), sem = DispatchSemaphore(value: 16)
        for ip in ips {
            group.enter()
            DispatchQueue.global(qos: .utility).async {
                defer { group.leave() }
                guard sem.wait(timeout: .now() + timeout) == .success else { return }
                defer { sem.signal() }
                var a = NetProbe.sockaddrIn(ip)
                var host = [CChar](repeating: 0, count: Int(NI_MAXHOST))
                let r = withUnsafePointer(to: &a) { $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    getnameinfo($0, socklen_t(MemoryLayout<sockaddr_in>.size), &host, socklen_t(host.count), nil, 0, NI_NAMEREQD) } }
                guard r == 0, let name = cleanHostname(String(cString: host)) else { return }
                box.lock.withLock { box.names[ip] = name }
            }
        }
        return await withCheckedContinuation { cont in
            DispatchQueue.global(qos: .utility).async {
                _ = group.wait(timeout: .now() + timeout)
                cont.resume(returning: box.lock.withLock { box.names })
            }
        }
    }

    /// "alex-iphone.fritz.box" → "alex-iphone"; IP-adressen als naam weg.
    nonisolated static func cleanHostname(_ s: String) -> String? {
        guard !s.isEmpty, parseIP(s) == nil else { return nil }
        let first = s.split(separator: ".").first.map(String.init) ?? s
        return first.isEmpty ? nil : first
    }

    // MARK: Reine logica

    /// Te scannen adressen: het hele subnet tot /24; groter → de /24 rond ons eigen adres (max 254).
    nonisolated static func hosts(ip: UInt32, mask: UInt32) -> [UInt32] {
        let m = mask.nonzeroBitCount < 24 ? 0xFFFF_FF00 : mask
        let net = ip & m, broadcast = net | ~m
        guard broadcast > net + 1 else { return [] }   // /31, /32: geen buren
        return Array(net + 1..<broadcast)
    }

    enum RangeError: Error, Equatable {
        case invalid, reversed, tooLarge(Int)
        var message: String {
            switch self {
            case .invalid: L("Not a range. Try 192.168.1.0/24, 192.168.1.10-80 or a single address.")
            case .reversed: L("The first address is higher than the last one.")
            case .tooLarge(let n): L("That's \(n) addresses; Fusebox scans at most \(NetScanner.maxRange) at a time (a /20). Use a smaller block.")
            }
        }
    }
    nonisolated static let maxRange = 4096

    /// Getypt bereik → aaneengesloten adressen: "192.168.1.0/24" (zonder netwerk- en broadcastadres tot /30),
    /// "192.168.1.10-192.168.1.80", kort "192.168.1.10-80" (alleen het laatste getal), of één adres. Spaties mogen.
    nonisolated static func parseRange(_ text: String) -> Result<ClosedRange<UInt32>, RangeError> {
        let s = text.filter { !$0.isWhitespace }
        let r: ClosedRange<UInt32>
        if let slash = s.firstIndex(of: "/") {
            guard let ip = parseIP(String(s[..<slash])), let bits = Int(s[s.index(after: slash)...]), (0...32).contains(bits) else { return .failure(.invalid) }
            let mask: UInt32 = bits == 0 ? 0 : .max << (32 - bits)
            let net = ip & mask, last = net | ~mask
            r = bits >= 31 ? net...last : net + 1...last - 1
        } else {
            let parts = s.split(separator: "-", omittingEmptySubsequences: false).map(String.init)
            guard parts.count <= 2, let a = parseIP(parts[0]) else { return .failure(.invalid) }
            var b = a
            if parts.count == 2 {
                if let ip = parseIP(parts[1]) { b = ip }
                else if let last = UInt32(parts[1]), last < 256, parts[1].allSatisfy(\.isNumber) { b = a & 0xFFFF_FF00 | last }
                else { return .failure(.invalid) }
            }
            guard a <= b else { return .failure(.reversed) }
            r = a...b
        }
        return r.count > maxRange ? .failure(.tooLarge(r.count)) : .success(r)
    }

    /// Lokaal beheerd adres (bit 2 van de eerste byte): willekeurig MAC-adres van iPhone/Android/Mac met Wi-Fi-privacy.
    nonisolated static func isPrivate(_ mac: [UInt8]) -> Bool { (mac.first ?? 0) & 0x02 != 0 }

    /// Eigen korte namen eerst (die herkent `guess`), anders de officiële IEEE-lijst.
    nonisolated static func vendor(_ mac: [UInt8]) -> String? {
        guard mac.count >= 3 else { return nil }
        let key = UInt32(mac[0]) << 16 | UInt32(mac[1]) << 8 | UInt32(mac[2])
        return ouiTable[key] ?? NetProbe.ieeeVendor(key)
    }

    /// Soort apparaat raden uit diensten, model, namen en maker. ponytail: simpele trefwoorden; volgorde bepaalt de winnaar.
    nonisolated static func guess(name: String, vendor: String, services: Set<String>, model: String?,
                                  isRouter: Bool, isMe: Bool, privateMAC: Bool) -> DeviceKind {
        if isRouter { return .router }
        if isMe { return .computer }
        let n = (name + " " + (model ?? "")).lowercased(), v = vendor.lowercased()
        func has(_ words: String...) -> Bool { words.contains { n.contains($0) } }
        func offers(_ types: String...) -> Bool { types.contains { services.contains($0) } }
        func made(_ prefixes: String...) -> Bool { prefixes.contains { v.hasPrefix($0) } }
        if offers("_ipp._tcp", "_printer._tcp", "_scanner._tcp") || made("hp", "hewlett packard", "canon", "epson", "seiko epson", "brother", "xerox", "lexmark", "kyocera") { return .printer }
        if has("xbox", "playstation", "nintendo") || made("nintendo") { return .console }
        if has("iphone", "ipad", "galaxy", "pixel", "android", "phone") { return .phone }
        if has("mac", "laptop", "desktop", "thinkpad", "-pc", "windows") { return .computer }
        if has("homepod", "audioaccessory", "sonos", "speaker", "echo", "nest mini", "home mini", "soundbar", "zoneplayer") || made("sonos", "bose") { return .speaker }
        if has("appletv", "apple tv", "tv", "chromecast", "roku", "bravia", "fire", "mediarenderer") || offers("_googlecast._tcp") || made("roku") { return .tv }
        if offers("_sonos._tcp", "_spotify-connect._tcp", "_raop._tcp") { return .speaker }
        if has("internetgatewaydevice", "router", "access point") { return .router }
        if offers("_hap._tcp", "_hap._udp", "_homekit._tcp", "_matter._tcp", "_meshcop._udp") || has("hue", "plug", "bulb", "thermostat", "camera", "doorbell", "esp", "bridge")
            || made("signify", "philips lighting", "espressif", "ring", "google nest", "nest labs", "tado", "shelly", "tuya", "ikea", "lumi") { return .smartHome }
        if has("nas", "diskstation", "synology", "qnap") || made("synology", "qnap") { return .nas }
        if offers("_rfb._tcp", "_workstation._tcp", "_ssh._tcp", "_sftp-ssh._tcp", "_afpovertcp._tcp", "_smb._tcp")
            || made("intel", "asus", "microsoft", "raspberry pi", "dell", "lenovo", "hon hai", "liteon", "azurewave") { return .computer }
        if made("tp-link", "ubiquiti", "netgear", "avm", "eero", "zyxel", "sagemcom", "arcadyan", "technicolor") { return .router }
        if privateMAC { return .phone }
        return .unknown
    }

    // MARK: Makers (OUI = eerste 3 bytes van het MAC-adres). Beste poging, geen volledige lijst.

    nonisolated static let ouiTable: [UInt32: String] = {
        var t: [UInt32: String] = [:]
        for line in ouiText.split(separator: "\n") {
            let parts = line.split(separator: ":", maxSplits: 1)
            guard parts.count == 2 else { continue }
            for hex in parts[1].split(separator: " ") { if let v = UInt32(hex, radix: 16) { t[v] = String(parts[0]) } }
        }
        return t
    }()

    nonisolated static let ouiText = """
    Apple:000393 000A27 000A95 000D93 0010FA 001124 001451 0016CB 0017F2 0019E3 001B63 001CB3 001D4F 001E52 001EC2 001F5B 001FF3 0021E9 002241 002312 002332 00236C 0023DF 002436 002500 00254B 0025BC 002608 00264A 0026B0 0026BB 040CCE 041552 042665 04DB56 04E536 04F13E 04F7E4 086698 086D41 0C74C2 1040F3 109ADD 10DDB1 14109F 18AF61 1CABA7 20C9D0 24A074 28CFDA 28E02C 34159E 3C0754 406C8F 40A6D9 442A60 4860BC 542696 5855CA 5C5948 60334B 64B9E8 685B35 705681 70CD60 7831C1 78CA39 7C6D62 7CC3A1 80E650 843835 8863DF 8C5877 9027E4 98FE94 9C207B A4B197 A82066 AC87A3 B817C2 B8C75D BC52B7 C82A14 C8BCC8 D023DB D49A20 D83062 DC2B61 E0F847 E4CE8F F0B479 F41BA1 F81EDF ACBC32 F01898 A483E7 3C22FB 147DDA BCD074 F84D89 88665A 00C610
    Samsung:0000F0 0007AB 001247 001599 001632 001D25 002119 002339 002637 08D42B 3423BA 38AA3C 5C0A5B 78BDBC 8C7712 94350A A00BBA BC1485 C4576E E8508B F025B7 FCA13E 8425DB 90F1AA D022BE 508569 CC07AB
    Sonos:000E58 347E5C 48A6B8 542A1B 5CAAFD 7828CA 949F3E B8E937 F0F6C1 38420B
    Google:3C5AB4 546009 F4F5D8 F4F5E8 6CADF8 A47733 1CF29A D86C63 001A11 48D6D5 30FD38 E4F042
    Google Nest:18B430 641666
    Amazon:00FC8B 0C47C9 34D270 38F73D 40B4CD 44650D 50DCE7 6837E9 74C246 84D6D0 A002DC AC63BE F0272D FC65DE 6854FD 747548 B47C9C F0D2F1 0CEE99
    TP-Link:002719 14CC20 18A6F7 50C7BF 54C80F 60E327 647002 98DAC4 A42BB0 B04E26 C04A00 E8DE27 F4F26D EC086B 30B5C2 1C3BF3 5C628B AC84C6 98254A
    Ubiquiti:00156D 002722 0418D6 24A43C 44D9E7 687251 7483C2 788A20 802AA8 B4FBE4 DC9FDB F09FC2 FCECDA E063DA 18E829 245A4C 74ACB9 68D79A
    Netgear:00095B 000FB5 00146C 001B2F 001E2A 00223F 0024B2 0026F2 204E7F 28C68E 2CB05D 30469A 4494FC 6CB0CE 841B5E A040A0 A06391 C03F0E E0469A 9C3DCF B07FB9 08BD43
    AVM (Fritz!):00040E 00150C 001C4A 001F3F 0024FE 0896D7 246511 2C3AFD 2C91AB 3431C4 3810D5 3CA62F 444E6D 5C4979 7CFF4D 989BCB 9CC7A6 BC0543 C02506 C80E14 CCCE1E DC396F E0286D E8DF70 F0B014 74427F 485D35 3C3712 04B4FE B0F208
    Signify (Philips Hue):001788 ECB5FA
    Raspberry Pi:B827EB DCA632 E45F01 D83ADD 28CDC1 2CCF67
    Espressif:240AC4 246F28 2462AB 30AEA4 3C71BF 4C11AE 5CCF7F 600194 68C63A 807D3A 840D8E 84CCA8 84F3EB 8CAAB5 98F4AB A020A6 A4CF12 AC67B2 B4E62D BCDDC2 C44F33 C82B96 CC50E3 D8A01D DC4F22 ECFABC 18FE34 2CF432 483FDA 7C9EBD 94B97E 083AF2 40F520 E8DB84 349454 C8C9A3 10521C 3C6105 70039F A848FA 24A160
    Intel:001B21 001E67 00216A 0024D7 3CA9F4 5891CF 606720 7C5CF8 8C8D28 A088B4 ACED5C B49691 DC5360 E4B318 F81654 3413E8 40A3CC 4851B7 5C879C 94659C 0013E8 001500 0016EA 001CBF 001F3B 7CB27D
    Xiaomi:009EC8 04CF8C 0C1DAF 14F65A 185936 286C07 34CE00 50642B 584498 640980 64B473 742344 7811DC 7C1DD9 8CBEBE 98FAE3 9C99A0 A086C6 ACF7F3 B0E235 C46AB7 D4970B F0B429 F8A45F FC64BA 5448E6 047A0B
    Huawei:001882 001E10 00259E 00464B 00E0FC 04BD70 0819A6 104780 20F3A3 283152 30D17E 4846FB 548998 70723C 80FB06 8853D4 ACE215 C81479 E0247F F4C714 009ACD 48DB50 582AF7
    Sony:00014A 0013A9 001DBA 0024BE 045D4B 104FA8 3017C8 544249 584822 78843C AC9B0A FCF152 00041F 00D9D1 280DFC 709E29 BC60A7 F8461C 0CFE45 2CCC44 00E421 A8E3EE
    LG:001C62 001E75 001F6B 001FE3 0022A9 002483 0026E2 10683F 344DF7 388C50 58A2B5 64995D 785DC8 88C9D0 A816B2 A823FE C4366C CCFA00 E85B5B F80CF3 2C54CF 64BC0C 14C913 7440BE 00AA70
    HP:0001E6 000802 000F20 00110A 001438 0017A4 001A4B 001E0B 00215A 00237D 0025B3 101F74 1458D0 2C44FD 30E171 3CD92B 705A0F 80CE62 9457A5 9C8E99 A0D3C1 B4B686 C8D9D2 D4C9EF ECB1D7 F43909 FC15B4 645106 3863BB 10E7C6 84A93E 6C02E0
    Canon:000085 001E8F 00BBC1 180CAC 2C9EFC 60128B 74BFC0 888717 F48139 9C32CE
    Epson:000048 0026AB 44D244 64EB8C 9CAED3 A4EE57 AC1826 B0E892 DCCD2F E0BB9E F8D027 381A52 50579C 64C6D2
    Brother:001BA9 008077 30055C 3C2AF4 B42200
    Nintendo:0009BF 001656 0017AB 00191D 001AE9 001B7A 001CBE 001E35 001F32 002147 00224C 00241E 0024F3 0025A0 0403D6 2C10C1 34AF2C 40F407 58BDA3 5C521E 7CBB8A 8C56C5 98415C 98B6E9 9CE635 A438CC B8AE6E CC9E00 D86BF7 DC68EB E00C7F E84ECE 64B5C6 9458CB 606BFF
    Microsoft:0003FF 000D3A 00125A 00155D 0017FA 001DD8 002248 0025AE 0050F2 281878 3059B7 501AC5 5882A8 6045BD 7C1E52 985FD3 B4AE2B C83F26 DCB4C4 7CED8D 9CAA1B C49DED
    ASUS:000C6E 000EA6 00112F 0013D4 0015F2 001731 0018F3 001A92 001BFC 001D60 001E8C 002215 002354 00248C 002618 049226 08606E 107B44 10BF48 14DAE9 1C872C 2C56DC 305A3A 382C4A 40167E 50465D 5404A6 6045CB 704D7B 74D02B 88D7F6 90E6BA AC220B AC9E17 B06EBF BCEE7B C86000 D850E6 E03F49 F07959 F46D04 FC3497 04D9F5 244BFE A85E45
    Synology:001132 9009D0
    QNAP:00089B 245EBE
    Ring:343EA4 54E019 9C7613
    Roku:000D4B B0A737 B83E59 C83A6B CC6DA0 D83134 DC3A5E 080581 105932 20EFBD 84EAED AC3A7A 88DEA9 B0EE7B D04D2C
    Bose:000C8A 0452C7 08DF1F 2C41A1 4C875D 60ABD2 782B64 BC87FA
    """

    // MARK: Zelftest (Fusebox --selftest)

    nonisolated static func selfTest() {
        // Eigen OUI-tabel: geen dubbele prefixen over makers heen, en opzoeken werkt
        var seen = [String: String]()
        for line in ouiText.split(separator: "\n") {
            let parts = line.split(separator: ":", maxSplits: 1)
            for hex in parts[1].split(separator: " ") {
                precondition(hex.count == 6 && UInt32(hex, radix: 16) != nil, "oui \(hex)")
                precondition(seen.updateValue(String(parts[0]), forKey: String(hex)) == nil, "oui dubbel \(hex)")
            }
        }
        precondition(vendor([0xB8, 0x27, 0xEB, 1, 2, 3]) == "Raspberry Pi" && vendor([0x00, 0x17, 0x88, 0, 0, 0]) == "Signify (Philips Hue)", "oui")
        precondition(vendor([0x12, 0x34, 0x56, 0, 0, 0]) == nil, "oui onbekend")
        precondition(isPrivate([0xDA, 0, 0, 0, 0, 0]) && isPrivate([0x02, 0, 0, 0, 0, 0]) && !isPrivate([0xB8, 0x27, 0xEB, 0, 0, 0]), "lokaal beheerd")
        // IEEE-lijst (TSV)
        let tsv = NetProbe.parseOUI("000000\tXEROX\n00000C\tCisco Systems\r\n001132\tSynology\nzzzzzz\tKapot\n1234\tTe kort\nAABBCC\tIEEE Registration Authority\nFCFCFC\t  NETGEAR  \n")
        precondition(tsv.count == 4 && tsv[0] == "Xerox" && tsv[0x00000C] == "Cisco Systems" && tsv[0x001132] == "Synology" && tsv[0xFCFCFC] == "Netgear", "oui tsv \(tsv)")
        precondition(NetProbe.tidyVendor("TP-LINK") == "TP-LINK" && NetProbe.tidyVendor("HUAWEI TECHNOLOGIES") == "Huawei Technologies" && NetProbe.tidyVendor("LG") == "LG", "maker netjes")
        // Subnet, MAC
        let ip = parseIP("192.168.1.23")!
        precondition(ipString(ip) == "192.168.1.23" && parseIP("1.2.3") == nil && parseIP("1.2.3.256") == nil && parseIP("1..2.3") == nil && parseIP("a.1.2.3.4") == nil, "ip")
        precondition(parseMAC("B8:27:EB:01:02:03") == [0xB8, 0x27, 0xEB, 1, 2, 3] && parseMAC("B8:27") == nil && macString(parseMAC("0a:0b:0c:0d:0e:0f")!) == "0A:0B:0C:0D:0E:0F", "mac")
        let h24 = hosts(ip: ip, mask: 0xFFFF_FF00)
        precondition(h24.count == 254 && h24.first == parseIP("192.168.1.1") && h24.last == parseIP("192.168.1.254"), "/24")
        let h16 = hosts(ip: parseIP("10.0.5.9")!, mask: 0xFFFF_0000)
        precondition(h16.count == 254 && h16.first == parseIP("10.0.5.1"), "/16 → /24 rond eigen adres")
        precondition(hosts(ip: ip, mask: 0xFFFF_FFF0).count == 14 && hosts(ip: ip, mask: 0xFFFF_FFFF).isEmpty, "/28, /32")
        // Scanbereik typen
        func range(_ t: String) -> ClosedRange<UInt32>? { if case .success(let r) = parseRange(t) { r } else { nil } }
        func fails(_ t: String) -> RangeError? { if case .failure(let e) = parseRange(t) { e } else { nil } }
        precondition(range("192.168.1.0/24") == parseIP("192.168.1.1")!...parseIP("192.168.1.254")!, "cidr /24")
        precondition(range("10.0.0.0/22") == parseIP("10.0.0.1")!...parseIP("10.0.3.254")! && range("10.0.1.77/22")?.count == 1022, "cidr /22, niet uitgelijnd")
        precondition(range("192.168.1.6/31")?.count == 2 && range("192.168.1.7/32") == parseIP("192.168.1.7")!...parseIP("192.168.1.7")!, "/31, /32")
        precondition(range("192.168.1.10-192.168.1.80") == parseIP("192.168.1.10")!...parseIP("192.168.1.80")!
                     && range(" 192.168.1.10 - 80 ") == parseIP("192.168.1.10")!...parseIP("192.168.1.80")!, "streepje, kort")
        precondition(range("192.168.1.7")?.count == 1 && range("10.0.0.0/20")?.count == 4094, "één adres, /20")
        precondition(["", "abc", "192.168.1", "192.168.1.300", "192.168.1.10-300", "192.168.1.10-", "192.168.1.1-2-3", "192.168.1.0/33", "192.168.1.0/", "192.168.1.10-+80"]
                        .allSatisfy { fails($0) == .invalid }, "ongeldig")
        precondition(fails("192.168.1.80-10") == .reversed && fails("192.168.1.80-192.168.1.10") == .reversed, "omgekeerd")
        precondition(fails("10.0.0.0/16") == .tooLarge(65_534) && fails("10.0.0.0-10.0.16.0") == .tooLarge(4097) && range("10.0.0.0-10.0.15.255")?.count == 4096, "te groot")
        // Zoeken in apparaten
        var d = NetDevice(id: "x", ip: parseIP("192.168.1.42")!)
        d.name = "Living Room"; d.names = [("Bonjour", "Living Room"), ("DNS", "alex-tv")]; d.mac = [0xB8, 0x27, 0xEB, 1, 2, 3]; d.vendor = "Raspberry Pi"
        precondition(["", "  ", "living", "ALEX-TV", "168.1.42", "b8:27:eb", "raspberry"].allSatisfy(d.matches) && !d.matches("sonos") && !d.matches("10.0.0"), "zoeken")
        precondition(NetProbe.isLAN(ip) && NetProbe.isLAN(parseIP("10.1.2.3")!) && NetProbe.isLAN(parseIP("172.20.0.1")!)
                     && !NetProbe.isLAN(parseIP("172.32.0.1")!) && !NetProbe.isLAN(parseIP("8.8.8.8")!), "lan")
        // ICMP
        let pkt = echoPacket(id: 0xBEEF, seq: 7)
        precondition(checksum(pkt) == 0, "icmp checksum")
        var reply = pkt; reply[0] = 0
        precondition(parseEchoReply(reply[...])! == (0xBEEF, 7), "echo zonder ip-kop")
        let withIP = [0x45] + [UInt8](repeating: 0, count: 19) + reply
        precondition(parseEchoReply(withIP[...])! == (0xBEEF, 7) && parseEchoReply(pkt[...]) == nil, "echo met ip-kop / request")
        #if os(macOS)
        // ARP: één volledige regel + één zonder MAC
        func msg(_ ipBytes: [UInt8], mac: [UInt8]) -> [UInt8] {
            let sin: [UInt8] = [16, UInt8(AF_INET), 0, 0] + ipBytes + [UInt8](repeating: 0, count: 8)
            let sdl: [UInt8] = [20, UInt8(AF_LINK), 4, 0, 6, 0, UInt8(mac.count), 0] + mac + [UInt8](repeating: 0, count: 12 - mac.count)
            var m = [UInt8](repeating: 0, count: MemoryLayout<rt_msghdr>.size) + sin + sdl
            m[0] = UInt8(m.count & 255); m[1] = UInt8(m.count >> 8)
            return m
        }
        let arp = parseARP(msg([192, 168, 1, 5], mac: [0xB8, 0x27, 0xEB, 1, 2, 3]) + msg([192, 168, 1, 6], mac: []))
        precondition(arp.count == 1 && arp[0].ip == parseIP("192.168.1.5") && arp[0].mac == [0xB8, 0x27, 0xEB, 1, 2, 3] && arp[0].ifindex == 4, "arp")
        #endif
        // Onthouden-sleutel: met MAC altijd het MAC (Mac en iPhone); zonder MAC alleen op de iPhone (naam, anders IP)
        precondition(storeKey(mac: [0xB8, 0x27, 0xEB, 1, 2, 3], ip: 5) == "B8:27:EB:01:02:03", "sleutel mac")
        #if os(iOS)
        precondition(storeKey(mac: nil, ip: parseIP("192.168.1.5")!) == "ip 192.168.1.5" && storeKey(mac: nil, ip: 0) == nil, "sleutel zonder mac")
        #else
        precondition(storeKey(mac: nil, ip: 5) == nil, "zonder mac niet onthouden")
        #endif
        // mDNS: vraag opbouwen, antwoord (met compressie) ontleden
        let q = NetProbe.dnsQuery(NetProbe.reverseName(parseIP("192.168.1.5")!), type: 12, id: 9)
        precondition(NetProbe.reverseName(parseIP("192.168.1.5")!) == "5.1.168.192.in-addr.arpa", "reverse naam")
        precondition(q.count == 12 + 26 + 4 && q[1] == 9 && q[5] == 1 && q[q.count - 4] == 0 && q[q.count - 3] == 12 && q[q.count - 2] == 0x80 && q[q.count - 1] == 1, "mdns vraag")
        var off = 12
        precondition(NetProbe.readName(q, &off) == "5.1.168.192.in-addr.arpa" && off == q.count - 4, "dns naam lezen")
        // Antwoord: kop (1 vraag, 1 antwoord), vraag, antwoord met naam-verwijzing naar de vraag, PTR "alex-iphone.local"
        var resp = q
        resp[2] = 0x84; resp[7] = 1
        let target: [UInt8] = [11] + Array("alex-iphone".utf8) + [5] + Array("local".utf8) + [0]
        resp += [0xC0, 12, 0, 12, 0x80, 1, 0, 0, 0, 120, 0, UInt8(target.count)] + target
        precondition(NetProbe.reverseAnswers(resp) == [parseIP("192.168.1.5")!: "alex-iphone"], "mdns antwoord")
        var loop: [UInt8] = [0xC0, 0]; var o2 = 0
        precondition(NetProbe.readName(loop, &o2) == nil, "dns lus")
        loop = Array(resp.prefix(20))
        precondition(NetProbe.reverseAnswers(loop).isEmpty && NetProbe.reverseAnswers([1, 2, 3]).isEmpty, "dns afgekapt")
        // NetBIOS: vraag + status-antwoord (groepsnaam overslaan, computernaam pakken)
        let nq = NetProbe.netbiosQuery
        precondition(nq.count == 50 && nq[12] == 0x20 && nq[13] == 0x43 && nq[14] == 0x4B && nq[15] == 0x41 && nq[47] == 0x21, "netbios vraag")
        func nbName(_ s: String, suffix: UInt8, group: Bool) -> [UInt8] {
            Array(s.utf8) + [UInt8](repeating: 0x20, count: 15 - s.utf8.count) + [suffix, group ? 0x84 : 0x04, 0]
        }
        let nbResp: [UInt8] = [0x46, 0x42, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0] + Array(nq[12..<46]) + [0, 0x21, 0, 1, 0, 0, 0, 0, 0, 41, 2]
            + nbName("WORKGROUP", suffix: 0, group: true) + nbName("OFFICE-PC", suffix: 0, group: false) + [UInt8](repeating: 0, count: 6)
        precondition(NetProbe.netbiosName(nbResp) == "OFFICE-PC" && NetProbe.netbiosName(Array(nbResp.prefix(60))) == nil, "netbios antwoord")
        // SSDP + UPnP
        let ssdp = NetProbe.ssdpHeaders(Array("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLocation: http://192.168.1.40:1400/xml/device_description.xml\r\nST: upnp:rootdevice\r\n\r\n".utf8))
        precondition(ssdp["location"] == "http://192.168.1.40:1400/xml/device_description.xml" && ssdp["st"] == "upnp:rootdevice", "ssdp kop")
        precondition(NetProbe.ssdpHeaders(Array("M-SEARCH * HTTP/1.1\r\nLOCATION: http://x\r\n".utf8)).isEmpty, "ssdp geen antwoord")
        let dev = parseIP("192.168.1.40")!
        precondition(NetProbe.lanURL("http://192.168.1.40:1400/desc.xml", from: dev) != nil, "lan-url")
        precondition(NetProbe.lanURL("http://8.8.8.8/desc.xml", from: parseIP("8.8.8.8")!) == nil
                     && NetProbe.lanURL("http://192.168.1.41/desc.xml", from: dev) == nil
                     && NetProbe.lanURL("https://192.168.1.40/desc.xml", from: dev) == nil
                     && NetProbe.lanURL("http://evil.example/desc.xml", from: dev) == nil, "lan-url weigeren")
        let xml = Array("""
        <?xml version="1.0"?><root xmlns="urn:schemas-upnp-org:device-1-0"><device>
        <deviceType>urn:schemas-upnp-org:device:ZonePlayer:1</deviceType><friendlyName> Living Room </friendlyName>
        <manufacturer>Sonos, Inc.</manufacturer><modelName>Sonos One</modelName>
        <deviceList><device><friendlyName>Sub device</friendlyName></device></deviceList></device></root>
        """.utf8)
        precondition(NetProbe.parseUPnP(xml) == NetProbe.UPnPInfo(friendlyName: "Living Room", manufacturer: "Sonos, Inc.", modelName: "Sonos One",
                                                                  deviceType: "urn:schemas-upnp-org:device:ZonePlayer:1"), "upnp xml")
        precondition(NetProbe.parseUPnP(Array("<!DOCTYPE x [<!ENTITY a \"aaaa\">]><root><friendlyName>&a;</friendlyName></root>".utf8)) == nil, "upnp dtd")
        precondition(NetProbe.parseUPnP([UInt8](repeating: 0x20, count: 70_000)) == nil && NetProbe.parseUPnP(Array("not xml".utf8)) == nil, "upnp groot/kapot")
        precondition(NetProbe.parseUPnP(Array(("<!--" + String(repeating: " ", count: 3000) + "--><!DOCTYPE x [<!ENTITY a \"a\">]><root/>").utf8)) == nil, "upnp dtd na lange opmerking")
        // Wake on LAN, poorten, modellen
        let mac: [UInt8] = [0x00, 0x11, 0x32, 0xAA, 0xBB, 0xCC]
        let magic = NetProbe.magicPacket(mac)!
        precondition(magic.count == 102 && magic.prefix(6).allSatisfy { $0 == 0xFF } && Array(magic[6..<12]) == mac && Array(magic[96..<102]) == mac
                     && NetProbe.magicPacket([1, 2, 3]) == nil, "wol")
        let portNumbers = NetProbe.ports.map(\.port)
        precondition(Set(portNumbers).count == portNumbers.count && portNumbers.count == 15
                     && [22, 80, 443, 445, 548, 631, 3389, 5000, 5001, 8080, 9100, 62078, 32400, 1883, 554].allSatisfy(portNumbers.contains), "poorten")
        precondition(NetProbe.webURL(dev, 80)?.absoluteString == "http://192.168.1.40/" && NetProbe.webURL(dev, 5001)?.absoluteString == "https://192.168.1.40:5001/"
                     && NetProbe.webURL(dev, 22) == nil, "open-link")
        precondition(NetProbe.appleModel("MacBookPro18,3") == "MacBook Pro" && NetProbe.appleModel("Mac15,3") == "Mac" && NetProbe.appleModel("Macmini9,1") == "Mac mini"
                     && NetProbe.appleModel("AudioAccessory5,1") == "HomePod mini" && NetProbe.appleModel("iPhone16,2") == "iPhone" && NetProbe.appleModel("Pixel 8") == nil, "apple-model")
        precondition(modelText(bonjour: "MacBookPro18,3", upnp: nil) == "MacBook Pro (MacBookPro18,3)", "modeltekst")
        // Ping-statistiek
        let s = PingStats([10, 12, 11, 15], sent: 5)
        precondition(s.got == 4 && abs(s.loss - 0.2) < 1e-9 && s.avg == 12 && s.min == 10 && s.max == 15 && abs(s.jitter! - 7.0 / 3) < 1e-9, "ping-statistiek")
        precondition(PingStats([], sent: 3).loss == 1 && PingStats([], sent: 3).avg == nil && PingStats([5], sent: 1).jitter == nil, "ping leeg")
        // Namen, soort, oordeel
        precondition(friendlyName("A1B2C3D4E5F6@Living Room", type: "_raop._tcp", txt: [:]) == "Living Room", "raop-naam")
        precondition(friendlyName("Chromecast-abc", type: "_googlecast._tcp", txt: ["fn": "Bedroom TV"]) == "Bedroom TV", "cast-naam")
        precondition(friendlyName("alex-pc [aa:bb:cc:dd:ee:ff]", type: "_workstation._tcp", txt: [:]) == "alex-pc"
                     && friendlyName("ABCDEF0123-0000000000000001", type: "_matter._tcp", txt: [:]) == nil, "werkstation/matter")
        precondition(cleanHostname("alex-iphone.fritz.box") == "alex-iphone" && cleanHostname("192.168.1.5") == nil, "hostnaam")
        precondition(guess(name: "Living Room", vendor: "Apple", services: ["_airplay._tcp", "_raop._tcp"], model: "AppleTV14,1", isRouter: false, isMe: false, privateMAC: false) == .tv, "apple tv")
        precondition(guess(name: "Alex's MacBook Pro", vendor: "Apple", services: ["_raop._tcp"], model: "Mac15,3", isRouter: false, isMe: false, privateMAC: false) == .computer, "mac")
        precondition(guess(name: "Unknown device", vendor: "Private address", services: [], model: nil, isRouter: false, isMe: false, privateMAC: true) == .phone, "privé")
        precondition(guess(name: "HL-L2350DW", vendor: "Brother", services: ["_ipp._tcp"], model: nil, isRouter: false, isMe: false, privateMAC: false) == .printer, "printer")
        precondition(guess(name: "Living Room Sonos One urn:schemas-upnp-org:device:ZonePlayer:1", vendor: "Sonos", services: [], model: nil, isRouter: false, isMe: false, privateMAC: false) == .speaker, "sonos")
        precondition(guess(name: "OFFICE-PC", vendor: "Hon Hai Precision Ind", services: [], model: nil, isRouter: false, isMe: false, privateMAC: false) == .computer, "netbios-pc")
        precondition(fallbackName(vendor: "Sonos", kind: .speaker, isRouter: false, privateMAC: false) == "Sonos speaker"
                     && fallbackName(vendor: "Samsung Electronics Co", kind: .tv, isRouter: false, privateMAC: false) == "Samsung TV"
                     && fallbackName(vendor: "–", kind: .unknown, isRouter: false, privateMAC: false) == "Unknown device", "terugvalnaam")
        let good = PingStats([3, 3, 4, 3, 3, 3, 3, 3, 3, 3], sent: 10), net = PingStats([15, 14, 16, 15, 15, 15, 14, 15, 16, 15], sent: 10)
        precondition(InternetStats.verdict(wired: false, rssi: -55, snr: 40, router: good, internet: net, dnsMs: 12, internetCheck: true).ok == true, "alles goed")
        let lossy = PingStats([3, 3, 4, 3, 3, 3, 3, 3, 3], sent: 10)
        let weak = InternetStats.verdict(wired: false, rssi: -78, snr: 15, router: lossy, internet: net, dnsMs: 12, internetCheck: true)
        precondition(weak.ok == false && weak.text.hasPrefix("Wi-Fi signal weak and 10% packet loss to the router") && weak.text.contains("cable"), "zwak wifi: \(weak.text)")
        let slowNet = InternetStats.verdict(wired: true, rssi: nil, snr: nil, router: good, internet: PingStats([300, 310, 290], sent: 3), dnsMs: nil, internetCheck: true)
        precondition(slowNet.ok == false && slowNet.text.contains("provider"), "provider")
        precondition(InternetStats.verdict(wired: true, rssi: nil, snr: nil, router: PingStats([], sent: 10), internet: PingStats([], sent: 10), dnsMs: nil, internetCheck: true).text.hasPrefix("No answer"), "niets")
        precondition(InternetStats.verdict(wired: true, rssi: nil, snr: nil, router: good, internet: nil, dnsMs: nil, internetCheck: false).ok == true, "internet uit")
        precondition(InternetStats.verdict(wired: true, rssi: nil, snr: nil, router: good, internet: net, dnsMs: 400, internetCheck: true).text.contains("DNS"), "trage dns")
        let meta = InternetStats.parseMeta(Data(#"{"clientIp":"203.0.113.7","asOrganization":"Example ISP","city":"Utrecht","country":"NL","colo":"AMS"}"#.utf8))
        precondition(meta?.ip == "203.0.113.7" && meta?.isp == "Example ISP" && meta?.place == "Utrecht, NL (Cloudflare AMS)" && InternetStats.parseMeta(Data("[]".utf8)) == nil, "meta")
        SpeedMath.selfTest()
        // Opslag: oude bestanden (zonder nieuwe velden) blijven leesbaar
        let old = Data(#"{"created":"2025-01-01T00:00:00Z","devices":{"B8:27:EB:01:02:03":{"name":"pi","first":"2025-01-01T00:00:00Z","last":"2025-01-02T00:00:00Z"}}}"#.utf8)
        let dec = JSONDecoder(); dec.dateDecodingStrategy = .iso8601
        let st = try? dec.decode(NetDeviceStore.self, from: old)
        precondition(st?.devices["B8:27:EB:01:02:03"]?.name == "pi" && st?.devices["B8:27:EB:01:02:03"]?.custom == nil, "opslag oud formaat")
        precondition(agoText(Date().addingTimeInterval(-3 * 86_400 - 5)) == "3 d ago" && agoText(Date().addingTimeInterval(-7200)) == "2 h ago", "geleden")
        print("netwerk: ok (\(ouiTable.count) eigen OUI-prefixen)")
    }
}

// MARK: - Onthouden apparaten (~/Documents/Fusebox/network-devices.json)

struct NetDeviceStore: Codable {
    struct Known: Codable {
        var name: String?        // laatst gevonden naam
        var custom: String?      // eigen naam ("Rename…")
        var ip: String?, net: String?
        var first: Date, last: Date
    }
    var created = Date()
    var devices: [String: Known] = [:]   // MAC "AA:BB:…" → naam, eerst/laatst gezien

    static var url: URL {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0].appendingPathComponent("Fusebox/network-devices.json")
    }
    static func load() -> NetDeviceStore {
        let dec = JSONDecoder(); dec.dateDecodingStrategy = .iso8601
        return (try? Data(contentsOf: url)).flatMap { try? dec.decode(NetDeviceStore.self, from: $0) } ?? NetDeviceStore()
    }
    func save() {
        let enc = JSONEncoder(); enc.dateEncodingStrategy = .iso8601; enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try? FileManager.default.createDirectory(at: Self.url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try? enc.encode(self).write(to: Self.url, options: .atomic)
    }
}
