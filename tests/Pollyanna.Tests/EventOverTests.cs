using System.Security.Cryptography;
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

    [Fact]
    public async Task Lost_pairings_are_listed_without_their_giver_with_one_sentence()
    {
        var ev = await OverAsync(EventStatus.Revealed, revealed: true);
        await app.UpdateAssignmentAsync(ev, bob, a => a.GiverTag = RandomNumberGenerator.GetBytes(32));

        var page = await browser.GetPageAsync($"/events/{ev}");

        Assert.Equal("Someone gave to Carol", WhoDrewWhom(page)[^1]);
        Assert.Equal("Some pairings can't be read any more, so their giver isn't shown.", Text(page.QuerySelector("#pairings p")));
    }

    [Fact]
    public async Task The_old_threads_and_the_notes_follow_collapsed_read_only_and_named()
    {
        var ev = await OverAsync(EventStatus.Revealed, revealed: true);
        await app.MessageAsync(ev, me, MessageSender.Giver, "Socks or gloves?");
        await app.MessageAsync(ev, me, MessageSender.Recipient, "Gloves, please.");
        await app.MessageAsync(ev, carol, MessageSender.Giver, "What's your shoe size?");
        await app.MessageAsync(ev, ann, MessageSender.Giver, "Ann asks Bob privately");

        var page = await browser.GetPageAsync($"/events/{ev}");

        var folds = page.QuerySelectorAll("details.fold").ToList();
        Assert.Equal(["ask", "questions", "your-notes"], folds.Select(f => f.Id));
        Assert.All(folds, f => Assert.False(f.HasAttribute("open")));
        Assert.True(page.QuerySelector("#pairings")!.CompareDocumentPosition(folds[0]).HasFlag(DocumentPositions.Following));

        Assert.Equal("You asked Ann", Text(page.QuerySelector("#ask-heading")));
        Assert.Equal(["Socks or gloves?", "Gloves, please."], page.QuerySelectorAll("#ask .bubble-body").Select(Text));
        Assert.Equal("Ann:", Text(page.QuerySelector("#ask .message-theirs .visually-hidden")));
        Assert.Equal("Carol asked you", Text(page.QuerySelector("#questions-heading")));
        Assert.Equal("Carol:", Text(page.QuerySelector("#questions .message-theirs .visually-hidden")));
        Assert.DoesNotContain("Ann asks Bob privately", page.Source.Text);
        Assert.Null(page.QuerySelector("textarea[name='body']"));
        Assert.Null(page.QuerySelector(".message-new"));

        // While revealed the notes can still change; the editor sits in its fold, outlined.
        var notes = page.QuerySelector("#your-notes")!;
        Assert.Equal("Your notes", Text(notes.QuerySelector("#notes-heading")));
        Assert.NotNull(notes.QuerySelector("form textarea#notes"));
        Assert.Empty(page.QuerySelectorAll(".button-fill"));
    }

    [Fact]
    public async Task Archived_after_the_reveal_it_says_over_and_keeps_the_list_with_notes_read_only()
    {
        var ev = await OverAsync(EventStatus.Archived, revealed: true);
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == me)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "Wool socks")));

        var page = await browser.GetPageAsync($"/events/{ev}");

        Assert.Equal("Dec 24, 2026 · $25 · over", Text(page.QuerySelector(".page-head-line")));
        Assert.Empty(page.QuerySelectorAll(".cloth, .stamp"));
        Assert.Equal(["You gave to Ann", "Carol gave to you"], page.QuerySelectorAll("#slip .over-line").Select(Text));
        Assert.Equal(4, WhoDrewWhom(page).Count);
        Assert.Null(page.QuerySelector("#your-notes textarea"));
        Assert.Equal("Wool socks", Text(page.QuerySelector("#your-notes .notes")));
    }

    [Fact]
    public async Task Archived_straight_from_drawn_it_shows_only_who_the_viewer_gave_to_and_no_list()
    {
        var ev = await OverAsync(EventStatus.Archived, revealed: false);
        await app.MessageAsync(ev, carol, MessageSender.Giver, "What's your shoe size?");

        var page = await browser.GetPageAsync($"/events/{ev}");

        Assert.Equal(["You gave to Ann"], page.QuerySelectorAll("#slip .over-line").Select(Text));
        Assert.Equal("The pairings were never revealed, so nobody else's is shown.", Text(page.QuerySelector("#slip .small")));
        Assert.Null(page.QuerySelector("#pairings"));
        Assert.Null(page.QuerySelector(".who-drew-whom"));
        Assert.Null(page.QuerySelector(".people"));
        // The kept thread with the viewer's giver doesn't name them.
        Assert.Equal("Your giver asked you", Text(page.QuerySelector("#questions-heading")));
        Assert.Equal("Your giver:", Text(page.QuerySelector("#questions .message-theirs .visually-hidden")));
        Assert.DoesNotContain("Carol", Text(page.QuerySelector("main")));
    }

    [Fact]
    public async Task When_the_viewers_own_pairing_cant_be_read_it_says_so()
    {
        var ev = await OverAsync(EventStatus.Archived, revealed: false);
        await app.UpdateAssignmentAsync(ev, me, a => a.GiverTag = RandomNumberGenerator.GetBytes(32));

        var page = await browser.GetPageAsync($"/events/{ev}");

        Assert.Empty(page.QuerySelectorAll("#slip .over-line"));
        Assert.Contains("can't be read any more", Text(page.QuerySelector("#slip")));
    }

    [Fact]
    public async Task An_organizer_who_takes_part_gets_the_switch_and_the_page_is_in_tablecloth()
    {
        var ev = await OverAsync(EventStatus.Revealed, revealed: true, organizers: [me]);

        var page = await browser.GetPageAsync($"/events/{ev}");

        Assert.NotNull(page.QuerySelector(".page-head nav.switch a[aria-current='page']"));
        Assert.Empty(page.QuerySelectorAll("[style], style"));
        var hexes = Regex.Matches(page.DocumentElement.OuterHtml, "#[0-9A-Fa-f]{6}\\b").Select(m => m.Value.ToUpperInvariant()).Distinct();
        Assert.All(hexes, h => Assert.Contains(h, Palette));
    }

    static readonly string[] Palette = ["#FFFFFF", "#1E1A16", "#5B5650", "#3F6E8C", "#DCE7EE", "#9E2019"];
}
