using Pollyanna.Auth;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages.Auth;

/// <summary>
/// <c>/auth/failed</c>: a sign-in that didn't work (spec §3). An open provider's refusal shows its
/// reason and the address just used, read only from the protected refusal the callback sent, then
/// every provider's button again. Any other failure keeps the general wording.
/// </summary>
public sealed class FailedModel(RefusedSignIn refusals, ProviderRegistry providers) : PageModel
{
    public RefusedSignIn.Refusal? Refusal { get; private set; }
    /// <summary>The provider that refused; its label names it in the "not verified" wording.</summary>
    public string ProviderLabel { get; private set; } = "";
    /// <summary>Every provider's button, in configured order, carrying the return URL.</summary>
    public IReadOnlyList<(string Label, string Href)> Choices { get; private set; } = [];
    public bool ProviderProblem { get; private set; }

    public void OnGet(string? reason)
    {
        ProviderProblem = reason == "provider";
        Refusal = refusals.Read(Request.Query[RefusedSignIn.QueryKey]);
        if (Refusal is null)
            return;

        ProviderLabel = providers.ByKey(Refusal.Provider)?.Label ?? Refusal.Provider;
        var target = Refusal.ReturnUrl is { } r && Url.IsLocalUrl(r) && r != "/" ? r : null;
        var query = target is null ? "" : "?returnUrl=" + Uri.EscapeDataString(target);
        Choices = [.. providers.Select(p => (p.Label, p.LoginPath + query))];
    }
}
