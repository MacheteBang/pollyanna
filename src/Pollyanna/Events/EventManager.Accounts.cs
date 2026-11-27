using Pollyanna.Data;
using Pollyanna.Happenings;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>
/// What stands in the way of deleting an account, and what deleting it does (spec §4), for the
/// confirmation page. Events are named; nothing here says anything about pairings.
/// </summary>
public sealed record DeletionCheck(
    IReadOnlyList<string> OnlyOrganizerOf,
    IReadOnlyList<string> WaitingForRedraw,
    IReadOnlyList<string> SlipChanges,
    IReadOnlyList<string> Leaves)
{
    public bool Blocked => OnlyOrganizerOf.Count > 0 || WaitingForRedraw.Count > 0;
}

/// <summary>Deleting one's own account (spec §4).</summary>
public sealed partial class EventManager
{
    /// <summary>
    /// Events that aren't archived where <paramref name="me"/> is the only organizer: deleting the
    /// account waits until they add another one.
    /// </summary>
    Task<List<string>> OnlyOrganizerOfAsync(Guid me) =>
        db.Events.Where(e => EventLifecycle.Active.Contains(e.Status)
                && e.Organizers.Any(o => o.PersonId == me) && e.Organizers.Count == 1)
            .OrderBy(e => e.Name).Select(e => e.Name).ToListAsync();

    /// <summary>The events deleting the account leaves: every one they take part in that isn't revealed or archived.</summary>
    Task<List<Participant>> LeavingAsync(Guid me) =>
        db.Participants.Where(p => p.PersonId == me
                && db.Events.Any(e => e.Id == p.EventId && EventLifecycle.BeforeReveal.Contains(e.Status)))
            .ToListAsync();

    public async Task<DeletionCheck> CheckDeletionAsync(Guid me)
    {
        var leaving = await db.Participants.Where(p => p.PersonId == me)
            .Join(db.Events, p => p.EventId, e => e.Id, (p, e) => new { e.Name, e.Status, p.WantsToLeave })
            .Where(x => EventLifecycle.BeforeReveal.Contains(x.Status))
            .OrderBy(x => x.Name).ToListAsync();
        return new DeletionCheck(
            await OnlyOrganizerOfAsync(me),
            [.. leaving.Where(x => x.WantsToLeave).Select(x => x.Name)],
            [.. leaving.Where(x => EventLifecycle.Live.Contains(x.Status) && !x.WantsToLeave).Select(x => x.Name)],
            [.. leaving.Where(x => EventLifecycle.BeforeDraw.Contains(x.Status)).Select(x => x.Name)]);
    }

    /// <summary>
    /// Deletes <paramref name="me"/>'s account (spec §4), all or nothing. It leaves every event
    /// that isn't revealed or archived by the leaving rules: drawn ones by the dropout patch. If
    /// any drawn event can't patch them out, nothing changes except that those events get a
    /// request to leave, and deletion waits for their redraw. Otherwise their organizer roles,
    /// profile, logins, picture, sessions and notes go, and the person row stays as an empty tombstone,
    /// so revealed lists, kept messages and the no-repeat rule keep working without a name.
    /// </summary>
    public async Task<Outcome> DeleteAccountAsync(Guid me)
    {
        var check = await CheckDeletionAsync(me);
        if (check.OnlyOrganizerOf.Count > 0)
            return Outcome.Fail($"You're the only organizer of {Names(check.OnlyOrganizerOf)}. Add another organizer first.");
        if (check.WaitingForRedraw.Count > 0)
            return Outcome.Fail(WaitsFor(check.WaitingForRedraw));

        var stuck = await InTransactionAsync(async () =>
        {
            var blocked = new List<Guid>();
            foreach (var p in await LeavingAsync(me))
            {
                var status = await db.Events.Where(e => e.Id == p.EventId).Select(e => e.Status).SingleAsync();
                if (EventLifecycle.BeforeDraw.Contains(status))
                    await RemoveAsync(new Happening(HappeningKind.Left, p.EventId, me, me));
                else if (!await TakeOutAfterDrawAsync(new Happening(HappeningKind.Left, p.EventId, me, me)))
                    blocked.Add(p.EventId);
            }
            if (blocked.Count > 0)
                return blocked;

            foreach (var organized in await db.Organizers.Where(o => o.PersonId == me).Select(o => o.EventId).ToListAsync())
                await announcer.AnnounceAsync(new Happening(HappeningKind.OrganizerRemoved, organized, me, me));
            await db.Organizers.Where(o => o.PersonId == me).ExecuteDeleteAsync();
            // Revealed and archived events keep the participant row; their notes go.
            await db.Participants.Where(p => p.PersonId == me)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Notes, "").SetProperty(p => p.NotesVersion, p => p.NotesVersion + 1));
            // Their "What's new" rows go with the account (spec §16).
            await db.InAppNotifications.Where(n => n.PersonId == me).ExecuteDeleteAsync();
            await db.PersonPictures.Where(p => p.PersonId == me).ExecuteDeleteAsync();
            await db.Sessions.Where(s => s.PersonId == me).ExecuteDeleteAsync();
            await db.Logins.Where(l => l.PersonId == me).ExecuteDeleteAsync();
            await db.People.Where(p => p.Id == me).ExecuteUpdateAsync(s => s
                .SetProperty(p => p.DisplayName, (string?)null)
                .SetProperty(p => p.Email, (string?)null)
                .SetProperty(p => p.EmailNormalized, (string?)null)
                .SetProperty(p => p.EmailVerified, (bool?)null)
                .SetProperty(p => p.PictureUrl, (string?)null)
                .SetProperty(p => p.CreatedAt, DateTime.UnixEpoch)
                .SetProperty(p => p.LastLoginAt, (DateTime?)null));
            return blocked;
        }, blocked => blocked.Count == 0);
        if (stuck.Count == 0)
            return Outcome.Done("Your Pollyanna account is deleted.");

        // Rolled back: only the stuck events change, getting the same request leaving them would.
        await InTransactionAsync(async () =>
        {
            await db.Participants.Where(p => p.PersonId == me && stuck.Contains(p.EventId))
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true));
            foreach (var eventId in stuck)
                await announcer.AnnounceAsync(new Happening(HappeningKind.RedrawNeeded, eventId, me));
            await db.SaveChangesAsync();
            return true;
        }, done => done);
        var names = await db.Events.Where(e => stuck.Contains(e.Id)).OrderBy(e => e.Name).Select(e => e.Name).ToListAsync();
        return Outcome.Fail(WaitsFor(names) + " The organizers have been asked.");
    }

    static string WaitsFor(IReadOnlyList<string> events) =>
        $"Taking you out of {Names(events)} means drawing names again, so deleting your account waits until the organizers do.";

    static string Names(IReadOnlyList<string> names) =>
        names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
}
