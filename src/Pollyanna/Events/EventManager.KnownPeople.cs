using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>
/// The people a viewer may pick from (spec §4, §7). This query is the one gate on whom a people
/// picker reveals and accepts, so every picker goes through it: the add-people control, the
/// organizer and late-joiner pickers, and the new-event form.
/// </summary>
public sealed partial class EventManager
{
    /// <summary>
    /// Every person whose account still exists and who is or was a participant or organizer in
    /// any event where <paramref name="viewer"/> is a participant or organizer, at any status,
    /// archived included. Never the viewer.
    /// </summary>
    public IQueryable<Person> KnownTo(Guid viewer)
    {
        var mine = db.Participants.Where(p => p.PersonId == viewer).Select(p => p.EventId)
            .Union(db.Organizers.Where(o => o.PersonId == viewer).Select(o => o.EventId));
        return db.People.Living().Where(person => person.Id != viewer
            && (db.Participants.Any(p => p.PersonId == person.Id && mine.Contains(p.EventId))
                || db.Organizers.Any(o => o.PersonId == person.Id && mine.Contains(o.EventId))));
    }

    /// <summary>Whether every one of <paramref name="personIds"/> is in <see cref="KnownTo"/>: a picker post's ticked people.</summary>
    public async Task<bool> AllKnownToAsync(Guid viewer, IReadOnlyCollection<Guid> personIds) =>
        await KnownTo(viewer).CountAsync(p => personIds.Contains(p.Id)) == personIds.Count;

    /// <summary>
    /// <see cref="KnownTo"/> as a picker lists it: names and pictures only, never emails, sorted by
    /// name, leaving out <paramref name="leaveOut"/> (such as the people already in the event).
    /// </summary>
    public async Task<List<PersonRow>> KnownRowsAsync(Guid viewer, IReadOnlyCollection<Guid> leaveOut)
    {
        var rows = await KnownTo(viewer).Where(p => !leaveOut.Contains(p.Id))
            .Select(p => new PersonRow(p.Id, p.DisplayName ?? "", db.PersonPictures.Any(x => x.PersonId == p.Id)))
            .ToListAsync();
        return [.. rows.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
    }
}
