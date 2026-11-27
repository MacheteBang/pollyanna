using System.Security.Cryptography;
using Pollyanna.Configuration;
using Pollyanna.Data;
using Microsoft.EntityFrameworkCore;

namespace Pollyanna.Events;

/// <summary>A giver's own slip as stored: whom they draw and the markers on their card.</summary>
public sealed record GiverSlip(Guid RecipientId, bool Viewed, int NotesVersionSeen, bool RecipientChanged)
{
    /// <summary>The row it was read from, so a later change lands on that row and no other. Only <see cref="SealedHat"/> reads it.</summary>
    internal Guid Row { get; init; }
}

/// <summary>What a giver's waiting line on home is worked out from (spec §5.2, §7).</summary>
public sealed record GiverSlipUpdates(bool Viewed, bool RecipientChanged, int NotesVersionSeen, int RecipientNotesVersion);

/// <summary>
/// What "What's new" works out from one person's two rows in a drawn event (spec §16,
/// docs/adr/0008): as recipient, the newest unread question; as giver, the newest unread reply
/// and the markers with no time.
/// </summary>
/// <param name="Question">When the newest question the person hasn't read was sent; null if none.</param>
/// <param name="Reply">When the newest reply the person hasn't read was sent; null if none.</param>
/// <param name="Patched">A patch gave the person a new recipient they haven't looked at.</param>
/// <param name="NotesUpdated">Their recipient's notes changed since they last opened the card.</param>
/// <param name="ThreadChanged">Their recipient closed or reopened the thread since they last looked.</param>
/// <param name="Closed">Whether that thread is closed now.</param>
/// <param name="QuestionUpto">The <c>Seq</c> of that newest unread question: what dismissing its row reads up to.</param>
/// <param name="ReplyUpto">The <c>Seq</c> of that newest unread reply: what dismissing its row reads up to.</param>
public sealed record SlipNews(DateTime? Question, DateTime? Reply, bool Patched, bool NotesUpdated, bool ThreadChanged, bool Closed,
    int QuestionUpto = 0, int ReplyUpto = 0)
{
    public static readonly SlipNews None = new(null, null, false, false, false, false);
}

/// <summary>Which marker a deliberate dismissal in "What's new" moves (spec §16).</summary>
public enum SlipMarker
{
    /// <summary>The recipient's questions: <c>RecipientReadUpto</c> to the thread's last message.</summary>
    Question,
    /// <summary>The giver's replies: <c>GiverReadUpto</c> to the thread's last message.</summary>
    Reply,
    /// <summary>The giver's "recipient changed" notice.</summary>
    Patched,
    /// <summary>The giver's notes version seen, to the recipient's current one.</summary>
    NotesUpdated,
    /// <summary>The giver's "closed or reopened" flag.</summary>
    ThreadChanged,
}

/// <summary>One side's way into a thread: the recipient it's about, whether it's closed, whether anyone has written.</summary>
public sealed record ThreadState(Guid RecipientId, bool Closed, bool Started)
{
    /// <summary>The row it was read from, so a message lands in this thread and no other. Only <see cref="SealedHat"/> reads it.</summary>
    internal Guid Row { get; init; }
}

/// <summary>
/// The sealed draw (spec §5.2, docs/adr/0002): every read and write of assignments and their
/// question threads, and the only user of <see cref="DrawKey.GiverTag"/>. A giver's row is found
/// by computing their tag, a recipient's by their ID; givers are matched to tags only in memory.
/// Rows are written in random order with random IDs, and no assignment, message or tag leaves
/// this class; a slip or thread read here carries its row only for this class to act on later. Callers decide what may happen (the draw rules, statuses, who is asking) and own
/// the transaction; this class may save, but never begins or commits one.
/// </summary>
public sealed class SealedHat(PollyannaDb db, DrawKey key)
{
    /// <summary>The givers a patch in this scope changed, by event, in memory only (<see cref="TakePatchedGivers"/>).</summary>
    readonly List<(Guid EventId, Guid Giver)> patched = [];

    // ---------- Sealing and clearing ----------

    /// <summary>Seals a fresh draw, giver → recipient. Each slip starts having seen its recipient's current notes.</summary>
    public async Task SealAsync(Guid eventId, IReadOnlyDictionary<Guid, Guid> pairs)
    {
        var versions = await NotesVersionsAsync(eventId);
        await InsertShuffledAsync(pairs.Select(p => new Assignment
        {
            EventId = eventId,
            RecipientId = p.Value,
            GiverTag = key.GiverTag(eventId, p.Key),
            NotesVersionSeen = versions[p.Value],
        }));
    }

