using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Pollyanna.Pages.Events;

/// <summary>
/// "Questions for you" (spec §6, §7, §12): the thread where the viewer is the recipient, read as a
/// conversation, with a reply box and "Close this conversation". It is found by the viewer's own
/// recipient ID, and says nothing about who the giver is. Opening the page counts its messages as
/// read; a question stays "new" until it's answered.
/// </summary>
public sealed class QuestionsModel(PollyannaDb db, EventManager events, TimeProvider clock) : EventPageModel(db)
{
    public QuestionThread? Thread { get; private set; }

    /// <summary>Today in UTC, for the day lines the page carries without the script (the script redoes them in local time).</summary>
    public DateOnly Today { get; private set; }

    /// <summary>The giver wrote last and the conversation is open: replying is the thing to do, so it sits on the cloth.</summary>
    public bool Unanswered => Thread is { Closed: false, Notes: [.., { Sender: MessageSender.Giver }] };

    /// <summary>Whether <paramref name="note"/> is one of the giver's questions since the viewer last replied, while unanswered.</summary>
    public bool IsNew(Note note) =>
        Unanswered && note.Sender == MessageSender.Giver
        && note.Seq > Thread!.Notes.Where(n => n.Sender == MessageSender.Recipient).Select(n => n.Seq).DefaultIfEmpty(0).Max();

    /// <summary>The day line (spec §7): "Today", the weekday within the week, otherwise the date ("December 1", with the year when it isn't this one).</summary>
    public static string DayLine(DateOnly day, DateOnly today) => Format.DayLine(day, today);

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView || !Membership.IsParticipant)
            return NotFound();
        if (!Membership.IsLive)
            return Redirect($"/events/{id}");
        Thread = await events.RecipientThreadAsync(Membership, Me, markRead: true);
        Today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
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
