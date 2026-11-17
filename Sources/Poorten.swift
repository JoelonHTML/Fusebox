import SwiftUI

// MARK: - Docked-pagina: bovenaan wat de Mac binnenkrijgt, daaronder per poort een korte lijst poort → kabel → lader/dock → apparaten
//
// Per regel één getal rechts; hubs en de Billboard van een dock alleen met "Show hubs & internals".
// Klik op een regel voor de details. Gegevens: PoortenData.swift; alleen bijgewerkt zolang deze pagina open is.

struct PortsView: View {
    @StateObject private var watcher = PortWatcher()
    var body: some View {
        ScrollView {
            PortsContent(snap: watcher.snap, loaded: watcher.loaded).padding(.horizontal, 12).padding(.bottom, 12)
        }
        .clipped()   // niet onder de doorzichtige werkbalk doorschuiven
        .onAppear { watcher.start() }
        .onDisappear { watcher.stop() }
    }
}

struct PortsContent: View {
    let snap: PortsSnapshot, loaded: Bool
    @State private var internals = false
    @Environment(\.theme) private var theme

    var body: some View {
        let sums = PortSum.all(snap)
        VStack(alignment: .leading, spacing: 14) {
            IntoMac(snap: snap, sums: sums).padding(.top, 2)
            if loaded && snap.ports.isEmpty {
                Text("No USB-C or MagSafe ports found on this Mac.").font(theme.label(12)).foregroundStyle(theme.secondary).padding(.vertical, 20)
            }
            DockedList(groups: PortTree.build(sums, internals: internals), empty: sums.filter { !$0.port.connected }, sums: sums)
            HStack(spacing: 14) {
                Button { withAnimation(.snappy(duration: 0.25)) { internals.toggle() } } label: {
                    Label(internals ? "Hide hubs & internals" : "Show hubs & internals", systemImage: "point.3.filled.connected.trianglepath.dotted")
                        .font(theme.label(10)).foregroundStyle(theme.onRow(true)).contentShape(.rect)
                }
                .buttonStyle(.plain)
                Spacer(minLength: 0)
            }
            otherDevices
        }
    }

    @ViewBuilder private var otherDevices: some View {
        let claimed = Set(snap.ports.flatMap(\.usbRoots))
        let other = snap.usb.filter { !claimed.contains($0.id) }
        let sockets = Set(snap.ports.filter { !$0.isMagSafe }.map { String($0.number) })
        let otherTB = snap.tb.filter { $0.socket.map { !sockets.contains($0) } ?? true }
        if !other.isEmpty || !otherTB.isEmpty {
            Disclosure(title: "Other devices", count: Ports.devices(other).count + otherTB.count) {
                Text("Not linked to a specific port (built in, or behind a Thunderbolt dock).").font(theme.label(10)).foregroundStyle(theme.onRow(true))
                DeviceTree(roots: other, tb: otherTB)
            }
        }
    }
}

// MARK: - Kop: wat er nu de Mac in gaat

private struct IntoMac: View {
    let snap: PortsSnapshot, sums: [PortSum]
    @Environment(\.theme) private var theme

    var body: some View {
        let n = snap.power
        let inW = n.flatMap { $0.external ? $0.mW : nil }.map { Double($0) / 1000 }
        let charger = sums.compactMap(\.port.charger).max { $0.maxW < $1.maxW }
        let fastest = sums.filter { $0.port.links.contains { $0.type != "DisplayPort" } }.compactMap(\.dataBps).max()
        let devices = sums.map(\.deviceCount).reduce(0, +)
        VStack(alignment: .leading, spacing: 10) {
            HStack(alignment: .firstTextBaseline, spacing: 10) {
                VStack(alignment: .leading, spacing: 0) {
                    BigNum(value: inW.map { trim($0) } ?? "0", unit: "W", size: 34)
                    Text(inW == nil ? "on battery" : "into this Mac").font(theme.label(10)).foregroundStyle(theme.onRow(true))
                }
                Spacer(minLength: 8)
                if let n, let pc = n.percent {
                    VStack(alignment: .trailing, spacing: 0) {
                        BigNum(value: "\(Int(pc.rounded()))", unit: "%", size: 24)
                        Text(batteryText(n)).font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                    }
                }
            }
            NetFlow(spacing: 6) {
                if let c = charger { stat("bolt.fill", "\(trim(c.maxW)) W", "charger") }
                if let f = fastest { stat("arrow.left.arrow.right", speedParts(f).0 + " " + speedParts(f).1, "link") }
                if devices > 0 { stat("point.3.connected.trianglepath.dotted", "\(devices)", devices == 1 ? "device" : "devices") }
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme)
    }

    private func batteryText(_ n: PowerNow) -> String {
        if n.full { return "battery full" }
        if n.charging { return "charging" + (n.minutes.map { " · full in " + duration($0) } ?? "") }
        return n.external ? "not charging" : (n.minutes.map { duration($0) + " left" } ?? "on battery")
    }

    private func stat(_ icon: String, _ value: String, _ label: String) -> some View {
        HStack(spacing: 4) {
            Image(systemName: icon).font(.system(size: 9 * theme.scale)).foregroundStyle(theme.onRow(true))
            Text(value).font(theme.number(11)).foregroundStyle(theme.onRow())
            Text(label).font(theme.label(10)).foregroundStyle(theme.onRow(true))
        }
        .padding(.horizontal, 7).padding(.vertical, 3)
        .background(theme.tile, in: .capsule)
    }
}

// MARK: - Lijst per poort

/// Wat er nu loopt naast wat maximaal kan (data in bit/s, stroom in W). `slow` = loopt duidelijk onder wat kan.
struct LinkLoad { var now: Double?, max: Double?, power = false, slow = false }

struct TNode: Identifiable {
    enum Kind { case port, cable, source, hub, device, tb }
    let id: String, kind: Kind, icon: String, title: String
    var main: LinkLoad? = nil       // het ene getal rechts
    var facts: [String] = []        // hooguit 4 korte feiten
    var warn: String? = nil         // één gewone zin onder de regel
    var info: String? = nil         // ⓘ met uitleg bij aanwijzen
    var dim = false
    var children: [TNode] = []
    var portID: String? = nil
    var usb: USBDev? = nil
}

enum PortTree {
    /// Per aangesloten poort één groep. `internals`: ook hubs (ook die in een dock) en de Billboard tonen.
    static func build(_ sums: [PortSum], internals: Bool) -> [TNode] {
        sums.filter(\.port.connected).map { port($0, internals: internals) }
    }

