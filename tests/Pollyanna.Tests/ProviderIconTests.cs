using System.Net;
using Pollyanna.Tests.Support;

namespace Pollyanna.Tests;

/// <summary>
/// Provider logos: the operator's image file sits beside the label on the sign-in buttons and is
/// served by Pollyanna itself to signed-out browsers (spec §2, §3).
/// </summary>
public sealed class ProviderIconTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() =>
        app = await TestApp.StartAsync(providers: [new("google", "Google", Icon: TestImages.Svg), new("work", "Work account")]);
    public async Task DisposeAsync() => await app.DisposeAsync();

    [Fact]
    public async Task The_chooser_shows_a_providers_icon_beside_its_label_and_others_stay_text_only()
    {
        using var browser = app.NewBrowser();

        var page = await browser.GetPageAsync("/auth/login");

        var buttons = page.QuerySelectorAll(".actions a.button").ToDictionary(a => a.TextContent.Trim());
        var icon = Assert.Single(buttons["Sign in with Google"].QuerySelectorAll("img"));
        Assert.StartsWith("/auth/icon/google?v=", icon.GetAttribute("src"));
        // The label says who the provider is, so the logo is decoration.
        Assert.Equal("", icon.GetAttribute("alt"));
        Assert.Empty(buttons["Sign in with Work account"].QuerySelectorAll("img"));
        Assert.Empty(buttons["Sign in with issuer.test"].QuerySelectorAll("img"));
    }

    [Fact]
    public async Task The_account_pages_add_buttons_show_the_icon_too()
    {
        var (browser, _) = await app.SignInAsync(new("alice", "Alice", "alice@example.com", true, null));
        using var _b = browser;

        var page = await browser.GetPageAsync("/account");

        var buttons = page.QuerySelectorAll("#logins form[action='/account/addlogin'] button").ToDictionary(b => b.TextContent.Trim());
        Assert.StartsWith("/auth/icon/google?v=", Assert.Single(buttons["Add Google"].QuerySelectorAll("img")).GetAttribute("src"));
        Assert.Contains("button-outline", buttons["Add Google"].ClassList);
        Assert.Empty(buttons["Add Work account"].QuerySelectorAll("img"));
    }

    [Fact]
    public async Task The_icon_is_served_to_a_signed_out_browser_with_its_content_type_and_nosniff()
    {
        using var browser = app.NewBrowser();
        var src = (await browser.GetPageAsync("/auth/login")).QuerySelector(".actions img")!.GetAttribute("src")!;

        var response = await browser.GetAsync(src);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal(TestImages.Svg, await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("/auth/icon/work")]
    [InlineData("/auth/icon/nobody")]
    public async Task A_provider_without_an_icon_has_none_to_serve(string path)
    {
        using var browser = app.NewBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(path)).StatusCode);
    }
}
