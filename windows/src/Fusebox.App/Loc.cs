using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Fusebox.App;

/// <summary>
/// XAML holds the English text (the key); Loc.Apply translates a loaded tree in place.
/// Text built in code uses Strings.L directly.
/// </summary>
public static class Loc
{
    public static void Apply(DependencyObject root)
    {
        switch (root)
        {
            case TextBlock t when t.Inlines.Count <= 1 && !string.IsNullOrEmpty(t.Text): t.Text = Strings.L(t.Text); break;
            case ToggleSwitch ts:
                if (ts.Header is string tsh) ts.Header = Strings.L(tsh);
                break;
            case ComboBox cb when cb.Header is string cbh: cb.Header = Strings.L(cbh); break;
            case TextBox tb:
                if (tb.Header is string th) tb.Header = Strings.L(th);
                if (!string.IsNullOrEmpty(tb.PlaceholderText)) tb.PlaceholderText = Strings.L(tb.PlaceholderText);
                break;
            case ContentControl c when c.Content is string s: c.Content = Strings.L(s); break;
        }
        if (root is FrameworkElement fe && ToolTipService.GetToolTip(fe) is string tip) ToolTipService.SetToolTip(fe, Strings.L(tip));
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++) Apply(VisualTreeHelper.GetChild(root, i));
    }
}
