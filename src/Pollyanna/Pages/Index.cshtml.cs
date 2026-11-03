using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages;

/// <summary>
/// Home (spec §7): the events the user takes part in or organizes, or the empty state.
/// Drafts show only to their organizers; archived events only count towards "Past events".
/// A dot marks a drawn event whose slip or question threads have news for the viewer (spec §5.2, §6).
/// </summary>
public sealed class IndexModel(PollyannaDb db, EventManager events) : PageModel
{
    public sealed record EventSlip(Guid Id, string Name, EventStatus Status, DateOnly ExchangeDate, string BudgetText, bool IsOrganizer, int ParticipantCount, int InvitationCount,
        bool IsParticipant)
    {
        /// <summary>What changed on the viewer's own slip or question threads, never naming anyone.</summary>
        public IReadOnlyList<string> Updates { get; set; } = [];
    }

    public IReadOnlyList<EventSlip> Events { get; private set; } = [];
    public int PastCount { get; private set; }
    public string? Email { get; private set; }
    public bool? EmailVerified { get; private set; }

    public async Task OnGetAsync()
    {
        var me = User.PersonId();

        var mine = db.Events.Where(e =>
            e.Organizers.Any(o => o.PersonId == me) || e.Participants.Any(p => p.PersonId == me));

        Events = await mine
            .Where(e => EventLifecycle.Active.Contains(e.Status))
            .Where(e => EventLifecycle.ShownToParticipants.Contains(e.Status) || e.Organizers.Any(o => o.PersonId == me))
            .OrderBy(e => e.ExchangeDate).ThenBy(e => e.Name)
            .Select(e => new EventSlip(e.Id, e.Name, e.Status, e.ExchangeDate, e.BudgetText,
                e.Organizers.Any(o => o.PersonId == me), e.Participants.Count, e.Invitations.Count,
                e.Participants.Any(p => p.PersonId == me)))
            .ToListAsync();

        var updates = await events.SlipUpdatesAsync(me, Events.Where(e => e.IsParticipant && EventLifecycle.Live.Contains(e.Status)).Select(e => e.Id));
        foreach (var e in Events)
            e.Updates = updates.GetValueOrDefault(e.Id) ?? [];

        PastCount = await mine.CountAsync(e => EventLifecycle.Archived.Contains(e.Status));

        var person = await db.People.Where(p => p.Id == me).Select(p => new { p.Email, p.EmailVerified }).SingleAsync();
        Email = person.Email;
        EmailVerified = person.EmailVerified;
    }
}
