#!/bin/bash
# Type-check the WinUI app's C# on Linux. XAML itself is only validated by the Windows CI.
cd "$(dirname "$0")"
mkdir -p obj
python3 xaml_lint.py ../../src/Fusebox.App || echo "XAML lint errors above"
python3 partial_lint.py ../../src || echo "partial lint errors above"
python3 xaml_stubs.py ../../src/Fusebox.App obj/XamlStubs.g.cs
dotnet build -nologo -v q 2>&1 | python3 -c "
import sys,re
seen=set()
for l in sys.stdin:
    if 'error' not in l and 'Build succeeded' not in l: continue
    l=re.sub(r'^.*?/windows/','',l.strip()); l=re.sub(r' \[/.*\]$','',l)
    if l not in seen: seen.add(l); print(l)
print(len([s for s in seen if 'error' in s]),'errors')"
