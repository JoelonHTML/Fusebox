import SwiftUI
import Combine

// MARK: - Network: internet (oordeel, Find the problem), snelheidstest, en de apparaten op je netwerk
//
// Meet alleen terwijl dit tabblad in beeld is en de app open staat. Cloudflare (snelheidstest, publiek IP) alleen na
// een tik. De modellen (InternetStats, NetScanner) zijn dezelfde als op de Mac.

struct NetworkScreen: View {
    var openSettings: () -> Void = {}
    @StateObject private var stats = InternetStats()
    @StateObject private var scanner = NetScanner()
    @Environment(\.scenePhase) private var phase
    @AppStorage("netAutoScan") private var autoScan = true
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @State private var visible = false
    @State private var lastScan = Date.distantPast
    @State private var started = false
    @Environment(\.theme) private var theme

    var body: some View {
        Screen {
            HStack(alignment: .firstTextBaseline) {
                PageTitle(title: L("Network"))
                Spacer()
                Button(action: openSettings) { Image(systemName: "gearshape").font(.system(size: 20)).foregroundStyle(theme.secondary) }
                    .buttonStyle(.plain).accessibilityLabel(Text("Settings"))
            }
            InternetCard(stats: stats)
            SpeedCard(stats: stats)
            DeviceList(scanner: scanner) { scan() }
        }
        .onAppear { visible = true; start() }
        .onDisappear { visible = false; stop() }
        // Alleen stoppen op de achtergrond: een iOS-vraag (zoals "Lokaal netwerk toestaan?") of het bedieningspaneel maakt de app
        // even inactief, en dan moet de scan gewoon doorlopen
        .onChange(of: phase) { if visible { if phase == .active { start() } else if phase == .background { stop() } } }
        .onChange(of: pingHost) { if visible { stats.start() } }
        // Wifi ↔ mobiel, of het netwerk valt weg: opnieuw meten op de nieuwe verbinding
        .onReceive(NotificationCenter.default.publisher(for: NetPath.changed).debounce(for: .seconds(1), scheduler: RunLoop.main)) { _ in
            guard visible, phase == .active, started else { return }
            if stats.conn?.kind != NetConnection.current()?.kind || stats.conn?.ipv4 != NetConnection.current()?.ipv4 { lastScan = .distantPast; start() }
        }
    }

    private func start() {
        Task {
            await NetPath.whenReady()
            guard visible, phase == .active else { return }
            started = true
            stats.start()
            // Niet bij elke terugkeer opnieuw scannen: pas als de vorige scan een minuut oud is, of geblokkeerd was
            // (net toestemming gegeven voor Lokaal netwerk)
            if autoScan, scanner.scanStart == nil, Date().timeIntervalSince(lastScan) > 60 || scanner.localBlocked { scan() }
        }
    }
    private func scan() { lastScan = Date(); scanner.scan() }
    private func stop() { stats.stop(); scanner.stop() }
}

// MARK: - Internet

