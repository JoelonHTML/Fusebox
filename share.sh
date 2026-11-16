#!/bin/zsh
# Creates Fusebox.zip to share with friends (Apple silicon, macOS 26 or newer).
# Usage: ./share.sh  → Fusebox.zip in this folder. Send it together with README.md.
set -e
cd "$(dirname "$0")"
./build.sh
rm -f Fusebox.zip
ditto -c -k --norsrc --noextattr --noqtn --keepParent "$HOME/Applications/Fusebox.app" Fusebox.zip   # zonder ._-metadata
echo "done: $(pwd)/Fusebox.zip ($(du -h Fusebox.zip | cut -f1))"
