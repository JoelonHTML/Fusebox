// Tekent het Fusebox-icoon naar een PNG (1024 px). Draait via build.sh.
// Ontwerp: effen donker vlak, schuine zekering met witte kapjes en een lime hartslag als draad.
import SwiftUI
import AppKit

struct IconView: View {
    let tile = Color(red: 0.08, green: 0.09, blue: 0.12)
    let lime = Color(red: 0.72, green: 0.95, blue: 0.35)
    let cap = Color(white: 0.93)

    var body: some View {
        ZStack {
            // Squircle op Apple's icoongrid: 824 px binnen 1024 canvas, één effen kleur
            RoundedRectangle(cornerRadius: 185, style: .continuous).fill(tile)
            ZStack {
                // Glazen buis: lichte vulling, heldere rand en een glanslijn, zodat hij ook in het Dock leesbaar is
                Capsule().fill(LinearGradient(colors: [.white.opacity(0.20), .white.opacity(0.07)], startPoint: .top, endPoint: .bottom))
                    .frame(width: 470, height: 200)
                Capsule().stroke(.white.opacity(0.42), lineWidth: 14).frame(width: 470, height: 200)
                Capsule().fill(.white.opacity(0.45)).frame(width: 300, height: 14).offset(y: -62)
                // Draad = hartslag
                Path { p in
                    p.addLines([.init(x: 0, y: 100), .init(x: 150, y: 100), .init(x: 190, y: 40), .init(x: 245, y: 170), .init(x: 300, y: 20),
                                .init(x: 340, y: 130), .init(x: 365, y: 100), .init(x: 470, y: 100)])
                }
                .stroke(lime, style: StrokeStyle(lineWidth: 26, lineCap: .round, lineJoin: .round))
                .frame(width: 470, height: 200)
                // Metalen kapjes
                HStack(spacing: 380) {
                    RoundedRectangle(cornerRadius: 28, style: .continuous).fill(cap).frame(width: 100, height: 240)
                    RoundedRectangle(cornerRadius: 28, style: .continuous).fill(cap).frame(width: 100, height: 240)
                }
            }
            .rotationEffect(.degrees(-30))
        }
        .frame(width: 824, height: 824)
        .clipShape(RoundedRectangle(cornerRadius: 185, style: .continuous))
        .frame(width: 1024, height: 1024)
    }
}

@MainActor func render() {
    let r = ImageRenderer(content: IconView())
    r.scale = 1
    guard let cg = r.cgImage else { fatalError("render mislukt") }
    let png = NSBitmapImageRep(cgImage: cg).representation(using: .png, properties: [:])!
    let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "AppIcon-1024.png")
    try! png.write(to: out)
}
MainActor.assumeIsolated { render() }
