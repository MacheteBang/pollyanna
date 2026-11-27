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
}
