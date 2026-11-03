using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages.Events;

/// <summary>
/// The organizer's page (spec §7): status, people and invitations, households and extra rules,
/// organizers, details. Anyone who isn't an organizer of the event gets "not found" (spec §8).
/// Every action posts, checks the organizer role, and comes back here with what happened.
/// Organizers see who has viewed their assignment and who is done shopping, never who drew whom
/// until the pairings are revealed.
/// </summary>
public sealed class ManageModel(PollyannaDb db, EventManager events) : EventPageModel(db)
{
    public sealed record ParticipantRow(Guid Id, string Name, bool HasPicture, string? Email, bool HasNotes, Guid? HouseholdId,
        bool DoneShopping, bool WantsToLeave) : PersonRow(Id, Name, HasPicture);
    public sealed record HouseholdRow(Guid Id, string Name, IReadOnlyList<ParticipantRow> Members);
    public sealed record ExclusionRow(Guid Id, string A, string B);
    public sealed record Pickable(Guid Id, string Label);

    public IReadOnlyList<ParticipantRow> Participants { get; private set; } = [];
    public IReadOnlyList<Invitation> Invitations { get; private set; } = [];
    public IReadOnlyList<HouseholdRow> Households { get; private set; } = [];
    public IReadOnlyList<ExclusionRow> Exclusions { get; private set; } = [];
    public IReadOnlyList<PersonRow> Organizers { get; private set; } = [];
    public IReadOnlyList<Pickable> CanJoin { get; private set; } = [];
    public IReadOnlyList<Pickable> CanOrganize { get; private set; } = [];
    public IReadOnlyList<Event> PreviousCandidates { get; private set; } = [];
    /// <summary>Before the draw: what blocks it and whether to warn (spec §5.1).</summary>
    public DrawCheck? Check { get; private set; }
    /// <summary>After the draw: who has opened their reveal card (spec §5.5).</summary>
    public IReadOnlySet<Guid> Viewed { get; private set; } = new HashSet<Guid>();
    /// <summary>Once revealed: who drew whom (spec §4).</summary>
    public IReadOnlyList<Pairing>? Pairings { get; private set; }
    /// <summary>In drawn: revealing now needs the "before the exchange" confirmation (spec §4).</summary>
    public bool RevealNeedsConfirmation { get; private set; }
    public Guid MeId => Me;

    // The details form, re-shown with what was typed when it doesn't save.
    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? BudgetText { get; set; }
    [BindProperty] public DateOnly? ExchangeDate { get; set; }
    [BindProperty] public Guid? PreviousEventId { get; set; }
    [BindProperty] public int NoRepeatDepth { get; set; } = 1;
    public string? DetailsError { get; private set; }

    string Here(string section) => $"/events/{Event.Id}/manage#{section}";

    async Task<bool> LoadAsOrganizerAsync(Guid id) => await LoadMembershipAsync(id) && Membership.IsOrganizer;

    public async Task<IActionResult> OnGetAsync(Guid id)
    {
        if (!await LoadAsOrganizerAsync(id))
            return NotFound();
        Name = Event.Name;
        BudgetText = Event.BudgetText;
        ExchangeDate = Event.ExchangeDate;
        PreviousEventId = Event.PreviousEventId;
        NoRepeatDepth = Event.NoRepeatDepth;
        await LoadPageAsync();
        return Page();
    }

    async Task LoadPageAsync()
    {
        var id = Event.Id;
        Participants = [.. (await Db.Participants.Where(p => p.EventId == id)
            .Join(Db.People, p => p.PersonId, person => person.Id, (p, person) => new ParticipantRow(
                person.Id, person.DisplayName ?? "Former participant",
                Db.PersonPictures.Any(x => x.PersonId == person.Id),
                person.Email, p.Notes != "", p.HouseholdId, p.DoneShopping, p.WantsToLeave))
            .ToListAsync())
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)];

        Invitations = await Db.Invitations.AsNoTracking().Where(i => i.EventId == id).OrderBy(i => i.CreatedAt).ToListAsync();

        Households = [.. (await Db.Households.AsNoTracking().Where(h => h.EventId == id).ToListAsync())
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(h => new HouseholdRow(h.Id, h.Name, Participants.Where(p => p.HouseholdId == h.Id).ToList()))];

        var names = Participants.ToDictionary(p => p.Id, p => p.Name);
        Exclusions = (await Db.Exclusions.AsNoTracking().Where(x => x.EventId == id).ToListAsync())
            .Select(x => new ExclusionRow(x.Id, names.GetValueOrDefault(x.PersonA, "?"), names.GetValueOrDefault(x.PersonB, "?")))
            .OrderBy(x => x.A, StringComparer.CurrentCultureIgnoreCase).ThenBy(x => x.B, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Organizers = [.. (await Rows(Db.People.Where(p => Db.Organizers.Any(o => o.EventId == id && o.PersonId == p.Id))).ToListAsync())
            .OrderBy(o => o.Id != Me).ThenBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)];

