using System.Net;
using Pollyanna.Configuration;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pollyanna.Tests;

/// <summary>
/// A misconfigured client or an unreachable provider ends on the sign-in failure page with a log
/// line naming the provider's error, never the general error page (spec §3, issue #4).
/// </summary>
public sealed class SignInFailureTests
{
    static readonly MockIdentity Alice = new("alice-1", "Alice Example", "alice@example.com", true, null);
    const string ProviderPage = "/auth/failed?reason=provider";
    const string WrongSecret = "not-the-secret";

    static Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder> Setting(string key, string value) => b => b.UseSetting(key, value);

    static async Task AssertProviderFailurePageAsync(TestBrowser browser)
    {
        var page = await browser.GetPageAsync(ProviderPage);
        Assert.Contains("Sign-in didn't work", page.Body!.TextContent);
        Assert.Contains("Whoever runs this site needs to check its settings", page.Body!.TextContent);
    }

    static void AssertLogged(TestApp app, string text)
    {
        Assert.Contains(app.Logs.Entries, e => e.Category == "Pollyanna.Auth" && e.Message.Contains(text));
        Assert.DoesNotContain(app.Logs.Entries, e => e.Message.Contains(TestApp.ClientSecret) || e.Message.Contains(WrongSecret));
    }

    [Fact]
    public async Task Sign_in_works_through_pushed_authorization_requests()
    {
        await using var app = await TestApp.StartAsync(pushedAuthorization: true);
        using var browser = app.NewBrowser();

        var callback = await browser.SignInAsync(Alice, "/account");

        Assert.Contains("request_uri", QueryHelpers.ParseQuery(browser.LastAuthorizeRequest!.Query).Keys);
        Assert.Equal("/account", callback.Headers.Location!.OriginalString);
        Assert.Equal(1, await app.WithDbAsync(db => db.People.CountAsync()));
    }

    [Theory]
    [InlineData(PollyannaSettings.ClientSecretKey, WrongSecret)]
    [InlineData(PollyannaSettings.ClientIdKey, "nobody-registered-this")]
    public async Task A_rejected_pushed_request_shows_the_failure_page_and_logs_invalid_client(string key, string value)
    {
        await using var app = await TestApp.StartAsync(pushedAuthorization: true, configure: Setting(key, value));
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync("/auth/login");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(ProviderPage, response.Headers.Location!.OriginalString);
        await AssertProviderFailurePageAsync(browser);
        AssertLogged(app, "Sign-in could not start: the provider rejected the client (invalid_client). Check POLLYANNA_OIDC_CLIENT_ID");
    }

    [Fact]
    public async Task An_unreachable_provider_shows_the_failure_page_and_names_the_issuer_setting()
    {
        await using var app = await TestApp.StartAsync(configure: b => b.ConfigureTestServices(services =>
            services.Configure<OpenIdConnectOptions>("oidc", o => o.BackchannelHttpHandler = new Unreachable())));
        using var browser = app.NewBrowser();

        var response = await browser.GetAsync("/auth/login");

        Assert.Equal(ProviderPage, response.Headers.Location!.OriginalString);
        await AssertProviderFailurePageAsync(browser);
        AssertLogged(app, $"couldn't be loaded from {TestApp.Issuer}");
        AssertLogged(app, "Check POLLYANNA_OIDC_ISSUER");
    }

    [Fact]
    public async Task A_wrong_secret_at_the_token_endpoint_logs_the_providers_error()
    {
        await using var app = await TestApp.StartAsync(configure: Setting(PollyannaSettings.ClientSecretKey, WrongSecret));
        using var browser = app.NewBrowser();

        var callback = await browser.SignInAsync(Alice);

        Assert.Equal(ProviderPage, callback.Headers.Location!.OriginalString);
        AssertLogged(app, "Sign-in failed: invalid_client. Check POLLYANNA_OIDC_CLIENT_ID");
        Assert.Equal(0, await app.WithDbAsync(db => db.People.CountAsync()));
    }

    [Fact]
    public async Task Cancelling_at_the_provider_is_not_a_settings_problem()
    {
        await using var app = await TestApp.StartAsync();
        using var browser = app.NewBrowser();

        var callback = await browser.CompleteAtIssuerAsync(await browser.GetAsync("/auth/login"), Alice, action: "cancel");

        Assert.Equal("/auth/failed", callback.Headers.Location!.OriginalString);
        var page = await browser.GetPageAsync("/auth/failed");
        Assert.DoesNotContain("check its settings", page.Body!.TextContent);
    }

    sealed class Unreachable : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Name or service not known");
    }
}
