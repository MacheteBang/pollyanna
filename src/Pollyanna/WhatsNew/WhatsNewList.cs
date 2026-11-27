using Pollyanna.Data;
using Pollyanna.Happenings;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.WhatsNew;

/// <summary>One notification as "What's new" shows it (spec §7, §16).</summary>
/// <param name="Line">The line, a link to <paramref name="Href"/>.</param>
/// <param name="At">When it happened (UTC); null for the kinds with no time.</param>
public sealed record WhatsNewRow(Guid EventId, HappeningKind Kind, string Line, string Href, string EventName, DateTime? At);

/// <summary>
/// A person's "What's new" (spec §16): what's in it, in order, dismissing a row and seeing the
/// page a row points to. Stored rows only, so far.
/// </summary>
public sealed class WhatsNewList(PollyannaDb db)
{
    /// <summary>The person's rows, newest first.</summary>
    public async Task<IReadOnlyList<WhatsNewRow>> RowsAsync(Guid me) =>
        [.. (await ViewsAsync(me, eventId: null))
            .Select(x => new WhatsNewRow(x.Row.EventId, x.Row.Kind, x.Kind.Line(x.View), x.Kind.Href(x.View), x.View.Event.Name, x.Row.At))
            .OrderByDescending(r => r.At).ThenBy(r => r.EventName, StringComparer.Ordinal).ThenBy(r => r.Kind)];

    /// <summary>How many rows the person has, for the bell.</summary>
    public Task<int> CountAsync(Guid me) => db.InAppNotifications.CountAsync(n => n.PersonId == me);

    /// <summary>Dismisses one row on purpose: a stored kind's row is deleted. False if there was none.</summary>
    public async Task<bool> DismissAsync(Guid me, Guid eventId, HappeningKind kind) =>
        await db.InAppNotifications.Where(n => n.PersonId == me && n.EventId == eventId && n.Kind == kind).ExecuteDeleteAsync() > 0;

    /// <summary>
    /// The person is seeing <paramref name="page"/> of the event: every row pointing there is
    /// deleted, inside this same page request (spec §16 "Seeing the thing clears it").
    /// </summary>
    public async Task SeenAsync(Guid me, Guid eventId, string page)
    {
        var seen = (await ViewsAsync(me, eventId)).Where(x => x.Kind.Href(x.View) == page).Select(x => x.Row.Id).ToList();
        if (seen.Count > 0)
            await db.InAppNotifications.Where(n => seen.Contains(n.Id)).ExecuteDeleteAsync();
    }

    async Task<List<(InAppNotification Row, StoredKind Kind, StoredView View)>> ViewsAsync(Guid me, Guid? eventId)
    {
        var rows = await db.InAppNotifications.AsNoTracking()
            .Where(n => n.PersonId == me && (eventId == null || n.EventId == eventId))
            .Join(db.Events, n => n.EventId, e => e.Id, (n, e) => new
            {
                Row = n,
                Event = e,
                Named = db.People.Where(p => p.Id == n.NamedPersonId).Select(p => p.DisplayName).FirstOrDefault(),
                TakesPart = db.Participants.Any(p => p.EventId == e.Id && p.PersonId == me),
            })
            .ToListAsync();
        return [.. rows
            .Where(x => HappeningKinds.Of(x.Row.Kind).Stored is not null)
            .Select(x => (x.Row, HappeningKinds.Of(x.Row.Kind).Stored!, new StoredView(x.Event, x.Named, x.TakesPart)))];
    }
}
