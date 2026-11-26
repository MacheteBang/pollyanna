using Pollyanna.Auth;
using Pollyanna.Pages.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages.Auth;

/// <summary>
/// <c>/auth/login</c>: with one provider, starts its OIDC flow straight away; with several, shows
/// the chooser, whose buttons start one provider's flow at <c>/auth/login/&lt;key&gt;</c>. Either way
/// the person returns to the page first asked for. The chooser remembers nothing and sets no
/// cookie (spec §3, §7).
/// </summary>
public sealed class LoginModel(ProviderRegistry providers, ILoggerFactory loggers) : PageModel
{
    /// <summary>The chooser's buttons, in configured order.</summary>
    public IReadOnlyList<ProviderButtonView> Choices { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(string? key, string? returnUrl)
    {
        var target = returnUrl is not null && Url.IsLocalUrl(returnUrl) ? returnUrl : "/";
        if (User.Identity?.IsAuthenticated == true)
            return LocalRedirect(target);

        var provider = key is null ? providers.Single : providers.ByKey(key);
        if (provider is null && key is not null)
            return NotFound();
        if (provider is null)
        {
            var query = target == "/" ? "" : "?returnUrl=" + Uri.EscapeDataString(target);
            Choices = [.. providers.Select(p => new ProviderButtonView(p, "Sign in with " + p.Label, p.LoginPath + query))];
            return Page();
        }

        // The challenge talks to the provider before redirecting (discovery, pushed authorization
        // requests), so a misconfigured client fails here rather than at the callback.
        try
        {
            await HttpContext.ChallengeAsync(provider.Scheme, new AuthenticationProperties { RedirectUri = target });
            return new EmptyResult();
        }
        catch (Exception e) when (!HttpContext.RequestAborted.IsCancellationRequested && !Response.HasStarted)
        {
            return LocalRedirect(SignInFailure.StartFailed(loggers.CreateLogger(SignInFailure.LoggerCategory), provider, e));
        }
    }
}
