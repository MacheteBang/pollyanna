using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The event page once the event is over, in the "Tablecloth" identity (spec §7 Event page, once
/// the event is over; §4 sealed archives; §12 no cloth on an event that is over).
/// </summary>
public sealed class EventOverTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob, carol;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        ann = await app.PersonAsync("Ann");
        bob = await app.PersonAsync("Bob");
        carol = await app.PersonAsync("Carol");
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    static string Text(IElement? e) => Regex.Replace(e?.TextContent ?? "", @"\s+", " ").Trim();

    /// <summary>Pat takes part; Pat → Ann → Bob → Carol → Pat. Ends at <paramref name="status"/>, revealed on the way or not.</summary>
    async Task<Guid> OverAsync(EventStatus status, bool revealed, Guid[]? organizers = null)
    {
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, organizers ?? [org], [me, ann, bob, carol]);
        await app.SealAsync(ev, Loop(me, ann, bob, carol));
        await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, status).SetProperty(e => e.PairingsRevealed, revealed)));
        return ev;
    }

    static List<string> WhoDrewWhom(IHtmlDocument page) =>
        [.. page.QuerySelectorAll("#pairings .who-drew-whom > li").Select(li => Text(li.Children.Last()))];

    [Fact]
    public async Task Once_revealed_the_page_opens_on_the_viewers_pairing_then_who_drew_whom_with_no_cloth()
    {
        var ev = await OverAsync(EventStatus.Revealed, revealed: true);

        var page = await browser.GetPageAsync($"/events/{ev}");

        Assert.Equal("Dec 24, 2026 · $25 · everyone's been revealed", Text(page.QuerySelector(".page-head-line")));
        Assert.Empty(page.QuerySelectorAll(".cloth, .panel"));
        Assert.Empty(page.QuerySelectorAll(".button-fill, .stamp"));

        // First, the viewer's own pairing; then the full list, which names everyone.
        var mine = page.QuerySelector("#slip")!;
        Assert.Equal(["You gave to Ann", "Carol gave to you"], mine.QuerySelectorAll(".over-line").Select(Text));
        Assert.Equal("Who drew whom", Text(page.QuerySelector("#pairings h2")));
        Assert.Equal(["Ann gave to Bob", "Bob gave to Carol", "Carol gave to you", "You gave to Ann"], WhoDrewWhom(page));
        Assert.True(mine.CompareDocumentPosition(page.QuerySelector("#pairings")!).HasFlag(DocumentPositions.Following));

        // No separate people list, and nothing to leave.
        Assert.Null(page.QuerySelector("#people-heading"));
        Assert.Null(page.QuerySelector(".people"));
        Assert.Null(page.QuerySelector($"a[href='/events/{ev}/leave']"));
    }
}
