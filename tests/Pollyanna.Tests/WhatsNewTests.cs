using Pollyanna.Data;
using Pollyanna.Happenings;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// "What's new" for the seven stored kinds, without the script (spec §7, §16, docs/adr/0008):
/// who gets each row, its words and link, the order, dismissing, and what clears a row.
/// </summary>
public sealed class WhatsNewTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser pat = null!, ann = null!;
    Guid me, annId, bob, cy;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (pat, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
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

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    Task<HttpResponseMessage> Post(Guid ev, string action, params (string Key, string Value)[] fields) =>
        pat.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/{action}", Form(fields));

    /// <summary>One row of /whats-new as the page shows it.</summary>
    sealed record Row(string Line, string Href, string Meta);

    static async Task<IReadOnlyList<Row>> RowsAsync(TestBrowser browser)
    {
        var page = await browser.GetPageAsync("/whats-new");
        return [.. page.QuerySelectorAll(".whats-new-rows > li").Select(li => new Row(
            li.QuerySelector("a")!.TextContent.Trim(),
            li.QuerySelector("a")!.GetAttribute("href")!,
            li.QuerySelector(".whats-new-meta")!.TextContent.Trim()))];
    }

    static async Task<IReadOnlyList<string>> LinesAsync(TestBrowser browser) =>
        [.. (await RowsAsync(browser)).Select(r => r.Line)];

    // ---------- Which happenings make a row ----------

    [Fact]
    public void Exactly_the_seven_stored_kinds_are_kept_as_stored_rows()
    {
        Assert.Equal(
            new[] { HappeningKind.AddedToEvent, HappeningKind.EventOpened, HappeningKind.EventRevealed, HappeningKind.InvitationAccepted,
                HappeningKind.MadeOrganizer, HappeningKind.NamesDrawn, HappeningKind.RedrawNeeded }.Order(),
            HappeningKinds.Table.Where(r => r.Stored is not null).Select(r => r.Kind).Order());
    }

    [Fact]
    public async Task Opening_an_event_tells_every_participant_but_whoever_opened_it()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId]);

        await Post(ev, "open");

        var row = Assert.Single(await RowsAsync(ann));
        Assert.Equal("It's open: write your notes", row.Line);
        Assert.Equal($"/events/{ev}", row.Href);
        Assert.StartsWith("Stockings", row.Meta);
        Assert.Empty(await RowsAsync(pat));
    }

    [Fact]
    public async Task Drawing_names_tells_every_participant_but_the_drawer_and_says_again_after_a_redraw()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, annId, bob]);

        await Post(ev, "draw");
        var row = Assert.Single(await RowsAsync(ann));
        Assert.Equal("Names are drawn: see who you're giving to", row.Line);
        Assert.Equal($"/events/{ev}", row.Href);
        Assert.Empty(await RowsAsync(pat));
        Assert.Single(await app.WithDbAsync(db => db.InAppNotifications.Where(n => n.PersonId == bob).ToListAsync()));

        await Post(ev, "redraw");
        Assert.Empty(await RowsAsync(ann));
        await Post(ev, "draw");
        Assert.Equal(["Names were drawn again: take another look"], await LinesAsync(ann));
    }

    [Fact]
    public async Task Revealing_tells_participants_and_organizers_and_an_organizer_who_doesnt_take_part_goes_to_organizing()
    {
        var (olgaBrowser, olga) = await app.SignInAsync(new MockIdentity("olga", "Olga", "olga@example.com", true, null));
        using var _ = olgaBrowser;
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me, olga], [me, annId, bob]);
        await app.SealAsync(ev, Loop(me, annId, bob));

        await Post(ev, "reveal", ("confirmed", "true"));

        var row = Assert.Single(await RowsAsync(ann));
        Assert.Equal(new Row("See who drew whom", $"/events/{ev}", row.Meta), row);
        var olgas = Assert.Single(await RowsAsync(olgaBrowser));
        Assert.Equal(new Row("See who drew whom", $"/events/{ev}/manage", olgas.Meta), olgas);
        Assert.Empty(await RowsAsync(pat));
    }

    [Fact]
    public async Task Someone_added_to_an_open_or_drawn_event_is_told_but_not_to_a_draft_or_by_themselves()
    {
        var open = await app.EventAsync("Open one", EventStatus.Open, [me], [me, bob, cy]);
        var drawn = await app.EventAsync("Drawn one", EventStatus.Open, [me], [me, bob, cy]);
        await app.SealAsync(drawn, Loop(me, bob, cy));
        var draft = await app.EventAsync("Draft one", EventStatus.Draft, [me], [me]);

        await Post(open, "addpeople", ("email", "ann@example.com"));
        await Post(drawn, "addperson", ("personId", annId.ToString()), ("change", "Patch"));
        await Post(draft, "addpeople", ("email", "ann@example.com"));
        await Post(open, "addpeople", ("email", "pat@example.com"));

        var rows = await RowsAsync(ann);
        Assert.Equal(
            [("You're taking part. See who you're giving to", $"/events/{drawn}"), ("You're taking part. Write your notes", $"/events/{open}")],
            rows.Select(r => (r.Line, r.Href)).OrderBy(r => r.Line));
        // The patch may have made Pat the giver to someone new, which is a row of its own (#98).
        Assert.DoesNotContain(await LinesAsync(pat), l => l.StartsWith("You're taking part"));
    }

    [Fact]
    public async Task Someone_made_an_organizer_is_told_who_did_it_and_someone_once_that_account_is_gone()
    {
        var (olgaBrowser, olga) = await app.SignInAsync(new MockIdentity("olga", "Olga", "olga@example.com", true, null));
        using var _ = olgaBrowser;
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [olga], [olga, bob, cy]);

        await olgaBrowser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addorganizer", Form(("email", "ann@example.com")));

        var row = Assert.Single(await RowsAsync(ann));
        Assert.Equal(new Row("Olga made you an organizer", $"/events/{ev}/manage", row.Meta), row);
        Assert.Empty(await RowsAsync(olgaBrowser));

        await app.WithDbAsync(db => db.People.Where(p => p.Id == olga).ExecuteUpdateAsync(s => s.SetProperty(p => p.DisplayName, (string?)null)));
        Assert.Equal(["Someone made you an organizer"], await LinesAsync(ann));
    }

    [Fact]
    public async Task A_request_to_leave_that_needs_a_redraw_tells_every_other_organizer()
    {
        var (olgaBrowser, olga) = await app.SignInAsync(new MockIdentity("olga", "Olga", "olga@example.com", true, null));
        using var _ = olgaBrowser;
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me, olga], [me, annId, bob]);
        await app.SealAsync(ev, Loop(me, annId, bob));

        await LeaveAsync(ann, ev);

        foreach (var organizer in new[] { pat, olgaBrowser })
        {
            var row = Assert.Single(await RowsAsync(organizer));
            Assert.Equal(new Row("Ann wants to leave, and that needs a redraw", $"/events/{ev}/manage", row.Meta), row);
        }
    }

    [Fact]
    public async Task An_accepted_invitation_tells_every_organizer_but_whoever_took_it_up()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, bob, cy]);
        await Post(ev, "addpeople", ("email", "sue@example.com"));

        var (sue, _) = await app.SignInAsync(new MockIdentity("sue", "Sue", "sue@example.com", true, null));
        using var __ = sue;

        var row = Assert.Single(await RowsAsync(pat));
        Assert.Equal(new Row("Sue is taking part", $"/events/{ev}/manage", row.Meta), row);
        Assert.Equal(["You're taking part. Write your notes"], await LinesAsync(sue));
    }

    [Fact]
    public async Task Rows_are_written_with_emails_switched_off_and_no_mail_relay()
    {
        await app.WithDbAsync(db => db.People.Where(p => p.Id == annId).ExecuteUpdateAsync(s => s.SetProperty(p => p.EmailsOn, false)));
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId]);

        await Post(ev, "open");

        Assert.Equal(["It's open: write your notes"], await LinesAsync(ann));
    }

    // ---------- The bell ----------

    [Theory]
    [InlineData("/")]
    [InlineData("/account")]
    [InlineData("/past")]
    [InlineData("/whats-new")]
    public async Task Every_signed_in_page_has_the_bell_linking_to_whats_new_filled_only_with_something_new(string path)
    {
        var quiet = (await ann.GetPageAsync(path)).QuerySelector(".masthead a.bell")!;
        Assert.Equal("/whats-new", quiet.GetAttribute("href"));
        Assert.Equal("What's new, nothing new", quiet.GetAttribute("aria-label"));
        Assert.False(quiet.ClassList.Contains("bell-new"));
        // The red dot is there only with something new (owner, November 27, 2026), on the link and the button alike.
        Assert.True(quiet.QuerySelector(".bell-dot")!.HasAttribute("hidden"));
        Assert.Equal("", quiet.TextContent.Trim());

        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId]);
        await Post(ev, "open");
        var one = (await ann.GetPageAsync(path)).QuerySelector(".masthead a.bell")!;
        Assert.Equal("What's new, 1 notification", one.GetAttribute("aria-label"));
        Assert.True(one.ClassList.Contains("bell-new"));
        Assert.False(one.QuerySelector(".bell-dot")!.HasAttribute("hidden"));
        Assert.False(one.ParentElement!.QuerySelector("button.bell .bell-dot")!.HasAttribute("hidden"));
        Assert.Equal("", one.TextContent.Trim());

        await Post(ev, "addorganizer", ("email", "ann@example.com"));
        var two = (await ann.GetPageAsync(path)).QuerySelector(".masthead a.bell")!;
        Assert.Equal("What's new, 2 notifications", two.GetAttribute("aria-label"));
    }

    // ---------- The bell's panel (#99): rendered into every signed-in masthead, hidden ----------

    /// <summary>The panel's rows as the masthead carries them.</summary>
    static IReadOnlyList<Row> PanelRows(AngleSharp.Html.Dom.IHtmlDocument page) =>
        [.. page.QuerySelectorAll(".masthead #whats-new-panel .bell-rows > li").Select(li => new Row(
            li.QuerySelector("a")!.TextContent.Trim(),
            li.QuerySelector("a")!.GetAttribute("href")!,
            li.QuerySelector(".bell-meta")!.TextContent.Trim()))];

    [Theory]
    [InlineData("/")]
    [InlineData("/account")]
    [InlineData("/past")]
    [InlineData("/whats-new")]
    public async Task Every_signed_in_page_carries_the_bell_button_and_its_panel_hidden_until_the_script_opens_it(string path)
    {
        var page = await ann.GetPageAsync(path);

        var button = page.QuerySelector(".masthead button.bell")!;
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.True(button.HasAttribute("hidden"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        Assert.Equal("whats-new-panel", button.GetAttribute("aria-controls"));
        Assert.Equal("What's new, nothing new", button.GetAttribute("aria-label"));
        Assert.False(button.ClassList.Contains("bell-new"));

        var panel = page.QuerySelector(".masthead #whats-new-panel")!;
        Assert.True(panel.HasAttribute("hidden"));
        Assert.Equal("What's new", panel.QuerySelector("#whats-new-panel-heading")!.TextContent.Trim());
        Assert.Equal("whats-new-panel-heading", panel.GetAttribute("aria-labelledby"));
        Assert.Empty(PanelRows(page));
        var empty = panel.QuerySelector(".bell-empty")!;
        Assert.False(empty.HasAttribute("hidden"));
        Assert.Contains("Nothing new right now.", empty.TextContent);
        Assert.Contains("When something happens in one of your events, it shows up here.", empty.TextContent);

        // Where the script says a new notification politely (spec §16), there and empty from the start.
        var say = page.QuerySelector(".masthead [data-bell-say]")!;
        Assert.Equal("polite", say.GetAttribute("aria-live"));
        Assert.Equal("", say.TextContent);
        Assert.True(say.ClassList.Contains("visually-hidden"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/account")]
    [InlineData("/whats-new")]
    public async Task The_panel_holds_the_same_rows_as_the_page_each_with_a_dismiss_x(string path)
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 1, 19, 42, 0, TimeSpan.Zero));
        var first = await app.EventAsync("Cousins' Swap", EventStatus.Draft, [me], [me, annId]);
        await Post(first, "open");
        app.Clock.Advance(TimeSpan.FromHours(1));
        await Post(first, "addorganizer", ("email", "ann@example.com"));

        var page = await ann.GetPageAsync(path);

        Assert.Equal(await RowsAsync(ann), PanelRows(page));
        Assert.Equal("What's new, 2 notifications", page.QuerySelector(".masthead button.bell")!.GetAttribute("aria-label"));
        Assert.True(page.QuerySelector(".masthead button.bell")!.ClassList.Contains("bell-new"));
        Assert.True(page.QuerySelector("#whats-new-panel .bell-empty")!.HasAttribute("hidden"));

        var rows = page.QuerySelectorAll("#whats-new-panel .bell-rows > li");
        var line = rows[0].QuerySelector("a")!.TextContent.Trim();
        var form = rows[0].QuerySelector("form")!;
        Assert.Equal("/whats-new/dismiss", form.GetAttribute("action"));
        Assert.Equal("post", form.GetAttribute("method"));
        Assert.Equal(first.ToString(), form.QuerySelector("input[name=event]")!.GetAttribute("value"));
        Assert.Equal("MadeOrganizer", form.QuerySelector("input[name=kind]")!.GetAttribute("value"));
        var x = Assert.Single(form.QuerySelectorAll("button"));
        Assert.Equal($"Dismiss: {line}", x.GetAttribute("aria-label"));
        Assert.Equal("", x.TextContent.Trim());
        Assert.NotNull(x.QuerySelector("svg[aria-hidden=true]"));
    }

    [Fact]
    public async Task Dismissing_from_the_panel_is_the_pages_own_post()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId]);
        await Post(ev, "open");

        var response = await ann.SubmitOnAsync("/account", "/whats-new/dismiss");

        Assert.Equal("/whats-new", response.Headers.Location?.OriginalString);
        Assert.Empty(PanelRows(await ann.GetPageAsync("/account")));
        Assert.Empty(await RowsAsync(ann));
    }

    [Fact]
    public async Task Signed_out_pages_have_no_bell()
    {
        using var stranger = app.NewBrowser();
        var page = await stranger.GetPageAsync("/auth/signed-out");
        Assert.Null(page.QuerySelector(".bell"));
        Assert.Null(page.QuerySelector("#whats-new-panel"));
    }

    // ---------- The page ----------

    [Fact]
    public async Task With_nothing_new_the_page_says_so()
    {
        var page = await ann.GetPageAsync("/whats-new");

        Assert.Equal("What's new", page.QuerySelector("h1")!.TextContent.Trim());
        Assert.Equal("/", page.QuerySelector(".page-head-back")!.GetAttribute("href"));
        Assert.Contains("Nothing new right now.", page.QuerySelector("main")!.TextContent);
        Assert.Contains("When something happens in one of your events, it shows up here.", page.QuerySelector("main")!.TextContent);
        Assert.Null(page.QuerySelector(".cloth"));
    }

    [Fact]
    public async Task Rows_are_newest_first_with_the_event_and_when_under_each_line()
    {
        app.Clock.SetUtcNow(new DateTimeOffset(2026, 12, 1, 19, 42, 0, TimeSpan.Zero));
        var first = await app.EventAsync("Cousins' Swap", EventStatus.Draft, [me], [me, annId]);
        await Post(first, "open");
        app.Clock.Advance(TimeSpan.FromHours(1));
        var second = await app.EventAsync("Book Club", EventStatus.Draft, [me], [me, annId]);
        await Post(second, "open");

        var rows = await RowsAsync(ann);

        Assert.Equal([$"/events/{second}", $"/events/{first}"], rows.Select(r => r.Href));
        Assert.Equal("Book Club · Today, 8:42 PM UTC", rows[0].Meta);
        Assert.Equal("Cousins' Swap · Today, 7:42 PM UTC", rows[1].Meta);
    }

    [Fact]
    public async Task Dismissing_deletes_the_row_and_a_new_happening_of_the_kind_brings_it_back()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, annId, bob]);
        await Post(ev, "draw");

        var response = await ann.SubmitOnAsync("/whats-new", "/whats-new/dismiss");

        Assert.Equal("/whats-new", response.Headers.Location?.OriginalString);
        Assert.Empty(await RowsAsync(ann));
        Assert.Contains("Nothing new right now.", (await ann.GetPageAsync("/whats-new")).QuerySelector("main")!.TextContent);

        await Post(ev, "redraw");
        await Post(ev, "draw");
        Assert.Equal(["Names were drawn again: take another look"], await LinesAsync(ann));
    }

    [Fact]
    public async Task The_dismiss_button_is_labeled_with_its_line_and_dismissing_needs_the_token()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId]);
        await Post(ev, "open");

        var page = await ann.GetPageAsync("/whats-new");
        var button = page.QuerySelector(".whats-new-rows form[action='/whats-new/dismiss'] button")!;
        Assert.Equal("Dismiss", button.TextContent.Trim());
        Assert.Equal("Dismiss: It's open: write your notes", button.GetAttribute("aria-label"));

        var forged = await ann.App.PostAsync("/whats-new/dismiss", new FormUrlEncodedContent(Form(("event", ev.ToString()), ("kind", "EventOpened"))));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Single(await RowsAsync(ann));
    }

    [Fact]
    public async Task Dismissing_only_touches_the_persons_own_row()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId, bob]);
        await Post(ev, "open");

        await pat.PostWithTokenAsync(Manage(ev), "/whats-new/dismiss", Form(("event", ev.ToString()), ("kind", "EventOpened")));

        Assert.Single(await RowsAsync(ann));
    }

    // ---------- What clears a row ----------

    [Fact]
    public async Task Seeing_the_event_page_clears_the_rows_pointing_there_and_only_those()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, annId, bob]);
        await Post(ev, "addorganizer", ("email", "ann@example.com"));
        await Post(ev, "draw");

        await ann.GetPageAsync($"/events/{ev}");

        Assert.Equal(["Pat made you an organizer"], await LinesAsync(ann));
        await ann.GetPageAsync(Manage(ev));
        Assert.Empty(await RowsAsync(ann));
    }

    [Fact]
    public async Task Untying_without_the_script_is_seeing_the_event_page()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, annId, bob]);
        await Post(ev, "draw");

        await ann.GetPageAsync($"/events/{ev}/untie");

        Assert.Empty(await RowsAsync(ann));
    }

    [Fact]
    public async Task Organizing_clears_the_reveal_for_an_organizer_who_doesnt_take_part_and_the_event_page_for_one_who_does()
    {
        var (olgaBrowser, olga) = await app.SignInAsync(new MockIdentity("olga", "Olga", "olga@example.com", true, null));
        using var _ = olgaBrowser;
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me, olga, annId], [me, annId, bob]);
        await app.SealAsync(ev, Loop(me, annId, bob));
        await Post(ev, "reveal", ("confirmed", "true"));

        await olgaBrowser.GetPageAsync(Manage(ev));
        await ann.GetPageAsync(Manage(ev));

        Assert.Empty(await RowsAsync(olgaBrowser));
        Assert.Equal(["See who drew whom"], await LinesAsync(ann));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("archive")]
    public async Task Canceling_or_archiving_deletes_every_row_of_the_event(string action)
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, annId, bob]);
        var other = await app.EventAsync("Other", EventStatus.Draft, [me], [me, annId]);
        await Post(ev, "draw");
        await Post(other, "open");

        await Post(ev, action, ("keepMessages", "false"));

        Assert.Equal([$"/events/{other}"], (await RowsAsync(ann)).Select(r => r.Href));
    }

    [Fact]
    public async Task Being_taken_out_deletes_the_persons_rows_for_that_event()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId, bob]);
        await Post(ev, "open");

        await Post(ev, "removeperson", ("personId", annId.ToString()));

        Assert.Empty(await RowsAsync(ann));
        Assert.Single(await app.WithDbAsync(db => db.InAppNotifications.Where(n => n.PersonId == bob).ToListAsync()));
    }

    [Fact]
    public async Task No_longer_organizing_deletes_the_organizer_rows_but_not_those_of_taking_part()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId, bob]);
        await Post(ev, "addorganizer", ("email", "ann@example.com"));
        await Post(ev, "open");

        await Post(ev, "removeorganizer", ("personId", annId.ToString()));

        Assert.Equal(["It's open: write your notes"], await LinesAsync(ann));
    }

    [Fact]
    public async Task Deleting_an_account_deletes_every_row_the_person_had()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, annId, bob]);
        await Post(ev, "open");
        var revealed = await app.EventAsync("Last year", EventStatus.Open, [me], [me, annId, bob]);
        await app.SealAsync(revealed, Loop(me, annId, bob));
        await Post(revealed, "reveal", ("confirmed", "true"));
        Assert.Equal(2, (await RowsAsync(ann)).Count);

        await ann.SubmitOnAsync("/account/delete", "/account/delete");

        Assert.Empty(await app.WithDbAsync(db => db.InAppNotifications.Where(n => n.PersonId == annId).ToListAsync()));
    }

    [Fact]
    public async Task A_redraw_needed_row_goes_once_the_request_is_withdrawn()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, annId, bob]);
        await app.SealAsync(ev, Loop(me, annId, bob));
        await LeaveAsync(ann, ev);
        Assert.Single(await RowsAsync(pat));

        await ann.SubmitOnAsync($"/events/{ev}", $"/events/{ev}/stay");

        Assert.Empty(await RowsAsync(pat));
    }

    [Fact]
    public async Task A_redraw_needed_row_goes_once_the_redraw_is_done()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, annId, bob, cy]);
        await app.SealAsync(ev, new Dictionary<Guid, Guid> { [me] = annId, [annId] = me, [bob] = cy, [cy] = bob });
        await LeaveAsync(ann, ev);
        Assert.Single(await RowsAsync(pat));

        await Post(ev, "redraw");

        Assert.Empty(await RowsAsync(pat));
    }

    /// <summary>Leaves a drawn event, confirming twice.</summary>
    static async Task LeaveAsync(TestBrowser browser, Guid ev)
    {
        var second = await TestBrowser.ParseAsync(await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave"));
        await browser.SubmitAsync(second, $"/events/{ev}/leave");
    }
}
