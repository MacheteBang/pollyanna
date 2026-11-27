using System.Runtime.CompilerServices;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Happenings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace Pollyanna.Live;

/// <summary>
/// Who a happening's live signal goes to (spec §16 "Who a live signal goes to"), the live column
/// of <see cref="HappeningKinds.Table"/>. Whoever acted also gets the hint on their other tabs.
/// </summary>
[Flags]
public enum LiveTo
{
    /// <summary>Nobody but whoever acted.</summary>
    Actor = 0,
    /// <summary>Public to the event: every participant and organizer (participants only once it's out of draft).</summary>
    Event = 1,
    /// <summary>The event's organizers.</summary>
    Organizers = 2,
    /// <summary>The person the happening is about (<see cref="Happening.About"/>), even once they're out of the event.</summary>
    About = 4,
    /// <summary>
    /// Between a giver and a recipient: only that one person, carrying the content itself (#104).
    /// Never a hint, since a hint and the fetch it sets off from one giver would pair them in the
    /// proxy log; until those frames exist, only whoever acted hears of it.
    /// </summary>
    Pair = 8,
}

/// <summary>
/// The live side's routing rule (spec §16): who gets a hint, worked out when it's sent, from the
/// database as committed. Hints name the event and an area (the happening's kind), never a
/// person. Connections are known by person only; an event's members are looked up here.
/// </summary>
public sealed class LiveSignals(PollyannaDb db, LiveConnections connections)
{
    /// <summary>The request header a page's own background post names its live connection in ("not this tab").</summary>
    public const string TabHeader = "Pollyanna-Tab";

    /// <summary>
    /// The request header (value <c>refetch</c>) marking a page's live refetch of its own URL
    /// (spec §16). Not read yet: making such a request a read-only view, not a visit, is #101.
    /// </summary>
    public const string RefetchHeader = "Pollyanna-Live";

    /// <summary>Sends <paramref name="happening"/>'s hint by its kind's <see cref="HappeningKindRow.Live"/>.</summary>
    public async Task SendAsync(Happening happening)
    {
        var to = HappeningKinds.Of(happening.Kind).Live;
        var people = new HashSet<Guid>();
        if (to.HasFlag(LiveTo.Event))
            people.UnionWith(await EventMembersAsync(happening.EventId));
        if (to.HasFlag(LiveTo.Organizers))
            people.UnionWith(await db.Organizers.Where(o => o.EventId == happening.EventId).Select(o => o.PersonId).ToListAsync());
        if (to.HasFlag(LiveTo.About) && happening.About is { } about)
            people.Add(about);
        // Your own action: your other tabs and devices.
        if (happening.Actor is { } actor)
            people.Add(actor);
        connections.Send(people, LiveFrame.Hint(happening.EventId, happening.Kind.ToString()));
    }

    /// <summary>
    /// Your own action that isn't a happening, such as a dismissal: a hint to your other tabs,
    /// leaving out <paramref name="tab"/>, the one that did it, when the page named itself.
    /// </summary>
    public void OwnAction(Guid me, Guid eventId, string area, string? tab) =>
        connections.Send([me], LiveFrame.Hint(eventId, area), tab);

    /// <summary>Everyone who can see the event: its organizers, and its participants unless it's a draft.</summary>
    async Task<List<Guid>> EventMembersAsync(Guid eventId)
    {
        var status = await db.Events.Where(e => e.Id == eventId).Select(e => (EventStatus?)e.Status).SingleOrDefaultAsync();
        var organizers = db.Organizers.Where(o => o.EventId == eventId).Select(o => o.PersonId);
        return status is null or EventStatus.Draft
            ? await organizers.ToListAsync()
            : await db.Participants.Where(p => p.EventId == eventId).Select(p => p.PersonId).Union(organizers).ToListAsync();
    }
}

/// <summary>
/// Live is the third thing behind <see cref="Announcer"/>: it holds each happening until the
/// change that made it is committed (spec §16: a signal before the commit could set off a refetch
/// that still sees the old state), then sends it. A rolled-back change sends nothing.
/// </summary>
public sealed class LiveOnHappening(PollyannaDb db, LiveAfterCommit afterCommit) : IHappeningListener
{
    public Task HearAsync(Happening happening)
    {
        afterCommit.Hold(db, happening, db.Database.CurrentTransaction?.GetDbTransaction());
        return Task.CompletedTask;
    }
}

/// <summary>
/// Sends held happenings once they're in the database: after a <c>SaveChanges</c> outside a
/// transaction, or when the transaction they were announced in (or the next one, for one announced
/// before it began) commits. Held per context; one instance for the app.
/// </summary>
public sealed class LiveAfterCommit(IServiceScopeFactory scopes) : ISaveChangesInterceptor, IDbTransactionInterceptor
{
    sealed record Held(Happening Happening, System.Data.Common.DbTransaction? Transaction);

    readonly ConditionalWeakTable<DbContext, List<Held>> held = new();

    public void Hold(DbContext db, Happening happening, System.Data.Common.DbTransaction? transaction)
    {
        var list = held.GetOrCreateValue(db);
        lock (list) list.Add(new Held(happening, transaction));
    }

    public async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db && db.Database.CurrentTransaction is null)
            // Anything held for a transaction is from one that's gone without committing.
            await SendAsync(db, h => h.Transaction is null, drop: h => true);
        return result;
    }

    public Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db && db.Database.CurrentTransaction is null)
            Take(db, _ => true);
        return Task.CompletedTask;
    }

    public async Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db)
            await SendAsync(db, h => h.Transaction is null || h.Transaction == transaction, drop: h => h.Transaction == transaction || h.Transaction is null);
    }

    public Task TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } db)
            Take(db, h => h.Transaction is null || h.Transaction == transaction);
        return Task.CompletedTask;
    }

    async Task SendAsync(DbContext db, Func<Held, bool> send, Func<Held, bool> drop)
    {
        var taken = Take(db, h => send(h) || drop(h));
        if (taken.Count == 0)
            return;
        // Worked out in a scope of its own: this context is still inside the commit it reports.
        await using var scope = scopes.CreateAsyncScope();
        var signals = scope.ServiceProvider.GetRequiredService<LiveSignals>();
        foreach (var h in taken.Where(send))
            await signals.SendAsync(h.Happening);
    }

    List<Held> Take(DbContext db, Func<Held, bool> which)
    {
        if (!held.TryGetValue(db, out var list))
            return [];
        lock (list)
        {
            var taken = list.Where(which).ToList();
            list.RemoveAll(h => taken.Contains(h));
            return taken;
        }
    }
}
