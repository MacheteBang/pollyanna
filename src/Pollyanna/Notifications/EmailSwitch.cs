using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Notifications;

/// <summary>
/// A person's one "Email me about Pollyanna events" setting (spec §15 "Opting out"). The sender
/// reads it again for every row it sends; turning it off also drops what is waiting, so turning
/// it back on only affects later notifications.
/// </summary>
public sealed class EmailSwitch(PollyannaDb db)
{
    public async Task SetAsync(Guid personId, bool on)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.People.Where(p => p.Id == personId).ExecuteUpdateAsync(s => s.SetProperty(p => p.EmailsOn, on));
        if (!on)
            await db.Notifications.Where(n => n.PersonId == personId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    }
}
