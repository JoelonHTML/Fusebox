// iPhone-icoon uit het Mac-icoon: het middelste vlak (824 px) beeldvullend, zonder doorzichtige hoeken.
// Gebruik: swift MakeIcon.swift ../icon/AppIcon-1024.png Fusebox/Assets.xcassets/AppIcon.appiconset/icon-1024.png
import AppKit
let a = CommandLine.arguments
guard a.count == 3, let src = NSImage(contentsOfFile: a[1])?.cgImage(forProposedRect: nil, context: nil, hints: nil),
      let ctx = CGContext(data: nil, width: 1024, height: 1024, bitsPerComponent: 8, bytesPerRow: 0, space: CGColorSpace(name: CGColorSpace.sRGB)!,
                          bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue) else { fatalError("gebruik: swift MakeIcon.swift bron.png doel.png") }
ctx.setFillColor(CGColor(srgbRed: 0.08, green: 0.09, blue: 0.12, alpha: 1))   // zelfde vlakkleur als icon/MakeIcon.swift
ctx.fill(CGRect(x: 0, y: 0, width: 1024, height: 1024))
let scale = 1024.0 / 824.0, size = 1024 * scale
ctx.draw(src, in: CGRect(x: (1024 - size) / 2, y: (1024 - size) / 2, width: size, height: size))
let rep = NSBitmapImageRep(cgImage: ctx.makeImage()!)
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: a[2]))