    /// Bij welke regel een oordeel hoort.
    private static func notes(_ p: PortSum, _ topics: Set<PortNote.Topic>) -> (warn: String?, info: String?) {
        let ns = p.notes.filter { topics.contains($0.topic) }
        let info = ns.filter { $0.level == .info }.map(\.text)
        return (ns.first { $0.level == .warn }?.text, info.isEmpty ? nil : info.joined(separator: "\n\n"))
    }

    /// Hoogste snelheid van wat erachter zit, als die vaststaat (USB 2- of 3.0-hub/apparaat).
    private static func sourceMax(_ p: PortSum) -> Double? {
        let all = p.roots.map { $0.bcd.flatMap(Ports.usbMax) }
        return all.isEmpty || all.contains(nil) ? nil : all.compactMap { $0 }.max()   // één onzekere = geen uitspraak
    }

    private static func port(_ p: PortSum, internals: Bool) -> TNode {
        let pmax = Ports.portMax(p.port.supported)
        // Trager dan poort, kabel en apparaat samen toelaten? Dan zit er ergens een rem op.
        let allowed = [pmax, p.cableBps, sourceMax(p)].compactMap { $0 }.min()
        let slow = p.dataBps.map { now in allowed.map { now < $0 * 0.9 } ?? false } ?? false
        let types = Set(p.port.links.map(\.type))
        let kind = types.contains("CIO") ? "Thunderbolt" : types.contains("USB3") ? "USB 3" : types.contains("USB2") ? "USB 2"
            : types.contains("DisplayPort") ? "video" : nil
        let n = notes(p, [.port, .data])
        var node = TNode(id: "port:" + p.id, kind: .port, icon: p.port.isMagSafe ? "bolt.horizontal.fill" : "cable.connector",
                         title: p.port.title,
                         main: p.port.isMagSafe || p.dataBps == nil ? p.nowW.map { LinkLoad(now: $0, max: nil, power: true) } : LinkLoad(now: p.dataBps, max: pmax, slow: slow),
                         facts: [kind, p.port.isMagSafe ? nil : p.nowW.map { "\(trim($0)) W in" }].compactMap { $0 },
                         warn: n.warn ?? (slow ? "Runs slower than the port, cable and device allow. Try another cable." : nil), info: n.info, portID: p.id)
        let src = source(p, internals: internals)
        node.children = p.port.isMagSafe ? [src] : [cable(p, src)]
        return node
    }

    private static func cable(_ p: PortSum, _ src: TNode) -> TNode {
        let c = p.port.cable
        var facts = [c == nil ? "no e-marker" : p.cableKind, "\(p.cableW ?? 60) W"]
        if let l = c?.latency, (1...8).contains(l) { facts.append(l == 8 ? "over 7 m" : "~\(l) m") }
        let data = !p.port.links.isEmpty && p.cableBps != nil
        let n = notes(p, [.cable])
        return TNode(id: "cable:" + p.id, kind: .cable, icon: "cable.connector.horizontal", title: "Cable",
                     main: data ? LinkLoad(now: p.dataBps, max: p.cableBps) : LinkLoad(now: nil, max: Double(p.cableW ?? 60), power: true),
                     facts: data ? facts : [facts[0]] + facts.dropFirst(2), warn: n.warn, info: n.info, children: [src], portID: p.id)
    }

    private static func source(_ p: PortSum, internals: Bool) -> TNode {
        var facts: [String] = []
        if let now = p.nowW, p.port.charger != nil { facts.append("\(trim(now)) W to Mac") }
        if let w = p.port.charger?.winning { facts.append("\(volt(w.mV)) × \(amp(w.mA))") }
        if p.port.charger == nil, let r = p.roots.first, !r.hub { facts.append(r.maker) }
        var kids: [TNode]
        if internals { kids = p.roots.compactMap { usb($0, via: 0) } }
        else { kids = p.devices.map { device($0) } }
        kids += p.tb.map { TNode(id: "tb:\($0.id)", kind: .tb, icon: "bolt.horizontal.fill", title: $0.name, facts: [$0.vendor, "Thunderbolt"].filter { !$0.isEmpty }, portID: p.id) }
        if !internals, p.roots.count == 1, !p.roots[0].hub { kids = [] }   // één los apparaat: dat is deze regel zelf
        let n = notes(p, [.power, .devices])
        return TNode(id: "src:" + p.id, kind: .source, icon: p.what.icon, title: p.what.name,
                     main: p.port.charger.map { LinkLoad(now: $0.maxW, max: nil, power: true) } ?? p.dataBps.map { LinkLoad(now: $0, max: nil) },
                     facts: facts, warn: n.warn, info: n.info, children: kids, portID: p.id)
    }

