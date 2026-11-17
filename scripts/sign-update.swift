// Ondertekent een bestand met de release-sleutel uit de sleutelhanger (Ed25519) en schrijft <bestand>.sig (base64).
// Gebruik: swift scripts/sign-update.swift Fusebox.zip   (release.sh doet dit). De sleutel staat nooit in de repo.
import CryptoKit
import Foundation

let file = URL(fileURLWithPath: CommandLine.arguments[1])
let p = Process(), out = Pipe()
p.executableURL = URL(fileURLWithPath: "/usr/bin/security")
p.arguments = ["find-generic-password", "-s", "fusebox-release-signing", "-w"]
p.standardOutput = out
try p.run(); p.waitUntilExit()
guard p.terminationStatus == 0,
      let b64 = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
      let raw = Data(base64Encoded: b64), let key = try? Curve25519.Signing.PrivateKey(rawRepresentation: raw)
else { FileHandle.standardError.write("Geen release-sleutel in de sleutelhanger (fusebox-release-signing).\n".data(using: .utf8)!); exit(1) }
let sig = try key.signature(for: Data(contentsOf: file))
try sig.base64EncodedString().write(to: file.appendingPathExtension("sig"), atomically: true, encoding: .utf8)
print("ondertekend: \(file.lastPathComponent).sig")
