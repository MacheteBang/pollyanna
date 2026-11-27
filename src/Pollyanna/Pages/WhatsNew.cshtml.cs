using Pollyanna.Auth;
using Pollyanna.Happenings;
using Pollyanna.Live;
using Pollyanna.WhatsNew;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Pollyanna.Pages;

/// <summary>
/// "What's new" without the script (spec §7, §16): the person's notifications, each a link to
/// where it's dealt with, with a "Dismiss" form button. No cloth.
/// </summary>
public sealed class WhatsNewModel(WhatsNewList whatsNew, TimeProvider clock, LiveSignals live) : PageModel
{
    public IReadOnlyList<WhatsNewRow> Rows { get; private set; } = [];
    public DateTime Now { get; private set; }

    public async Task OnGetAsync()
    {
        Rows = await whatsNew.RowsAsync(User.PersonId());
        Now = clock.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Dismisses one row on purpose, then shows the page again. Your other tabs get a hint (spec
    /// §16, your own action); the panel's background post names its own tab in
    /// <see cref="LiveSignals.TabHeader"/> to be left out.
    /// </summary>
    public async Task<IActionResult> OnPostDismissAsync(Guid @event, HappeningKind kind)
    {
        await whatsNew.DismissAsync(User.PersonId(), @event, kind);
        live.OwnAction(User.PersonId(), @event, "Dismissed", Request.Headers[LiveSignals.TabHeader].FirstOrDefault());
        return Redirect("/whats-new");
    }

    /// <summary>A post naming no action this page has.</summary>
    public IActionResult OnPost() => NotFound();
}
