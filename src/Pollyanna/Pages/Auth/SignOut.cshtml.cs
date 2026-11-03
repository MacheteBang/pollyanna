using Pollyanna.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages.Auth;

/// <summary>
/// <c>POST /auth/signout</c>: ends the Pollyanna session only, not the provider's (spec §3).
/// </summary>
public sealed class SignOutModel : PageModel
{
    public IActionResult OnGet() => NotFound();

    public async Task<IActionResult> OnPostAsync()
    {
        await HttpContext.SignOutAsync(SessionAuthenticationHandler.SchemeName);
        return Redirect("/auth/signed-out");
    }
}
