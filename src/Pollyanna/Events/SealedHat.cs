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

/// <summary>What a giver's home-page dots are worked out from (spec §5.2, §6).</summary>
public sealed record GiverSlipUpdates(bool Viewed, bool RecipientChanged, int NotesVersionSeen, int RecipientNotesVersion, bool Replies);

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
        row.NotesVersionSeen = notesVersion;
        if (threadUpto > row.GiverReadUpto)
        {
            var last = await db.Messages.Where(x => x.AssignmentId == row.Id).MaxAsync(x => (int?)x.Seq) ?? 0;
            row.GiverReadUpto = Math.Min(threadUpto, last);
        }
        await db.SaveChangesAsync();
        return true;
    }

    /// <summary>What <paramref name="giver"/>'s home-page dots need, in one query; null without a slip.</summary>
    public Task<GiverSlipUpdates?> GiverSlipUpdatesAsync(Guid eventId, Guid giver)
    {
        var tag = key.GiverTag(eventId, giver);
        return db.Assignments.Where(a => a.EventId == eventId && a.GiverTag == tag)
            .Select(a => new GiverSlipUpdates(
                a.Viewed,
                a.RecipientChanged,
                a.NotesVersionSeen,
                db.Participants.Where(p => p.EventId == eventId && p.PersonId == a.RecipientId).OrderBy(p => p.PersonId).Select(p => p.NotesVersion).FirstOrDefault(),
                db.Messages.Any(x => x.AssignmentId == a.Id && x.Sender == MessageSender.Recipient && x.Seq > a.GiverReadUpto)))
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
    /// stored under <paramref name="hour"/>, saving whatever else the caller has pending with it.
    /// False if the thread is gone (a redraw took it since it was read).
    /// </summary>
    public async Task<bool> AddMessageAsync(ThreadState thread, MessageSender side, string body, DateTime hour)
    {
        var row = thread.Row;
        for (var attempt = 0; ; attempt++)
        {
            var seq = (await db.Messages.Where(x => x.AssignmentId == row).MaxAsync(x => (int?)x.Seq) ?? 0) + 1;
            var message = new Message { AssignmentId = row, Seq = seq, Sender = side, Body = body, CreatedAt = hour };
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
