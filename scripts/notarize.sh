#!/bin/zsh
# Notariseren bij Apple, zodra er een Apple Developer-account is (Developer ID). Zonder account doet dit niets.
# Eenmalig klaarzetten (met je eigen account, niet in deze repo):
#   1. Developer ID Application-certificaat in de sleutelhanger (Xcode › Settings › Accounts › Manage Certificates)
#   2. xcrun notarytool store-credentials fusebox-notary --apple-id <jouw Apple ID> --team-id <TEAMID>
# Daarna doet release.sh dit vanzelf: ondertekenen met hardened runtime, insturen, wachten, "staple".
set -e
APP="${1:-$HOME/Applications/Fusebox.app}"
ID=$(security find-identity -v -p codesigning | grep -o '"Developer ID Application:[^"]*"' | head -1 | tr -d '"')
if [ -z "$ID" ] || ! xcrun notarytool history --keychain-profile fusebox-notary >/dev/null 2>&1; then
  echo "notariseren: overgeslagen (geen Developer ID of geen notarytool-profiel 'fusebox-notary')"
  exit 0
fi
codesign --force --options runtime --timestamp --sign "$ID" --identifier local.fusebox.fand "$APP/Contents/Resources/fusebox-fand"
codesign --force --options runtime --timestamp --sign "$ID" "$APP"
Z=$(mktemp -d)/Fusebox.zip
ditto -c -k --keepParent "$APP" "$Z"
xcrun notarytool submit "$Z" --keychain-profile fusebox-notary --wait
xcrun stapler staple "$APP"
echo "notariseren: klaar"
