using Pollyanna.Data;
using Pollyanna.Notifications.Kinds;
using Pollyanna.Happenings;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>What the organizer picks when someone drops out or joins after the draw (spec §5.4).</summary>
public enum DrawChange
{
    /// <summary>Change only one giver's assignment.</summary>
    Patch,
    /// <summary>Put every name back in the hat: status back to open, assignments deleted.</summary>
    Redraw,
}

/// <summary>
/// What the manage page shows before the draw (spec §4, §5.1): what blocks it, why it's
/// impossible, and whether the pairings may be guessable.
/// </summary>
public sealed record DrawCheck(string? Blocker, string? Problem, bool Predictable, bool NoRepeatIncomplete = false)
{
    public bool CanDraw => Blocker is null && Problem is null;
}

/// <summary>
/// The signed-in giver's own slip (spec §5.2, §7): their recipient and the markers on the card.
/// Nothing here says anything about anyone else's assignment. <paramref name="Viewed"/> is the
/// assignment's own "viewed" flag: whether the giver has untied it before (spec §12 The reveal).
/// </summary>
public sealed record Slip(PersonRow Recipient, string Notes, bool NotesUpdated, bool RecipientChanged, bool DrawnAgain, bool Viewed = false);

/// <summary>
/// The draw, redraw and the changes after it (spec §5). Pairings are held in plaintext only in
/// memory while an action runs, are never logged, and never leave this class except as a
/// person's own <see cref="Slip"/>, or once revealed as the full list (<c>EventManager.Reveal.cs</c>).
/// Before that, organizers learn only "viewed assignment".
/// </summary>
public sealed partial class EventManager
{
    // ---------- Before the draw ----------

    /// <summary>The draw rules for the event's current participants, households and exclusions.</summary>
    async Task<DrawRules> RulesAsync(Guid eventId) => (await RulesWithNoRepeatAsync(eventId)).Rules;

    /// <summary>
    /// The draw rules, and whether the no-repeat rule couldn't be fully applied because an earlier
    /// event's pairings are unreadable (spec §5.1).
    /// </summary>
    async Task<(DrawRules Rules, bool NoRepeatIncomplete)> RulesWithNoRepeatAsync(Guid eventId)
    {
        var people = await db.Participants.Where(p => p.EventId == eventId)
            .Join(db.People, p => p.PersonId, person => person.Id, (p, person) => new { p.PersonId, p.HouseholdId, person.DisplayName })
            .ToListAsync();
        var households = await db.Households.Where(h => h.EventId == eventId).ToDictionaryAsync(h => h.Id, h => h.Name);
        var exclusions = await db.Exclusions.Where(x => x.EventId == eventId).Select(x => new { x.PersonA, x.PersonB }).ToListAsync();

        var names = people.ToDictionary(p => p.PersonId, p => p.DisplayName ?? "Former participant");
        foreach (var (id, name) in households)
            names[id] = name;
        var (forbidden, incomplete) = await NoRepeatPairsAsync(eventId, people.Select(p => p.PersonId).ToHashSet());
        var rules = new DrawRules(
            [.. people.Select(p => p.PersonId)],
            people.Where(p => p.HouseholdId is not null).ToDictionary(p => p.PersonId, p => p.HouseholdId!.Value),
            [.. exclusions.Select(x => (x.PersonA, x.PersonB))],
            forbidden)
        {
            Names = names,
        };
        return (rules, incomplete);
    }

