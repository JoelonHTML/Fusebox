import SwiftUI
import Carbon.HIToolbox

// MARK: - Sneltoets: Fusebox overal openen en weer wegzetten
//
// Carbon RegisterEventHotKey: werkt in elke app en vraagt geen toegankelijkheids-toestemming.
// Instellingen: "hotkeyCode" (virtuele toetscode, -1 = uit), "hotkeyMods" (Carbon-modifiers), "hotkeyLabel" ("⌃⌥⌘F").

enum Hotkey {
    /// Gezet door RootView (die heeft openWindow); werkt ook nadat het venster dicht is.
    @MainActor static var openMain: (() -> Void)?
    nonisolated(unsafe) private static var ref: EventHotKeyRef?
    nonisolated(unsafe) private static var handler: EventHandlerRef?

    /// (Opnieuw) registreren volgens de instellingen. Aanroepen bij het starten en na elke wijziging.
    static func apply() {
        if let r = ref { UnregisterEventHotKey(r); ref = nil }
        let d = UserDefaults.standard
        let code = d.integer(forKey: "hotkeyCode"), mods = d.integer(forKey: "hotkeyMods")
        guard code >= 0, mods != 0 else { return }
        if handler == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                DispatchQueue.main.async { MainActor.assumeIsolated { Hotkey.toggle() } }
                return noErr
            }, 1, &spec, nil, &handler)
        }
        RegisterEventHotKey(UInt32(code), UInt32(mods), EventHotKeyID(signature: 0x4642_4F58, id: 1), GetApplicationEventTarget(), 0, &ref)
    }

    /// Fusebox vooraan → wegzetten (verbergen, blijft meten). Anders → venster tonen en vooraan halen.
    @MainActor static func toggle() {
        if NSApp.isActive, NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeKey }) {
            NSApp.hide(nil)
        } else {
            NSApp.unhide(nil)
            openMain?()
            NSApp.activate()
        }
    }

    /// NSEvent-modifiers → Carbon-modifiers (wat RegisterEventHotKey wil).
    static func carbon(_ f: NSEvent.ModifierFlags) -> Int {
        (f.contains(.command) ? cmdKey : 0) | (f.contains(.option) ? optionKey : 0)
            | (f.contains(.control) ? controlKey : 0) | (f.contains(.shift) ? shiftKey : 0)
    }

    /// "⌃⌥⇧⌘" + toets, in de vaste macOS-volgorde.
    static func label(_ f: NSEvent.ModifierFlags, key: String) -> String {
        (f.contains(.control) ? "⌃" : "") + (f.contains(.option) ? "⌥" : "") + (f.contains(.shift) ? "⇧" : "")
            + (f.contains(.command) ? "⌘" : "") + key
    }

    static func selfTest() {
        precondition(carbon([.command, .option, .control]) == cmdKey | optionKey | controlKey, "carbon")
        precondition(label([.command, .control, .option], key: "F") == "⌃⌥⌘F", "label")
        print("sneltoets ok")
    }
}

/// Sneltoets kiezen: klik, druk de combinatie in. Esc = annuleren, ⌫ = uitzetten.
struct HotkeyRecorder: View {
    @AppStorage("hotkeyCode") private var code = Int(kVK_ANSI_F)
    @AppStorage("hotkeyMods") private var mods = cmdKey | optionKey | controlKey
    @AppStorage("hotkeyLabel") private var label = "⌃⌥⌘F"
    @State private var recording = false
    @State private var monitor: Any?

    var body: some View {
        LabeledContent {
            Button(recording ? L("Press keys…") : code < 0 ? L("Off") : label) { recording ? stop() : start() }
                .monospaced()
        } label: {
            Text("Shortcut to show or hide Fusebox")
            Text(recording ? "Esc cancels, ⌫ turns it off. Use ⌘, ⌥ or ⌃." : "Works from any app.")
        }
        .onDisappear { stop() }
    }

    private func start() {
        recording = true
        if let r = monitor { NSEvent.removeMonitor(r) }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            let f = e.modifierFlags.intersection([.command, .option, .control, .shift])
            switch Int(e.keyCode) {
            case kVK_Escape: stop()
            case kVK_Delete, kVK_ForwardDelete: code = -1; stop(); Hotkey.apply()
            default:
                guard !f.intersection([.command, .option, .control]).isEmpty else { NSSound.beep(); return nil }
                code = Int(e.keyCode); mods = Hotkey.carbon(f)
                label = Hotkey.label(f, key: (e.charactersIgnoringModifiers ?? "?").uppercased())
                stop(); Hotkey.apply()
            }
            return nil   // toets niet doorgeven
        }
    }

    private func stop() {
        recording = false
        if let r = monitor { NSEvent.removeMonitor(r); monitor = nil }
    }
}
