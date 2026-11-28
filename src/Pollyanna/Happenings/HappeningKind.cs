namespace Pollyanna.Happenings;

/// <summary>
/// Each kind of happening (spec §16). <see cref="Happening.About"/> is null unless a kind says
/// otherwise. What each kind does is in <see cref="HappeningKinds"/>.
/// </summary>
public enum HappeningKind
{
    /// <summary>A draft was opened.</summary>
    EventOpened,
    /// <summary>Names were drawn, for the first time or again.</summary>
    NamesDrawn,
    /// <summary>The pairings were revealed.</summary>
    EventRevealed,
    /// <summary>An address was invited. About: the invitation.</summary>
    Invited,
    /// <summary>An organizer added someone. About: the new participant.</summary>
    AddedToEvent,
    /// <summary>Someone took up their invitation at sign-in, so nobody acted. About: the new participant.</summary>
    InvitationAccepted,
    /// <summary>Someone was made an organizer. About: the new organizer.</summary>
    MadeOrganizer,
    /// <summary>The actor asked to leave, and taking them out needs a redraw.</summary>
    RedrawNeeded,
    /// <summary>A giver asked their recipient something. About: the recipient, never stored.</summary>
    Question,

    /// <summary>Every name went back in the hat: drawn to open again (spec §4).</summary>
    NamesPutBack,
    /// <summary>The event was archived.</summary>
    EventArchived,
    /// <summary>The event was canceled.</summary>
    EventCanceled,
    /// <summary>The name, budget, date or earlier event changed.</summary>
    DetailsChanged,
    /// <summary>The actor's own notes changed.</summary>
    NotesEdited,
    /// <summary>Someone left on their own, or deleted their account. About: who left, the actor.</summary>
    Left,
    /// <summary>Someone was taken out by an organizer, or by a redraw they had asked for. About: who was taken out.</summary>
    TakenOut,
    /// <summary>
    /// The draw was patched (spec §5.4), so one giver gives to someone new. About: the dropout or
    /// late joiner it was patched for, never the giver.
    /// </summary>
    Patched,
    /// <summary>A pending invitation was canceled. About: the invitation.</summary>
    InvitationCanceled,
    /// <summary>Someone stopped organizing. About: who.</summary>
    OrganizerRemoved,
    /// <summary>A household or a "can't draw" rule was added, changed or removed.</summary>
    RulesChanged,
    /// <summary>The actor opened their reveal card for the first time ("has looked", spec §5.5).</summary>
    HasLooked,
    /// <summary>The actor marked themselves done shopping, or still shopping.</summary>
    DoneShopping,
    /// <summary>A request to leave was dropped, by the one who asked or an organizer. About: who asked.</summary>
    LeaveWithdrawn,
    /// <summary>A recipient replied to their giver. The giver is never named.</summary>
    Reply,
    /// <summary>A recipient closed their conversation.</summary>
    ThreadClosed,
    /// <summary>A recipient reopened their conversation.</summary>
    ThreadReopened,
}