    /// Apparaat in de gewone weergave: naam, snelheid, maker en USB-soort.
    private static func device(_ d: USBDev) -> TNode {
        // USB 3-apparaat dat op USB 2-snelheid hangt: betrouwbaar teken van een rem (kabel, hub of poort)
        let slow = (d.bcd ?? 0) >= 0x300 && (d.bps ?? 0) > 0 && (d.bps ?? 0) <= 480e6
        return TNode(id: "usb:\(d.id)", kind: .device, icon: deviceIcon(d), title: d.name, main: LinkLoad(now: d.bps, max: nil, slow: slow),
                     facts: [d.maker, d.bps.map(shortSpeed)].compactMap { $0 },
                     warn: slow ? "Runs at USB 2 speed, but it can do USB 3. Try another cable or port." : nil, usb: d)
    }

    /// Met "internals": de echte USB-boom. Hubs zonder iets erachter vallen weg; een hub met alleen één hub eronder wordt één regel.
    private static func usb(_ d: USBDev, via: Int) -> TNode? {
        guard d.hub else {
            var n = device(d)
            if d.billboard { n.facts = ["mode info from the dock"]; n.dim = true; n.info = "Billboard: the dock tells the Mac which extra modes (such as video over USB-C) are not active." }
            return n
        }
        if d.leaves.isEmpty { return nil }
        if d.children.count == 1, d.children[0].hub { return usb(d.children[0], via: via + 1) }
        return TNode(id: "usb:\(d.id)", kind: .hub, icon: deviceIcon(d), title: d.name, main: LinkLoad(now: d.bps, max: nil),
                     facts: [d.maker, via > 0 ? "\(via + 1) hubs" : nil].compactMap { $0 }, dim: true,
                     children: d.children.compactMap { usb($0, via: 0) }, usb: d)
    }

    static func shortSpeed(_ b: Double) -> String {
        switch b { case ..<100e6: "USB 1"; case ..<1e9: "USB 2"; default: "USB 3" }
    }

