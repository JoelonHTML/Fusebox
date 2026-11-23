using Fusebox.Core.Look;

public class ThemeTests
{
    static readonly Argb Blue = new(255, 0, 120, 212);   // Windows default accent

    static Theme R(string id, Scheme s, string accent = "", double scale = 1, string font = "style", bool low = false) =>
        Themes.Resolve(id, accent, scale, font, s, low, Blue);

    [Fact]
    public void ExactMacTokens()
    {
        var i = Themes.Instrument;
        Assert.Equal("#1F2B3D", i.Bg!.Value.Hex);
        Assert.Equal("#F2F5F7", i.Text.Hex);
        Assert.Equal("#99A6B8", i.Secondary.Hex);
        Assert.Equal("#F5B83D", i.Accent.Hex);
        Assert.Equal("#FF6159", i.Danger.Hex);
        Assert.Equal("#7DFC99", Themes.Terminal.Text.Hex);
        Assert.Equal("#006BD9", Themes.Minimal.Accent.Hex);
        Assert.Equal("#5C4AD6", Themes.Glass.Bg!.Value.Hex);
        Assert.Equal(199, Themes.Glass.Secondary.A);   // white @ 78 %
        Assert.Equal(["#E0573D", "#2E7DD6", "#1C9E75", "#7A54C7", "#D9991A"], Themes.Blocks.Blocks.Select(b => b.Hex));
        Assert.Null(Themes.Liquid.Bg);
        Assert.Equal(["instrument", "terminal", "minimal", "glas", "blokken", "liquid"], Themes.All.Select(t => t.Id));
    }

    [Fact]
    public void UnknownIdFallsBackToInstrument() => Assert.Equal("instrument", R("glass", Scheme.Dark).Id);

    [Fact]
    public void LightConversionKeepsAccent()
    {
        var t = R("instrument", Scheme.Light);
        Assert.Equal("#FFFFFF", t.Bg!.Value.Hex);
        Assert.Equal("#141414", t.Text.Hex);
        Assert.Equal("#737373", t.Secondary.Hex);
        Assert.Equal("#E6E6E6", t.Line.Hex);
        Assert.Equal("#F5B83D", t.Accent.Hex);         // luminance 0.54: stays
        Assert.Equal(RowStyle.Lines, t.RowStyle);
        Assert.Equal(Scheme.Light, t.Scheme);
        Assert.Equal("#141414", R("glas", Scheme.Light).Accent.Hex);
    }

    [Fact]
    public void DarkConversionAndAccentBrightening()
    {
        var t = R("minimal", Scheme.Dark);
        Assert.Equal("#1C1C1C", t.Bg!.Value.Hex);
        Assert.Equal("#F2F2F2", t.Text.Hex);
        Assert.Equal("#292929", t.Card.Hex);
        Assert.Equal("#66B1FF", t.Accent.Hex);         // #006BD9 too dark on dark: HSB(h, s×0.6, 1)
        var b = R("blokken", Scheme.Dark);
        Assert.Equal(RowStyle.Blocks, b.RowStyle);
        Assert.Equal("#E0573D", b.Block(0).Hex);       // blocks keep their colors in dark mode
        Assert.Equal("#2E7DD6", b.Block(6).Hex);
    }

    [Fact]
    public void LightAccentDarkensAlsoOnBlocks()
    {
        Assert.Equal("#008C8C", R("minimal", Scheme.Light, "#00FFFF").Accent.Hex);
        Assert.Equal("#008C8C", R("blokken", Scheme.Light, "00ffff").Accent.Hex);   // Mac 0.13: readable on Color blocks too
        Assert.Equal("#F5B83D", R("instrument", Scheme.Dark, "#12345G").Accent.Hex);   // bad hex = theme accent
    }

    [Fact]
    public void HexParsing()
    {
        Assert.Null(Argb.FromHex("abc"));
        Assert.Null(Argb.FromHex("#1234567"));
        Assert.Null(Argb.FromHex(""));
        Assert.Equal(new Argb(255, 0x12, 0x34, 0x56), Argb.FromHex("123456"));
        Assert.Equal("#ABCDEF", Argb.FromHex("#abcdef")!.Value.Hex);
    }

    [Fact]
    public void LuminanceAndHsbRoundTrip()
    {
        Assert.Equal(1.0, Argb.White(1).Luminance, 3);
        Assert.Equal(0.0, Argb.White(0).Luminance, 3);
        var c = new Argb(255, 46, 125, 214);
        var (h, s, v) = c.Hsb;
        Assert.Equal(c, Argb.FromHsb(h, s, v));
    }

    [Fact]
    public void LiquidFollowsTheSystem()
    {
        var d = R("liquid", Scheme.Dark);
        var l = R("liquid", Scheme.Light);
        Assert.Null(d.Bg);
        Assert.Equal("#FFFFFF", d.Text.Hex);
        Assert.Equal(0, l.Text.R);
        Assert.Equal(Blue, l.Accent);
        Assert.Equal(RowStyle.Glass, d.RowStyle);
    }

    [Fact]
    public void LowImpactOnlyChangesLiquid()
    {
        var t = R("liquid", Scheme.Dark, low: true);
        Assert.True(t.Solid);
        Assert.Equal(RowStyle.Cards, t.RowStyle);
        Assert.NotNull(t.Bg);
        Assert.Equal(22, t.RowPad);
        Assert.True(R("instrument", Scheme.Dark).SameLook(R("instrument", Scheme.Dark, low: true)));
        Assert.False(R("terminal", Scheme.Dark, low: true).Solid);
    }

    [Fact]
    public void ScaleFontAndRowSpecs()
    {
        Assert.Equal(1.6, R("minimal", Scheme.Light, scale: 2).Scale);
        Assert.Equal(0.8, R("minimal", Scheme.Light, scale: 0.1).Scale);
        Assert.Equal(1.0, R("minimal", Scheme.Light, scale: double.NaN).Scale);
        Assert.Equal(FontFace.Rounded, R("instrument", Scheme.Dark).Face);
        Assert.Equal(FontFace.Mono, R("terminal", Scheme.Dark).Face);
        Assert.Equal(FontFace.Segoe, R("minimal", Scheme.Light).Face);
        Assert.Equal(FontFace.Serif, R("terminal", Scheme.Dark, font: "serif").Face);
        Assert.Equal(FontFace.Mono, R("terminal", Scheme.Dark, font: "comic").Face);   // unknown font = style
        Assert.Equal(15, Themes.Instrument.RowPad);
        Assert.Equal(10, Themes.Terminal.RowPad);
        Assert.Equal(12, RowSpec.For(RowStyle.Cards).Radius);
        Assert.Equal(14, RowSpec.For(RowStyle.Glass).Radius);
    }

    [Fact]
    public void RowColors()
    {
        var b = R("blokken", Scheme.Light);
        Assert.Equal("#FFFFFF", b.OnRow().Hex);
        Assert.Equal(204, b.OnRow(true).A);
        Assert.Equal(b.OnRow(), b.Level(2));            // blocks never turn red
        var i = R("instrument", Scheme.Dark);
        Assert.Equal(i.Accent, i.LabelColor);
        Assert.Equal(i.Danger, i.Level(2));
        Assert.Equal(i.Warn, i.Level(1));
        Assert.Equal(R("minimal", Scheme.Light).Secondary, R("minimal", Scheme.Light).LabelColor);
    }
}
