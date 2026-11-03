using Pollyanna.Auth;
using Pollyanna.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages.Auth;

/// <summary><c>/auth/login</c>: starts the OIDC flow, then returns to the page first asked for.</summary>
public sealed class LoginModel(PollyannaSettings settings, ILoggerFactory loggers) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? returnUrl)
    {
        var target = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(target);

        // The challenge talks to the provider before redirecting (discovery, pushed authorization
        // requests), so a misconfigured client fails here rather than at the callback.
        try
        {
            await HttpContext.ChallengeAsync("oidc", new AuthenticationProperties { RedirectUri = target });
            return new EmptyResult();
        }
        catch (Exception e) when (!HttpContext.RequestAborted.IsCancellationRequested && !Response.HasStarted)
        {
            return LocalRedirect(SignInFailure.StartFailed(loggers.CreateLogger(SignInFailure.LoggerCategory), e, settings.Issuer));
        }
    }
}
