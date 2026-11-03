using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>The "Added to an event" and "Made an organizer" emails (spec §15).</summary>
public sealed class MembershipNotificationTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    Task AddAsync(Guid ev, Guid person) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/addperson", Form(("personId", person.ToString())));

    [Fact]
    public async Task Someone_added_to_an_open_event_is_emailed()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [me], [me, bob]);

        await AddAsync(ev, ann);
        await app.SendMailAsync();

        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("ann@example.com", mail.To);
        Assert.Contains("Stockings 2026", mail.Subject);
        Assert.Contains("added", mail.Subject);
        Assert.Contains($"{TestApp.PublicUrl}/events/{ev}", mail.Body);
        Assert.Contains("you take part in", mail.Body);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Someone_added_to_a_draft_is_not_emailed_until_it_opens()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Draft, [me], [me]);

        await AddAsync(ev, ann);
        await app.SendMailAsync();
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/open");
        await app.SendMailAsync();
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("ann@example.com", mail.To);
        Assert.Contains("is open", mail.Subject);
    }

    [Fact]
    public async Task An_organizer_adding_themselves_is_not_emailed()
    {
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], []);

        await AddAsync(ev, me);
        await app.SendMailAsync();

        Assert.Contains(me, await app.ParticipantsAsync(ev));
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Inviting_the_email_of_someone_who_has_signed_in_adds_and_emails_them()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me]);

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/invite", Form(("email", "Ann@Example.com")));
        await app.SendMailAsync();

        Assert.Contains(ann, await app.ParticipantsAsync(ev));
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("ann@example.com", mail.To);
        Assert.Contains("added", mail.Subject);
    }

    [Fact]
    public async Task An_invitation_accepted_at_sign_in_emails_the_new_participant_unless_the_event_is_a_draft()
    {
        var open = await app.EventAsync("Open one", EventStatus.Open, [me], [me]);
        var draft = await app.EventAsync("Draft one", EventStatus.Draft, [me], [me]);
        foreach (var ev in new[] { open, draft })
            await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/invite", Form(("email", "sue@example.com")));
        await app.SendMailAsync();
        app.Mail.Sent.Clear(); // whatever the invitations themselves send

        var (sueBrowser, sue) = await app.SignInAsync(new MockIdentity("sue", "Sue", "Sue@Example.com", true, null));
        sueBrowser.Dispose();
        await app.SendMailAsync();

        Assert.Contains(sue, await app.ParticipantsAsync(open));
        Assert.Contains(sue, await app.ParticipantsAsync(draft));
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("Sue@Example.com", mail.To);
        Assert.Contains("Open one", mail.Subject);
        Assert.Contains("added", mail.Subject);
    }

    [Theory]
    [InlineData("Patch")]
    [InlineData("Redraw")]
    public async Task A_late_joiner_is_emailed_and_nobody_else_hears_about_it(string change)
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var cy = await app.PersonAsync("Cy", "cy@example.com");
        var dee = await app.PersonAsync("Dee", "dee@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy]);
        await app.SealAsync(ev, Loop(ann, bob, cy));

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/addperson",
            [KeyValuePair.Create("personId", dee.ToString()), KeyValuePair.Create("change", change), KeyValuePair.Create("householdId", "")]);
        await app.SendMailAsync();

        Assert.Contains(dee, await app.ParticipantsAsync(ev));
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("dee@example.com", mail.To);
        Assert.Contains("added", mail.Subject);
        foreach (var name in new[] { "Ann", "Bob", "Cy" })
        {
            Assert.DoesNotContain(name, mail.Subject);
            Assert.DoesNotContain(name, mail.Body);
        }
    }

    [Fact]
    public async Task A_late_joiner_who_couldnt_be_patched_in_is_not_emailed()
    {
        // Dee joins Ann and Bob's household, so no pairing can make room for her.
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var bob = await app.PersonAsync("Bob", "bob@example.com");
        var cy = await app.PersonAsync("Cy", "cy@example.com");
        var dee = await app.PersonAsync("Dee", "dee@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [ann, bob, cy]);
        await app.SealAsync(ev, Loop(ann, bob, cy));
        await app.HouseholdAsync(ev, "The Parks", ann, bob);
        var household = await app.WithDbAsync(db => db.Households.Where(h => h.EventId == ev).Select(h => h.Id).SingleAsync());

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/addperson",
            [KeyValuePair.Create("personId", dee.ToString()), KeyValuePair.Create("change", "Patch"), KeyValuePair.Create("householdId", household.ToString())]);
        await app.SendMailAsync();

        Assert.DoesNotContain(dee, await app.ParticipantsAsync(ev));
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    Task AddOrganizerAsync(Guid ev, Guid person) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/addorganizer", Form(("personId", person.ToString())));

    [Theory]
    [InlineData(EventStatus.Draft)]
    [InlineData(EventStatus.Open)]
    [InlineData(EventStatus.Drawn)]
    public async Task Someone_made_an_organizer_is_emailed_with_who_did_it(EventStatus status)
    {
        var olga = await app.PersonAsync("Olga", "olga@example.com");
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var ev = await app.EventAsync("Stockings 2026", status, [me], [me, ann]);

        await AddOrganizerAsync(ev, olga);
        await app.SendMailAsync();

        Assert.Contains(olga, await app.OrganizersAsync(ev));
        var mail = Assert.Single(app.Mail.Sent);
        Assert.Equal("olga@example.com", mail.To);
        Assert.Contains("Pat made you an organizer", mail.Subject);
        Assert.Contains("Stockings 2026", mail.Subject);
        Assert.Contains($"{TestApp.PublicUrl}/events/{ev}", mail.Body);
        Assert.Contains("you organize", mail.Body);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task Removals_send_nothing()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var olga = await app.PersonAsync("Olga", "olga@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me, olga], [me, ann]);

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/removeperson", Form(("personId", ann.ToString())),
            match: f => f.QuerySelector($"input[name=personId][value='{ann}']") is not null);
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/removeorganizer", Form(("personId", olga.ToString())),
            match: f => f.QuerySelector($"input[name=personId][value='{olga}']") is not null);
        await app.SendMailAsync();

        Assert.DoesNotContain(ann, await app.ParticipantsAsync(ev));
        Assert.DoesNotContain(olga, await app.OrganizersAsync(ev));
        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }

    [Fact]
    public async Task A_waiting_email_is_dropped_if_they_are_taken_out_again_first()
    {
        var ann = await app.PersonAsync("Ann", "ann@example.com");
        var olga = await app.PersonAsync("Olga", "olga@example.com");
        var ev = await app.EventAsync("Stockings", EventStatus.Open, [me], [me]);
        await AddAsync(ev, ann);
        await AddOrganizerAsync(ev, olga);
        Assert.Equal(2, (await app.WithDbAsync(db => db.Notifications.ToListAsync())).Count);

        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/removeperson", Form(("personId", ann.ToString())),
            match: f => f.QuerySelector($"input[name=personId][value='{ann}']") is not null);
        await browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/removeorganizer", Form(("personId", olga.ToString())),
            match: f => f.QuerySelector($"input[name=personId][value='{olga}']") is not null);
        await app.SendMailAsync();

        Assert.Empty(app.Mail.Sent);
        Assert.Empty(await app.WithDbAsync(db => db.Notifications.ToListAsync()));
    }
}
