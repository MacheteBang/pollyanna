using System.Net;
using AngleSharp.Dom;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;

namespace Pollyanna.Tests;

/// <summary>
/// The error and sign-in failure pages in the "Tablecloth" identity: the girl large, a heading, one
/// or two sentences and one way out, with no cloth (spec §7 Error pages, §12 Marks).
/// </summary>
public sealed class ErrorPagesTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() =>
        app = await TestApp.StartAsync(providers: [new("google", "Google", "open", TestImages.Svg), new("work", "Work account")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    static readonly MockIdentity Ana = new("ana", "Ana Example", "ana@example.com", true, null);

    /// <summary>The lone block: its heading, its paragraphs, and its way out (label, href, outlined button).</summary>
    static (string Heading, List<string> Text, List<(string Label, string? Href, bool Button)> WayOut) Lone(IDocument page)
    {
        var lone = Assert.Single(page.QuerySelectorAll("main .lone"));
        return (
            lone.QuerySelector("h1")!.TextContent.Trim(),
            [.. lone.QuerySelectorAll("p").Select(p => p.TextContent.Trim())],
            [.. lone.QuerySelectorAll("a").Select(a => (a.TextContent.Trim(), a.GetAttribute("href"), a.ClassList.Contains("button")))]);
    }

    /// <summary>The girl stands large above the heading, and there's no cloth or filled button.</summary>
    static void AssertGirlAboveAndNoCloth(IDocument page)
    {
        var girl = Assert.Single(page.QuerySelectorAll("main svg.girl"));
        Assert.Equal("130", girl.GetAttribute("width"));
        Assert.True(girl.CompareDocumentPosition(page.QuerySelector("main h1")!).HasFlag(DocumentPositions.Following));
        Assert.Empty(page.QuerySelectorAll(".cloth, .panel"));
    }

    [Fact]
    public async Task A_missing_page_is_a_404_that_says_it_may_have_moved_and_leads_back_to_your_events()
    {
        var (browser, _) = await app.SignInAsync(Ana);
        using var _b = browser;

        var response = await browser.GetAsync("/no/such/page");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var page = await TestBrowser.ParseAsync(response);
        var (heading, text, wayOut) = Lone(page);
        Assert.Equal("We can't find that page", heading);
        Assert.Equal(["It may have moved, or the event may have been canceled."], text);
        Assert.Equal([("Back to your events", "/", false)], wayOut);
        AssertGirlAboveAndNoCloth(page);
        // Signed in, so the masthead names them.
        Assert.Contains(page.QuerySelectorAll("header.masthead a"), a => a.GetAttribute("href") == "/account");
    }

    [Fact]
    public async Task A_server_error_says_its_on_Pollyanna_and_leads_back_to_your_events()
    {
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync("/error/500");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var page = await TestBrowser.ParseAsync(response);
        var (heading, text, wayOut) = Lone(page);
        Assert.Equal("Something went wrong", heading);
        Assert.Equal(["That one's on Pollyanna, not you. Nothing was changed. Try again in a minute."], text);
        Assert.Equal([("Back to your events", "/", false)], wayOut);
        AssertGirlAboveAndNoCloth(page);
    }

    [Theory]
    [InlineData("/auth/failed", false)]
    [InlineData("/auth/failed?reason=provider", true)]
    public async Task A_failed_sign_in_keeps_its_words_and_offers_every_provider_button_again(string path, bool settingsProblem)
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync(path);

        var (heading, text, wayOut) = Lone(page);
        Assert.Equal("Sign-in didn't work", heading);
        Assert.Equal("Your sign-in provider didn't let you in, or the sign-in took too long. Nothing was changed.", text[0]);
        Assert.Equal(settingsProblem, text.Contains("The sign-in service turned Pollyanna away. Whoever runs this site needs to check its settings."));
        // The buttons keep §3's "Sign in with" words, outlined, in configured order.
        Assert.Equal(
            [("Sign in with issuer.test", "/auth/login/default", true), ("Sign in with Google", "/auth/login/google", true), ("Sign in with Work account", "/auth/login/work", true)],
            wayOut);
        Assert.Empty(page.QuerySelectorAll(".button-fill"));
        Assert.StartsWith("/auth/icon/google?v=", Assert.Single(page.QuerySelectorAll(".lone .button img")).GetAttribute("src"));
        AssertGirlAboveAndNoCloth(page);
        // Signed out: the masthead is the mark and wordmark only.
        Assert.Single(page.QuerySelectorAll("header.masthead a"));
    }

    [Fact]
    public async Task A_refused_sign_in_is_the_same_page_with_the_girl_and_no_cloth()
    {
        using var browser = app.NewBrowser();
        var callback = await browser.SignInAsync(Ana with { Sub = "ana-google" }, "/account", provider: "google");
        var location = callback.Headers.Location!.OriginalString;
        Assert.StartsWith("/auth/failed?refused=", location);

        var page = await browser.GetPageAsync(location);

        Assert.Equal("Pollyanna doesn't know you yet", Lone(page).Heading);
        AssertGirlAboveAndNoCloth(page);
    }

    [Theory]
    [InlineData("/auth/login")]
    [InlineData("/auth/signed-out")]
    [InlineData("/auth/deleted")]
    public async Task The_girl_is_large_only_on_error_pages(string path)
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync(path);

        Assert.All(page.QuerySelectorAll("svg.girl"), g => Assert.Equal("40", g.GetAttribute("width")));
    }
}
