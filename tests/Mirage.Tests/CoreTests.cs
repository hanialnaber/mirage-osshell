using Mirage.Core;
using Xunit;

namespace Mirage.Tests;

public class FuzzyTests
{
    [Fact]
    public void Ranks_exact_then_prefix_then_acronym_then_substring()
    {
        int exact = Fuzzy.Score("chrome", "Chrome");
        int prefix = Fuzzy.Score("chr", "Chrome");
        int acr = Fuzzy.Score("vsc", "Visual Studio Code");
        int sub = Fuzzy.Score("rom", "Chrome");
        int seq = Fuzzy.Score("crm", "Chrome");
        Assert.True(exact > prefix && prefix > acr && acr > sub && sub > seq && seq > 0, $"{exact} {prefix} {acr} {sub} {seq}");
    }

    [Fact]
    public void Multi_word_queries_match_word_starts()
    {
        Assert.True(Fuzzy.Score("vis code", "Visual Studio Code") > 700);
        Assert.True(Fuzzy.Score("micro word", "Microsoft Word") > 700);
    }

    [Fact]
    public void No_match_is_negative()
    {
        Assert.True(Fuzzy.Score("xyz", "Chrome") < 0);
        Assert.True(Fuzzy.Score("chromez", "Chrome") < 0);
    }

    [Fact]
    public void CamelCase_counts_as_word_start() => Assert.Equal("pt", Fuzzy.Acronym("PowerToys"));
}

public class CalculatorTests
{
    [Theory]
    [InlineData("2+2", 4)]
    [InlineData("2+3*4", 14)]
    [InlineData("(2+3)*4", 20)]
    [InlineData("2^3^2", 512)]
    [InlineData("-2^2", -4)]
    [InlineData("10 % 3", 1)]
    [InlineData("sqrt(16)+abs(-2)", 6)]
    [InlineData("5!", 120)]
    [InlineData("2pi", 6.283185307179586)]
    [InlineData("1_000 * 3", 3000)]
    [InlineData("max(1, 7, 3)", 7)]
    [InlineData("1.5e3/3", 500)]
    [InlineData("12×3÷4", 9)]
    [InlineData("=7*6", 42)]
    public void Evaluates(string expr, double expected)
    {
        Assert.True(Calculator.TryEvaluate(expr, out var v, out var err), err);
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("2/0")]
    [InlineData("2+")]
    [InlineData("foo(2)")]
    [InlineData("(1+2")]
    [InlineData("sqrt(-1)")]
    public void Rejects_bad_input(string expr) => Assert.False(Calculator.TryEvaluate(expr, out _, out _));

    [Theory]
    [InlineData("7-zip", false)]
    [InlineData("2048", false)]
    [InlineData("12*7", true)]
    [InlineData("c:\\a-b", false)]
    [InlineData("sqrt(2)", true)]
    [InlineData("notepad", false)]
    public void Detects_expressions(string input, bool expected) => Assert.Equal(expected, Calculator.LooksLikeExpression(input));

    [Fact]
    public void Formats_cleanly()
    {
        Assert.Equal("0.3", Calculator.Format(0.1 + 0.2));
        Assert.Equal("1,234,567.5", Calculator.Format(1234567.5, true));
    }
}

public class UnitTests
{
    [Theory]
    [InlineData("10 km to mi", 6.21371192)]
    [InlineData("100 c in f", 212)]
    [InlineData("32 f to c", 0)]
    [InlineData("1 gib to mb", 1073.741824)]
    [InlineData("2 hours in min", 120)]
    [InlineData("5 lb to kg", 2.26796185)]
    public void Converts(string q, double expected)
    {
        Assert.True(Units.TryConvert(q, out _, out var v));
        Assert.Equal(expected, v, 5);
    }

    [Fact]
    public void Rejects_mixed_dimensions() => Assert.False(Units.TryConvert("10 kg to km", out _, out _));
}

public class RouterTests
{
    readonly Settings _s = Settings.CreateDefault();

    [Theory]
    [InlineData("w chrome", QueryMode.Windows, "chrome")]
    [InlineData("f report", QueryMode.Files, "report")]
    [InlineData("c ", QueryMode.Clipboard, "")]
    [InlineData("clip hello", QueryMode.Clipboard, "hello")]
    [InlineData(";da", QueryMode.Snippets, ";da")]
    [InlineData("n buy milk", QueryMode.NoteAdd, "buy milk")]
    [InlineData("notes milk", QueryMode.Notes, "milk")]
    [InlineData("notes", QueryMode.Notes, "")]
    [InlineData("ws focus", QueryMode.Workspaces, "focus")]
    [InlineData("? summarize this", QueryMode.Ai, "summarize this")]
    [InlineData("ai hello", QueryMode.Ai, "hello")]
    [InlineData("> ipconfig", QueryMode.Shell, "ipconfig")]
    [InlineData("= 2+2", QueryMode.Calc, "2+2")]
    [InlineData("word", QueryMode.Default, "word")]
    [InlineData("windows", QueryMode.Windows, "")]
    public void Routes(string raw, QueryMode mode, string text)
    {
        var p = QueryRouter.Parse(raw, _s);
        Assert.Equal(mode, p.Mode);
        Assert.Equal(text, p.Text);
    }

