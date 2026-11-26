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

    public IReadOnlyList<ParticipantRow> Participants { get; private set; } = [];
    public IReadOnlyList<Invitation> Invitations { get; private set; } = [];
    public IReadOnlyList<HouseholdRow> Households { get; private set; } = [];
    public IReadOnlyList<ExclusionRow> Exclusions { get; private set; } = [];
    public IReadOnlyList<PersonRow> Organizers { get; private set; } = [];
    /// <summary>
    /// The add-people control's ticklist before the draw, and the late-joiner picker's list after
    /// it: people the viewer knows who aren't in yet (spec §5.4, §7).
    /// </summary>
    public IReadOnlyList<PersonRow> Known { get; private set; } = [];
    /// <summary>The organizer picker's list: people the viewer knows who don't organize it yet (spec §4).</summary>
    public IReadOnlyList<PersonRow> KnownNotOrganizing { get; private set; } = [];
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

        if (Membership.PeopleEditable || Membership.IsLive)
            Known = await events.KnownRowsAsync(Me, names.Keys);
        if (Membership.Editable)
            KnownNotOrganizing = await events.KnownRowsAsync(Me, [.. Organizers.Select(o => o.Id)]);

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

    /// <summary>A post naming no action this page has, such as one it used to have.</summary>
    public IActionResult OnPost() => NotFound();

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

    /// <summary>A picked person the organizer may not pick (spec §7): refused like any other bad input.</summary>
    async Task<bool> MayPickAsync(Guid? personId) =>
        personId is not { } p || await events.KnownTo(Me).AnyAsync(x => x.Id == p);

    // After the draw, adding and removing people also say whether to patch or redraw (spec §5.4).
    // Before it, people are added through the add-people control instead. A typed address only
    // finds someone, since invitations can't be used while the names are drawn.
    public async Task<IActionResult> OnPostAddPersonAsync(Guid id, Guid? personId, string? email, DrawChange? change, Guid? householdId)
    {
        if (!await LoadAsOrganizerAsync(id) || !Membership.IsLive || !await MayPickAsync(personId))
            return NotFound();
        var (person, refusal) = await events.PickOneAsync(personId, email, EventManager.NobodyForLateJoiner);
        return Finish(refusal ?? await events.AddParticipantAsync(Membership, Me, person!.Id, change, householdId), Here("people"));
    }

    /// <summary>
    /// The add-people control (spec §7). A ticked person the organizer may not pick is refused
    /// like any other bad input, so the list can't be bypassed by posting IDs.
    /// </summary>
    public async Task<IActionResult> OnPostAddPeopleAsync(Guid id, List<Guid> personIds, string? email)
    {
        if (!await LoadAsOrganizerAsync(id))
            return NotFound();
        var ticked = personIds.Distinct().ToList();
        if (await events.KnownTo(Me).CountAsync(p => ticked.Contains(p.Id)) != ticked.Count)
            return NotFound();
        return Finish(await events.AddPeopleAsync(Membership, Me, ticked, email), Here("people"));
    }

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

    /// <summary>The organizer picker (spec §4): someone from the list, or an address that finds an existing person.</summary>
    public async Task<IActionResult> OnPostAddOrganizerAsync(Guid id, Guid? personId, string? email)
    {
        if (!await LoadAsOrganizerAsync(id) || !await MayPickAsync(personId))
            return NotFound();
        var (person, refusal) = await events.PickOneAsync(personId, email, EventManager.NobodyForOrganizer);
        return Finish(refusal ?? await events.AddOrganizerAsync(Membership, Me, person!.Id), Here("organizers"));
    }

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
