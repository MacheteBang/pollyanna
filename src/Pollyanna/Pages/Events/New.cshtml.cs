using Pollyanna.Auth;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages.Events;

/// <summary>
/// New event (spec §7). The creator becomes its organizer and chooses whether to take part.
/// Linked to an earlier event they organize, its people come along. The form adds nobody else:
/// people are added on the organizing page, where the creator lands (spec §4).
/// </summary>
public sealed class NewModel(EventManager events) : PageModel
{
    public sealed record Candidate(Guid Id, string Name, DateOnly ExchangeDate);

    [BindProperty] public string? Name { get; set; }
    [BindProperty] public string? BudgetText { get; set; }
    [BindProperty] public DateOnly? ExchangeDate { get; set; }
    [BindProperty] public Guid? PreviousEventId { get; set; }
    [BindProperty] public int NoRepeatDepth { get; set; } = 1;
    [BindProperty] public bool TakePart { get; set; } = true;

    public IReadOnlyList<Candidate> Candidates { get; private set; } = [];
    public string? Error { get; private set; }

    public async Task OnGetAsync()
    {
        await LoadCandidatesAsync();
        // The most recent event they organize is the usual "same group as last year".
        PreviousEventId = Candidates.FirstOrDefault()?.Id;
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var (ev, error) = ModelState.IsValid
            ? await events.CreateAsync(User.PersonId(), new NewEvent(Name ?? "", BudgetText ?? "", ExchangeDate, PreviousEventId, NoRepeatDepth, TakePart))
            : (null, "Check the exchange date and try again.");
        if (ev is not null)
        {
            TempData["Notice"] = "Draft created. Add people below, then open it when you're ready.";
            return LocalRedirect($"/events/{ev.Id}/manage");
        }

        Error = error;
        await LoadCandidatesAsync();
        return Page();
    }

    async Task LoadCandidatesAsync() =>
        Candidates = (await events.PreviousCandidatesAsync(User.PersonId()))
            .Select(c => new Candidate(c.Id, c.Name, c.ExchangeDate)).ToList();
}
