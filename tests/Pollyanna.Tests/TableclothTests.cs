using System.Net;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Pollyanna.Tests.Support;

namespace Pollyanna.Tests;

/// <summary>
/// The "Tablecloth" identity's shell and the signed-out pages that use it first: the masthead,
/// one white panel on the gingham band, the short about text, the buttons, the fonts and icons
/// the app serves, and nothing outside the six colors (spec §7, §12).
/// </summary>
public sealed class TableclothTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() =>
        app = await TestApp.StartAsync(providers: [new("google", "Google", Icon: TestImages.Svg), new("work", "Work account")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    const string ShortAbout = "Pollyanna is your family's gift exchange: everyone draws one name and nobody sees who drew whom. Around Philadelphia, that's what they call it.";

    static readonly string[] Palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];

    /// <summary>The one white panel on the cloth: its heading, its paragraphs and its buttons (label, filled).</summary>
    static (string Heading, List<string> Text, List<(string Label, bool Filled)> Buttons) Panel(IDocument page)
    {
        var panel = Assert.Single(page.QuerySelectorAll(".cloth > .panel"));
        return (
            panel.QuerySelector("h1")!.TextContent.Trim(),
            [.. panel.QuerySelectorAll("p").Select(p => p.TextContent.Trim())],
            [.. panel.QuerySelectorAll(".button").Select(b => (b.TextContent.Trim(), b.ClassList.Contains("button-fill")))]);
    }

    [Fact]
    public async Task The_signed_out_page_is_one_panel_on_the_cloth_with_the_short_about_text_above_sign_in_again()
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync("/auth/signed-out");

        var (heading, text, buttons) = Panel(page);
        Assert.Equal("You're signed out", heading);
        Assert.Equal(ShortAbout, text[0]);
        Assert.Equal([("Sign in again", true)], buttons);
        Assert.Equal("/auth/login", page.QuerySelector(".panel a.button")!.GetAttribute("href"));
    }

    [Fact]
    public async Task The_account_deleted_page_is_the_same_panel()
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync("/auth/deleted");

        var (heading, text, buttons) = Panel(page);
        Assert.Equal("Your account is deleted", heading);
        Assert.Equal(ShortAbout, text[0]);
        Assert.Equal([("Sign in again", true)], buttons);
        Assert.Equal("/auth/login", page.QuerySelector(".panel a.button")!.GetAttribute("href"));
        // What deleting did stays said, under the button.
        Assert.Contains("signing in again starts a new, empty Pollyanna account.", text[^1]);
    }

    [Fact]
    public async Task The_chooser_says_what_Pollyanna_is_then_outlines_every_provider_button_with_its_logo()
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync("/auth/login");

        var (heading, text, buttons) = Panel(page);
        Assert.Equal("Sign in to Pollyanna", heading);
        Assert.Equal([ShortAbout], text);
        // Several providers: none is the main one, so none is filled.
        Assert.Equal([("Continue with issuer.test", false), ("Continue with Google", false), ("Continue with Work account", false)], buttons);
        Assert.StartsWith("/auth/icon/google?v=", Assert.Single(page.QuerySelectorAll(".panel .button img")).GetAttribute("src"));
        // The about text comes before the buttons.
        var about = page.QuerySelector(".panel p")!;
        var first = page.QuerySelector(".panel .button")!;
        Assert.True(about.CompareDocumentPosition(first).HasFlag(DocumentPositions.Following));
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/signed-out")]
    [InlineData("/auth/deleted")]
    public async Task Signed_out_pages_show_only_the_girl_and_the_lowercase_wordmark_linking_home(string path)
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync(path);

        var masthead = Assert.Single(page.QuerySelectorAll("header.masthead"));
        var link = Assert.Single(masthead.QuerySelectorAll("a"));
        Assert.Equal("/", link.GetAttribute("href"));
        Assert.Equal("pollyanna", link.TextContent.Trim());
        Assert.Equal("40", link.QuerySelector("svg.girl")!.GetAttribute("width"));
        Assert.Empty(masthead.QuerySelectorAll("form, button, nav"));
    }

    [Fact]
    public async Task For_a_signed_in_person_the_masthead_adds_an_empty_reserved_place_and_their_first_name_but_no_sign_out()
    {
        var (browser, _) = await app.SignInAsync(new("ana", "Ana María Example", "ana@example.com", true, null));
        using var _b = browser;

        var page = await browser.GetPageAsync("/auth/signed-out");

        var masthead = page.QuerySelector("header.masthead")!;
        var links = masthead.QuerySelectorAll("a").Select(a => (a.TextContent.Trim(), a.GetAttribute("href"))).ToList();
        Assert.Equal([("pollyanna", "/"), ("Ana", "/account")], links);
        var slot = Assert.Single(masthead.QuerySelectorAll(".masthead-slot"));
        Assert.Equal("", slot.InnerHtml);
        Assert.DoesNotContain("Sign out", masthead.TextContent);
        Assert.Empty(masthead.QuerySelectorAll("form, button"));
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/signed-out")]
    [InlineData("/auth/deleted")]
    public async Task Moved_pages_declare_light_only_and_load_only_the_apps_own_files(string path)
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync(path);

        Assert.Equal("light", page.QuerySelector("meta[name=color-scheme]")!.GetAttribute("content"));
        Assert.Contains(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/tablecloth.css"));
        Assert.DoesNotContain(page.QuerySelectorAll("link[rel=stylesheet]"), l => l.GetAttribute("href")!.StartsWith("/css/site.css"));
        foreach (var el in page.QuerySelectorAll("[src], [href]").Where(e => e.LocalName != "a"))
        {
            var url = el.GetAttribute("src") ?? el.GetAttribute("href")!;
            Assert.True(url.StartsWith('/') && !url.StartsWith("//"), $"{path} loads {url}");
        }
        Assert.Empty(page.QuerySelectorAll("[style], style"));
        // No color but the six, in the markup (the girl included).
        var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
        Assert.All(hexes, h => Assert.Contains(h, Palette));
    }

    [Fact]
    public async Task The_stylesheet_defines_the_six_colors_once_and_uses_no_other()
    {
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync("/css/tablecloth.css");

        Assert.Equal("text/css", response.Content.Headers.ContentType!.MediaType);
        var css = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("http", css);
        var hexes = Regex.Matches(css, "#[0-9A-Fa-f]{3,8}\\b").Select(m => m.Value.ToUpperInvariant()).ToList();
        Assert.Equal(Palette.Order(), hexes.Order());
        // The gingham's thread is ribbon blue at 50%, and no other color is written any other way.
        Assert.Equal(["rgb(63 110 140 / 0.5)"], Regex.Matches(css, @"(rgb|hsl|oklch|lab)a?\([^)]*\)").Select(m => m.Value).Distinct());
        Assert.Contains("background-size: 28px 28px", css);
        Assert.Contains("color-scheme: light", css);
    }

    [Theory]
    [InlineData("/fonts/figtree-latin-400-normal.woff2", "font/woff2")]
    [InlineData("/fonts/figtree-latin-600-normal.woff2", "font/woff2")]
    [InlineData("/fonts/figtree-latin-800-normal.woff2", "font/woff2")]
    [InlineData("/fonts/figtree-latin-ext-400-normal.woff2", "font/woff2")]
    [InlineData("/fonts/figtree-latin-ext-600-normal.woff2", "font/woff2")]
    [InlineData("/fonts/figtree-latin-ext-800-normal.woff2", "font/woff2")]
    [InlineData("/fonts/playpen-sans-latin-500-normal.woff2", "font/woff2")]
    [InlineData("/fonts/playpen-sans-latin-ext-500-normal.woff2", "font/woff2")]
    [InlineData("/fonts/Figtree-OFL.txt", "text/plain")]
    [InlineData("/fonts/PlaypenSans-OFL.txt", "text/plain")]
    public async Task The_fonts_and_their_licenses_are_served_by_the_app(string path, string type)
    {
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
        if (type == "text/plain")
            Assert.Contains("SIL OPEN FONT LICENSE Version 1.1", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_touch_icon_is_a_180px_png()
    {
        using var browser = app.NewBrowser();

        var png = await (await browser.GetAsync("/apple-touch-icon.png")).Content.ReadAsByteArrayAsync();

        // The PNG header's width and height, big-endian at bytes 16 and 20.
        Assert.Equal(180, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(180, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }
}
