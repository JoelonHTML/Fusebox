import SwiftUI
import UniformTypeIdentifiers

// MARK: - Netwerk-kaarten: Verbinding, Wi-Fi (of kabel) en Snelheidstest, boven de LAN-scanner
//
// Op elke kaart alleen de kerngetallen; de rest onder "Details" (één knop voor alle kaarten, onthouden). Zeldzame acties in ⋯.
// Elke waarde heeft een label met uitleg (zweven of klikken), de ⓘ in de kop toont alle uitleg van die kaart.
// Geen eigen timers: alles komt uit InternetStats.

/// Uitleg per waarde, op één plek: labels in de app, de ⓘ per kaart en het netwerkrapport (PDF) gebruiken deze teksten.
enum NetTerm: String, CaseIterable, Identifiable {
    case router, internet, live, jitter, lost, p95, dnsLookup
    case type, vpn, metered, ipv4, ipv6, gateway, dnsServers, ipv6Internet, publicIP
    case signal, noise, linkSpeed, channel, standard, security, cable, adapterMax, duplex
    case download, upload, delay, busyDelay, stability, kinds
    var id: String { rawValue }

    var title: String {
        switch self {
        case .router: L("Router")
        case .internet: "Internet"
        case .live: L("Live graph")
        case .jitter: L("Jitter")
        case .lost: L("Lost")
        case .p95: "95%"
        case .dnsLookup: L("DNS lookup")
        case .type: L("Type")
        case .vpn: "VPN"
        case .metered: L("Metered")
        case .ipv4: "IPv4"
        case .ipv6: "IPv6"
        case .gateway: L("Router address")
        case .dnsServers: "DNS"
        case .ipv6Internet: L("IPv6 internet")
        case .publicIP: L("Public IP")
        case .signal: L("Signal")
        case .noise: L("Noise and SNR")
        case .linkSpeed: L("Link speed")
        case .channel: L("Channel")
        case .standard: L("Standard")
        case .security: L("Security")
        case .cable: L("Cable")
        case .adapterMax: L("Adapter can do")
        case .duplex: L("Duplex")
        case .download: L("Download")
        case .upload: L("Upload")
        case .delay: L("Delay")
        case .busyDelay: L("Busy delay")
        case .stability: L("Stability")
        case .kinds: L("Quick and Full")
        }
    }

    var text: String {
        switch self {
        case .router: L("How fast your router answers: this is your own Wi-Fi or cable. Below 10 ms is great; above 30 ms points to Wi-Fi interference or a busy router.")
        case .internet: L("How fast a server on the internet answers, through your provider. Below 20 ms is very good and below 50 ms fine; above 100 ms you notice it in calls and games.")
        case .live: L("Reply times of the internet (line) and your router (thin line) over the last two minutes. Spikes are moments of delay; a flat, low line is best.")
        case .jitter: L("How much the delay changes from one message to the next. Low jitter means a steady line. Above about 30 ms, calls and video can stutter.")
        case .lost: L("The share of messages that never came back. 0% is normal. Even 1 or 2% makes calls break up and pages load slowly.")
        case .p95: L("95 out of 100 replies were at least this fast. It shows the slow moments that an average hides.")
        case .dnsLookup: L("How long it takes to look up a name like apple.com. Below 50 ms is good; above 150 ms websites are slow to start loading.")
        case .type: L("How this Mac is connected: Wi-Fi, a cable (Ethernet) or a VPN. A cable is usually faster and steadier than Wi-Fi.")
        case .vpn: L("On means (some of) your traffic goes through an encrypted tunnel to a VPN server. That adds a little delay and can lower the speed.")
        case .metered: L("Yes on mobile data, an iPhone hotspot or with Low Data Mode. macOS and apps then hold back big downloads and backups.")
        case .ipv4: L("This Mac's address on your own network, handed out by the router. The internet doesn't see it.")
        case .ipv6: L("This Mac's newer, longer address, if your network supports IPv6. \"none\" is common and not a problem.")
        case .gateway: L("The address of your router, the box between your network and the internet. Open it in a browser to reach the router's settings.")
        case .dnsServers: L("The servers that turn names like apple.com into addresses. Usually your router or provider; you can change them in System Settings › Network.")
        case .ipv6Internet: L("Whether you can reach the internet over IPv6. Nice to have; without it everything still works over IPv4.")
        case .publicIP: L("The address the internet sees for your whole home or office, given by your provider. Fusebox only asks Cloudflare and the IP registry (RIPE) when you click.")
        case .signal: L("Wi-Fi signal strength in dBm; closer to 0 is stronger. −50 is excellent, −60 good, below −70 weak.")
        case .noise: L("Noise is background radio interference. SNR is the gap between signal and noise: above 25 dB is good, below 20 dB causes errors and slowdowns.")
        case .linkSpeed: L("The speed between this Mac and the router (or switch), not your internet speed. It's usually higher than a speed test shows; on Wi-Fi it drops when the signal is weak.")
        case .channel: L("The radio channel, band and width. 5 and 6 GHz are faster and less crowded; 2.4 GHz reaches further but is often busy. Wider channels are faster.")
        case .standard: L("The Wi-Fi generation in use. Wi-Fi 6 and 7 are faster and cope better with many devices than Wi-Fi 4 or 5.")
        case .cable: L("A cable has no signal or interference to worry about, so it's usually faster and steadier than Wi-Fi. The link speed shows what the cable and port can do.")
        case .security: L("How this Wi-Fi network is protected. WPA3 is the strongest and WPA2 is fine. With WEP, the first WPA or no security at all, others can listen in: change it in the router's settings.")
        case .adapterMax: L("The fastest speed this network adapter supports. If the link speed is lower, the cable or the port on the router or switch is holding it back. 100 Mbit/s on a 1 Gbit/s adapter usually means a damaged cable.")
        case .duplex: L("Full duplex means both sides can send at the same time, which is normal. Half duplex is slow and points to a bad cable or a very old hub.")
        case .download: L("How much data per second comes in: streaming, websites, downloads. Compare it with your subscription; over Wi-Fi you often get less than over a cable.")
        case .upload: L("How much data per second goes out: video calls, sending files, backups. Usually lower than download.")
        case .delay: L("The delay to the test server while the line is quiet. Below 20 ms is very good, below 50 ms fine.")
        case .busyDelay: L("The delay while the line is full with a download or upload. If it rises a lot compared with the normal delay, calls and games suffer whenever someone downloads. A router with smart queueing (SQM) fixes this.")
        case .stability: L("How much the speed changed from second to second during the test. ±10% or less is steady; above ±25% the line is unsteady, often from Wi-Fi interference or a busy network.")
        case .kinds: L("Quick takes about 10 seconds and gives a good estimate. Full takes about 30 seconds and also measures top speed, busy delay and stability.")
        }
    }

