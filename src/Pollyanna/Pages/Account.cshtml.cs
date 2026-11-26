using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Notifications;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages;

/// <summary>
/// Account (spec §7): the person's ways to sign in (list, add, remove), the email switch (§15)
/// when a mail relay is configured, sign out, and a link to delete the account.
/// </summary>
public sealed class AccountModel(PollyannaDb db, Outbox outbox, EmailSwitch emailSwitch, ProviderRegistry providers,
    ILoggerFactory loggers, ILogger<AccountModel> logger) : PageModel
{
    /// <summary>One login on the list: its provider's label (null when the provider is no longer set up) and the email it last sent.</summary>
    public sealed record LoginRow(Guid Id, string Label, bool NoLongerSetUp, string? LastEmail);

    public IReadOnlyList<LoginRow> Logins { get; private set; } = [];
    /// <summary>The "Add" buttons: every provider in configured order, only when there are several.</summary>
    public IReadOnlyList<Provider> AddChoices => providers.Count > 1 ? providers : [];
    /// <summary>Whether to show the email switch: only with a mail relay configured.</summary>
    public bool EmailsAvailable => outbox.Enabled;
    public bool EmailsOn { get; private set; }

    public async Task OnGetAsync()
    {
        var me = User.PersonId();
        EmailsOn = await db.People.Where(p => p.Id == me).Select(p => p.EmailsOn).SingleAsync();
        var logins = await db.Logins.AsNoTracking().Where(l => l.PersonId == me)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Issuer).ThenBy(l => l.Subject).ToListAsync();
        Logins = [.. logins.Select(l => providers.ByIssuer(l.Issuer) is { } p
            ? new LoginRow(l.Id, p.Label, false, l.LastEmail)
            : new LoginRow(l.Id, Uri.TryCreate(l.Issuer, UriKind.Absolute, out var u) ? u.Host : l.Issuer, true, l.LastEmail))];
    }

    public async Task<IActionResult> OnPostEmailsAsync(bool on)
    {
        if (!outbox.Enabled)
            return NotFound();
        await emailSwitch.SetAsync(User.PersonId(), on);
        TempData["Notice"] = on ? "Emails are on." : "Emails are off.";
        return LocalRedirect("/account");
    }

    /// <summary>
    /// Starts the round trip that adds a login from <paramref name="key"/>'s provider; the
    /// callback attaches it to this person (<see cref="AddLogin"/>). Only with several providers.
    /// </summary>
    public async Task<IActionResult> OnPostAddLoginAsync(string? key)
    {
        if (providers.Count < 2 || providers.ByKey(key) is not { } provider)
            return NotFound();
        try
        {
            await HttpContext.ChallengeAsync(provider.Scheme, AddLogin.Properties(User.PersonId()));
            return new EmptyResult();
        }
        catch (Exception e) when (!HttpContext.RequestAborted.IsCancellationRequested && !Response.HasStarted)
        {
            return LocalRedirect(SignInFailure.StartFailed(loggers.CreateLogger(SignInFailure.LoggerCategory), provider, e));
        }
    }

    /// <summary>
    /// Removes one of this person's logins, any but the last (spec §7). The session carries on,
    /// even when it was made through the login removed.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveLoginAsync(Guid loginId)
    {
        var me = User.PersonId();
        var login = await db.Logins.AsNoTracking().SingleOrDefaultAsync(l => l.Id == loginId && l.PersonId == me);
        if (login is null)
            return NotFound();
        // One statement, so two removals at once can't leave the person with none.
        var removed = await db.Logins
            .Where(l => l.Id == loginId && db.Logins.Any(o => o.PersonId == me && o.Id != loginId))
            .ExecuteDeleteAsync();
        if (removed == 0)
        {
            TempData["LastLogin"] = true;
            return LocalRedirect("/account");
        }

        if (providers.ByIssuer(login.Issuer) is { } provider)
            logger.LogInformation("Person {PersonId} removed a login through provider {Provider}.", me, provider.Key);
        else
            logger.LogInformation("Person {PersonId} removed a login through a provider that is no longer set up.", me);
        TempData["Notice"] = "Removed.";
        return LocalRedirect("/account");
    }
}