private struct InternetCard: View {
    @ObservedObject var stats: InternetStats
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @AppStorage("netPanelOpen") private var open = false
    @Environment(\.theme) private var theme
    @Environment(\.onScreen) private var motion

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let c = stats.conn {
                let mobile = c.kind == NetConnection.mobile
                Label {
                    Text(L("This iPhone \(c.ipv4 ?? "–") on \(c.kindTitle)"))
                } icon: {
                    Image(systemName: mobile ? "antenna.radiowaves.left.and.right" : c.kind == "Wi-Fi" ? "wifi" : "cable.connector")
                }
                .font(theme.label(13)).foregroundStyle(theme.onRow(true))
                HStack(alignment: .bottom, spacing: 18) {
                    if !mobile { stat(.router, stats.router) }
                    if !pingHost.isEmpty { stat(.internet, stats.internet) }
                    Spacer(minLength: 0)
                    if stats.liveInternet.count > 1 || stats.liveRouter.count > 1 { graph.frame(maxWidth: 150) }
                }
                verdict(c)
                HStack {
                    Button { stats.findProblem() } label: {
                        NetPill(title: stats.finding == nil ? L("Find the problem") : L("Check again"), icon: "stethoscope")
                    }
                    .buttonStyle(.plain).disabled(stats.findBusy)
                    Spacer()
                    Button { withAnimation(motion ? .snappy(duration: 0.25) : nil) { open.toggle() } } label: {
                        Label { Text("Details") } icon: { Image(systemName: "chevron.down").rotationEffect(.degrees(open ? 180 : 0)) }
                            .font(theme.label(12)).foregroundStyle(theme.onRow(true))
                    }
                    .buttonStyle(.plain)
                }
                if open { details(c, mobile: mobile) }
            } else {
                Label(L("Not connected to a network"), systemImage: "wifi.slash").font(theme.label(14)).foregroundStyle(theme.onRow(true))
            }
        }
        .card(theme)
    }

    private func stat(_ term: NetTerm, _ s: PingStats?) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            InfoTap(term).font(theme.label(12)).foregroundStyle(theme.onRow(true))
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(verbatim: s.map { $0.got == 0 ? "–" : msText($0.avg) } ?? "…").font(theme.number(26)).foregroundStyle(theme.onRow())
                Text(verbatim: "ms").font(theme.label(12)).foregroundStyle(theme.onRow(true))
                if let s, s.loss > 0, s.got > 0 {
                    Text(L("\(Int((s.loss * 100).rounded()))% lost")).font(theme.label(11)).foregroundStyle(theme.tone(false))
                }
            }
        }
    }

    private var graph: some View {
        let inet = stats.liveInternet, router = stats.liveRouter
        return VStack(alignment: .trailing, spacing: 2) {
            SparkLine(main: inet.isEmpty ? router : inet, second: inet.isEmpty ? [] : router, points: InternetStats.graphPoints).frame(height: 38)
            InfoTap(.live, title: inet.isEmpty || router.isEmpty ? L("\(inet.isEmpty ? L("router") : "internet") · live") : L("internet — router ┄ · live"))
                .font(theme.label(10)).foregroundStyle(theme.onRow(true))
        }
    }

    @ViewBuilder private func verdict(_ c: NetConnection) -> some View {
        if stats.findBusy {
            HStack(spacing: 8) {
                ProgressView().controlSize(.small)
                Text("Checking Wi-Fi, router, DNS and internet… (about 6 s)").font(theme.label(13)).foregroundStyle(theme.onRow(true))
            }
        } else if let f = stats.finding {
            StatusLine(text: f.text, ok: f.ok)
            if !f.numbers.isEmpty {
                Text(verbatim: f.numbers).font(theme.label(11)).foregroundStyle(theme.onRow(true)).padding(.leading, 28).fixedSize(horizontal: false, vertical: true)
            }
        } else if stats.testing {
            Label(L("Testing the connection…"), systemImage: "hourglass").font(theme.label(13)).foregroundStyle(theme.onRow(true))
        } else {
            let v = quick(c)
            StatusLine(text: v.text, ok: v.ok)
        }
    }

    /// Snel oordeel uit de eerste tien pings. Op mobiele data is er geen router om te meten: dan alleen het internet.
    private func quick(_ c: NetConnection) -> (text: String, ok: Bool?) {
        let v = InternetStats.verdict(wired: c.kind != "Wi-Fi", rssi: nil, snr: nil, router: stats.router, internet: stats.internet,
                                      dnsMs: stats.dnsMs, internetCheck: !pingHost.isEmpty)
        guard c.kind == NetConnection.mobile else { return v }
        guard !pingHost.isEmpty else { return (L("The internet check is off in Settings"), nil) }
        return InternetStats.mobileVerdict(stats.internet) ?? v
    }

    @ViewBuilder private func details(_ c: NetConnection, mobile: Bool) -> some View {
        VStack(alignment: .leading, spacing: 7) {
            Divider().overlay(theme.line)
            Text("Connection").font(theme.label(12)).foregroundStyle(theme.onRow())
            KV(.type, c.kindTitle)
            KV(.ipv4, c.ipv4 ?? "–")
            KV(.ipv6, c.ipv6.first.map { $0 + (c.ipv6.count > 1 ? " (+\(c.ipv6.count - 1))" : "") } ?? L("none"))
            if !mobile { KV(.gateway, c.router.map(ipString) ?? L("unknown")) }
            KV(.ipv6Internet, c.ipv6Internet ? L("Yes") : L("No"))
            if let m = stats.metered { KV(.metered, m ? L("Yes") : L("No")) }
            Text("Quality (10 pings)").font(theme.label(12)).foregroundStyle(theme.onRow()).padding(.top, 6)
            if !mobile { KV(.router, c.router == nil ? "–" : statsText(stats.router)) }
            if !pingHost.isEmpty {
                KV(.internet, statsText(stats.internet) + (stats.internetViaTCP ? " (TCP)" : ""))
                KV(.dnsLookup, stats.dnsMs.map { "\(msText($0)) ms" } ?? "–")
            }
            HStack(spacing: 8) {
                Button { stats.start() } label: { NetPill(title: stats.testing ? L("Testing…") : L("Test again"), icon: "arrow.clockwise") }
                    .buttonStyle(.plain).disabled(stats.testing)
                if stats.publicIP == nil {
                    Button { stats.loadPublicIP() } label: { NetPill(title: stats.publicIPState ?? L("Show public IP"), icon: "globe") }.buttonStyle(.plain)
                }
            }
            .padding(.top, 4)
            if let ip = stats.publicIP {
                KV(.publicIP, ip.ip)
                if let isp = ip.isp { KV(L("Provider"), isp) }
                if let place = ip.place { KV(L("Location"), place) }
            }
        }
        .transition(.opacity)
    }

    private func statsText(_ s: PingStats?) -> String {
        guard let s else { return stats.testing ? "…" : "–" }
        guard s.got > 0, let avg = s.avg else { return L("no answer") }
        var t = "\(msText(avg)) ms"
        if let lo = s.min, let hi = s.max { t += " (\(msText(lo))–\(msText(hi)))" }
        if let j = s.jitter { t += L(" · jitter \(msText(j))") }
        return t + L(" · \(Int((s.loss * 100).rounded()))% lost")
    }
}

