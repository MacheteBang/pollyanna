using Pollyanna.Auth;
using Pollyanna.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages.Auth;

/// <summary><c>/auth/login</c>: starts the OIDC flow, then returns to the page first asked for.</summary>
public sealed class LoginModel(PollyannaSettings settings, ILoggerFactory loggers, IWebHostEnvironment env) : PageModel
{
    // ---------- PROTOTYPE (wayfinder #13): sign-in chooser. Throwaway. ----------
    /// <summary>A, B or C when the prototype is on (Development only, ?variant=); null otherwise.</summary>
    public string? ProtoVariant { get; private set; }
    public string ProtoReturnUrl { get; private set; } = "/";
    // ---------- end PROTOTYPE ----------

    public async Task<IActionResult> OnGetAsync(string? returnUrl, string? variant)
    {
        var target = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";

        // PROTOTYPE: show the chooser instead of going straight to the provider.
        if (env.IsDevelopment() && variant?.ToUpperInvariant() is "A" or "B" or "C")
        {
            ProtoVariant = variant.ToUpperInvariant();
            ProtoReturnUrl = target;
            return Page();
        }

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
