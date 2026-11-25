#!/bin/zsh
# Bouwt Fusebox en zet hem op de aangesloten iPhone: de wekelijkse verversing in één commando (een gratis Apple ID tekent voor 7 dagen).
# iPhone: aan de kabel of op hetzelfde wifi, ontgrendeld, Ontwikkelaarsmodus aan. Mac: Xcode ingelogd met het Apple ID, xcodegen.
# Het team komt uit Local.xcconfig (niet in git; zie Team.xcconfig). NO_LAUNCH=1: alleen installeren, niet openen (de automatische
# verversing van de Mac, ~/.local/bin/"iPhone-apps verversen"). Zelfde opzet als de scripts van Atelier en KoeScan Beheer.
set -euo pipefail
cd "$(dirname "$0")"
# Homebrew's xcodegen first: the loose copy in ~/.local/bin lacks its SettingPresets ("No base settings found") and then generates a
# project without PRODUCT_NAME ("Multiple commands produce .../.app").
export PATH="/opt/homebrew/bin:$HOME/.local/bin:$PATH"
BUNDLE_ID=io.github.joelonhtml.fusebox
OUT="${FUSEBOX_IOS_DEVICE_BUILD:-$HOME/Library/Caches/FuseboxIOS-device}" # buiten de repo, net als build.sh
xcodegen generate --quiet
UDID=$(xcodebuild -project Fusebox.xcodeproj -scheme Fusebox -showdestinations 2>/dev/null | grep 'platform:iOS, arch' | head -1 | sed -E 's/.*id:([^,]+),.*/\1/')
[ -n "$UDID" ] || { echo "Geen iPhone gevonden: sluit hem aan met de kabel en ontgrendel hem."; exit 1; }
# Wekt de ontwikkelaarsdiensten van de iPhone ("connected (no DDI)" liet xcodebuild wachten tot de time-out).
xcrun devicectl device info ddiServices --device "$UDID" >/dev/null 2>&1 || true
# Xcode vernieuwt een gratis 7-dagenprofiel pas als het al VERLOPEN is: een build op dag 6 kreeg het oude profiel en de app stopte
# een paar uur later (2026-11-19). Daarom gaan profielen van deze app die binnen 6 dagen verlopen eerst weg.
PROFILES="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
expiry() { security cms -D -i "$1" 2>/dev/null | plutil -extract ExpirationDate raw -o - - 2>/dev/null \
  | xargs -I{} date -j -u -f "%Y-%m-%dT%H:%M:%SZ" {} +%s 2>/dev/null || echo 0; }
for f in "$PROFILES"/*.mobileprovision(N); do
  security cms -D -i "$f" 2>/dev/null | plutil -extract Name raw -o - - 2>/dev/null | grep -q "$BUNDLE_ID" || continue
  [ $(( $(expiry "$f") - $(date +%s) )) -lt $((6 * 86400)) ] && rm -f "$f"
done
mkdir -p "$OUT"
# Het bouwlog gaat naar een bestand: een mislukte build stopt hier in plaats van de vorige .app opnieuw te installeren.
xcodebuild -project Fusebox.xcodeproj -scheme Fusebox -configuration Debug -destination "id=$UDID" -destination-timeout 90 \
  -derivedDataPath "$OUT" -allowProvisioningUpdates build > "$OUT/xcodebuild.log" 2>&1 \
  || { grep -E "error" "$OUT/xcodebuild.log" | head -20; echo "Bouwen mislukt ($OUT/xcodebuild.log)."; exit 1; }
grep -E "BUILD" "$OUT/xcodebuild.log"
APP="$OUT/Build/Products/Debug-iphoneos/Fusebox.app"
# Alleen een handtekening die echt (bijna) 7 dagen geldig is telt als klaar.
left=$(( ($(expiry "$APP/embedded.mobileprovision") - $(date +%s)) / 3600 ))
[ "$left" -ge $((6 * 24)) ] || { echo "De ondertekening is nog maar $left uur geldig in plaats van 7 dagen; niet geinstalleerd."; exit 1; }
echo "Ondertekend tot $(date -r $(expiry "$APP/embedded.mobileprovision") '+%a %d %b %H:%M')."
xcrun devicectl device install app --device "$UDID" "$APP"
[ -n "${NO_LAUNCH:-}" ] && exit 0
xcrun devicectl device process launch --terminate-existing --device "$UDID" "$BUNDLE_ID"
