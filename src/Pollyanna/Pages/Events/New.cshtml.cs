using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages.Events;

/// <summary>
/// New event (spec §7). The creator becomes its organizer and chooses whether to take part.
/// Linked to an earlier event they organize, its people come pre-ticked. Next to them is the
/// add-people control (spec §7), the same as on the manage page.
/// </summary>
public sealed class NewModel(PollyannaDb db, EventManager events) : PageModel
{
    public sealed record Candidate(Guid Id, string Name, DateOnly ExchangeDate, IReadOnlyList<PersonRow> People);

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? BudgetText { get; set; }
    [BindProperty] public DateOnly? ExchangeDate { get; set; }
    [BindProperty] public Guid? PreviousEventId { get; set; }
    [BindProperty] public int NoRepeatDepth { get; set; } = 1;
    [BindProperty] public bool TakePart { get; set; } = true;
    /// <summary>The add-people control's ticked people and typed address.</summary>
    [BindProperty(Name = "personIds")] public List<Guid> PersonIds { get; set; } = [];
    [BindProperty(Name = "email")] public string? Email { get; set; }

    public IReadOnlyList<Candidate> Candidates { get; private set; } = [];
    /// <summary>The add-people control's ticklist: the people the creator knows (spec §7).</summary>
    public IReadOnlyList<PersonRow> Known { get; private set; } = [];
    public string? Error { get; private set; }
    /// <summary>Who is ticked under each earlier event; everyone, until the form comes back.</summary>
    public Func<Guid, Guid, bool> IsTicked { get; private set; } = (_, _) => true;

    public static string CopyField(Guid eventId) => $"copy-{eventId}";

    public async Task OnGetAsync()
    {
        await LoadCandidatesAsync();
        // The most recent event they organize is the usual "same group as last year".
        PreviousEventId = Candidates.FirstOrDefault()?.Id;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var me = User.PersonId();
        // A ticked person the creator may not pick is refused like any other bad input (spec §4).
        var ticked = PersonIds.Distinct().ToList();
        if (!await events.AllKnownToAsync(me, ticked))
            return NotFound();
        var copy = PreviousEventId is { } p
            ? Request.Form[CopyField(p)].Select(v => Guid.TryParse(v, out var g) ? g : Guid.Empty).ToHashSet()
            : [];

        var (ev, error) = ModelState.IsValid
            ? await events.CreateAsync(me, new NewEvent(Name ?? "", BudgetText ?? "", ExchangeDate, PreviousEventId, NoRepeatDepth, TakePart, copy, ticked, Email))
            : (null, "Check the exchange date and try again.");
        if (ev is not null)
        {
            TempData["Notice"] = "Draft created. Add people below, then open it when you're ready.";
            return LocalRedirect($"/events/{ev.Id}/manage");
        }

        Error = error;
        await LoadCandidatesAsync();
        IsTicked = (eventId, personId) => eventId == PreviousEventId && copy.Contains(personId);
        return Page();
    }

    async Task LoadCandidatesAsync()
    {
        var me = User.PersonId();
        var candidates = await events.PreviousCandidatesAsync(me);
        var ids = candidates.Select(c => c.Id).ToList();
        var people = await db.Participants
            .Where(p => ids.Contains(p.EventId) && p.PersonId != me)
            .Join(db.People.Living(), p => p.PersonId, person => person.Id, (p, person) => new
            {
                p.EventId,
                Row = new PersonRow(person.Id, person.DisplayName ?? "", db.PersonPictures.Any(x => x.PersonId == person.Id)),
            })
            .ToListAsync();
        Known = await events.KnownRowsAsync(me, []);
        Candidates = candidates.Select(c => new Candidate(c.Id, c.Name, c.ExchangeDate,
            people.Where(p => p.EventId == c.Id).Select(p => p.Row)
                .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList())).ToList();
    }
}
