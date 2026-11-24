#!/bin/zsh
# Bouwt Fusebox voor iPhone voor de simulator en draait de zelftest. Gebruik: ./build.sh [naam van de simulator]
# Op je eigen iPhone: open Fusebox.xcodeproj in Xcode, kies je team bij Signing & Capabilities en druk op Run.
set -e
cd "$(dirname "$0")"
SIM="${1:-iPhone 17 Pro}"
OUT="${FUSEBOX_IOS_BUILD:-$HOME/Library/Caches/FuseboxIOS}"
xcodegen generate --quiet
xcodebuild -project Fusebox.xcodeproj -scheme Fusebox -destination "platform=iOS Simulator,name=$SIM" -derivedDataPath "$OUT" -quiet build
APP="$OUT/Build/Products/Debug-iphonesimulator/Fusebox.app"
xcrun simctl bootstatus "$SIM" -b >/dev/null
xcrun simctl install "$SIM" "$APP"
# Zelftest van de gedeelde netwerkcode (verwacht Engelse teksten, net als `Fusebox --selftest` op de Mac)
xcrun simctl launch --console-pty "$SIM" io.github.joelonhtml.fusebox --selftest -AppleLanguages "(en)" | tee /dev/stderr | grep -q "iphone: ok"
echo "Klaar: $APP"
