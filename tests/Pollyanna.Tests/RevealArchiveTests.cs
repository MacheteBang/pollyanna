using System.Net;
using System.Security.Cryptography;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Reveal and archive (spec §4, §6, §7): the confirmation before the exchange date, the revealed
/// list and who sees it, read-only threads, archiving with and without the threads, the past
/// events page, and status changes only from the right status.
/// </summary>
public sealed class RevealArchiveTests : IAsyncLifetime
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

    string Manage(Guid ev) => $"/events/{ev}/manage";

    DateOnly Today => DateOnly.FromDateTime(app.Clock.GetUtcNow().UtcDateTime);

    /// <summary>Pat organizes and takes part; Pat → Ann → Bob → Carol → Pat.</summary>
    async Task<Guid> DrawnAsync(DateOnly? date = null)
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob, carol], date: date);
        await app.SealAsync(ev, Loop(me, ann, bob, carol));
        return ev;
    }

    Task<HttpResponseMessage> RevealAsync(Guid ev, bool confirmed = true) =>
        browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/reveal", confirmed ? Form(("confirmed", "true")) : null);

    Task<HttpResponseMessage> ArchiveAsync(Guid ev, bool keep = false) =>
        browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/archive", keep ? Form(("keepMessages", "true")) : null);

    async Task<string?> NoticeAsync(string path)
    {
        var page = await browser.GetPageAsync(path);
        return (page.QuerySelector(".notice-error") ?? page.QuerySelector(".notice"))?.TextContent;
    }

    /// <summary>The revealed list as "Giver → Recipient" lines; a person's name is the last thing beside their avatar.</summary>
    static List<string> Lines(AngleSharp.Html.Dom.IHtmlDocument page) =>
        [.. page.QuerySelectorAll("li.pairing").Select(li =>
        {
            var people = li.QuerySelectorAll(".pairing-person").Select(p => (p.Children.LastOrDefault() ?? p).TextContent.Trim()).ToList();
            return $"{people[0]} → {people[1]}";
        })];

    Task<int> MessageCountAsync(Guid ev) =>
        app.WithDbAsync(db => db.Messages.CountAsync(x => db.Assignments.Any(a => a.Id == x.AssignmentId && a.EventId == ev)));

    // ---------- Reveal ----------

    [Fact]
    public async Task Revealing_before_the_exchange_date_takes_a_confirmation()
    {
        var ev = await DrawnAsync(Today.AddDays(5));

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.NotNull(page.QuerySelector($"form[action='{Manage(ev)}/reveal'] input[name='confirmed'][required]"));

        await RevealAsync(ev, confirmed: false);
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        Assert.Contains("Tick the box", await NoticeAsync(Manage(ev)));

        await RevealAsync(ev);
        Assert.Equal(EventStatus.Revealed, await app.StatusAsync(ev));
        Assert.True(await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).Select(e => e.PairingsRevealed).SingleAsync()));
    }

    [Theory]
    [InlineData(11, true)]  // still the day before somewhere (UTC−12): ask
    [InlineData(13, false)] // the date has begun everywhere
    public async Task On_the_exchange_date_the_confirmation_goes_once_the_date_has_begun_everywhere(int utcHour, bool asks)
    {
        // Tomorrow, so the fake clock only moves forward, and not far enough to end the session.
        var date = Today.AddDays(1);
        var ev = await DrawnAsync(date);
        app.Clock.SetUtcNow(new DateTimeOffset(date.ToDateTime(new TimeOnly(utcHour, 0)), TimeSpan.Zero));

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Equal(asks, page.QuerySelector($"form[action='{Manage(ev)}/reveal'] input[name='confirmed']") is not null);
        await RevealAsync(ev, confirmed: false);
        Assert.Equal(asks ? EventStatus.Drawn : EventStatus.Revealed, await app.StatusAsync(ev));
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    public async Task Reveal_needs_names_drawn(EventStatus status)
    {
        var ev = await app.EventAsync("Stockings", status, [me], [me, ann, bob]);
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/reveal']"));
        await RevealAsync(ev);
        Assert.Equal(status, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task Status_changes_are_claimed_from_the_status_the_organizer_saw()
    {
        var ev = await DrawnAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PollyannaDb>();
            var events = scope.ServiceProvider.GetRequiredService<EventManager>();
            var seen = (await EventAccess.FindAsync(db, ev, me))!;
            // Someone else redrew in the meantime.
            await db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventStatus.Open));
            Assert.False((await events.RevealAsync(seen, me, confirmed: true)).Ok);
            Assert.False((await events.ArchiveAsync(seen, keepMessages: false)).Ok);
        }
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task The_revealed_list_shows_who_drew_whom_to_participants_and_organizers()
    {
        var olive = await app.SignInAsync(new MockIdentity("olive", "Olive", "olive@example.com", true, null));
        using var oliveBrowser = olive.Browser;
        var outsider = await app.SignInAsync(new MockIdentity("otto", "Otto", "otto@example.com", true, null));
        using var outsiderBrowser = outsider.Browser;
        var ev = await DrawnAsync();
        await app.WithDbAsync(async db => { db.Organizers.Add(new Organizer { EventId = ev, PersonId = olive.PersonId }); await db.SaveChangesAsync(); });
        await RevealAsync(ev);

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Equal(["Ann → Bob", "Bob → Carol", "Carol → Pat (you)", "Pat (you) → Ann"], Lines(page).Order());
        Assert.Equal(2, page.QuerySelectorAll("li.pairing-mine").Length);
        Assert.Null(page.QuerySelector("[data-reveal]"));

        // An organizer who isn't in the hat sees it on the manage page.
        var oliveEvent = await oliveBrowser.GetFollowingAsync($"/events/{ev}");
        var manage = await TestBrowser.ParseAsync(oliveEvent);
        Assert.Equal(4, Lines(manage).Count);
        Assert.Contains("Pat → Ann", Lines(manage));

        Assert.Equal(HttpStatusCode.NotFound, (await outsiderBrowser.GetAsync($"/events/{ev}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsiderBrowser.GetAsync(Manage(ev))).StatusCode);
    }

    [Fact]
    public async Task Unreadable_slips_are_listed_without_a_giver()
    {
        var ev = await DrawnAsync();
        await app.UpdateAssignmentAsync(ev, bob, a => a.GiverTag = RandomNumberGenerator.GetBytes(32));
        await RevealAsync(ev);

        var page = await browser.GetPageAsync($"/events/{ev}");
        var lines = Lines(page);
        Assert.Equal(4, lines.Count);
        Assert.Equal("Someone → Carol", lines[^1]);
        Assert.Contains("can't be read any more", page.QuerySelector("#pairings")!.TextContent);
    }

    [Fact]
    public async Task After_reveal_threads_are_read_only_named_and_private()
    {
        var ev = await DrawnAsync();
        await app.MessageAsync(ev, me, MessageSender.Giver, "Socks or gloves?");
        await app.MessageAsync(ev, me, MessageSender.Recipient, "Gloves, please.");
        await app.MessageAsync(ev, carol, MessageSender.Giver, "What's your shoe size?");
        await app.MessageAsync(ev, ann, MessageSender.Giver, "Ann asks Bob privately");
        await RevealAsync(ev);

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Equal("You asked Ann", page.QuerySelector("#ask-heading")!.TextContent);
        Assert.Contains("Gloves, please.", page.QuerySelector("#ask")!.TextContent);
        Assert.Equal("Carol asked you", page.QuerySelector("#questions-heading")!.TextContent);
        Assert.Contains("CAROL ASKED", page.QuerySelector("#questions")!.TextContent.ToUpperInvariant());
        Assert.DoesNotContain("Ann asks Bob privately", page.Source.Text);
        Assert.Null(page.QuerySelector("textarea[name='body']"));
        Assert.Empty(page.QuerySelectorAll(".passed-new"));
        Assert.DoesNotContain("Ann asks Bob privately", (await browser.GetPageAsync(Manage(ev))).Source.Text);

        // Posting is refused, and the questions page sends back to the event page.
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/ask", Form(("body", "One more?")));
        Assert.Contains("read-only", await NoticeAsync($"/events/{ev}"));
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/questions/reply", Form(("body", "Ten.")));
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/questions/close");
        Assert.Equal(4, await MessageCountAsync(ev));
        Assert.False((await app.AssignmentOfAsync(ev, carol)).ThreadClosed);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync($"/events/{ev}/questions")).StatusCode);
    }

    [Fact]
    public async Task Leaving_isnt_offered_once_revealed()
    {
        var ev = await DrawnAsync();
        await RevealAsync(ev);

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Null(page.QuerySelector($"a[href='/events/{ev}/leave']"));
        Assert.NotNull(page.QuerySelector("#your-notes textarea"));
        Assert.Null(page.QuerySelector("#shopping"));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync($"/events/{ev}/leave")).StatusCode);
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/leave", Form(("confirmed", "true")));
        Assert.Contains(me, await app.ParticipantsAsync(ev));
    }

    [Fact]
    public async Task Revealing_keeps_someone_waiting_to_leave_and_drops_the_request()
    {
        var ev = await DrawnAsync();
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == ann)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true)));

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("Ann asked to leave", page.QuerySelector("#reveal")!.TextContent);
        Assert.Contains("Ann asked to leave", page.QuerySelector("#archive")!.TextContent);

        await RevealAsync(ev);
        Assert.Contains(ann, await app.ParticipantsAsync(ev));
        Assert.False(await app.WithDbAsync(db => db.Participants.AnyAsync(p => p.EventId == ev && p.WantsToLeave)));
    }

    // ---------- Archive ----------

    [Fact]
    public async Task Archiving_a_drawn_event_deletes_its_threads_and_keeps_pairings_sealed()
    {
        var ev = await DrawnAsync();
        await app.MessageAsync(ev, me, MessageSender.Giver, "Socks or gloves?");
        await app.MessageAsync(ev, carol, MessageSender.Giver, "What's your shoe size?");

        await ArchiveAsync(ev);
        Assert.Equal(EventStatus.Archived, await app.StatusAsync(ev));
        Assert.Equal(0, await MessageCountAsync(ev));
        Assert.Contains("deleted", await NoticeAsync(Manage(ev)));

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Contains("You got", page.QuerySelector("#slip")!.TextContent);
        Assert.Contains("Ann", page.QuerySelector(".reveal-name")!.TextContent);
        Assert.Null(page.QuerySelector(".pairings"));
        Assert.Null(page.QuerySelector(".passed-note"));
        Assert.Null(page.QuerySelector("#your-notes textarea"));
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector(".pairings"));
    }

    [Fact]
    public async Task Archiving_can_keep_the_threads_for_their_own_two_people()
    {
        var ev = await DrawnAsync();
        await app.MessageAsync(ev, me, MessageSender.Giver, "Socks or gloves?");
        await app.MessageAsync(ev, carol, MessageSender.Giver, "What's your shoe size?");
        await app.MessageAsync(ev, ann, MessageSender.Giver, "Ann asks Bob privately");

        await ArchiveAsync(ev, keep: true);
        Assert.Equal(3, await MessageCountAsync(ev));

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Contains("Socks or gloves?", page.QuerySelector("#ask")!.TextContent);
        // Never revealed, so the giver stays anonymous.
        Assert.Equal("Your giver asked you", page.QuerySelector("#questions-heading")!.TextContent);
        Assert.DoesNotContain("Carol asked", page.Source.Text);
        Assert.DoesNotContain("Ann asks Bob privately", page.Source.Text);
    }

    [Fact]
    public async Task An_event_archived_after_reveal_keeps_its_list()
    {
        var ev = await DrawnAsync();
        await app.MessageAsync(ev, carol, MessageSender.Giver, "What's your shoe size?");
        await RevealAsync(ev);
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/reveal']"));
        await ArchiveAsync(ev, keep: true);

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Equal(4, Lines(page).Count);
        Assert.Equal("Carol asked you", page.QuerySelector("#questions-heading")!.TextContent);
        Assert.Equal("ARCHIVED", page.QuerySelector(".stamp")!.TextContent);
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    public async Task Only_drawn_or_revealed_events_can_be_archived(EventStatus status)
    {
        var ev = await app.EventAsync("Stockings", status, [me], [me, ann, bob]);
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/archive']"));
        await ArchiveAsync(ev);
        Assert.Equal(status, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task An_archived_event_is_final()
    {
        var ev = await DrawnAsync();
        await ArchiveAsync(ev);

        var manage = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("archived", manage.QuerySelector("#status")!.TextContent);
        Assert.Empty(manage.QuerySelectorAll("#status form"));
        Assert.Null(manage.QuerySelector($"form[action='{Manage(ev)}/details']"));

        await RevealAsync(ev);
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/redraw");
        await ArchiveAsync(ev, keep: true);
        Assert.Equal(EventStatus.Archived, await app.StatusAsync(ev));
        Assert.False(await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).Select(e => e.PairingsRevealed).SingleAsync()));
    }

    [Fact]
    public async Task Reveal_archive_and_past_events_log_no_pairing_or_message()
    {
        var ev = await DrawnAsync();
        await app.MessageAsync(ev, me, MessageSender.Giver, "Socks or gloves?");
        await app.MessageAsync(ev, carol, MessageSender.Giver, "What's your shoe size?");
        var rows = await app.WithDbAsync(db => db.Assignments.AsNoTracking().Where(a => a.EventId == ev).ToListAsync());
        var messages = await app.WithDbAsync(db => db.Messages.AsNoTracking().Where(x => rows.Select(r => r.Id).Contains(x.AssignmentId)).ToListAsync());

        await RevealAsync(ev);
        await browser.GetPageAsync($"/events/{ev}");
        await browser.GetPageAsync(Manage(ev));
        await ArchiveAsync(ev, keep: true);
        await browser.GetPageAsync($"/events/{ev}");
        await browser.GetPageAsync("/past");

        var secrets = new[] { me, ann, bob, carol }.Select(p => p.ToString())
            .Concat(rows.Select(r => r.Id.ToString()))
            .Concat(messages.Select(m => m.Id.ToString()))
            .Concat(rows.Select(r => Convert.ToHexString(r.GiverTag)))
            .Concat(rows.Select(r => Convert.ToBase64String(r.GiverTag)));
        var logs = app.Logs.Entries.Select(e => e.Message).ToList();
        Assert.NotEmpty(logs);
        foreach (var secret in secrets)
            Assert.DoesNotContain(logs, m => m.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Archived_events_move_from_home_to_past_events()
    {
        var other = await app.PersonAsync("Org");
        var ev = await DrawnAsync();
        var organizedOnly = await app.EventAsync("Cousins", EventStatus.Archived, [me], [ann, bob, carol], date: new DateOnly(2024, 12, 24));
        var notMine = await app.EventAsync("Strangers", EventStatus.Archived, [other], [ann, bob, carol]);
        await ArchiveAsync(ev);

        var home = await browser.GetPageAsync("/");
        Assert.Null(home.QuerySelector($"a[href='/events/{ev}']"));
        Assert.Equal("Past events (2)", home.QuerySelector("a[href='/past']")!.TextContent);

        var past = await browser.GetPageAsync("/past");
        var links = past.QuerySelectorAll(".slips a").Select(a => a.GetAttribute("href")).ToList();
        Assert.Equal([$"/events/{ev}", $"/events/{organizedOnly}"], links);
        Assert.DoesNotContain($"/events/{notMine}", past.Source.Text);
        Assert.Contains("You organized", past.QuerySelectorAll(".slips li")[1].TextContent);
    }
}
