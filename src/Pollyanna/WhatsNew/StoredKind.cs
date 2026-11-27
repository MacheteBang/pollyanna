using Pollyanna.Data;
using Pollyanna.Happenings;

namespace Pollyanna.WhatsNew;

/// <summary>Which role a stored kind is for, so a row goes when its person loses that role in the event.</summary>
public enum StoredFor
{
    Participants,
    Organizers,
    /// <summary>Participants and organizers: it goes only when the person has neither role.</summary>
    Everyone,
}

/// <summary>What a stored row's line and link are worked out from when it is shown.</summary>
/// <param name="Event">The event as it is now: its current name and state.</param>
/// <param name="Named">The display name of the person the row names; null when none or their account is gone.</param>
/// <param name="TakesPart">Whether the viewer is a participant of the event.</param>
public sealed record StoredView(Event Event, string? Named, bool TakesPart)
{
    public string EventUrl => $"/events/{Event.Id}";
    public string ManageUrl => $"/events/{Event.Id}/manage";
}

/// <summary>
/// A kind of happening kept in "What's new" as a stored row (spec §16, docs/adr/0008): the
/// What's new column of its row in <see cref="HappeningKinds.Table"/>.
/// </summary>
/// <param name="For">The role it is for.</param>
/// <param name="To">Who gets a row, as the database stands in the happening's transaction. The actor is left out after.</param>
/// <param name="Line">The line, in the event's current words.</param>
/// <param name="Href">The page it points to; seeing that page deletes the row.</param>
/// <param name="Named">Who the line names, if anyone.</param>
public sealed record StoredKind(
    StoredFor For,
    Func<Happening, StoredNotifications, Task<IReadOnlyList<Guid>>> To,
    Func<StoredView, string> Line,
    Func<StoredView, string> Href,
    Func<Happening, Guid?>? Named = null);
