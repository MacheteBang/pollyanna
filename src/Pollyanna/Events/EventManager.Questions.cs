using Pollyanna.Data;
using Pollyanna.Notifications.Kinds;

namespace Pollyanna.Events;

/// <summary>One passed note as one side of the thread sees it; <see cref="IsNew"/> only for the other side's.</summary>
public sealed record Note(int Seq, MessageSender Sender, string Body, DateTime CreatedAt, bool IsNew);

/// <summary>
/// An anonymous question thread (spec §6) as one side sees it. It never says who the giver is:
/// the giver reaches it through their own giver tag, the recipient as the recipient.
/// </summary>
public sealed record QuestionThread(IReadOnlyList<Note> Notes, bool Closed)
{
    /// <summary>The other side's messages this viewer hasn't seen yet.</summary>
    public int Unread => Notes.Count(n => n.IsNew);

    /// <summary>The last message shown, for marking the thread read up to it.</summary>
    public int Upto => Notes.Count == 0 ? 0 : Notes[^1].Seq;
}

/// <summary>
/// The anonymous question box (spec §6): who may read and write, and what a message may say.
/// The threads themselves, and how each side reaches theirs, are <see cref="SealedHat"/>'s.
/// </summary>
public sealed partial class EventManager
{
    public const int MessageMax = 1000;

    /// <summary>Why <paramref name="m"/> can't use the question box now, if they can't: it takes messages only while drawn.</summary>
    static Outcome? Refusal(Membership m) =>
        !m.IsParticipant ? Outcome.Fail("You aren't in this event.")
        : m.IsOver ? Outcome.Fail("The exchange is over, so conversations are read-only now.")
        : !m.IsLive ? Outcome.Fail("Names haven't been drawn.")
        : null;

    /// <summary>The thread with the signed-in giver's own recipient; null without a slip.</summary>
    public async Task<QuestionThread?> GiverThreadAsync(Membership m, Guid me)
    {
        if (!m.ThreadsReadable)
            return null;
        return await hat.ThreadAsync(m.Event.Id, me, MessageSender.Giver, live: m.IsLive);
    }

    /// <summary>
    /// The thread with the signed-in person's giver ("Your giver asks…"); null if
    /// names aren't drawn. With <paramref name="markRead"/>, what it returns counts as seen.
    /// </summary>
    public async Task<QuestionThread?> RecipientThreadAsync(Membership m, Guid me, bool markRead = false)
    {
        if (!m.ThreadsReadable)
            return null;
        return await hat.ThreadAsync(m.Event.Id, me, MessageSender.Recipient, live: m.IsLive, markRead);
    }

    /// <summary>The giver asks their recipient something (spec §6). Only the giver starts a thread.</summary>
    public async Task<Outcome> AskAsync(Membership m, Guid me, string? body)
    {
        if (Refusal(m) is { } refusal)
            return refusal;
        var thread = await hat.ThreadStateAsync(m.Event.Id, me, MessageSender.Giver);
        if (thread is null)
            return Outcome.Fail("Your slip can't be found. Ask an organizer to draw names again.");
        return await PostAsync(m.Event.Id, thread, MessageSender.Giver, me, body, "Sent. Your recipient won't see your name.");
    }

    /// <summary>The recipient answers their giver, once the giver has written.</summary>
    public async Task<Outcome> ReplyAsync(Membership m, Guid me, string? body)
    {
        if (Refusal(m) is { } refusal)
            return refusal;
        var thread = await hat.ThreadStateAsync(m.Event.Id, me, MessageSender.Recipient);
        if (thread is null || !thread.Started)
            return Outcome.Fail("There's nothing to reply to yet. Your giver writes first.");
        return await PostAsync(m.Event.Id, thread, MessageSender.Recipient, me, body, "Reply sent.");
    }

    async Task<Outcome> PostAsync(Guid eventId, ThreadState thread, MessageSender sender, Guid me, string? body, string sent)
    {
        if (thread.Closed)
            return Outcome.Fail(sender == MessageSender.Giver
                ? "Your recipient has closed this conversation."
                : "You closed this conversation. Reopen it to write again.");
        body = (body ?? "").Replace("\r\n", "\n").Trim();
        if (body.Length == 0)
            return Outcome.Fail("Write something first.");
        if (body.Length > MessageMax)
            return Outcome.Fail($"Keep it under {MessageMax:N0} characters.");
        if (!limit.TryTake(me))
            return Outcome.Fail("You've sent a lot of messages this hour. Try again a little later.");

        // "A question for you", saved with the message. Nothing is ever queued for the giver (docs/adr/0001).
        if (sender == MessageSender.Giver)
            await outbox.QueueAsync(new Question.Cause(eventId, thread.RecipientId, me));
        return await hat.AddMessageAsync(thread, sender, body, Message.HourOf(Now))
            ? Outcome.Done(sent)
            : Outcome.Fail("Names were drawn again, so that conversation is gone.");
    }

    /// <summary>The recipient closes or reopens their thread. Nobody else is told (spec §6).</summary>
    public async Task<Outcome> SetThreadClosedAsync(Membership m, Guid me, bool closed)
    {
        if (Refusal(m) is { } refusal)
            return refusal;
        if (!await hat.SetClosedAsync(m.Event.Id, me, closed))
            return Outcome.Fail("There's no conversation here.");
        return Outcome.Done(closed
            ? "Conversation closed. Your giver can't send more questions until you reopen it."
            : "Conversation reopened. Your giver can write again.");
    }
}
