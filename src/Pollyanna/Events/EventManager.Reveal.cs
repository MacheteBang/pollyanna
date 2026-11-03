using Pollyanna.Data;
using Pollyanna.Notifications.Kinds;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>One line of the revealed list: who gave to whom. <see cref="Giver"/> is null when the row can't be read (spec §5.3).</summary>
public sealed record Pairing(PersonRow? Giver, PersonRow Recipient);

/// <summary>
/// Reveal and archive (spec §4, §6): the two status changes after the draw, and the full pairing
/// list, the only place pairings are shown.
/// </summary>
public sealed partial class EventManager
{
    /// <summary>
    /// Revealing before the exchange date takes a confirmation (spec §4). The exchange date has no
    /// time zone, so "before" errs towards asking: until the date has begun everywhere (UTC−12).
    /// </summary>
    public bool RevealNeedsConfirmation(Event ev) =>
        ev.ExchangeDate > DateOnly.FromDateTime(Now.AddHours(-12));

    /// <summary>
    /// Reveals every pairing to the event's participants and organizers (spec §4). Irreversible.
    /// Anyone still waiting to leave stays in, and their request is cleared. Everyone in the event
    /// but <paramref name="me"/> is emailed (§15).
    /// </summary>
    public async Task<Outcome> RevealAsync(Membership m, Guid me, bool confirmed)
    {
        if (EventLifecycle.Reveal.Refusal(m.Event.Status) is { } refusal)
            return Outcome.Fail(refusal);
        if (RevealNeedsConfirmation(m.Event) && !confirmed)
            return Outcome.Fail("The exchange hasn't happened yet. Tick the box to reveal anyway.");

        var id = m.Event.Id;
        return await InTransactionAsync(async () =>
        {
            var claimed = await db.Events.Where(e => e.Id == id && EventLifecycle.Reveal.From.Contains(e.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventLifecycle.Reveal.To).SetProperty(e => e.PairingsRevealed, true));
            if (claimed == 0)
                return Outcome.Fail("The status changed in the meantime. Have a look and try again.");
            await ClearLeaveRequestsAsync(id);
            await outbox.QueueAsync(new EventRevealed.Cause(id, me));
            await db.SaveChangesAsync();
            return Outcome.Done("Revealed. Everyone in the event can see who drew whom on the event page.");
        });
    }

    /// <summary>
    /// Archives a drawn or revealed event (spec §4, §6): read-only from then on, and off the home
    /// page. Its question threads are deleted unless <paramref name="keepMessages"/>. Irreversible.
    /// </summary>
    public async Task<Outcome> ArchiveAsync(Membership m, bool keepMessages)
    {
        var from = m.Event.Status;
        if (EventLifecycle.Archive.Refusal(from) is { } refusal)
            return Outcome.Fail(refusal);

        var id = m.Event.Id;
        return await InTransactionAsync(async () =>
        {
            var claimed = await db.Events.Where(e => e.Id == id && e.Status == from)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventLifecycle.Archive.To));
            if (claimed == 0)
                return Outcome.Fail("The status changed in the meantime. Have a look and try again.");
            if (!keepMessages)
                await hat.ClearThreadsAsync(id);
            await ClearLeaveRequestsAsync(id);
            return Outcome.Done(keepMessages
                ? "Archived. The question threads were kept, and only their own two people can read them."
                : "Archived. The question threads were deleted.");
        });
    }

    Task ClearLeaveRequestsAsync(Guid eventId) =>
        db.Participants.Where(p => p.EventId == eventId && p.WantsToLeave)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, false));

    /// <summary>
    /// The full pairing list (spec §4, §7), for participants and organizers of an event whose
    /// pairings were revealed; null otherwise. Each row's giver is found by matching its tag
    /// against the event's participants, in memory (§5.2): nothing is written back. A row no
    /// participant's tag matches (e.g. the draw key changed) is listed without a giver.
    /// </summary>
    public async Task<IReadOnlyList<Pairing>?> PairingsAsync(Membership m)
    {
        if (!m.Event.PairingsRevealed || !m.CanView)
            return null;
        var id = m.Event.Id;
        var people = await db.Participants.Where(p => p.EventId == id)
            .Join(db.People, p => p.PersonId, person => person.Id, (p, person) =>
                new PersonRow(person.Id, person.DisplayName ?? "Former participant", db.PersonPictures.Any(x => x.PersonId == person.Id)))
            .ToDictionaryAsync(p => p.Id);
        var rows = await hat.UnsealAsync(id, people.Keys);

        return [.. rows
            .Where(r => people.ContainsKey(r.Recipient))
            .Select(r => new Pairing(r.Giver is { } g ? people[g] : null, people[r.Recipient]))
            .OrderBy(p => p.Giver is null)
            .ThenBy(p => p.Giver?.Name ?? p.Recipient.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
