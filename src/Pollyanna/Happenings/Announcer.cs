using Pollyanna.Data;
using Pollyanna.Notifications;

namespace Pollyanna.Happenings;

/// <summary>
/// The one entry point every happening is announced through (spec §16). Each listener hears it
/// inside the caller's transaction, so whatever a listener adds to the context is saved by the
/// caller's own <c>SaveChanges</c>, with the change that made it.
/// </summary>
public sealed class Announcer(IEnumerable<IHappeningListener> listeners)
{
    public async Task AnnounceAsync(Happening happening)
    {
        HappeningKinds.Of(happening.Kind);
        foreach (var listener in listeners)
            await listener.HearAsync(happening);
    }
}

/// <summary>Email, the first thing behind <see cref="Announcer"/>: adds each kind's emails to the outbox by its table row (spec §15).</summary>
public sealed class EmailOnHappening(Outbox outbox) : IHappeningListener
{
    public Task HearAsync(Happening happening) =>
        HappeningKinds.Of(happening.Kind).Email?.Invoke(happening, outbox) ?? Task.CompletedTask;
}
