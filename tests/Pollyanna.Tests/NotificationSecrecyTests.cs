using Pollyanna.Data;
using Pollyanna.MockIssuer;
using Pollyanna.Notifications;
using Pollyanna.Notifications.Kinds;
using Pollyanna.Tests.Support;
using Microsoft.EntityFrameworkCore;
using static Pollyanna.Tests.Support.Seed;

namespace Pollyanna.Tests;

/// <summary>
/// No notification weakens the sealed draw (spec §9, §15, docs/adr/0001), across every kind.
/// Like <see cref="PairingSecrecyTests"/>, one scenario runs twice with everyone else's recipients
/// shuffled in between: if what is queued or sent depended on someone else's pairing, the two runs
/// would differ. A second scenario sets off every kind and checks what the emails say, and that
/// nothing is queued with emails switched off or without a mail relay.
/// </summary>
public sealed class NotificationSecrecyTests
{
    /// <summary>What is queued, as an operator reading the outbox would see it, without the event's ID.</summary>
    sealed record Row(NotificationKind Kind, Guid? Person, string? Email, Guid? Named);

    static async Task<List<Row>> RowsAsync(TestApp app) =>
        [.. (await app.WithDbAsync(db => db.Notifications.ToListAsync()))
            .Select(n => new Row(n.Kind, n.PersonId, n.Email, n.NamedPersonId))
            .OrderBy(r => r.Kind).ThenBy(r => r.Person).ThenBy(r => r.Email)];

    /// <summary>Sends what's due and returns it in a stable order, with the event's ID taken out.</summary>
    static async Task<List<string>> SentAsync(TestApp app, Guid ev)
    {
        await app.SendMailAsync();
        var sent = new List<string>();
        while (app.Mail.Sent.TryDequeue(out var m))
            sent.Add($"To: {m.To}\nSubject: {m.Subject}\n{m.Body}".Replace(ev.ToString(), "{event}"));
        return [.. sent.Order(StringComparer.Ordinal)];
    }

    static void NextFullHour(TestApp app) =>
        app.Clock.SetUtcNow(Question.NextFullHour(app.Clock.GetUtcNow().UtcDateTime));

    static async Task<Dictionary<string, (TestBrowser Browser, Guid Id)>> SignInAllAsync(TestApp app, params string[] names)
    {
        var people = new Dictionary<string, (TestBrowser, Guid)>();
        foreach (var name in names)
            people[name] = await app.SignInAsync(new MockIdentity(name.ToLowerInvariant(), name, $"{name.ToLowerInvariant()}@example.com", true, null));
        return people;
    }

    static Task ManageAsync(TestBrowser organizer, Guid ev, string action, IEnumerable<KeyValuePair<string, string>>? fields = null) =>
        organizer.SubmitOnAsync($"/events/{ev}/manage", $"/events/{ev}/manage/{action}", fields);

    static async Task LeaveAsync(TestBrowser browser, Guid ev)
    {
        var second = await TestBrowser.ParseAsync(await browser.SubmitOnAsync($"/events/{ev}/leave", $"/events/{ev}/leave"));
        await browser.SubmitAsync(second, $"/events/{ev}/leave");
    }

    // ---------- Pairings don't change who is emailed ----------

