#!/bin/zsh
# Welke UI-teksten missen een vertaling? Gebruik: scripts/strings-check.sh [--suspects] [--keys]
# De compiler zoekt zelf alle vertaalbare teksten op (Text/Button/Toggle/Label/Section/.help/LabeledContent
# met een letterlijke tekst, en L("…")/String(localized:)), met de juiste %@/%lld-sleutels zoals SwiftUI ze opzoekt.
# Daarna per taal in Resources/<taal>.lproj/Localizable.strings: wat ontbreekt en wat er nog in staat maar niet meer gebruikt wordt.
#   --suspects  ook regels met Engels-ogende tekst die (nog) niet vertaalbaar is (grof; veel ruis van logs/CSV)
#   --keys      alle sleutels als JSON naar stdout (voor het aanvullen van een vertaling)
set -e
cd "$(dirname "$0")/.."
OUT=$(mktemp -d)
swiftc -Onone -parse-as-library -swift-version 5 -target arm64-apple-macos26.0 Sources/*.swift -o "$OUT/Fusebox" \
  -emit-localized-strings -emit-localized-strings-path "$OUT" 2>&1 | grep 'error:' && exit 1
python3 - "$OUT" "$@" <<'EOF'
import json, glob, re, sys, subprocess
out, args = sys.argv[1], sys.argv[2:]
keys, where, locs = set(), {}, set()
for f in glob.glob(out + '/*.stringsdata'):
    d = json.load(open(f))
    for e in d['tables'].get('Localizable', []):
        locs.add((d['source'].split('/')[-1], e['location']['startingLine'])); keys.add(e['key']); where.setdefault(e['key'], f"{d['source'].split('/')[-1]}:{e['location']['startingLine']}")
if '--keys' in args:
    print(json.dumps(sorted(keys), ensure_ascii=False, indent=0)); sys.exit()
print(f"{len(keys)} vertaalbare teksten in Sources")
def spec(s):   # {plaats: soort}; volgorde telt, behalve met %1$@-nummering
    r, n = {}, 0
    for pos, t in re.findall(r'%(?:(\d+)\$)?((?:ll|l)?[@dfs])', re.sub(r'%%', '', s)):
        n += 1; r[int(pos) if pos else n] = t
    return r
bad = 0
for p in sorted(glob.glob('Resources/*.lproj/Localizable.strings')):
    lang = p.split('/')[1].split('.')[0]
    if lang == 'en': continue
    j = subprocess.run(['plutil', '-convert', 'json', '-o', '-', p], capture_output=True, text=True)
    if j.returncode: print(f"{lang}: KAPOT bestand: {j.stderr.strip()}"); bad += 1; continue
    have = json.loads(j.stdout)
    miss = sorted(keys - have.keys()); stale = sorted(have.keys() - keys)
    # Plaatshouders moeten kloppen, anders crasht of liegt de opmaak
    wrong = [k for k in keys & have.keys() if spec(k) != spec(have[k])]
    print(f"{lang}: {len(miss)} ontbreken, {len(stale)} ongebruikt, {len(wrong)} met andere %-plaatshouders")
    for k in miss: print(f"   - {where[k]}  {k!r}")
    for k in wrong: print(f"   ! {where[k]}  {k!r} → {have[k]!r}")
    bad += len(miss) + len(wrong)
if '--suspects' in args:
    for f in sorted(glob.glob('Sources/*.swift')):
        for i, line in enumerate(open(f), 1):
            s = line.strip()
            if s.startswith('//') or 'precondition' in s or 'print(' in s or 'L("' in s: continue
            if re.search(r'"[A-Z][a-z]+ [a-z]{2,}[^"]*"', line) and (f.split('/')[-1], i) not in locs:
                print(f"   ? {f.split('/')[-1]}:{i}  {s[:120]}")
sys.exit(1 if bad else 0)
EOF
