using Fusebox.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Fusebox.App.Pages;

public sealed partial class LivePage : Page
{
    public LivePage()
    {
        InitializeComponent();
        Loaded += (_, _) => Loc.Apply(this);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e) { App.Live.Updated += Render; Render(); }
    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Live.Updated -= Render;

    void Render()
    {
        if (App.Live.Last is not { } s) return;
        CpuText.Text = $"CPU  {Fmt.Percent(s.Cpu)}";
        RamText.Text = $"RAM  {Fmt.Bytes(s.RamUsed)} / {Fmt.Bytes(s.RamTotal)}";
        NetText.Text = $"NET  ↓ {Fmt.Rate(s.NetIn)}  ↑ {Fmt.Rate(s.NetOut)}";
    }
}
