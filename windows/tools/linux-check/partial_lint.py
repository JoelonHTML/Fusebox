#!/usr/bin/env python3
"""Native AOT + CsWinRT: a C# class that derives from a WinUI type must be `partial`, otherwise WinUI's virtual calls
(MeasureOverride, OnApplyTemplate…) into it fail at runtime with InvalidCastException. Usage: partial_lint.py <src dir>"""
import re, sys, glob
WINUI = r"(Grid|StackPanel|Panel|Border|UserControl|ContentControl|Button|ButtonBase|Control|Canvas|TextBlock|ToggleButton|Page|Window|ListView|ListViewBase|ItemsControl|FrameworkElement|UIElement|ScrollViewer|Viewbox|RelativePanel|Expander|HyperlinkButton|ComboBox|TextBox|Slider|Flyout|ContentPresenter|Shape|Path|Rectangle|Ellipse|Image|ToolTip|Popup|InfoBar|SelectorBar|NavigationView|Application|ResourceDictionary|DependencyObject)"
pat = re.compile(r"^\s*(?:(?:public|internal|private|protected|sealed|abstract|static|partial)\s+)*class\s+(\w+)(?:<[^>]*>)?\s*:\s*" + WINUI + r"\b")
bad = 0
for f in glob.glob(sys.argv[1] + "/**/*.cs", recursive=True):
    if "/obj/" in f or "/bin/" in f: continue
    for n, line in enumerate(open(f, encoding="utf-8"), 1):
        m = pat.match(line)
        if m and not re.search(r"\bpartial\b", line.split(":")[0]):
            print(f"error {f}:{n}: class {m.group(1)} derives from a WinUI type but is not partial (AOT InvalidCastException)"); bad += 1
sys.exit(1 if bad else 0)
