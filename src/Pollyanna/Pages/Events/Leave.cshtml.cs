using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages.Events;

/// <summary>
/// Leaving an event, after a confirmation step (spec §4). Once names are drawn there is a second
/// one, explaining that someone else's slip will change.
/// </summary>
public sealed class LeaveModel(PollyannaDb db, EventManager events) : EventPageModel(db)
{
    /// <summary>The first step is confirmed and names are drawn: ask again.</summary>
    public bool SecondStep { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        if (!Membership.CanLeave || await WantsToLeaveAsync(id))
            return Redirect($"/events/{id}");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(Guid id, bool confirmed)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        if (Membership.CanLeave && Membership.IsLive && !confirmed && !await WantsToLeaveAsync(id))
        {
            SecondStep = true;
            return Page();
        }
        var outcome = await events.LeaveAsync(Membership, Me, confirmed);
        // Still in, waiting for a redraw, or refused: back to the event page.
        if (!outcome.Ok || await Db.Participants.AnyAsync(p => p.EventId == id && p.PersonId == Me))
            return Finish(outcome, $"/events/{id}");
        // Leaving doesn't end an organizer role, so organizers carry on from the manage page.
        return Finish(outcome, Membership.IsOrganizer ? $"/events/{id}/manage" : "/");
    }

    Task<bool> WantsToLeaveAsync(Guid id) =>
        Db.Participants.AnyAsync(p => p.EventId == id && p.PersonId == Me && p.WantsToLeave);
}