    static let connection: [NetTerm] = [.router, .internet, .live, .jitter, .lost, .dnsLookup, .type, .vpn, .metered, .ipv4, .ipv6, .gateway, .dnsServers, .ipv6Internet, .publicIP]
    static let wifi: [NetTerm] = [.signal, .noise, .linkSpeed, .channel, .standard, .security]
    static let wired: [NetTerm] = [.cable, .linkSpeed, .adapterMax, .duplex]
    static let speed: [NetTerm] = [.download, .upload, .delay, .busyDelay, .stability, .kinds]
    #if os(macOS)
    static let report: [NetTerm] = [.router, .internet, .jitter, .lost, .p95, .dnsLookup, .download, .upload, .delay, .busyDelay, .stability, .signal, .noise, .linkSpeed]
    #else   // iPhone: geen wifi-signaal, ruis of linksnelheid (iOS geeft die niet)
    static let report: [NetTerm] = [.router, .internet, .jitter, .lost, .p95, .dnsLookup, .download, .upload, .delay, .busyDelay, .stability]
    #endif
}

// MARK: Uitleg-bouwstenen

/// Label met uitleg: na 0,4 s zweven een klein popover (stippellijn als hint), klikken zet het vast (ook voor toetsenbord).
struct TermLabel: View {
    let label: String, item: (title: String, text: String)
    init(term: NetTerm, title: String? = nil) { label = title ?? term.title; item = (term.title, term.text) }
    /// Zelfde label voor een waarde die geen netwerkterm is ("More details" op Live).
    init(_ title: String, hint: String) { label = title; item = (title, hint) }
    @Environment(\.theme) private var theme
    @State private var hover = false
    @State private var show = false
    @State private var pinned = false

    var body: some View {
        Button { pinned.toggle(); show = pinned } label: {
            Text(label).underline(hover, pattern: .dot, color: theme.onRow(true).opacity(0.7))
        }
        .buttonStyle(.plain)
        .onHover { inside in
            hover = inside
            if inside {
                Task { try? await Task.sleep(for: .milliseconds(400)); if hover && !show { show = true } }
            } else if !pinned { show = false }
        }
        .onChange(of: show) { if !show { pinned = false } }
        .popover(isPresented: $show, arrowEdge: .bottom) { TermsPopover(items: [item]) }
        .accessibilityHint(item.text)
    }
}

/// Inhoud van de uitleg-popovers: één term (label) of alle termen van een kaart (ⓘ).
struct TermsPopover: View {
    var title: String? = nil
    let items: [(title: String, text: String)]
    init(title: String? = nil, terms: [NetTerm], extra: [(title: String, text: String)] = []) {
        self.title = title; items = terms.map { ($0.title, $0.text) } + extra
    }
    init(items: [(title: String, text: String)]) { self.items = items }
    @Environment(\.theme) private var theme

