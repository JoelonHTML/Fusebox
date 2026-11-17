// Tekent het Fusebox-icoon naar een PNG (1024 px). Draait via build.sh.
// Ontwerp: effen donker vlak, schuine glazen zekering met metalen kapjes en een lime hartslag als draad.
import SwiftUI
import AppKit

struct IconView: View {
    let tile = Color(red: 0.08, green: 0.09, blue: 0.12)
    let lime = Color(red: 0.72, green: 0.95, blue: 0.35)

    var body: some View {
        ZStack {
            // Squircle op Apple's icoongrid: 824 px binnen 1024 canvas, één effen kleur
            RoundedRectangle(cornerRadius: 185, style: .continuous).fill(tile)
            ZStack {
                // Glazen buis: lichte vulling, zachte lime gloed, heldere rand en een glanslijn
                Capsule().fill(LinearGradient(colors: [.white.opacity(0.22), .white.opacity(0.07)], startPoint: .top, endPoint: .bottom))
                    .frame(width: 470, height: 200)
                Capsule().fill(lime.opacity(0.18)).frame(width: 440, height: 170).blur(radius: 18)
                Capsule().stroke(.white.opacity(0.45), lineWidth: 14).frame(width: 470, height: 200)
                Capsule().fill(.white.opacity(0.5)).frame(width: 300, height: 14).offset(y: -62)
                // Draad = hartslag
                Path { p in
                    p.addLines([.init(x: 0, y: 100), .init(x: 150, y: 100), .init(x: 190, y: 40), .init(x: 245, y: 170), .init(x: 300, y: 20),
                                .init(x: 340, y: 130), .init(x: 365, y: 100), .init(x: 470, y: 100)])
                }
                .stroke(lime, style: StrokeStyle(lineWidth: 34, lineCap: .round, lineJoin: .round))
                .frame(width: 470, height: 200)
                // Metalen kapjes
                HStack(spacing: 380) {
                    ForEach(0..<2) { _ in
                        RoundedRectangle(cornerRadius: 30, style: .continuous)
                            .fill(LinearGradient(colors: [Color(white: 0.98), Color(white: 0.78)], startPoint: .top, endPoint: .bottom))
                            .frame(width: 110, height: 250)
                    }
                }
            }
            .scaleEffect(1.22)   // groot genoeg om in het Dock leesbaar te zijn
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
