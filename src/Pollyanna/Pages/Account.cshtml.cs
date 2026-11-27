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
    PersonSignIn signIn) : PageModel
{
    /// <summary>One login on the list: its provider's label (null when the provider is no longer set up) and the email it last sent.</summary>
    public sealed record LoginRow(Guid Id, string Label, bool NoLongerSetUp, string? LastEmail);

    public IReadOnlyList<LoginRow> Logins { get; private set; } = [];
    /// <summary>The "Add" buttons: every provider in configured order, only when there are several.</summary>
    public IReadOnlyList<Provider> AddChoices => providers.Count > 1 ? providers : [];
    /// <summary>Whether to show the email switch: only with a mail relay configured.</summary>
    public bool EmailsAvailable => outbox.Enabled;
    public bool EmailsOn { get; private set; }
    /// <summary>The page head's small line: the person's name and email, whichever they have.</summary>
    public string? NameAndEmail { get; private set; }

    public async Task OnGetAsync()
    {
        var me = User.PersonId();
        var person = await db.People.Where(p => p.Id == me).Select(p => new { p.EmailsOn, p.DisplayName, p.Email }).SingleAsync();
        EmailsOn = person.EmailsOn;
        var parts = new[] { person.DisplayName, person.Email }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        NameAndEmail = parts.Count == 0 ? null : string.Join(" · ", parts);
        var logins = await db.Logins.AsNoTracking().Where(l => l.PersonId == me)
            .OrderBy(l => l.CreatedAt).ThenBy(l => l.Issuer).ThenBy(l => l.Subject).ToListAsync();
        Logins = [.. logins.Select(l =>
        {
            var (label, noLongerSetUp) = providers.LabelFor(l.Issuer);
            return new LoginRow(l.Id, label, noLongerSetUp, l.LastEmail);
        })];
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
        return await SignInFailure.ChallengeAsync(HttpContext, provider, AddLogin.Properties(User.PersonId()));
    }

    /// <summary>Removes one of this person's logins, any but the last (<see cref="PersonSignIn.RemoveLoginAsync"/>).</summary>
    public async Task<IActionResult> OnPostRemoveLoginAsync(Guid loginId)
    {
        switch (await signIn.RemoveLoginAsync(User.PersonId(), loginId))
        {
            case RemoveLoginResult.NotYours:
                return NotFound();
            case RemoveLoginResult.LastLogin:
                TempData["Error"] = "That's your only way to sign in, so it can't be removed. To leave Pollyanna, delete your account instead.";
                break;
            default:
                TempData["Notice"] = "Removed.";
                break;
        }
        return LocalRedirect("/account");
    }
}