    static func find(_ id: String, in nodes: [TNode]) -> TNode? {
        for n in nodes { if n.id == id { return n }; if let f = find(id, in: n.children) { return f } }
        return nil
    }
}

/// "10 / 40 Gbit/s", "480 Mbit/s of 10 Gbit/s", "max 100 W" of alleen "5 Gbit/s"; oranje met pijltje als het trager loopt dan kan.
private struct LoadView: View {
    let m: LinkLoad
    var size: CGFloat = 16
    @Environment(\.theme) private var theme
    var body: some View {
        let f: (Double) -> (String, String) = { m.power ? (trim($0), "W") : speedParts($0) }
        let small = theme.label(max(size * 0.5, 9))
        HStack(alignment: .firstTextBaseline, spacing: 3) {
            if let n = m.now {
                Text(f(n).0).font(theme.number(size)).foregroundStyle(m.slow ? theme.level(.warn) : theme.onRow())
                if let x = m.max {
                    let a = f(n), b = f(x)
                    Text(a.1 == b.1 ? "/ \(b.0) \(b.1)" : "\(a.1) of \(b.0) \(b.1)").font(small).foregroundStyle(theme.onRow(true))
                } else {
                    Text(f(n).1).font(small).foregroundStyle(theme.onRow(true))
                }
            } else if let x = m.max {
                Text("max").font(small).foregroundStyle(theme.onRow(true))
                Text(f(x).0).font(theme.number(size)).foregroundStyle(theme.onRow())
                Text(f(x).1).font(small).foregroundStyle(theme.onRow(true))
            }
            if m.slow { Image(systemName: "arrow.down.circle.fill").font(.system(size: size * 0.55)).foregroundStyle(theme.level(.warn)) }
        }
        .lineLimit(1)
    }
}

/// Details van de aangeklikte regel.
private struct NodeDetails: View {
    let node: TNode, sums: [PortSum]
    @Environment(\.theme) private var theme
    var body: some View {
        let p = node.portID.flatMap { id in sums.first { $0.id == id } }
        VStack(alignment: .leading, spacing: 8) {
            if let d = node.usb { DeviceCard(dev: d) }
            else if let p {
                Label(node.kind == .cable ? "Cable in \(p.port.title)" : node.title, systemImage: node.icon)
                    .font(theme.label(13).weight(.semibold))
                PortDetails(sum: p, topics: node.kind == .cable ? [.cable] : node.kind == .port ? [.port] : [.power, .data])
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// Breed: lijst links (niet breder dan nodig), details rechts. Smal: details onder de aangeklikte regel.
private struct DockedList: View {
    let groups: [TNode], empty: [PortSum], sums: [PortSum]
    @State private var sel: String?
    @Environment(\.theme) private var theme
    var body: some View {
        let picked = sel.flatMap { PortTree.find($0, in: groups) }
        ViewThatFits(in: .horizontal) {
            HStack(alignment: .top, spacing: 16) {
                list(inline: false).frame(maxWidth: 560)
                Group {
                    if let picked { NodeDetails(node: picked, sums: sums) }
                    else { Text("Click a row for its details.").font(theme.label(11)).foregroundStyle(theme.onRow(true)) }
                }
                .frame(maxWidth: .infinity, alignment: .leading).themedRow(theme)
            }
            .frame(minWidth: 900)
            list(inline: true).frame(maxWidth: 600, alignment: .leading)   // getallen niet ver van de namen
        }
    }

    private func list(inline: Bool) -> some View {
        VStack(alignment: .leading, spacing: 14) {
            ForEach(Array(groups.enumerated()), id: \.element.id) { i, g in
                PortGroupList(root: g, sums: sums, sel: $sel, inline: inline).themedRow(theme, index: i)
            }
            if !empty.isEmpty {
                Label(empty.map(\.port.title).joined(separator: " · ") + " · empty", systemImage: "circle.dashed")
                    .font(theme.label(11)).foregroundStyle(theme.onRow(true).opacity(0.75)).padding(.leading, 4)
            }
        }
    }
}

/// Eén poort met wat eraan hangt, met verbindingslijnen zoals in Finder.
private struct PortGroupList: View {
    let root: TNode, sums: [PortSum]
    @Binding var sel: String?
    let inline: Bool
    @Environment(\.theme) private var theme

    private struct Row { let node: TNode, last: [Bool] }   // last: per niveau of die regel de laatste is

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            ForEach(rows, id: \.node.id) { row($0) }
        }
    }

    private var rows: [Row] {
        var out: [Row] = []
        func walk(_ n: TNode, _ last: [Bool]) {
            out.append(Row(node: n, last: last))
            for (i, c) in n.children.enumerated() { walk(c, last + [i == n.children.count - 1]) }
        }
        walk(root, [])
        return out
    }

    private func row(_ r: Row) -> some View {
        let n = r.node, on = sel == n.id
        return HStack(alignment: .top, spacing: 0) {
            ForEach(Array(r.last.enumerated()), id: \.offset) { i, isLast in
                Connector(kind: i == r.last.count - 1 ? (isLast ? .elbow : .tee) : (isLast ? .none : .pass))
                    .stroke(theme.onRow(true).opacity(0.4), lineWidth: 1.5)
                    .frame(width: 16).frame(maxHeight: .infinity)
            }
            VStack(alignment: .leading, spacing: 6) {
            VStack(alignment: .leading, spacing: 4) {
                HStack(alignment: .top, spacing: 7) {
                    Image(systemName: n.icon).font(.system(size: 11 * theme.scale)).foregroundStyle(theme.onRow(true)).frame(width: 16).padding(.top, 2)
                    VStack(alignment: .leading, spacing: 1) {
                        HStack(spacing: 5) {
                            Text(n.title).font(theme.label(n.kind == .port ? 13 : 12).weight(.semibold)).lineLimit(1)
                                .foregroundStyle(theme.onRow(n.dim))
                            if let i = n.info {
                                Image(systemName: "info.circle").font(.system(size: 10 * theme.scale)).foregroundStyle(theme.onRow(true)).help(i)
                            }
                        }
                        if !n.facts.isEmpty {
                            Text(n.facts.prefix(4).joined(separator: " · ")).font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                        }
                    }
                    Spacer(minLength: 6)
                    if let m = n.main { LoadView(m: m, size: n.kind == .port ? 16 : 14).fixedSize() }
                }
                if let w = n.warn {
                    Label(w, systemImage: "exclamationmark.triangle.fill").font(theme.label(10)).foregroundStyle(theme.level(.warn))
                        .fixedSize(horizontal: false, vertical: true).padding(.leading, 23)
                }
            }
            .padding(.vertical, 5).padding(.horizontal, 6)
            .background(on ? theme.accent.opacity(0.12) : .clear, in: .rect(cornerRadius: 7))
            .contentShape(.rect)
            .onTapGesture { withAnimation(.snappy(duration: 0.2)) { sel = on ? nil : n.id } }
            // Smal venster: details onder de regel, binnen dezelfde lijnen
            if on && inline { NodeDetails(node: n, sums: sums).padding(.leading, 29).padding(.bottom, 8).transition(.opacity) }
            }
        }
        .fixedSize(horizontal: false, vertical: true)
    }
}

/// Verbindingslijn in een kolom van 16 pt: ├ (tee), └ (elbow), │ (pass) of niets.
private struct Connector: Shape {
    enum Kind { case tee, elbow, pass, none }
    let kind: Kind
    var y: CGFloat = 15   // hoogte van de naamregel
    func path(in r: CGRect) -> Path {
        var p = Path()
        let x = r.midX - 2
        switch kind {
        case .none: break
        case .pass: p.move(to: CGPoint(x: x, y: r.minY)); p.addLine(to: CGPoint(x: x, y: r.maxY))
        case .tee, .elbow:
            p.move(to: CGPoint(x: x, y: r.minY)); p.addLine(to: CGPoint(x: x, y: kind == .tee ? r.maxY : r.minY + y - 4))
            if kind == .elbow { p.addQuadCurve(to: CGPoint(x: x + 4, y: r.minY + y), control: CGPoint(x: x, y: r.minY + y)) }
            else { p.move(to: CGPoint(x: x, y: r.minY + y)) }
            p.addLine(to: CGPoint(x: r.maxX + 2, y: r.minY + y))
        }
        return p
    }
}

// MARK: - Samenvatting per poort (gedeeld door kop en lijst)

struct PortSum: Identifiable {
    let port: PortInfo, roots: [USBDev], tb: [TBDev], tbLink: TBLink?, notes: [PortNote], now: PowerNow?
    var id: String { port.id }

    static func all(_ s: PortsSnapshot) -> [PortSum] {
        let live = Ports.livePort(s.ports, s.power)
        return s.ports.map { p in
            let roots = s.usb.filter { p.usbRoots.contains($0.id) }
            let sock = String(p.number)
            let tb = p.isMagSafe ? [] : s.tb.filter { $0.socket == sock }
            let link = p.isMagSafe || !p.links.contains(where: { $0.type == "CIO" }) ? nil
                : s.tbLinks.filter { $0.socket == sock }.max { $0.width < $1.width }
            return PortSum(port: p, roots: roots, tb: tb, tbLink: link, notes: Ports.notes(p, roots: roots, tb: link), now: p.id == live ? s.power : nil)
        }
    }

    var devices: [USBDev] { Ports.devices(roots).filter { !$0.billboard } }
    var deviceCount: Int { devices.count + tb.count }
    /// Ernstigste oordeel over deze onderwerpen (leeg = alle).
    func level(_ topics: PortNote.Topic...) -> PortNote.Level? {
        let ls = notes.filter { topics.isEmpty || topics.contains($0.topic) }.map(\.level)
        return ls.contains(.warn) ? .warn : ls.contains(.info) ? .info : ls.isEmpty ? nil : .good
    }
    var status: (text: String, level: PortNote.Level)? {
        guard port.connected else { return nil }
        if let w = notes.first(where: { $0.level == .warn }) { return ("Check " + w.topic.noun, .warn) }
        return ("Good", .good)
    }
    var short: String { port.isMagSafe ? "MagSafe" : "Port \(port.number)" }
    var chargerW: Double? { port.charger?.winning?.watts }
    var nowW: Double? { now?.mW.flatMap { $0 > 0 ? Double($0) / 1000 : nil } }
    var dataBps: Double? {
        if let g = tbLink?.gbps { return Double(g) * 1e9 }
        if let b = roots.compactMap(\.bps).max() { return b }
        for l in port.links where l.type != "DisplayPort" {   // anders wat de poort meldt ("10 Gbps")
            if let r = l.rate, let v = Double(r.prefix { $0.isNumber || $0 == "." }) { return v * (r.contains("G") ? 1e9 : 1e6) }
        }
        return nil
    }
    var dataName: String {
        let t = Set(port.links.map(\.type))
        if t.contains("CIO") { return "Thunderbolt / USB4" }
        if t.contains("USB3") { return (dataBps ?? 0) >= 15e9 ? "USB 3.2 Gen 2×2" : (dataBps ?? 0) >= 6e9 ? "USB 3.2 Gen 2" : "USB 3.2 Gen 1" }
        if t.contains("USB2") { return "USB 2" }
        return t.contains("DisplayPort") ? "Video only" : "Power only"
    }
    /// Wat erin zit, in één of twee woorden, met icoon.
    var what: (name: String, icon: String) {
        if let t = tb.first { return (t.name, "bolt.horizontal.fill") }
        if roots.contains(where: \.hub) { return (port.charger != nil ? "Dock" : "Hub", "dock.rectangle") }
        if let r = roots.first { return (r.name, deviceIcon(r)) }
        if port.links.contains(where: { $0.type == "DisplayPort" }) { return ("Display", "display") }
        if port.charger != nil { return (port.isMagSafe ? "MagSafe charger" : "Charger", "powerplug.fill") }
        return (port.partnerKind?.capitalized ?? "Device", "cable.connector.horizontal")
    }
    var cableW: Int? { port.cable?.watts }
    var cableBps: Double? { port.cable?.speed.map { Ports.cableMax($0, thunderbolt: tbLink != nil) } }
    var cableKind: String {
        guard let c = port.cable else { return "no e-marker" }
        return port.optical ? "optical" : (c.active || port.activeCable) ? "active" : "passive"
    }
}

extension PortNote.Topic {
    var noun: String {
        switch self { case .port: "port"; case .power: "charger"; case .cable: "cable"; case .data: "speed"; case .devices: "devices" }
    }
}

/// Icoon bij een USB-apparaat, op naam (grof, alleen voor de herkenbaarheid).
func deviceIcon(_ d: USBDev) -> String {
    let n = d.name.lowercased()
    if d.hub { return "point.3.filled.connected.trianglepath.dotted" }
    if d.billboard { return "info.circle" }
    for (k, i) in [("keyboard", "keyboard"), ("mouse", "computermouse"), ("trackpad", "rectangle.and.hand.point.up.left"),
                   ("ethernet", "network"), ("lan", "network"), ("audio", "hifispeaker"), ("headset", "headphones"),
                   ("card", "sdcard"), ("reader", "sdcard"), ("ssd", "externaldrive"), ("disk", "externaldrive"), ("drive", "externaldrive"),
                   ("camera", "web.camera"), ("display", "display"), ("hdmi", "display"), ("phone", "iphone")] where n.contains(k) { return i }
    return "cable.connector.horizontal"
}

/// "10 Gbit/s" als getal + eenheid.
func speedParts(_ bps: Double) -> (String, String) { bps >= 1e9 ? (trim(bps / 1e9), "Gbit/s") : (trim(bps / 1e6), "Mbit/s") }

extension Theme {
    func level(_ l: PortNote.Level?) -> Color {
        if rowStyle == .blocks { return .white }
        switch l { case .warn: return warn; case .info: return accent; case .good: return good; case nil: return onRow(true) }
    }
    /// Zachte vlakvulling voor tegels en knopen.
    var tile: Color { rowStyle == .blocks ? .white.opacity(0.14) : onRow().opacity(0.06) }
}

// MARK: - Bouwstenen

/// Groot getal met kleine eenheid: "90" "W".
private struct BigNum: View {
    let value: String, unit: String
    var size: CGFloat = 26
    @Environment(\.theme) private var theme
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 3) {
            Text(value).font(theme.number(size)).foregroundStyle(theme.onRow())
            if !unit.isEmpty { Text(unit).font(theme.label(size * 0.42)).foregroundStyle(theme.onRow(true)) }
        }
        .lineLimit(1).minimumScaleFactor(0.6)
    }
}

private struct Badge: View {
    let level: PortNote.Level?
    @Environment(\.theme) private var theme
    var body: some View {
        if let l = level, l != .good {
            Image(systemName: l == .warn ? "exclamationmark.triangle.fill" : "info.circle.fill")
                .font(.system(size: 10 * theme.scale)).foregroundStyle(theme.level(l))
        }
    }
}

/// Inklapbaar blok met een chevron.
private struct Disclosure<Content: View>: View {
    let title: String
    var count: Int? = nil
    @ViewBuilder let content: Content
    @State private var open = false
    @Environment(\.theme) private var theme
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Button { withAnimation(.snappy(duration: 0.25)) { open.toggle() } } label: {
                HStack(spacing: 5) {
                    Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold)).rotationEffect(.degrees(open ? 90 : 0))
                    Text(title)
                    if let count { Text("\(count)").font(theme.number(10)).foregroundStyle(theme.onRow(true)) }
                    Spacer(minLength: 0)
                }
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).contentShape(.rect)
            }
            .buttonStyle(.plain)
            if open { content.transition(.opacity) }
        }
    }
}

