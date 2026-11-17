import SwiftUI

// MARK: - Poorten-pagina: per fysieke poort wat erin zit, de kabel, de lader en de verbinding
//
// Tijdelijk drie lay-outs om te kiezen (instelling "portsLayout"):
//   a = kaart: de zijkant van de Mac met lijnen naar wat erin zit, details in een paneel
//   b = kaarten: per poort vier tegels met grote getallen, de rest achter "Details"
//   c = keten: per poort Mac → kabel → apparaat → apparaten, de zwakke schakel gemarkeerd
// Gegevens: PoortenData.swift; alleen bijgewerkt zolang deze pagina open is.

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
    var forced: String? = nil   // vaste lay-out (bijv. om te renderen)
    @AppStorage("portsLayout") private var layout = "c"
    @Environment(\.theme) private var theme

    var body: some View {
        let sums = PortSum.all(snap)
        let style = forced ?? layout
        VStack(alignment: .leading, spacing: 12) {
            header(style)
            if loaded && snap.ports.isEmpty {
                Text("No USB-C or MagSafe ports found on this Mac.").font(theme.label(12)).foregroundStyle(theme.secondary).padding(.vertical, 20)
            }
            switch style {
            case "a": PortsMap(sums: sums)
            case "b": PortsCards(sums: sums)
            default: PortsFlow(sums: sums)
            }
            if style != "a" { EmptyPorts(sums: sums) }
            otherDevices(sums)
        }
    }

    /// Bovenaan: wat de Mac nu binnenkrijgt en hoeveel poorten in gebruik zijn; tijdelijk de lay-outkeuze.
    private func header(_ style: String) -> some View {
        let used = snap.ports.filter(\.connected).count
        let watts = snap.power.flatMap { $0.external ? $0.mW : nil }.map { trim(Double($0) / 1000) }
        return HStack(spacing: 8) {
            if let watts { Text("\(Image(systemName: "bolt.fill")) \(watts) W in") .foregroundStyle(theme.onRow()) }
            Text("\(used) of \(snap.ports.count) ports in use")
            Spacer(minLength: 4)
            if forced == nil {   // TIJDELIJK: lay-out kiezen
                Picker("", selection: $layout) { Text("Map").tag("a"); Text("Cards").tag("b"); Text("Flow").tag("c") }
                    .pickerStyle(.segmented).labelsHidden().fixedSize().controlSize(.small)
            }
        }
        .font(theme.label(11)).foregroundStyle(theme.secondary).lineLimit(1)
        .padding(.top, 2)
    }

    @ViewBuilder private func otherDevices(_ sums: [PortSum]) -> some View {
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

// MARK: - Samenvatting per poort (gedeeld door de drie lay-outs)

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

private struct StatusPill: View {
    let text: String, level: PortNote.Level
    @Environment(\.theme) private var theme
    var body: some View {
        let c = theme.level(level)
        Label(text, systemImage: level == .warn ? "exclamationmark.triangle.fill" : "checkmark.circle.fill")
            .font(theme.label(11)).foregroundStyle(c).lineLimit(1)
            .padding(.horizontal, 8).padding(.vertical, 3)
            .background(c.opacity(0.15), in: .capsule)
    }
}

/// Klein merkteken voor een oordeel: driehoek (let op) of i (weetje).
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

/// Lege poorten op één rustige regel.
private struct EmptyPorts: View {
    let sums: [PortSum]
    @Environment(\.theme) private var theme
    var body: some View {
        let empty = sums.filter { !$0.port.connected }
        if !empty.isEmpty {
            HStack(spacing: 6) {
                Image(systemName: "circle.dashed")
                Text("Empty: " + empty.map { $0.port.isMagSafe ? $0.port.kind : "USB-C \($0.port.number)" }.joined(separator: " · "))
                ForEach(empty.flatMap(\.notes).filter { $0.level == .warn }, id: \.self) { n in Badge(level: n.level).help(n.text) }
            }
            .font(theme.label(11)).foregroundStyle(theme.onRow(true)).padding(.horizontal, 2)
        }
    }
}

// MARK: - A. Kaart: de Mac met lijnen naar wat erin zit

/// Lijn van het gaatje naar de kabel: op de hoogte van de keten, ook als de apparaten eronder staan.
private extension VerticalAlignment {
    enum ChainMid: AlignmentID { static func defaultValue(in d: ViewDimensions) -> CGFloat { d[VerticalAlignment.center] } }
    static let chainMid = VerticalAlignment(ChainMid.self)
}

private enum MapSel: Hashable { case port(String), cable(String), device(String), child(String, UInt64) }

private struct PortsMap: View {
    let sums: [PortSum]
    @State private var sel: MapSel?
    @Environment(\.theme) private var theme

    var body: some View {
        let s = sel ?? sums.first(where: \.port.connected).map { .device($0.id) }
        ViewThatFits(in: .horizontal) {
            HStack(alignment: .top, spacing: 16) {
                diagram(s).frame(maxWidth: .infinity, alignment: .leading)
                panel(s).frame(width: 400)
            }
            .frame(minWidth: 860)
            VStack(alignment: .leading, spacing: 14) { diagram(s); panel(s) }
        }
    }

    private func diagram(_ s: MapSel?) -> some View {
        VStack(alignment: .leading, spacing: 18) {
            ForEach(sums) { row($0, s) }
        }
        .padding(.vertical, 34)
        .background(alignment: .topLeading) {
            // De Mac van opzij: een plaat met de poorten op de rand
            RoundedRectangle(cornerRadius: 12).fill(theme.tile)
                .overlay(RoundedRectangle(cornerRadius: 12).stroke(theme.onRow(true).opacity(0.35), lineWidth: 1))
                .overlay(alignment: .top) {
                    Image(systemName: "laptopcomputer").font(.system(size: 13)).foregroundStyle(theme.onRow(true)).padding(.top, 9)
                }
                .frame(width: 44)
        }
    }

    private func row(_ p: PortSum, _ s: MapSel?) -> some View {
        HStack(alignment: .chainMid, spacing: 0) {
            socket(p, s)
            if p.port.connected {
                ViewThatFits(in: .horizontal) {
                    HStack(spacing: 0) { chain(p, s).alignmentGuide(.chainMid) { $0[VerticalAlignment.center] }; children(p, s) }
                    VStack(alignment: .leading, spacing: 6) {
                        chain(p, s).alignmentGuide(.chainMid) { $0[VerticalAlignment.center] }
                        children(p, s).padding(.leading, 60)
                    }
                }
            } else {
                Text(p.port.isMagSafe ? "MagSafe · empty" : "Empty").font(theme.label(10)).foregroundStyle(theme.onRow(true).opacity(0.7)).padding(.leading, 10)
            }
        }
    }

    /// Het gaatje in de rand van de Mac, met nummer.
    private func socket(_ p: PortSum, _ s: MapSel?) -> some View {
        let on = s == .port(p.id)
        return Button { sel = .port(p.id) } label: {
            HStack(spacing: 6) {
                Text(p.port.isMagSafe ? "M" : "\(p.port.number)").font(theme.number(11)).foregroundStyle(theme.onRow(true)).frame(width: 14)
                Capsule().fill(p.port.connected ? theme.level(p.level(.port) ?? .good) : theme.onRow(true).opacity(0.25))
                    .frame(width: 22, height: 9)
                    .overlay(Capsule().stroke(on ? theme.accent : .clear, lineWidth: 2).padding(-3))
            }
            .padding(.leading, 12).contentShape(.rect)
        }
        .buttonStyle(.plain).help(p.port.title)
    }

    private func line(_ l: PortNote.Level?, _ w: CGFloat = 22) -> some View {
        Rectangle().fill(l == .warn ? theme.warn : theme.onRow(true).opacity(0.45)).frame(width: w, height: 2)
    }

    /// Kabel → apparaat (dock, lader, schijf …).
    private func chain(_ p: PortSum, _ s: MapSel?) -> some View {
        HStack(spacing: 0) {
            if !p.port.isMagSafe {
                line(p.level(.cable), 10)
                node(icon: "cable.connector.horizontal", title: "Cable",
                     value: p.cableW.map { ("\($0)", "W") } ?? ("—", ""), level: p.level(.cable), on: s == .cable(p.id)) { sel = .cable(p.id) }
            }
            line(p.level(.cable))
            let w = p.what
            let v: (String, String)? = p.chargerW.map { (trim($0), "W") } ?? p.dataBps.map(speedParts)
            node(icon: w.icon, title: w.name, value: v ?? ("", ""), level: p.level(.power, .data), on: s == .device(p.id)) { sel = .device(p.id) }
        }
    }

    @ViewBuilder private func children(_ p: PortSum, _ s: MapSel?) -> some View {
        let devs = p.devices
        if !devs.isEmpty {
            HStack(spacing: 0) {
                line(nil, 14)
                VStack(alignment: .leading, spacing: 5) {
                    ForEach(devs.prefix(6)) { d in
                        HStack(spacing: 0) {
                            line(nil, 10)
                            leaf(d, on: s == .child(p.id, d.id)) { sel = .child(p.id, d.id) }
                        }
                    }
                    if devs.count > 6 { Text("+\(devs.count - 6) more").font(theme.label(10)).foregroundStyle(theme.onRow(true)).padding(.leading, 14) }
                }
                .overlay(alignment: .leading) {   // de stam waar de takjes aan hangen
                    Rectangle().fill(theme.onRow(true).opacity(0.45)).frame(width: 2).padding(.vertical, 11)
                }
            }
        }
    }

    private func node(icon: String, title: String, value: (String, String), level: PortNote.Level?, on: Bool, tap: @escaping () -> Void) -> some View {
        Button(action: tap) {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    Image(systemName: icon).font(.system(size: 10 * theme.scale))
                    Text(title).lineLimit(1)
                    Badge(level: level)
                }
                .font(theme.label(10)).foregroundStyle(theme.onRow(true))
                if !value.0.isEmpty { BigNum(value: value.0, unit: value.1, size: 18) }
            }
            .padding(.horizontal, 9).padding(.vertical, 6)
            .background(theme.tile, in: .rect(cornerRadius: 9))
            .overlay(RoundedRectangle(cornerRadius: 9).stroke(on ? theme.accent : level == .warn ? theme.warn.opacity(0.7) : .clear, lineWidth: 1.5))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
    }

    private func leaf(_ d: USBDev, on: Bool, tap: @escaping () -> Void) -> some View {
        Button(action: tap) {
            HStack(spacing: 5) {
                Image(systemName: deviceIcon(d)).font(.system(size: 10 * theme.scale)).foregroundStyle(theme.onRow(true)).frame(width: 14)
                Text(d.name).font(theme.label(11)).lineLimit(1).frame(maxWidth: 150, alignment: .leading)
                if let b = d.bps { Text(speedParts(b).0 + " " + (b >= 1e9 ? "G" : "M")).font(theme.number(10)).foregroundStyle(theme.onRow(true)) }
            }
            .padding(.horizontal, 7).padding(.vertical, 4)
            .background(theme.tile, in: .capsule)
            .overlay(Capsule().stroke(on ? theme.accent : .clear, lineWidth: 1.5))
            .contentShape(.capsule)
        }
        .buttonStyle(.plain)
    }

    // MARK: Paneel met details van wat aangeklikt is

    @ViewBuilder private func panel(_ s: MapSel?) -> some View {
        Group {
            switch s {
            case .port(let id)?: if let p = find(id) { panelBody(p.port.title, p, numbers: [], topics: [.port]) }
            case .cable(let id)?:
                if let p = find(id) {
                    panelBody("Cable in \(p.port.title)", p, numbers: [p.cableW.map { ("\($0)", "W", "power") }, p.cableBps.map { let x = speedParts($0); return (x.0, x.1, "data") }].compactMap { $0 },
                              topics: [.cable])
                }
            case .device(let id)?:
                if let p = find(id) {
                    panelBody(p.what.name, p, numbers: [p.chargerW.map { (trim($0), "W", "charger") }, p.nowW.map { (trim($0), "W", "in now") },
                                                         p.dataBps.map { let x = speedParts($0); return (x.0, x.1, p.dataName) }].compactMap { $0 },
                              topics: [.power, .data])
                }
            case .child(let id, let dev)?:
                if let p = find(id), let d = p.devices.first(where: { $0.id == dev }) { DeviceCard(dev: d) }
            case nil: Text("Click a port, cable or device.").font(theme.label(11)).foregroundStyle(theme.onRow(true))
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .themedRow(theme)
    }

    private func find(_ id: String) -> PortSum? { sums.first { $0.id == id } }

    private func panelBody(_ title: String, _ p: PortSum, numbers: [(String, String, String)], topics: Set<PortNote.Topic>) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(theme.label(14).weight(.semibold))
            if !numbers.isEmpty {
                HStack(alignment: .top, spacing: 16) {
                    ForEach(numbers, id: \.2) { n in
                        VStack(alignment: .leading, spacing: 0) {
                            BigNum(value: n.0, unit: n.1, size: 22)
                            Text(n.2).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                        }
                    }
                }
            }
            ForEach(p.notes.filter { topics.contains($0.topic) && $0.level != .good }, id: \.self) { NoteBox(note: $0) }
            Disclosure(title: "Details") { PortDetails(sum: p, topics: topics) }
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

// MARK: - B. Kaarten met grote getallen

private struct PortsCards: View {
    let sums: [PortSum]
    @Environment(\.theme) private var theme

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(Array(sums.filter(\.port.connected).enumerated()), id: \.element.id) { i, p in card(p).themedRow(theme, index: i) }
        }
    }

    private func card(_ p: PortSum) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            HStack(spacing: 10) {
                Image(systemName: p.what.icon).font(.system(size: 15, weight: .semibold)).foregroundStyle(theme.accent)
                    .frame(width: 32, height: 32).background(theme.accent.opacity(0.14), in: .circle)
                VStack(alignment: .leading, spacing: 0) {
                    Text(p.what.name).font(theme.label(15).weight(.semibold))
                    Text(p.port.title).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                }
                Spacer(minLength: 6)
                if let s = p.status { StatusPill(text: s.text, level: s.level) }
            }
            LazyVGrid(columns: Array(repeating: GridItem(.flexible(minimum: 80, maximum: 230), spacing: 8, alignment: .top), count: p.port.isMagSafe ? 3 : 4),
                      alignment: .leading, spacing: 8) {
                tile("Power", "bolt.fill", p.chargerW.map { (trim($0), "W") },
                     p.nowW.map { "\(trim($0)) W in now" } ?? (p.port.charger == nil ? "no charger" : "charger"), p.level(.power))
                tile("Data", "arrow.left.arrow.right", p.dataBps.map(speedParts), p.dataName, p.level(.data))
                if !p.port.isMagSafe {
                    tile("Cable", "cable.connector.horizontal", p.cableW.map { ("\($0)", "W") },
                         p.cableBps.map { speedParts($0).0 + " " + speedParts($0).1 } ?? p.cableKind, p.level(.cable))
                }
                tile("Devices", "point.3.connected.trianglepath.dotted", p.deviceCount > 0 ? ("\(p.deviceCount)", "") : nil,
                     p.deviceCount > 0 ? "behind " + p.what.name.lowercased() : "none", p.level(.devices))
            }
            ForEach(p.notes.filter { $0.level == .warn }, id: \.self) { NoteBox(note: $0) }
            Disclosure(title: "Details") { PortDetails(sum: p) }
        }
    }

    private func tile(_ title: String, _ icon: String, _ v: (String, String)?, _ sub: String, _ l: PortNote.Level?) -> some View {
        VStack(alignment: .leading, spacing: 3) {
            HStack(spacing: 4) {
                Image(systemName: icon).font(.system(size: 9 * theme.scale))
                Text(title.uppercased()).lineLimit(1)
                Spacer(minLength: 0)
                Badge(level: l)
            }
            .font(theme.label(9)).foregroundStyle(theme.onRow(true))
            if let v { BigNum(value: v.0, unit: v.1, size: 26) } else { Text("—").font(theme.number(26)).foregroundStyle(theme.onRow(true)) }
            Text(sub).font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(1).truncationMode(.tail)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(10)
        .background(theme.tile, in: .rect(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(l == .warn ? theme.warn.opacity(0.7) : .clear, lineWidth: 1.5))
    }
}

// MARK: - C. Keten: Mac → kabel → apparaat → apparaten

private struct PortsFlow: View {
    let sums: [PortSum]
    @State private var pick: [String: PortNote.Topic] = [:]   // aangeklikte schakel per poort
    @Environment(\.theme) private var theme

    private struct Step: Identifiable {
        let id: PortNote.Topic, icon: String, title: String, value: (String, String)?, sub: String, level: PortNote.Level?
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            ForEach(Array(sums.filter(\.port.connected).enumerated()), id: \.element.id) { i, p in card(p).themedRow(theme, index: i) }
        }
    }

    private func steps(_ p: PortSum) -> [Step] {
        var out = [Step(id: .port, icon: p.port.isMagSafe ? "bolt.horizontal.fill" : "laptopcomputer", title: p.short,
                        value: p.nowW.map { (trim($0), "W") }, sub: p.nowW == nil ? (p.port.isMagSafe ? "MagSafe" : "USB-C") : "into the Mac",
                        level: p.level(.port))]
        if !p.port.isMagSafe {
            out.append(Step(id: .cable, icon: "cable.connector.horizontal", title: "Cable", value: p.cableW.map { ("\($0)", "W") },
                            sub: p.cableBps.map { speedParts($0).0 + " " + speedParts($0).1 } ?? p.cableKind, level: p.level(.cable)))
        }
        out.append(Step(id: .power, icon: p.what.icon, title: p.what.name, value: p.chargerW.map { (trim($0), "W") } ?? p.dataBps.map(speedParts),
                        sub: p.chargerW != nil ? "charger" : p.dataName, level: p.level(.power)))
        if p.deviceCount > 0 || p.dataBps != nil && p.chargerW != nil {
            out.append(Step(id: .data, icon: "point.3.connected.trianglepath.dotted", title: "Data",
                            value: p.dataBps.map(speedParts), sub: p.deviceCount == 1 ? "1 device" : "\(p.deviceCount) devices", level: p.level(.data, .devices)))
        }
        return out
    }

    private func card(_ p: PortSum) -> some View {
        let st = steps(p)
        let picked = pick[p.id]
        let weak = p.notes.first { $0.level == .warn } ?? p.notes.first { $0.level == .info }
        return VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                Text(p.what.name).font(theme.label(14).weight(.semibold))
                Text(p.port.title).font(theme.label(10)).foregroundStyle(theme.onRow(true))
                Spacer(minLength: 6)
                if let s = p.status { StatusPill(text: s.text, level: s.level) }
            }
            ViewThatFits(in: .horizontal) {
                HStack(spacing: 0) { chain(p, st, picked).frame(maxWidth: 640); leaves(p); Spacer(minLength: 0) }.frame(minWidth: 860)
                chain(p, st, picked)
                VStack(alignment: .leading, spacing: 0) {
                    ForEach(Array(st.enumerated()), id: \.element.id) { i, s in
                        if i > 0 { arrow(s.level, vertical: true).padding(.leading, 18) }
                        chip(p, s, on: picked == s.id)
                    }
                }
            }
            if let weak, picked == nil {
                Button { withAnimation(.snappy(duration: 0.25)) { pick[p.id] = weak.topic == .devices ? .data : weak.topic } } label: {
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Badge(level: weak.level)
                        Text(weak.text).lineLimit(2).multilineTextAlignment(.leading)
                        Spacer(minLength: 0)
                    }
                    .font(theme.label(11)).foregroundStyle(theme.onRow(true)).contentShape(.rect)
                }
                .buttonStyle(.plain)
            }
            if let picked {
                PortDetails(sum: p, topics: picked == .data ? [.data, .devices] : picked == .power ? [.power] : [picked]).transition(.opacity)
            }
        }
    }

    private func chain(_ p: PortSum, _ st: [Step], _ picked: PortNote.Topic?) -> some View {
        HStack(spacing: 0) {
            ForEach(Array(st.enumerated()), id: \.element.id) { i, s in
                if i > 0 { arrow(s.level, vertical: false) }
                chip(p, s, on: picked == s.id)
            }
        }
    }

    /// Breed venster: de apparaten zelf achter de laatste schakel.
    @ViewBuilder private func leaves(_ p: PortSum) -> some View {
        if !p.devices.isEmpty {
            arrow(nil, vertical: false)
            VStack(alignment: .leading, spacing: 3) {
                ForEach(p.devices.prefix(5)) { d in
                    HStack(spacing: 5) {
                        Image(systemName: deviceIcon(d)).frame(width: 14)
                        Text(d.name).lineLimit(1).foregroundStyle(theme.onRow())
                        if let b = d.bps { Text(Ports.speedText(b)).font(theme.number(10)) }
                    }
                }
                if p.devices.count > 5 { Text("+\(p.devices.count - 5) more") }
            }
            .font(theme.label(11)).foregroundStyle(theme.onRow(true)).frame(maxWidth: 300, alignment: .leading)
        }
    }

    private func arrow(_ l: PortNote.Level?, vertical: Bool) -> some View {
        Image(systemName: vertical ? "chevron.down" : "chevron.right").font(.system(size: 10, weight: .bold))
            .foregroundStyle(l == .warn ? theme.warn : theme.onRow(true).opacity(0.6))
            .frame(width: vertical ? nil : 16, height: vertical ? 14 : nil)
    }

    private func chip(_ p: PortSum, _ s: Step, on: Bool) -> some View {
        Button { withAnimation(.snappy(duration: 0.25)) { pick[p.id] = on ? nil : s.id } } label: {
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 4) {
                    Image(systemName: s.icon).font(.system(size: 9 * theme.scale))
                    Text(s.title).lineLimit(1)
                    Spacer(minLength: 0)
                    Badge(level: s.level)
                }
                .font(theme.label(10)).foregroundStyle(theme.onRow(true))
                if let v = s.value { BigNum(value: v.0, unit: v.1, size: 20) } else { Text("—").font(theme.number(20)).foregroundStyle(theme.onRow(true)) }
                Text(s.sub).font(theme.label(10)).foregroundStyle(theme.onRow(true)).lineLimit(1)
            }
            .padding(.horizontal, 9).padding(.vertical, 7)
            .frame(minWidth: 76, maxWidth: .infinity, alignment: .leading)
            .background(theme.tile, in: .rect(cornerRadius: 10))
            .overlay(RoundedRectangle(cornerRadius: 10).stroke(on ? theme.accent : s.level == .warn ? theme.warn.opacity(0.7) : .clear, lineWidth: 1.5))
            .contentShape(.rect)
        }
        .buttonStyle(.plain)
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
