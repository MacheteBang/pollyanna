using System.Net.Mail;
using Pollyanna.Configuration;
using Pollyanna.Data;
using Pollyanna.Notifications;
using Pollyanna.Notifications.Kinds;
using Invitation = Pollyanna.Data.Invitation;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>The result of a management action: what to tell the person who asked for it.</summary>
public sealed record Outcome(bool Ok, string Message)
{
    public static Outcome Done(string message) => new(true, message);
    public static Outcome Fail(string message) => new(false, message);
}

/// <summary>What the new-event form sends (spec §4, §7).</summary>
public sealed record NewEvent(
    string Name,
    string BudgetText,
    DateOnly? ExchangeDate,
    Guid? PreviousEventId,
    int NoRepeatDepth,
    bool TakePart,
    IReadOnlyCollection<Guid> CopyPeople);

/// <summary>
/// Creating and managing events (spec §4). Callers check who may act (<see cref="Membership"/>);
/// this class checks the event's own rules: statuses, who can be added, the last organizer.
/// The draw and the changes after it are in <c>EventManager.Draw.cs</c>, the question box in
/// <c>EventManager.Questions.cs</c>.
/// </summary>
public sealed partial class EventManager(PollyannaDb db, TimeProvider clock, SealedHat hat, PostingLimit limit, Outbox outbox)
{
    public const int NameMax = 200;
    public const int BudgetMax = 100;
    public const int HouseholdNameMax = 100;
    public const int NotesMax = 4000;
    public const int EmailMax = 320;
    public const int MaxNoRepeatDepth = 3;

    DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>People who have signed in and whose account still exists, so they can be picked.</summary>
    public IQueryable<Person> SignedInPeople => db.People.Where(p => p.OidcSub != null);

    // ---------- Creating and editing ----------

    /// <summary>
    /// Events <paramref name="me"/> organizes that <paramref name="forEvent"/> may be linked to:
    /// never the event itself, nor one whose own chain of previous events leads back to it.
    /// </summary>
    public async Task<List<Event>> PreviousCandidatesAsync(Guid me, Guid? forEvent = null)
    {
        var organized = await db.Events.AsNoTracking()
            .Where(e => e.Organizers.Any(o => o.PersonId == me) && e.Id != forEvent)
            .OrderByDescending(e => e.ExchangeDate).ThenBy(e => e.Name)
            .ToListAsync();
        if (forEvent is not { } id)
            return organized;

        var links = await db.Events.AsNoTracking().Where(e => e.PreviousEventId != null)
            .ToDictionaryAsync(e => e.Id, e => e.PreviousEventId!.Value);
        return organized.Where(e => !ChainReaches(links, e.Id, id)).ToList();
    }

    static bool ChainReaches(Dictionary<Guid, Guid> links, Guid from, Guid target)
    {
        var seen = new HashSet<Guid>();
        for (Guid? at = from; at is { } a && seen.Add(a); at = links.TryGetValue(a, out var next) ? next : null)
            if (a == target)
                return true;
        return false;
    }

