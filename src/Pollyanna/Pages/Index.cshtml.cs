using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Events;
using Pollyanna.Live;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages;

/// <summary>
/// Home (spec §7): one panel per event the user takes part in or organizes, with the one thing
/// waiting on them, or the empty home. Drafts show only to their organizers; archived events
/// are under "Past events". There is no dot or badge: the waiting line is the signal.
/// </summary>
public sealed class IndexModel(PollyannaDb db, EventManager events) : PageModel
{
    public sealed record EventPanel(Guid Id, string Name, EventStatus Status, DateOnly ExchangeDate, string BudgetText, bool IsOrganizer,
        int ParticipantCount, int InvitationCount, bool IsParticipant)
    {
        public Waiting Waiting { get; set; }

        /// <summary>
        /// The waiting line is left out of this render: a live refetch a hint set off, in a drawn
        /// event the viewer takes part in, where the line can be about their recipient or giver (spec §16).
        /// </summary>
        public bool Withheld { get; set; }

        /// <summary>Where the event opens: its own page, or the organizing page for an organizer who doesn't take part.</summary>
        public string Href => IsParticipant ? $"/events/{Id}" : $"/events/{Id}/manage";
    }

    public IReadOnlyList<EventPanel> Events { get; private set; } = [];
    public string? Email { get; private set; }
    public bool? EmailVerified { get; private set; }

    /// <summary>The waiting line's words and where it is done (spec §7); null for none.</summary>
    public static (string Text, string Href)? Line(EventPanel e) => e.Waiting switch
    {
        Waiting.WriteNotes => ("Write your notes", $"/events/{e.Id}"),
        Waiting.NothingUntilDrawn => ("Nothing to do until names are drawn", $"/events/{e.Id}"),
        Waiting.SeeWhoYoureGivingTo => ("See who you're giving to", $"/events/{e.Id}"),
        Waiting.GivingToSomeoneNew => ("You're giving to someone new", $"/events/{e.Id}"),
        Waiting.RecipientUpdatedNotes => ("Your recipient updated their notes", $"/events/{e.Id}"),
        Waiting.GiverAskedYou => ("Your giver asked you something", $"/events/{e.Id}/questions"),
        Waiting.ReadyToOpen => ("Ready to open", $"/events/{e.Id}/manage"),
        Waiting.ReadyToDrawNames => ("Ready to draw names", $"/events/{e.Id}/manage"),
        Waiting.SomeoneWantsToLeave => ("Someone wants to leave", $"/events/{e.Id}/manage"),
        _ => null,
    };

    public async Task OnGetAsync()
    {
        var me = User.PersonId();

        Events = await db.Events
            .Where(e => e.Organizers.Any(o => o.PersonId == me) || e.Participants.Any(p => p.PersonId == me))
            .Where(e => EventLifecycle.Active.Contains(e.Status))
            .Where(e => EventLifecycle.ShownToParticipants.Contains(e.Status) || e.Organizers.Any(o => o.PersonId == me))
            .OrderBy(e => e.ExchangeDate).ThenBy(e => e.Name)
            .Select(e => new EventPanel(e.Id, e.Name, e.Status, e.ExchangeDate, e.BudgetText,
                e.Organizers.Any(o => o.PersonId == me), e.Participants.Count, e.Invitations.Count,
                e.Participants.Any(p => p.PersonId == me)))
            .ToListAsync();

        var pairParts = Request.CarriesPairParts();
        foreach (var e in Events)
        {
            e.Withheld = !pairParts && e.IsParticipant && EventLifecycle.Live.Contains(e.Status);
            if (!e.Withheld)
                e.Waiting = await events.WaitingAsync(me, e.Id, e.Status, e.IsOrganizer, e.IsParticipant);
        }

        var person = await db.People.Where(p => p.Id == me).Select(p => new { p.Email, p.EmailVerified }).SingleAsync();
        Email = person.Email;
        EmailVerified = person.EmailVerified;
    }
}
