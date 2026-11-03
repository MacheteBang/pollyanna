using System.Net;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Deleting one's account (spec §4, §9): blocked while the last organizer of an event that isn't
/// archived, the leaving rules for every live event (all or nothing), and the tombstone that
/// keeps revealed lists, kept messages and the no-repeat rule working as "Former participant".
/// </summary>
public sealed class AccountDeletionTests : IAsyncLifetime
{
    static readonly MockIdentity Pat = new("pat", "Pat", "pat@example.com", true, null);

    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid org, ann, bob, carol, dave;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync();
        (browser, me) = await app.SignInAsync(Pat);
        org = await app.PersonAsync("Org");
        ann = await app.PersonAsync("Ann");
        bob = await app.PersonAsync("Bob");
        carol = await app.PersonAsync("Carol");
        dave = await app.PersonAsync("Dave");
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    Task<HttpResponseMessage> DeleteAsync() => browser.PostWithTokenAsync("/account/delete", "/account/delete");

    Task<Person> MeAsync() => app.WithDbAsync(db => db.People.AsNoTracking().SingleAsync(p => p.Id == me));

    async Task<Guid> DrawnAsync(string name, Guid[] people, Dictionary<Guid, Guid> pairs, EventStatus status = EventStatus.Drawn)
    {
        var ev = await app.EventAsync(name, EventStatus.Open, [org], people);
        await app.SealAsync(ev, pairs);
        if (status != EventStatus.Drawn)
            await app.WithDbAsync(db => db.Events.Where(e => e.Id == ev).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, status).SetProperty(e => e.PairingsRevealed, true)));
        return ev;
    }

    Task NotesAsync(Guid ev, Guid person, string notes) =>
        app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == person)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, notes)));

    [Fact]
    public async Task Deletion_waits_while_the_only_organizer_of_an_event_that_isnt_archived()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me, ann]);

        var page = await browser.GetPageAsync("/account/delete");
        Assert.Contains("only organizer of Stockings", page.QuerySelector("main")!.TextContent);
        Assert.Null(page.QuerySelector("form[action='/account/delete']"));

        await DeleteAsync();
        Assert.Equal("Pat", (await MeAsync()).DisplayName);
        Assert.Contains("Add another organizer first", (await browser.GetPageAsync("/account/delete")).QuerySelector(".notice-error")!.TextContent);

        // With a co-organizer it goes ahead, and drops the role.
        await app.WithDbAsync(async db => { db.Organizers.Add(new Organizer { EventId = ev, PersonId = org }); await db.SaveChangesAsync(); });
        Assert.Equal("/auth/deleted", (await DeleteAsync()).Headers.Location!.OriginalString);
        Assert.Equal([org], await app.OrganizersAsync(ev));
        Assert.Equal([ann], await app.ParticipantsAsync(ev));
    }

    [Fact]
    public async Task An_archived_event_doesnt_block_deletion()
    {
        var ev = await app.EventAsync("Last year", EventStatus.Archived, [me], [ann, bob, carol]);

        Assert.NotNull((await browser.GetPageAsync("/account/delete")).QuerySelector("form[action='/account/delete']"));
        await DeleteAsync();
        Assert.Null((await MeAsync()).OidcSub);
        Assert.Empty(await app.OrganizersAsync(ev));
    }

    [Fact]
    public async Task Deleting_leaves_live_events_and_leaves_an_empty_tombstone()
    {
        var open = await app.EventAsync("Open one", EventStatus.Open, [org], [me, ann]);
        var drawn = await DrawnAsync("Drawn one", [me, ann, bob, carol, dave], Loop(me, ann, bob, carol, dave));
        var revealed = await DrawnAsync("Revealed one", [me, ann, bob], Loop(me, ann, bob), EventStatus.Revealed);
        var archived = await DrawnAsync("Archived one", [me, ann, bob], Loop(me, ann, bob), EventStatus.Archived);
        var coOrganized = await app.EventAsync("Co-organized", EventStatus.Draft, [me, org], []);
        foreach (var ev in new[] { open, drawn, revealed, archived })
            await NotesAsync(ev, me, "Size M, no wool");
        await app.WithDbAsync(async db =>
        {
            db.PersonPictures.Add(new PersonPicture { PersonId = me, Data = TestImages.Png, ContentType = "image/png" });
            await db.SaveChangesAsync();
        });

        var page = await browser.GetPageAsync("/account/delete");
        var text = page.QuerySelector("main")!.TextContent;
        Assert.Contains("You leave Open one.", text);
        Assert.Contains("You also leave Drawn one", text);

        Assert.Equal("/auth/deleted", (await DeleteAsync()).Headers.Location!.OriginalString);

        var person = await MeAsync();
        Assert.Null(person.OidcIssuer);
        Assert.Null(person.OidcSub);
        Assert.Null(person.DisplayName);
        Assert.Null(person.Email);
        Assert.Null(person.EmailNormalized);
        Assert.Null(person.EmailVerified);
        Assert.Null(person.PictureUrl);
        Assert.Null(person.LastLoginAt);
        Assert.False(await app.WithDbAsync(db => db.PersonPictures.AnyAsync(p => p.PersonId == me)));
        Assert.False(await app.WithDbAsync(db => db.Sessions.AnyAsync(s => s.PersonId == me)));
        Assert.False(await app.WithDbAsync(db => db.Organizers.AnyAsync(o => o.PersonId == me)));
        Assert.Equal([org], await app.OrganizersAsync(coOrganized));

        // Live events: out, the drawn one by the patch (Dave now gives to Ann).
        Assert.DoesNotContain(me, await app.ParticipantsAsync(open));
        Assert.DoesNotContain(me, await app.ParticipantsAsync(drawn));
        Assert.Equal(Loop(ann, bob, carol, dave), await app.PairsAsync(drawn));
        Assert.True((await app.AssignmentOfAsync(drawn, dave)).RecipientChanged);

        // Events that are over: still in, without notes, and the pairings still read.
        foreach (var ev in new[] { revealed, archived })
        {
            Assert.Contains(me, await app.ParticipantsAsync(ev));
            Assert.Equal("", await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev && p.PersonId == me).Select(p => p.Notes).SingleAsync()));
            Assert.Equal(Loop(me, ann, bob), await app.PairsAsync(ev));
        }

        // The old session is gone.
        var after = await browser.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/auth/login", after.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Deletion_is_all_or_nothing_when_a_drawn_event_cant_patch_them_out()
    {
        var open = await app.EventAsync("Open one", EventStatus.Open, [org], [me, ann]);
        // Only three people: taking Pat out would leave too few.
        var stuck = await DrawnAsync("Small one", [me, ann, bob], Loop(me, ann, bob));

        await DeleteAsync();
        Assert.Equal("Pat", (await MeAsync()).DisplayName);
        Assert.Contains(me, await app.ParticipantsAsync(open));
        Assert.Contains(me, await app.ParticipantsAsync(stuck));
        Assert.Equal(Loop(me, ann, bob), await app.PairsAsync(stuck));
        Assert.True(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == stuck && p.PersonId == me).Select(p => p.WantsToLeave).SingleAsync()));
        Assert.False(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == open && p.PersonId == me).Select(p => p.WantsToLeave).SingleAsync()));

        var page = await browser.GetPageAsync("/account/delete");
        Assert.Contains("drawing names again", page.QuerySelector(".notice-error")!.TextContent);
        Assert.Contains("Waiting for a redraw", page.QuerySelector("main")!.TextContent);
        Assert.Null(page.QuerySelector("form[action='/account/delete']"));

        // The organizer redraws, which takes Pat out; then deletion goes through.
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PollyannaDb>();
            var events = scope.ServiceProvider.GetRequiredService<EventManager>();
            Assert.True((await events.RedrawAsync((await EventAccess.FindAsync(db, stuck, org))!)).Ok);
        }
        Assert.DoesNotContain(me, await app.ParticipantsAsync(stuck));
        await DeleteAsync();
        Assert.Null((await MeAsync()).DisplayName);
        Assert.DoesNotContain(me, await app.ParticipantsAsync(open));
    }

    [Fact]
    public async Task Deletion_gives_the_same_answer_whatever_stopped_the_patch()
    {
        // Pat and Ann drew each other, so Ann can't take over Pat's recipient.
        var impossible = await DrawnAsync("Big one", [me, ann, bob, carol, dave],
            new Dictionary<Guid, Guid> { [me] = ann, [ann] = me, [bob] = carol, [carol] = dave, [dave] = bob });
        await DeleteAsync();
        var cantPatch = (await browser.GetPageAsync("/account/delete")).QuerySelector(".notice-error")!.TextContent;
        Assert.Contains(me, await app.ParticipantsAsync(impossible));

        // An event of the same name, with only three people: taking Pat out would leave too few.
        await app.WithDbAsync(db => db.Events.Where(e => e.Id == impossible).ExecuteDeleteAsync());
        await DrawnAsync("Big one", [me, ann, bob], Loop(me, ann, bob));
        await DeleteAsync();
        var tooFew = (await browser.GetPageAsync("/account/delete")).QuerySelector(".notice-error")!.TextContent;

        // Whatever stopped the patch, Pat hears the same thing, so it hints at nobody's slip.
        Assert.Equal(cantPatch, tooFew);
    }

    [Fact]
    public async Task The_tombstone_shows_as_former_participant_and_their_messages_stay()
    {
        var (annBrowser, annId) = await app.SignInAsync(new MockIdentity("ann", "Ann", "ann@example.com", true, null));
        using var _a = annBrowser;
        var ev = await DrawnAsync("Revealed one", [me, annId, bob, carol], Loop(me, annId, bob, carol), EventStatus.Revealed);
        await app.MessageAsync(ev, me, MessageSender.Giver, "Socks or gloves?");
        await app.MessageAsync(ev, me, MessageSender.Recipient, "Gloves, please.");

        await DeleteAsync();

        var page = await annBrowser.GetPageAsync($"/events/{ev}");
        var lines = page.QuerySelectorAll("li.pairing").Select(li => li.TextContent).ToList();
        Assert.Contains(lines, l => l.Contains("Former participant") && l.Contains("Ann (you)") && l.IndexOf("Former") < l.IndexOf("Ann (you)"));
        Assert.Contains(lines, l => l.Contains("Carol") && l.Contains("Former participant"));
        Assert.Equal("Former participant asked you", page.QuerySelector("#questions-heading")!.TextContent);
        Assert.Contains("Socks or gloves?", page.QuerySelector("#questions")!.TextContent);
        Assert.Contains("Former participant", page.QuerySelector("#people-heading")!.ParentElement!.TextContent);
    }

    [Fact]
    public async Task Signing_in_again_starts_a_new_empty_account()
    {
        var ev = await DrawnAsync("Revealed one", [me, ann, bob], Loop(me, ann, bob), EventStatus.Revealed);
        await DeleteAsync();

        var (again, newId) = await app.SignInAsync(Pat);
        using var _ = again;
        Assert.NotEqual(me, newId);
        Assert.Null((await MeAsync()).OidcSub);
        Assert.Equal(HttpStatusCode.NotFound, (await again.GetAsync($"/events/{ev}")).StatusCode);
        Assert.Contains("Nothing to unwrap yet", (await again.GetPageAsync("/")).QuerySelector("h1")!.TextContent);
    }
}