    public static string? CheckDetails(string? name, string? budget, DateOnly? date, int depth)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "Give the event a name.";
        if (name.Trim().Length > NameMax)
            return $"Keep the name under {NameMax} characters.";
        if ((budget ?? "").Trim().Length > BudgetMax)
            return $"Keep the budget under {BudgetMax} characters.";
        if (date is not { } d)
            return "Pick the exchange date.";
        if (d.Year is < 2000 or > 2100)
            return "Pick an exchange date between 2000 and 2100.";
        if (depth is < 1 or > MaxNoRepeatDepth)
            return "Pick how many years back pairings shouldn't repeat.";
        return null;
    }

    /// <summary>
    /// Creates a draft with <paramref name="me"/> as its organizer. Linked to an earlier event,
    /// it takes the ticked people from it and copies their households.
    /// </summary>
    public async Task<(Event? Event, string? Error)> CreateAsync(Guid me, NewEvent input)
    {
        if (CheckDetails(input.Name, input.BudgetText, input.ExchangeDate, input.NoRepeatDepth) is { } error)
            return (null, error);

        Event? previous = null;
        if (input.PreviousEventId is { } previousId)
        {
            previous = await db.Events.Include(e => e.Participants).Include(e => e.Households)
                .SingleOrDefaultAsync(e => e.Id == previousId && e.Organizers.Any(o => o.PersonId == me));
            if (previous is null)
                return (null, "You can only link to an earlier event you organize.");
        }

        var ev = new Event
        {
            Name = input.Name.Trim(),
            BudgetText = input.BudgetText.Trim(),
            ExchangeDate = input.ExchangeDate!.Value,
            PreviousEventId = previous?.Id,
            NoRepeatDepth = input.NoRepeatDepth,
            CreatedBy = me,
            CreatedAt = Now,
        };
        ev.Organizers.Add(new Organizer { PersonId = me });

        var people = new HashSet<Guid>();
        if (input.TakePart)
            people.Add(me);
        if (previous is not null)
        {
            var previousPeople = previous.Participants.Select(p => p.PersonId).ToList();
            var alive = await SignedInPeople.Where(p => previousPeople.Contains(p.Id)).Select(p => p.Id).ToListAsync();
            people.UnionWith(input.CopyPeople.Where(id => id != me && alive.Contains(id)));
        }
        foreach (var id in people)
            ev.Participants.Add(new Participant { PersonId = id, JoinedAt = Now });

        if (previous is not null)
        {
            // Households come along for the people in both events (spec §4).
            foreach (var old in previous.Households)
            {
                var members = previous.Participants.Where(p => p.HouseholdId == old.Id && people.Contains(p.PersonId)).ToList();
                if (members.Count == 0)
                    continue;
                var copy = new Household { Name = old.Name };
                ev.Households.Add(copy);
                foreach (var m in members)
                    ev.Participants.Single(p => p.PersonId == m.PersonId).HouseholdId = copy.Id;
            }
        }

        db.Events.Add(ev);
        await db.SaveChangesAsync();
        return (ev, null);
    }

    public async Task<Outcome> UpdateDetailsAsync(Membership m, Guid me, string? name, string? budget, DateOnly? date, Guid? previousId, int depth)
    {
        if (!m.Editable)
            return Outcome.Fail("This event is archived.");
        if (CheckDetails(name, budget, date, depth) is { } error)
            return Outcome.Fail(error);

        var ev = await db.Events.SingleAsync(e => e.Id == m.Event.Id);
        if (previousId != ev.PreviousEventId || depth != ev.NoRepeatDepth)
        {
            // The no-repeat rule shapes the draw, so it is settled before it.
            if (!m.PeopleEditable)
                return Outcome.Fail("The earlier event can't change after the draw.");
            if (previousId is { } p && !(await PreviousCandidatesAsync(me, ev.Id)).Any(c => c.Id == p))
                return Outcome.Fail("You can only link to an earlier event you organize.");
            ev.PreviousEventId = previousId;
            ev.NoRepeatDepth = depth;
        }

        ev.Name = name!.Trim();
        ev.BudgetText = (budget ?? "").Trim();
        ev.ExchangeDate = date!.Value;
        await db.SaveChangesAsync();
        return Outcome.Done("Details saved.");
    }

    /// <summary>
    /// Opens a draft and emails every participant but <paramref name="me"/>, and every address
    /// still invited (spec §15).
    /// </summary>
    public async Task<Outcome> OpenAsync(Membership m, Guid me)
    {
        if (EventLifecycle.Open.Refusal(m.Event.Status) is { } refusal)
            return Outcome.Fail(refusal);
        var ev = await db.Events.SingleAsync(e => e.Id == m.Event.Id);
        ev.Status = EventLifecycle.Open.To;
        await outbox.QueueAsync(new EventOpen.Cause(ev.Id, me));
        // Invitations made while it was a draft are emailed now.
        foreach (var invitation in await db.Invitations.Where(i => i.EventId == ev.Id).OrderBy(i => i.CreatedAt).ToListAsync())
            await outbox.QueueAsync(new Notifications.Kinds.Invitation.Cause(ev.Id, invitation.Email, invitation.InvitedBy, me));
        await db.SaveChangesAsync();
        return Outcome.Done("The event is open. Participants can see it and write their notes.");
    }

    // ---------- Participants and invitations ----------

    /// <summary>
    /// Adds someone who has signed in. After the draw they are a late joiner (spec §5.4): the
    /// organizer picks <paramref name="change"/>, and may put them in an existing household.
    /// </summary>
    public async Task<Outcome> AddParticipantAsync(Membership m, Guid me, Guid personId, DrawChange? change = null, Guid? householdId = null)
    {
        if (!m.PeopleChangeable)
            return Outcome.Fail("People can't be added any more.");
        var person = await SignedInPeople.SingleOrDefaultAsync(p => p.Id == personId);
        if (person is null)
            return Outcome.Fail("Pick someone from the list.");
        if (m.IsLive)
            return await AddLateJoinerAsync(m, me, person, change, householdId);
        return await AddAsync(m.Event, me, person);
    }

    async Task<Outcome> AddAsync(Event ev, Guid me, Person person)
    {
        if (await db.Participants.AnyAsync(p => p.EventId == ev.Id && p.PersonId == person.Id))
            return Outcome.Fail($"{person.DisplayName} is already in the hat.");
        db.Participants.Add(new Participant { EventId = ev.Id, PersonId = person.Id, JoinedAt = Now });
        await outbox.QueueAsync(new AddedToEvent.Cause(ev.Id, person.Id, me));
        await db.SaveChangesAsync();
        return Outcome.Done($"Added {person.DisplayName}.");
    }

    /// <summary>
    /// Invites by email (spec §4). Someone who has already signed in with that email is added
    /// straight away; otherwise the invitation waits for a matching sign-in.
    /// </summary>
    public async Task<Outcome> InviteAsync(Membership m, Guid me, string? email)
    {
        if (!m.PeopleEditable)
            return Outcome.Fail("Invitations can't be sent after the draw.");
        email = email?.Trim() ?? "";
        if (!IsEmail(email))
            return Outcome.Fail("That doesn't look like an email address.");
        var normalized = Person.NormalizeEmail(email)!;

        // Same matching as on sign-in: case-insensitive, and never an email marked unverified.
        var matches = await SignedInPeople
            .Where(p => p.EmailNormalized == normalized && p.EmailVerified != false)
            .OrderBy(p => p.Id).Take(2).ToListAsync();
        if (matches.Count > 1)
            return Outcome.Fail("More than one person has signed in with that email. Pick them from the list instead.");
        if (matches.Count == 1)
        {
            var added = await AddAsync(m.Event, me, matches[0]);
            return added.Ok ? Outcome.Done($"{matches[0].DisplayName} has already signed in, so they're in the hat now.") : added;
        }

        if (await db.Invitations.AnyAsync(i => i.EventId == m.Event.Id && i.EmailNormalized == normalized))
            return Outcome.Fail($"{email} is already invited.");
        db.Invitations.Add(new Invitation
        {
            EventId = m.Event.Id,
            Email = email,
            EmailNormalized = normalized,
            InvitedBy = me,
            CreatedAt = Now,
        });
        // A draft's invitations are emailed when it opens (spec §4, §15).
        if (m.ShownToParticipants)
            await outbox.QueueAsync(new Notifications.Kinds.Invitation.Cause(m.Event.Id, email, me, me));
        await db.SaveChangesAsync();
        return Outcome.Done($"Invited {email}. They join the moment they sign in with that email.");
    }

    internal static bool IsEmail(string email) =>
        email.Length <= EmailMax
        && !email.Any(char.IsWhiteSpace)
        && MailAddress.TryCreate(email, out var parsed)
        && parsed.Address == email
        && parsed.Host.Length > 0;

    public async Task<Outcome> CancelInvitationAsync(Membership m, Guid invitationId)
    {
        var invitation = await db.Invitations.SingleOrDefaultAsync(i => i.Id == invitationId && i.EventId == m.Event.Id);
        if (invitation is null)
            return Outcome.Fail("That invitation is gone already.");
        db.Invitations.Remove(invitation);
        await db.SaveChangesAsync();
        return Outcome.Done($"Cancelled the invitation to {invitation.Email}.");
    }

    /// <summary>
    /// Takes someone out of the hat. After the draw they are a dropout (spec §5.4): the organizer
    /// picks <paramref name="change"/>.
    /// </summary>
    public async Task<Outcome> RemoveParticipantAsync(Membership m, Guid personId, DrawChange? change = null)
    {
        if (!m.PeopleChangeable)
            return Outcome.Fail("People can't be taken out any more.");
        var name = await db.People.Where(p => p.Id == personId).Select(p => p.DisplayName).SingleOrDefaultAsync();
        if (m.IsLive)
            return await RemoveDropoutAsync(m, personId, name ?? "them", change);
        return await RemoveAsync(m.Event.Id, personId)
            ? Outcome.Done($"Took {name ?? "them"} out of the hat.")
            : Outcome.Fail("They aren't in this event.");
    }

    /// <summary>
    /// Leaving (spec §4). Before the draw the person is simply removed. After it, once they've
    /// confirmed a second time, the dropout patch takes them out; if it can't, they stay in and
    /// the organizers are asked to redraw. Their organizer role, if any, stays.
    /// </summary>
    public async Task<Outcome> LeaveAsync(Membership m, Guid me, bool confirmedAfterDraw = false)
    {
        if (!m.CanLeave)
            return Outcome.Fail("You can't leave this event here.");
        if (m.IsLive)
            return confirmedAfterDraw
                ? await LeaveAfterDrawAsync(m, me)
                : Outcome.Fail("Confirm that someone else's assignment will change.");
        return await RemoveAsync(m.Event.Id, me)
            ? Outcome.Done($"You've left {m.Event.Name}.")
            : Outcome.Fail("You aren't in this event.");
    }

    // ---------- Transactions ----------

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction, committed only if <paramref name="commit"/>
    /// accepts its result. Otherwise, or if it throws, the transaction rolls back and the change
    /// tracker is cleared, so no entity tracked inside it carries values the database no longer has.
    /// Every transaction in this class goes through here.
    /// </summary>
    async Task<T> InTransactionAsync<T>(Func<Task<T>> work, Func<T, bool> commit)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var committed = false;
        try
        {
            var result = await work();
            if (commit(result))
            {
                await tx.CommitAsync();
                committed = true;
            }
            return result;
        }
        finally
        {
            if (!committed)
                db.ChangeTracker.Clear();
        }
    }

    /// <summary><see cref="InTransactionAsync{T}"/>, committed when the outcome is <see cref="Outcome.Ok"/>.</summary>
    Task<Outcome> InTransactionAsync(Func<Task<Outcome>> work) => InTransactionAsync(work, o => o.Ok);

    /// <summary>Removes a participant with their notes, household place and exclusions.</summary>
    async Task<bool> RemoveAsync(Guid eventId, Guid personId)
    {
        var participant = await db.Participants.SingleOrDefaultAsync(p => p.EventId == eventId && p.PersonId == personId);
        if (participant is null)
            return false;
        db.Participants.Remove(participant);
        db.Exclusions.RemoveRange(db.Exclusions.Where(x => x.EventId == eventId && (x.PersonA == personId || x.PersonB == personId)));
        await db.SaveChangesAsync();
        return true;
    }

    // ---------- Organizers ----------

    /// <summary>Makes someone an organizer and emails them, naming <paramref name="me"/> (spec §15).</summary>
    public async Task<Outcome> AddOrganizerAsync(Membership m, Guid me, Guid personId)
    {
        if (!m.Editable)
            return Outcome.Fail("This event is archived.");
        var person = await SignedInPeople.SingleOrDefaultAsync(p => p.Id == personId);
        if (person is null)
            return Outcome.Fail("Pick someone from the list.");
        if (await db.Organizers.AnyAsync(o => o.EventId == m.Event.Id && o.PersonId == personId))
            return Outcome.Fail($"{person.DisplayName} already organizes this event.");
        db.Organizers.Add(new Organizer { EventId = m.Event.Id, PersonId = personId });
        await outbox.QueueAsync(new MadeOrganizer.Cause(m.Event.Id, personId, me));
        await db.SaveChangesAsync();
        return Outcome.Done($"{person.DisplayName} is now an organizer.");
    }

    public async Task<Outcome> RemoveOrganizerAsync(Membership m, Guid personId)
    {
        if (!m.Editable)
            return Outcome.Fail("This event is archived.");
        var organizers = await db.Organizers.Where(o => o.EventId == m.Event.Id).ToListAsync();
        var organizer = organizers.SingleOrDefault(o => o.PersonId == personId);
        if (organizer is null)
            return Outcome.Fail("They don't organize this event.");
        if (organizers.Count == 1)
            return Outcome.Fail("An event needs at least one organizer. Add another one first.");
        db.Organizers.Remove(organizer);
        await db.SaveChangesAsync();
        var name = await db.People.Where(p => p.Id == personId).Select(p => p.DisplayName).SingleOrDefaultAsync();
        return Outcome.Done($"{name} no longer organizes this event.");
    }

    // ---------- Households and exclusions ----------

    /// <summary>
    /// Creates a household (<paramref name="householdId"/> null) or renames it and sets its
    /// members. A participant is in at most one household, so ticking someone moves them.
    /// </summary>
    public async Task<Outcome> SaveHouseholdAsync(Membership m, Guid? householdId, string? name, IReadOnlyCollection<Guid> members)
    {
        if (!m.PeopleEditable)
            return Outcome.Fail("Households can't change after the draw.");
        name = name?.Trim() ?? "";
        if (name.Length == 0)
            return Outcome.Fail("Give the household a name.");
        if (name.Length > HouseholdNameMax)
            return Outcome.Fail($"Keep the household name under {HouseholdNameMax} characters.");

        Household household;
        if (householdId is { } id)
        {
            var found = await db.Households.SingleOrDefaultAsync(h => h.Id == id && h.EventId == m.Event.Id);
            if (found is null)
                return Outcome.Fail("That household is gone already.");
            household = found;
            household.Name = name;
        }
        else
        {
            household = new Household { EventId = m.Event.Id, Name = name };
            db.Households.Add(household);
        }

        var participants = await db.Participants.Where(p => p.EventId == m.Event.Id).ToListAsync();
        foreach (var p in participants)
        {
            if (members.Contains(p.PersonId))
                p.HouseholdId = household.Id;
            else if (p.HouseholdId == household.Id)
                p.HouseholdId = null;
        }
        await db.SaveChangesAsync();
        return Outcome.Done($"Saved {name}.");
    }

    public async Task<Outcome> DeleteHouseholdAsync(Membership m, Guid householdId)
    {
        if (!m.PeopleEditable)
            return Outcome.Fail("Households can't change after the draw.");
        var household = await db.Households.SingleOrDefaultAsync(h => h.Id == householdId && h.EventId == m.Event.Id);
        if (household is null)
            return Outcome.Fail("That household is gone already.");
        foreach (var p in await db.Participants.Where(p => p.HouseholdId == householdId).ToListAsync())
            p.HouseholdId = null;
        db.Households.Remove(household);
        await db.SaveChangesAsync();
        return Outcome.Done($"Removed the household {household.Name}.");
    }

    public async Task<Outcome> AddExclusionAsync(Membership m, Guid a, Guid b)
    {
        if (!m.PeopleEditable)
            return Outcome.Fail("Rules can't change after the draw.");
        if (a == b)
            return Outcome.Fail("Pick two different people.");
        var inEvent = await db.Participants.CountAsync(p => p.EventId == m.Event.Id && (p.PersonId == a || p.PersonId == b));
        if (inEvent != 2)
            return Outcome.Fail("Pick two people from the hat.");
        var exclusion = Exclusion.Between(m.Event.Id, a, b);
        if (await db.Exclusions.AnyAsync(x => x.EventId == m.Event.Id && x.PersonA == exclusion.PersonA && x.PersonB == exclusion.PersonB))
            return Outcome.Fail("That rule is there already.");
        db.Exclusions.Add(exclusion);
        await db.SaveChangesAsync();
        return Outcome.Done("Added the rule.");
    }

    public async Task<Outcome> RemoveExclusionAsync(Membership m, Guid exclusionId)
    {
        if (!m.PeopleEditable)
            return Outcome.Fail("Rules can't change after the draw.");
        var exclusion = await db.Exclusions.SingleOrDefaultAsync(x => x.Id == exclusionId && x.EventId == m.Event.Id);
        if (exclusion is null)
            return Outcome.Fail("That rule is gone already.");
        db.Exclusions.Remove(exclusion);
        await db.SaveChangesAsync();
        return Outcome.Done("Removed the rule.");
    }

    // ---------- Notes ----------

    /// <summary>Only the participant edits their own notes, until the event is archived (spec §4).</summary>
    public async Task<Outcome> UpdateNotesAsync(Membership m, Guid me, string? notes)
    {
        if (!m.IsParticipant || !m.CanView)
            return Outcome.Fail("You aren't in this event.");
        if (!m.Editable)
            return Outcome.Fail("This event is archived.");
        notes = (notes ?? "").Replace("\r\n", "\n").Trim();
        if (notes.Length > NotesMax)
            return Outcome.Fail($"Keep your notes under {NotesMax:N0} characters.");

        var participant = await db.Participants.SingleAsync(p => p.EventId == m.Event.Id && p.PersonId == me);
        if (participant.Notes != notes)
        {
            participant.Notes = notes;
            participant.NotesVersion++;
            await db.SaveChangesAsync();
        }
        return Outcome.Done("Notes saved.");
    }
}