// MARK: - Snelheidstest en rapport

private struct SpeedCard: View {
    @ObservedObject var stats: InternetStats
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @Environment(\.theme) private var theme
    @State private var share: ShareFile?
    @State private var note: String?

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            CardHeader(title: L("Speed test"), icon: "speedometer")
            if let last = stats.history.first { result(last) } else if stats.speedPhase == nil {
                Text("No speed test yet").font(theme.label(13)).foregroundStyle(theme.onRow(true))
            }
            controls
            if stats.history.count > 1 { historyBars }
            Divider().overlay(theme.line)
            Button { shareReport() } label: { NetPill(title: L("Share report…"), icon: "square.and.arrow.up") }
                .buttonStyle(.plain).disabled(stats.testing)
            Text("A PDF with your connection, quality and speed tests, for a helpdesk or your provider. No network name, addresses or device names.")
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            if let note { Text(verbatim: note).font(theme.label(11)).foregroundStyle(theme.tone(false)) }
        }
        .card(theme, index: 1)
        .sheet(item: $share) { ShareSheet(url: $0.url) }
    }

    @ViewBuilder private func result(_ last: SpeedResult) -> some View {
        HStack(alignment: .top, spacing: 22) {
            big(.download, "↓ " + L("Download"), last.down)
            big(.upload, "↑ " + L("Upload"), last.up)
        }
        if let p = last.ping {
            HStack(spacing: 5) {
                InfoTap(.delay).foregroundStyle(theme.onRow(true))
                Text(verbatim: "\(msText(p)) ms").foregroundStyle(theme.onRow())
                if let b = last.loadedPing {
                    Text(verbatim: "·").foregroundStyle(theme.onRow(true))
                    InfoTap(.busyDelay, title: L("busy")).foregroundStyle(theme.onRow(true))
                    Text(verbatim: "\(msText(b)) ms").foregroundStyle(SpeedMath.loadVerdict(idle: p, loaded: b).ok == false ? theme.tone(false) : theme.onRow())
                }
            }
            .font(theme.label(13))
        }
        if let v = last.variation { KV(.stability, SpeedMath.stabilityText(v)) }
        if let idle = last.ping, let busy = last.loadedPing {
            let v = SpeedMath.loadVerdict(idle: idle, loaded: busy)
            StatusLine(text: v.text, ok: v.ok)
        }
        Text(verbatim: ([last.date.formatted(date: .abbreviated, time: .shortened), kindName(last.kind)] + [netName(last.net)].compactMap { $0 }).joined(separator: " · "))
            .font(theme.label(11)).foregroundStyle(theme.onRow(true))
        if let c = compare { Text(verbatim: c).font(theme.label(12)).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true) }
    }

    private func big(_ term: NetTerm, _ title: String, _ mbit: Double) -> some View {
        let parts = mbitText(mbit).split(separator: " ", maxSplits: 1).map(String.init)
        return VStack(alignment: .leading, spacing: 0) {
            InfoTap(term, title: title).font(theme.label(12)).foregroundStyle(theme.onRow(true))
            HStack(alignment: .firstTextBaseline, spacing: 4) {
                Text(verbatim: parts.first ?? "").font(theme.number(26)).foregroundStyle(theme.onRow())
                Text(verbatim: parts.count > 1 ? parts[1] : "").font(theme.label(12)).foregroundStyle(theme.onRow(true))
            }
        }
    }

    @ViewBuilder private var controls: some View {
        if let phase = stats.speedPhase {
            VStack(alignment: .leading, spacing: 6) {
                HStack(spacing: 8) {
                    if let live = stats.speedLive { Text(verbatim: mbitText(live)).font(theme.number(20)).foregroundStyle(theme.onRow()) } else { ProgressView().controlSize(.small) }
                    Spacer(minLength: 4)
                    Button { stats.cancelSpeedTest() } label: { NetPill(title: L("Cancel"), icon: "xmark") }.buttonStyle(.plain)
                }
                ProgressView(value: stats.speedProgress).tint(theme.onRowAccent)
                Text(verbatim: phase).font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            }
        } else if let kind = stats.speedConfirm {
            Text("This connection is metered (mobile data, iPhone hotspot or Low Data Mode). The \(kind.title.lowercased()) test uses \(usage(kind)) of data.")
                .font(theme.label(12)).foregroundStyle(theme.tone(false)).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 8) {
                Button { stats.runSpeedTest(kind) } label: { NetPill(title: L("Run anyway"), icon: "speedometer") }.buttonStyle(.plain)
                Button { stats.dismissSpeedConfirm() } label: { NetPill(title: L("Not now"), icon: "xmark") }.buttonStyle(.plain)
            }
        } else {
            HStack(spacing: 8) {
                Button { stats.requestSpeedTest(.quick) } label: { NetPill(title: L("Quick"), icon: "hare") }.buttonStyle(.plain)
                Button { stats.requestSpeedTest(.full) } label: { NetPill(title: L("Full"), icon: "gauge.with.dots.needle.67percent") }.buttonStyle(.plain)
                InfoTap(.kinds, title: L("What's the difference?")).font(theme.label(11)).foregroundStyle(theme.onRow(true))
            }
            Text("Quick uses \(usage(.quick)), Full \(usage(.full)) (via Cloudflare)").font(theme.label(11)).foregroundStyle(theme.onRow(true))
                .fixedSize(horizontal: false, vertical: true)
        }
        if let e = stats.speedError { Text(verbatim: e).font(theme.label(11)).foregroundStyle(theme.tone(false)).fixedSize(horizontal: false, vertical: true) }
    }

    /// Laatste 10 metingen als staafjes (download), oudste links. Volledig = brede staaf.
    private var historyBars: some View {
        let list = Array(stats.history.prefix(10).reversed()), top = max(list.map(\.down).max() ?? 1, 1)
        return HStack(alignment: .bottom, spacing: 4) {
            ForEach(Array(list.enumerated()), id: \.offset) { _, r in
                Capsule().fill(theme.onRowAccent.opacity(r == stats.history.first ? 1 : 0.45))
                    .frame(width: r.kind == .full ? 10 : 6, height: max(3, 26 * r.down / top))
            }
            Text("last \(list.count) · wide = Full").font(theme.label(10)).foregroundStyle(theme.onRow(true)).padding(.leading, 4)
        }
        .frame(height: 28, alignment: .bottom)
        .accessibilityHidden(true)
    }

    private func usage(_ kind: SpeedKind) -> String {
        SpeedMath.estimate(kind, last: stats.history.first).map { L("about \(bytes(Double($0)))") } ?? L("at most \(bytes(Double(kind.cap * 2)))")
    }
    private func kindName(_ k: SpeedKind?) -> String { k.map { L("\($0.title) test") } ?? L("single connection") }
    private func netName(_ n: String?) -> String? { n == "wifi" ? "Wi-Fi" : n == "mobile" ? L("Mobile data") : nil }

    /// Wifi tegen mobiel, uit de laatste test van elk: "Wi-Fi is 3× faster than mobile data here".
    private var compare: String? {
        guard let w = stats.history.first(where: { $0.net == "wifi" })?.down, let m = stats.history.first(where: { $0.net == "mobile" })?.down,
              w > 0, m > 0 else { return nil }
        let ratio = max(w, m) / min(w, m)
        guard ratio >= 1.5 else { return L("Wi-Fi and mobile data are about equally fast here") }
        let times = ratio >= 10 ? "\(Int(ratio.rounded()))" : one(ratio)
        return w > m ? L("Wi-Fi was \(times)× faster than mobile data in your last tests") : L("Mobile data was \(times)× faster than Wi-Fi in your last tests")
    }

    private func shareReport() {
        note = nil
        let url = FileManager.default.temporaryDirectory.appendingPathComponent("fusebox-network-report.pdf")
        if stats.report(internetCheck: !pingHost.isEmpty).writePDF(to: url) { share = ShareFile(url: url) } else { note = L("Creating the PDF failed") }
    }
}

