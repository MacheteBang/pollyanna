using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Notifications;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>The "Names are drawn" and "Event is revealed" emails (spec §15, ADR-0001).</summary>
public sealed class DrawNotificationTests : IAsyncLifetime
{
    TestApp app = null!;
    TestBrowser browser = null!;
    Guid me;
    Guid ann, bob, cy;

    public async Task InitializeAsync()
    {
        app = await TestApp.StartAsync(mail: true);
        (browser, me) = await app.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        ann = await app.PersonAsync("Ann", "ann@example.com");
        bob = await app.PersonAsync("Bob", "bob@example.com");
        cy = await app.PersonAsync("Cy", "cy@example.com");
    }

    public async Task DisposeAsync()
    {
        browser.Dispose();
        await app.DisposeAsync();
    }

    static string Manage(Guid ev) => $"/events/{ev}/manage";

    Task Post(Guid ev, string action, IEnumerable<KeyValuePair<string, string>>? fields = null) =>
        browser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/{action}", fields);

    Task DrawAsync(Guid ev) => Post(ev, "draw");

    /// <summary>Sends what's due and returns who got it, then forgets it.</summary>
    async Task<List<OutgoingMail>> SentAsync()
    {
        await app.SendMailAsync();
        var sent = new List<OutgoingMail>();
        while (app.Mail.Sent.TryDequeue(out var m))
            sent.Add(m);
        return sent;
    }

    [Fact]
    public async Task A_draw_tells_every_participant_their_slip_is_ready_but_not_the_organizer_who_drew()
    {
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [me], [me, ann, bob, cy]);

        await DrawAsync(ev);
        var sent = await SentAsync();

        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        Assert.Equal(["ann@example.com", "bob@example.com", "cy@example.com"], sent.Select(m => m.To).Order());
        foreach (var mail in sent)
        {
            Assert.Contains("Stockings 2026", mail.Subject);
            Assert.Contains("names are drawn", mail.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("your slip is ready", mail.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"{TestApp.PublicUrl}/events/{ev}", mail.Body);
            // Never whose slip it is.
            foreach (var name in new[] { "Pat", "Ann", "Bob", "Cy" })
                Assert.DoesNotContain(name, mail.Body.Replace("Stockings 2026", ""));
        }
    }

    [Fact]
    public async Task A_redraw_sends_nothing_and_the_new_draw_says_the_names_were_drawn_again()
    {
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [me], [me, ann, bob, cy]);
        await DrawAsync(ev);
        Assert.Equal(3, (await SentAsync()).Count);

        await Post(ev, "redraw");
        Assert.Equal(EventStatus.Open, await app.StatusAsync(ev));
        Assert.Empty(await SentAsync());

