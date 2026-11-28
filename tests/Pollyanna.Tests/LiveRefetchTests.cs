using Pollyanna.Data;
using Pollyanna.Happenings;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// A live refetch isn't a visit (spec §16 "What each page does live", §6): a page fetched on a
/// live hint, at its own URL with the live header, renders as usual but moves no read marker or
/// <c>NotesVersionSeen</c>, clears no <c>RecipientChanged</c> or <c>ThreadChangeUnseen</c>,
/// deletes no stored notification and sets no <c>Viewed</c>. A plain navigation to the same page
/// afterwards does, so a header the page ignores can't pass. Pat gives to Ann, Ann to Bob, Bob to
/// Cy, Cy to Pat; Pat also organizes.
/// </summary>
public sealed class LiveRefetchTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!, ann = null!;
    Guid patId, annId, bob, cy;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (pat, patId) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        (ann, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        bob = await app.PersonAsync("Bob", "bob@example.com");
        cy = await app.PersonAsync("Cy", "cy@example.com");
    }

    public async Task DisposeAsync()
    {
        pat.Dispose();
        ann.Dispose();
        await app.DisposeAsync();
    }

    async Task<Guid> EventAsync(EventStatus status)
    {
        var ev = await app.EventAsync("Stockings", status, [patId], [patId, annId, bob, cy]);
        if (status is EventStatus.Drawn or EventStatus.Revealed or EventStatus.Archived)
            await app.SealAsync(ev, Loop(patId, annId, bob, cy));
        return ev;
    }

    /// <summary>Everything a visit could move: every sealed row's markers and every stored notification.</summary>
    Task<string> MarksAsync() => app.WithDbAsync(async db =>
    {
        var rows = await db.Assignments.AsNoTracking().ToListAsync();
        var stored = await db.InAppNotifications.AsNoTracking().ToListAsync();
        return string.Join("\n", rows
            .Select(a => $"{a.Id} viewed={a.Viewed} notes={a.NotesVersionSeen} changed={a.RecipientChanged} "
                + $"unseen={a.ThreadChangeUnseen} giver={a.GiverReadUpto} recipient={a.RecipientReadUpto}")
            .Concat(stored.Select(n => $"{n.PersonId} {n.EventId} {n.Kind}"))
            .Order(StringComparer.Ordinal));
    });

    Task StoreAsync(Guid person, Guid ev, HappeningKind kind) => app.WithDbAsync(db =>
    {
        db.InAppNotifications.Add(new InAppNotification
        {
            PersonId = person, EventId = ev, Kind = kind, NamedPersonId = patId, At = app.Clock.GetUtcNow().UtcDateTime,
        });
        return db.SaveChangesAsync();
    });

    Task<int> StoredAsync(Guid person) => app.WithDbAsync(db => db.InAppNotifications.CountAsync(n => n.PersonId == person));

    /// <summary>Gives Ann's own row (Ann gives to Bob) every marker a visit or opening the card would move.</summary>
    async Task UnseenEverythingAsync(Guid ev)
    {
        await app.MessageAsync(ev, annId, MessageSender.Recipient, "Medium.");
        await app.MessageAsync(ev, patId, MessageSender.Giver, "Size?");
        await app.UpdateAssignmentAsync(ev, annId, a =>
        {
            a.Viewed = false;
            a.RecipientChanged = true;
            a.ThreadChangeUnseen = true;
            a.NotesVersionSeen = -1;
            a.GiverReadUpto = 0;
        });
    }

    [Fact]
    public async Task Home_fetched_live_renders_and_moves_nothing()
    {
        var ev = await EventAsync(EventStatus.Drawn);
        await UnseenEverythingAsync(ev);
        await StoreAsync(annId, ev, HappeningKind.NamesDrawn);
        var before = await MarksAsync();

        var page = await ann.GetPageAsync("/", live: true);

        Assert.Equal("Your giver asked you something", page.QuerySelector(".panel .waiting")!.TextContent.Trim());
        Assert.Equal(before, await MarksAsync());
    }

    public static TheoryData<EventStatus, HappeningKind> EventPageStates => new()
    {
        { EventStatus.Open, HappeningKind.EventOpened },
        { EventStatus.Drawn, HappeningKind.NamesDrawn },
        { EventStatus.Revealed, HappeningKind.EventRevealed },
        { EventStatus.Archived, HappeningKind.EventRevealed },
    };

    [Theory]
    [MemberData(nameof(EventPageStates))]
    public async Task The_event_page_fetched_live_deletes_no_stored_row_until_a_plain_visit(EventStatus status, HappeningKind kind)
    {
        var ev = await EventAsync(status);
        if (status is EventStatus.Drawn)
            await UnseenEverythingAsync(ev);
        await StoreAsync(annId, ev, kind);
        var before = await MarksAsync();

        var page = await ann.GetPageAsync($"/events/{ev}", live: true);

        Assert.Equal("Stockings", page.QuerySelector("h1")!.TextContent.Trim());
        Assert.Equal(before, await MarksAsync());

        await ann.GetPageAsync($"/events/{ev}");
        Assert.Equal(0, await StoredAsync(annId));
    }

    [Fact]
    public async Task Untying_without_the_script_fetched_live_deletes_no_stored_row()
    {
        var ev = await EventAsync(EventStatus.Drawn);
        await StoreAsync(annId, ev, HappeningKind.NamesDrawn);
        var before = await MarksAsync();

        await ann.GetPageAsync($"/events/{ev}/untie", live: true);

        Assert.Equal(before, await MarksAsync());
        await ann.GetPageAsync($"/events/{ev}/untie");
        Assert.Equal(0, await StoredAsync(annId));
    }

    [Fact]
    public async Task Questions_fetched_live_show_the_question_but_leave_it_unread_until_a_plain_visit()
    {
        var ev = await EventAsync(EventStatus.Drawn);
        await UnseenEverythingAsync(ev);
        var before = await MarksAsync();

        var page = await ann.GetPageAsync($"/events/{ev}/questions", live: true);

        Assert.Contains("Size?", page.QuerySelector("[data-conversation]")!.TextContent);
        Assert.Equal(before, await MarksAsync());
        Assert.Equal(0, (await app.AssignmentOfAsync(ev, patId)).RecipientReadUpto);

        await ann.GetPageAsync($"/events/{ev}/questions");
        Assert.Equal(1, (await app.AssignmentOfAsync(ev, patId)).RecipientReadUpto);
    }

    public static TheoryData<EventStatus> OrganizingStates => new() { EventStatus.Draft, EventStatus.Open, EventStatus.Drawn, EventStatus.Revealed };

    [Theory]
    [MemberData(nameof(OrganizingStates))]
    public async Task Organizing_fetched_live_deletes_no_stored_row_until_a_plain_visit(EventStatus status)
    {
        var ev = await EventAsync(status);
        await StoreAsync(patId, ev, HappeningKind.MadeOrganizer);
        await StoreAsync(patId, ev, HappeningKind.InvitationAccepted);
        var before = await MarksAsync();

        var page = await pat.GetPageAsync($"/events/{ev}/manage", live: true);

        Assert.NotNull(page.QuerySelector("#next-step"));
        Assert.Equal(before, await MarksAsync());

        await pat.GetPageAsync($"/events/{ev}/manage");
        Assert.Equal(0, await StoredAsync(patId));
    }

    [Fact]
    public async Task A_live_refetch_leaves_a_waiting_notice_and_the_untied_gift_for_the_next_visit()
    {
        var ev = await EventAsync(EventStatus.Drawn);
        var posted = await ann.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/shopping", Form(("done", "true")));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, posted.StatusCode);

        // A hint lands before the redirect's page loads: the background fetch takes nothing away.
        await ann.GetPageAsync($"/events/{ev}", live: true);

        var page = await ann.GetPageAsync($"/events/{ev}");
        Assert.NotNull(page.QuerySelector("main .notice"));
        Assert.True(page.QuerySelector("#gift")!.ClassList.Contains("untied"));
    }
}