// MARK: - Apparaten op je netwerk

private enum DeviceFilter: String, CaseIterable, Identifiable {
    case all, online, new, offline
    var id: String { rawValue }
    var title: String {
        switch self { case .all: L("All"); case .online: L("Online"); case .new: L("New"); case .offline: L("Offline") }
    }
    func matches(_ d: NetDevice) -> Bool {
        switch self { case .all: true; case .online: d.online; case .new: d.isNew; case .offline: !d.online }
    }
}

private struct DeviceList: View {
    @ObservedObject var scanner: NetScanner
    let scan: () -> Void
    @Environment(\.theme) private var theme
    @AppStorage("netAutoScan") private var autoScan = true
    @State private var filter = DeviceFilter.all
    @State private var open: String?

    /// Online eerst, daarbinnen op IP (zoals de Mac standaard sorteert).
    private var devices: [NetDevice] {
        scanner.devices.filter(filter.matches).sorted { a, b in a.online != b.online ? a.online : a.ip < b.ip }
    }

    var body: some View {
        let all = scanner.devices, list = devices, scanning = scanner.scanStart != nil
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 8) {
                Text("Devices").font(theme.label(17)).foregroundStyle(theme.text)
                if !all.isEmpty { Text(verbatim: count(all)).font(theme.label(12)).foregroundStyle(theme.secondary).lineLimit(1) }
                Spacer(minLength: 4)
                Menu {
                    Picker(selection: $filter) { ForEach(DeviceFilter.allCases) { Text(verbatim: $0.title).tag($0) } } label: { EmptyView() }
                } label: {
                    NetPill(title: filter.title, icon: "line.3.horizontal.decrease", onPage: true)
                }
                Button { scanning ? scanner.stop() : scan() } label: {
                    NetPill(title: scanning ? L("Stop") : L("Scan"), icon: scanning ? "stop.fill" : "arrow.clockwise", onPage: true)
                }
                .buttonStyle(.plain)
            }
            if let start = scanner.scanStart {
                HStack(spacing: 8) {
                    ProgressView(timerInterval: start...start.addingTimeInterval(scanner.expected), countsDown: false) { EmptyView() } currentValueLabel: { EmptyView() }
                        .tint(theme.accent)
                    Text(verbatim: scanner.phase).font(theme.label(11)).foregroundStyle(theme.secondary).fixedSize()
                }
            }
            if scanner.localBlocked { blocked }
            if list.isEmpty {
                Text(verbatim: empty(scanning)).font(theme.label(13)).foregroundStyle(theme.secondary)
                    .frame(maxWidth: .infinity).padding(.vertical, 24)
            } else {
                let tight = theme.rowStyle == .lines || theme.rowStyle == .plain
                VStack(spacing: tight ? 0 : 6) {
                    ForEach(Array(list.enumerated()), id: \.element.id) { i, d in
                        Button { open = d.id } label: { DeviceRow(device: d).themedRow(theme, index: i + 2) }.buttonStyle(.plain)
                    }
                }
            }
            Text("Fusebox shows the name, address and kind of each device. An iPhone app can't see hardware addresses, so the maker isn't shown.")
                .font(theme.label(11)).foregroundStyle(theme.secondary).fixedSize(horizontal: false, vertical: true).padding(.top, 4)
        }
        .padding(.top, 6)
        .sheet(item: Binding(get: { open.map { OpenDevice(id: $0) } }, set: { open = $0?.id })) { o in
            DeviceSheet(scanner: scanner, id: o.id)
        }
    }

    private struct OpenDevice: Identifiable { let id: String }

    private func count(_ all: [NetDevice]) -> String {
        let on = all.filter(\.online).count, new = all.filter(\.isNew).count, off = all.count - on
        return L("\(on) online") + (new > 0 ? L(" · \(new) new") : "") + (off > 0 ? L(" · \(off) offline") : "")
    }

    private func empty(_ scanning: Bool) -> String {
        if scanner.info == nil { return NetPath.cellular ? L("Connect to Wi-Fi to see the devices on your network") : autoScan ? L("Not connected to a network") : L("Press Scan to look for devices") }
        if scanning { return L("Looking for devices…") }
        if filter != .all { return L("No \(filter.title.lowercased()) devices") }
        return L("No devices found")
    }

    private var blocked: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Image(systemName: "lock.shield")
            Text("iOS is blocking the scan. Allow Local Network for Fusebox in Settings, then press Scan.").fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 4)
            Button("Open") { if let u = URL(string: UIApplication.openSettingsURLString) { UIApplication.shared.open(u) } }
                .buttonStyle(.plain).foregroundStyle(theme.accent)
        }
        .font(theme.label(12)).foregroundStyle(theme.warn)
    }
}

