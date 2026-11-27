using System.Net;
using Pollyanna.Events;
using Pollyanna.Data;
using Pollyanna.Live;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pollyanna.Tests;

/// <summary>
/// The live connection (spec §16, "The connection" under Testing): one <c>/live</c> stream per
/// open page, its headers at once, a keep-alive comment every 15 seconds, a status without a
/// session, and an end on sign-out, account deletion and session expiry.
/// </summary>
public sealed class LiveTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (pat, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        pat.Dispose();
        await app.DisposeAsync();
    }

    [Fact]
    public async Task The_stream_sends_its_headers_at_once_unbuffered_and_uncompressed()
    {
        using var live = await pat.OpenLiveAsync();

        var response = live.Response;
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(["identity"], response.Content.Headers.ContentEncoding);
        Assert.Equal(["no"], response.Headers.GetValues("X-Accel-Buffering"));
        Assert.True(response.Headers.CacheControl?.NoStore);
        await app.Live.SettleAsync();
        Assert.Empty(live.Frames);
    }

    [Fact]
    public async Task A_keep_alive_comment_comes_every_15_seconds_of_the_apps_clock()
    {
        using var response = await pat.App.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/live"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        async Task<string[]> FrameAsync()
        {
            var lines = new List<string>();
            while (await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)) is { Length: > 0 } line)
                lines.Add(line);
            return [.. lines];
        }
        Assert.Equal("event: hello", (await FrameAsync())[0]);

        app.Clock.Advance(TimeSpan.FromSeconds(14));
        app.Services.GetRequiredService<LiveConnections>().Mark("before");
        Assert.Equal(["event: settle", "data: before"], await FrameAsync());

        app.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([": keep-alive"], await FrameAsync());
        app.Clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal([": keep-alive"], await FrameAsync());
    }

    [Fact]
    public async Task Without_a_session_it_answers_with_a_status_not_the_sign_in_redirect()
    {
        using var stranger = app.NewBrowser();

        var response = await stranger.GetAsync("/live");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Signing_out_ends_that_sessions_streams_and_only_those()
    {
        using var phone = await SecondSessionAsync();
        using var phoneLive = await phone.OpenLiveAsync();
        using var live = await pat.OpenLiveAsync();

        await pat.SubmitOnAsync("/account", "/auth/signout");

        await live.EndedAsync();
        await app.Live.SettleAsync();
        Assert.False(phoneLive.Ended);
        Assert.Equal(HttpStatusCode.Unauthorized, (await pat.GetAsync("/live")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_ends_every_stream_of_the_person()
    {
        using var phone = await SecondSessionAsync();
        using var phoneLive = await phone.OpenLiveAsync();
        using var live = await pat.OpenLiveAsync();

        await pat.SubmitOnAsync("/account/delete", "/account/delete");

        await live.EndedAsync();
        await phoneLive.EndedAsync();
    }

    [Fact]
    public async Task The_stream_ends_once_the_session_expires()
    {
        using var live = await pat.OpenLiveAsync();

        app.Clock.Advance(TimeSpan.FromDays(31));

        await live.EndedAsync();
    }

    [Fact]
    public async Task A_connection_is_one_log_line_naming_nobody_with_none_per_keep_alive_or_hint()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me]);
        var before = app.Logs.Entries.Count;
        using (var live = await pat.OpenLiveAsync())
        {
            app.Clock.Advance(TimeSpan.FromSeconds(15));
            await pat.PostWithTokenAsync($"/events/{ev}/manage", $"/events/{ev}/manage/details",
                Seed.Form(("name", "Stockings 2"), ("exchangeDate", "2030-12-24")));
            await app.Live.SettleAsync();
            Assert.NotEmpty(live.Hints);
            await pat.SubmitOnAsync("/account", "/auth/signout");
            await live.EndedAsync();
        }

        await WaitUntilAsync(() => app.Logs.Entries.Any(e => e.Message == "GET /live 200"));
        Assert.Equal(["GET /live 200"], app.Logs.Entries.Skip(before).Select(e => e.Message).Where(m => m.Contains("/live")));
        Assert.DoesNotContain(app.Logs.Entries.Skip(before), e => e.Message.Contains(me.ToString()) || e.Message.Contains(ev.ToString()));
    }

    static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(20);
    }

    /// <summary>Pat signed in a second time, as on another device.</summary>
    async Task<TestBrowser> SecondSessionAsync()
    {
        var (phone, _) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        return phone;
    }
}