/// Eén USB-apparaat: snelheid groot, de rest klein, ruwe waarden ingeklapt.
private struct DeviceCard: View {
    let dev: USBDev
    @Environment(\.theme) private var theme
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            Label(dev.name, systemImage: deviceIcon(dev)).font(theme.label(14).weight(.semibold))
            if let b = dev.bps { let x = speedParts(b); BigNum(value: x.0, unit: x.1, size: 22) }
            Text([dev.maker, dev.bps.map(Ports.usbSpeedName), dev.powerMA.map { "\($0) mA" }, String(format: "ID %04X:%04X", dev.vid, dev.pid)]
                    .compactMap { $0 }.joined(separator: " · "))
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
            Disclosure(title: "All values") { RawList(lines: dev.raw) }
        }
    }
}


// MARK: - Details (gedeeld): oordelen, kabel, lader, verbinding, aansluiting, apparaten, ruwe waarden

private struct PortDetails: View {
    let sum: PortSum
    var topics: Set<PortNote.Topic> = [.port, .power, .cable, .data, .devices]
    var openRaw = false
    @Environment(\.theme) private var theme
    private var port: PortInfo { sum.port }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            let notes = sum.notes.filter { topics.contains($0.topic) }
            if !notes.isEmpty { VStack(alignment: .leading, spacing: 4) { ForEach(notes, id: \.self) { NoteBox(note: $0) } } }
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 220), spacing: 14, alignment: .top)], alignment: .leading, spacing: 12) {
                if topics.contains(.cable) && !port.isMagSafe && port.connected { cableSection }
                if topics.contains(.power) && (!port.sources.isEmpty || port.charger != nil) { chargerSection }
                if topics.contains(.data) && port.connected { linkSection }
                if topics.contains(.port) { factsSection }
            }
            if topics.contains(.devices) && (!sum.roots.isEmpty || !sum.tb.isEmpty) {
                PortGroup("Devices behind this port", icon: "point.3.connected.trianglepath.dotted") { DeviceTree(roots: sum.roots, tb: sum.tb) }
            }
            if topics.contains(.port) {
                Disclosure(title: "All IOKit values") {
                    ForEach(port.raw, id: \.self) { g in
                        Text(g.title).font(theme.label(10).weight(.semibold)).padding(.top, 4)
                        RawList(lines: g.lines)
                    }
                }
            }
        }
        .foregroundStyle(theme.onRow())
    }

    private var cableSection: some View {
        PortGroup("Cable", icon: "cable.connector.horizontal") {
            if let c = port.cable {
                PortFact("Type", port.optical ? "Optical (active)" : (c.active || port.activeCable) ? "Active (has electronics)" : "Passive (no signal electronics)")
                if let s = c.speed { PortFact("Data", Ports.cableSpeedText(s)) }
                if let a = c.mA {
                    PortFact("Power", "\(amp(a))" + (c.mV.map { " at up to \(volt($0))" } ?? "") + (c.watts.map { " ≈ \($0) W" } ?? "") + (c.epr ? " (EPR)" : ""))
                }
                if let l = c.latency, (1...8).contains(l) {
                    PortFact("Length", l == 8 ? "longer than about 7 m (from signal delay)" : "about \(l) m (from signal delay)")
                }
                PortFact("E-marker ID", String(format: "%04X:%04X", c.vid, c.pid) + (c.pd.map { " · " + Ports.pdText($0) } ?? ""))
            } else {
                Text("No e-marker found. A plain USB-C cable carries up to 3 A (60 W); its data speed can't be read.")
                    .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                if port.activeCable { PortFact("Type", "Active (has electronics)") }
            }
        }
    }

    private var chargerSection: some View {
        PortGroup(port.isMagSafe ? "MagSafe charger" : "Charger", icon: "bolt.fill") {
            if let c = port.charger, let w = c.winning {
                PortFact("Agreed", "\(w.text) = \(trim(w.watts)) W")
                PortFact("Maximum", "\(trim(c.maxW)) W")
                if let n = sum.now?.adapterName { PortFact("Reported as", n) }
                if let p = sum.now, let t = Ports.nowText(p) { PortFact("Now", t.replacingOccurrences(of: "Drawing ", with: "")) }
            }
            ForEach(Array(port.sources.enumerated()), id: \.offset) { _, src in
                if src.options.count > 1 || src.winning != nil {
                    Text(src.source == "USB-PD" ? "Offered profiles" : src.source).font(theme.label(10)).foregroundStyle(theme.onRow(true)).padding(.top, 2)
                    ForEach(src.options, id: \.self) { o in
                        let on = o == src.winning
                        HStack(spacing: 6) {
                            Image(systemName: on ? "checkmark.circle.fill" : "circle").font(.system(size: 9))
                                .foregroundStyle(on ? theme.level(.good) : theme.onRow(true))
                            Text("\(volt(o.mV))  ·  \(amp(o.mA))  ·  \(trim(o.watts)) W").font(theme.number(11))
                            if on { Text("in use").font(theme.label(9)).foregroundStyle(theme.onRow(true)) }
                        }
                    }
                } else if let o = src.options.first {
                    PortFact(src.source == "TypeC" ? "Type-C current" : src.source, "\(volt(o.mV)) · \(amp(o.mA)) (\(trim(o.watts)) W)")
                }
            }
        }
    }

    private var linkSection: some View {
        PortGroup("Measured by the Mac", icon: "gauge.with.needle") {
            ForEach(port.links, id: \.self) { l in
                PortFact(Ports.transportName(l.type), Ports.linkText(l, tb: sum.tbLink))
                if let p = l.product { PortFact("  Device", [p, l.maker].compactMap { $0 }.joined(separator: " · ")) }
                if let r = l.role { PortFact("  Mac is", r.lowercased()) }
            }
            if port.links.isEmpty { PortFact("Data", "No data link (power only or not set up)") }
            if let k = port.partnerKind { PortFact("Device type", k + (port.partnerModal ? " (can switch modes)" : "")) }
            if let v = port.partnerVID { PortFact("Device ID", String(format: "%04X", v) + (port.partnerPID.map { String(format: ":%04X", $0) } ?? "")) }
            if let pd = port.partnerPD { PortFact("Talks", Ports.pdText(pd)) }
            if let s = port.links.first?.security { PortFact("Accessory", s) }
        }
    }

    private var factsSection: some View {
        PortGroup("Connection", icon: "info.circle") {
            if port.connected {
                PortFact("SuperSpeed", port.superSpeed ? "Yes" : "No")
                if !port.isMagSafe { PortFact("Cable", port.optical ? "Optical" : port.activeCable ? "Active" : "Passive") }
                if port.orientation > 0 { PortFact("Plug side", port.orientation == 1 ? "A (normal)" : "B (flipped)") }
            }
            if let n = port.plugEvents { PortFact("Plug events", "\(n)") }
            if let s = Ports.supportedText(port.supported) { PortFact("Can do", String(s.dropFirst(9))) }
            if port.connected {
                PortFact("Active", port.active.map(Ports.transportName).joined(separator: ", ").nonEmpty ?? "–")
            }
            if port.liquid { PortFact("Liquid", "Detected") }
        }
    }
}

