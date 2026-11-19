import SwiftUI

// MARK: - Oplaadlimiet (zoals AlDente), standaard uit.
// De app beslist in zijn gewone meting (geen eigen timer) en vraagt de helper (Helper/FanHelper.swift) om het laden
// tegen te houden. De helper controleert zelf de accu (% en temperatuur), kent alleen vaste SMC-sleutels en laadt
// na 10 min zonder bericht weer normaal: een crash laat het laden nooit uit staan.

enum ChargeLimit {
    enum Reason: Equatable { case limit, hot }
    static let band = 5.0, hotOn = 40.0, hotOff = 37.0
    static let range = 50...100

    /// Laden tegenhouden, en waarom; nil = gewoon laden. Hysterese: tegenhouden vanaf de grens, weer laden op grens − 5%.
    /// Warm: tegenhouden vanaf 40 °C, weer laden onder 37 °C. Geen meting → gewoon laden.
    static func decide(battery: Double?, limit: Int, heat: Bool, temp: Double?, was: Reason?) -> Reason? {
        if heat, let t = temp, t >= (was == .hot ? hotOff : hotOn) { return .hot }
        guard let b = battery, limit < 100 else { return nil }   // 100% = geen grens
        let l = Double(limit)
        return b >= (was == nil ? l : l - band) ? .limit : nil
    }

    /// Regel op de BAT-kaart (hoofdthread), of nil als de limiet uit staat.
    static func fact(_ s: Sample) -> Fact? {
        let d = UserDefaults.standard
        guard d.bool(forKey: "chargeLimitOn"), s.battery != nil, ChargeController.supported != false else { return nil }
        let l = d.integer(forKey: "chargeLimit")
        switch ChargeController.now {
        case .limit: return Fact(label: "Charge limit", value: "\(l)% · holding")
        case .hot: return Fact(label: "Charging", value: "paused · battery warm")
        case nil: return Fact(label: "Charge limit", value: "\(l)%")
        }
    }

    static func selfTest() {
        // Hysterese
        precondition(decide(battery: 79, limit: 80, heat: false, temp: nil, was: nil) == nil, "onder de grens: laden")
        precondition(decide(battery: 80, limit: 80, heat: false, temp: nil, was: nil) == .limit, "grens bereikt")
        precondition(decide(battery: 76, limit: 80, heat: false, temp: nil, was: .limit) == .limit, "binnen de 5%: blijven houden")
        precondition(decide(battery: 75, limit: 80, heat: false, temp: nil, was: .limit) == .limit, "precies op grens − 5: nog houden")
        precondition(decide(battery: 74.9, limit: 80, heat: false, temp: nil, was: .limit) == nil, "eronder: weer laden")
        precondition(decide(battery: 77, limit: 80, heat: false, temp: nil, was: nil) == nil, "na het loslaten pas weer bij 80")
        precondition(decide(battery: nil, limit: 80, heat: true, temp: nil, was: .limit) == nil, "geen meting: laden")
        precondition(decide(battery: 100, limit: 100, heat: false, temp: nil, was: nil) == nil, "100% = geen grens")
        // Warmte
        precondition(decide(battery: 30, limit: 80, heat: true, temp: 40, was: nil) == .hot, "warm: pauze")
        precondition(decide(battery: 30, limit: 80, heat: false, temp: 45, was: nil) == nil, "warmte-optie uit")
        precondition(decide(battery: 30, limit: 80, heat: true, temp: 38, was: .hot) == .hot && decide(battery: 30, limit: 80, heat: true, temp: 38, was: nil) == nil, "warmte-hysterese")
        precondition(decide(battery: 30, limit: 80, heat: true, temp: 36.9, was: .hot) == nil, "afgekoeld: laden")
        precondition(decide(battery: 78, limit: 80, heat: true, temp: 36, was: .hot) == .limit, "afgekoeld boven grens − 5: houden voor de grens")
        // Opdrachten naar de helper
        precondition(ChargeController.command(.limit, limit: 80) == "charge limit 80" && ChargeController.command(.hot, limit: 80) == "charge pause"
                     && ChargeController.command(nil, limit: 80) == "charge auto" && ChargeController.command(.limit, limit: 20) == "charge limit 50", "opdrachten")
        precondition(FanHelper.field("ok fans=0 forced=0 hot=0 charge=CHTE held=1 v=3", "charge") == "CHTE"
                     && FanHelper.field("ok fans=0 v=2", "charge") == nil, "status lezen")
        print("oplaadlimiet: ok")
    }
}

/// Elke meting (2 s, hoofdthread): beslissen, en alleen bij een wissel of eens per minuut (teken van leven) de helper bellen.
final class ChargeController {
    /// Wat nu geldt (hoofdthread; voor de BAT-kaart).
    nonisolated(unsafe) static var now: ChargeLimit.Reason?
    /// nil = onbekend, false = deze Mac heeft geen bekende laadsleutel.
    /// Vooraf al bekend zonder helper: kent de SMC een van de laadsleutels? (macOS 27 heeft ze op veel Macs dichtgezet)
    nonisolated(unsafe) static var supported: Bool? = {
        let smc = SMC(scanTemps: false)
        return smc.has("CHTE") || (smc.has("CH0B") && smc.has("CH0C")) ? nil : false
    }()

