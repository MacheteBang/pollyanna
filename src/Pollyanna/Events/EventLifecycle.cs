using Pollyanna.Data;

namespace Pollyanna.Events;

/// <summary>
/// A change of an event's status, allowed only from <see cref="From"/>. Callers claim it with a
/// status-conditioned update on <see cref="From"/>, so two organizers can't both make it.
/// </summary>
public sealed class Transition(IReadOnlyList<EventStatus> from, EventStatus to, Func<EventStatus, string> refusal)
{
    public IReadOnlyList<EventStatus> From => from;
    public EventStatus To => to;

    /// <summary>Why the event can't make this change from <paramref name="status"/>; null if it can.</summary>
    public string? Refusal(EventStatus status) => from.Contains(status) ? null : refusal(status);
}

/// <summary>
/// What each event status allows (spec §4): the one place that says which statuses a rule
/// holds in. Each set lists its statuses explicitly, so a new status joins none until someone
/// decides. Queries use the sets with <c>Contains</c>; <see cref="Membership"/> adds who is asking.
/// </summary>
public static class EventLifecycle
{
    /// <summary>Participants see the event once it is out of draft; organizers always do.</summary>
    public static readonly IReadOnlyList<EventStatus> ShownToParticipants =
        [EventStatus.Open, EventStatus.Drawn, EventStatus.Revealed, EventStatus.Archived];

    /// <summary>Before the draw: participants, invitations, households and exclusions change freely.</summary>
    public static readonly IReadOnlyList<EventStatus> BeforeDraw = [EventStatus.Draft, EventStatus.Open];

    /// <summary>
    /// Before the reveal: people can still leave or be added and taken out, after the draw as
    /// late joiners and dropouts (spec §5.4). Once revealed, the exchange has happened.
    /// </summary>
    public static readonly IReadOnlyList<EventStatus> BeforeReveal = [EventStatus.Draft, EventStatus.Open, EventStatus.Drawn];

    /// <summary>Names are drawn: there are slips, and question threads to read (spec §6).</summary>
    public static readonly IReadOnlyList<EventStatus> NamesDrawn = [EventStatus.Drawn, EventStatus.Revealed, EventStatus.Archived];

    /// <summary>Drawn and not yet revealed: slips carry markers and threads take messages.</summary>
    public static readonly IReadOnlyList<EventStatus> Live = [EventStatus.Drawn];

    /// <summary>The exchange has happened: slips and conversations are read-only.</summary>
    public static readonly IReadOnlyList<EventStatus> Over = [EventStatus.Revealed, EventStatus.Archived];

    /// <summary>Not archived: on the home page, and its details, organizers and notes can change.</summary>
    public static readonly IReadOnlyList<EventStatus> Active = [EventStatus.Draft, EventStatus.Open, EventStatus.Drawn, EventStatus.Revealed];

    /// <summary>Archived: read-only, and listed under past exchanges.</summary>
    public static readonly IReadOnlyList<EventStatus> Archived = [EventStatus.Archived];

    public static readonly Transition Open = new([EventStatus.Draft], EventStatus.Open,
        _ => "This event is already open.");

    public static readonly Transition Draw = new([EventStatus.Open], EventStatus.Drawn,
        s => s == EventStatus.Draft ? "Open the event before drawing names." : "Names have been drawn already.");

    /// <summary>Every name back in the hat: from drawn to open again.</summary>
    public static readonly Transition Redraw = new([EventStatus.Drawn], EventStatus.Open,
        _ => "Names haven't been drawn.");

    public static readonly Transition Reveal = new([EventStatus.Drawn], EventStatus.Revealed,
        s => Over.Contains(s) ? "The pairings have been revealed already." : "Names haven't been drawn.");

    /// <summary>Irreversible, from drawn or revealed (spec §4).</summary>
    public static readonly Transition Archive = new([EventStatus.Drawn, EventStatus.Revealed], EventStatus.Archived,
        s => s == EventStatus.Archived ? "This event is archived already." : "Only an event whose names are drawn can be archived.");
}