    /// <summary>
    /// Giver → recipient pairs the no-repeat rule forbids (spec §5.1, constraint 4): every pairing
    /// between <paramref name="people"/> in the last N earlier events with pairings, following the
    /// previous-event chain back. Events without pairings (never drawn, or redrawing) and canceled
    /// events are skipped;
    /// one whose pairings can't be read under the current key still counts, and makes the result
    /// incomplete. The earlier tags are recomputed in memory and nothing found leaves this method.
    /// </summary>
    async Task<(IReadOnlyCollection<(Guid Giver, Guid Recipient)> Pairs, bool Incomplete)> NoRepeatPairsAsync(Guid eventId, IReadOnlySet<Guid> people)
    {
        var ev = await db.Events.AsNoTracking().Where(e => e.Id == eventId)
            .Select(e => new { e.PreviousEventId, e.NoRepeatDepth }).SingleAsync();
        var pairs = new HashSet<(Guid, Guid)>();
        var incomplete = false;
        var seen = new HashSet<Guid> { eventId };
        var counted = 0;
        for (var at = ev.PreviousEventId; at is { } earlier && counted < ev.NoRepeatDepth && seen.Add(earlier);)
        {
            var standing = await db.Events.AnyAsync(e => e.Id == earlier && EventLifecycle.Standing.Contains(e.Status));
            var earlierPeople = await db.Participants.Where(p => p.EventId == earlier).Select(p => p.PersonId).ToListAsync();
            var rows = standing ? await hat.UnsealAsync(earlier, earlierPeople) : [];
            if (rows.Count > 0)
            {
                counted++;
                foreach (var (giver, recipient) in rows)
                {
                    if (giver is not { } g)
                        incomplete = true;
                    else if (people.Contains(g) && people.Contains(recipient))
                        pairs.Add((g, recipient));
                }
            }
            at = await db.Events.Where(e => e.Id == earlier).Select(e => e.PreviousEventId).SingleOrDefaultAsync();
        }
        return (pairs, incomplete);
    }

    async Task<string?> BlockerAsync(Guid eventId, DrawRules rules)
    {
        var invitations = await db.Invitations.CountAsync(i => i.EventId == eventId);
        if (invitations > 0)
            return invitations == 1
                ? "Can't draw yet: 1 invitation is still waiting for someone to sign in. Take it back or wait."
                : $"Can't draw yet: {invitations} invitations are still waiting for someone to sign in. Take them back or wait.";
        if (rules.People.Count < Draw.MinPeople)
            return $"Can't draw yet: at least {Draw.MinPeople} people need to take part.";
        return null;
    }

    public Task<DrawCheck> CheckDrawAsync(Membership m) => CheckDrawAsync(m.Event.Id);

    async Task<DrawCheck> CheckDrawAsync(Guid eventId)
    {
        var (rules, incomplete) = await RulesWithNoRepeatAsync(eventId);
        if (await BlockerAsync(eventId, rules) is { } blocker)
            return new DrawCheck(blocker, null, false);
        if (Draw.Problem(rules) is { } problem)
            return new DrawCheck(null, "Can't draw: " + problem, false, incomplete);
        return new DrawCheck(null, null, Draw.IsPredictable(rules), incomplete);
    }

    /// <summary>Said when an earlier event's pairings can't be read (spec §5.1); the draw goes ahead.</summary>
    public const string NoRepeatIncompleteWarning =
        "Some earlier pairings can't be read any more (the draw key may have changed), so the no-repeat rule can't be fully applied. The other rules still hold.";

    // ---------- Draw and redraw ----------

