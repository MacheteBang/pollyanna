using System.Linq.Expressions;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Happenings;
using Pollyanna.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.WhatsNew;

/// <summary>
/// Writes and deletes stored "What's new" rows inside the caller's transaction (spec §16): new
/// rows are added to the context and saved by the caller's own <c>SaveChanges</c>; deletions run
/// at once, in the same transaction.
/// </summary>
public sealed class StoredNotifications(PollyannaDb db, TimeProvider clock)
{
    /// <summary>
    /// A row of <paramref name="kind"/> for each person it goes to but the actor, replacing any
    /// row of that kind they have for the event, with new words and a new time.
    /// </summary>
    public async Task WriteAsync(Happening happening, StoredKind kind)
    {
        var named = kind.Named?.Invoke(happening);
        var at = clock.GetUtcNow().UtcDateTime;
        foreach (var person in (await kind.To(happening, this)).Distinct())
        {
            if (person == happening.Actor)
                continue;
            var row = db.InAppNotifications.Local.FirstOrDefault(n => Is(n, person, happening.EventId, happening.Kind))
                ?? await db.InAppNotifications.SingleOrDefaultAsync(n => n.PersonId == person && n.EventId == happening.EventId && n.Kind == happening.Kind);
            if (row is null)
                db.InAppNotifications.Add(row = new InAppNotification { PersonId = person, EventId = happening.EventId, Kind = happening.Kind });
            row.NamedPersonId = named;
            row.At = at;
        }
    }

    /// <summary>The people in <paramref name="audience"/> of the event, as the database stands in this transaction.</summary>
    public async Task<IReadOnlyList<Guid>> MembersAsync(Guid eventId, Audience audience) =>
        await audience.Members(db, eventId).ToListAsync();

    /// <summary>A new participant, unless the event is still a draft: "It's open" covers them when it opens, as the email does.</summary>
    public async Task<IReadOnlyList<Guid>> NewParticipantAsync(Guid eventId, Guid person)
    {
        var status = db.Events.Local.FirstOrDefault(e => e.Id == eventId)?.Status
            ?? await db.Events.Where(e => e.Id == eventId).Select(e => e.Status).SingleAsync();
        return EventLifecycle.ShownToParticipants.Contains(status) ? [person] : [];
    }

    static bool Is(InAppNotification n, Guid person, Guid eventId, HappeningKind kind) =>
        n.PersonId == person && n.EventId == eventId && n.Kind == kind;

    /// <summary>Every row of the person's, once they delete their account.</summary>
    public Task ForgetPersonAsync(Guid person) => DeleteAsync(n => n.PersonId == person);

    /// <summary>Every row of the event, once it is canceled or archived.</summary>
    public Task ForgetEventAsync(Guid eventId) => DeleteAsync(n => n.EventId == eventId);

    /// <summary>The event's rows of <paramref name="kind"/>; only those naming <paramref name="named"/> when given.</summary>
    public Task ForgetKindAsync(Guid eventId, HappeningKind kind, Guid? named = null) =>
        named is { } who
            ? DeleteAsync(n => n.EventId == eventId && n.Kind == kind && n.NamedPersonId == who)
            : DeleteAsync(n => n.EventId == eventId && n.Kind == kind);

    /// <summary>
    /// <paramref name="person"/>'s rows for the event that were for the role they just lost
    /// (<paramref name="lost"/>, participant or organizer), as the role they keep doesn't cover them.
    /// </summary>
    public async Task ForgetRoleAsync(Guid eventId, Guid person, StoredFor lost)
    {
        var keeps = lost == StoredFor.Participants
            ? await db.Organizers.AnyAsync(o => o.EventId == eventId && o.PersonId == person) ? StoredFor.Organizers : (StoredFor?)null
            : await db.Participants.AnyAsync(p => p.EventId == eventId && p.PersonId == person) ? StoredFor.Participants : null;
        var kinds = HappeningKinds.Table
            .Where(r => r.Stored is { } s && (keeps is null || (s.For != StoredFor.Everyone && s.For != keeps)))
            .Select(r => r.Kind)
            .ToList();
        await DeleteAsync(n => n.EventId == eventId && n.PersonId == person && kinds.Contains(n.Kind));
    }

    /// <summary>Deletes matching rows, saved or only just added in this transaction.</summary>
    async Task DeleteAsync(Expression<Func<InAppNotification, bool>> match)
    {
        var test = match.Compile();
        foreach (var row in db.InAppNotifications.Local.Where(test).ToList())
            db.Entry(row).State = EntityState.Detached;
        await db.InAppNotifications.Where(match).ExecuteDeleteAsync();
    }
}

/// <summary>"What's new", behind <see cref="Announcer"/>: writes and deletes stored rows by each kind's table row (spec §16).</summary>
public sealed class WhatsNewOnHappening(StoredNotifications stored) : IHappeningListener
{
    public async Task HearAsync(Happening happening)
    {
        var row = HappeningKinds.Of(happening.Kind);
        if (row.Stored is { } kind)
            await stored.WriteAsync(happening, kind);
        if (row.WhatsNew is { } effect)
            await effect(happening, stored);
    }
}