    /// <summary>Throws away every assignment of the event and its thread, for a redraw (spec §4).</summary>
    public async Task ClearAsync(Guid eventId)
    {
        await ClearThreadsAsync(eventId);
        await db.Assignments.Where(a => a.EventId == eventId).ExecuteDeleteAsync();
    }

    /// <summary>Deletes every question thread of the event, keeping the assignments (archiving without messages).</summary>
    public Task ClearThreadsAsync(Guid eventId) =>
        db.Messages.Where(x => db.Assignments.Any(a => a.Id == x.AssignmentId && a.EventId == eventId)).ExecuteDeleteAsync();

    // ---------- Unsealing ----------

    /// <summary>
    /// Every pairing of the event, each giver found by matching tags against
    /// <paramref name="people"/> in memory; null where no tag matches (e.g. the draw key changed).
    /// Only the no-repeat rule and the revealed list (spec §5.1, §5.2) call this; nothing is written back.
    /// </summary>
    public async Task<IReadOnlyList<(Guid? Giver, Guid Recipient)>> UnsealAsync(Guid eventId, IEnumerable<Guid> people)
    {
        var givers = GiversByTag(eventId, people);
        var rows = await db.Assignments.AsNoTracking().Where(a => a.EventId == eventId)
            .Select(a => new { a.GiverTag, a.RecipientId }).ToListAsync();
        return [.. rows.Select(r => (givers.TryGetValue(r.GiverTag, out var g) ? g : (Guid?)null, r.RecipientId))];
    }

    // ---------- Patches (spec §5.4) ----------

    /// <summary>
    /// The dropout patch: the dropout's giver takes over the dropout's recipient, if
    /// <paramref name="rules"/> allow it. Only that giver's row changes, and its "viewed" flag
    /// stays as it was. Both threads involving the dropout go: the one they asked in, and their
    /// giver's with them. False, with nothing changed, if not.
    /// </summary>
    public async Task<bool> PatchOutAsync(Guid eventId, Guid dropout, DrawRules rules)
    {
        var givers = GiversByTag(eventId, rules.People);
        var rows = await db.Assignments.Where(a => a.EventId == eventId).ToListAsync();
        var dropoutTag = key.GiverTag(eventId, dropout);
        var theirs = rows.SingleOrDefault(r => TagEquality.Equals(r.GiverTag, dropoutTag));
        var giverRow = rows.SingleOrDefault(r => r.RecipientId == dropout);
        if (theirs is null || giverRow is null || !givers.TryGetValue(giverRow.GiverTag, out var giver))
            return false;
        // Also false when the two drew each other: the giver would draw themselves.
        if (!rules.Allows(giver, theirs.RecipientId))
            return false;

        var recipient = theirs.RecipientId;
        await ClearThreadAsync(theirs);
        await ClearThreadAsync(giverRow);
        db.Assignments.Remove(theirs);
        await db.SaveChangesAsync(); // frees the recipient for the giver's row (one row per recipient)
        giverRow.RecipientId = recipient;
        giverRow.RecipientChanged = true;
        giverRow.NotesVersionSeen = await db.Participants.Where(p => p.EventId == eventId && p.PersonId == recipient).Select(p => p.NotesVersion).SingleAsync();
        await db.SaveChangesAsync();
        patched.Add((eventId, giver));
        return true;
    }

