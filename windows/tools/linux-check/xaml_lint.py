#!/usr/bin/env python3
"""XAML mistakes the WinUI compiler only reports on Windows: duplicate x:Key in one dictionary (WMC0065),
ThemeDictionaries not first in a ResourceDictionary (WMC0035), malformed XML. Usage: xaml_lint.py <app dir>"""
import glob, sys, xml.etree.ElementTree as ET
X = '{http://schemas.microsoft.com/winfx/2006/xaml}'
problems = []
for f in glob.glob(sys.argv[1] + '/**/*.xaml', recursive=True):
    if '/obj/' in f or '/bin/' in f: continue
    try: root = ET.parse(f).getroot()
    except ET.ParseError as e: problems.append(f'{f}: XML error {e}'); continue
    def walk(el):
        keys = set()
        kids = list(el)
        if el.tag.endswith('ResourceDictionary'):
            names = [c.tag.split('}')[-1] for c in kids]
            if 'ResourceDictionary.ThemeDictionaries' in names:
                i = names.index('ResourceDictionary.ThemeDictionaries')
                if any(not n.startswith('ResourceDictionary.') for n in names[:i]):
                    problems.append(f'{f}: ThemeDictionaries must come first (WMC0035)')
        for c in kids:
            k = c.get(X + 'Key')
            if k:
                if k in keys: problems.append(f'{f}: duplicate x:Key "{k}" (WMC0065)')
                keys.add(k)
            walk(c)
    walk(root)
for p in problems: print('error', p)
sys.exit(1 if problems else 0)
