using Pollyanna.Data;
using Pollyanna.Happenings;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Every happening is announced at its source, through one entry point, naming the event, the
/// kind, who acted and who it's about (spec §16). The emailed kinds are covered by the email tests.
/// </summary>
public sealed class HappeningTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!, annBrowser = null!;
    Guid me, ann;
    Guid bob, cy;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        (annBrowser, ann) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        bob = await app.PersonAsync("Bob", "bob@example.com");
        cy = await app.PersonAsync("Cy", "cy@example.com");
        app.Happenings.Clear();
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        annBrowser.Dispose();
        await app.DisposeAsync();
    }

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    Task Post(Guid ev, string action, IEnumerable<KeyValuePair<string, string>>? fields = null) =>
        browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/{action}", fields);

    Task<Guid> OpenAsync() => app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob, cy]);

    async Task<Guid> DrawnAsync()
    {
        var ev = await OpenAsync();
        await app.SealAsync(ev, Loop(me, ann, bob, cy));
        app.Happenings.Clear();
        return ev;
    }

    void Heard(params Happening[] expected)
    {
        Assert.Equal(expected, app.Happenings.Heard);
        app.Happenings.Clear();
    }

    // ---------- The table ----------

    [Fact]
    public void Every_kind_has_exactly_one_row_in_the_table()
    {
        var kinds = HappeningKinds.Table.Select(r => r.Kind).ToList();
        Assert.Equal(kinds.Distinct().Count(), kinds.Count);
        Assert.Equal(Enum.GetValues<HappeningKind>().Order(), kinds.Order());
    }

    [Fact]
    public async Task Announcing_a_kind_the_table_lacks_fails()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var announcer = scope.ServiceProvider.GetRequiredService<Announcer>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            announcer.AnnounceAsync(new Happening((HappeningKind)999, Guid.NewGuid(), me)));
    }

    // ---------- Status ----------

    [Fact]
    public async Task Drawing_names_announces_it_naming_who_drew()
    {
        var ev = await OpenAsync();
        await Post(ev, "draw");
        Heard(new Happening(HappeningKind.NamesDrawn, ev, me));
    }

    [Fact]
    public async Task Opening_a_draft_announces_it()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, ann, bob]);
        await Post(ev, "open");
        Heard(new Happening(HappeningKind.EventOpened, ev, me));
    }

    [Fact]
    public async Task Putting_the_names_back_announces_it_and_takes_out_whoever_asked_to_leave()
    {
        var ev = await DrawnAsync();
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == cy)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true)));

        await Post(ev, "redraw");

        Heard(new Happening(HappeningKind.NamesPutBack, ev, me), new Happening(HappeningKind.TakenOut, ev, me, cy));
    }

    [Fact]
    public async Task Archiving_and_canceling_are_announced()
    {
        var drawn = await DrawnAsync();
        await Post(drawn, "archive");
        Heard(new Happening(HappeningKind.EventArchived, drawn, me));

        var open = await OpenAsync();
        app.Happenings.Clear();
        await Post(open, "cancel");
        Heard(new Happening(HappeningKind.EventCanceled, open, me));
    }

    [Fact]
    public async Task Changing_the_details_is_announced()
    {
        var ev = await OpenAsync();
        await Post(ev, "details",
            Form(("Name", "Stockings 2027"), ("BudgetText", "$30"), ("ExchangeDate", "2027-01-06"), ("PreviousEventId", ""), ("NoRepeatDepth", "1")));
        Heard(new Happening(HappeningKind.DetailsChanged, ev, me));
    }

    // ---------- People ----------

    [Fact]
    public async Task Inviting_an_address_announces_the_invitation_even_in_a_draft()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);
        await Post(ev, "addpeople", Form(("email", "sue@example.com")));
        var invitation = await app.WithDbAsync(db => db.Invitations.Select(i => i.Id).SingleAsync());
        Heard(new Happening(HappeningKind.Invited, ev, me, invitation));

        await Post(ev, "cancelinvite", Form(("invitationId", invitation.ToString())));
        Heard(new Happening(HappeningKind.InvitationCanceled, ev, me, invitation));
    }

    [Fact]
    public async Task Leaving_before_the_draw_is_announced()
    {
        var ev = await OpenAsync();
        await annBrowser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/leave");
        Heard(new Happening(HappeningKind.Left, ev, ann, ann));
    }

    [Fact]
    public async Task Leaving_after_the_draw_announces_the_patch_too()
    {
        var ev = await DrawnAsync();
        await annBrowser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/leave", Form(("confirmed", "true")));
        Heard(new Happening(HappeningKind.Patched, ev, ann, ann), new Happening(HappeningKind.Left, ev, ann, ann));
    }

    [Fact]
    public async Task Taking_someone_out_is_announced_with_the_patch_or_the_redraw_it_needs()
    {
        var open = await OpenAsync();
        await Post(open, "removeperson", Form(("personId", cy.ToString())));
        Heard(new Happening(HappeningKind.TakenOut, open, me, cy));

        var patched = await DrawnAsync();
        await Post(patched, "removeperson", Form(("personId", cy.ToString()), ("change", "Patch")));
        Heard(new Happening(HappeningKind.Patched, patched, me, cy), new Happening(HappeningKind.TakenOut, patched, me, cy));

        var redrawn = await DrawnAsync();
        await Post(redrawn, "removeperson", Form(("personId", cy.ToString()), ("change", "Redraw")));
        Heard(new Happening(HappeningKind.TakenOut, redrawn, me, cy), new Happening(HappeningKind.NamesPutBack, redrawn, me));
    }

    [Fact]
    public async Task A_late_joiner_patched_in_is_announced_with_the_patch()
    {
        var ev = await DrawnAsync();
        var dee = await app.PersonAsync("Dee", "dee@example.com");
        await app.EventAsync("Known", EventStatus.Open, [me], [me, dee]);
        app.Happenings.Clear();

        await Post(ev, "addperson", Form(("personId", dee.ToString()), ("change", "Patch"), ("householdId", "")));

        Heard(new Happening(HappeningKind.AddedToEvent, ev, me, dee), new Happening(HappeningKind.Patched, ev, me, dee));
    }

    [Fact]
    public async Task Taking_away_an_organizer_is_announced()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me, ann], [me]);
        await Post(ev, "removeorganizer", Form(("personId", ann.ToString())));
        Heard(new Happening(HappeningKind.OrganizerRemoved, ev, me, ann));
    }

    [Fact]
    public async Task Every_change_to_the_rules_is_announced()
    {
        var ev = await OpenAsync();
        await Post(ev, "savehousehold", Form(("householdName", "Parks"), ("members", ann.ToString())));
        var household = await app.WithDbAsync(db => db.Households.Select(h => h.Id).SingleAsync());
        await Post(ev, "deletehousehold", Form(("householdId", household.ToString())));
        await Post(ev, "addrule", Form(("personA", bob.ToString()), ("personB", cy.ToString())));
        var rule = await app.WithDbAsync(db => db.Exclusions.Select(x => x.Id).SingleAsync());
        await Post(ev, "removerule", Form(("exclusionId", rule.ToString())));

        var changed = new Happening(HappeningKind.RulesChanged, ev, me);
        Heard(changed, changed, changed, changed);
    }

    [Fact]
    public async Task Deleting_an_account_announces_leaving_and_giving_up_organizing()
    {
        var ev = await OpenAsync();
        var organized = await app.EventAsync("Other", EventStatus.Open, [me, ann], [me]);
        app.Happenings.Clear();

        await annBrowser.PostWithTokenAsync("/account", "/account/delete");

        Heard(new Happening(HappeningKind.Left, ev, ann, ann), new Happening(HappeningKind.OrganizerRemoved, organized, ann, ann));
    }

    // ---------- A participant's own ----------

    [Fact]
    public async Task Saving_changed_notes_is_announced_and_saving_the_same_ones_is_not()
    {
        var ev = await OpenAsync();
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", "Socks")));
        Heard(new Happening(HappeningKind.NotesEdited, ev, me));

        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/notes", Form(("notes", "Socks")));
        Heard();
    }

    [Fact]
    public async Task Looking_for_the_first_time_is_announced_and_looking_again_is_not()
    {
        var ev = await DrawnAsync();
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/seen");
        Heard(new Happening(HappeningKind.HasLooked, ev, me));

        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/seen");
        Heard();
    }

    [Fact]
    public async Task Marking_done_shopping_is_announced()
    {
        var ev = await DrawnAsync();
        await browser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/shopping", Form(("done", "true")));
        Heard(new Happening(HappeningKind.DoneShopping, ev, me));
    }

    [Fact]
    public async Task Dropping_a_request_to_leave_is_announced_whoever_drops_it()
    {
        var ev = await DrawnAsync();
        await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && (p.PersonId == ann || p.PersonId == bob))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true)));

        await annBrowser.PostWithTokenAsync($"/events/{ev}", $"/events/{ev}/stay");
        await Post(ev, "keepin", Form(("personId", bob.ToString())));

        Heard(new Happening(HappeningKind.LeaveWithdrawn, ev, ann, ann), new Happening(HappeningKind.LeaveWithdrawn, ev, me, bob));
    }

    // ---------- Between a giver and a recipient ----------

    [Fact]
    public async Task A_reply_closing_and_reopening_are_announced_naming_only_the_recipient()
    {
        var ev = await DrawnAsync();
        await app.MessageAsync(ev, me, MessageSender.Giver, "Favorite color?");
        var questions = $"/events/{ev}/questions";

        await annBrowser.PostWithTokenAsync(questions, $"{questions}/reply", Form(("body", "Blue.")));
        await annBrowser.PostWithTokenAsync(questions, $"{questions}/close");
        await annBrowser.PostWithTokenAsync(questions, $"{questions}/reopen");

        Heard(new Happening(HappeningKind.Reply, ev, ann),
            new Happening(HappeningKind.ConversationClosed, ev, ann),
            new Happening(HappeningKind.ConversationReopened, ev, ann));
    }
}