    /// <summary>
    /// The late-joiner patch, with the late joiner already a participant: a random pairing A → B
    /// where <paramref name="rules"/> let A give to the late joiner and the late joiner to B becomes
    /// A → late joiner → B. Only A's assignment changes, and A's thread with B is deleted; every
    /// other thread survives the rewrite. False, with nothing changed, if no pairing fits.
    /// </summary>
    public async Task<bool> PatchInAsync(Guid eventId, Guid lateJoiner, DrawRules rules)
    {
        var givers = GiversByTag(eventId, rules.People);
        var rows = await db.Assignments.Where(a => a.EventId == eventId).ToListAsync();
        var candidates = rows
            .Where(r => givers.TryGetValue(r.GiverTag, out var a)
                && rules.Allows(a, lateJoiner) && rules.Allows(lateJoiner, r.RecipientId))
            .ToList();
        if (candidates.Count == 0)
            return false;
        var pick = candidates[RandomNumberGenerator.GetInt32(candidates.Count)];
        var versions = await NotesVersionsAsync(eventId);

        var next = rows.Select(r => r == pick
            ? Copy(r, recipient: lateJoiner, notesVersionSeen: versions[lateJoiner], recipientChanged: true, newThread: true)
            : Copy(r)).ToList();
        next.Add(new Assignment
        {
            EventId = eventId,
            RecipientId = pick.RecipientId,
            GiverTag = key.GiverTag(eventId, lateJoiner),
            NotesVersionSeen = versions[pick.RecipientId],
        });

        // Rewrite every row in a new random order (same IDs), so the late joiner's row isn't
        // simply the last one written. The threads are set aside and put back (all but A's),
        // also in a new order, rather than relying on the database to keep or cascade them.
        var ids = rows.Select(r => r.Id).ToList();
        var messages = await db.Messages.AsNoTracking().Where(x => ids.Contains(x.AssignmentId) && x.AssignmentId != pick.Id).ToArrayAsync();
        await db.Messages.Where(x => ids.Contains(x.AssignmentId)).ExecuteDeleteAsync();
        db.Assignments.RemoveRange(rows);
        await db.SaveChangesAsync();
        await InsertShuffledAsync(next);
        RandomNumberGenerator.Shuffle(messages.AsSpan());
        db.Messages.AddRange(messages);
        await db.SaveChangesAsync();
        patched.Add((eventId, givers[pick.GiverTag]));
        return true;
    }

    /// <summary>A copy of a row with the same ID; <paramref name="newThread"/> starts its question thread afresh.</summary>
    static Assignment Copy(Assignment a, Guid? recipient = null, int? notesVersionSeen = null, bool? recipientChanged = null, bool newThread = false) => new()
    {
        Id = a.Id,
        EventId = a.EventId,
        RecipientId = recipient ?? a.RecipientId,
        GiverTag = a.GiverTag,
        Viewed = a.Viewed,
        NotesVersionSeen = notesVersionSeen ?? a.NotesVersionSeen,
        RecipientChanged = recipientChanged ?? a.RecipientChanged,
        ThreadClosed = !newThread && a.ThreadClosed,
        ThreadChangeUnseen = !newThread && a.ThreadChangeUnseen,
        GiverReadUpto = newThread ? 0 : a.GiverReadUpto,
        RecipientReadUpto = newThread ? 0 : a.RecipientReadUpto,
    };

    // ---------- A giver's own slip ----------

    /// <summary><paramref name="giver"/>'s slip, found by computing their tag; null if there is none under the current key.</summary>
    public async Task<GiverSlip?> GiverSlipAsync(Guid eventId, Guid giver) =>
        await FindAsync(eventId, giver, MessageSender.Giver) is { } row
            ? new GiverSlip(row.RecipientId, row.Viewed, row.NotesVersionSeen, row.RecipientChanged) { Row = row.Id }
            : null;

