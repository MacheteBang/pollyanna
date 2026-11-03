using System.Security.Cryptography;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The no-repeat rule (spec §5.1, constraint 4): nobody draws a recipient they had in the last N
/// earlier events with pairings, following the previous-event chain; events without pairings are
/// skipped, and unreadable pairings warn and let the draw go ahead.
/// With three people there are only two ways names can fall (Pat → Ann → Bob → Pat, or the
/// reverse), so forbidding one forces the other.
/// </summary>
public sealed class NoRepeatTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        ann = await app.PersonAsync("Ann");
        bob = await app.PersonAsync("Bob");
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    string Manage(Guid ev) => $"/events/{ev}/manage";

    /// <summary>An earlier event, archived with the given pairing (or none), linked to <paramref name="previous"/>.</summary>
    async Task<Guid> EarlierAsync(string name, Dictionary<Guid, Guid>? pairs, Guid? previous = null, Guid[]? people = null,
        EventStatus status = EventStatus.Archived)
    {
        var ev = await app.EventAsync(name, EventStatus.Open, [me], people ?? [me, ann, bob]);
        if (pairs is not null)
            await app.SealAsync(ev, pairs);
        await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, status).SetProperty(e => e.PreviousEventId, previous)));
        return ev;
    }

    async Task<Guid> CurrentAsync(Guid previous, int depth, Guid[]? people = null)
    {
        var ev = await app.EventAsync("This year", EventStatus.Open, [me], people ?? [me, ann, bob]);
        await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.PreviousEventId, previous).SetProperty(e => e.NoRepeatDepth, depth)));
        return ev;
    }

    /// <summary>Draws from the manage page several times, redrawing in between; returns each pairing.</summary>
    async Task<List<Dictionary<Guid, Guid>>> DrawManyAsync(Guid ev, int times = 6)
    {
        var draws = new List<Dictionary<Guid, Guid>>();
        for (var i = 0; i < times; i++)
        {
            await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/draw");
            Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
            draws.Add(await app.PairsAsync(ev));
            await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/redraw");
        }
        return draws;
    }

    [Fact]
    public async Task Last_years_pairings_dont_repeat()
    {
        var last = await EarlierAsync("Last year", Loop(me, ann, bob));
        var ev = await CurrentAsync(last, depth: 1);

        Assert.All(await DrawManyAsync(ev), pairs => Assert.Equal(Loop(me, bob, ann), pairs));
    }

    [Fact]
    public async Task Without_a_link_there_is_no_rule()
    {
        await EarlierAsync("Last year", Loop(me, ann, bob));
        var ev = await app.EventAsync("This year", EventStatus.Open, [me], [me, ann, bob]);

        var draws = await DrawManyAsync(ev, times: 30);
        Assert.Contains(draws, d => d[me] == ann);
        Assert.Contains(draws, d => d[me] == bob);
    }

    [Fact]
    public async Task It_applies_only_to_people_in_both_events()
    {
        // Last year Pat → Ann → Carol → Bob → Pat. Of that, only Pat → Ann concerns this
        // year's three, which forbids Pat → Ann → Bob → Pat.
        var carol = await app.PersonAsync("Carol");
        var last = await EarlierAsync("Last year", Loop(me, ann, carol, bob), people: [me, ann, bob, carol]);
        var ev = await CurrentAsync(last, depth: 1);

        Assert.All(await DrawManyAsync(ev), pairs => Assert.Equal(Loop(me, bob, ann), pairs));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task The_depth_counts_back_that_many_events(int depth, bool possible)
    {
        var twoYearsAgo = await EarlierAsync("Two years ago", Loop(me, bob, ann));
        var last = await EarlierAsync("Last year", Loop(me, ann, bob), twoYearsAgo);
        var ev = await CurrentAsync(last, depth);

        if (possible)
        {
            Assert.All(await DrawManyAsync(ev, times: 3), pairs => Assert.Equal(Loop(me, bob, ann), pairs));
            return;
        }
        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("counting who they drew in earlier years", page.QuerySelector("#draw-blocked")!.TextContent);
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)] // e.g. names back in the hat after a redraw
    public async Task Linked_events_without_pairings_are_skipped(EventStatus status)
    {
        var twoYearsAgo = await EarlierAsync("Two years ago", Loop(me, ann, bob));
        var last = await EarlierAsync("Last year", null, twoYearsAgo, status: status);
        var ev = await CurrentAsync(last, depth: 1);

        Assert.All(await DrawManyAsync(ev, times: 4), pairs => Assert.Equal(Loop(me, bob, ann), pairs));
    }

    [Fact]
    public async Task Unreadable_pairings_warn_and_the_draw_goes_ahead()
    {
        var twoYearsAgo = await EarlierAsync("Two years ago", Loop(me, ann, bob));
        var last = await EarlierAsync("Last year", Loop(me, bob, ann), twoYearsAgo);
        // Last year's slips were sealed under another key: they still count as one of the N.
        await app.WithDbAsync(async db =>
        {
            foreach (var a in await db.Assignments.Where(a => a.EventId == last).ToListAsync())
                a.GiverTag = RandomNumberGenerator.GetBytes(32);
            await db.SaveChangesAsync();
        });
        var ev = await CurrentAsync(last, depth: 1);

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("can't be fully applied", page.QuerySelector("#status .warning")!.TextContent);
        var response = await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/draw");
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        Assert.Contains("can't be fully applied", (await browser.GetPageAsync(Manage(ev))).QuerySelector(".notice")!.TextContent);
        Assert.Equal(3, (await app.PairsAsync(ev)).Count);
    }

    [Fact]
    public async Task The_rule_survives_account_deletion_in_the_earlier_event()
    {
        // Ann deleted her account after last year: she's a nameless tombstone there, but her
        // pairings still count for the people in both events.
        var carol = await app.PersonAsync("Carol");
        var last = await EarlierAsync("Last year", Loop(me, ann, bob, carol), people: [me, ann, bob, carol]);
        await app.WithDbAsync(db => db.People.Where(p => p.Id == ann).ExecuteUpdateAsync(s => s
            .SetProperty(p => p.DisplayName, (string?)null).SetProperty(p => p.OidcSub, (string?)null).SetProperty(p => p.OidcIssuer, (string?)null)));
        // This year: Pat, Bob, Carol. Last year's Bob → Carol forbids Pat → Bob → Carol → Pat.
        var ev = await CurrentAsync(last, depth: 1, people: [me, bob, carol]);

        Assert.All(await DrawManyAsync(ev), pairs => Assert.Equal(Loop(me, carol, bob), pairs));
    }
}
