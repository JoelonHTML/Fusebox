import SwiftUI

// MARK: - Poorten-pagina: per fysieke poort wat erin zit, de kabel, de lader en de verbinding
//
// Bovenaan per poort een kaart met kop en oordelen (groen/oranje), daaronder groepen (Kabel, Lader, Verbinding,
// Apparaten, Aansluiting) in een raster dat bij een smal venster onder elkaar valt. Ruwe IOKit-waarden ingeklapt.
// Gegevens: PoortenData.swift; alleen bijgewerkt zolang deze pagina open is.

struct PortsView: View {
    @StateObject private var watcher = PortWatcher()
    @Environment(\.theme) private var theme
    @State private var technical: Set<String> = []       // poorten met open "Technical details"
    @State private var collapsed: Set<UInt64> = []        // ingeklapte hubs
    @State private var openDevice: Set<UInt64> = []       // USB-/Thunderbolt-apparaten met open details

    var body: some View {
        let s = watcher.snap
        let live = Ports.livePort(s.ports, s.power)
        let claimed = Set(s.ports.flatMap(\.usbRoots))
        let other = s.usb.filter { !claimed.contains($0.id) }
        let sockets = Set(s.ports.filter { !$0.isMagSafe }.map { String($0.number) })
        let otherTB = s.tb.filter { $0.socket.map { !sockets.contains($0) } ?? true }
        ScrollView {
            VStack(alignment: .leading, spacing: 10) {
                summary(s, live: live)
                if watcher.loaded && s.ports.isEmpty {
                    Text("No USB-C or MagSafe ports found on this Mac.").font(theme.label(12)).foregroundStyle(theme.secondary).padding(.vertical, 20)
                }
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 380), spacing: 10, alignment: .top)], alignment: .leading, spacing: 10) {
                    ForEach(Array(s.ports.enumerated()), id: \.element.id) { i, p in
                        PortCard(port: p, index: i, snap: s, power: p.id == live ? s.power : nil,
                                 technical: binding(p.id, in: $technical), collapsed: $collapsed, openDevice: $openDevice)
                    }
                }
                if !other.isEmpty || !otherTB.isEmpty {
                    VStack(alignment: .leading, spacing: 6) {
                        Text("Other connected devices").font(theme.label(12).weight(.semibold)).foregroundStyle(theme.onRow())
                        Text("Not linked to a specific port (for example built-in or behind a Thunderbolt dock).")
                            .font(theme.label(10)).foregroundStyle(theme.onRow(true))
                        USBTree(roots: other, collapsed: $collapsed, open: $openDevice)
                        ForEach(otherTB) { TBRow(dev: $0, open: $openDevice) }
                    }
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .themedRow(theme, index: s.ports.count)
                }
            }
            .padding(.horizontal, 12).padding(.bottom, 12)
        }
        .clipped()   // niet onder de doorzichtige werkbalk doorschuiven
        .onAppear { watcher.start() }
        .onDisappear { watcher.stop() }
    }

    private func binding(_ id: String, in set: Binding<Set<String>>) -> Binding<Bool> {
        Binding(get: { set.wrappedValue.contains(id) }, set: { if $0 { set.wrappedValue.insert(id) } else { set.wrappedValue.remove(id) } })
    }

    /// Eén regel bovenaan: hoeveel poorten, hoeveel in gebruik, en wat de Mac nu binnenkrijgt.
    private func summary(_ s: PortsSnapshot, live: String?) -> some View {
        let used = s.ports.filter(\.connected).count
        let watts = s.power.flatMap { $0.external ? $0.mW : nil }.map { trim(Double($0) / 1000) }
        return HStack(spacing: 8) {
            Text("\(s.ports.count) port\(s.ports.count == 1 ? "" : "s") · \(used) in use")
            if let watts { Text("· power in \(watts) W") }
            Spacer(minLength: 4)
            Button {
                let all = Set(s.ports.map(\.id))
                withAnimation(.snappy(duration: 0.25)) { technical = technical == all ? [] : all }
            } label: {
                NetPill(title: technical.isEmpty ? "Show technical details" : "Hide technical details", icon: "list.bullet.indent")
            }
            .buttonStyle(.plain).help("All raw IOKit values for every port")
        }
        .font(theme.label(11)).foregroundStyle(theme.secondary).lineLimit(1)
        .padding(.top, 2)
    }
}

// MARK: - Kaart per poort

