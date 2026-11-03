using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Changes after the draw (spec §4, §5.4, §9): dropout and late-joiner patches keep every rule,
/// change only one giver and fall back correctly, and leaving on one's own follows the same rules.
/// </summary>
public sealed class AfterDrawTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob, carol, dave, erin;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        ann = await app.PersonAsync("Ann");
        bob = await app.PersonAsync("Bob");
        carol = await app.PersonAsync("Carol");
        dave = await app.PersonAsync("Dave");
        erin = await app.PersonAsync("Erin");
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    string Manage(Guid ev) => $"/events/{ev}/manage";

    /// <summary>A drawn event that Pat organizes, with the given people and pairing.</summary>
    async Task<Guid> DrawnAsync(Guid[] people, Dictionary<Guid, Guid> pairs)
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], people);
        await app.SealAsync(ev, pairs);
        return ev;
    }

    Task<HttpResponseMessage> RemoveAsync(Guid ev, Guid person, string change) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/removeperson", Form(("personId", person.ToString()), ("change", change)));

    Task<HttpResponseMessage> AddLateAsync(Guid ev, Guid person, string change, Guid? household = null) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/addperson",
            [KeyValuePair.Create("personId", person.ToString()), KeyValuePair.Create("change", change),
             KeyValuePair.Create("householdId", household?.ToString() ?? "")]);

    async Task<string> NoticeAsync(string path)
    {
        var page = await browser.GetPageAsync(path);
        return (page.QuerySelector(".notice-error") ?? page.QuerySelector(".notice"))!.TextContent;
    }

    Task<List<Assignment>> RowsAsync(Guid ev) =>
        app.WithDbAsync(db => db.Assignments.AsNoTracking().Where(a => a.EventId == ev).ToListAsync());

    /// <summary>Every participant gives once and receives once, and every draw rule holds.</summary>
    async Task AssertRulesHoldAsync(Guid ev)
    {
        var pairs = await app.PairsAsync(ev);
        var people = await app.ParticipantsAsync(ev);
        Assert.Equal(people.Order(), pairs.Keys.Order());
        Assert.Equal(people.Order(), pairs.Values.Order());
        var households = await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.HouseholdId != null)
            .ToDictionaryAsync(p => p.PersonId, p => p.HouseholdId!.Value));
        var exclusions = await app.WithDbAsync(db => db.Exclusions.Where(x => x.EventId == ev).Select(x => new { x.PersonA, x.PersonB }).ToListAsync());
        foreach (var (giver, recipient) in pairs)
        {
            Assert.NotEqual(giver, recipient);
            Assert.False(households.TryGetValue(giver, out var a) && households.TryGetValue(recipient, out var b) && a == b, "same household");
            Assert.DoesNotContain(exclusions, x => (x.PersonA == giver && x.PersonB == recipient) || (x.PersonA == recipient && x.PersonB == giver));
        }
    }

    /// <summary>Which rows differ between two snapshots, by ID.</summary>
    static List<Guid> Changed(List<Assignment> before, List<Assignment> after) =>
        [.. after.Where(a => before.SingleOrDefault(b => b.Id == a.Id) is not { } b
                || b.RecipientId != a.RecipientId || b.Viewed != a.Viewed || b.RecipientChanged != a.RecipientChanged
                || b.NotesVersionSeen != a.NotesVersionSeen || !b.GiverTag.SequenceEqual(a.GiverTag))
            .Select(a => a.Id)];

    // ---------- Dropouts ----------

    [Fact]
    public async Task Dropout_patch_hands_their_recipient_to_their_giver_and_changes_nothing_else()
    {
        var ev = await DrawnAsync([ann, bob, carol, dave, erin], Loop(ann, bob, carol, dave, erin));
        // Bob (Carol's giver) has looked; his "viewed" must not be reset by the patch.
        await app.WithDbAsync(async db =>
        {
            foreach (var a in await db.Assignments.Where(a => a.EventId == ev).ToListAsync())
                a.Viewed = true;
            await db.SaveChangesAsync();
        });
        var before = await RowsAsync(ev);

        var response = await RemoveAsync(ev, carol, "Patch");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        Assert.DoesNotContain(carol, await app.ParticipantsAsync(ev));
        var pairs = await app.PairsAsync(ev);
        Assert.Equal(new Dictionary<Guid, Guid> { [ann] = bob, [bob] = dave, [dave] = erin, [erin] = ann }, pairs);
        await AssertRulesHoldAsync(ev);

        var after = await RowsAsync(ev);
        var bobs = await app.AssignmentOfAsync(ev, bob);
        Assert.Equal([bobs.Id], Changed(before, after));
        Assert.True(bobs.RecipientChanged);
        Assert.True(bobs.Viewed);

        // The organizer is told one slip changed, never whose or to whom.
        var notice = await NoticeAsync(Manage(ev));
        Assert.Contains("Took Carol out", notice);
        Assert.Contains("One person's slip changed", notice);
        foreach (var name in new[] { "Ann", "Bob", "Dave", "Erin" })
            Assert.DoesNotContain(name, notice);
    }

    [Fact]
    public async Task Dropout_patch_isnt_possible_when_they_and_their_giver_drew_each_other()
    {
        var pairs = new Dictionary<Guid, Guid> { [ann] = bob, [bob] = ann, [carol] = dave, [dave] = erin, [erin] = carol };
        var ev = await DrawnAsync([ann, bob, carol, dave, erin], pairs);
        var before = await RowsAsync(ev);

        await RemoveAsync(ev, ann, "Patch");
        Assert.Contains(ann, await app.ParticipantsAsync(ev));
        Assert.Empty(Changed(before, await RowsAsync(ev)));
        Assert.Equal(before.Count, (await RowsAsync(ev)).Count);
        Assert.Contains("A patch isn't possible for Ann", await NoticeAsync(Manage(ev)));

        // Redraw everyone instead: Ann goes, the names go back in the hat.
        await RemoveAsync(ev, ann, "Redraw");
        Assert.DoesNotContain(ann, await app.ParticipantsAsync(ev));
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Empty(await RowsAsync(ev));
        Assert.Contains("Took Ann out and put the names back in the hat", await NoticeAsync(Manage(ev)));
    }

    [Theory]
    [InlineData(true)]   // Bob and Dave share a household
    [InlineData(false)]  // an exclusion between Bob and Dave
    public async Task Dropout_patch_isnt_possible_when_it_would_break_a_rule(bool household)
    {
        var ev = await DrawnAsync([ann, bob, carol, dave, erin], Loop(ann, bob, carol, dave, erin));
        if (household)
            await app.HouseholdAsync(ev, "Bob & Dave", bob, dave);
        else
            await app.ExclusionAsync(ev, bob, dave);
        var before = await RowsAsync(ev);

        // Bob drew Carol, who drew Dave: Bob can't take Dave.
        await RemoveAsync(ev, carol, "Patch");
        Assert.Contains(carol, await app.ParticipantsAsync(ev));
        Assert.Empty(Changed(before, await RowsAsync(ev)));
        Assert.Contains("A patch isn't possible", await NoticeAsync(Manage(ev)));
    }

    [Theory]
    [InlineData("Patch")]
    [InlineData("Redraw")]
    public async Task Taking_someone_out_after_the_draw_needs_three_left(string change)
    {
        var ev = await DrawnAsync([ann, bob, carol], Loop(ann, bob, carol));
        await RemoveAsync(ev, carol, change);
        Assert.Contains(carol, await app.ParticipantsAsync(ev));
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        Assert.Equal(3, (await RowsAsync(ev)).Count);
        Assert.Contains("fewer than 3 people", await NoticeAsync(Manage(ev)));
    }

    // ---------- Late joiners ----------

    [Fact]
    public async Task Late_joiner_patch_slots_them_into_one_pairing_and_changes_only_that_giver()
    {
        var giversSeen = new HashSet<Guid>();
        for (var round = 0; round < 20; round++)
        {
            var ev = await DrawnAsync([ann, bob, carol, dave], Loop(ann, bob, carol, dave));
            await app.HouseholdAsync(ev, "Bob & Erin", bob);
            var household = await app.WithDbAsync(db => db.Households.Where(h => h.EventId == ev).Select(h => h.Id).SingleAsync());
            await app.WithDbAsync(async db =>
            {
                foreach (var a in await db.Assignments.Where(a => a.EventId == ev).ToListAsync())
                    a.Viewed = true;
                await db.SaveChangesAsync();
            });
            var beforePairs = await app.PairsAsync(ev);
            var before = await RowsAsync(ev);

            // Erin joins Bob's household: Ann → Bob and Bob → Carol can't make room, the others can.
            await AddLateAsync(ev, erin, "Patch", household);
            Assert.Contains(erin, await app.ParticipantsAsync(ev));
            Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
            await AssertRulesHoldAsync(ev);

            var pairs = await app.PairsAsync(ev);
            var giver = pairs.Single(p => p.Value == erin).Key;
            giversSeen.Add(giver);
            Assert.Equal(beforePairs[giver], pairs[erin]);
            Assert.All(beforePairs.Where(p => p.Key != giver), p => Assert.Equal(p.Value, pairs[p.Key]));

            // Every old row keeps its ID; only the giver's changed, and their "viewed" stays.
            var after = await RowsAsync(ev);
            Assert.Subset(after.Select(a => a.Id).ToHashSet(), before.Select(a => a.Id).ToHashSet());
            var givers = await app.AssignmentOfAsync(ev, giver);
            var erins = await app.AssignmentOfAsync(ev, erin);
            Assert.Equal(new[] { givers.Id, erins.Id }.Order(), Changed(before, after).Order());
            Assert.True(givers.RecipientChanged);
            Assert.True(givers.Viewed);
            Assert.False(erins.Viewed);

            var notice = await NoticeAsync(Manage(ev));
            Assert.Contains("Added Erin", notice);
            foreach (var name in new[] { "Ann", "Bob", "Carol", "Dave" })
                Assert.DoesNotContain(name, notice);
        }
        // The pairing to break is picked at random among those that work.
        Assert.Equal(new[] { carol, dave }.Order(), giversSeen.Order());
    }

    [Fact]
    public async Task Late_joiner_patch_isnt_possible_when_no_pairing_can_make_room()
    {
        // Erin joins Ann and Bob's household. Ann → Bob and Bob → Carol can't give to her, and
        // Carol → Ann would have her give to Ann.
        var ev = await DrawnAsync([ann, bob, carol], Loop(ann, bob, carol));
        await app.HouseholdAsync(ev, "The Parks", ann, bob);
        var household = await app.WithDbAsync(db => db.Households.Where(h => h.EventId == ev).Select(h => h.Id).SingleAsync());
        var before = await RowsAsync(ev);

        await AddLateAsync(ev, erin, "Patch", household);
        Assert.DoesNotContain(erin, await app.ParticipantsAsync(ev));
        Assert.Empty(Changed(before, await RowsAsync(ev)));
        Assert.Equal(3, (await RowsAsync(ev)).Count);
        Assert.Contains("A patch isn't possible", await NoticeAsync(Manage(ev)));

        await AddLateAsync(ev, erin, "Redraw", household);
        Assert.Contains(erin, await app.ParticipantsAsync(ev));
        Assert.Equal(household, await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == erin).Select(p => p.HouseholdId).SingleAsync()));
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Empty(await RowsAsync(ev));
    }

    [Fact]
    public async Task The_affected_giver_sees_their_recipient_has_changed_until_they_open_the_card()
    {
        // Pat drew Carol; Carol drops out, so Pat takes over Carol's recipient.
        var ev = await DrawnAsync([me, ann, bob, carol, dave], Loop(me, carol, ann, bob, dave));
        await RemoveAsync(ev, carol, "Patch");

        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Contains("Your recipient has changed", page.QuerySelector("main")!.TextContent);
        Assert.Equal("Ann", page.QuerySelector(".reveal-name")!.TextContent);
        Assert.Equal("Your recipient has changed", (await browser.GetPageAsync("/")).QuerySelector(".slip-update")!.TextContent.Trim());

        await browser.SubmitAsync(page, $"/events/{ev}/seen");
        Assert.DoesNotContain("Your recipient has changed", (await browser.GetPageAsync($"/events/{ev}")).QuerySelector("main")!.TextContent);
        Assert.Null((await browser.GetPageAsync("/")).QuerySelector(".slip-update"));
    }

    // ---------- Leaving on one's own ----------

    [Fact]
    public async Task Leaving_after_the_draw_asks_twice_then_patches()
    {
        var ev = await DrawnAsync([me, ann, bob, carol], Loop(me, ann, bob, carol));
        var before = await RowsAsync(ev);

        var eventPage = await browser.GetPageAsync($"/events/{ev}");
        Assert.NotNull(eventPage.QuerySelector($"a[href='/events/{ev}/leave']"));

        // First confirmation, then the second one explaining someone else's slip changes.
        var second = await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave");
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var page = await TestBrowser.ParseAsync(second);
        Assert.Contains("Someone else's slip will change", page.Body!.TextContent);
        Assert.Contains(me, await app.ParticipantsAsync(ev));

        var response = await browser.SubmitAsync(page, $"/events/{ev}/leave");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain(me, await app.ParticipantsAsync(ev));
        Assert.Equal(new Dictionary<Guid, Guid> { [carol] = ann, [ann] = bob, [bob] = carol }, await app.PairsAsync(ev));
        var carols = await app.AssignmentOfAsync(ev, carol);
        Assert.Equal([carols.Id], Changed(before, await RowsAsync(ev)));
        Assert.True(carols.RecipientChanged);
        // Pat still organizes, so they carry on at the manage page.
        Assert.Equal(Manage(ev), response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Leaving_after_the_draw_without_the_second_confirmation_does_nothing()
    {
        var ev = await DrawnAsync([me, ann, bob, carol], Loop(me, ann, bob, carol));
        await browser.PostWithTokenAsync("/account", $"/events/{ev}/leave");
        Assert.Contains(me, await app.ParticipantsAsync(ev));
        Assert.False(await app.WithDbAsync(db => db.Participants.AnyAsync(p => p.WantsToLeave)));
    }

    async Task<string> LeaveConfirmedAsync(Guid ev)
    {
        var second = await TestBrowser.ParseAsync(await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave"));
        await browser.SubmitAsync(second, $"/events/{ev}/leave");
        return await NoticeAsync($"/events/{ev}");
    }

    [Fact]
    public async Task Leaving_when_the_patch_isnt_possible_waits_for_a_redraw_and_a_redraw_takes_them_out()
    {
        // Pat and Ann drew each other, so Ann can't take over Pat's recipient.
        var pairs = new Dictionary<Guid, Guid> { [me] = ann, [ann] = me, [bob] = carol, [carol] = dave, [dave] = bob };
        var ev = await DrawnAsync([me, ann, bob, carol, dave], pairs);
        var before = await RowsAsync(ev);

        var notice = await LeaveConfirmedAsync(ev);
        Assert.Contains("You're still in for now", notice);
        Assert.Contains(me, await app.ParticipantsAsync(ev));
        Assert.Empty(Changed(before, await RowsAsync(ev)));
        Assert.True(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == me).Select(p => p.WantsToLeave).SingleAsync()));

        // The event page says so, and offers to stay instead; "Leave" isn't offered again.
        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Contains("You've asked to leave", page.Body!.TextContent);
        Assert.Null(page.QuerySelector($"a[href='/events/{ev}/leave']"));
        Assert.NotNull(page.QuerySelector(".reveal"));

        var manage = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("Pat wants to leave — redraw needed.", manage.QuerySelector("#status")!.TextContent);

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/redraw");
        Assert.DoesNotContain(me, await app.ParticipantsAsync(ev));
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Contains("Pat left, as they asked", await NoticeAsync(Manage(ev)));
    }

    [Fact]
    public async Task Leaving_that_would_leave_fewer_than_three_waits_for_a_redraw_with_the_same_message()
    {
        var bigger = await DrawnAsync([me, ann, bob, carol], new Dictionary<Guid, Guid> { [me] = ann, [ann] = me, [bob] = carol, [carol] = bob });
        var impossible = await LeaveConfirmedAsync(bigger);

        var small = await DrawnAsync([me, ann, bob], Loop(me, ann, bob));
        var tooFew = await LeaveConfirmedAsync(small);
        Assert.Contains(me, await app.ParticipantsAsync(small));
        Assert.True(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == small && p.PersonId == me).Select(p => p.WantsToLeave).SingleAsync()));
        // Whatever stopped the patch, the leaver hears the same thing, so it hints at nobody's slip.
        Assert.Equal(impossible, tooFew);
    }

    [Fact]
    public async Task A_leave_request_can_be_withdrawn()
    {
        var ev = await DrawnAsync([me, ann, bob], Loop(me, ann, bob));
        await LeaveConfirmedAsync(ev);

        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/stay");
        Assert.False(await app.WithDbAsync(db => db.Participants.AnyAsync(p => p.WantsToLeave)));
        Assert.DoesNotContain("wants to leave", (await browser.GetPageAsync(Manage(ev))).QuerySelector("#status")!.TextContent);

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/redraw");
        Assert.Contains(me, await app.ParticipantsAsync(ev));
    }

    // ---------- Logs ----------

    // ---------- Question threads (spec §4, §5.4, §6) ----------

    /// <summary>Gives every giver's thread a question and an answer, and some markers.</summary>
    async Task ThreadsEverywhereAsync(Guid ev, IEnumerable<Guid> givers)
    {
        foreach (var giver in givers)
        {
            await app.MessageAsync(ev, giver, MessageSender.Giver, $"Question from {giver}");
            await app.MessageAsync(ev, giver, MessageSender.Recipient, $"Answer to {giver}");
            await app.UpdateAssignmentAsync(ev, giver, a => { a.ThreadClosed = true; a.GiverReadUpto = 2; a.RecipientReadUpto = 1; });
        }
    }

    /// <summary>Each thread by its assignment row ID, with everything a message holds.</summary>
    Task<Dictionary<Guid, string>> ThreadsAsync(Guid ev) =>
        app.WithDbAsync(async db => (await db.Messages.AsNoTracking()
                .Where(x => db.Assignments.Any(a => a.Id == x.AssignmentId && a.EventId == ev))
                .ToListAsync())
            .GroupBy(x => x.AssignmentId)
            .ToDictionary(g => g.Key, g => string.Join("|", g.OrderBy(x => x.Seq).Select(x => $"{x.Id} {x.Seq} {x.Sender} {x.Body} {x.CreatedAt:O}"))));

    [Fact]
    public async Task A_redraw_deletes_every_thread()
    {
        var ev = await DrawnAsync([ann, bob, carol, dave], Loop(ann, bob, carol, dave));
        await ThreadsEverywhereAsync(ev, [ann, bob, carol, dave]);
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/redraw");
        Assert.Equal(0, await app.WithDbAsync(db => db.Messages.CountAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dropout_patch_deletes_only_the_threads_involving_the_dropout(bool leavesThemselves)
    {
        // Ann → Bob → Pat → Dave → Erin → Ann; Pat drops out, so Bob takes over Dave.
        var people = new[] { ann, bob, me, dave, erin };
        var ev = await DrawnAsync(people, Loop(people));
        await ThreadsEverywhereAsync(ev, people);
        var keep = new[] { ann, dave, erin };
        var kept = new Dictionary<Guid, Guid>();
        foreach (var giver in keep)
            kept[giver] = (await app.AssignmentOfAsync(ev, giver)).Id;
        var threads = await ThreadsAsync(ev);

        if (leavesThemselves)
        {
            var second = await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave");
            await browser.SubmitAsync(await TestBrowser.ParseAsync(second), $"/events/{ev}/leave");
        }
        else
            await RemoveAsync(ev, me, "Patch");
        Assert.DoesNotContain(me, await app.ParticipantsAsync(ev));

        // Bob's thread was with Pat, so it starts again; Pat's own went with their row.
        var bobs = await app.AssignmentOfAsync(ev, bob);
        Assert.Equal(dave, bobs.RecipientId);
        Assert.Empty(await app.ThreadOfAsync(ev, bob));
        Assert.False(bobs.ThreadClosed);
        Assert.Equal((0, 0), (bobs.GiverReadUpto, bobs.RecipientReadUpto));

        var after = await ThreadsAsync(ev);
        Assert.Equal(kept.Values.Order(), after.Keys.Order());
        foreach (var giver in keep)
        {
            Assert.Equal(threads[kept[giver]], after[kept[giver]]);
            var row = await app.AssignmentOfAsync(ev, giver);
            Assert.True(row.ThreadClosed);
            Assert.Equal((2, 1), (row.GiverReadUpto, row.RecipientReadUpto));
        }
    }

    [Fact]
    public async Task Late_joiner_patch_deletes_only_the_broken_pairings_thread_and_keeps_the_rest()
    {
        var people = new[] { ann, bob, carol, dave };
        var ev = await DrawnAsync(people, Loop(people));
        await ThreadsEverywhereAsync(ev, people);
        var before = await RowsAsync(ev);
        var threads = await ThreadsAsync(ev);
        Assert.Equal(4, threads.Count);

        await AddLateAsync(ev, erin, "Patch");
        var giver = (await app.PairsAsync(ev)).Single(p => p.Value == erin).Key;
        var givers = await app.AssignmentOfAsync(ev, giver);
        Assert.Empty(await app.ThreadOfAsync(ev, giver));
        Assert.False(givers.ThreadClosed);
        Assert.Equal((0, 0), (givers.GiverReadUpto, givers.RecipientReadUpto));
        Assert.Empty(await app.ThreadOfAsync(ev, erin));

        // Every other thread is exactly as it was, on its row with the same ID and markers.
        var after = await ThreadsAsync(ev);
        Assert.Equal(threads.Keys.Where(id => id != givers.Id).Order(), after.Keys.Order());
        foreach (var (id, thread) in after)
            Assert.Equal(threads[id], thread);
        foreach (var row in (await RowsAsync(ev)).Where(r => r.Id != givers.Id && before.Any(b => b.Id == r.Id)))
        {
            Assert.True(row.ThreadClosed);
            Assert.Equal((2, 1), (row.GiverReadUpto, row.RecipientReadUpto));
        }
    }

    [Fact]
    public async Task Draws_patches_and_redraws_log_no_pairing()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob, carol, dave]);
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/draw");
        await browser.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/seen");
        await AddLateAsync(ev, erin, "Patch");
        await RemoveAsync(ev, ann, "Patch");
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/redraw");
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/draw");

        var rows = await RowsAsync(ev);
        var secrets = new[] { me, ann, bob, carol, dave, erin }.Select(p => p.ToString())
            .Concat(rows.Select(r => r.Id.ToString()))
            .Concat(rows.Select(r => Convert.ToHexString(r.GiverTag)))
            .Concat(rows.Select(r => Convert.ToBase64String(r.GiverTag)));
        var logs = app.Logs.Entries.Select(e => e.Message).ToList();
        Assert.NotEmpty(logs);
        foreach (var secret in secrets)
            Assert.DoesNotContain(logs, m => m.Contains(secret, StringComparison.OrdinalIgnoreCase));
    }
}
