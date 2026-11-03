using System.Net;
using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// Email invitations (spec §4, §9): they turn into participants only when someone signs in
/// with a matching email, and someone who has already signed in is added straight away.
/// </summary>
public sealed class InvitationTests : IAsyncLifetime
{
    TestApp app = null!;

    public async Task InitializeAsync() => app = await TestApp.StartAsync();
    public async Task DisposeAsync() => await app.DisposeAsync();

    async Task<(TestBrowser Organizer, Guid EventId)> EventWithOrganizerAsync(EventStatus status = EventStatus.Draft)
    {
        var (browser, org) = await app.SignInAsync(new("org", "Org", "org@example.com", true, null));
        var ev = await app.EventAsync("Stockings", status, [org], [org]);
        return (browser, ev);
    }

    async Task<HttpResponseMessage> InviteAsync(TestBrowser organizer, Guid ev, string email) =>
        await organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/invite", Form(("email", email)));

    Task<List<Invitation>> InvitationsAsync(Guid ev) => app.WithDbAsync(db => db.Invitations.Where(i => i.EventId == ev).ToListAsync());

    async Task<Guid> SignInAsNewPersonAsync(MockIdentity identity)
    {
        var (browser, id) = await app.SignInAsync(identity);
        browser.Dispose();
        return id;
    }

    [Fact]
    public async Task An_unknown_email_is_stored_and_shown_as_waiting()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;

        var response = await InviteAsync(organizer, ev, "Sue.M@Example.com");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var invitation = Assert.Single(await InvitationsAsync(ev));
        Assert.Equal("Sue.M@Example.com", invitation.Email);
        Assert.Equal("sue.m@example.com", invitation.EmailNormalized);

