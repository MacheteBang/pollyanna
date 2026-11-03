using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Events;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages.Events;

/// <summary>Shared by the pages of one event: who the viewer is in it, and the people lists.</summary>
public abstract class EventPageModel(PollyannaDb db) : PageModel
{
    protected PollyannaDb Db => db;
    protected Guid Me => User.PersonId();

    public Membership Membership { get; private set; } = null!;
    public Event Event => Membership.Event;

    protected async Task<bool> LoadMembershipAsync(Guid id)
    {
        var membership = await EventAccess.FindAsync(db, id, Me, HttpContext.RequestAborted);
        if (membership is null)
            return false;
        Membership = membership;
        return true;
    }

    /// <summary>
    /// Records the outcome for the next page to show, and goes there. With <paramref name="at"/>,
    /// the page shows it beside the form of that name (a <c>_Notice</c> with that NoticeAt) rather than at the top.
    /// </summary>
    protected IActionResult Finish(Outcome outcome, string url, string? at = null)
    {
        TempData[outcome.Ok ? "Notice" : "Error"] = outcome.Message;
        if (at is not null)
            TempData["NoticeAt"] = at;
        return LocalRedirect(url);
    }

    protected IQueryable<PersonRow> Rows(IQueryable<Person> people) =>
        people.Select(p => new PersonRow(p.Id, p.DisplayName ?? "Former participant", db.PersonPictures.Any(x => x.PersonId == p.Id)));
}
