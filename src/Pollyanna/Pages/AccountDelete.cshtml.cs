using Pollyanna.Auth;
using Pollyanna.Events;
using Pollyanna.Live;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages;

/// <summary>
/// Deleting one's account, after a confirmation step (spec §4, §7). The page says what stands in
/// the way and which events it leaves; once deleted, the session ends.
/// </summary>
public sealed class AccountDeleteModel(EventManager events, LiveConnections live) : PageModel
{
    public DeletionCheck Check { get; private set; } = null!;

    public async Task OnGetAsync() => Check = await events.CheckDeletionAsync(User.PersonId());

    public async Task<IActionResult> OnPostAsync()
    {
        var outcome = await events.DeleteAccountAsync(User.PersonId());
        if (!outcome.Ok)
        {
            TempData["Error"] = outcome.Message;
            return LocalRedirect("/account/delete");
        }
        // Every session went with the account, so its live streams end on every device (spec §16).
        live.EndPerson(User.PersonId());
        await HttpContext.SignOutAsync(SessionAuthenticationHandler.SchemeName);
        return LocalRedirect("/auth/deleted");
    }
}