private extension String { var nonEmpty: String? { isEmpty ? nil : self } }

/// Groep met kopje (icoon + naam in kleine letters).
private struct PortGroup<Content: View>: View {
    let title: String, icon: String
    @ViewBuilder let content: Content
    @Environment(\.theme) private var theme
    init(_ title: String, icon: String, @ViewBuilder content: () -> Content) { self.title = title; self.icon = icon; self.content = content() }
    var body: some View {
        VStack(alignment: .leading, spacing: 4) {
            Label(title.uppercased(), systemImage: icon).font(theme.label(10)).foregroundStyle(theme.onRow(true))
            content
        }
        .frame(maxWidth: .infinity, alignment: .leading)
    }
}

/// "Naam   waarde", de waarde loopt door op de volgende regel als het smal wordt.
private struct PortFact: View {
    let key: String, value: String
    @Environment(\.theme) private var theme
    init(_ key: String, _ value: String) { self.key = key; self.value = value }
    var body: some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text(key).foregroundStyle(theme.onRow(true)).frame(width: 84, alignment: .leading)
            Text(value).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
        }
        .font(theme.label(11))
    }
}

/// Oordeel: groen (in orde), oranje (let op) of accent (weetje).
private struct NoteBox: View {
    let note: PortNote
    @Environment(\.theme) private var theme
    var body: some View {
        let c = theme.level(note.level)
        HStack(alignment: .firstTextBaseline, spacing: 6) {
            Image(systemName: note.level == .good ? "checkmark.seal.fill" : note.level == .warn ? "exclamationmark.triangle.fill" : "info.circle.fill")
                .foregroundStyle(c)
            Text(note.text).foregroundStyle(theme.onRow()).fixedSize(horizontal: false, vertical: true)
            Spacer(minLength: 0)
        }
        .font(theme.label(11))
        .padding(.horizontal, 8).padding(.vertical, 5)
        .background(c.opacity(0.14), in: .rect(cornerRadius: 7))
    }
}