    /// <summary>
    /// Everything that happens in someone's own assignment after a draw: every giver asks, every
    /// recipient reads, replies, closes and reopens their thread, everyone edits their notes, and
    /// an organizer patches a dropout out and a late joiner in. Returns what was queued and sent at
    /// each stage.
    /// </summary>
    static async Task<List<object>> AssignmentActivityAsync(
        TestApp app, Dictionary<string, (TestBrowser Browser, Guid Id)> people, Dictionary<Guid, Guid> pairs)
    {
        var (pat, ann, bob, carol, dave, eve) = (people["Pat"], people["Ann"], people["Bob"], people["Carol"], people["Dave"], people["Eve"]);
        Guid[] inTheHat = [pat.Id, ann.Id, bob.Id, carol.Id, dave.Id];
        var ev = await app.EventAsync("Gifts 2026", EventStatus.Open, [pat.Id], inTheHat);
        await app.SealAsync(ev, pairs);
        var page = $"/events/{ev}";
        var questions = $"{page}/questions";
        var stages = new List<object>();

        // What was queued, then what went out an hour later (questions are held to the hour).
        async Task StageAsync()
        {
            stages.Add(await RowsAsync(app));
            app.Clock.Advance(TimeSpan.FromHours(1));
            stages.Add(await SentAsync(app, ev));
        }

        // Each question is queued for the asker's recipient and nobody else.
        foreach (var (name, (browser, asker)) in people.Where(p => p.Key != "Eve"))
        {
            var waiting = await RowsAsync(app);
            await browser.SubmitOnAsync(page, $"{page}/ask", Form(("body", $"A question from {name}")));
            Assert.Equal([new Row(NotificationKind.Question, pairs[asker], null, null)], (await RowsAsync(app)).Except(waiting));
        }
        await StageAsync();

        foreach (var (name, (browser, _)) in people.Where(p => p.Key != "Eve"))
        {
            await browser.GetPageAsync(questions);
            await browser.SubmitOnAsync(questions, $"{questions}/reply", Form(("body", $"A reply from {name}")));
            await browser.SubmitOnAsync(questions, $"{questions}/close");
            await browser.SubmitOnAsync(questions, $"{questions}/reopen");
            await browser.SubmitOnAsync(page, $"{page}/notes", Form(("notes", $"Notes by {name}")));
        }
        Assert.All(await app.WithDbAsync(db => db.Participants.Where(p => p.EventId == ev).ToListAsync()),
            p => Assert.StartsWith("Notes by ", p.Notes));
        await StageAsync();

        await ManageAsync(pat.Browser, ev, "removeperson", Form(("personId", dave.Id.ToString()), ("change", "patch")));
        await ManageAsync(pat.Browser, ev, "addperson", Form(("personId", eve.Id.ToString()), ("change", "patch"), ("householdId", "")));
        Assert.Equal(new Guid[] { pat.Id, ann.Id, bob.Id, carol.Id, eve.Id }.Order(), (await app.ParticipantsAsync(ev)).Order());
        Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));
        await StageAsync();
        return stages;
    }

    [Fact]
    public async Task What_is_queued_and_sent_doesnt_change_when_other_peoples_recipients_do()
    {
        await using var app = await TestApp.StartAsync(mail: true);
        var people = await SignInAllAsync(app, "Pat", "Ann", "Bob", "Carol", "Dave", "Eve");
        try
        {
            var (p0, p1, p2, p3, p4) = (people["Pat"].Id, people["Ann"].Id, people["Bob"].Id, people["Carol"].Id, people["Dave"].Id);
            // Pat keeps Ann; everyone else's recipient changes, and so does Pat's giver.
            var first = Loop(p0, p1, p2, p3, p4);
            var second = new Dictionary<Guid, Guid> { [p0] = p1, [p1] = p4, [p2] = p0, [p3] = p2, [p4] = p3 };
            Assert.All(new[] { p1, p2, p3, p4 }, p => Assert.NotEqual(first[p], second[p]));

            var before = await AssignmentActivityAsync(app, people, first);
            var after = await AssignmentActivityAsync(app, people, second);

            Assert.Equal(before.Count, after.Count);
            for (var i = 0; i < before.Count; i++)
                Assert.Equivalent(before[i], after[i], strict: true);

            // And it isn't equal because nothing happens: each recipient hears about their
            // question, and the late joiner that they're in.
            var (questionRows, questionMail) = ((List<Row>)before[0], (List<string>)before[1]);
            Assert.Equal(5, questionRows.Count);
            Assert.All(questionRows, r => Assert.Equal(NotificationKind.Question, r.Kind));
            Assert.Equal(5, questionMail.Count);
            // Replies, closing, reopening and note edits reach no giver, or anyone.
            Assert.Empty((List<Row>)before[2]);
            Assert.Empty((List<string>)before[3]);
            // The patches tell no giver that their recipient changed; only the new participant hears.
            Assert.Equal([new Row(NotificationKind.AddedToEvent, people["Eve"].Id, null, null)], (List<Row>)before[4]);
            Assert.StartsWith("To: eve@example.com\n", Assert.Single((List<string>)before[5]));
        }
        finally
        {
            foreach (var (browser, _) in people.Values)
                browser.Dispose();
        }
    }

    // ---------- Every kind, end to end ----------

    /// <summary>Names that can't turn up inside other words in an email.</summary>
    static readonly string[] Everyone = ["Peregrine", "Annabel", "Bartholomew", "Cordelia", "Desmond", "Evangeline", "Fitzgerald", "Ottoline"];

    /// <summary>One email, and the queued rows it came from.</summary>
    sealed record Sent(string Step, OutgoingMail Mail);

    sealed record Run(List<Sent> Mail, List<(string Step, List<Notification> Rows)> Queued);

    /// <summary>
    /// Sets off every notification kind in one event, through the pages: an invitation and a new
    /// organizer while it's a draft, opening it, adding someone, everyone's notes, a draw, a redraw
    /// and a new draw, everyone's question, a late joiner patched in, a request to leave that
    /// needs a redraw, and the reveal. Peregrine organizes and takes part and does the organizing;
    /// Ottoline is made an organizer. Before each send, the rows waiting in the outbox are kept.
    /// </summary>
    static async Task<Run> EveryKindAsync(TestApp app, bool emailsOff = false)
    {
        var people = await SignInAllAsync(app, Everyone);
        try
        {
            var org = people["Peregrine"].Browser;
            Guid Id(string name) => people[name].Id;
            if (emailsOff)
                foreach (var (browser, _) in people.Values)
                    await browser.SubmitOnAsync("/account", "/account/emails");

            var ev = await app.EventAsync("Gifts 2026", EventStatus.Draft, [Id("Peregrine")],
                [Id("Peregrine"), Id("Annabel"), Id("Bartholomew"), Id("Cordelia"), Id("Desmond")]);
            var page = $"/events/{ev}";
            var run = new Run([], []);

            async Task StepAsync(string step)
            {
                // Everything this step queued is still waiting; then it's sent and collected.
                run.Queued.Add((step, await app.WithDbAsync(db => db.Notifications.AsNoTracking().ToListAsync())));
                await app.SendMailAsync();
                while (app.Mail.Sent.TryDequeue(out var m))
                    run.Mail.Add(new Sent(step, m));
            }

            await ManageAsync(org, ev, "invite", Form(("email", "sue@example.com")));
            await ManageAsync(org, ev, "addorganizer", Form(("personId", Id("Ottoline").ToString())));
            await StepAsync("draft");
            await ManageAsync(org, ev, "open");
            await StepAsync("open");
            await ManageAsync(org, ev, "addperson", Form(("personId", Id("Evangeline").ToString())));
            await StepAsync("added");
            // A waiting invitation blocks the draw.
            await ManageAsync(org, ev, "cancelinvite");
            foreach (var name in Everyone[..6])
                await people[name].Browser.SubmitOnAsync(page, $"{page}/notes", Form(("notes", $"Hush-notes of {name}")));
            await StepAsync("notes");
            await ManageAsync(org, ev, "draw");
            await StepAsync("drawn");
            await ManageAsync(org, ev, "redraw");
            await StepAsync("redraw");
            await ManageAsync(org, ev, "draw");
            await StepAsync("drawn again");
            foreach (var name in Everyone[..6])
                await people[name].Browser.SubmitOnAsync(page, $"{page}/ask", Form(("body", $"Hush-question of {name}")));
            NextFullHour(app);
            await StepAsync("questions");
            await ManageAsync(org, ev, "addperson", Form(("personId", Id("Fitzgerald").ToString()), ("change", "patch"), ("householdId", "")));
            await StepAsync("late joiner");
            Assert.Equal(EventStatus.Drawn, await app.StatusAsync(ev));

            // Annabel and Bartholomew drew each other, so Annabel's leaving needs a redraw (spec §4).
            await app.SealAsync(ev, new Dictionary<Guid, Guid>
            {
                [Id("Annabel")] = Id("Bartholomew"), [Id("Bartholomew")] = Id("Annabel"),
                [Id("Cordelia")] = Id("Desmond"), [Id("Desmond")] = Id("Evangeline"), [Id("Evangeline")] = Id("Fitzgerald"),
                [Id("Fitzgerald")] = Id("Peregrine"), [Id("Peregrine")] = Id("Cordelia"),
            });
            await app.MessageAsync(ev, Id("Annabel"), MessageSender.Giver, "Hush-question of Annabel");
            await app.MessageAsync(ev, Id("Annabel"), MessageSender.Recipient, "Hush-reply of Bartholomew");
            await LeaveAsync(people["Annabel"].Browser, ev);
            var annabel = Id("Annabel");
            Assert.True(await app.WithDbAsync(db => db.Participants.AnyAsync(p => p.EventId == ev && p.PersonId == annabel && p.WantsToLeave)));
            await StepAsync("request to leave");
            await ManageAsync(org, ev, "reveal", Form(("confirmed", "true")));
            Assert.Equal(EventStatus.Revealed, await app.StatusAsync(ev));
            await StepAsync("revealed");
            return run;
        }
        finally
        {
            foreach (var (browser, _) in people.Values)
                browser.Dispose();
        }
    }

    /// <summary>The kind an email is, told from its subject alone.</summary>
    static NotificationKind KindOf(OutgoingMail mail) => mail.Subject switch
    {
        var s when s.Contains("is open") => NotificationKind.EventOpen,
        var s when s.Contains("added to") => NotificationKind.AddedToEvent,
        var s when s.Contains("invited") => NotificationKind.Invitation,
        var s when s.Contains("made you an organizer") => NotificationKind.MadeOrganizer,
        var s when s.Contains("names are drawn") || s.Contains("names were drawn again") => NotificationKind.NamesDrawn,
        var s when s.Contains("is revealed") => NotificationKind.EventRevealed,
        var s when s.Contains("a question for you") => NotificationKind.Question,
        var s when s.Contains("a redraw is needed") => NotificationKind.RedrawNeeded,
        _ => throw new InvalidOperationException($"Unknown subject: {mail.Subject}"),
    };

    [Fact]
    public async Task No_email_of_any_kind_holds_a_message_notes_or_a_givers_or_recipients_name()
    {
        await using var app = await TestApp.StartAsync(mail: true);
        var run = await EveryKindAsync(app);

        Assert.Equal(Enum.GetValues<NotificationKind>().Order(), run.Mail.Select(s => KindOf(s.Mail)).Distinct().Order());
        Assert.Contains(run.Mail, s => s.Mail.Body.Contains("drawn again"));
        Assert.Contains(run.Mail, s => s.Step == "added" && KindOf(s.Mail) == NotificationKind.AddedToEvent);
        Assert.Contains(run.Mail, s => s.Step == "late joiner" && KindOf(s.Mail) == NotificationKind.AddedToEvent);

        foreach (var (step, mail) in run.Mail)
        {
            var text = mail.Subject + "\n" + mail.Body;
            Assert.DoesNotContain("Hush-", text);
            Assert.DoesNotContain("Bartholomew", text);
            // Only who acted, where it isn't secret, or the organizers' leaver (spec §15 "Content").
            string[] named = KindOf(mail) switch
            {
                NotificationKind.MadeOrganizer or NotificationKind.Invitation => ["Peregrine"],
                NotificationKind.RedrawNeeded => ["Annabel"],
                _ => [],
            };
            foreach (var name in Everyone.Except(named))
                Assert.False(text.Contains(name), $"The {step} email to {mail.To} names {name}:\n{text}");
        }
        // The leaver is only named to the organizers.
        Assert.All(run.Mail.Where(s => s.Mail.Body.Contains("Annabel")),
            s => Assert.Contains(s.Mail.To, new[] { "peregrine@example.com", "ottoline@example.com" }));
    }

    [Fact]
    public async Task No_email_of_any_kind_mentions_Santa_or_stockings()
    {
        await using var app = await TestApp.StartAsync(mail: true);
        var run = await EveryKindAsync(app);

        foreach (var (step, mail) in run.Mail)
        {
            var text = mail.Subject + "\n" + mail.Body;
            Assert.False(text.Contains("santa", StringComparison.OrdinalIgnoreCase), $"The {step} email to {mail.To} says Santa:\n{text}");
            Assert.False(text.Contains("stocking", StringComparison.OrdinalIgnoreCase), $"The {step} email to {mail.To} says stocking:\n{text}");
        }
    }

    [Fact]
    public async Task With_emails_switched_off_nothing_is_queued_for_anyone()
    {
        await using var app = await TestApp.StartAsync(mail: true);
        var run = await EveryKindAsync(app, emailsOff: true);

        Assert.All(run.Queued, q => Assert.All(q.Rows, r => Assert.Null(r.PersonId)));
        // The invited address has no account, so no switch: its invitation still goes.
        var only = Assert.Single(run.Mail);
        Assert.Equal(("open", "sue@example.com"), (only.Step, only.Mail.To));
    }

    [Fact]
    public async Task Without_a_mail_relay_nothing_is_queued_or_sent()
    {
        await using var app = await TestApp.StartAsync();
        var run = await EveryKindAsync(app);

        Assert.All(run.Queued, q => Assert.Empty(q.Rows));
        Assert.Empty(run.Mail);
    }
}