private struct PortCard: View {
    let port: PortInfo, index: Int, snap: PortsSnapshot, power: PowerNow?
    @Binding var technical: Bool
    @Binding var collapsed: Set<UInt64>
    @Binding var openDevice: Set<UInt64>
    @Environment(\.theme) private var theme

    var body: some View {
        let roots = snap.usb.filter { port.usbRoots.contains($0.id) }
        let tb = port.isMagSafe ? [] : snap.tb.filter { $0.socket == String(port.number) }
        let link = port.isMagSafe ? nil : snap.tbLinks.filter { $0.socket == String(port.number) }.max { $0.width < $1.width }
        let tbLink = port.links.contains { $0.type == "CIO" } ? link : nil
        let notes = Ports.notes(port, roots: roots, tb: tbLink)
        VStack(alignment: .leading, spacing: 10) {
            header(roots: roots, tb: tb, tbLink: tbLink)
            if !notes.isEmpty {
                VStack(alignment: .leading, spacing: 4) { ForEach(notes, id: \.self) { NoteBox(note: $0) } }
            }
            if port.connected {
                LazyVGrid(columns: [GridItem(.adaptive(minimum: 220), spacing: 14, alignment: .top)], alignment: .leading, spacing: 12) {
                    if !port.isMagSafe { cableSection }
                    if !port.sources.isEmpty || port.charger != nil { chargerSection }
                    linkSection(tbLink)
                    factsSection
                }
                if !roots.isEmpty || !tb.isEmpty {
                    PortGroup("Devices behind this port", icon: "point.3.connected.trianglepath.dotted") {
                        USBTree(roots: roots, collapsed: $collapsed, open: $openDevice)
                        ForEach(tb) { TBRow(dev: $0, open: $openDevice) }
                    }
                }
            } else {
                Text(port.isMagSafe ? "Plug in a MagSafe charger to see its power and how it's charging."
                     : "Plug in a cable, charger or device to see its speed, power and the cable's e-marker.")
                    .font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                factsSection
            }
            technicalToggle
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .foregroundStyle(theme.onRow())
        .themedRow(theme, index: index)
    }

    private func header(roots: [USBDev], tb: [TBDev], tbLink: TBLink?) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: port.isMagSafe ? "bolt.horizontal.fill" : "cable.connector")
                .font(.system(size: 15, weight: .semibold))
                .foregroundStyle(port.connected ? (theme.rowStyle == .blocks ? .white : theme.accent) : theme.onRow(true))
                .frame(width: 30, height: 30)
                .background((theme.rowStyle == .blocks ? Color.white : theme.accent).opacity(port.connected ? 0.16 : 0.07), in: .circle)
            VStack(alignment: .leading, spacing: 2) {
                HStack(spacing: 6) {
                    Text(port.title).font(theme.label(11)).foregroundStyle(theme.onRow(true))
                    Text(port.connected ? "In use" : "Empty").font(theme.label(9))
                        .padding(.horizontal, 5).padding(.vertical, 1)
                        .foregroundStyle(port.connected ? (theme.rowStyle == .blocks ? .white : theme.good) : theme.onRow(true))
                        .background((port.connected ? (theme.rowStyle == .blocks ? .white : theme.good) : theme.onRow(true)).opacity(0.15), in: .capsule)
                }
                Text(Ports.headline(port, roots: roots, tb: tb)).font(theme.label(14).weight(.semibold))
                    .fixedSize(horizontal: false, vertical: true)
                if let d = Ports.dataLine(port, tb: tbLink) {
                    Text(d).font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                } else if let s = Ports.supportedText(port.supported) {
                    Text(s).font(theme.label(11)).foregroundStyle(theme.onRow(true)).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: 0)
        }
    }

    // MARK: Groepen

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
                PortFact("Vendor ID", String(format: "0x%04X", c.vid))
                PortFact("Product ID", String(format: "0x%04X", c.pid))
                if let pd = c.pd { PortFact("E-marker", Ports.pdText(pd)) }
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
                if let n = power?.adapterName { PortFact("Reported as", n) }
                if let p = power, let t = Ports.nowText(p) { PortFact("Now", t.replacingOccurrences(of: "Drawing ", with: "")) }
            }
            ForEach(Array(port.sources.enumerated()), id: \.offset) { _, src in
                if src.options.count > 1 || src.winning != nil {
                    Text(src.source == "USB-PD" ? "Offered profiles" : src.source).font(theme.label(10)).foregroundStyle(theme.onRow(true)).padding(.top, 2)
                    ForEach(src.options, id: \.self) { o in
                        let on = o == src.winning
                        HStack(spacing: 6) {
                            Image(systemName: on ? "checkmark.circle.fill" : "circle").font(.system(size: 9))
                                .foregroundStyle(on ? (theme.rowStyle == .blocks ? .white : theme.good) : theme.onRow(true))
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

    private func linkSection(_ tb: TBLink?) -> some View {
        PortGroup("Measured by the Mac", icon: "gauge.with.needle") {
            ForEach(port.links, id: \.self) { l in
                PortFact(Ports.transportName(l.type), Ports.linkText(l, tb: tb))
                if let p = l.product { PortFact("  Device", [p, l.maker].compactMap { $0 }.joined(separator: " · ")) }
                if let r = l.role { PortFact("  Mac is", r.lowercased()) }
            }
            if port.links.isEmpty { PortFact("Data", "No data link (power only or not set up)") }
            if let k = port.partnerKind { PortFact("Device type", k + (port.partnerModal ? " (can switch modes)" : "")) }
            if let v = port.partnerVID { PortFact("Device ID", String(format: "0x%04X", v) + (port.partnerPID.map { String(format: " : 0x%04X", $0) } ?? "")) }
            if let pd = port.partnerPD { PortFact("Talks", Ports.pdText(pd)) }
            if let s = port.links.first?.security { PortFact("Accessory", s) }
        }
    }

    private var factsSection: some View {
        PortGroup("Connection", icon: "info.circle") {
            PortFact("Connected", port.connected ? "Yes" : "No")
            if port.connected {
                PortFact("SuperSpeed", port.superSpeed ? "Yes" : "No")
                if !port.isMagSafe {
                    PortFact("Active cable", port.activeCable ? "Yes" : "No")
                    PortFact("Optical", port.optical ? "Yes" : "No")
                }
                if port.orientation > 0 { PortFact("Plug side", port.orientation == 1 ? "A (normal)" : "B (flipped)") }
            }
            if let n = port.plugEvents { PortFact("Plug events", "\(n) counted by the port") }
            if let s = Ports.supportedText(port.supported) { PortFact("Can do", String(s.dropFirst(9))) }
            if port.connected {
                PortFact("Set up", port.provisioned.map(Ports.transportName).joined(separator: ", ").nonEmpty ?? "–")
                PortFact("Active", port.active.map(Ports.transportName).joined(separator: ", ").nonEmpty ?? "–")
            }
            if port.liquid { PortFact("Liquid", "Detected") }
        }
    }

    private var technicalToggle: some View {
        VStack(alignment: .leading, spacing: 6) {
            Button { withAnimation(.snappy(duration: 0.25)) { technical.toggle() } } label: {
                HStack(spacing: 4) {
                    Image(systemName: "chevron.right").font(.system(size: 9, weight: .bold)).rotationEffect(.degrees(technical ? 90 : 0))
                    Text("Technical details")
                }
                .font(theme.label(11)).foregroundStyle(theme.onRow(true)).contentShape(.rect)
            }
            .buttonStyle(.plain)
            if technical {
                ForEach(port.raw, id: \.self) { g in
                    Text(g.title).font(theme.label(10).weight(.semibold)).padding(.top, 4)
                    RawList(lines: g.lines)
                }
                .transition(.opacity)
            }
        }
    }
}

private extension String { var nonEmpty: String? { isEmpty ? nil : self } }

// MARK: - Bouwstenen

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
        let c: Color = theme.rowStyle == .blocks ? .white : note.level == .good ? theme.good : note.level == .warn ? theme.warn : theme.accent
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

/// USB-boom: hubs in- en uitklapbaar, per apparaat snelheid, aantal hubs ertussen en (op klik) alle ruwe waarden.
private struct USBTree: View {
    let roots: [USBDev]
    @Binding var collapsed: Set<UInt64>
    @Binding var open: Set<UInt64>
    var body: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(rows, id: \.dev.id) { USBRow(dev: $0.dev, depth: $0.depth, hubs: $0.hubs, collapsed: $collapsed, open: $open) }
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
        let isOpen = open.contains(dev.id)
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
                Image(systemName: dev.hub ? "point.3.filled.connected.trianglepath.dotted" : dev.billboard ? "rectangle.connected.to.line.below" : "cable.connector.horizontal")
                    .font(.system(size: 11)).frame(width: 16).foregroundStyle(theme.onRow(true))
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
            if isOpen { RawList(lines: dev.raw).padding(.leading, 34).transition(.opacity) }
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
