using Fusebox.Core;

public class StringsTests
{
    [Fact]
    public void ParsesAppleStrings()
    {
        var d = Strings.Parse("/* c */\n\"a\" = \"b\";\n// x\n\"q \\\"x\\\"\" = \"line\\nnext\";\n\"\" = \"\";");
        Assert.Equal("b", d["a"]);
        Assert.Equal("line\nnext", d["q \"x\""]);
    }

    [Fact]
    public void FormatsMacPlaceholders()
    {
        Strings.Load("en");
        Assert.Equal("3 of 4 x", Strings.Format("%lld of %@ x", [3, "4"]));
        Assert.Equal("b a", Strings.Format("%2$@ %1$@", ["a", "b"]));
        Assert.Equal("50% 1.5", Strings.Format("%d%% %.1f", [50, 1.5]));
    }

    [Fact]
    public void LoadsMacTranslations()
    {
        Assert.True(Strings.TableFor("nl").Count > 1000);
        Strings.Load("nl");
        Assert.Equal("  Apparaat", Strings.L("  Device"));
        Assert.Equal("untranslated key", Strings.L("untranslated key"));
        Strings.Load("en");
    }
}

public class CultureTests
{
    [Fact]
    public void NumbersUseADotInEveryLanguageDatesStayLocal()
    {
        Strings.Load("nl");
        Assert.Equal("6.2", 6.2.ToString("0.0", Strings.Culture));
        Assert.Equal("6.2%", Strings.Format("%.1f%%", [6.2]));
        Assert.Equal("november", new DateTime(2026, 11, 22).ToString("MMMM", Strings.Culture));
        Strings.Load("en");
    }
}

public class HistoryTests
{
    [Fact]
    public void RingBufferKeepsLastValues()
    {
        var h = new History(3);
        foreach (var v in new double[] { 1, 2, 3, 4 }) h.Add(v);
        Assert.Equal([2, 3, 4], h.Tail(10));
        Assert.Equal(2, h.Min); Assert.Equal(4, h.Max); Assert.Equal(3, h.Average);
        h.Add(double.NaN);
        Assert.Equal(4, h.Last);
    }
}
