#!/bin/zsh
# Welke teksten van de iPhone-app missen een vertaling? Draai eerst ./build.sh (de compiler levert de lijst met teksten).
# Tabel per taal = ../Resources/<taal>.lproj/Localizable.strings (gedeeld met de Mac) + Strings/<taal>.strings (alleen iPhone).
#   --keys   ontbrekende sleutels als JSON (voor het aanvullen van een vertaling)
cd "$(dirname "$0")"
OUT="${FUSEBOX_IOS_BUILD:-$HOME/Library/Caches/FuseboxIOS}"
python3 - "$OUT" "$@" <<'PY'
import json, glob, re, sys, subprocess
out, args = sys.argv[1], sys.argv[2:]
keys, where = set(), {}
for f in glob.glob(out + '/Build/Intermediates.noindex/Fusebox.build/*/Fusebox.build/Objects-normal/*/*.stringsdata'):
    d = json.load(open(f))
    for e in d['tables'].get('Localizable', []):
        keys.add(e['key']); where.setdefault(e['key'], f"{d['source'].split('/')[-1]}:{e['location']['startingLine']}")
if not keys: sys.exit("Geen teksten gevonden: draai eerst ./build.sh")
def table(p):
    if not open(p).read().strip(): return {}
    j = subprocess.run(['plutil', '-convert', 'json', '-o', '-', p], capture_output=True, text=True)
    if j.returncode: sys.exit(f"KAPOT bestand {p}: {j.stderr.strip()}")
    return json.loads(j.stdout) if j.stdout.strip() else {}
def spec(s):
    r, n = {}, 0
    for pos, t in re.findall(r'%(?:(\d+)\$)?((?:ll|l)?[@dfs])', re.sub(r'%%', '', s)):
        n += 1; r[int(pos) if pos else n] = t
    return r
print(f"{len(keys)} vertaalbare teksten in de iPhone-app")
bad, missing = 0, set()
for lang in ['nl', 'de', 'fr', 'es']:
    mac, own = table(f'../Resources/{lang}.lproj/Localizable.strings'), table(f'Strings/{lang}.strings')
    have = {**mac, **own}
    miss = sorted(keys - have.keys()); stale = sorted(own.keys() - keys); double = sorted(own.keys() & mac.keys())
    wrong = [k for k in keys & have.keys() if spec(k) != spec(have[k])]
    print(f"{lang}: {len(miss)} ontbreken, {len(stale)} ongebruikt in Strings/, {len(double)} dubbel met de Mac, {len(wrong)} met andere %-plaatshouders")
    for k in miss: print(f"   - {where[k]}  {k!r}")
    for k in stale: print(f"   ongebruikt: {k!r}")
    for k in double: print(f"   dubbel: {k!r}")
    for k in wrong: print(f"   plaatshouders: {k!r} -> {have[k]!r}")
    bad += len(miss) + len(stale) + len(double) + len(wrong); missing |= set(miss)
if '--keys' in args: print(json.dumps(sorted(missing), ensure_ascii=False, indent=0))
sys.exit(1 if bad else 0)
PY
