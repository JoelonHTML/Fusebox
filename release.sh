#!/bin/zsh
# Nieuwe versie publiceren op GitHub: bouwen, zippen, taggen en een release met Fusebox.zip.
# De Fusebox van iedereen ziet die release vanzelf (binnen 6 uur) en werkt zichzelf bij.
# Gebruik: ./release.sh "Wat is er nieuw"   (commit je wijzigingen eerst)
set -e
cd "$(dirname "$0")"
[ -z "$(git status --porcelain)" ] || { echo "Eerst committen: er zijn nog wijzigingen."; exit 1; }
NOTES="${1:-Improvements and fixes.}"
./share.sh
APP="$HOME/Applications/Fusebox.app"
V=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$APP/Contents/Info.plist")
B=$(/usr/libexec/PlistBuddy -c "Print :CFBundleVersion" "$APP/Contents/Info.plist")
BETA=""; [[ "$V" == 0.* ]] && BETA="-beta"   # 0.x = beta
TAG="v$V$BETA-$B"
git tag "$TAG"
git symbolic-ref -q HEAD >/dev/null && git push origin HEAD
git push origin "$TAG"
gh release create "$TAG" Fusebox.zip --title "Fusebox $V${BETA:+ beta}" --notes "$NOTES"
echo "gepubliceerd: $TAG"
