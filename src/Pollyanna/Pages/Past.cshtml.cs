using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages;

/// <summary>Past events (spec §7): archived events the user took part in or organized, newest first.</summary>
public sealed class PastModel(PollyannaDb db) : PageModel
{
    public sealed record PastSlip(Guid Id, string Name, DateOnly ExchangeDate, string BudgetText, bool IsOrganizer, bool IsParticipant,
        int ParticipantCount, bool Revealed);

    public IReadOnlyList<PastSlip> Events { get; private set; } = [];

    public async Task OnGetAsync()
    {
        var me = User.PersonId();
        Events = await db.Events
            .Where(e => EventLifecycle.Archived.Contains(e.Status))
            .Where(e => e.Organizers.Any(o => o.PersonId == me) || e.Participants.Any(p => p.PersonId == me))
            .OrderByDescending(e => e.ExchangeDate).ThenBy(e => e.Name)
            .Select(e => new PastSlip(e.Id, e.Name, e.ExchangeDate, e.BudgetText,
                e.Organizers.Any(o => o.PersonId == me), e.Participants.Any(p => p.PersonId == me),
                e.Participants.Count, e.PairingsRevealed))
            .ToListAsync();
    }
}