        var signedIn = await events.SignedInPeople.AsNoTracking()
            .Select(p => new { p.Id, p.DisplayName, p.Email })
            .ToListAsync();
        var everyone = signedIn
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new Pickable(p.Id, p.Email is null ? p.DisplayName ?? "" : $"{p.DisplayName} — {p.Email}"))
            .ToList();
        CanJoin = everyone.Where(p => !names.ContainsKey(p.Id)).ToList();
        CanOrganize = everyone.Where(p => !Organizers.Any(o => o.Id == p.Id)).ToList();

        var candidates = await events.PreviousCandidatesAsync(Me, id);
        // A link made by someone else stays selectable, so saving the details doesn't drop it.
        if (Event.PreviousEventId is { } previous && !candidates.Any(c => c.Id == previous))
            candidates.Insert(0, await Db.Events.AsNoTracking().SingleAsync(e => e.Id == previous));
        PreviousCandidates = candidates;

        if (Membership.Allows(EventLifecycle.Draw))
            Check = await events.CheckDrawAsync(Membership);
        if (Membership.IsLive)
        {
            Viewed = await events.ViewedAsync(id, Participants.Select(p => p.Id));
            RevealNeedsConfirmation = events.RevealNeedsConfirmation(Event);
        }
        Pairings = await events.PairingsAsync(Membership);
    }

    // ---------- Actions ----------

    public async Task<IActionResult> OnPostOpenAsync(Guid id) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.OpenAsync(Membership, Me), Here("status")) : NotFound();

    public async Task<IActionResult> OnPostDrawAsync(Guid id) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.DrawAsync(Membership, Me), Here("status")) : NotFound();

    public async Task<IActionResult> OnPostRedrawAsync(Guid id) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.RedrawAsync(Membership), Here("status")) : NotFound();

    public async Task<IActionResult> OnPostRevealAsync(Guid id, bool confirmed) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.RevealAsync(Membership, Me, confirmed), Here("status")) : NotFound();

    public async Task<IActionResult> OnPostArchiveAsync(Guid id, bool keepMessages) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.ArchiveAsync(Membership, keepMessages), Here("status")) : NotFound();

    // After the draw, adding and removing people also say whether to patch or redraw (spec §5.4).
    public async Task<IActionResult> OnPostAddPersonAsync(Guid id, Guid personId, DrawChange? change, Guid? householdId) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.AddParticipantAsync(Membership, Me, personId, change, householdId), Here("people")) : NotFound();

    public async Task<IActionResult> OnPostInviteAsync(Guid id, string? email) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.InviteAsync(Membership, Me, email), Here("people")) : NotFound();

    public async Task<IActionResult> OnPostRemovePersonAsync(Guid id, Guid personId, DrawChange? change) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.RemoveParticipantAsync(Membership, personId, change), Here("people")) : NotFound();

    public async Task<IActionResult> OnPostCancelInviteAsync(Guid id, Guid invitationId) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.CancelInvitationAsync(Membership, invitationId), Here("people")) : NotFound();

    public async Task<IActionResult> OnPostSaveHouseholdAsync(Guid id, Guid? householdId, string? householdName, List<Guid> members) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.SaveHouseholdAsync(Membership, householdId, householdName, members), Here("rules")) : NotFound();

    public async Task<IActionResult> OnPostDeleteHouseholdAsync(Guid id, Guid householdId) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.DeleteHouseholdAsync(Membership, householdId), Here("rules")) : NotFound();

    public async Task<IActionResult> OnPostAddRuleAsync(Guid id, Guid personA, Guid personB) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.AddExclusionAsync(Membership, personA, personB), Here("rules")) : NotFound();

    public async Task<IActionResult> OnPostRemoveRuleAsync(Guid id, Guid exclusionId) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.RemoveExclusionAsync(Membership, exclusionId), Here("rules")) : NotFound();

    public async Task<IActionResult> OnPostAddOrganizerAsync(Guid id, Guid personId) =>
        await LoadAsOrganizerAsync(id) ? Finish(await events.AddOrganizerAsync(Membership, Me, personId), Here("organizers")) : NotFound();

    public async Task<IActionResult> OnPostRemoveOrganizerAsync(Guid id, Guid personId)
    {
        if (!await LoadAsOrganizerAsync(id))
            return NotFound();
        var outcome = await events.RemoveOrganizerAsync(Membership, personId);
        // Someone who stops organizing an event they're not in can't see it any more.
        if (outcome.Ok && personId == Me)
            return Finish(outcome, Membership.IsParticipant && Membership.ShownToParticipants ? $"/events/{id}" : "/");
        return Finish(outcome, Here("organizers"));
    }

    public async Task<IActionResult> OnPostDetailsAsync(Guid id)
    {
        if (!await LoadAsOrganizerAsync(id))
            return NotFound();
        var outcome = ModelState.IsValid
            ? await events.UpdateDetailsAsync(Membership, Me, Name, BudgetText, ExchangeDate, PreviousEventId, NoRepeatDepth)
            : Outcome.Fail("Check the exchange date and try again.");
        if (outcome.Ok)
            return Finish(outcome, Here("details"));

        DetailsError = outcome.Message;
        await LoadPageAsync();
        return Page();
    }
}