        var page = await organizer.GetPageAsync($"/events/{ev}/manage");
        Assert.Contains("Invited Sue.M@Example.com", page.QuerySelector(".notice")!.TextContent);
        Assert.Contains("Sue.M@Example.com", page.QuerySelector("#people")!.TextContent);
        Assert.Contains("1 invite pending", (await organizer.GetPageAsync("/")).Body!.TextContent);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)] // no email_verified claim: the email still counts for invitations
    public async Task Signing_in_with_a_matching_email_turns_the_invitation_into_a_participant(bool? verified)
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        await InviteAsync(organizer, ev, "sue@example.com");

        var sue = await SignInAsNewPersonAsync(new("sue", "Sue", "SUE@example.COM", verified, null));

        Assert.Contains(sue, await app.ParticipantsAsync(ev));
        Assert.Empty(await InvitationsAsync(ev));
    }

    [Fact]
    public async Task An_email_marked_unverified_never_matches()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        await InviteAsync(organizer, ev, "sue@example.com");

        var impostor = await SignInAsNewPersonAsync(new("impostor", "Sue?", "sue@example.com", false, null));

        Assert.DoesNotContain(impostor, await app.ParticipantsAsync(ev));
        Assert.Single(await InvitationsAsync(ev));
    }

    [Fact]
    public async Task Other_sign_ins_leave_the_invitation_alone()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        await InviteAsync(organizer, ev, "sue@example.com");

        await SignInAsNewPersonAsync(new("tom", "Tom", "tom@example.com", true, null));
        await SignInAsNewPersonAsync(new("noemail", "No Email", null, null, null));
        await SignInAsNewPersonAsync(new("close", "Close", "sue@example.com.evil.test", true, null));

        Assert.Single(await app.ParticipantsAsync(ev));
        Assert.Single(await InvitationsAsync(ev));
    }

    [Fact]
    public async Task One_sign_in_accepts_invitations_to_several_events()
    {
        var (organizer, ev1) = await EventWithOrganizerAsync();
        using var _o = organizer;
        var org = (await app.OrganizersAsync(ev1)).Single();
        var ev2 = await app.EventAsync("Cousins", EventStatus.Open, [org], []);
        await InviteAsync(organizer, ev1, "sue@example.com");
        await InviteAsync(organizer, ev2, "sue@example.com");

        var sue = await SignInAsNewPersonAsync(new("sue", "Sue", "sue@example.com", true, null));

        Assert.Contains(sue, await app.ParticipantsAsync(ev1));
        Assert.Contains(sue, await app.ParticipantsAsync(ev2));
        Assert.Equal(0, await app.WithDbAsync(db => db.Invitations.CountAsync()));
    }

    [Fact]
    public async Task Inviting_someone_who_has_signed_in_adds_them_straight_away()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        var carol = await app.PersonAsync("Carol", "carol@example.com", verified: null);

        await InviteAsync(organizer, ev, "Carol@example.com");

        Assert.Contains(carol, await app.ParticipantsAsync(ev));
        Assert.Empty(await InvitationsAsync(ev));
        var page = await organizer.GetPageAsync($"/events/{ev}/manage");
        Assert.Contains("Carol has already signed in", page.QuerySelector(".notice")!.TextContent);
    }

    [Fact]
    public async Task Someone_whose_email_is_marked_unverified_isnt_added_by_email()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        var carol = await app.PersonAsync("Carol", "carol@example.com", verified: false);

        await InviteAsync(organizer, ev, "carol@example.com");

        Assert.DoesNotContain(carol, await app.ParticipantsAsync(ev));
        Assert.Single(await InvitationsAsync(ev));
    }

    [Fact]
    public async Task An_email_shared_by_two_people_asks_the_organizer_to_pick_by_name()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        await app.PersonAsync("Kid 1", "family@example.com");
        await app.PersonAsync("Kid 2", "family@example.com");

        await InviteAsync(organizer, ev, "family@example.com");

        Assert.Single(await app.ParticipantsAsync(ev));
        Assert.Empty(await InvitationsAsync(ev));
        var page = await organizer.GetPageAsync($"/events/{ev}/manage");
        Assert.Contains("Pick them from the list", page.QuerySelector(".notice-error")!.TextContent);
    }

    [Theory]
    [InlineData("not an email")]
    [InlineData("sue@")]
    [InlineData("Sue <sue@example.com>")]
    [InlineData("")]
    public async Task Malformed_emails_are_refused(string email)
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        await InviteAsync(organizer, ev, email);
        Assert.Empty(await InvitationsAsync(ev));
    }

    [Fact]
    public async Task The_same_email_is_invited_once()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        await InviteAsync(organizer, ev, "sue@example.com");
        await InviteAsync(organizer, ev, "SUE@example.com");
        Assert.Single(await InvitationsAsync(ev));
    }

    [Fact]
    public async Task Organizers_can_cancel_an_invitation()
    {
        var (organizer, ev) = await EventWithOrganizerAsync();
        using var _o = organizer;
        await InviteAsync(organizer, ev, "sue@example.com");

        var response = await organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/cancelinvite");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(await InvitationsAsync(ev));

        // Signing in later no longer joins the event.
        var sue = await SignInAsNewPersonAsync(new("sue", "Sue", "sue@example.com", true, null));
        Assert.DoesNotContain(sue, await app.ParticipantsAsync(ev));
    }

    [Fact]
    public async Task No_invitations_after_the_draw()
    {
        var (organizer, ev) = await EventWithOrganizerAsync(EventStatus.Drawn);
        using var _o = organizer;
        var response = await organizer.PostWithTokenAsync("/account", $"/events/{ev}/manage/invite", Form(("email", "late@example.com")));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Empty(await InvitationsAsync(ev));
    }

    [Fact]
    public async Task An_invited_person_sees_the_event_on_their_home_page_after_signing_in()
    {
        var (organizer, ev) = await EventWithOrganizerAsync(EventStatus.Open);
        using var _o = organizer;
        await InviteAsync(organizer, ev, "sue@example.com");

        var (sue, _) = await app.SignInAsync(new("sue", "Sue", "sue@example.com", true, null));
        using var _s = sue;
        var home = await sue.GetPageAsync("/");
        Assert.Contains("Stockings", home.QuerySelector(".slip-title")!.TextContent);
    }
}
