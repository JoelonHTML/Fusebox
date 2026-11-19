#!/bin/zsh
# Werkt ~/Applications/Fusebox.app bij op dezelfde plek, zodat je Dock-icoon blijft werken.
# (Buiten iCloud, anders weigert codesign.) Gebruik: ./build.sh
set -e
cd "$(dirname "$0")"
APP="$HOME/Applications/Fusebox.app"
OLD="$HOME/Applications/Meterkast.app"   # oude naam; wordt na het bouwen naar de Prullenmand verplaatst
TMP=$(mktemp -d)

# Eerst compileren naar tijdelijke map: mislukt het, dan blijft de werkende app staan
swiftc -O -parse-as-library -swift-version 5 -target arm64-apple-macos26.0 Sources/*.swift -o "$TMP/Fusebox"
"$TMP/Fusebox" --selftest
# Fan-helper (losse root-helper, alleen geïnstalleerd als je dat in Settings › Fans kiest)
swiftc -O -swift-version 5 -target arm64-apple-macos26.0 Helper/FanHelper.swift -o "$TMP/fusebox-fand"
"$TMP/fusebox-fand" --selftest

# Icoon: PNG tekenen → alle formaten → .icns
swift icon/MakeIcon.swift "$TMP/1024.png" >/dev/null
mkdir "$TMP/AppIcon.iconset"
for s in 16 32 128 256 512; do
  sips -z $s $s "$TMP/1024.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$TMP/1024.png" --out "$TMP/AppIcon.iconset/icon_${s}x${s}@2x.png" >/dev/null
done
cp "$TMP/1024.png" icon/AppIcon-1024.png

# Draaiende app netjes sluiten, dan bestanden in de bestaande bundel vervangen
WAS_RUNNING=$( (pgrep -x Fusebox || pgrep -x Meterkast) >/dev/null && echo 1 || true)
osascript -e 'tell application id "local.meterkast" to quit' 2>/dev/null || true
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
iconutil -c icns "$TMP/AppIcon.iconset" -o "$APP/Contents/Resources/AppIcon.icns"
cp "$TMP/Fusebox" "$APP/Contents/MacOS/Fusebox"
cp "$TMP/fusebox-fand" "$APP/Contents/Resources/fusebox-fand"
cp Resources/oui.tsv "$APP/Contents/Resources/oui.tsv"   # officiële IEEE-fabrikantenlijst (MAC → merk) voor de LAN-scanner
rm -rf "$APP/Contents/Resources/Fonts" && cp -R Resources/Fonts "$APP/Contents/Resources/Fonts"   # Montserrat (OFL) en Ubuntu (UFL), met licenties
# Ondertekenen met het vaste Fusebox-certificaat (eigen sleutelhanger, wachtwoord in de inlog-sleutelhanger):
# dan herkent macOS elke nieuwe versie als dezelfde app en blijven toestemmingen (Volledige schijftoegang e.d.)
# na een update staan. Geen certificaat (andere Mac)? Dan ad hoc, zoals vroeger.
KC="$HOME/Library/Keychains/fusebox-signing.keychain-db"
SIGN=(--sign -)
if [ -f "$KC" ] && KCPW=$(security find-generic-password -s fusebox-codesign-keychain -a fusebox -w 2>/dev/null); then
  security unlock-keychain -p "$KCPW" "$KC"
  SIGN=(--keychain "$KC" --sign "Fusebox Code Signing")
fi
codesign --force "${SIGN[@]}" --identifier local.fusebox.fand "$APP/Contents/Resources/fusebox-fand"

cat > "$APP/Contents/Info.plist" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>Fusebox</string>
  <key>CFBundleDevelopmentRegion</key><string>en</string>
  <key>CFBundleLocalizations</key><array><string>en</string></array>
  <key>CFBundleDisplayName</key><string>Fusebox</string>
  <key>CFBundleIdentifier</key><string>local.meterkast</string>
  <key>CFBundleExecutable</key><string>Fusebox</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>0.10</string>
  <key>CFBundleVersion</key><string>1</string>
  <key>LSMinimumSystemVersion</key><string>26.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>FuseboxUpdateRepo</key><string>JoelonHTML/Fusebox</string>
  <key>ATSApplicationFontsPath</key><string>Fonts</string>
  <key>NSLocalNetworkUsageDescription</key><string>Fusebox scans your local network to show which devices are connected.</string>
  <key>NSBonjourServices</key><array><string>_companion-link._tcp</string><string>_device-info._tcp</string><string>_airplay._tcp</string><string>_raop._tcp</string><string>_googlecast._tcp</string><string>_sonos._tcp</string><string>_spotify-connect._tcp</string><string>_ipp._tcp</string><string>_printer._tcp</string><string>_smb._tcp</string><string>_hap._tcp</string><string>_homekit._tcp</string><string>_http._tcp</string><string>_scanner._tcp</string><string>_afpovertcp._tcp</string><string>_workstation._tcp</string><string>_ssh._tcp</string><string>_sftp-ssh._tcp</string><string>_rfb._tcp</string><string>_hap._udp</string><string>_meshcop._udp</string><string>_matter._tcp</string></array>
</dict></plist>
EOF
# Bouwnummer = datum+tijd: zo weet een nieuwe Fusebox dat hij nieuwer is en werkt hij de oude bij (Installer.swift)
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $(date +%Y%m%d%H%M)" "$APP/Contents/Info.plist"
codesign --force "${SIGN[@]}" "$APP" >/dev/null
[ -d "$OLD" ] && mv "$OLD" "$HOME/.Trash/Meterkast-oud-$(date +%s).app"
touch "$APP" # Finder/Dock laten weten dat het icoon veranderd kan zijn
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP"
[ -n "$WAS_RUNNING" ] && open "$APP"
echo "klaar: $APP"
