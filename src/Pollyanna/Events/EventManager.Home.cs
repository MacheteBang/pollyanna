using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>
/// The one thing waiting on a person in an event, as home shows it (spec §7 Home, with events).
/// None of them names anyone: the home page isn't behind the bow.
/// </summary>
public enum Waiting
{
    None,
    WriteNotes,
    NothingUntilDrawn,
    SeeWhoYoureGivingTo,
    GivingToSomeoneNew,
    RecipientUpdatedNotes,
    GiverAskedYou,
    ReadyToOpen,
    ReadyToDrawNames,
    SomeoneWantsToLeave,
}

public sealed partial class EventManager
{
    /// <summary>
    /// What waits on <paramref name="me"/> in one event on home (spec §7). When several apply,
    /// the first of these wins: an unread question, a changed recipient, an organizer's line,
    /// updated notes, then the status's own line.
    /// </summary>
    public async Task<Waiting> WaitingAsync(Guid me, Guid eventId, EventStatus status, bool isOrganizer, bool isParticipant)
    {
        GiverSlipUpdates? slip = null;
        if (isParticipant && EventLifecycle.Live.Contains(status))
        {
            if (await hat.HasUnreadQuestionAsync(eventId, me))
                return Waiting.GiverAskedYou;
            slip = await hat.GiverSlipUpdatesAsync(eventId, me);
            if (slip is { RecipientChanged: true })
                return Waiting.GivingToSomeoneNew;
        }

        if (isOrganizer && await OrganizerWaitingAsync(eventId, status) is { } organizer)
            return organizer;

        if (!isParticipant)
            return Waiting.None;
        if (slip is { Viewed: true } && slip.RecipientNotesVersion != slip.NotesVersionSeen)
            return Waiting.RecipientUpdatedNotes;

        return status switch
        {
            EventStatus.Open => await db.Participants.AnyAsync(p => p.EventId == eventId && p.PersonId == me && p.Notes != "")
                ? Waiting.NothingUntilDrawn
                : Waiting.WriteNotes,
            EventStatus.Drawn when slip is { Viewed: false } => Waiting.SeeWhoYoureGivingTo,
            _ => Waiting.None,
        };
    }

    async Task<Waiting?> OrganizerWaitingAsync(Guid eventId, EventStatus status) => status switch
    {
        EventStatus.Draft => Waiting.ReadyToOpen,
        EventStatus.Open when (await CheckDrawAsync(eventId)).CanDraw => Waiting.ReadyToDrawNames,
        EventStatus.Drawn when await db.Participants.AnyAsync(p => p.EventId == eventId && p.WantsToLeave) => Waiting.SomeoneWantsToLeave,
        _ => null,
    };
}
