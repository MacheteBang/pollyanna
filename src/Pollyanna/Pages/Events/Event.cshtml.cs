using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages.Events;

/// <summary>
/// The event page (spec §7): details, the reveal card once names are drawn (with the giver's
/// question thread inside it), a link to the viewer's own giver's questions, the people
/// in the hat with their notes, the viewer's own notes editor and "Leave this event". Once
/// revealed, the full pairing list and both threads, read-only and named; once archived, the
/// list if it was revealed (or else the viewer's own slip) and any kept threads. An organizer who
/// isn't a participant goes to manage. Before reveal, only the viewer's own slip and threads are
/// ever looked up.
/// </summary>
public sealed class EventModel(PollyannaDb db, EventManager events) : EventPageModel(db)
{
    public sealed record ParticipantRow(Guid Id, string Name, bool HasPicture, string Notes) : PersonRow(Id, Name, HasPicture);

    public IReadOnlyList<ParticipantRow> Participants { get; private set; } = [];
    public string MyNotes { get; private set; } = "";
    public bool DoneShopping { get; private set; }
    public bool WantsToLeave { get; private set; }
    /// <summary>The viewer's own slip once names are drawn; null then means it can't be found (spec §5.3).</summary>
    public Slip? Slip { get; private set; }
    /// <summary>The viewer's thread with their recipient, inside the reveal card.</summary>
    public QuestionThread? AskThread { get; private set; }
    /// <summary>The viewer's thread with their own giver, on <c>/events/{id}/questions</c>.</summary>
    public QuestionThread? QuestionsThread { get; private set; }
    /// <summary>Just asked something: show the card unfolded, where the thread is.</summary>
    public bool Unfold { get; private set; }
    /// <summary>Once revealed: who drew whom (spec §4).</summary>
    public IReadOnlyList<Pairing>? Pairings { get; private set; }
    /// <summary>Once revealed: who drew the viewer, to name them on the viewer's questions thread (spec §6).</summary>
    public PersonRow? MyGiver { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        if (!Membership.IsParticipant)
            return Redirect($"/events/{id}/manage");

        Participants = await Db.Participants.Where(p => p.EventId == id)
            .Join(Db.People, p => p.PersonId, person => person.Id, (p, person) => new ParticipantRow(
                person.Id, person.DisplayName ?? "Former participant",
                Db.PersonPictures.Any(x => x.PersonId == person.Id), p.Notes))
            .ToListAsync();
        Participants = [.. Participants.OrderBy(p => p.Id != Me).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];
        MyNotes = Participants.Single(p => p.Id == Me).Notes;

        var mine = await Db.Participants.Where(p => p.EventId == id && p.PersonId == Me)
            .Select(p => new { p.DoneShopping, p.WantsToLeave }).SingleAsync();
        DoneShopping = mine.DoneShopping;
        WantsToLeave = mine.WantsToLeave;
        Slip = await events.SlipAsync(Membership, Me);
        if (Slip is not null)
            AskThread = await events.GiverThreadAsync(Membership, Me);
        QuestionsThread = await events.RecipientThreadAsync(Membership, Me);
        Unfold = Slip is not null && Membership.IsLive && TempData["Unfold"] is true;
        Pairings = await events.PairingsAsync(Membership);
        MyGiver = Pairings?.FirstOrDefault(p => p.Recipient.Id == Me)?.Giver;
        return Page();
    }

    /// <summary>
    /// The reveal card was opened: posted by the script when it unfolds, or by "Fold it back up"
    /// without the script. Clears the card's markers, and marks the thread read up to the last
    /// message the card showed; says nothing back.
    /// </summary>
    public async Task<IActionResult> OnPostSeenAsync(Guid id, int upto)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        await events.MarkSlipSeenAsync(Membership, Me, upto);
        return LocalRedirect($"/events/{id}#slip");
    }

    /// <summary>The giver asks their recipient something; the card comes back unfolded at the thread.</summary>
    public async Task<IActionResult> OnPostAskAsync(Guid id, string? body)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        TempData["Unfold"] = true;
        return Finish(await events.AskAsync(Membership, Me, body), $"/events/{id}#ask", at: "ask");
    }

    public async Task<IActionResult> OnPostShoppingAsync(Guid id, bool done)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        return Finish(await events.SetDoneShoppingAsync(Membership, Me, done), $"/events/{id}#shopping");
    }

    public async Task<IActionResult> OnPostStayAsync(Guid id)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        return Finish(await events.StayAsync(Membership, Me), $"/events/{id}");
    }

    public async Task<IActionResult> OnPostNotesAsync(Guid id, string? notes)
    {
        if (!await LoadMembershipAsync(id) || !Membership.CanView)
            return NotFound();
        return Finish(await events.UpdateNotesAsync(Membership, Me, notes), $"/events/{id}#your-notes");
    }
}