    /// <summary>
    /// The giver opened their reveal card: viewed, any "recipient changed" notice cleared, the
    /// recipient's notes seen at <paramref name="notesVersion"/>, and the thread read up to
    /// <paramref name="threadUpto"/> (never past its last message). False if the slip is gone (a redraw took it).
    /// </summary>
    public async Task<bool> MarkSeenAsync(GiverSlip slip, int notesVersion, int threadUpto)
    {
        var row = await db.Assignments.SingleOrDefaultAsync(a => a.Id == slip.Row);
        if (row is null)
            return false;
        row.Viewed = true;
        row.RecipientChanged = false;
        row.ThreadChangeUnseen = false;
        row.NotesVersionSeen = notesVersion;
        if (threadUpto > row.GiverReadUpto)
        {
            var last = await db.Messages.Where(x => x.AssignmentId == row.Id).MaxAsync(x => (int?)x.Seq) ?? 0;
            row.GiverReadUpto = Math.Min(threadUpto, last);
        }
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>What <paramref name="giver"/>'s waiting line on home needs, in one query; null without a slip.</summary>
    public Task<GiverSlipUpdates?> GiverSlipUpdatesAsync(Guid eventId, Guid giver)
    {
        var tag = key.GiverTag(eventId, giver);
        return db.Assignments.Where(a => a.EventId == eventId && a.GiverTag == tag)
            .Select(a => new GiverSlipUpdates(
                a.Viewed,
                a.RecipientChanged,
                a.NotesVersionSeen,
                db.Participants.Where(p => p.EventId == eventId && p.PersonId == a.RecipientId).OrderBy(p => p.PersonId).Select(p => p.NotesVersion).FirstOrDefault()))
            .SingleOrDefaultAsync();
    }

    /// <summary>Which of <paramref name="people"/> have opened their reveal card (spec §5.5): nothing about whom they drew.</summary>
    public async Task<HashSet<Guid>> ViewedAsync(Guid eventId, IEnumerable<Guid> people)
    {
        var viewed = (await db.Assignments.Where(a => a.EventId == eventId && a.Viewed).Select(a => a.GiverTag).ToListAsync())
            .ToHashSet(TagEquality);
        return [.. GiversByTag(eventId, people).Where(g => viewed.Contains(g.Key)).Select(g => g.Value)];
    }

    // ---------- Question threads (spec §6) ----------

    /// <summary><paramref name="me"/>'s thread on the <paramref name="side"/> they're on; null if there is none.</summary>
    public async Task<ThreadState?> ThreadStateAsync(Guid eventId, Guid me, MessageSender side) =>
        await FindAsync(eventId, me, side) is { } row
            ? new ThreadState(row.RecipientId, row.ThreadClosed, await db.Messages.AnyAsync(x => x.AssignmentId == row.Id)) { Row = row.Id }
            : null;

    /// <summary>
    /// The thread as <paramref name="me"/> sees it from <paramref name="side"/>; nothing is new once
    /// it is read-only (<paramref name="live"/> false). With <paramref name="markRead"/>, what it
    /// returns counts as read on that side. Null if there is no thread.
    /// </summary>
    public async Task<QuestionThread?> ThreadAsync(Guid eventId, Guid me, MessageSender side, bool live, bool markRead = false)
    {
        var row = await FindAsync(eventId, me, side);
        if (row is null)
            return null;
        var readUpto = side == MessageSender.Giver ? row.GiverReadUpto : row.RecipientReadUpto;
        var shownAfter = live ? readUpto : int.MaxValue;
        var notes = await db.Messages.Where(x => x.AssignmentId == row.Id).OrderBy(x => x.Seq)
            .Select(x => new Note(x.Seq, x.Sender, x.Body, x.CreatedAt, x.Sender != side && x.Seq > shownAfter))
            .ToListAsync();
        var thread = new QuestionThread(notes, row.ThreadClosed);
        if (markRead && live && thread.Upto > readUpto)
        {
            if (side == MessageSender.Giver)
                row.GiverReadUpto = thread.Upto;
            else
                row.RecipientReadUpto = thread.Upto;
            await db.SaveChangesAsync();
        }
        return thread;
    }

    /// <summary>
    /// Adds a message from <paramref name="side"/> to <paramref name="thread"/>, next in line,
    /// sent at <paramref name="at"/> (UTC), saving whatever else the caller has pending with it.
    /// False if the thread is gone (a redraw took it since it was read).
    /// </summary>
    public async Task<bool> AddMessageAsync(ThreadState thread, MessageSender side, string body, DateTime at)
    {
        var row = thread.Row;
        for (var attempt = 0; ; attempt++)
        {
            var seq = (await db.Messages.Where(x => x.AssignmentId == row).MaxAsync(x => (int?)x.Seq) ?? 0) + 1;
            var message = new Message { AssignmentId = row, Seq = seq, Sender = side, Body = body, CreatedAt = at };
            db.Messages.Add(message);
            try
            {
                await db.SaveChangesAsync();
                return true;
            }
            catch (DbUpdateException) when (attempt < 2)
            {
                // Someone posted the same number at the same moment, or a redraw took the thread.
                db.Entry(message).State = EntityState.Detached;
                if (!await db.Assignments.AnyAsync(a => a.Id == row))
                    return false;
            }
        }
    }

    /// <summary>The recipient closes or reopens their thread; false if they have none.</summary>
    public async Task<bool> SetClosedAsync(Guid eventId, Guid recipient, bool closed)
    {
        var row = await FindAsync(eventId, recipient, MessageSender.Recipient);
        if (row is null)
            return false;
        if (row.ThreadClosed != closed)
            row.ThreadChangeUnseen = true;
        row.ThreadClosed = closed;
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Whether <paramref name="recipient"/>'s giver has written something they haven't read yet:
    /// what shows "Your giver asked you something", and what keeps a question email wanted
    /// (spec §15).
    /// </summary>
    public Task<bool> HasUnreadQuestionAsync(Guid eventId, Guid recipient, CancellationToken cancellationToken = default) =>
        db.Assignments.AnyAsync(a => a.EventId == eventId && a.RecipientId == recipient
            && db.Messages.Any(m => m.AssignmentId == a.Id && m.Sender == MessageSender.Giver && m.Seq > a.RecipientReadUpto), cancellationToken);

    // ---------- "What's new" (spec §16, docs/adr/0008) ----------

    /// <summary>What "What's new" works out for <paramref name="me"/> in a drawn event, from their two rows' markers.</summary>
    public async Task<SlipNews> NewsAsync(Guid eventId, Guid me)
    {
        // Two plain subqueries each, for the time and the number (SQLite has no APPLY for one with both).
        var question = await db.Assignments.Where(a => a.EventId == eventId && a.RecipientId == me).OrderBy(a => a.Id)
            .Select(a => new
            {
                CreatedAt = db.Messages.Where(m => m.AssignmentId == a.Id && m.Sender == MessageSender.Giver && m.Seq > a.RecipientReadUpto)
                    .OrderByDescending(m => m.Seq).Select(m => (DateTime?)m.CreatedAt).FirstOrDefault(),
                Seq = db.Messages.Where(m => m.AssignmentId == a.Id && m.Sender == MessageSender.Giver && m.Seq > a.RecipientReadUpto)
                    .Max(m => (int?)m.Seq),
            })
            .FirstOrDefaultAsync();
        var tag = key.GiverTag(eventId, me);
        var giver = await db.Assignments.Where(a => a.EventId == eventId && a.GiverTag == tag)
            .Select(a => new
            {
                Reply = db.Messages.Where(m => m.AssignmentId == a.Id && m.Sender == MessageSender.Recipient && m.Seq > a.GiverReadUpto)
                    .OrderByDescending(m => m.Seq).Select(m => (DateTime?)m.CreatedAt).FirstOrDefault(),
                ReplySeq = db.Messages.Where(m => m.AssignmentId == a.Id && m.Sender == MessageSender.Recipient && m.Seq > a.GiverReadUpto)
                    .Max(m => (int?)m.Seq),
                a.RecipientChanged,
                a.Viewed,
                a.NotesVersionSeen,
                NotesVersion = db.Participants.Where(p => p.EventId == eventId && p.PersonId == a.RecipientId).OrderBy(p => p.PersonId).Select(p => p.NotesVersion).FirstOrDefault(),
                a.ThreadChangeUnseen,
                a.ThreadClosed,
            })
            .SingleOrDefaultAsync();
        return giver is null
            ? SlipNews.None with { Question = question?.CreatedAt, QuestionUpto = question?.Seq ?? 0 }
            // Notes updated only once the card has been opened, as home's waiting line (spec §7).
            : new SlipNews(question?.CreatedAt, giver.Reply, giver.RecipientChanged, giver.Viewed && giver.NotesVersion != giver.NotesVersionSeen,
                giver.ThreadChangeUnseen, giver.ThreadClosed, question?.Seq ?? 0, giver.ReplySeq ?? 0);
    }

    /// <summary>
    /// <paramref name="me"/> dismissed one of "What's new"'s lines on purpose: the marker seeing it
    /// would move moves, on their own row on that side only. A question or reply is read up to
    /// <paramref name="upto"/>, the message the row was shown for (spec §6: a marker holds what was
    /// seen, so one that arrived since stays unread), or to the last one without it. False if they
    /// have no such row.
    /// </summary>
    public async Task<bool> DismissAsync(Guid eventId, Guid me, SlipMarker marker, int? upto = null)
    {
        var row = await FindAsync(eventId, me, marker == SlipMarker.Question ? MessageSender.Recipient : MessageSender.Giver);
        if (row is null)
            return false;
        switch (marker)
        {
            case SlipMarker.Question or SlipMarker.Reply:
                var last = await db.Messages.Where(x => x.AssignmentId == row.Id).MaxAsync(x => (int?)x.Seq) ?? 0;
                if (upto is { } shown)
                    last = Math.Min(last, shown);
                if (marker == SlipMarker.Question)
                    row.RecipientReadUpto = Math.Max(row.RecipientReadUpto, last);
                else
                    row.GiverReadUpto = Math.Max(row.GiverReadUpto, last);
                break;
            case SlipMarker.Patched:
                row.RecipientChanged = false;
                break;
            case SlipMarker.NotesUpdated:
                row.NotesVersionSeen = await db.Participants.Where(p => p.EventId == eventId && p.PersonId == row.RecipientId)
                    .OrderBy(p => p.PersonId).Select(p => p.NotesVersion).FirstOrDefaultAsync();
                break;
            case SlipMarker.ThreadChanged:
                row.ThreadChangeUnseen = false;
                break;
        }
        await db.SaveChangesAsync();
        return true;
    }

    // ---------- Live (spec §16) ----------

    /// <summary>
    /// Which of <paramref name="people"/> gives to <paramref name="recipient"/>, matched by tag in
    /// memory, so a reply, closing or reopening reaches that giver's open page; null if none of them.
    /// Nothing is written or kept.
    /// </summary>
    public async Task<Guid?> GiverAmongAsync(Guid eventId, Guid recipient, IEnumerable<Guid> people)
    {
        var tag = await db.Assignments.AsNoTracking().Where(a => a.EventId == eventId && a.RecipientId == recipient)
            .Select(a => a.GiverTag).SingleOrDefaultAsync();
        return tag is not null && GiversByTag(eventId, people).TryGetValue(tag, out var giver) ? giver : null;
    }

    /// <summary>
    /// The givers this scope's patches of the event gave someone new, worked out in memory while
    /// patching (never stored), so the patch's content reaches only them; each is given out once.
    /// </summary>
    public IReadOnlyList<Guid> TakePatchedGivers(Guid eventId)
    {
        var taken = patched.Where(p => p.EventId == eventId).Select(p => p.Giver).ToList();
        patched.RemoveAll(p => p.EventId == eventId);
        return taken;
    }

    // ---------- Across events ----------

    /// <summary>How many drawn events hold sealed slips: what a new draw key can no longer read (spec §5.3).</summary>
    public Task<int> DrawnEventsWithSlipsAsync(CancellationToken cancellationToken = default) =>
        db.Events.CountAsync(e => e.Status == EventStatus.Drawn && db.Assignments.Any(a => a.EventId == e.Id), cancellationToken);

    // ---------- Inside ----------

    /// <summary>The tracked row <paramref name="me"/> is on as <paramref name="side"/>: by giver tag, or as the one recipient.</summary>
    Task<Assignment?> FindAsync(Guid eventId, Guid me, MessageSender side)
    {
        if (side == MessageSender.Recipient)
            return db.Assignments.SingleOrDefaultAsync(a => a.EventId == eventId && a.RecipientId == me);
        var tag = key.GiverTag(eventId, me);
        return db.Assignments.SingleOrDefaultAsync(a => a.EventId == eventId && a.GiverTag == tag);
    }

    /// <summary>Deletes one thread's messages and resets its markers, e.g. when a patch changes its recipient.</summary>
    async Task ClearThreadAsync(Assignment row)
    {
        await db.Messages.Where(x => x.AssignmentId == row.Id).ExecuteDeleteAsync();
        row.ThreadClosed = false;
        row.ThreadChangeUnseen = false;
        row.GiverReadUpto = 0;
        row.RecipientReadUpto = 0;
    }

    /// <summary>
    /// Writes assignments in random order, so neither row order nor the random IDs say anything
    /// about who drew whom or who joined last (spec §5.2).
    /// </summary>
    async Task InsertShuffledAsync(IEnumerable<Assignment> rows)
    {
        var shuffled = rows.ToArray();
        RandomNumberGenerator.Shuffle(shuffled.AsSpan());
        db.Assignments.AddRange(shuffled);
        await db.SaveChangesAsync();
    }

    async Task<Dictionary<Guid, int>> NotesVersionsAsync(Guid eventId) =>
        await db.Participants.Where(p => p.EventId == eventId).ToDictionaryAsync(p => p.PersonId, p => p.NotesVersion);

    /// <summary>Maps the tags <paramref name="people"/> would have back to them, in memory only.</summary>
    Dictionary<byte[], Guid> GiversByTag(Guid eventId, IEnumerable<Guid> people) =>
        people.ToDictionary(p => key.GiverTag(eventId, p), p => p, TagEquality);

    static readonly TagComparer TagEquality = new();

    /// <summary>Compares giver tags by their bytes.</summary>
    sealed class TagComparer : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x is null ? y is null : y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] tag)
        {
            var hash = new HashCode();
            hash.AddBytes(tag);
            return hash.ToHashCode();
        }
    }
}