        await DrawAsync(ev);
        var sent = await SentAsync();
        Assert.Equal(["ann@example.com", "bob@example.com", "cy@example.com"], sent.Select(m => m.To).Order());
        Assert.All(sent, mail => Assert.Contains("Names were drawn again, take another look", mail.Body));
    }

    [Fact]
    public async Task A_slip_email_still_waiting_at_a_redraw_is_not_sent()
    {
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [me], [me, ann, bob, cy]);
        await DrawAsync(ev);

        await Post(ev, "redraw");
        Assert.Empty(await SentAsync());

        // Drawn again: one email each, the new one.
        await DrawAsync(ev);
        app.Clock.Advance(TimeSpan.FromHours(1));
        var sent = await SentAsync();
        Assert.Equal(["ann@example.com", "bob@example.com", "cy@example.com"], sent.Select(m => m.To).Order());
        Assert.All(sent, mail => Assert.Contains("drawn again", mail.Body));
    }

    /// <summary>A drawn event that Pat and Olga organize; Olga isn't taking part.</summary>
    async Task<(Guid Event, Guid Olga)> DrawnAsync()
    {
        var olga = await app.PersonAsync("Olga", "olga@example.com");
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [me, olga], [me, ann, bob, cy]);
        await app.SealAsync(ev, new Dictionary<Guid, Guid> { [me] = ann, [ann] = bob, [bob] = cy, [cy] = me });
        return (ev, olga);
    }

    [Fact]
    public async Task Revealing_emails_every_participant_and_organizer_but_the_organizer_who_revealed()
    {
        var (ev, _) = await DrawnAsync();

        await Post(ev, "reveal", Form(("confirmed", "true")));
        var sent = await SentAsync();

        Assert.Equal(EventStatus.Revealed, await app.StatusAsync(ev));
        Assert.Equal(["ann@example.com", "bob@example.com", "cy@example.com", "olga@example.com"], sent.Select(m => m.To).Order());
        foreach (var mail in sent)
        {
            Assert.Contains("Stockings 2026", mail.Subject);
            Assert.Contains("revealed", mail.Subject);
            Assert.Contains($"{TestApp.PublicUrl}/events/{ev}", mail.Body);
            foreach (var name in new[] { "Pat", "Ann", "Bob", "Cy", "Olga" })
                Assert.DoesNotContain(name, mail.Body.Replace("Stockings 2026", ""));
        }
        Assert.Contains("you're in \"Stockings 2026\"", sent.Single(m => m.To == "olga@example.com").Body);
    }

    static readonly string[] Givers = ["pat@example.com", "ann@example.com", "bob@example.com", "cy@example.com"];

    [Fact]
    public async Task A_dropout_patch_or_a_late_joiner_patch_emails_no_giver()
    {
        var (ev, olga) = await DrawnAsync();
        var dee = await app.PersonAsync("Dee", "dee@example.com");

        await Post(ev, "removeperson", Form(("personId", cy.ToString()), ("change", "patch")));
        Assert.DoesNotContain(cy, await app.ParticipantsAsync(ev));
        await Post(ev, "addperson", Form(("personId", dee.ToString()), ("change", "patch"), ("householdId", "")));
        Assert.Contains(dee, await app.ParticipantsAsync(ev));
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));

        // Whatever the late joiner themselves may be told, no giver hears about their own slip (ADR-0001).
        Assert.DoesNotContain(await SentAsync(), m => Givers.Contains(m.To) || m.To == "olga@example.com");
    }

    [Fact]
    public async Task Leaving_by_a_patch_emails_no_giver()
    {
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [me], [me, ann, bob, cy]);
        var dee = await app.PersonAsync("Dee", "dee@example.com");
        await app.WithDbAsync(async db =>
        {
            db.Participants.Add(new Participant { EventId = ev, PersonId = dee });
            await db.SaveChangesAsync();
        });
        await app.SealAsync(ev, new Dictionary<Guid, Guid> { [me] = ann, [ann] = bob, [bob] = cy, [cy] = dee, [dee] = me });
        var (cyBrowser, _) = await app.SignInAsync(new MockIdentity("cy", "Cy", "cy@example.com", true, null));
        using var _ = cyBrowser;

        // Two confirmations after the draw (spec §4).
        var second = await TestBrowser.ParseAsync(await cyBrowser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave"));
        await cyBrowser.SubmitAsync(second, $"/events/{ev}/leave");

        Assert.DoesNotContain(cy, await app.ParticipantsAsync(ev));
        Assert.Empty(await SentAsync());
    }

    [Fact]
    public async Task Archiving_sends_nothing()
    {
        var (ev, _) = await DrawnAsync();
        await Post(ev, "reveal", Form(("confirmed", "true")));
        await SentAsync();

        await Post(ev, "archive", Form(("keepMessages", "false")));

        Assert.Equal(EventStatus.Archived, await app.StatusAsync(ev));
        Assert.Empty(await SentAsync());
    }

    [Fact]
    public async Task Without_a_mail_relay_drawing_and_revealing_queue_nothing()
    {
        await using var plain = await TestApp.StartAsync();
        var (patBrowser, pat) = await plain.SignInAsync(new MockIdentity("pat", "Pat", "pat@example.com", true, null));
        using var _ = patBrowser;
        var people = new[] { pat, await plain.PersonAsync("Ann", "ann@example.com"), await plain.PersonAsync("Bob", "bob@example.com") };
        var ev = await plain.EventAsync("Stockings", EventStatus.Open, [pat], people);

        await patBrowser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/draw");
        await patBrowser.SubmitOnAsync(Manage(ev), $"{Manage(ev)}/reveal", Form(("confirmed", "true")));
        await plain.SendMailAsync();

        Assert.Equal(EventStatus.Revealed, await plain.StatusAsync(ev));
        Assert.Empty(await plain.WithDbAsync(db => db.Notifications.ToListAsync()));
        Assert.Empty(plain.Mail.Sent);
    }

    [Fact]
    public async Task A_dropouts_waiting_slip_email_is_not_sent()
    {
        var dee = await app.PersonAsync("Dee", "dee@example.com");
        var eve = await app.PersonAsync("Eve", "eve@example.com");
        // Six people, so the draw is one loop and Cy's giver can always take over Cy's recipient;
        // with five, a random draw can pair Cy both ways and the patch is refused.
        var ev = await app.EventAsync("Stockings 2026", EventStatus.Open, [me], [me, ann, bob, cy, dee, eve]);
        await DrawAsync(ev);

        await Post(ev, "removeperson", Form(("personId", cy.ToString()), ("change", "patch")));
        Assert.DoesNotContain(cy, await app.ParticipantsAsync(ev));
        var sent = await SentAsync();

        Assert.Equal(["ann@example.com", "bob@example.com", "dee@example.com", "eve@example.com"], sent.Select(m => m.To).Order());
    }
}
