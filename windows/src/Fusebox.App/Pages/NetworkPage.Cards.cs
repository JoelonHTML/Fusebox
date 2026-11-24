using Fusebox.App.Controls;
using Fusebox.Core.Look;
using Fusebox.Core.Network;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using static Fusebox.Core.Strings;

namespace Fusebox.App.Pages;

/// <summary>The three cards (Mac NetwerkKaarten): frame, header with ⓘ, face, "Details" toggle (pref per card), plus the hover explanation of every value.
/// Cards sit side by side from 700 DIPs, below each other when narrower. Content is filled by RenderConnection / RenderLink / RenderSpeed.</summary>
public sealed partial class NetworkPage
{
    sealed class Card
    {
        public readonly Border Frame = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        public readonly Grid Body = new() { RowSpacing = 8 };   // header | face | toggle | details | spacer
        public readonly FontIcon Icon = new() { FontSize = 11, Width = 14 };
        public readonly TextBlock Title = new();
        public readonly StackPanel Face = new() { Spacing = 6 };
        public readonly StackPanel Details = new() { Spacing = 4 };
        public readonly Border DetailsBox = new();
        public readonly TextBlock ToggleText = new();
        public readonly FontIcon Chevron = new() { Glyph = "", FontSize = 8, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5), RenderTransform = new RotateTransform() };
        public Button Toggle = null!;
        public Button? Menu;
        public NetTerm[] Terms = [];
        public string? OpenKey;
    }

    Card[] cards = [];
    bool? stackedNow;
    MenuFlyoutItem miTest = null!, miIp = null!;

    // ---- build ----

    void BuildCards()
    {
        CardsHost.Children.Clear();
        graph = new NetGraph { Height = 34, MinWidth = 50, HorizontalAlignment = HorizontalAlignment.Stretch };
        graphCaption = Lbl("", 9, "FuseboxOnRowSecondary", wrap: false);
        graphCaption.HorizontalAlignment = HorizontalAlignment.Right;
        Tip(graphCaption, NetTerm.Live);
        graphBox = new StackPanel { Spacing = 2, MinWidth = 50 };
        graphBox.Children.Add(graph); graphBox.Children.Add(graphCaption);
        cards = [MakeCard(0, "", L("Connection"), NetTerms.Connection, true), MakeCard(1, "", "Wi-Fi", NetTerms.Wifi, false), MakeCard(2, "", L("Speed test"), NetTerms.Speed, false)];
        foreach (var c in cards) CardsHost.Children.Add(c.Frame);
        stackedNow = null;
        LayoutCards();
    }

    Card MakeCard(int index, string glyph, string title, NetTerm[] terms, bool menu)
    {
        var c = new Card { Terms = terms };
        StyleFrame(c, index);
        c.Icon.Glyph = glyph; c.Icon.Foreground = B("FuseboxOnRowSecondary"); c.Icon.VerticalAlignment = VerticalAlignment.Center;
        c.Title.Text = title; c.Title.Foreground = B("FuseboxOnRow"); c.Title.TextTrimming = TextTrimming.CharacterEllipsis; c.Title.VerticalAlignment = VerticalAlignment.Center;
        Look.Label(c.Title, 12);

        var head = new Grid { ColumnSpacing = 6 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(c.Title, 1);
        head.Children.Add(c.Icon); head.Children.Add(c.Title);

        if (menu)   // ⋯ : the rare actions of the Connection card
        {
            miTest = new MenuFlyoutItem { Text = L("Test again") };
            miTest.Click += (_, _) => StartQuality();
            miIp = new MenuFlyoutItem { Text = L("Show public IP") };
            miIp.Click += (_, _) => { Prefs.Set("netConnDetails", true); ApplyOpen(cards[0]); if (extras == null) LoadExtras(); LoadPublicIp(); };
            var fly = new MenuFlyout();
            fly.Items.Add(miTest); fly.Items.Add(miIp);
            c.Menu = Plain(Icon("", 11, "FuseboxOnRowSecondary"), L("More actions"), 22, 20, (_, _) => { });
            c.Menu.Flyout = fly;
            Grid.SetColumn(c.Menu, 2);
            head.Children.Add(c.Menu);
        }

        var info = Plain(Icon("", 12, "FuseboxOnRowSecondary"), L("What the numbers mean"), 22, 20, (_, _) => { });
        info.Flyout = InfoFlyout(c);
        Grid.SetColumn(info, 3);
        head.Children.Add(info);

        var toggleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        c.ToggleText.Foreground = B("FuseboxOnRowSecondary"); Look.Label(c.ToggleText, 10);
        c.Chevron.Foreground = B("FuseboxOnRowSecondary"); c.Chevron.VerticalAlignment = VerticalAlignment.Center;
        toggleRow.Children.Add(c.ToggleText); toggleRow.Children.Add(c.Chevron);
        c.Toggle = Plain(toggleRow, null, double.NaN, double.NaN, (_, _) => ToggleOpen(c));
        c.Toggle.HorizontalAlignment = HorizontalAlignment.Left; c.Toggle.Padding = new Thickness(2, 2, 4, 2);

        c.DetailsBox.BorderBrush = B(Blocks ? "FuseboxOnRowSecondary" : "FuseboxLine");
        c.DetailsBox.BorderThickness = new Thickness(0, 1, 0, 0); c.DetailsBox.Padding = new Thickness(0, 6, 0, 0);
        c.DetailsBox.Child = c.Details;

        for (int i = 0; i < 5; i++) c.Body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(c.Face, 1); Grid.SetRow(c.Toggle, 2); Grid.SetRow(c.DetailsBox, 3);
        c.Body.Children.Add(head); c.Body.Children.Add(c.Face); c.Body.Children.Add(c.Toggle); c.Body.Children.Add(c.DetailsBox);
        c.Frame.Child = c.Body;
        return c;
    }

    void StyleFrame(Card c, int index)
    {
        if (ThemeService.Current.RowStyle is RowStyle.Lines or RowStyle.Plain)   // a soft card of its own (Mac NetCardShape)
        {
            c.Frame.Background = B("FuseboxCardFill"); c.Frame.BorderBrush = B("FuseboxLine"); c.Frame.BorderThickness = new Thickness(1);
            c.Frame.CornerRadius = new CornerRadius(12); c.Frame.Padding = new Thickness(12);
        }
        else
        {
            ThemedRow.Apply(c.Frame, index);
            var p = c.Frame.Padding;
            c.Frame.Padding = new Thickness(p.Left + 2, p.Top + 4, p.Right + 2, p.Bottom + 4);
        }
    }

    /// <summary>ⓘ: all explanations of this card (built when it opens, so a card that switches from Wi-Fi to cable lists the right terms).</summary>
    Flyout InfoFlyout(Card c)
    {
        var fly = new Flyout { Placement = FlyoutPlacementMode.Bottom };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        fly.FlyoutPresenterStyle = presenter;
        fly.Opening += (_, _) =>
        {
            var list = new StackPanel { Spacing = 10, Width = 300, Padding = new Thickness(14) };
            list.Children.Add(Lbl(L("What the numbers mean"), 13, "FuseboxText"));
            foreach (var t in c.Terms)
            {
                var item = new StackPanel { Spacing = 2 };
                item.Children.Add(Lbl(NetTerms.Title(t), 11.5, "FuseboxText"));
                item.Children.Add(Lbl(NetTerms.Text(t), 11.5, "FuseboxSecondary"));
                list.Children.Add(item);
            }
            fly.Content = c.Terms.Length > 3 ? new ScrollViewer { Content = list, MaxHeight = 460 } : list;
        };
        return fly;
    }

    /// <summary>Cards side by side from 700 DIPs, otherwise under each other (equal height side by side: the grid stretches them).</summary>
    void LayoutCards()
    {
        if (cards.Length != 3) return;
        bool stacked = Root.ActualWidth < 700;
        if (stackedNow == stacked) return;
        stackedNow = stacked;
        CardsHost.RowDefinitions.Clear(); CardsHost.ColumnDefinitions.Clear();
        CardsHost.ColumnSpacing = 10; CardsHost.RowSpacing = 10;
        for (int i = 0; i < 3; i++)
        {
            if (stacked) CardsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            else CardsHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(cards[i].Frame, stacked ? i : 0); Grid.SetColumn(cards[i].Frame, stacked ? 0 : i);
        }
    }

    // ---- details toggle (pref per card, Mac netConnDetails / netLinkDetails / netSpeedDetails) ----

    void ToggleOpen(Card c)
    {
        if (c.OpenKey is not { } key) return;
        Prefs.Set(key, !Prefs.Bool(key));
        ApplyOpen(c);
        if (c == cards[0] && Prefs.Bool(key) && extras == null) LoadExtras();
    }

    /// <summary>Shows or hides the "Details" row and part. Closed: the toggle sits at the bottom (equal with its neighbours); open: right below the face.</summary>
    void ApplyOpen(Card c)
    {
        bool has = c.OpenKey != null, open = has && Prefs.Bool(c.OpenKey!);
        c.Toggle.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        c.DetailsBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        c.ToggleText.Text = open ? L("Fewer details") : L("Details");
        ((RotateTransform)c.Chevron.RenderTransform).Angle = open ? 180 : 0;
        bool grow = has && !open;
        c.Body.RowDefinitions[2].Height = grow ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        c.Body.RowDefinitions[4].Height = grow ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        c.Toggle.VerticalAlignment = grow ? VerticalAlignment.Bottom : VerticalAlignment.Top;
    }

    void RenderCards() { RenderConnection(); RenderLink(); RenderSpeed(); }

    // ---- building blocks ----

    /// <summary>Hover explanation of a value (and help text for screen readers).</summary>
    static void Tip(FrameworkElement e, NetTerm t)
    {
        var text = NetTerms.Text(t);
        ToolTipService.SetToolTip(e, text);
        AutomationProperties.SetHelpText(e, text);
    }

    /// <summary>Label of a value with its explanation on hover.</summary>
    static TextBlock Term(NetTerm t, string? title = null, double pt = 10, bool wrap = false)
    {
        var tb = Lbl(title ?? NetTerms.Title(t), pt, "FuseboxOnRowSecondary", wrap);
        Tip(tb, t);
        return tb;
    }

    /// <summary>Label (96 DIPs) + value, the whole row explains itself on hover.</summary>
    static Grid Kv(NetTerm t, string value, string? title = null)
    {
        var g = new Grid { ColumnSpacing = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var k = Term(t, title, 11, wrap: true);
        var v = Lbl(value, 11, "FuseboxOnRow");
        v.IsTextSelectionEnabled = true;
        Grid.SetColumn(v, 1);
        g.Children.Add(k); g.Children.Add(v);
        Tip(g, t);
        return g;
    }

    static TextBlock Subhead(string title) { var t = Lbl(title, 11, "FuseboxOnRow"); t.Margin = new Thickness(0, 6, 0, 0); return t; }

    /// <summary>Big number + small unit.</summary>
    static StackPanel Big(string value, string unit)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var v = Num(value, 24); v.VerticalAlignment = VerticalAlignment.Bottom;
        var u = Lbl(unit, 11, "FuseboxOnRowSecondary", wrap: false); u.VerticalAlignment = VerticalAlignment.Bottom; u.Margin = new Thickness(0, 0, 0, 3);
        sp.Children.Add(v); sp.Children.Add(u);
        return sp;
    }

    /// <summary>"412 Mbit/s" becomes a big number and a small unit.</summary>
    static StackPanel BigSplit(string s)
    {
        var i = s.IndexOf(' ');
        return i < 0 ? Big(s, "") : Big(s[..i], s[(i + 1)..]);
    }

    /// <summary>A labelled number: term label above, big value below.</summary>
    static StackPanel Stat(NetTerm t, StackPanel big, string? title = null)
    {
        var sp = new StackPanel { Spacing = 1, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        sp.Children.Add(Term(t, title));
        sp.Children.Add(big);
        Tip(sp, t);
        return sp;
    }

    static string LinkSpeedText(double mbit) => mbit >= 1000 && mbit % 1000 == 0 ? $"{(int)(mbit / 1000)} Gbit/s" : $"{(int)mbit} Mbit/s";
}