    private let queue = DispatchQueue(label: "local.fusebox.charge")
    private var sent: String?, sentAt = Date.distantPast
    private var busy = false

    init() {
        NotificationCenter.default.addObserver(forName: NSApplication.willTerminateNotification, object: nil, queue: nil) { [weak self] _ in
            guard let self, self.sent != nil, self.sent != "charge auto" else { return }
            self.queue.sync { _ = FanHelper.send("charge auto") }   // meteen normaal laden i.p.v. na 10 min
        }
    }

    static func command(_ r: ChargeLimit.Reason?, limit: Int) -> String {
        switch r {
        case .limit: "charge limit \(Swift.min(Swift.max(limit, ChargeLimit.range.lowerBound), ChargeLimit.range.upperBound))"
        case .hot: "charge pause"
        case nil: "charge auto"
        }
    }

    func update(_ s: Sample) {
        guard !busy, s.battery != nil, FanHelper.installed else { return }
        let d = UserDefaults.standard, on = d.bool(forKey: "chargeLimitOn") && Self.supported != false
        let limit = d.integer(forKey: "chargeLimit")
        let r = on ? ChargeLimit.decide(battery: s.battery, limit: limit, heat: d.bool(forKey: "chargeHeat"), temp: s.batteryTemp, was: Self.now) : nil
        Self.now = r
        let cmd = Self.command(r, limit: limit)
        // Eerste meting: altijd één keer sturen (de helper kan nog van de vorige keer vasthouden).
        // Tegenhouden: eens per minuut herhalen, anders laadt de helper na 10 min weer.
        guard cmd != sent || (cmd != "charge auto" && s.time.timeIntervalSince(sentAt) >= 60) else { return }
        busy = true
        queue.async { [weak self] in
            let reply = FanHelper.send(cmd)
            DispatchQueue.main.async {
                guard let self else { return }
                self.busy = false
                guard let reply else { return }   // helper niet bereikbaar: volgende meting opnieuw
                self.sent = cmd; self.sentAt = s.time
                if reply == "err unsupported" { Self.supported = false; Self.now = nil }
            }
        }
    }
}

// MARK: - Instellingen › Battery

struct ChargeSettings: View {
    @EnvironmentObject var meter: Meter
    @AppStorage("chargeLimitOn") private var on = false
    @AppStorage("chargeLimit") private var limit = 80
    @AppStorage("chargeHeat") private var heat = false
    @State private var key: String?        // laadsleutel volgens de helper; "none" = niet ondersteund

    var body: some View {
        let s = meter.latest
        Section {
            Toggle(isOn: $on) {
                Text("Limit charging")
                Text("Keeps the battery healthier when you're mostly plugged in. Needs the Fusebox helper (admin password once).")
            }
            .disabled(key == "none" || ChargeController.supported == false)
            LabeledContent("Stop charging at") {
                HStack {
                    Slider(value: Binding(get: { Double(limit) }, set: { limit = Int($0.rounded()) }), in: 50...100, step: 5).frame(width: 200)
                    Text("\(limit)%").monospacedDigit().frame(width: 44, alignment: .trailing)
                }
            }
            .disabled(!on)
            Toggle(isOn: $heat) {
                Text("Pause charging when the battery is hot")
                Text("From 40 °C until it has cooled to 37 °C.")
            }
            .disabled(!on)
        } header: {
            Text("Charge limit")
        } footer: {
            VStack(alignment: .leading, spacing: 4) {
                if key == "none" || ChargeController.supported == false {
                    Text("Not supported on this Mac: macOS doesn't let apps hold the charge here. Use macOS's own charge limit in System Settings › Battery.").foregroundStyle(.orange)
                    Button("Open Battery Settings") { NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.Battery-Settings.extension")!) }
                } else if on && !FanHelper.installed {
                    Text("Install the helper below, then Fusebox can hold the charge.").foregroundStyle(.orange)
                }
                Text("Charging starts again \(Int(ChargeLimit.band))% below the limit. When Fusebox quits or you turn this off, charging goes back to normal.")
                    .foregroundStyle(.secondary)
                if let b = s.battery {
                    Text("Now \(pct(b))" + (s.batteryTemp.map { " · \(Int($0.rounded())) °C" } ?? "")
                         + (ChargeController.now == .limit ? " · holding" : ChargeController.now == .hot ? " · paused (warm)" : ""))
                        .monospacedDigit().foregroundStyle(.secondary)
                }
            }
            .font(.caption)
        }
        HelperSection()
            .task {
                guard FanHelper.installed else { return }
                let reply = await Task.detached { FanHelper.send("status") }.value
                key = reply.flatMap { FanHelper.field($0, "charge") }
                if key == "none" { ChargeController.supported = false } else if key != nil { ChargeController.supported = true }
            }
    }
}
