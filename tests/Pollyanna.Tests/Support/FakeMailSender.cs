using System.Collections.Concurrent;
using Pollyanna.Notifications;

namespace Pollyanna.Tests.Support;

/// <summary>
/// Stands in for the mail relay (spec §11): records what would have been sent, or fails the way
/// a relay does when <see cref="Failure"/> is set.
/// </summary>
public sealed class FakeMailSender : IMailSender
{
    public ConcurrentQueue<OutgoingMail> Sent { get; } = new();

    /// <summary>Makes every send throw this, until set back to null.</summary>
    public Func<OutgoingMail, Exception>? Failure { get; set; }

    /// <summary>
    /// Runs once, at the next send, while that email is "on the wire": for changes that happen
    /// in the middle of an outbox run. Cleared before it runs.
    /// </summary>
    public Func<OutgoingMail, Task>? DuringNextSend { get => duringNextSend; set => duringNextSend = value; }
    Func<OutgoingMail, Task>? duringNextSend;

    public async Task SendAsync(OutgoingMail mail, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref duringNextSend, null) is { } during)
            await during(mail);
        if (Failure is { } fail)
            throw fail(mail);
        Sent.Enqueue(mail);
    }

    public Task CheckAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>The mail sent to <paramref name="address"/>.</summary>
    public List<OutgoingMail> To(string address) => Sent.Where(m => m.To == address).ToList();
}