/// Ruwe sleutel/waarde-regels, selecteerbaar.
private struct RawList: View {
    let lines: [RawLine]
    @Environment(\.theme) private var theme
    var body: some View {
        VStack(alignment: .leading, spacing: 1) {
            ForEach(lines, id: \.self) { l in
                Text("\(Text(l.key).foregroundStyle(theme.onRow(true)))  \(Text(l.value).foregroundStyle(theme.onRow()))")
                    .font(.system(size: 10 * theme.scale, design: .monospaced))
                    .fixedSize(horizontal: false, vertical: true)
            }
        }
        .textSelection(.enabled)
    }
}

/// USB-boom (hubs in-/uitklapbaar, per apparaat snelheid; klik = alle waarden) plus Thunderbolt-apparaten.
private struct DeviceTree: View {
    let roots: [USBDev], tb: [TBDev]
    @State private var collapsed: Set<UInt64> = []
    @State private var open: Set<UInt64> = []
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(rows, id: \.dev.id) { USBRow(dev: $0.dev, depth: $0.depth, hubs: $0.hubs, collapsed: $collapsed, open: $open) }
            ForEach(tb) { TBRow(dev: $0, open: $open) }
        }
    }
    private var rows: [(dev: USBDev, depth: Int, hubs: Int)] {
        var out: [(USBDev, Int, Int)] = []
        func walk(_ d: USBDev, _ depth: Int, _ hubs: Int) {
            out.append((d, depth, hubs))
            guard !collapsed.contains(d.id) else { return }
            for c in d.children { walk(c, depth + 1, hubs + (d.hub ? 1 : 0)) }
        }
        roots.forEach { walk($0, 0, 0) }
        return out
    }
}

