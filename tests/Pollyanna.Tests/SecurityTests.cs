using System.Net;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>Authentication everywhere, headers and logging (spec §3, §8).</summary>
public sealed class SecurityTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    [Theory]
    [InlineData("/")]
    [InlineData("/account")]
    [InlineData("/events/new")]
    [InlineData("/past")]
    public async Task Pages_require_sign_in(string path)
    {
        using var browser = app.NewBrowser();
        var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/auth/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Healthz_is_open_and_says_nothing_else()
    {
        using var browser = app.NewBrowser();
        var response = await browser.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/auth/signed-out")]
    [InlineData("/")]
    [InlineData("/css/site.css")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        using var browser = app.NewBrowser();
        var response = await browser.GetAsync(path);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("default-src 'self'", csp);
        Assert.DoesNotContain("unsafe", csp);
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Theory]
    [InlineData("/css/site.css", "text/css")]
    [InlineData("/fonts/courier-prime-latin-400-normal.woff2", "font/woff2")]
    [InlineData("/fonts/reenie-beanie-latin-400-normal.woff2", "font/woff2")]
    [InlineData("/favicon.svg", "image/svg+xml")]
    [InlineData("/apple-touch-icon.png", "image/png")]
    [InlineData("/js/site.js", "text/javascript")]
    public async Task Assets_are_served_by_the_app_itself(string path, string type)
    {
        using var browser = app.NewBrowser();
        var response = await browser.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Pages_load_nothing_from_other_origins_and_use_no_inline_styles_or_scripts()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(new MockIdentity("a", "A", "a@example.com", true, null));
        var me = await app.WithDbAsync(db => db.People.Select(p => p.Id).SingleAsync());
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings", Pollyanna.Data.EventStatus.Open, [me], [me, carol]);
        await app.EventAsync("Last year", Pollyanna.Data.EventStatus.Archived, [me], [me, carol]);

        foreach (var path in new[] { "/", "/account", "/auth/signed-out", "/auth/failed", "/events/new", $"/events/{ev}", $"/events/{ev}/manage", $"/events/{ev}/leave" })
        {
            var page = await browser.GetPageAsync(path);
            foreach (var el in page.QuerySelectorAll("[src], link[href]"))
            {
                var url = el.GetAttribute("src") ?? el.GetAttribute("href")!;
                Assert.True(url.StartsWith('/') && !url.StartsWith("//"), $"{path} loads {url}");
            }
            Assert.Empty(page.QuerySelectorAll("[style], style"));
            // The one script is the app's own file; nothing inline.
            Assert.All(page.QuerySelectorAll("script"), s =>
            {
                Assert.StartsWith("/js/site.js", s.GetAttribute("src"));
                Assert.Equal("", s.TextContent);
            });
            Assert.DoesNotContain(page.QuerySelectorAll("*"), e => e.Attributes.Any(a => a.Name.StartsWith("on")));
        }
    }

    [Fact]
    public async Task Stylesheet_names_no_external_url_and_no_colour_outside_the_palette()
    {
        using var browser = app.NewBrowser();
        var css = await (await browser.GetAsync("/css/site.css")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("http", css);
        Assert.DoesNotContain("gradient", css);
        var hexes = System.Text.RegularExpressions.Regex.Matches(css, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
        Assert.All(hexes, h => Assert.Contains(h, new[] { "#C9A77C", "#F4EDE0", "#FFFDF8", "#1E1A16", "#B3261E", "#9E2019", "#3F6E8C" }));
    }

    [Fact]
    public async Task Access_log_records_route_patterns_not_paths()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(new MockIdentity("a", "A", null, null, null));
        var id = await app.WithDbAsync(db => db.People.Select(p => p.Id).SingleAsync());
        await browser.GetAsync($"/people/{id}/picture");

        var access = app.Logs.Entries.Where(e => e.Category == "Pollyanna.Access").Select(e => e.Message).ToList();
        Assert.Contains("GET /people/{id:guid}/picture 404", access);
        Assert.DoesNotContain(app.Logs.Entries, e => e.Message.Contains(id.ToString()));
        // The authorization code and state never reach the log either.
        Assert.DoesNotContain(app.Logs.Entries, e => e.Message.Contains("code="));
    }

    [Fact]
    public async Task Pages_with_forms_work_when_the_proxy_forwards_plain_http()
    {
        // The proxy terminates TLS (spec §2): the app sees http, the browser uses https.
        using var browser = app.NewBrowser();
        await browser.SignInAsync(new MockIdentity("a", "A", null, null, null));
        var personId = await app.WithDbAsync(db => db.People.Select(p => p.Id).SingleAsync());
        var token = "plain-http-test-token";
        await app.WithDbAsync(async db =>
        {
            db.Sessions.Add(new Pollyanna.Data.Session
            {
                Id = Pollyanna.Auth.SessionAuthenticationHandler.HashToken(token),
                PersonId = personId,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(1),
            });
            await db.SaveChangesAsync();
        });

        using var proxy = app.CreateRawClient("http://pollyanna.internal:8080");
        proxy.DefaultRequestHeaders.Add("Cookie", "__Host-pollyanna=" + token);
        var response = await proxy.GetAsync("/account");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var antiforgery = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("pollyanna-af"));
        Assert.Contains("secure", antiforgery, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Client_secret_never_reaches_the_log()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(new MockIdentity("a", "A", null, null, null));
        Assert.DoesNotContain(app.Logs.Entries, e => e.Message.Contains(TestApp.ClientSecret));
    }
}