    /// <summary>
    /// Draws names (spec §5.1) and seals them (§5.2), all in one transaction, and emails every
    /// participant but <paramref name="me"/> that their slip is ready (§15).
    /// </summary>
    public async Task<Outcome> DrawAsync(Membership m, Guid me)
    {
        var id = m.Event.Id;
        return await InTransactionAsync(async () =>
        {
            // Claim the event first, so two organizers drawing at once can't both store a draw.
            var claimed = await db.Events.Where(e => e.Id == id && EventLifecycle.Draw.From.Contains(e.Status))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventLifecycle.Draw.To));
            if (claimed == 0)
                return Outcome.Fail(EventLifecycle.Draw.Refusal(m.Event.Status) ?? "Names have been drawn already.");

            var (rules, incomplete) = await RulesWithNoRepeatAsync(id);
            if (await BlockerAsync(id, rules) is { } blocker)
                return Outcome.Fail(blocker);
            var result = Draw.Run(rules);
            if (!result.Ok)
                return Outcome.Fail("Can't draw: " + result.Problem);

            await hat.SealAsync(id, result.Pairs!);
            await announcer.AnnounceAsync(new Happening(HappeningKind.NamesDrawn, id, me));
            await db.SaveChangesAsync();
            return Outcome.Done("Names are drawn. Everyone can see who they're giving to on their event page."
                + (incomplete ? " " + NoRepeatIncompleteWarning : ""));
        });
    }

    /// <summary>
    /// Puts every name back in the hat (spec §4): assignments and their question threads deleted,
    /// back to open. Anyone who asked to leave after the draw is taken out now.
    /// </summary>
    public async Task<Outcome> RedrawAsync(Membership m)
    {
        if (EventLifecycle.Redraw.Refusal(m.Event.Status) is { } refusal)
            return Outcome.Fail(refusal);
        return await InTransactionAsync(async () =>
            await ResetToOpenAsync(m.Event.Id) is { } left
                ? Outcome.Done("The draw is undone. Draw again when you're ready." + LeftNote(left))
                : Outcome.Fail("Names haven't been drawn."));
    }

    static string LeftNote(IReadOnlyList<string> left) =>
        left.Count == 0 ? "" : $" {string.Join(" and ", left)} left, as {(left.Count == 1 ? "they" : "they each")} asked.";

    /// <summary>
    /// The redraw itself, inside the caller's transaction. Returns the names of those taken out
    /// because they had asked to leave, or null if the event wasn't drawn.
    /// </summary>
    async Task<IReadOnlyList<string>?> ResetToOpenAsync(Guid eventId)
    {
        var claimed = await db.Events.Where(e => e.Id == eventId && EventLifecycle.Redraw.From.Contains(e.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Status, EventLifecycle.Redraw.To).SetProperty(e => e.Redrawn, true));
        if (claimed == 0)
            return null;
        await hat.ClearAsync(eventId);
        await NamesDrawn.WithdrawWaitingAsync(db, eventId);
        // A new recipient means shopping starts over.
        await db.Participants.Where(p => p.EventId == eventId && p.DoneShopping)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.DoneShopping, false));

        var leaving = await db.Participants.Where(p => p.EventId == eventId && p.WantsToLeave)
            .Join(db.People, p => p.PersonId, person => person.Id, (p, person) => new { p.PersonId, person.DisplayName })
            .ToListAsync();
        foreach (var l in leaving)
            await RemoveAsync(eventId, l.PersonId);
        return [.. leaving.Select(l => l.DisplayName ?? "Former participant")];
    }

    // ---------- Dropouts ----------

    async Task<Outcome> RemoveDropoutAsync(Membership m, Guid personId, string name, DrawChange? change)
    {
        var id = m.Event.Id;
        var people = await db.Participants.Where(p => p.EventId == id).Select(p => p.PersonId).ToListAsync();
        if (!people.Contains(personId))
            return Outcome.Fail("They aren't in this event.");
        if (change is null)
            return Outcome.Fail("Choose whether to patch the draw or draw names again.");
        if (people.Count - 1 < Draw.MinPeople)
            return Outcome.Fail($"Taking {name} out would leave fewer than {Draw.MinPeople} people taking part. Draw names again first, then take them out.");

        return await InTransactionAsync(async () =>
        {
            if (change == DrawChange.Redraw)
            {
                await RemoveAsync(id, personId);
                return await ResetToOpenAsync(id) is { } left
                    ? Outcome.Done($"Took {name} out. The draw is undone. Draw again when you're ready." + LeftNote(left))
                    : Outcome.Fail("Names haven't been drawn.");
            }

            return await TakeOutAfterDrawAsync(id, personId)
                ? Outcome.Done($"Took {name} out. One person is now giving to someone new, and they'll see a notice. Nobody else changed.")
                : Outcome.Fail($"A patch isn't possible for {name}: their giver can't take over their recipient under the draw rules. Draw names again instead.");
        });
    }

    /// <summary>
    /// The one way out of a drawn event (spec §4, §5.4), for a dropout whether an organizer
    /// removed them, they left, or they deleted their account: the dropout patch, then their
    /// removal, inside the caller's transaction, which rolls back on false. False if it would
    /// leave fewer than <see cref="Draw.MinPeople"/> people or the patch isn't possible. It doesn't
    /// say which, so no caller can give an answer that hints at anyone's assignment.
    /// </summary>
    async Task<bool> TakeOutAfterDrawAsync(Guid eventId, Guid dropout)
    {
        if (await db.Participants.CountAsync(p => p.EventId == eventId) - 1 < Draw.MinPeople)
            return false;
        if (!await hat.PatchOutAsync(eventId, dropout, await RulesAsync(eventId)))
            return false;
        return await RemoveAsync(eventId, dropout);
    }

    /// <summary>
    /// Leaving after the draw (spec §4), already confirmed twice: the dropout patch if it's
    /// possible and leaves at least 3 people; otherwise they stay in, asking for a redraw.
    /// </summary>
    async Task<Outcome> LeaveAfterDrawAsync(Membership m, Guid me)
    {
        var id = m.Event.Id;
        if (await db.Participants.Where(p => p.EventId == id && p.PersonId == me).Select(p => p.WantsToLeave).SingleAsync())
            return Outcome.Done("You've already asked to leave. You'll be taken out when the organizers draw names again.");
        if (await InTransactionAsync(() => TakeOutAfterDrawAsync(id, me), left => left))
            return Outcome.Done($"You've left {m.Event.Name}. One other person is now giving to someone new.");

        // Rolled back: they stay in, asking for a redraw.
        await InTransactionAsync(async () =>
        {
            await db.Participants.Where(p => p.EventId == id && p.PersonId == me)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.WantsToLeave, true));
            await announcer.AnnounceAsync(new Happening(HappeningKind.RedrawNeeded, id, me));
            await db.SaveChangesAsync();
            return true;
        }, done => done);
        return Outcome.Done("You're still in for now: taking you out means drawing names again. The organizers have been asked, and you'll be out once they do.");
    }

    /// <summary>Withdraws a request to leave that is waiting for a redraw.</summary>
    public async Task<Outcome> StayAsync(Membership m, Guid me)
    {
        var participant = await db.Participants.SingleOrDefaultAsync(p => p.EventId == m.Event.Id && p.PersonId == me);
        if (participant is null || !participant.WantsToLeave)
            return Outcome.Fail("You haven't asked to leave.");
        participant.WantsToLeave = false;
        await db.SaveChangesAsync();
        return Outcome.Done("You're staying in. The organizers won't see your request any more.");
    }

    /// <summary>
    /// An organizer keeps someone in who asked to leave (spec §7, "Keep X in instead"): the request
    /// is dropped and nothing else changes, as revealing or archiving would do.
    /// </summary>
    public async Task<Outcome> KeepInAsync(Membership m, Guid personId)
    {
        if (!m.IsLive)
            return Outcome.Fail("There's nobody to keep in now.");
        var participant = await db.Participants.SingleOrDefaultAsync(p => p.EventId == m.Event.Id && p.PersonId == personId);
        if (participant is null || !participant.WantsToLeave)
            return Outcome.Fail("They haven't asked to leave.");
        participant.WantsToLeave = false;
        await db.SaveChangesAsync();
        var name = await db.People.Where(p => p.Id == personId).Select(p => p.DisplayName).SingleAsync();
        return Outcome.Done($"Kept {name ?? "them"} in. Their request to leave is dropped.");
    }

    // ---------- Late joiners ----------

    async Task<Outcome> AddLateJoinerAsync(Membership m, Guid me, Person person, DrawChange? change, Guid? householdId)
    {
        var id = m.Event.Id;
        if (await db.Participants.AnyAsync(p => p.EventId == id && p.PersonId == person.Id))
            return Outcome.Fail($"{person.DisplayName} is already taking part.");
        if (change is null)
            return Outcome.Fail("Choose whether to patch the draw or draw names again.");
        if (householdId is { } h && !await db.Households.AnyAsync(x => x.Id == h && x.EventId == id))
            return Outcome.Fail("That household is gone already.");

        return await InTransactionAsync(async () =>
        {
            db.Participants.Add(new Participant { EventId = id, PersonId = person.Id, JoinedAt = Now, HouseholdId = householdId });
            await announcer.AnnounceAsync(new Happening(HappeningKind.AddedToEvent, id, me, person.Id));
            await db.SaveChangesAsync();

            if (change == DrawChange.Redraw)
                return await ResetToOpenAsync(id) is { } left
                    ? Outcome.Done($"Added {person.DisplayName}. The draw is undone. Draw again when you're ready." + LeftNote(left))
                    : Outcome.Fail("Names haven't been drawn.");

            return await PatchInAsync(id, person.Id)
                ? Outcome.Done($"Added {person.DisplayName}. One person is now giving to someone new to make room, and they'll see a notice. Nobody else changed.")
                : Outcome.Fail($"A patch isn't possible: no pairing can make room for {person.DisplayName} under the draw rules. Add them and draw names again instead.");
        });
    }

    /// <summary>
    /// The late-joiner patch (spec §5.4) under the event's draw rules, inside the caller's
    /// transaction, with the late joiner already a participant.
    /// </summary>
    async Task<bool> PatchInAsync(Guid eventId, Guid lateJoiner) =>
        await hat.PatchInAsync(eventId, lateJoiner, await RulesAsync(eventId));

    // ---------- The giver's own slip ----------

    /// <summary>
    /// The signed-in person's own slip, found by computing their giver tag (spec §5.2). Null if
    /// there is none under the current draw key (§5.3). After the exchange (revealed, archived)
    /// it is read-only and carries no markers.
    /// </summary>
    public async Task<Slip?> SlipAsync(Membership m, Guid me)
    {
        if (!m.IsParticipant || !m.NamesDrawn)
            return null;
        var live = m.IsLive;
        var row = await hat.GiverSlipAsync(m.Event.Id, me);
        if (row is null)
            return null;
        var recipient = await db.Participants.Where(p => p.EventId == m.Event.Id && p.PersonId == row.RecipientId)
            .Join(db.People, p => p.PersonId, person => person.Id, (p, person) => new
            {
                Row = new PersonRow(person.Id, person.DisplayName ?? "Former participant", db.PersonPictures.Any(x => x.PersonId == person.Id)),
                p.Notes,
                p.NotesVersion,
            })
            .SingleOrDefaultAsync();
        if (recipient is null)
            return null;
        return new Slip(recipient.Row, recipient.Notes,
            NotesUpdated: live && row.Viewed && recipient.NotesVersion != row.NotesVersionSeen,
            RecipientChanged: live && row.RecipientChanged,
            DrawnAgain: live && m.Event.Redrawn && !row.Viewed,
            Viewed: row.Viewed);
    }

    /// <summary>
    /// The giver opened their reveal card: "viewed assignment", the recipient's notes seen, any
    /// "recipient changed" notice cleared, and their question thread read up to
    /// <paramref name="threadUpto"/>, the last message the card showed.
    /// </summary>
    public async Task<bool> MarkSlipSeenAsync(Membership m, Guid me, int threadUpto = 0)
    {
        if (!m.IsParticipant || !m.IsLive)
            return false;
        var slip = await hat.GiverSlipAsync(m.Event.Id, me);
        if (slip is null)
            return false;
        var notesVersion = await db.Participants.Where(p => p.EventId == m.Event.Id && p.PersonId == slip.RecipientId)
            .Select(p => p.NotesVersion).SingleOrDefaultAsync();
        return await hat.MarkSeenAsync(slip, notesVersion, threadUpto);
    }

    /// <summary>Only the participant sets their own "done shopping", once names are drawn (spec §4).</summary>
    public async Task<Outcome> SetDoneShoppingAsync(Membership m, Guid me, bool done)
    {
        if (!m.IsParticipant || !m.IsLive)
            return Outcome.Fail("Names haven't been drawn.");
        var participant = await db.Participants.SingleAsync(p => p.EventId == m.Event.Id && p.PersonId == me);
        participant.DoneShopping = done;
        await db.SaveChangesAsync();
        return Outcome.Done(done ? "Marked as done shopping." : "Marked as still shopping.");
    }

    // ---------- What organizers see ----------

    /// <summary>
    /// Who has opened their reveal card (spec §5.5): a yes or no per person, nothing about whom
    /// they drew.
    /// </summary>
    public Task<HashSet<Guid>> ViewedAsync(Guid eventId, IEnumerable<Guid> people) => hat.ViewedAsync(eventId, people);
}