private struct USBRow: View {
    let dev: USBDev, depth: Int, hubs: Int
    @Binding var collapsed: Set<UInt64>
    @Binding var open: Set<UInt64>
    @Environment(\.theme) private var theme
    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(alignment: .top, spacing: 6) {
                if dev.hub && !dev.children.isEmpty {
                    Button { withAnimation(.snappy(duration: 0.2)) { toggle(&collapsed, dev.id) } } label: {
                        Image(systemName: "chevron.right").font(.system(size: 8, weight: .bold))
                            .rotationEffect(.degrees(collapsed.contains(dev.id) ? 0 : 90)).frame(width: 12, height: 14).contentShape(.rect)
                    }
                    .buttonStyle(.plain).help(collapsed.contains(dev.id) ? "Show devices on this hub" : "Hide devices on this hub")
                } else {
                    Color.clear.frame(width: 12, height: 1)
                }
                Image(systemName: deviceIcon(dev)).font(.system(size: 11)).frame(width: 16).foregroundStyle(theme.onRow(true))
                VStack(alignment: .leading, spacing: 1) {
                    HStack(spacing: 6) {
                        Text(dev.name).font(theme.label(12)).lineLimit(1).truncationMode(.tail)
                        Spacer(minLength: 4)
                        if let b = dev.bps { Text(Ports.speedText(b)).font(theme.number(11)) }
                    }
                    Text(detail).font(theme.label(10)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                }
            }
            .contentShape(.rect)
            .onTapGesture { withAnimation(.snappy(duration: 0.2)) { toggle(&open, dev.id) } }
            .help("Click for all values")
            if open.contains(dev.id) { RawList(lines: dev.raw).padding(.leading, 34).transition(.opacity) }
        }
        .padding(.leading, CGFloat(min(depth, 6)) * 14)
    }
    private var detail: String {
        var parts = [dev.maker]
        if let b = dev.bps { parts.append(Ports.usbSpeedName(b)) }
        if dev.hub { parts.append("\(dev.children.count) device\(dev.children.count == 1 ? "" : "s") on it") }
        if hubs > 0 { parts.append("via \(hubs) hub\(hubs == 1 ? "" : "s")") }
        if let p = dev.powerMA { parts.append("\(p) mA power budget") }
        if dev.billboard { parts.append("billboard (mode info)") }
        parts.append(String(format: "ID %04X:%04X", dev.vid, dev.pid))
        return parts.joined(separator: " · ")
    }
}

private struct TBRow: View {
    let dev: TBDev
    @Binding var open: Set<UInt64>
    @Environment(\.theme) private var theme
    var body: some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 6) {
                Image(systemName: "bolt.horizontal.fill").font(.system(size: 11)).frame(width: 16).foregroundStyle(theme.onRow(true))
                Text(dev.name).font(theme.label(12)).lineLimit(1)
                Text([dev.vendor, "Thunderbolt", dev.depth > 1 ? "\(dev.depth - 1) device\(dev.depth == 2 ? "" : "s") in between" : nil]
                        .compactMap { $0?.nonEmpty }.joined(separator: " · "))
                    .font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                Spacer(minLength: 0)
            }
            .contentShape(.rect)
            .onTapGesture { withAnimation(.snappy(duration: 0.2)) { toggle(&open, dev.id) } }
            if open.contains(dev.id) { RawList(lines: dev.raw).padding(.leading, 22) }
        }
    }
}

private func toggle(_ s: inout Set<UInt64>, _ id: UInt64) { if s.contains(id) { s.remove(id) } else { s.insert(id) } }
