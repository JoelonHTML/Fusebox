// Tekent het Meterkast-icoon naar een PNG (1024 px). Draait via build.sh.
// Ontwerp: donkerblauw vlak, gele meterboog, witte naald.
import SwiftUI
import AppKit

struct IconView: View {
    let center = CGPoint(x: 412, y: 520)
    var body: some View {
        ZStack {
            // Squircle op Apple's icoongrid: 824 px binnen 1024 canvas
            RoundedRectangle(cornerRadius: 185, style: .continuous)
                .fill(Color(red: 0.122, green: 0.169, blue: 0.239))

            // Meterboog (halve cirkel)
            Path { p in p.addArc(center: center, radius: 262, startAngle: .degrees(180), endAngle: .degrees(360), clockwise: false) }
                .stroke(Color(red: 0.96, green: 0.72, blue: 0.24), style: StrokeStyle(lineWidth: 56, lineCap: .round))

            // Naald + as
            Path { p in p.move(to: center); p.addLine(to: CGPoint(x: center.x + 150, y: center.y - 200)) }
                .stroke(.white, style: StrokeStyle(lineWidth: 36, lineCap: .round))
            Circle().fill(.white).frame(width: 100, height: 100).position(center)

            // Heel lichte glans, zodat hij in het Dock niet plat oogt
            RoundedRectangle(cornerRadius: 185, style: .continuous)
                .fill(LinearGradient(colors: [.white.opacity(0.10), .clear], startPoint: .top, endPoint: .center))
            RoundedRectangle(cornerRadius: 185, style: .continuous)
                .strokeBorder(.white.opacity(0.12), lineWidth: 3)
        }
        .frame(width: 824, height: 824)
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
