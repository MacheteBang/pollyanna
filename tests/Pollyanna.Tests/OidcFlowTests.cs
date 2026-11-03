using System.Net;
using Pollyanna.Auth;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Tests;

/// <summary>The sign-in flow end to end against the in-process mock issuer (spec §3, §9).</summary>
public sealed class OidcFlowTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    static readonly MockIdentity Alice = new("alice-1", "Alice Example", "alice@example.com", true, null);

    [Fact]
    public async Task Signed_out_request_is_sent_to_login_with_the_page_it_asked_for()
    {
        using var browser = app.NewBrowser();
        var response = await browser.GetAsync("/account?tab=x");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/login?returnUrl=%2Faccount%3Ftab%3Dx", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Authorization_request_uses_code_flow_with_pkce_and_random_state_and_nonce()
    {
        using var browser = app.NewBrowser();
        var response = await browser.GetAsync("/auth/login");
        var location = response.Headers.Location!;
        var q = QueryHelpers.ParseQuery(location.Query);

        Assert.Equal(TestApp.Issuer + "/authorize", location.GetLeftPart(UriPartial.Path));
        Assert.Equal("code", q["response_type"]);
        Assert.Equal(TestApp.ClientId, q["client_id"]);
        Assert.Equal(TestApp.PublicUrl + "/auth/callback", q["redirect_uri"]);
        Assert.Equal(["email", "openid", "profile"], q["scope"].ToString().Split(' ').Order());
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(43, q["code_challenge"].ToString().Length); // base64url SHA-256
        // At least 128 bits of randomness: 22+ base64url characters.
        Assert.True(q["state"].ToString().Length >= 22);
        Assert.True(q["nonce"].ToString().Length >= 22);

        // A second login gets fresh values.
        using var other = app.NewBrowser();
        var q2 = QueryHelpers.ParseQuery((await other.GetAsync("/auth/login")).Headers.Location!.Query);
        Assert.NotEqual(q["state"], q2["state"]);
        Assert.NotEqual(q["nonce"], q2["nonce"]);
        Assert.NotEqual(q["code_challenge"], q2["code_challenge"]);
    }

    [Fact]
    public async Task Sign_in_returns_to_the_page_first_asked_for_with_a_session_cookie()
    {
        using var browser = app.NewBrowser();
        var callback = await browser.SignInAsync(Alice, "/account");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/account", callback.Headers.Location!.OriginalString);

        var cookie = Assert.Single(callback.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-pollyanna="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        var token = cookie.Split(';')[0]["__Host-pollyanna=".Length..];
        Assert.Equal(32, WebEncoders.Base64UrlDecode(token).Length); // 256 bits

        var page = await browser.GetPageAsync("/account");
        Assert.Contains("Alice Example", page.Body!.TextContent);
        Assert.Contains("alice@example.com", page.Body!.TextContent);

        // Only a hash of the token is stored.
        var stored = await app.WithDbAsync(db => db.Sessions.SingleAsync());
        Assert.NotEqual(token, stored.Id);
        Assert.Equal(SessionAuthenticationHandler.HashToken(token), stored.Id);
        Assert.Equal(TimeSpan.FromDays(30), stored.ExpiresAt - stored.CreatedAt);
    }

    [Fact]
    public async Task First_sign_in_creates_a_person_keyed_by_issuer_and_sub()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(Alice);

        var person = await app.WithDbAsync(db => db.People.SingleAsync());
        Assert.Equal(TestApp.Issuer, person.OidcIssuer);
        Assert.Equal("alice-1", person.OidcSub);
        Assert.Equal("Alice Example", person.DisplayName);
        Assert.Equal("alice@example.com", person.Email);
        Assert.True(person.EmailVerified);
        Assert.NotNull(person.LastLoginAt);
    }

    [Fact]
    public async Task Each_sign_in_refreshes_name_and_email_on_the_same_person()
    {
        using (var first = app.NewBrowser())
            await first.SignInAsync(Alice);
        var id = await app.WithDbAsync(db => db.People.Select(p => p.Id).SingleAsync());

        using var second = app.NewBrowser();
        await second.SignInAsync(Alice with { Name = "Alice Renamed", Email = "alice.new@example.com", EmailVerified = null });

        var person = await app.WithDbAsync(db => db.People.SingleAsync());
        Assert.Equal(id, person.Id);
        Assert.Equal("Alice Renamed", person.DisplayName);
        Assert.Equal("alice.new@example.com", person.Email);
        Assert.Null(person.EmailVerified);
    }

    [Fact]
    public async Task Returning_after_sign_in_skips_the_provider()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(Alice);
        var response = await browser.GetAsync("/auth/login?returnUrl=%2Faccount");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account", response.Headers.Location!.OriginalString);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task Return_url_must_be_local(string returnUrl)
    {
        using var browser = app.NewBrowser();
        var toIssuer = await browser.GetAsync("/auth/login?returnUrl=" + Uri.EscapeDataString(returnUrl));
        var callback = await browser.CompleteAtIssuerAsync(toIssuer, Alice);
        Assert.Equal("/", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Cancelling_at_the_provider_shows_the_failure_page_and_signs_nobody_in()
    {
        using var browser = app.NewBrowser();
        var toIssuer = await browser.GetAsync("/auth/login");
        var callback = await browser.CompleteAtIssuerAsync(toIssuer, Alice, action: "cancel");

        Assert.Equal("/auth/failed", callback.Headers.Location!.OriginalString);
        var failed = await browser.GetAsync("/auth/failed");
        Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.People.CountAsync()));
    }

    [Fact]
    public async Task A_callback_from_another_browser_is_rejected()
    {
        // The state is bound to the browser that started the flow by its correlation cookie.
        using var victim = app.NewBrowser();
        using var attacker = app.NewBrowser();
        var toIssuer = await attacker.GetAsync("/auth/login");
        var callbackUrl = await attacker.AuthorizeAtIssuerAsync(toIssuer, new MockIdentity("attacker", "Mallory", null, null, null));

        var callback = await victim.App.GetAsync(callbackUrl.PathAndQuery);

        Assert.Equal("/auth/failed", callback.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await victim.GetAsync("/")).StatusCode);
        Assert.Equal(0, await app.WithDbAsync(db => db.People.CountAsync()));
    }

    [Fact]
    public async Task Sign_out_needs_the_antiforgery_token_then_ends_the_session()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(Alice);

        var forged = await browser.App.PostAsync("/auth/signout", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(1, await app.WithDbAsync(db => db.Sessions.CountAsync()));

        var account = await browser.GetPageAsync("/account");
        var response = await browser.SubmitAsync(account, "/auth/signout");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/auth/signed-out", response.Headers.Location!.OriginalString);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-pollyanna=;"));

        Assert.Equal(0, await app.WithDbAsync(db => db.Sessions.CountAsync()));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/auth/signed-out")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Session_slides_forward_with_use_and_expires_after_30_idle_days()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(Alice);

        app.Clock.Advance(TimeSpan.FromDays(20));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);
        app.Clock.Advance(TimeSpan.FromDays(20));
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/")).StatusCode);

        app.Clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromMinutes(1));
        var response = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/auth/login", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_made_up_session_cookie_is_not_accepted()
    {
        using var browser = app.NewBrowser();
        await browser.SignInAsync(Alice);
        using var stranger = app.NewBrowser();
        stranger.App.DefaultRequestHeaders.Add("Cookie", "__Host-pollyanna=" + WebEncoders.Base64UrlEncode(new byte[32]));
        Assert.Equal(HttpStatusCode.Redirect, (await stranger.GetAsync("/")).StatusCode);
    }
}
