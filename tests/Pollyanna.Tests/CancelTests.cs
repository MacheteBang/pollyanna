using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Canceling an event (spec §4): any organizer, from draft to revealed, irreversible; the event
/// is gone for everyone; invitations, leave requests, messages and waiting emails go with it;
/// it never counts for the no-repeat rule or as a previous event.
/// </summary>
public sealed class CancelTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob, carol;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        ann = await app.PersonAsync("Ann", "ann@example.com");
        bob = await app.PersonAsync("Bob", "bob@example.com");
        carol = await app.PersonAsync("Carol", "carol@example.com");
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    Task<HttpResponseMessage> CancelAsync(Guid ev) => browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/cancel");

    async Task<Guid> EventInAsync(EventStatus status, string name = "Test swap")
    {
        var drawn = status is EventStatus.Drawn or EventStatus.Revealed or EventStatus.Archived;
        var ev = await app.EventAsync(name, drawn ? EventStatus.Open : status, [me], [me, ann, bob, carol]);
        if (drawn)
        {
            await app.SealAsync(ev, Loop(me, ann, bob, carol));
            await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status).SetProperty(e => e.PairingsRevealed, status != EventStatus.Drawn)));
        }
        return ev;
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.Drawn)]
    [InlineData(EventStatus.Revealed)]
    public async Task An_organizer_cancels_an_active_event_and_it_is_gone_for_everyone(EventStatus status)
    {
        var ev = await EventInAsync(status);
        var (deeBrowser, dee) = await app.SignInAsync(new MockIdentity("dee", "Dee", "dee@example.com", true, null));
        await app.WithDbAsync(async db =>
        {
            db.Organizers.Add(new Organizer { EventId = ev, PersonId = dee });
            await db.SaveChangesAsync();
        });

        Assert.NotNull((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/cancel']"));
        var response = await CancelAsync(ev);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Equal(EventStatus.Canceled, await app.StatusAsync(ev));
        Assert.Contains("Test swap is canceled", (await browser.GetPageAsync("/")).QuerySelector(".notice")!.TextContent);

        foreach (var b in new[] { browser, deeBrowser })
        {
            foreach (var path in new[] { $"/events/{ev}", Manage(ev), $"/events/{ev}/questions", $"/events/{ev}/leave" })
                Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(path)).StatusCode);
            Assert.DoesNotContain("Test swap", (await b.GetPageAsync("/")).QuerySelector("main")!.TextContent);
            Assert.DoesNotContain("Test swap", (await b.GetPageAsync("/past")).QuerySelector("main")!.TextContent);
        }
        deeBrowser.Dispose();

        // Irreversible: nothing more can be done to it.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostWithTokenAsync("/", $"{Manage(ev)}/open")).StatusCode);
    }

    [Fact]
    public async Task An_archived_event_cant_be_canceled()
    {
        var ev = await EventInAsync(EventStatus.Archived);
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/cancel']"));
        await CancelAsync(ev);
        Assert.Equal(EventStatus.Archived, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task A_participant_who_doesnt_organize_cant_cancel()
    {
        var ev = await app.EventAsync("Test swap", EventStatus.Open, [ann], [me, ann, bob]);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostWithTokenAsync($"/events/{ev}", $"{Manage(ev)}/cancel")).StatusCode);
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
    }

    [Fact]
    public async Task The_confirmation_says_what_goes()
    {
        var open = await EventInAsync(EventStatus.Open, "Cousins");
        await app.WithDbAsync(async db =>
        {
            db.Invitations.Add(new Invitation { EventId = open, Email = "sue@example.com", EmailNormalized = "sue@example.com", InvitedBy = me });
            await db.SaveChangesAsync();
        });
        var text = (await browser.GetPageAsync(Manage(open))).QuerySelector("#cancel")!.TextContent;
        Assert.Contains("Cousins", text);
        Assert.Contains("can't be undone", text);
        Assert.Contains("4 people", text);
        Assert.Contains("1 invitation", text);
        Assert.Contains("Nobody is emailed", text);
        Assert.DoesNotContain("slips", text);

        var drawn = await EventInAsync(EventStatus.Drawn);
        Assert.Contains("slips", (await browser.GetPageAsync(Manage(drawn))).QuerySelector("#cancel")!.TextContent);
    }

    [Fact]
    public async Task Canceling_drops_invitations_leave_requests_messages_and_waiting_emails_and_keeps_the_rest()
    {
        var ev = await EventInAsync(EventStatus.Drawn);
        await app.MessageAsync(ev, ann, MessageSender.Giver, "Sizes?");
        await app.HouseholdAsync(ev, "Smiths", ann, bob);
        await app.WithDbAsync(async db =>
        {
            db.Invitations.Add(new Invitation { EventId = ev, Email = "sue@example.com", EmailNormalized = "sue@example.com", InvitedBy = me });
            db.Notifications.Add(new Notification { Kind = NotificationKind.NamesDrawn, EventId = ev, PersonId = ann, NotBefore = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await db.Participants.Where(p => p.EventId == ev && p.PersonId == bob).ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true));
        });

        await CancelAsync(ev);
        await app.SendMailAsync();

        Assert.Empty(app.Mail.Sent);
        await app.WithDbAsync(async db =>
        {
            Assert.False(await db.Invitations.AnyAsync(i => i.EventId == ev));
            Assert.False(await db.Notifications.AnyAsync(n => n.EventId == ev));
            Assert.False(await db.Participants.AnyAsync(p => p.EventId == ev && p.WantsToLeave));
            Assert.False(await db.Messages.AnyAsync(x => db.Assignments.Any(a => a.Id == x.AssignmentId && a.EventId == ev)));
            Assert.Equal(4, await db.Participants.CountAsync(p => p.EventId == ev));
            Assert.Equal(4, await db.Assignments.CountAsync(a => a.EventId == ev));
            Assert.True(await db.Households.AnyAsync(h => h.EventId == ev));
            return 0;
        });
    }

    [Fact]
    public async Task A_canceled_event_isnt_offered_or_accepted_as_a_previous_event()
    {
        var canceled = await EventInAsync(EventStatus.Drawn, "Practice run");
        await CancelAsync(canceled);

        var form = await browser.GetPageAsync("/events/new");
        Assert.Null(form.QuerySelector($"select[name='PreviousEventId'] option[value='{canceled}']"));

        await browser.SubmitOnAsync("/events/new", "/events/new",
        [
            new("Name", "Stockings"), new("BudgetText", "$25"), new("ExchangeDate", "2026-12-24"),
            new("TakePart", "true"), new("PreviousEventId", canceled.ToString()), new("NoRepeatDepth", "1"),
        ]);
        Assert.False(await app.WithDbAsync(db => db.Events.AnyAsync(e => e.Name == "Stockings")));
    }

    [Fact]
    public async Task An_event_linked_to_a_canceled_one_keeps_the_link_shown_as_canceled()
    {
        var canceled = await EventInAsync(EventStatus.Drawn, "Practice run");
        var ev = await app.EventAsync("This year", EventStatus.Open, [me], [me, ann, bob]);
        await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(e => e.PreviousEventId, canceled)));
        await CancelAsync(canceled);
        await browser.GetPageAsync("/"); // shows the "canceled" notice, which names it

        var page = await browser.GetPageAsync(Manage(ev));
        var option = page.QuerySelector($"select[name='PreviousEventId'] option[value='{canceled}']")!;
        Assert.True(option.HasAttribute("selected"));
        Assert.Equal("a canceled event", option.TextContent.Trim());
        Assert.DoesNotContain("Practice run", page.Body!.TextContent);
    }

    [Fact]
    public async Task Being_the_only_organizer_of_a_canceled_event_doesnt_block_account_deletion()
    {
        var ev = await EventInAsync(EventStatus.Revealed);
        await CancelAsync(ev);

        Assert.Equal("/auth/deleted", (await browser.PostWithTokenAsync("/account/delete", "/account/delete")).Headers.Location!.OriginalString);
        Assert.Empty(await app.OrganizersAsync(ev));
        Assert.Contains(me, await app.ParticipantsAsync(ev));
    }
}
