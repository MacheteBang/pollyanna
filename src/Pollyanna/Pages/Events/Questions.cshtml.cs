using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc;

namespace Pollyanna.Pages.Events;

/// <summary>
/// "Your giver asks…" (spec §6, §7): the thread where the viewer is the recipient, with a
/// reply box and "Close this conversation". It is found by the viewer's own recipient ID, and
/// says nothing about who the giver is. Opening the page counts its messages as read.
/// </summary>
public sealed class QuestionsModel(PollyannaDb db, EventManager events) : EventPageModel(db)
{
    public QuestionThread? Thread { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView || !Membership.IsParticipant)
            return NotFound();
        if (!Membership.IsLive)
            return Redirect($"/events/{id}");
        Thread = await events.RecipientThreadAsync(Membership, Me, markRead: true);
        return Page();
    }

    public async Task<IActionResult> OnPostReplyAsync(Guid id, string? body)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView || !Membership.IsParticipant)
            return NotFound();
        return Finish(await events.ReplyAsync(Membership, Me, body), $"/events/{id}/questions#reply", at: "reply");
    }

    public async Task<IActionResult> OnPostCloseAsync(Guid id)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView || !Membership.IsParticipant)
            return NotFound();
        return Finish(await events.SetThreadClosedAsync(Membership, Me, closed: true), $"/events/{id}/questions");
    }

    public async Task<IActionResult> OnPostReopenAsync(Guid id)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView || !Membership.IsParticipant)
            return NotFound();
        return Finish(await events.SetThreadClosedAsync(Membership, Me, closed: false), $"/events/{id}/questions");
    }
}