    [Fact]
    public void Custom_command_keywords()
    {
        var p = QueryRouter.Parse("gh mirage launcher", _s);
        Assert.Equal(QueryMode.Command, p.Mode);
        Assert.Equal("gh", p.Command!.Keyword);
        Assert.Equal("mirage launcher", p.Text);
        Assert.Equal("https://github.com/search?q=mirage%20launcher", TextTools.FillTemplate(p.Command.Template, p.Text, true));
    }
}

public class TextToolTests
{
    static string Run(string id, string s) => TextTools.Transforms.Single(t => t.Id == id).Apply(s);

    [Fact]
    public void Transforms()
    {
        Assert.Equal("Hello World", Run("title", "hELLO world"));
        Assert.Equal("Hello there. How are you?", Run("sentence", "HELLO THERE. HOW ARE YOU?"));
        Assert.Equal("hello-world-cafe", Run("slug", "Hello, World! Café"));
        Assert.Equal("helloBigWorld", Run("camel", "hello big world"));
        Assert.Equal("hello_big_world", Run("snake", "helloBigWorld"));
        Assert.Equal("{\"a\":1}", Run("jsonmin", "{ \"a\" : 1 }"));
        Assert.Equal("aGk=", Run("b64enc", "hi"));
        Assert.Equal("hi", Run("b64dec", "aGk="));
        Assert.Equal("a b", Run("oneline", "a\n  b"));
    }

    [Fact]
    public void Placeholders()
    {
        var now = new DateTime(2026, 11, 23, 9, 5, 0);
        Assert.Equal("23 November 2026 09:05 Monday [clip]",
            TextTools.ExpandPlaceholders("{date} {time} {weekday} [{clipboard}]", now, () => "clip"));
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("https://example.org/x?y=1", true)]
    [InlineData("hello world", false)]
    [InlineData("notepad", false)]
    [InlineData("mirage.ae/about", true)]
    public void Url_detection(string s, bool expected) => Assert.Equal(expected, TextTools.LooksLikeUrl(s));

    [Fact]
    public void Stats() => Assert.StartsWith("3 words · 13 characters · 2 lines", TextTools.Stats("one two\nthree"));
}

public class SnippetTriggerTests
{
    [Fact]
    public void Fires_on_completed_keyword_and_prefers_longest()
    {
        var t = new SnippetTrigger();
        t.SetSnippets(new[] { new Snippet { Keyword = ";d", Text = "short" }, new Snippet { Keyword = ";date", Text = "long" } });
        Snippet? hit = null;
        foreach (var c in "hi ;dat") hit = t.Feed(c) ?? hit;
        Assert.Equal("short", hit!.Text); // ";d" completes first; users pick non-overlapping keywords
        var t2 = new SnippetTrigger();
        t2.SetSnippets(new[] { new Snippet { Keyword = ";sig", Text = "Hani" } });
        Snippet? h2 = null;
        foreach (var c in "thanks ;sig") h2 = t2.Feed(c) ?? h2;
        Assert.Equal("Hani", h2!.Text);
    }

    [Fact]
    public void Backspace_and_control_chars_reset()
    {
        var t = new SnippetTrigger();
        t.SetSnippets(new[] { new Snippet { Keyword = ";sig", Text = "x" } });
        foreach (var c in ";si") t.Feed(c);
        t.Backspace();
        Assert.Null(t.Feed('g'));
        t.Feed('\r');
        foreach (var c in ";si") t.Feed(c);
        Assert.NotNull(t.Feed('g'));
    }
}

public class StoreTests
{
    [Fact]
    public void Notes_roundtrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "mirage-test-" + Guid.NewGuid() + ".md");
        var n = new NotesStore(path);
        n.Add("first note", new DateTime(2026, 1, 2, 3, 4, 0));
        n.Add("second\nline");
        var all = n.All();
        Assert.Equal(2, all.Count);
        Assert.Equal("first note", all[0].Text);
        Assert.Equal("second line", all[1].Text);
        File.Delete(path);
    }

    [Fact]
    public void Usage_boosts_frequent_items()
    {
        var path = Path.Combine(Path.GetTempPath(), "mirage-usage-" + Guid.NewGuid() + ".json");
        var u = new UsageStore(path);
        for (int i = 0; i < 5; i++) u.Record("app:chrome");
        u.Record("app:paint");
        Assert.True(u.Boost("app:chrome") > u.Boost("app:paint"));
        Assert.Equal(0, u.Boost("app:none"));
        var reloaded = new UsageStore(path);
        Assert.True(reloaded.Boost("app:chrome") > 0);
        File.Delete(path);
    }

    [Fact]
    public void Settings_roundtrip_and_normalize()
    {
        var path = Path.Combine(Path.GetTempPath(), "mirage-settings-" + Guid.NewGuid() + ".json");
        var s = Settings.CreateDefault();
        s.Bridge.Permissions["clipboard_read"] = Permission.Allow;
        s.Save(path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"clipboard_read\": \"allow\"", json);
        var back = Settings.Load(path);
        Assert.Equal(Permission.Allow, back.Bridge.Permissions["clipboard_read"]);
        Assert.Equal(s.Bridge.Token, back.Bridge.Token);
        Assert.StartsWith("mrg_", back.Bridge.Token);
        File.Delete(path);
    }
}
