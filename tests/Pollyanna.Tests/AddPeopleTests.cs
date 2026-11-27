using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The add-people control on the manage page (spec §4, §7): a ticklist of the people the
/// organizer has shared an event with, names and pictures only, and an address that adds or invites.
/// </summary>
public sealed class AddPeopleTests : IAsyncLifetime
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

    static string Manage(Guid ev) => $"/events/{ev}/manage";
    static string AddPeople(Guid ev) => $"/events/{ev}/manage/addpeople";

    Task<HttpResponseMessage> AddAsync(Guid ev, params (string Key, string Value)[] fields) =>
        browser.SubmitOnAsync(Manage(ev), AddPeople(ev), fields.Select(f => KeyValuePair.Create(f.Key, f.Value)));

    async Task<string?> NoticeAsync(Guid ev)
    {
        var page = await browser.GetPageAsync(Manage(ev));
        return (page.QuerySelector(".notice-error") ?? page.QuerySelector(".notice"))?.TextContent;
    }

    async Task<AngleSharp.Dom.IElement> ControlAsync(Guid ev) =>
        (await browser.GetPageAsync(Manage(ev))).QuerySelector($"form[action='{AddPeople(ev)}']")
        ?? throw new InvalidOperationException("No add-people form.");

    async Task<Guid> TombstoneAsync()
    {
        var tombstone = new Person { CreatedAt = DateTime.UtcNow };
        await app.WithDbAsync(async db => { db.People.Add(tombstone); await db.SaveChangesAsync(); });
        return tombstone.Id;
    }

    [Fact]
    public async Task Lists_people_from_shared_events_at_any_status_and_nobody_else()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var eve = await app.PersonAsync("Eve", "eve@example.com");
        var carol = await app.PersonAsync("Carol", "carol@example.com");
        var dave = await app.PersonAsync("Dave", "dave@example.com");
        var gone = await TombstoneAsync();
        // Ann took part with Pat in an archived exchange; Bob organizes one Pat is in; Eve is in one Pat only organizes.
        await app.EventAsync("Old", EventStatus.Archived, [carol], [me, ann, gone]);
        await app.EventAsync("Bob's", EventStatus.Draft, [bob], [me]);
        await app.EventAsync("Pat's", EventStatus.Revealed, [me], [eve, dave]);
        // Carol organizes an event Pat isn't in: she still counts, through "Old".
        var stranger = await app.PersonAsync("Stranger", "stranger@example.com");
        await app.EventAsync("Elsewhere", EventStatus.Open, [stranger], [stranger]);
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me, dave]);

        var control = await ControlAsync(ev);

        var names = control.QuerySelectorAll("input[name=personIds]")
            .Select(i => control.QuerySelector($"label[for='{i.Id}']")!.TextContent.Trim()).ToList();
        Assert.Equal(["Ann", "Bob", "Carol", "Eve"], names);
        Assert.Equal(new[] { ann, bob, carol, eve }.Select(g => g.ToString()),
            control.QuerySelectorAll("input[name=personIds]").Select(i => i.GetAttribute("value")));
        foreach (var name in new[] { "ann", "bob", "carol", "eve", "dave", "stranger", "pat" })
            Assert.DoesNotContain($"{name}@example.com", control.OuterHtml);
    }

    [Fact]
    public async Task Several_ticked_people_and_an_address_are_added_in_one_post()
    {
        var ann = await app.PersonAsync("Ann");
        var bob = await app.PersonAsync("Bob");
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, ann, bob]);
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        var response = await AddAsync(ev, ("personIds", ann.ToString()), ("personIds", bob.ToString()), ("email", "Sue@Example.com"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(new[] { me, ann, bob }.Order(), (await app.ParticipantsAsync(ev)).Order());
        var invitation = await app.WithDbAsync(db => db.Invitations.SingleAsync(i => i.EventId == ev));
        Assert.Equal("Sue@Example.com", invitation.Email);
        var notice = await NoticeAsync(ev);
        Assert.Contains("Added Ann and Bob.", notice);
        Assert.Contains("Invited Sue@Example.com.", notice);
    }

    [Fact]
    public async Task An_address_that_belongs_to_one_person_adds_them_even_if_Pat_doesnt_know_them()
    {
        var carol = await app.PersonAsync("Carol", "carol@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        await AddAsync(ev, ("email", "CAROL@example.com"));

        Assert.Equal(new[] { me, carol }.Order(), (await app.ParticipantsAsync(ev)).Order());
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
        Assert.Equal("Carol has already signed in, so they're taking part now.", await NoticeAsync(ev));
    }

    [Fact]
    public async Task An_address_that_belongs_to_several_people_stores_nothing_and_adds_nobody()
    {
        var ann = await app.PersonAsync("Ann");
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, ann]);
        await app.PersonAsync("Carol", "family@example.com");
        await app.PersonAsync("Carl", "Family@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        await AddAsync(ev, ("personIds", ann.ToString()), ("email", "family@example.com"));

        Assert.Equal([me], await app.ParticipantsAsync(ev));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
        var notice = await NoticeAsync(ev);
        Assert.Contains("More than one person uses that address", notice);
        Assert.DoesNotContain("Carol", notice);
        Assert.DoesNotContain("Carl", notice);
    }

    [Fact]
    public async Task Ticking_someone_outside_the_list_is_refused_and_changes_nothing()
    {
        var ann = await app.PersonAsync("Ann");
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, ann]);
        var stranger = await app.PersonAsync("Stranger");
        var gone = await TombstoneAsync();
        await app.EventAsync("Old", EventStatus.Archived, [me], [me, gone]);
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        foreach (var outsider in new[] { stranger, gone, Guid.NewGuid() })
        {
            var response = await browser.PostWithTokenAsync(Manage(ev), AddPeople(ev),
                [new("personIds", ann.ToString()), new("personIds", outsider.ToString()), new("email", "sue@example.com")]);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Equal([me], await app.ParticipantsAsync(ev));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
    }

    [Fact]
    public async Task Only_organizers_can_add_people_and_only_before_the_draw()
    {
        var ann = await app.PersonAsync("Ann");
        var org = await app.PersonAsync("Org");
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, ann]);
        var theirs = await app.EventAsync("Theirs", EventStatus.Open, [org], [me, org]);

        var response = await browser.PostWithTokenAsync("/account", AddPeople(theirs), [new("personIds", ann.ToString())]);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(ann, await app.ParticipantsAsync(theirs));

        var a = await app.PersonAsync("A");
        var b = await app.PersonAsync("B");
        var drawn = await app.EventAsync("Drawn", EventStatus.Open, [me], [me, a, b]);
        await app.SealAsync(drawn, Loop(me, a, b));
        Assert.Null((await browser.GetPageAsync(Manage(drawn))).QuerySelector($"form[action='{AddPeople(drawn)}']"));
        await browser.PostWithTokenAsync(Manage(drawn), AddPeople(drawn), [new("personIds", ann.ToString())]);
        Assert.DoesNotContain(ann, await app.ParticipantsAsync(drawn));
    }

    [Fact]
    public async Task Works_as_a_plain_form_post()
    {
        // The form itself holds a visible email field and the ticklist; the address row the
        // script reveals is disabled, so without the script it never posts.
        var ann = await app.PersonAsync("Ann");
        await app.EventAsync("Last year", EventStatus.Archived, [me], [me, ann]);
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        var control = await ControlAsync(ev);
        var email = control.QuerySelectorAll("input[name=email]").ToList();
        Assert.Single(email, e => !e.HasAttribute("disabled") && e.GetAttribute("type") == "email" && !e.HasAttribute("hidden")
            && e.Closest("[hidden]") is null && control.QuerySelector($"label[for='{e.Id}']") is not null);
        Assert.All(email.Where(e => e.GetAttribute("type") == "checkbox"), e => Assert.True(e.HasAttribute("disabled")));
        Assert.NotNull(control.QuerySelector("button[type=submit]"));

        await AddAsync(ev, ("personIds", ann.ToString()), ("email", ""));
        Assert.Contains(ann, await app.ParticipantsAsync(ev));
    }

    [Fact]
    public async Task The_old_add_and_invite_actions_are_gone_before_the_draw()
    {
        var stranger = await app.PersonAsync("Stranger");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        Assert.Equal(HttpStatusCode.NotFound,
            (await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/addperson", Form(("personId", stranger.ToString())))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await browser.PostWithTokenAsync(Manage(ev), $"{Manage(ev)}/invite", Form(("email", "sue@example.com")))).StatusCode);
        Assert.Equal([me], await app.ParticipantsAsync(ev));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
    }
}
