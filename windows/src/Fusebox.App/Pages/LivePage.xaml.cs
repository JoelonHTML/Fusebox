using Fusebox.App.Controls;
using Fusebox.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fusebox.App.Pages;

/// <summary>Responsive grid of metric cards. Redraws only on LiveHub.Updated (never while the page is not shown).</summary>
public sealed partial class LivePage : Page
{
    const double CardMinWidth = 300;
    readonly Dictionary<Metric, MetricCard> cards = [];
    List<Metric> shown = [];
    int columns;

    public LivePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { App.Live.Updated += Render; Render(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Live.Updated -= Render;

    void Cards_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        int c = (int)Math.Clamp(Math.Floor((e.NewSize.Width + 12) / (CardMinWidth + 12)), 1, 3);
        if (c == columns) return;
        columns = c;
        ArrangeCards();
        Render();
    }

    void ArrangeCards()
    {
        Cards.ColumnDefinitions.Clear();
        Cards.RowDefinitions.Clear();
        for (int i = 0; i < Math.Max(columns, 1); i++) Cards.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < shown.Count; i++)
        {
            if (i % Math.Max(columns, 1) == 0) Cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var card = cards[shown[i]];
            Grid.SetColumn(card, i % Math.Max(columns, 1));
            Grid.SetRow(card, i / Math.Max(columns, 1));
        }
        Hint.Visibility = columns < 3 ? Visibility.Visible : Visibility.Collapsed;
    }

    void Render()
    {
        if (App.Live.Last is not { } s) return;
        // Card order and visibility from settings; GPU and BAT only when this PC has them.
        var want = Metrics.Parse(Prefs.Str("metrics")).Where(m => !double.IsNaN(Metrics.Value(m, s))).ToList();
        if (!want.SequenceEqual(shown))
        {
            shown = want;
            Cards.Children.Clear();
            foreach (var m in shown)
            {
                if (!cards.TryGetValue(m, out var card))
                {
                    cards[m] = card = new MetricCard(m);
                    card.Clicked += OnCardClicked;
                }
                Cards.Children.Add(card);
            }
            ArrangeCards();
        }
        bool wide = columns > 1 || Cards.ActualWidth > 500;
        foreach (var m in shown)
            cards[m].Update(MetricText.Summary(m, s, App.Live), App.Live.Values(m, TimeRange.Short), wide);
    }

    void OnCardClicked(Metric m) => Frame.Navigate(typeof(DetailPage), m);
}
