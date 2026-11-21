#!/usr/bin/env python3
"""Checks that every English UI string the Windows app uses is translated (nl, de, fr, es).

Keys = literals passed to L( / Strings.L( in windows/src/**/*.cs plus translated XAML attributes in
src/Fusebox.App/**/*.xaml. Translations = Mac Resources/<lang>.lproj/Localizable.strings, overridden by
windows/Strings/<lang>.strings (same merge as Strings.cs). Exit 1 on missing keys or placeholder mismatches.
Run from the windows folder: python3 tools/strings-check.py
"""
import re, sys, glob, os
import xml.etree.ElementTree as ET

WIN = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
MAC = os.path.join(os.path.dirname(WIN), "Resources")
LANGS = ["nl", "de", "fr", "es"]
NEVER = {"Windows", "Assets", "Fusebox.ico", "Fusebox", "Live", "Checkup", "Docked", "CPU", "GPU", "RAM", "SSD", "NET", "BAT", "I/O",
         "Glass", "Minimal light", "Color blocks", "Instrument", "Terminal"}
PH = re.compile(r"%(?:\d+\$)?[-+ 0#]*\d*(?:\.\d+)?(?:ll|l|h)?([@dDiuf%s])")


def parse(text):
    """Apple .strings parser, same rules as Strings.Parse."""
    d, i, key = {}, 0, None
    while i < len(text):
        c = text[i]
        if text.startswith("/*", i):
            e = text.find("*/", i + 2)
            i = len(text) if e < 0 else e + 2
            continue
        if text.startswith("//", i):
            e = text.find("\n", i)
            i = len(text) if e < 0 else e
            continue
        if c == '"':
            out, i = [], i + 1
            while i < len(text) and text[i] != '"':
                if text[i] == "\\" and i + 1 < len(text):
                    i += 1
                    out.append({"n": "\n", "t": "\t", "r": "\r"}.get(text[i], text[i]))
                else:
                    out.append(text[i])
                i += 1
            i += 1
            if key is None:
                key = "".join(out)
            else:
                d[key] = "".join(out)
                key = None
            continue
        if c == ";":
            key = None
        i += 1
    return d


def load(path):
    try:
        with open(path, encoding="utf-8-sig") as f:
            return parse(f.read())
    except FileNotFoundError:
        return {}


def unescape(s):
    return re.sub(r"\\(.)", lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), s)


CS_STR = r'(?:@"(?:[^"]|"")*"|"(?:[^"\\\n]|\\.)*")'
CALL = re.compile(r'(?<![\w.])(?:Strings\.)?L\(\s*(\$?)(' + CS_STR + r')((?:\s*\+\s*' + CS_STR + r')*)')
INDIRECT = re.compile(r'(?<![\w.])(?:Strings\.)?L\(\s*[A-Za-z_(]')
LIT = re.compile(CS_STR)


def lit(s):
    if s.startswith("@"):
        return s[2:-1].replace('""', '"')
    return unescape(s[1:-1])


def cs_keys(warn):
    keys = {}
    for p in glob.glob(os.path.join(WIN, "src", "**", "*.cs"), recursive=True):
        if os.path.basename(p) in ("Strings.cs", "Loc.cs"):
            continue
        t = open(p, encoding="utf-8-sig").read()
        rel = os.path.relpath(p, WIN)
        for m in CALL.finditer(t):
            if m.group(1) == "$":
                warn.append(f"{rel}: interpolated key {m.group(2)[:50]}")
                continue
            k = lit(m.group(2)) + "".join(lit(x) for x in LIT.findall(m.group(3)))
            keys.setdefault(k, rel)
        # L(variable): the keys live elsewhere in the file, so collect its capitalised literals instead.
        if INDIRECT.search(t):
            warn.append(f"{rel}: L(variable) call, collecting capitalised literals of the file instead")
            for m in LIT.finditer(t):
                s = lit(m.group(0))
                if re.match(r"[A-Z][A-Za-z' ,.!?():%@-]{3,}$", s):
                    keys.setdefault(s, rel)
    return keys


TEXT_ATTRS = {"TextBlock": ["Text"], "ToggleSwitch": ["Header"], "ComboBox": ["Header"],
              "TextBox": ["Header", "PlaceholderText"]}
CONTENT = {"Button", "CheckBox", "RadioButton", "ToggleButton", "HyperlinkButton", "NavigationViewItem",
           "ToggleMenuFlyoutItem", "MenuFlyoutItem", "AppBarButton", "ComboBoxItem"}


def xaml_keys():
    keys = {}
    for p in glob.glob(os.path.join(WIN, "src", "Fusebox.App", "**", "*.xaml"), recursive=True):
        rel = os.path.relpath(p, WIN)
        for el in ET.parse(p).getroot().iter():
            tag = el.tag.split("}")[-1]
            names = list(TEXT_ATTRS.get(tag, [])) + (["Content"] if tag in CONTENT else []) + ["ToolTipService.ToolTip"]
            for a in names:
                v = el.attrib.get(a)
                if v is not None:
                    keys.setdefault(v, rel)
            if tag == "TextBlock" and len(el) == 0 and el.text and el.text.strip():
                keys.setdefault(el.text.strip(), rel)
    return keys


def wanted(k):
    return not (k.startswith("{") or k in NEVER or not re.search(r"[A-Za-z]{2}", k))


def phs(s):
    return sorted(PH.findall(s.replace("%%", "")))


def main():
    warn = []
    keys = {k: f for k, f in {**xaml_keys(), **cs_keys(warn)}.items() if wanted(k)}
    bad = 0
    for lang in LANGS:
        mac = load(os.path.join(MAC, f"{lang}.lproj", "Localizable.strings"))
        win = load(os.path.join(WIN, "Strings", f"{lang}.strings"))
        table = {**mac, **win}
        missing = sorted(k for k in keys if not table.get(k))
        mism = sorted(k for k in keys if table.get(k) and phs(k) != phs(table[k]))
        mism += sorted(k for k, v in win.items() if k not in keys and phs(k) != phs(v))
        print(f"{lang}: {len(keys)} keys used, {len(missing)} missing, {len(mism)} placeholder mismatches, {len(win)} Windows entries")
        for k in missing:
            print(f"  MISSING  {k!r}  ({keys[k]})")
        for k in mism:
            print(f"  PLACEHOLDER  {k!r} -> {table.get(k)!r}")
        bad += len(missing) + len(mism)
    for w in sorted(set(warn)):
        print("warning:", w)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
