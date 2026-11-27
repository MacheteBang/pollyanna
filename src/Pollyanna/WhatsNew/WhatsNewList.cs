using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Happenings;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.WhatsNew;

/// <summary>One notification as "What's new" shows it (spec §7, §16).</summary>
/// <param name="Line">The line, a link to <paramref name="Href"/>.</param>
/// <param name="At">When it happened (UTC); null for the kinds with no time.</param>
/// <param name="Upto">For a question or reply, the message the row is shown for: its dismiss form reads up to it and no further.</param>
public sealed record WhatsNewRow(Guid EventId, HappeningKind Kind, string Line, string Href, string EventName, DateTime? At, int? Upto = null);

/// <summary>
/// A person's "What's new" (spec §16): what's in it, in order, dismissing a row and seeing the
/// page a row points to. Stored rows come from <c>InAppNotifications</c>; the five kinds tied to
/// an assignment are worked out from the sealed rows' markers on each call (docs/adr/0008).
/// </summary>
public sealed class WhatsNewList(PollyannaDb db, SealedHat hat)
{
    /// <summary>
    /// The person's rows: the kinds with no time first (patched, notes updated, closed or
    /// reopened), then everything else newest first (spec §16).
    /// </summary>
    public async Task<IReadOnlyList<WhatsNewRow>> RowsAsync(Guid me) =>
        [.. (await ViewsAsync(me, eventId: null))
            .Select(x => new WhatsNewRow(x.Row.EventId, x.Row.Kind, x.Kind.Line(x.View), x.Kind.Href(x.View), x.View.Event.Name, x.Row.At))
            .Concat(await WorkedOutAsync(me))
            .OrderBy(r => HappeningKinds.Of(r.Kind).WorkedOut?.TimelessRank ?? int.MaxValue)
            .ThenByDescending(r => r.At).ThenBy(r => r.EventName, StringComparer.Ordinal).ThenBy(r => r.Kind)];

    /// <summary>How many rows the person has, for the bell.</summary>
    public async Task<int> CountAsync(Guid me) =>
        await db.InAppNotifications.CountAsync(n => n.PersonId == me) + (await WorkedOutAsync(me)).Count;

    /// <summary>
    /// Dismisses one row on purpose: a stored kind's row is deleted; a kind tied to an assignment
    /// moves the marker seeing it would (spec §16), so home's matching waiting line goes too.
    /// False if there was none.
    /// </summary>
    public async Task<bool> DismissAsync(Guid me, Guid eventId, HappeningKind kind, int? upto = null)
    {
        if (HappeningKinds.Find(kind)?.WorkedOut?.Marker is not { } marker)
            return await db.InAppNotifications.Where(n => n.PersonId == me && n.EventId == eventId && n.Kind == kind).ExecuteDeleteAsync() > 0;
        // Only where the rows show: a participant of a drawn event (a revealed event's threads are read-only).
        if (!await DrawnFor(me).AnyAsync(e => e.Id == eventId))
            return false;
        return await hat.DismissAsync(eventId, me, marker, upto);
    }

    /// <summary>The drawn events the person takes part in: where the worked-out kinds come from.</summary>
    IQueryable<Event> DrawnFor(Guid me) =>
        db.Events.AsNoTracking().Where(e => EventLifecycle.Live.Contains(e.Status) && db.Participants.Any(p => p.EventId == e.Id && p.PersonId == me));

    /// <summary>The rows worked out from the markers of the person's slips in the drawn events they take part in.</summary>
    async Task<List<WhatsNewRow>> WorkedOutAsync(Guid me)
    {
        var rows = new List<WhatsNewRow>();
        foreach (var ev in await DrawnFor(me).ToListAsync())
            rows.AddRange(WorkedOutKinds.Rows(ev, await hat.NewsAsync(ev.Id, me)));
        return rows;
    }

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
                Named = db.People.Where(p => p.Id == n.NamedPersonId).OrderBy(p => p.Id).Select(p => p.DisplayName).FirstOrDefault(),
                TakesPart = db.Participants.Any(p => p.EventId == e.Id && p.PersonId == me),
            })
            .ToListAsync();
        return [.. rows
            .Where(x => HappeningKinds.Of(x.Row.Kind).Stored is not null)
            .Select(x => (x.Row, HappeningKinds.Of(x.Row.Kind).Stored!, new StoredView(x.Event, x.Named, x.TakesPart)))];
    }
}
