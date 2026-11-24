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
    /// Tijdens het opnemen in Instellingen: niets geregistreerd, anders zet de huidige combinatie het venster weg.
    nonisolated(unsafe) static var suspended = false
    /// Uitkomst van de laatste RegisterEventHotKey. macOS meldt hier géén botsing met een andere app
    /// (een tweede proces krijgt gewoon 0 terug); alleen een echte weigering komt hier binnen.
    nonisolated(unsafe) private(set) static var status: OSStatus = noErr
    static var conflict: Bool { status != noErr && !suspended }

    /// (Opnieuw) registreren volgens de instellingen. Aanroepen bij het starten en na elke wijziging.
    static func apply() {
        if let r = ref { UnregisterEventHotKey(r); ref = nil }
        status = noErr
        let d = UserDefaults.standard
        let code = d.integer(forKey: "hotkeyCode"), mods = d.integer(forKey: "hotkeyMods")
        guard !suspended, code >= 0, mods != 0 else { return }   // toetscode 0 = A en dus geldig; alleen -1 is uit
        if handler == nil {
            var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
            InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
                DispatchQueue.main.async { MainActor.assumeIsolated { Hotkey.toggle() } }
                return noErr
            }, 1, &spec, nil, &handler)
        }
        status = RegisterEventHotKey(UInt32(code), UInt32(mods), EventHotKeyID(signature: 0x4642_4F58, id: 1), GetApplicationEventTarget(), 0, &ref)
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

    private static let names: [Int: String] = {
        var n = [kVK_Space: "Space", kVK_Return: "↩", kVK_Tab: "⇥", kVK_LeftArrow: "←", kVK_RightArrow: "→",
                 kVK_UpArrow: "↑", kVK_DownArrow: "↓", kVK_PageUp: "PgUp", kVK_PageDown: "PgDn", kVK_Home: "Home", kVK_End: "End"]
        let f = [kVK_F1, kVK_F2, kVK_F3, kVK_F4, kVK_F5, kVK_F6, kVK_F7, kVK_F8, kVK_F9, kVK_F10,
                 kVK_F11, kVK_F12, kVK_F13, kVK_F14, kVK_F15, kVK_F16, kVK_F17, kVK_F18, kVK_F19, kVK_F20]
        for (i, k) in f.enumerated() { n[k] = "F\(i + 1)" }
        return n
    }()

    /// Leesbare naam van een toets voor het label; nil = geen bruikbare toets (piep, blijven luisteren).
    /// `chars` = charactersIgnoringModifiers: F-toetsen en pijlen geven daar een privé-teken, vandaar de tabel.
    static func keyName(keyCode: Int, chars: String?) -> String? {
        if let n = names[keyCode] { return n }
        guard let c = chars, c.count == 1, c != " ", c.unicodeScalars.allSatisfy({
            $0.properties.generalCategory != .control && $0.properties.generalCategory != .privateUse
        }) else { return nil }
        return c.uppercased()
    }

    enum Clash { case macOS, fusebox }
    private static let modMask = cmdKey | optionKey | controlKey | shiftKey

    /// Botst de combinatie met een ingeschakelde macOS-toetscombinatie of met een eigen menu-item?
    /// Vergelijkt toetscodes (niet tekens: die hangen af van de toetsenbordindeling). Andere apps zijn niet op te vragen.
    static func conflicts(code: Int, mods: Int, system: [(Int, Int, Bool)], menu: [(Int, Int)]) -> Clash? {
        // macOS zet bij F-toetsen en pijlen een extra fn-bit (1<<17) in de modifiers: wegmaskeren
        if system.contains(where: { $0.2 && $0.0 == code && $0.1 & modMask == mods }) { return .macOS }
        if menu.contains(where: { $0 == (code, mods) }) { return .fusebox }
        return nil
    }

    /// De toetscombinaties van macOS zelf (Spotlight, Mission Control, schermafbeelding…): (toetscode, Carbon-modifiers, aan).
    static func systemKeys() -> [(Int, Int, Bool)] {
        var out: Unmanaged<CFArray>?
        guard CopySymbolicHotKeys(&out) == noErr, let list = out?.takeRetainedValue() as? [[String: Any]] else { return [] }
        return list.map { ($0[kHISymbolicHotKeyCode as String] as? Int ?? -1, $0[kHISymbolicHotKeyModifiers as String] as? Int ?? 0,
                           $0[kHISymbolicHotKeyEnabled as String] as? Bool ?? false) }
    }

    /// Menu-items van Fusebox die op het teken van deze toetsaanslag zitten, als (toetscode, Carbon-modifiers).
    /// Een menu kent alleen tekens; de aanslag zelf levert de toetscode die erbij hoort, dus geen indelingstabel nodig.
    @MainActor static func menuKeys(for e: NSEvent) -> [(Int, Int)] {
        func all(_ m: NSMenu?) -> [NSMenuItem] { (m?.items ?? []).flatMap { [$0] + all($0.submenu) } }
        guard let c = e.charactersIgnoringModifiers?.lowercased(), !c.isEmpty else { return [] }
        return all(NSApp.mainMenu).filter { $0.keyEquivalent.lowercased() == c }.map {
            // Hoofdletter als toets = ⇧ erbij (zo schrijft AppKit ⇧⌘N)
            (Int(e.keyCode), carbon($0.keyEquivalentModifierMask.union($0.keyEquivalent != c ? .shift : [])))
        }
    }

    static func selfTest() {
        precondition(carbon([.command, .option, .control]) == cmdKey | optionKey | controlKey, "carbon")
        precondition(label([.command, .control, .option], key: "F") == "⌃⌥⌘F", "label")
        precondition(keyName(keyCode: kVK_F5, chars: "\u{F708}") == "F5" && keyName(keyCode: kVK_Space, chars: " ") == "Space", "F5, spatie")
        precondition(keyName(keyCode: kVK_LeftArrow, chars: "\u{F702}") == "←" && keyName(keyCode: kVK_ANSI_A, chars: "a") == "A", "pijl, letter")
        precondition(keyName(keyCode: kVK_Help, chars: "\u{F746}") == nil && keyName(keyCode: kVK_ANSI_KeypadEnter, chars: "\u{3}") == nil
                     && keyName(keyCode: kVK_ANSI_E, chars: "") == nil, "privé-teken, stuurteken, dode toets")
        let sys = [(kVK_Space, cmdKey, true), (kVK_Space, controlKey, false), (kVK_UpArrow, controlKey | 1 << 17, true)]
        let menu = [(kVK_ANSI_Q, cmdKey), (kVK_ANSI_Comma, cmdKey)]
        precondition(conflicts(code: kVK_Space, mods: cmdKey, system: sys, menu: menu) == .macOS, "⌘spatie is van macOS")
        precondition(conflicts(code: kVK_Space, mods: controlKey, system: sys, menu: menu) == nil, "uitgeschakeld telt niet")
        precondition(conflicts(code: kVK_UpArrow, mods: controlKey, system: sys, menu: menu) == .macOS, "fn-bit weggemaskeerd")
        precondition(conflicts(code: kVK_ANSI_Q, mods: cmdKey, system: sys, menu: menu) == .fusebox, "⌘Q is van het menu")
        precondition(conflicts(code: kVK_ANSI_Q, mods: cmdKey | shiftKey, system: sys, menu: menu) == nil, "andere modifiers")
        precondition(conflicts(code: kVK_ANSI_A, mods: cmdKey | optionKey | controlKey, system: sys, menu: menu) == nil, "toetscode 0 (A) is geldig en vrij")
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
    @State private var note: String?

    var body: some View {
        LabeledContent {
            Button(recording ? L("Press keys…") : code < 0 ? L("Off") : label) { recording ? stop() : start() }
                .monospaced()
        } label: {
            Text("Shortcut to show or hide Fusebox")
            Text(recording ? "Esc cancels, ⌫ turns it off. Use ⌘, ⌥ or ⌃." : "Works from any app.")
        }
        .onAppear { note = Hotkey.conflict ? L("macOS didn't accept this shortcut. Pick another one.") : nil }
        .onDisappear { if recording { stop() } }
        // Naar een andere app gegaan tijdens het opnemen: stoppen, anders blijft de sneltoets uit tot je terugkomt
        .onReceive(NotificationCenter.default.publisher(for: NSApplication.didResignActiveNotification)) { _ in if recording { stop() } }
        if let note { Text(note).font(.caption).foregroundStyle(.orange) }
    }

    private func start() {
        recording = true; note = nil
        Hotkey.suspended = true; Hotkey.apply()   // nu niets geregistreerd: de huidige combinatie is gewoon op te nemen
        if let r = monitor { NSEvent.removeMonitor(r) }
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { e in
            let f = e.modifierFlags.intersection([.command, .option, .control, .shift])
            switch Int(e.keyCode) {
            case kVK_Escape: stop()
            case kVK_Delete, kVK_ForwardDelete: code = -1; stop()
            default:
                guard !f.intersection([.command, .option, .control]).isEmpty,
                      let key = Hotkey.keyName(keyCode: Int(e.keyCode), chars: e.charactersIgnoringModifiers) else { NSSound.beep(); return nil }
                // Van macOS of van het eigen menu: weigeren en blijven luisteren, niets opslaan
                switch Hotkey.conflicts(code: Int(e.keyCode), mods: Hotkey.carbon(f), system: Hotkey.systemKeys(), menu: Hotkey.menuKeys(for: e)) {
                case .macOS: note = L("macOS already uses this shortcut."); NSSound.beep(); return nil
                case .fusebox: note = L("Fusebox already uses this shortcut."); NSSound.beep(); return nil
                case nil: break
                }
                code = Int(e.keyCode); mods = Hotkey.carbon(f); label = Hotkey.label(f, key: key)
                stop()
            }
            return nil   // toets niet doorgeven
        }
    }

    private func stop() {
        recording = false
        if let r = monitor { NSEvent.removeMonitor(r); monitor = nil }
        // Weer registreren met wat er nu staat. Lukt dat niet, dan blijft de keuze staan en komt er alleen een waarschuwing.
        Hotkey.suspended = false; Hotkey.apply()
        note = Hotkey.conflict ? L("macOS didn't accept this shortcut. Pick another one.") : nil
    }
}
