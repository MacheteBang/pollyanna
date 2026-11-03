using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.MockIssuer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pollyanna.Tests.Support;

/// <summary>Shortcuts for putting people and events straight into the database.</summary>
public static class Seed
{
    public static Dictionary<string, string> Form(params (string Key, string Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value);

    /// <summary>Signs in through the whole OIDC flow and returns the browser with the person's ID.</summary>
    public static async Task<(TestBrowser Browser, Guid PersonId)> SignInAsync(this TestApp app, MockIdentity identity)
    {
        var browser = app.NewBrowser();
        await browser.SignInAsync(identity);
        var id = await app.WithDbAsync(db => db.People.Where(p => p.OidcIssuer == TestApp.Issuer && p.OidcSub == identity.Sub).Select(p => p.Id).SingleAsync());
        return (browser, id);
    }

    /// <summary>A person who signed in once (at another issuer, so test sign-ins never match them by sub).</summary>
    public static async Task<Guid> PersonAsync(this TestApp app, string name, string? email = null, bool? verified = true)
    {
        var p = new Person
        {
            OidcIssuer = "https://other.test",
            OidcSub = name + "-" + Guid.NewGuid(),
            DisplayName = name,
            Email = email,
            EmailNormalized = Person.NormalizeEmail(email),
            EmailVerified = email is null ? null : verified,
            CreatedAt = DateTime.UtcNow,
        };
        await app.WithDbAsync(async db => { db.People.Add(p); await db.SaveChangesAsync(); });
        return p.Id;
    }

    public static async Task<Guid> EventAsync(this TestApp app, string name, EventStatus status, Guid[] organizers, Guid[] participants,
        string budget = "$25", DateOnly? date = null)
    {
        var ev = new Event
        {
            Name = name,
            BudgetText = budget,
            ExchangeDate = date ?? new DateOnly(2026, 12, 24),
            Status = status,
            CreatedBy = organizers.FirstOrDefault(),
            CreatedAt = DateTime.UtcNow,
        };
        ev.Organizers.AddRange(organizers.Select(o => new Organizer { PersonId = o }));
        ev.Participants.AddRange(participants.Select(p => new Participant { PersonId = p, JoinedAt = DateTime.UtcNow }));
        await app.WithDbAsync(async db => { db.Events.Add(ev); await db.SaveChangesAsync(); });
        return ev.Id;
    }

    public static Task<Guid[]> ParticipantsAsync(this TestApp app, Guid eventId) =>
        app.WithDbAsync(db => db.Participants.Where(p => p.EventId == eventId).Select(p => p.PersonId).ToArrayAsync());

    public static Task<Guid[]> OrganizersAsync(this TestApp app, Guid eventId) =>
        app.WithDbAsync(db => db.Organizers.Where(o => o.EventId == eventId).Select(o => o.PersonId).ToArrayAsync());

    /// <summary>Seals a given pairing (giver → recipient) into the event and marks it drawn.</summary>
    public static Task SealAsync(this TestApp app, Guid eventId, IReadOnlyDictionary<Guid, Guid> pairs) =>
        app.WithHatAsync(async (db, hat) =>
        {
            await hat.ClearAsync(eventId);
            await hat.SealAsync(eventId, pairs);
            (await db.Events.SingleAsync(e => e.Id == eventId)).Status = EventStatus.Drawn;
            await db.SaveChangesAsync();
        });

    /// <summary>Runs <paramref name="action"/> with the app's own <see cref="SealedHat"/> over a fresh database context.</summary>
    static Task<T> WithHatAsync<T>(this TestApp app, Func<PollyannaDb, SealedHat, Task<T>> action)
    {
        var key = app.Services.GetRequiredService<DrawKey>();
        return app.WithDbAsync(db => action(db, new SealedHat(db, key)));
    }

    static Task WithHatAsync(this TestApp app, Func<PollyannaDb, SealedHat, Task> action) =>
        app.WithHatAsync<bool>(async (db, hat) => { await action(db, hat); return true; });

    /// <summary>A loop through the people in the order given: each draws the next.</summary>
    public static Dictionary<Guid, Guid> Loop(params Guid[] people) =>
        people.Select((p, i) => (p, people[(i + 1) % people.Length])).ToDictionary(x => x.p, x => x.Item2);

    /// <summary>
    /// Recovers the pairing the way a determined operator with the database and the draw key
    /// could (spec §5.2): only tests do this.
    /// </summary>
    public static Task<Dictionary<Guid, Guid>> PairsAsync(this TestApp app, Guid eventId) =>
        app.WithHatAsync(async (db, hat) =>
        {
            var people = await db.Participants.Where(p => p.EventId == eventId).Select(p => p.PersonId).ToListAsync();
            var rows = await hat.UnsealAsync(eventId, people);
            return rows.ToDictionary(r => r.Giver ?? throw new KeyNotFoundException("A slip no participant's tag matches."), r => r.Recipient);
        });

    public static Task<Assignment> AssignmentOfAsync(this TestApp app, Guid eventId, Guid giver)
    {
        var tag = app.Services.GetRequiredService<DrawKey>().GiverTag(eventId, giver);
        return app.WithDbAsync(db => db.Assignments.AsNoTracking().SingleAsync(a => a.EventId == eventId && a.GiverTag == tag));
    }

    /// <summary>Adds a message to the thread of <paramref name="giver"/>'s assignment, next in line, on the hour.</summary>
    public static Task MessageAsync(this TestApp app, Guid eventId, Guid giver, MessageSender sender, string body) =>
        app.WithHatAsync(async (db, hat) =>
        {
            var thread = await hat.ThreadStateAsync(eventId, giver, MessageSender.Giver)
                ?? throw new InvalidOperationException("No slip for that giver.");
            if (!await hat.AddMessageAsync(thread, sender, body, Message.HourOf(app.Clock.GetUtcNow().UtcDateTime)))
                throw new InvalidOperationException("That giver's thread is gone.");
        });

    /// <summary>Changes <paramref name="giver"/>'s assignment row directly, e.g. its thread markers.</summary>
    public static Task UpdateAssignmentAsync(this TestApp app, Guid eventId, Guid giver, Action<Assignment> change)
    {
        var tag = app.Services.GetRequiredService<DrawKey>().GiverTag(eventId, giver);
        return app.WithDbAsync(async db =>
        {
            change(await db.Assignments.SingleAsync(a => a.EventId == eventId && a.GiverTag == tag));
            await db.SaveChangesAsync();
        });
    }

    /// <summary>The messages of <paramref name="giver"/>'s thread, in order.</summary>
    public static async Task<List<Message>> ThreadOfAsync(this TestApp app, Guid eventId, Guid giver)
    {
        var row = await app.AssignmentOfAsync(eventId, giver);
        return await app.WithDbAsync(db => db.Messages.AsNoTracking().Where(x => x.AssignmentId == row.Id).OrderBy(x => x.Seq).ToListAsync());
    }

    public static Task<EventStatus> StatusAsync(this TestApp app, Guid eventId) =>
        app.WithDbAsync(db => db.Events.Where(e => e.Id == eventId).Select(e => e.Status).SingleAsync());

    public static Task HouseholdAsync(this TestApp app, Guid eventId, string name, params Guid[] members) =>
        app.WithDbAsync(async db =>
        {
            var h = new Household { EventId = eventId, Name = name };
            db.Households.Add(h);
            foreach (var p in await db.Participants.Where(p => p.EventId == eventId && members.Contains(p.PersonId)).ToListAsync())
                p.HouseholdId = h.Id;
            await db.SaveChangesAsync();
        });

    public static Task ExclusionAsync(this TestApp app, Guid eventId, Guid a, Guid b) =>
        app.WithDbAsync(async db => { db.Exclusions.Add(Exclusion.Between(eventId, a, b)); await db.SaveChangesAsync(); });
}