    var body: some View {
        let body = theme.font.font(11.5 * theme.scale, weight: .regular, style: theme.design)
        let list = VStack(alignment: .leading, spacing: 10) {
            if let title { Text(title).font(theme.label(13)).foregroundStyle(theme.text) }
            ForEach(items.indices, id: \.self) { i in
                VStack(alignment: .leading, spacing: 2) {
                    Text(items[i].title).font(theme.label(11.5)).foregroundStyle(theme.text)
                    Text(items[i].text).font(body).foregroundStyle(theme.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .padding(14).frame(width: 300, alignment: .leading)
        Group {
            if items.count > 3 { ScrollView { list }.frame(maxHeight: 460) } else { list }
        }
        .presentationBackground(theme.bg.map { AnyShapeStyle($0) } ?? AnyShapeStyle(.regularMaterial))
    }
}

#if os(macOS)   // de kaarten hieronder zijn de Mac-indeling; de iPhone (ios/) deelt alleen de uitleg hierboven
/// Kaartvorm per thema. Bij lijnen/kaal (Instrument, Minimal, Terminal) een eigen zacht kaartvlak; anders de themarij.
private struct NetCardShape: ViewModifier {
    let theme: Theme, index: Int
    func body(content: Content) -> some View {
        switch theme.rowStyle {
        case .lines, .plain:
            content.padding(12)
                .background(theme.card, in: .rect(cornerRadius: 12))
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(theme.line, lineWidth: 1))
        default:
            content.padding(.horizontal, 2).padding(.vertical, 4).themedRow(theme, index: index)
        }
    }
}

// MARK: - De drie kaarten

struct InternetCards: View {
    let stacked: Bool
    @StateObject private var stats = InternetStats()
    @Environment(\.theme) private var theme
    @AppStorage("pingHost") private var pingHost = "1.1.1.1"
    @AppStorage("netFindNow") private var findNow = false   // gezet door "Network" in het menubalkpaneel
    @AppStorage("netDetails") private var detailsOpen = false   // één knop klapt alle kaarten tegelijk open
    @AppStorage("speedAllNets") private var allNets = false   // standaard alleen tests van dit netwerk
    @State private var confirmClear = false
    @State private var reportNote: String?

    var body: some View {
        Group {
            if stacked {
                VStack(spacing: 10) { connection; link; speed }
            } else {
                HStack(alignment: .top, spacing: 10) {
                    connection; link; speed
                }
                .fixedSize(horizontal: false, vertical: true)   // even hoge kaarten
            }
        }
        .onAppear { stats.start(); findIfAsked() }
        .onDisappear { stats.stop() }
        .onChange(of: pingHost) { stats.start() }
        .onChange(of: findNow) { findIfAsked() }
    }

    private func findIfAsked() {
        guard findNow else { return }
        findNow = false
        stats.findProblem()
    }

    private var blocks: Bool { theme.rowStyle == .blocks }
    private func tone(_ ok: Bool?) -> Color { blocks ? .white : ok == false ? theme.warn : ok == true ? theme.good : theme.onRow(true) }
    private func icon(_ ok: Bool?) -> String { ok == false ? "exclamationmark.triangle.fill" : ok == true ? "checkmark.circle.fill" : "info.circle.fill" }

    // MARK: Verbinding

    private var connection: some View {
        card(L("Connection"), icon: stats.conn?.vpn == true ? "lock.shield" : "network", index: 0, terms: NetTerm.connection, open: $detailsOpen) {
            Button { stats.start() } label: { Label(stats.testing ? L("Testing…") : L("Test again"), systemImage: "arrow.clockwise") }
                .disabled(stats.testing || stats.conn == nil)
            Button { detailsOpen = true; stats.loadPublicIP() } label: { Label(L("Show public IP"), systemImage: "globe") }
                .disabled(stats.publicIP != nil).help("Asks Cloudflare which address you appear as, and the public IP registry (RIPE) which provider that address belongs to")
            Divider()
            reportItem
        } face: {
            if stats.conn != nil {
                verdict
                HStack(alignment: .top, spacing: 18) {
                    pingStat(.router, stats.router)
                    if !pingHost.isEmpty { pingStat(.internet, stats.internet) }
                    graph.frame(minWidth: 50, maxWidth: .infinity)
                }
                findLine
            } else {
                status("wifi.slash", L("Not connected to a network"), ok: false)
            }
            if let reportNote { Text(reportNote).font(theme.label(10)).foregroundStyle(tone(false)) }
        } details: {
            if let c = stats.conn { route(c) }
            if let c = stats.conn {
                subhead(L("This Mac"))
                kv(.type, c.kind == "VPN" ? "VPN" : "\(c.kind) (\(c.iface))")
                kv(.vpn, c.vpn ? L("VPN on") : L("Off"))
                if let m = stats.metered { kv(.metered, m ? L("Yes: macOS and apps may hold back big downloads and backups") : L("No")) }
                kv(.ipv4, c.ipv4 ?? "–")
                kv(.ipv6, c.ipv6.first.map { $0 + (c.ipv6.count > 1 ? " (+\(c.ipv6.count - 1))" : "") } ?? L("none"))
                kv(.dnsServers, c.dns.isEmpty ? L("unknown") : c.dns.prefix(3).joined(separator: ", "))
                kv(.ipv6Internet, c.ipv6Internet ? L("Yes") : L("No"))
            }
            subhead(L("Quality (10 pings)"))
            kv(.router, statsText(stats.router))
            if !pingHost.isEmpty {
                kv(.internet, statsText(stats.internet) + (stats.internetViaTCP ? " (TCP)" : ""))
                kv(.dnsLookup, stats.dnsMs.map { "\(msText($0)) ms" } ?? "–")
            } else {
                kv(.internet, L("off (Settings)"))
            }
        }
    }

    /// Waar je verbinding vandaan komt: Wi-Fi of kabel → router (merk, adres) → provider (naam, publiek adres, plaats).
    @ViewBuilder private func route(_ c: NetConnection) -> some View {
        subhead(L("Where it comes from"))
        if let w = stats.wifi {
            kv(.type, ["Wi-Fi", w.band, w.channel.map { L("channel \($0)") }, w.security].compactMap { $0 }.joined(separator: " · "), title: L("Via"))
        } else {
            kv(.type, c.kind == "Ethernet" ? L("Cable") : c.kind, title: L("Via"))
        }
        kv(.gateway, [stats.routerMaker, c.router.map(ipString)].compactMap { $0 }.joined(separator: " · ").ifEmpty(L("unknown")), title: L("Router"))
        if let ip = stats.publicIP {
            kv(.publicIP, ip.isp ?? L("unknown provider"), title: L("Provider"))
            kv(.publicIP, ip.ip)
            if let place = ip.place { kv(.publicIP, place, title: L("Location")) }
        } else if let state = stats.publicIPState {
            status("globe", state, ok: nil).padding(.top, 2)
        } else {
            Button { stats.loadPublicIP() } label: { NetPill(title: L("Show provider and public IP"), icon: "globe") }
                .buttonStyle(.plain).help("Asks Cloudflare which address you appear as, and the public IP registry (RIPE) which provider that address belongs to")
        }
    }

    @ViewBuilder private var verdict: some View {
        if stats.testing {
            status("hourglass", L("Testing the connection…"), ok: nil)
        } else if stats.finding == nil && !stats.findBusy, stats.router != nil || stats.internet != nil {
            // De grondige controle (hieronder) vervangt het snelle oordeel: geen twee tegenstrijdige regels
            let v = InternetStats.verdict(wired: stats.conn?.kind != "Wi-Fi", rssi: stats.wifi?.rssi, snr: stats.wifi?.snr,
                                          router: stats.router, internet: stats.internet, dnsMs: stats.dnsMs, internetCheck: !pingHost.isEmpty)
            status(icon(v.ok), v.ok == true ? L("All good") : v.text, ok: v.ok).help(v.text)
        }
    }

    private func pingStat(_ term: NetTerm, _ s: PingStats?) -> some View {
        VStack(alignment: .leading, spacing: 1) {
            TermLabel(term: term).font(theme.label(10)).foregroundStyle(theme.onRow(true))
            big(s == nil ? "…" : s!.got == 0 ? "–" : msText(s?.avg), "ms")
            if let s, s.loss > 0 { Text("\(Int((s.loss * 100).rounded()))% lost").font(theme.label(10)).foregroundStyle(tone(false)) }
        }
        .help(statsText(s))
    }

    /// Live: internet (lijn) en router (dunne lijn), laatste ~2 minuten.
    private var graph: some View {
        let inet = stats.liveInternet, router = stats.liveRouter
        let main = pingHost.isEmpty || inet.isEmpty ? router : inet
        let n = max(main.count, 2)
        return VStack(alignment: .leading, spacing: 2) {
            SparkView(values: main, second: main == router ? [] : router, top: max((main + router).max() ?? 1, 5) * 1.2,
                      fill: Double(n - 1) / Double(InternetStats.graphPoints - 1), glides: false)
                .frame(height: 34)
            TermLabel(term: .live, title: main == router ? L("router · live") : L("internet — router ┄ · live"))
                .font(theme.label(9)).foregroundStyle(theme.onRow(true)).lineLimit(1).minimumScaleFactor(0.7)
                .frame(maxWidth: .infinity, alignment: .trailing)
        }
        .help("Reply times over the last two minutes (max \(msText((main + router).max())) ms)")
    }

    /// "Is het mijn Wi-Fi of het internet?": knop, daarna één oordeel met de getallen eronder.
    @ViewBuilder private var findLine: some View {
        if stats.findBusy {
            HStack(spacing: 6) {
                ProgressView().controlSize(.small)
                Text("Checking Wi-Fi, router, DNS and internet… (about 6 s)").fixedSize(horizontal: false, vertical: true)
            }
            .font(theme.label(10)).foregroundStyle(theme.onRow(true))
        } else {
            if let f = stats.finding {
                status(icon(f.ok), f.text, ok: f.ok)
                if !f.numbers.isEmpty {
                    Text(f.numbers).font(theme.label(10)).foregroundStyle(theme.onRow(true)).padding(.leading, 20)
                        .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                }
            }
            Button { stats.findProblem() } label: { NetPill(title: stats.finding == nil ? L("Find the problem") : L("Check again"), icon: "stethoscope") }
                .buttonStyle(.plain).help("Is it your Wi-Fi or the internet? About 6 s: pings your router and 1.1.1.1, times fresh DNS lookups and reads the Wi-Fi signal")
        }
    }

    // MARK: Wi-Fi of kabel

    @ViewBuilder private var link: some View {
        if let w = stats.wifi {
            card("Wi-Fi", icon: "wifi", index: 1, terms: NetTerm.wifi, open: $detailsOpen) {
                EmptyView()
            } face: {
                HStack(alignment: .top, spacing: 18) {
                    VStack(alignment: .leading, spacing: 1) {
                        TermLabel(term: .signal).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                        HStack(alignment: .lastTextBaseline, spacing: 8) {
                            big("\(w.rssi)", "dBm")
                            SignalBars(rssi: w.rssi, color: tone(signalOK(w.rssi))).padding(.bottom, 3)
                        }
                        Text(w.word).font(theme.label(10)).foregroundStyle(tone(signalOK(w.rssi)))
                    }
                    if w.rate > 0 {
                        VStack(alignment: .leading, spacing: 1) {
                            TermLabel(term: .linkSpeed).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                            big("\(Int(w.rate))", "Mbit/s")
                        }
                    }
                }
                let facts = [w.band, w.phy.map { $0.components(separatedBy: " (").first ?? $0 }].compactMap { $0 }
                if !facts.isEmpty { Text(facts.joined(separator: " · ")).font(theme.label(11)).foregroundStyle(theme.onRow(true)) }
            } details: {
                kv(.signal, "\(w.rssi) dBm (\(w.word))")
                if let snr = w.snr { kv(.noise, "\(w.noise) dBm · SNR \(snr) dB", title: L("Noise")) }
                if w.rate > 0 { kv(.linkSpeed, "\(Int(w.rate)) Mbit/s") }
                if let ch = w.channel { kv(.channel, ([String(ch)] + [w.band, w.width].compactMap { $0 }).joined(separator: " · ")) }
                if let phy = w.phy { kv(.standard, phy) }
                if let sec = w.security { kv(.security, sec) }
                nearby(w)
            }
        } else {
            let c = stats.conn
            let wired = c?.kind == "Ethernet"
            let note = c?.media?.note(linkMbit: c?.linkMbit)   // loopt de kabel onder wat de adapter kan?
            card(c == nil ? "Wi-Fi" : wired ? L("Cable") : c?.kind == "VPN" ? "VPN" : L("Network link"),
                 icon: c == nil ? "wifi.slash" : wired ? "cable.connector" : c?.kind == "VPN" ? "lock.shield" : "network",
                 index: 1, terms: NetTerm.wired, open: c?.media?.maxMbit != nil || c?.media?.duplex != nil ? $detailsOpen : nil) {
                EmptyView()
            } face: {
                if let c {
                    status(wired ? "cable.connector" : "network",
                           wired ? L("Connected by cable, not Wi-Fi") : c.kind == "VPN" ? L("Traffic goes through a VPN tunnel; Wi-Fi details aren't available") : L("Connected through \(c.iface), not Wi-Fi"), ok: nil)
                    HStack(alignment: .top, spacing: 18) {
                        if let mbit = c.linkMbit {
                            VStack(alignment: .leading, spacing: 1) {
                                TermLabel(term: .linkSpeed).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                                big(mbit >= 1000 && mbit.truncatingRemainder(dividingBy: 1000) == 0 ? "\(Int(mbit / 1000))" : "\(Int(mbit))", mbit >= 1000 && mbit.truncatingRemainder(dividingBy: 1000) == 0 ? "Gbit/s" : "Mbit/s")
                            }
                        }
                        VStack(alignment: .leading, spacing: 1) {
                            TermLabel(term: wired ? .cable : .type, title: L("Adapter")).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                            Text(c.name ?? c.kind).font(theme.label(13)).foregroundStyle(theme.onRow())
                            Text(c.iface).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                        }
                    }
                    if let note, note.serious { status("exclamationmark.triangle.fill", note.text, ok: false) }
                } else {
                    status("wifi.slash", L("Not connected"), ok: false)
                }
            } details: {
                if let m = c?.media {
                    if let now = c?.linkMbit { kv(.linkSpeed, EtherMedia.speed(Int(now))) }
                    if let top = m.maxMbit { kv(.adapterMax, EtherMedia.speed(top)) }
                    if let d = m.duplex { kv(.duplex, d) }
                    if let note, !note.serious { Text(note.text).font(theme.label(10)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true) }
                }
            }
        }
    }

    private func signalOK(_ rssi: Int) -> Bool? { rssi >= -60 ? true : rssi >= -70 ? nil : false }

    /// Netwerken in de buurt: een oordeel met uitleg, en de lijst (sterkste eerst). Pas na een klik (~6 s).
    @ViewBuilder private func nearby(_ w: WifiDetail) -> some View {
        subhead(L("Around you"))
        let band = ["2.4 GHz": 1, "5 GHz": 2, "6 GHz": 3][w.band ?? ""]
        if let n = stats.nearby {
            let others = w.channel.flatMap { ch in band.map { max(0, n.crowd(channel: ch, band: $0) - 1) } }
            let mine = n.nets.filter { $0.channel == w.channel && abs($0.rssi - w.rssi) <= 8 }.max { $0.rssi < $1.rssi }
            VStack(alignment: .leading, spacing: 3) {
                if let others {
                    status(icon(others == 0 ? true : others >= 3 ? false : nil),
                           others == 0 ? L("None share your channel") : band == 1 ? L("\(others) on or next to your channel") : L("\(others) on your channel"),
                           ok: others == 0 ? true : others >= 3 ? false : nil)
                        .help("Networks on the same channel take turns sending and slow each other down. Weak ones barely matter.")
                }
                if band == 1, let q = n.quietest24, q != w.channel {
                    Text("Quietest 2.4 GHz channel: \(q). Your router can switch to it.").foregroundStyle(theme.onRow(true))
                }
                Text("\(n.nets.count) Wi-Fi networks in range").foregroundStyle(theme.onRow(true)).padding(.top, 2)
                ForEach(Array(n.nets.sorted { $0.rssi > $1.rssi }.prefix(5).enumerated()), id: \.offset) { _, x in
                    HStack(spacing: 6) {
                        SignalBars(rssi: x.rssi, color: x.channel == w.channel ? theme.onRow() : theme.onRow(true))
                        Text(L("channel \(x.channel)") + (x == mine ? " · " + L("probably yours") : ""))
                            .foregroundStyle(x.channel == w.channel ? theme.onRow() : theme.onRow(true))
                        Spacer(minLength: 4)
                        Text(verbatim: "\(x.rssi) dBm").font(theme.number(10)).foregroundStyle(theme.onRow(true))
                    }
                }
            }
            .font(theme.label(11)).fixedSize(horizontal: false, vertical: true)
        }
        HStack(spacing: 6) {
            Button { stats.scanNearby() } label: { NetPill(title: stats.nearby == nil ? L("Look around") : L("Look again"), icon: "dot.radiowaves.left.and.right") }
                .buttonStyle(.plain).disabled(stats.nearbyBusy)
                .help("Lists the Wi-Fi networks in range by channel and signal (about 6 s). Fusebox doesn't read their names.")
            if stats.nearbyBusy { ProgressView().controlSize(.small) }
        }
        .padding(.top, 2)
    }

    // MARK: Snelheidstest

    /// Tests op de tegel: van dit netwerk, of alles ("Alle netwerken" in ⋯). Laatste 10.
    private var shown: [SpeedResult] {
        guard !allNets else { return Array(stats.history.prefix(10)) }
        let here = NetInfo.current()?.networkKey
        return Array(stats.history.filter { $0.place != nil && $0.place == here }.prefix(10))
    }

    private var speed: some View {
        let shown = shown
        return card(L("Speed test"), icon: "speedometer", index: 2, terms: NetTerm.speed, open: shown.isEmpty ? nil : $detailsOpen,
             extra: [(L("Data use"), L("Quick uses \(usage(.quick)), Full \(usage(.full)) (via Cloudflare)"))]) {
            reportItem
            Toggle(L("All networks"), isOn: $allNets)
                .help("Show speed tests from every network together, instead of only this one")
            Button(role: .destructive) { confirmClear = true } label: { Label(L("Clear history…"), systemImage: "trash") }
                .disabled(stats.history.isEmpty).help("Deletes the saved speed tests, so fastest and average start fresh")
        } face: {
            if let last = shown.first {
                HStack(alignment: .top, spacing: 18) {
                    VStack(alignment: .leading, spacing: 1) {
                        TermLabel(term: .download, title: "↓ " + L("Download")).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                        bigSplit(mbitText(last.down))
                    }
                    VStack(alignment: .leading, spacing: 1) {
                        TermLabel(term: .upload, title: "↑ " + L("Upload")).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                        bigSplit(mbitText(last.up))
                    }
                }
                if let p = last.ping {
                    HStack(spacing: 4) {
                        TermLabel(term: .delay).foregroundStyle(theme.onRow(true))
                        Text(verbatim: "\(msText(p)) ms").foregroundStyle(theme.onRow())
                        if let b = last.loadedPing {
                            Text(verbatim: "·").foregroundStyle(theme.onRow(true))
                            TermLabel(term: .busyDelay, title: L("busy")).foregroundStyle(theme.onRow(true))
                            let bad = SpeedMath.loadVerdict(idle: p, loaded: b).ok == false
                            Text(verbatim: "\(msText(b)) ms").foregroundStyle(bad ? tone(false) : theme.onRow())
                        }
                    }
                    .font(theme.label(11))
                }
                Text(last.date.formatted(date: .abbreviated, time: .shortened) + " · " + kindName(last.kind))
                    .font(theme.label(10)).foregroundStyle(theme.onRow(true))
            } else if stats.speedPhase == nil {
                Text(stats.history.isEmpty ? "No speed test yet" : "No speed test on this network yet").font(theme.label(12)).foregroundStyle(theme.onRow(true))
            }
            speedControls
        } details: {
            if let last = shown.first {
                if let d = last.downPeak { kv(.download, L("top \(mbitText(d))")) }
                if let u = last.upPeak { kv(.upload, L("top \(mbitText(u))")) }
                if let v = last.variation { kv(.stability, SpeedMath.stabilityText(v)) }
                if let idle = last.ping, let busy = last.loadedPing {
                    let v = SpeedMath.loadVerdict(idle: idle, loaded: busy)
                    status(icon(v.ok), v.text, ok: v.ok).padding(.top, 2)
                }
                if shown.count > 1 { historyBars(shown).padding(.top, 4) }
            }
        }
        .confirmationDialog("Clear speed test history?", isPresented: $confirmClear) {
            Button("Clear history", role: .destructive) { stats.clearHistory() }
        } message: {
            Text("All \(stats.history.count) saved speed tests are deleted. Fastest and average start fresh with your next test.")
        }
    }

    @ViewBuilder private var speedControls: some View {
        if let phase = stats.speedPhase {
            VStack(alignment: .leading, spacing: 4) {
                HStack(spacing: 6) {
                    if let live = stats.speedLive { bigSplit(mbitText(live)) } else { ProgressView().controlSize(.small) }
                    Spacer(minLength: 4)
                    Button { stats.cancelSpeedTest() } label: { NetPill(title: L("Cancel"), icon: "xmark") }.buttonStyle(.plain)
                }
                ProgressView(value: stats.speedProgress).controlSize(.small).tint(blocks ? .white : theme.accent)
                Text(phase).font(theme.label(10)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            }
        } else if let kind = stats.speedConfirm {
            Text("This connection is metered (mobile data, iPhone hotspot or Low Data Mode). The \(kind.title.lowercased()) test uses \(usage(kind)) of data.")
                .font(theme.label(11)).foregroundStyle(tone(false)).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 6) {
                Button { stats.runSpeedTest(kind) } label: { NetPill(title: L("Run anyway"), icon: "speedometer") }.buttonStyle(.plain)
                Button { stats.dismissSpeedConfirm() } label: { NetPill(title: L("Not now"), icon: "xmark") }.buttonStyle(.plain)
            }
        } else {
            HStack(spacing: 6) {
                Button { stats.requestSpeedTest(.quick) } label: { NetPill(title: L("Quick"), icon: "hare") }
                    .buttonStyle(.plain).help("About 10 s, 4 connections at once. The first 2 s, while the line speeds up, don't count.")
                Button { stats.requestSpeedTest(.full) } label: { NetPill(title: L("Full"), icon: "gauge.with.dots.needle.67percent") }
                    .buttonStyle(.plain).help("About 30 s, 6 connections: typical and top speed, delay while the line is busy (bufferbloat) and how steady it is.")
            }
        }
        if let e = stats.speedError { Text(e).font(theme.label(10)).foregroundStyle(tone(false)).fixedSize(horizontal: false, vertical: true) }
    }

    /// Laatste 10 metingen als staafjes (download), oudste links. Volledig = brede staaf.
    private func historyBars(_ shown: [SpeedResult]) -> some View {
        let list = Array(shown.reversed()), top = max(list.map(\.down).max() ?? 1, 1)
        let color = blocks ? Color.white : theme.accent
        return HStack(alignment: .bottom, spacing: 3) {
            ForEach(Array(list.enumerated()), id: \.offset) { _, r in
                Capsule().fill(color.opacity(r == shown.first ? 1 : 0.45))
                    .frame(width: r.kind == .full ? 9 : 5, height: max(3, 24 * r.down / top))
                    .help("\(r.date.formatted(date: .abbreviated, time: .shortened)) · \(kindName(r.kind)): ↓\(mbitText(r.down)) ↑\(mbitText(r.up))")
            }
            Text(allNets ? "last \(list.count), all networks · wide = Full" : "last \(list.count) · wide = Full").font(theme.label(9)).foregroundStyle(theme.onRow(true)).padding(.leading, 4)
        }
        .frame(height: 26, alignment: .bottom)
    }

    /// Geschat verbruik: op basis van de vorige uitslag, anders het plafond (beide richtingen).
    private func usage(_ kind: SpeedKind) -> String {
        SpeedMath.estimate(kind, last: shown.first).map { L("about \(bytes(Double($0)))") } ?? L("at most \(bytes(Double(kind.cap * 2)))")
    }

    private func kindName(_ k: SpeedKind?) -> String { k.map { L("\($0.title) test") } ?? L("single connection") }

    // MARK: Rapport

    private var reportItem: some View {
        Button { saveReport() } label: { Label(L("Save report…"), systemImage: "doc.text") }
            .disabled(stats.testing)
            .help("Saves a PDF with your connection, quality and speed tests, to send to a helpdesk or your provider. No network name, addresses or device names.")
    }

    private func saveReport() {
        reportNote = nil
        let p = NSSavePanel()
        p.nameFieldStringValue = "fusebox-network-report.pdf"
        p.allowedContentTypes = [.pdf]
        guard p.runModal() == .OK, let url = p.url else { return }
        if !stats.report(internetCheck: !pingHost.isEmpty).writePDF(to: url) { reportNote = L("Creating the PDF failed") }
    }

    // MARK: Bouwstenen

    /// Kaart: kop (icoon, titel, ⋯, ⓘ), de kerngetallen, en onderaan "Details" (alleen als er details zijn).
    private func card<M: View, F: View, D: View>(_ title: String, icon: String, index: Int, terms: [NetTerm], open: Binding<Bool>?,
                                                  extra: [(title: String, text: String)] = [],
                                                  @ViewBuilder menu: () -> M, @ViewBuilder face: () -> F, @ViewBuilder details: () -> D) -> some View {
        CardFrame(title: title, icon: icon, index: index, terms: terms, open: open, extra: extra, menu: menu(), face: face(), details: details())
    }

    private func big(_ value: String, _ unit: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 3) {
            Text(value).font(theme.number(24)).foregroundStyle(theme.onRow()).lineLimit(1)
            Text(unit).font(theme.label(11)).foregroundStyle(theme.onRow(true)).lineLimit(1)
        }
        .fixedSize()
    }

    /// "412 Mbit/s" → groot getal + kleine eenheid.
    private func bigSplit(_ s: String) -> some View {
        let parts = s.split(separator: " ", maxSplits: 1).map(String.init)
        return big(parts.first ?? s, parts.count > 1 ? parts[1] : "")
    }

    private func status(_ icon: String, _ text: String, ok: Bool?) -> some View {
        Label { Text(text).foregroundStyle(ok == nil ? theme.onRow() : tone(ok)).fixedSize(horizontal: false, vertical: true) }
              icon: { Image(systemName: icon).foregroundStyle(tone(ok)).frame(width: 14) }
            .font(theme.label(12))
    }

    private func subhead(_ title: String) -> some View {
        Text(title).font(theme.label(11)).foregroundStyle(theme.onRow()).padding(.top, 6)
    }

    private func kv(_ term: NetTerm, _ v: String, title: String? = nil) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            TermLabel(term: term, title: title).foregroundStyle(theme.onRow(true)).frame(width: 96, alignment: .leading)
            Text(v).foregroundStyle(theme.onRow()).textSelection(.enabled).fixedSize(horizontal: false, vertical: true)
        }
        .font(theme.label(11))
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

/// Eén kaart. Apart view zodat elke kaart zijn eigen ⓘ-popover-status heeft.
private struct CardFrame<M: View, F: View, D: View>: View {
    let title: String, icon: String, index: Int, terms: [NetTerm]
    let open: Binding<Bool>?
    var extra: [(title: String, text: String)] = []
    let menu: M, face: F, details: D
    @Environment(\.theme) private var theme
    @State private var info = false

    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack(spacing: 6) {
                Image(systemName: icon).font(.system(size: 11, weight: .semibold)).foregroundStyle(theme.onRow(true)).frame(width: 14)
                Text(title).font(theme.label(12)).foregroundStyle(theme.onRow()).lineLimit(1)
                Spacer(minLength: 4)
                if M.self != EmptyView.self {
                    Menu { menu } label: {
                        Image(systemName: "ellipsis").font(.system(size: 11, weight: .semibold)).frame(width: 20, height: 18).contentShape(.rect)
                    }
                    .menuStyle(.button).buttonStyle(.plain).menuIndicator(.hidden).fixedSize()
                    .foregroundStyle(theme.onRow(true)).help("More actions")
                }
                Button { info.toggle() } label: {
                    Image(systemName: "info.circle").font(.system(size: 12)).frame(width: 18, height: 18).contentShape(.rect)
                }
                .buttonStyle(.plain).foregroundStyle(theme.onRow(true)).help("What these numbers mean")
                .popover(isPresented: $info, arrowEdge: .bottom) { TermsPopover(title: L("What the numbers mean"), terms: terms, extra: extra) }
            }
            face
            if let open {
                if !open.wrappedValue { Spacer(minLength: 0) }   // dicht: "Details" onderaan, gelijk met de buurkaarten
                Button { withAnimation(.snappy(duration: 0.25)) { open.wrappedValue.toggle() } } label: {
                    HStack(spacing: 4) {
                        Text(open.wrappedValue ? "Fewer details" : "Details")
                        Image(systemName: "chevron.down").font(.system(size: 8, weight: .bold)).rotationEffect(.degrees(open.wrappedValue ? 180 : 0))
                    }
                    .font(theme.label(10)).foregroundStyle(theme.onRow(true)).contentShape(.rect)
                }
                .buttonStyle(.plain)
                if open.wrappedValue {
                    VStack(alignment: .leading, spacing: 4) { details }
                        .padding(.top, 6)
                        .overlay(alignment: .top) { Rectangle().fill(theme.rowStyle == .blocks ? Color.white.opacity(0.3) : theme.line).frame(height: 1) }
                        .transition(.opacity)
                    Spacer(minLength: 0)
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)   // naast elkaar: even hoog
        .modifier(NetCardShape(theme: theme, index: index))
    }
}

/// Vier streepjes zoals in de menubalk: hoe sterker het signaal, hoe meer gevuld.
struct SignalBars: View {
    let rssi: Int, color: Color
    @Environment(\.theme) private var theme
    var body: some View {
        let level = rssi >= -55 ? 4 : rssi >= -62 ? 3 : rssi >= -70 ? 2 : rssi >= -78 ? 1 : 0
        HStack(alignment: .bottom, spacing: 2) {
            ForEach(0..<4) { i in
                RoundedRectangle(cornerRadius: 1.5).fill(i < level ? color : theme.onRow(true).opacity(0.25))
                    .frame(width: 4, height: CGFloat(6 + i * 4))
            }
        }
        .accessibilityLabel(Text(verbatim: "\(level)/4"))
    }
}
#endif