private struct DeviceRow: View {
    let device: NetDevice
    @Environment(\.theme) private var theme
    var body: some View {
        HStack(spacing: 10) {
            Image(systemName: device.kind.icon).font(.system(size: 15)).frame(width: 22).foregroundStyle(theme.onRow(true))
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: 6) {
                    Text(verbatim: device.name).font(theme.label(14)).foregroundStyle(theme.onRow()).lineLimit(1)
                    if device.isMe { if device.custom != nil { tag(L("This iPhone")) } } else if device.isRouter, device.name != L("Router") { tag(L("Router")) }
                    if device.isNew {
                        Text("New").font(theme.label(10)).padding(.horizontal, 5).padding(.vertical, 1)
                            .foregroundStyle(theme.onRowAccent).background(theme.onRowAccent.opacity(0.18), in: .capsule)
                    }
                }
                Text(verbatim: sub).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1)
            }
            Spacer(minLength: 6)
            if device.online {
                if !device.isMe {
                    Text(verbatim: msText(device.ms)).font(theme.number(14)).foregroundStyle((device.loss ?? 0) > 0 && !theme.onBlocks ? theme.warn : theme.onRow())
                    Text(verbatim: "ms").font(theme.label(10)).foregroundStyle(theme.onRow(true))
                }
            } else {
                Text("off").font(theme.label(12)).foregroundStyle(theme.onRow(true))
            }
            Image(systemName: "chevron.right").font(.system(size: 11, weight: .semibold)).foregroundStyle(theme.onRow(true).opacity(0.6))
        }
        .opacity(device.online ? 1 : 0.5)
        .contentShape(.rect)
    }

    private var sub: String {
        var parts = [device.ip == 0 ? "–" : ipString(device.ip), device.kind.title]
        if !device.online, let last = device.lastSeen { parts.append(L("last seen \(agoText(last))")) }
        return parts.joined(separator: " · ")
    }
    private func tag(_ t: String) -> some View { Text(verbatim: t).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1).fixedSize() }
}

