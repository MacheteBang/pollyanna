using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>The organizer's manage page (spec §4, §7): people, organizers, households, rules, details, status.</summary>
public sealed class ManageTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    string Manage(Guid ev) => $"/events/{ev}/manage";

    Task<HttpResponseMessage> ActAsync(Guid ev, string handler, IEnumerable<KeyValuePair<string, string>>? fields = null, Func<AngleSharp.Html.Dom.IHtmlFormElement, bool>? match = null) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/{handler}", fields, match);

    async Task<string?> NoticeAsync(Guid ev)
    {
        var page = await browser.GetPageAsync(Manage(ev));
        return (page.QuerySelector(".notice-error") ?? page.QuerySelector(".notice"))?.TextContent;
    }

    static Func<AngleSharp.Html.Dom.IHtmlFormElement, bool> With(string name, Guid value) =>
        f => f.QuerySelector($"input[name='{name}'][value='{value}']") is not null;

    [Fact]
    public async Task Page_shows_people_with_notes_households_invitations_and_organizers()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob");
        var ev = await app.EventAsync("Cousins' Swap", EventStatus.Open, [me], [me, ann, bob], budget: "$15");
        await app.WithDbAsync(async db =>
        {
            var h = new Household { EventId = ev, Name = "Mom & Dad" };
            db.Households.Add(h);
            foreach (var p in await db.Participants.Where(p => p.EventId == ev && p.PersonId != me).ToListAsync())
                p.HouseholdId = h.Id;
            (await db.Participants.SingleAsync(p => p.EventId == ev && p.PersonId == ann)).Notes = "Socks";
            db.Invitations.Add(new Invitation { EventId = ev, Email = "sue@example.com", EmailNormalized = "sue@example.com", InvitedBy = me });
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Equal("Cousins' Swap", page.QuerySelector("h1")!.TextContent);
        Assert.Equal("OPEN", page.QuerySelector("#status .stamp")!.TextContent);
        var people = page.QuerySelectorAll("#people > ul:first-of-type > li").Select(li => li.TextContent).ToList();
        Assert.Equal(3, people.Count);
        Assert.Contains("Ann", people[0]);
        Assert.Contains("Mom & Dad", people[0]);
        Assert.Contains("has written notes", people[0]);
        Assert.Contains("no notes yet", people[1]);
        Assert.Contains("Pat (you)", people[2]);
        Assert.Contains("no household", people[2]);
        Assert.Contains("sue@example.com", page.QuerySelector("#people")!.TextContent);
        Assert.Contains("Ann, Bob", page.QuerySelector(".household")!.TextContent);
        Assert.Contains("You're the only organizer", page.QuerySelector("#organizers")!.TextContent);
        // The pick lists offer only people who aren't in yet.
        Assert.DoesNotContain(page.QuerySelectorAll("#add-person option"), o => o.GetAttribute("value") is { Length: > 0 });
        Assert.Contains("Ann — ann@example.com", page.QuerySelector("#add-organizer")!.TextContent);
    }

    [Fact]
    public async Task Adding_and_removing_a_participant()
    {
        var carol = await app.PersonAsync("Carol", "carol@example.com");
        var dave = await app.PersonAsync("Dave");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, dave]);
        await app.WithDbAsync(async db =>
        {
            db.Exclusions.Add(Exclusion.Between(ev, me, dave));
            await db.SaveChangesAsync();
        });

        await ActAsync(ev, "addperson", Form(("personId", carol.ToString())));
        Assert.Contains(carol, await app.ParticipantsAsync(ev));
        Assert.Equal("Added Carol.", await NoticeAsync(ev));

        await ActAsync(ev, "addperson", Form(("personId", carol.ToString())));
        Assert.Contains("already in the hat", await NoticeAsync(ev));

        // Removing someone takes their rules with them.
        await ActAsync(ev, "removeperson", match: With("personId", dave));
        Assert.DoesNotContain(dave, await app.ParticipantsAsync(ev));
        Assert.Equal(0, await app.WithDbAsync(db => db.Exclusions.CountAsync()));
    }

    [Fact]
    public async Task People_who_never_signed_in_or_deleted_their_account_cant_be_picked()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);
        var tombstone = new Person { CreatedAt = DateTime.UtcNow };
        await app.WithDbAsync(async db => { db.People.Add(tombstone); await db.SaveChangesAsync(); });

        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addperson", Form(("personId", tombstone.Id.ToString())));
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addperson", Form(("personId", Guid.NewGuid().ToString())));

        Assert.Equal([me], await app.ParticipantsAsync(ev));
    }

    [Fact]
    public async Task Organizers_can_be_added_and_removed_but_never_the_last_one()
    {
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], []);

        // The only organizer can't step down.
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/removeorganizer']"));
        await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/removeorganizer", Form(("personId", me.ToString())));
        Assert.Equal([me], await app.OrganizersAsync(ev));
        Assert.Contains("at least one organizer", await NoticeAsync(ev));

        await ActAsync(ev, "addorganizer", Form(("personId", carol.ToString())));
        Assert.Equal(new[] { me, carol }.Order(), (await app.OrganizersAsync(ev)).Order());
        // Being an organizer doesn't put Carol in the hat.
        Assert.Empty(await app.ParticipantsAsync(ev));

        await ActAsync(ev, "removeorganizer", match: With("personId", carol));
        Assert.Equal([me], await app.OrganizersAsync(ev));
    }

    [Fact]
    public async Task Stepping_down_as_organizer_of_a_draft_leaves_it_out_of_sight()
    {
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me, carol], [me]);

        var response = await ActAsync(ev, "removeorganizer", match: With("personId", me));
        Assert.Equal("/", response.Headers.Location!.OriginalString);
        Assert.Equal([carol], await app.OrganizersAsync(ev));
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(Manage(ev))).StatusCode);
    }

    [Fact]
    public async Task Households_are_created_edited_and_removed_and_hold_each_person_once()
    {
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        var lee = await app.PersonAsync("Lee");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann, bob, lee]);
        Task<Dictionary<Guid, string?>> HouseholdsAsync() => app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev)
            .ToDictionaryAsync(p => p.PersonId, p => db.Households.Where(h => h.Id == p.HouseholdId).Select(h => h.Name).SingleOrDefault()));

        await ActAsync(ev, "savehousehold", [new("householdName", "Mom & Dad"), new("members", ann.ToString()), new("members", bob.ToString())],
            match: f => f.QuerySelector("input[name='householdId']") is null);
        await ActAsync(ev, "savehousehold", [new("householdName", "The Parks"), new("members", lee.ToString()), new("members", bob.ToString())],
            match: f => f.QuerySelector("input[name='householdId']") is null);

        var households = await HouseholdsAsync();
        Assert.Equal("Mom & Dad", households[ann]);
        Assert.Equal("The Parks", households[bob]); // moved: at most one household per event
        Assert.Equal("The Parks", households[lee]);
        Assert.Null(households[me]);

        // Rename the Parks and take Lee out.
        var parks = await app.WithDbAsync(db => db.Households.Where(h => h.Name == "The Parks").Select(h => h.Id).SingleAsync());
        await ActAsync(ev, "savehousehold", [new("householdName", "Parks"), new("members", bob.ToString())], match: With("householdId", parks));
        households = await HouseholdsAsync();
        Assert.Equal("Parks", households[bob]);
        Assert.Null(households[lee]);

        await ActAsync(ev, "deletehousehold", match: With("householdId", parks));
        households = await HouseholdsAsync();
        Assert.Null(households[bob]);
        Assert.Equal(1, await app.WithDbAsync(db => db.Households.CountAsync()));
    }

    [Fact]
    public async Task Household_members_must_be_participants_of_that_event()
    {
        var outsider = await app.PersonAsync("Outsider");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me]);
        var other = await app.EventAsync("Other", EventStatus.Open, [me], [outsider]);

        await ActAsync(ev, "savehousehold", [new("householdName", "Us"), new("members", outsider.ToString())]);

        Assert.Null(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == other).Select(p => p.HouseholdId).SingleAsync()));
    }

    [Fact]
    public async Task Extra_rules_are_symmetric_unique_and_between_participants()
    {
        var carol = await app.PersonAsync("Carol");
        var bob = await app.PersonAsync("Bob");
        var outsider = await app.PersonAsync("Outsider");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, carol, bob]);

        await ActAsync(ev, "addrule", Form(("personA", carol.ToString()), ("personB", bob.ToString())));
        await ActAsync(ev, "addrule", Form(("personA", bob.ToString()), ("personB", carol.ToString())));
        Assert.Contains("already", await NoticeAsync(ev));
        await ActAsync(ev, "addrule", Form(("personA", carol.ToString()), ("personB", carol.ToString())));
        await ActAsync(ev, "addrule", Form(("personA", carol.ToString()), ("personB", outsider.ToString())));

        var rule = Assert.Single(await app.WithDbAsync(db => db.Exclusions.ToListAsync()));
        Assert.Equal(new[] { carol, bob }.Order(), new[] { rule.PersonA, rule.PersonB }.Order());
        var page = await browser.GetPageAsync(Manage(ev));
        Assert.Contains("(both ways)", page.QuerySelector("#rules")!.TextContent);

        await ActAsync(ev, "removerule");
        Assert.Equal(0, await app.WithDbAsync(db => db.Exclusions.CountAsync()));
    }

    [Fact]
    public async Task Details_can_be_edited_and_bad_input_is_shown_again()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me]);

        await ActAsync(ev, "details", Form(("Name", " Stockings 2027 "), ("BudgetText", "$30"), ("ExchangeDate", "2027-01-06"), ("PreviousEventId", ""), ("NoRepeatDepth", "1")));
        var saved = await app.WithDbAsync(db => db.Events.SingleAsync(e => e.Id == ev));
        Assert.Equal(("Stockings 2027", "$30", new DateOnly(2027, 1, 6), 2027), (saved.Name, saved.BudgetText, saved.ExchangeDate, saved.Year));

        var response = await ActAsync(ev, "details", Form(("Name", ""), ("BudgetText", "$99"), ("ExchangeDate", "2027-01-06"), ("PreviousEventId", ""), ("NoRepeatDepth", "1")));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await TestBrowser.ParseAsync(response);
        Assert.Contains("Give the event a name.", page.QuerySelector("#details")!.TextContent);
        Assert.Equal("$99", page.QuerySelector("#budget")!.GetAttribute("value"));
        Assert.Equal("Stockings 2027", await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).Select(e => e.Name).SingleAsync()));
    }

    [Fact]
    public async Task The_earlier_event_must_be_one_I_organize_and_never_makes_a_loop()
    {
        var other = await app.PersonAsync("Other");
        var y2024 = await app.EventAsync("2024", EventStatus.Draft, [me], [me]);
        var y2025 = await app.EventAsync("2025", EventStatus.Archived, [me], [me]);
        var theirs = await app.EventAsync("Theirs", EventStatus.Archived, [other], [me]);
        var ev = await app.EventAsync("2026", EventStatus.Draft, [me], [me]);
        await app.WithDbAsync(async db =>
        {
            (await db.Events.SingleAsync(e => e.Id == y2025)).PreviousEventId = y2024;
            await db.SaveChangesAsync();
        });
        async Task<Guid?> LinkAsync(Guid target, Guid? to)
        {
            await browser.PostWithTokenAsync(Manage(target), $"{Manage(target)}/details",
                Form(("Name", "Same"), ("ExchangeDate", "2026-12-24"), ("PreviousEventId", to?.ToString() ?? ""), ("NoRepeatDepth", "2")));
            return await app.WithDbAsync(db => db.Events.Where(e => e.Id == target).Select(e => e.PreviousEventId).SingleAsync());
        }

        Assert.Equal(y2025, await LinkAsync(ev, y2025));
        Assert.Equal(y2025, await LinkAsync(ev, theirs)); // not theirs to link
        Assert.Equal(y2025, await LinkAsync(ev, ev));     // not itself
        // 2024 can't point at 2026, which already leads back to 2024.
        Assert.Null(await LinkAsync(y2024, ev));
        var options = (await browser.GetPageAsync(Manage(y2024))).QuerySelectorAll("#previous option").Select(o => o.TextContent).ToList();
        Assert.Equal(["None"], options);
    }

    [Fact]
    public async Task Opening_a_draft_lets_participants_see_it()
    {
        var (carolBrowser, carol) = await app.SignInAsync(new MockIdentity("carol", "Carol", "carol@example.com", true, null));
        using var _c = carolBrowser;
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, carol]);
        Assert.Equal(HttpStatusCode.NotFound, (await carolBrowser.GetAsync($"/events/{ev}")).StatusCode);

        await ActAsync(ev, "open");

        Assert.Equal(EventStatus.Open, await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).Select(e => e.Status).SingleAsync()));
        Assert.Equal(HttpStatusCode.OK, (await carolBrowser.GetAsync($"/events/{ev}")).StatusCode);
        Assert.Contains("OPEN", (await carolBrowser.GetPageAsync("/")).QuerySelector(".stamp")!.TextContent);
        Assert.Null((await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{Manage(ev)}/open']"));
    }
}
