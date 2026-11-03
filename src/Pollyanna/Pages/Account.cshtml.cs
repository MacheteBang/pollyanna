using Pollyanna.Auth;
using Pollyanna.Data;
using Pollyanna.Notifications;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Pages;

/// <summary>
/// Account (spec §7): name and email as the provider sent them, the email switch (§15) when a mail
/// relay is configured, sign out, and a link to delete the account.
/// </summary>
public sealed class AccountModel(PollyannaDb db, Outbox outbox, EmailSwitch emailSwitch) : PageModel
{
    public string DisplayName { get; private set; } = "";
    public string? Email { get; private set; }
    public bool? EmailVerified { get; private set; }
    /// <summary>Whether to show the email switch: only with a mail relay configured.</summary>
    public bool EmailsAvailable => outbox.Enabled;
    public bool EmailsOn { get; private set; }

    public async Task OnGetAsync()
    {
        var me = User.PersonId();
        var person = await db.People.AsNoTracking().SingleAsync(p => p.Id == me);
        DisplayName = person.DisplayName ?? "";
        Email = person.Email;
        EmailVerified = person.EmailVerified;
        EmailsOn = person.EmailsOn;
    }

    public async Task<IActionResult> OnPostEmailsAsync(bool on)
    {
        if (!outbox.Enabled)
            return NotFound();
        await emailSwitch.SetAsync(User.PersonId(), on);
        TempData["Notice"] = on ? "Emails are on." : "Emails are off.";
        return LocalRedirect("/account");
    }
}
