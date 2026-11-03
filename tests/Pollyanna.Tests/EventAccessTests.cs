using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// The visibility rules (spec §4, §9): non-organizers can't manage an event, and people outside
/// an event can't see it. Refusals look like "not found", so they don't reveal the event exists.
/// </summary>
public sealed class EventAccessTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    static readonly MockIdentity MeIdentity = new("me", "Me", "me@example.com", true, null);

    /// <summary>Every management action, with fields that would succeed for an organizer.</summary>
    static IEnumerable<(string Handler, Dictionary<string, string> Fields)> ManageActions(Guid someone, Guid participant, Guid other) =>
    [
        ("open", Form()),
        ("addperson", Form(("personId", someone.ToString()))),
        ("invite", Form(("email", "new@example.com"))),
        ("removeperson", Form(("personId", participant.ToString()))),
        ("cancelinvite", Form(("invitationId", Guid.NewGuid().ToString()))),
        ("savehousehold", Form(("householdName", "Sneaky"), ("members", participant.ToString()))),
        ("deletehousehold", Form(("householdId", Guid.NewGuid().ToString()))),
        ("addrule", Form(("personA", participant.ToString()), ("personB", other.ToString()))),
        ("removerule", Form(("exclusionId", Guid.NewGuid().ToString()))),
        ("addorganizer", Form(("personId", someone.ToString()))),
        ("removeorganizer", Form(("personId", someone.ToString()))),
        ("details", Form(("Name", "Hijacked"), ("ExchangeDate", "2026-12-01"), ("NoRepeatDepth", "1"))),
    ];

    [Theory]
    [InlineData(true)]   // a participant who doesn't organize
    [InlineData(false)]  // someone outside the event
    public async Task Non_organizers_cant_see_the_manage_page_or_use_any_of_its_actions(bool participant)
    {
        var (browser, me) = await app.SignInAsync(MeIdentity);
        using var _b = browser;
        var organizer = await app.PersonAsync("Org");
        var carol = await app.PersonAsync("Carol");
        var dave = await app.PersonAsync("Dave");
        var outsider = await app.PersonAsync("Outsider");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [organizer], participant ? [me, carol, dave] : [carol, dave]);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/events/{ev}/manage")).StatusCode);

        foreach (var (handler, fields) in ManageActions(outsider, carol, dave))
        {
            var response = await browser.PostWithTokenAsync("/account", $"/events/{ev}/manage/{handler}", fields);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{handler} answered {response.StatusCode}");
        }

        var after = await app.WithDbAsync(db => db.Events.AsNoTracking()
            .Include(e => e.Participants).Include(e => e.Organizers).Include(e => e.Invitations).Include(e => e.Households).Include(e => e.Exclusions)
            .SingleAsync(e => e.Id == ev));
        Assert.Equal("Stockings", after.Name);
        Assert.Equal(EventStatus.Draft, after.Status);
        Assert.Equal(participant ? 3 : 2, after.Participants.Count);
        Assert.Equal([organizer], after.Organizers.Select(o => o.PersonId));
        Assert.Empty(after.Invitations);
        Assert.Empty(after.Households);
        Assert.Empty(after.Exclusions);
    }

    [Fact]
    public async Task Organizers_can_use_the_same_actions()
    {
        var (browser, me) = await app.SignInAsync(MeIdentity);
        using var _b = browser;
        var someone = await app.PersonAsync("Someone");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        var page = await browser.GetPageAsync($"/events/{ev}/manage");
        var response = await browser.SubmitAsync(page, $"/events/{ev}/manage/addperson", Form(("personId", someone.ToString())));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains(someone, await app.ParticipantsAsync(ev));
    }

    [Fact]
    public async Task Management_actions_need_a_form_token()
    {
        var (browser, me) = await app.SignInAsync(MeIdentity);
        using var _b = browser;
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        var response = await browser.App.PostAsync($"/events/{ev}/manage/open", new FormUrlEncodedContent(Form()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(EventStatus.Draft, await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).Select(e => e.Status).SingleAsync()));
    }

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.Drawn)]
    [InlineData(EventStatus.Archived)]
    public async Task People_outside_an_event_cant_see_it_in_any_status(EventStatus status)
    {
        var (browser, _) = await app.SignInAsync(MeIdentity);
        using var _b = browser;
        var org = await app.PersonAsync("Org");
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Secret Swap", status, [org], [org, carol]);

        foreach (var path in new[] { $"/events/{ev}", $"/events/{ev}/manage", $"/events/{ev}/leave" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain("Secret Swap", await response.Content.ReadAsStringAsync());
        }
        foreach (var action in new[] { $"/events/{ev}/notes", $"/events/{ev}/leave" })
            Assert.Equal(HttpStatusCode.NotFound, (await browser.PostWithTokenAsync("/account", action, Form(("notes", "hi")))).StatusCode);
        Assert.DoesNotContain("Secret Swap", (await browser.GetPageAsync("/")).Body!.TextContent);
    }

    [Fact]
    public async Task Participants_cant_see_a_draft_until_it_is_opened()
    {
        var (browser, me) = await app.SignInAsync(MeIdentity);
        using var _b = browser;
        var org = await app.PersonAsync("Org");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [org], [me]);

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/events/{ev}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.PostWithTokenAsync("/account", $"/events/{ev}/notes", Form(("notes", "early")))).StatusCode);

        await app.WithDbAsync(async db => { (await db.Events.SingleAsync(e => e.Id == ev)).Status = EventStatus.Open; await db.SaveChangesAsync(); });
        var page = await browser.GetPageAsync($"/events/{ev}");
        Assert.Equal("Stockings", page.QuerySelector("h1")!.TextContent);
        Assert.Null(page.QuerySelector($"a[href='/events/{ev}/manage']"));
    }

    [Fact]
    public async Task An_organizer_who_isnt_taking_part_is_sent_to_the_manage_page()
    {
        var (browser, me) = await app.SignInAsync(MeIdentity);
        using var _b = browser;
        var carol = await app.PersonAsync("Carol");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [carol]);

        var response = await browser.GetAsync($"/events/{ev}");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal($"/events/{ev}/manage", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Organizers_adding_people_can_load_anyones_picture_but_others_cant()
    {
        var (browser, me) = await app.SignInAsync(MeIdentity);
        using var _b = browser;
        var stranger = await app.PersonAsync("Stranger");
        await app.WithDbAsync(async db =>
        {
            db.PersonPictures.Add(new PersonPicture { PersonId = stranger, Data = TestImages.Png, ContentType = "image/png" });
            await db.SaveChangesAsync();
        });

        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/people/{stranger}/picture")).StatusCode);

        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], []);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync($"/people/{stranger}/picture")).StatusCode);

        // Once people can't be added any more, the extra access ends.
        await app.WithDbAsync(async db => { (await db.Events.SingleAsync(e => e.Id == ev)).Status = EventStatus.Archived; await db.SaveChangesAsync(); });
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync($"/people/{stranger}/picture")).StatusCode);
    }
}
