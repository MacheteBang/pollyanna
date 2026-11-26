using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.People;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Pollyanna.Tests;

/// <summary>Profile pictures: fetch rules and who may load them (spec §3).</summary>
public sealed class PictureTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    static PictureFetcher Fetcher(StubHttpHandler stub) =>
        new(new HttpClient(stub), NullLogger<PictureFetcher>.Instance) { Timeout = TimeSpan.FromMilliseconds(200) };

    [Theory]
    [InlineData("png", "image/png")]
    [InlineData("jpeg", "image/jpeg")]
    [InlineData("gif", "image/gif")]
    [InlineData("webp", "image/webp")]
    public async Task Accepts_the_four_image_types_by_their_bytes(string kind, string expected)
    {
        byte[] body = kind switch
        {
            "png" => TestImages.Png,
            "jpeg" => TestImages.Jpeg,
            "gif" => "GIF89a\x01\x00"u8.ToArray(),
            _ => [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8 "u8],
        };
        var stub = new StubHttpHandler();
        stub.Serve("https://pics.test/me", body, "text/plain"); // the header is ignored; bytes decide
        var result = await Fetcher(stub).FetchAsync("https://pics.test/me");
        Assert.Equal(expected, result!.ContentType);
        Assert.Equal(body, result.Data);
    }

    [Fact]
    public async Task Rejects_svg_even_when_labelled_as_an_image()
    {
        var stub = new StubHttpHandler();
        stub.Serve("https://pics.test/me.svg", TestImages.Svg, "image/svg+xml");
        Assert.Null(await Fetcher(stub).FetchAsync("https://pics.test/me.svg"));
    }

    [Fact]
    public async Task Rejects_more_than_1_MB()
    {
        var stub = new StubHttpHandler();
        stub.Serve("https://pics.test/big", [.. TestImages.Png, .. new byte[PictureFetcher.MaxBytes]]);
        Assert.Null(await Fetcher(stub).FetchAsync("https://pics.test/big"));

        stub.Serve("https://pics.test/fits", [.. TestImages.Png, .. new byte[PictureFetcher.MaxBytes - TestImages.Png.Length]]);
        Assert.NotNull(await Fetcher(stub).FetchAsync("https://pics.test/fits"));
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://pics.test/me.png")]
    [InlineData("data:image/png;base64,iVBORw0KGgo=")]
    [InlineData("not a url")]
    public async Task Only_http_and_https_are_fetched(string url)
    {
        var stub = new StubHttpHandler();
        Assert.Null(await Fetcher(stub).FetchAsync(url));
        Assert.Empty(stub.Requested);
    }

    [Fact]
    public async Task Gives_up_after_the_timeout()
    {
        var stub = new StubHttpHandler { Delay = TimeSpan.FromSeconds(10) };
        stub.Serve("https://pics.test/slow", TestImages.Png);
        Assert.Null(await Fetcher(stub).FetchAsync("https://pics.test/slow"));
    }

    [Fact]
    public async Task Fetch_timeout_is_five_seconds_by_default() =>
        Assert.Equal(TimeSpan.FromSeconds(5), new PictureFetcher(new HttpClient(), NullLogger<PictureFetcher>.Instance).Timeout);

    async Task<(TestBrowser Browser, Guid Id)> SignInAsync(string sub, string? picture)
    {
        var browser = app.NewBrowser();
        var response = await browser.SignInAsync(new MockIdentity(sub, sub, null, null, picture));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        return (browser, await app.PersonIdAsync(TestApp.Issuer, sub));
    }

    [Fact]
    public async Task Picture_is_downloaded_at_sign_in_and_served_by_pollyanna_with_its_checked_type()
    {
        app.Pictures.Serve("https://pics.test/ann.png", TestImages.Png, "application/octet-stream");
        var (browser, ann) = await SignInAsync("ann", "https://pics.test/ann.png");
        using var _b = browser;

        var response = await browser.GetAsync($"/people/{ann}/picture");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(TestImages.Png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_failed_fetch_keeps_the_previous_picture_and_doesnt_block_sign_in()
    {
        app.Pictures.Serve("https://pics.test/ann.png", TestImages.Png);
        var (first, ann) = await SignInAsync("ann", "https://pics.test/ann.png");
        first.Dispose();

        app.Pictures.Serve("https://pics.test/ann.png", TestImages.Svg, "image/svg+xml"); // now broken
        var (second, _) = await SignInAsync("ann", "https://pics.test/ann.png");
        using var _b = second;

        var response = await second.GetAsync($"/people/{ann}/picture");
        Assert.Equal(TestImages.Png, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_sign_in_without_a_picture_keeps_the_stored_one()
    {
        app.Pictures.Serve("https://pics.test/ann.png", TestImages.Png);
        var (first, ann) = await SignInAsync("ann", "https://pics.test/ann.png");
        first.Dispose();
        var (second, _) = await SignInAsync("ann", null);
        using var _b = second;

        var response = await second.GetAsync($"/people/{ann}/picture");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TestImages.Png, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("https://pics.test/ann.png", await app.WithDbAsync(db => db.People.Where(p => p.Id == ann).Select(p => p.PictureUrl).SingleAsync()));
    }

    [Fact]
    public async Task A_sign_in_without_a_name_or_email_keeps_the_stored_ones()
    {
        using (var first = app.NewBrowser())
            await first.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        using (var second = app.NewBrowser())
            await second.SignInAsync(new MockIdentity("ann", null, null, null, null));

        var ann = await app.PersonIdAsync(TestApp.Issuer, "ann");
        var person = await app.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.Id == ann));
        Assert.Equal(("Ann", "ann@example.com", "ann@example.com", true),
            (person.DisplayName, person.Email, person.EmailNormalized, person.EmailVerified));
        var login = await app.WithDbAsync(db => db.Logins.AsNoTracking().SingleAsync(l => l.PersonId == ann));
        Assert.Equal("ann@example.com", login.LastEmail);
    }

    [Fact]
    public async Task Each_sign_in_takes_the_values_just_received()
    {
        app.Pictures.Serve("https://pics.test/ann.png", TestImages.Png);
        app.Pictures.Serve("https://pics.test/ann-2.jpg", TestImages.Jpeg);
        using (var first = app.NewBrowser())
            await first.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, "https://pics.test/ann.png"));
        using (var second = app.NewBrowser())
            await second.SignInAsync(new MockIdentity("ann", "Annie", "Annie@Example.com", true, "https://pics.test/ann-2.jpg"));

        var ann = await app.PersonIdAsync(TestApp.Issuer, "ann");
        var person = await app.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.Id == ann));
        Assert.Equal(("Annie", "Annie@Example.com", "annie@example.com"), (person.DisplayName, person.Email, person.EmailNormalized));
        Assert.Equal("Annie@Example.com", (await app.WithDbAsync(db => db.Logins.AsNoTracking().SingleAsync(l => l.PersonId == ann))).LastEmail);
        Assert.Equal(TestImages.Jpeg, await app.WithDbAsync(db => db.PersonPictures.Where(p => p.PersonId == ann).Select(p => p.Data).SingleAsync()));
    }

    [Fact]
    public async Task Only_people_sharing_an_event_can_load_a_picture()
    {
        app.Pictures.Serve("https://pics.test/ann.png", TestImages.Png);
        var (annBrowser, ann) = await SignInAsync("ann", "https://pics.test/ann.png");
        annBrowser.Dispose();
        var (bob, bobId) = await SignInAsync("bob", null);
        using var _b = bob;

        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/people/{ann}/picture")).StatusCode);

        var ev = new Event { Name = "Shared", ExchangeDate = new DateOnly(2026, 12, 24), Status = EventStatus.Open, CreatedBy = bobId, CreatedAt = DateTime.UtcNow };
        ev.Organizers.Add(new Organizer { PersonId = bobId });
        ev.Participants.Add(new Participant { PersonId = ann, JoinedAt = DateTime.UtcNow });
        await app.WithDbAsync(async db => { db.Events.Add(ev); await db.SaveChangesAsync(); });

        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/people/{ann}/picture")).StatusCode);
    }

    [Fact]
    public async Task Pictures_need_sign_in()
    {
        using var browser = app.NewBrowser();
        var response = await browser.GetAsync($"/people/{Guid.NewGuid()}/picture");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