// MARK: - Details van één apparaat (blad van onderen)

private struct DeviceSheet: View {
    @ObservedObject var scanner: NetScanner
    let id: String
    @Environment(\.theme) private var theme
    @Environment(\.dismiss) private var dismiss
    @State private var renaming = false
    @State private var newName = ""

    var body: some View {
        ScrollView {
            if let d = scanner.devices.first(where: { $0.id == id }) { content(d) } else {
                Text("This device is no longer in the list").font(theme.label(13)).foregroundStyle(theme.secondary).padding(30)
            }
        }
        .background(theme.page.ignoresSafeArea())
        .presentationDetents([.medium, .large])
        .presentationDragIndicator(.visible)
    }

    private func content(_ d: NetDevice) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 10) {
                Image(systemName: d.kind.icon).font(.system(size: 22)).foregroundStyle(theme.accent)
                Text(verbatim: d.name).font(theme.label(20)).foregroundStyle(theme.text)
                Spacer()
            }
            .padding(.bottom, 4)
            if let first = d.names.first {
                let also = d.names.dropFirst().map { "\($0.name) (\(sourceTitle($0.source)))" }.joined(separator: ", ")
                line("tag", (first.source == "Your name" ? L("Name from you") : L("Name from \(sourceTitle(first.source))")) + (also.isEmpty ? "" : L(" · also: ") + also))
            } else {
                line("tag", L("No name found on the network"))
            }
            if d.ip != 0 { line("network", d.online ? L("IP address \(ipString(d.ip))") : L("Last IP address \(ipString(d.ip))")) }
            if let model = d.model { line("info.circle", L("Model: \(model)")) }
            line(d.kind.icon, L("Looks like: \(d.kind.title.lowercased())"))
            if !d.services.isEmpty { chips("dot.radiowaves.left.and.right", L("Offers"), Set(d.services.map(NetScanner.serviceTitle)).sorted().map { ($0, nil) }) }
            if let ports = d.ports {
                if ports.isEmpty { line("door.left.hand.closed", L("No common ports open")) } else {
                    // Webpoorten zijn tikbaar: opent de pagina van het apparaat (printer, NAS, router) in Safari
                    chips("door.left.hand.open", L("Open ports"), ports.map { ("\(NetProbe.portName($0)) \($0)", NetProbe.webURL(d.ip, $0)) })
                }
            }
            if d.online, !d.isMe { line("timer", latency(d)) }
            if let f = d.firstSeen { line("clock", L("First seen \(f.formatted(date: .abbreviated, time: .shortened))")) }
            if let l = d.lastSeen { line("clock.arrow.circlepath", L("Last seen \(l.formatted(date: .abbreviated, time: .shortened))")) }
            NetFlow(spacing: 8) {
                if d.online, !d.isMe, d.ip != 0 {
                    Button { scanner.checkPorts(d.ip) } label: {
                        NetPill(title: d.checkingPorts ? L("Checking…") : d.ports == nil ? L("Check ports") : L("Check again"), icon: "door.left.hand.open", onPage: true)
                    }
                    .buttonStyle(.plain).disabled(d.checkingPorts)
                }
                if d.key != nil {
                    Button { newName = d.custom ?? d.name; renaming = true } label: { NetPill(title: L("Rename…"), icon: "pencil", onPage: true) }.buttonStyle(.plain)
                    if !d.isMe {
                        Button { scanner.forget(d); dismiss() } label: { NetPill(title: L("Forget"), icon: "trash", onPage: true) }.buttonStyle(.plain)
                    }
                }
            }
            .padding(.top, 6)
            Text("Check ports tries 15 common ports (web, file sharing, printing, …). Rename and Forget only change Fusebox's own list on this iPhone.")
                .font(theme.label(11)).foregroundStyle(theme.secondary).fixedSize(horizontal: false, vertical: true)
        }
        .font(theme.label(13)).foregroundStyle(theme.secondary)
        .padding(20).frame(maxWidth: .infinity, alignment: .leading)
        .alert("Rename device", isPresented: $renaming) {
            TextField("Name", text: $newName)
            Button("Save") { scanner.rename(d, to: newName) }
            Button("Cancel", role: .cancel) {}
        } message: {
            Text("Leave empty to use the name Fusebox finds itself.")
        }
    }

    private func sourceTitle(_ s: String) -> String {
        switch s { case "Remembered": L("Remembered"); case "This Mac": L("This iPhone"); case "Your name": L("Your name"); default: s }
    }

    private func latency(_ d: NetDevice) -> String {
        guard let ms = d.ms else { return L("Doesn't answer (many phones and TVs sleep or ignore pings; that's normal)") }
        if d.viaTCP { return L("Answers in \(msText(ms)) ms (via a TCP connection; it ignores pings)") }
        let lost = Int(((d.loss ?? 0) * 3).rounded())
        return L("Answers in \(msText(ms)) ms · ") + (lost == 0 ? L("no packet loss (3 pings)") : L("\(lost) of 3 pings lost"))
    }

    private func line(_ icon: String, _ text: String) -> some View {
        Label { Text(verbatim: text).fixedSize(horizontal: false, vertical: true) } icon: { Image(systemName: icon).frame(width: 20) }
    }

    private func chips(_ icon: String, _ title: String, _ items: [(text: String, url: URL?)]) -> some View {
        Label {
            NetFlow(spacing: 5) {
                Text(verbatim: title + ":")
                ForEach(items, id: \.text) { item in
                    let chip = HStack(spacing: 3) {
                        Text(verbatim: item.text)
                        if item.url != nil { Image(systemName: "arrow.up.right").font(.system(size: 9, weight: .semibold)) }
                    }
                    .font(theme.label(11)).padding(.horizontal, 6).padding(.vertical, 2)
                    .foregroundStyle(theme.accent).background(theme.accent.opacity(0.15), in: .rect(cornerRadius: 5))
                    if let url = item.url { Link(destination: url) { chip } } else { chip }
                }
            }
        } icon: { Image(systemName: icon).frame(width: 20) }
    }
}
