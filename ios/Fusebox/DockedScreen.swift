import SwiftUI
import AVFAudio
import GameController

// MARK: - Docked: wat hangt er aan deze iPhone
//
// Veel kaler dan op de Mac: iOS laat een app alleen zien of de iPhone laadt, waar het geluid heen gaat, en of er een
// beeldscherm, toetsenbord, muis, gamecontroller of netwerkadapter aan hangt. Geen lader-watt, kabelgegevens,
// USB-schijven of de accu van oortjes. Geen serienummers of hardware-adressen.

@MainActor
final class DockedModel: ObservableObject {
    struct Item: Identifiable, Equatable {
        let id: String, icon: String, title: String, fact: String
    }
    @Published private(set) var items: [Item] = []

    func refresh() {
        var out: [Item] = []
        // Stroom
        let dev = UIDevice.current
        dev.isBatteryMonitoringEnabled = true
        let pct = dev.batteryLevel < 0 ? "" : " · \(Int((dev.batteryLevel * 100).rounded()))%"
        if dev.batteryState == .charging { out.append(Item(id: "power", icon: "bolt.fill", title: L("Power"), fact: L("Charging") + pct)) }
        if dev.batteryState == .full { out.append(Item(id: "power", icon: "bolt.fill", title: L("Power"), fact: L("Plugged in, full"))) }
        // Geluid: alles behalve de eigen luidsprekers
        for o in AVAudioSession.sharedInstance().currentRoute.outputs {
            let kind: (icon: String, name: String)? = switch o.portType {
            case .headphones: ("headphones", L("Wired headphones"))
            case .bluetoothA2DP, .bluetoothHFP, .bluetoothLE: ("headphones", L("Bluetooth audio"))
            case .airPlay: ("airplayaudio", "AirPlay")
            case .carAudio: ("car", "CarPlay")
            case .HDMI, .displayPort: ("tv", L("Sound over the display cable"))
            case .usbAudio: ("cable.connector", L("USB audio"))
            case .lineOut: ("cable.connector", L("Line out"))
            case .builtInSpeaker, .builtInReceiver: nil
            default: ("speaker.wave.2", L("Audio output"))
            }
            if let kind { out.append(Item(id: "audio " + o.uid, icon: kind.icon, title: String(o.portName.prefix(64)), fact: kind.name)) }
        }
        // Extern beeldscherm (kabel of AirPlay-synchrone weergave)
        for (i, s) in UIScreen.screens.dropFirst().enumerated() {
            let px = s.nativeBounds.size
            out.append(Item(id: "display \(i)", icon: "display", title: L("External display"),
                            fact: "\(Int(max(px.width, px.height))) × \(Int(min(px.width, px.height))) · \(s.maximumFramesPerSecond) Hz"))
        }
        // Toetsenbord, muis of trackpad, gamecontroller
        if let k = GCKeyboard.coalesced { out.append(Item(id: "keyboard", icon: "keyboard", title: L("Keyboard"), fact: k.vendorName.map { String($0.prefix(64)) } ?? L("Connected"))) }
        for (i, m) in GCMouse.mice().enumerated() {
            out.append(Item(id: "mouse \(i)", icon: "computermouse", title: L("Mouse or trackpad"), fact: m.vendorName.map { String($0.prefix(64)) } ?? L("Connected")))
        }
        for (i, c) in GCController.controllers().enumerated() {
            let battery = c.battery.map { " · \(Int(($0.batteryLevel * 100).rounded()))%" } ?? ""
            out.append(Item(id: "controller \(i)", icon: "gamecontroller", title: String((c.vendorName ?? L("Game controller")).prefix(64)), fact: L("Game controller") + battery))
        }
        // Netwerk via een adapter
        if NetPath.path.usesInterfaceType(.wiredEthernet) { out.append(Item(id: "ethernet", icon: "cable.connector", title: L("Network adapter"), fact: "Ethernet")) }
        if out != items { items = out }
    }
}

struct DockedScreen: View {
    @StateObject private var model = DockedModel()
    @Environment(\.theme) private var theme
    @Environment(\.scenePhase) private var phase

    var body: some View {
        Screen {
            PageTitle(title: "Docked", note: L("What is connected to this iPhone"))
            if model.items.isEmpty {
                VStack(spacing: 8) {
                    Image(systemName: "dock.rectangle").font(.system(size: 28)).foregroundStyle(theme.secondary)
                    Text("Nothing is connected right now").font(theme.label(14)).foregroundStyle(theme.secondary)
                }
                .frame(maxWidth: .infinity).padding(.vertical, 36)
            } else {
                let tight = theme.rowStyle == .lines || theme.rowStyle == .plain
                VStack(spacing: tight ? 0 : 6) {
                    ForEach(Array(model.items.enumerated()), id: \.element.id) { i, item in
                        HStack(spacing: 12) {
                            Image(systemName: item.icon).font(.system(size: 16)).frame(width: 24).foregroundStyle(theme.onRow(true))
                            Text(verbatim: item.title).font(theme.label(14)).foregroundStyle(theme.onRow()).lineLimit(1)
                            Spacer(minLength: 8)
                            Text(verbatim: item.fact).font(theme.label(12)).foregroundStyle(theme.onRow(true)).lineLimit(1)
                        }
                        .themedRow(theme, index: i)
                    }
                }
            }
            Text("An iPhone app can see whether the iPhone charges, where sound goes, and a connected display, keyboard, mouse, game controller or network adapter. It can't see the charger's power, cable details, USB drives or the battery of earbuds.")
                .font(theme.label(11)).foregroundStyle(theme.secondary).fixedSize(horizontal: false, vertical: true).padding(.top, 6)
        }
        // Goedkope vragen aan iOS, alleen terwijl deze pagina in beeld is
        .task(id: phase) {
            guard phase == .active else { return }
            while !Task.isCancelled {
                model.refresh()
                try? await Task.sleep(for: .seconds(ProcessInfo.processInfo.isLowPowerModeEnabled ? 10 : 3))
            }
        }
    }
}
