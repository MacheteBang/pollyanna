using Pollyanna.Auth;
using Pollyanna.Events;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages;

/// <summary>
/// Deleting one's account, after a confirmation step (spec §4, §7). The page says what stands in
/// the way and which events it leaves; once deleted, the session ends.
/// </summary>
public sealed class AccountDeleteModel(EventManager events) : PageModel
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
        await HttpContext.SignOutAsync(SessionAuthenticationHandler.SchemeName);
        return LocalRedirect("/auth/deleted");
    }
}
