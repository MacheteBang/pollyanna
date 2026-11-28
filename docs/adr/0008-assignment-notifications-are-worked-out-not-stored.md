# Notifications tied to an assignment are worked out, not stored

The five in-app notifications tied to an assignment (a question for you, a reply, closed or reopened, patched, notes updated) are worked out on each request from markers on the sealed assignment row, as home's waiting lines are. Dismissing one moves those markers (`recipient_read_upto`, `giver_read_upto`, `notes_version_seen`, `recipient_changed`, and a new yes/no `thread_change_unseen`). Only the other kinds, which say nothing about who gives to whom, are stored, one row per person, event and kind.

A stored row such as "Bob: your recipient replied" would sit next to the plaintext `recipient_id` on threads and the exact message times (ADR-0007), and pair Bob with Carol from the database alone. SQLite also keeps deleted content by default, so deleting such rows on dismiss wouldn't be enough.

## Considered Options

- **Store every notification, with a dismissal table.** Rejected: it pairs giver and recipient without the draw key, the thing the sealed draw (ADR-0002) exists to prevent.
- **Derive every kind.** Rejected: "made an organizer" or "Sue is taking part" leave no marker to derive from, and those rows pair no one.

## Consequences

- "Seen" means the same thing on home, in the thread and in the menu; dismissing a reply marks the thread read.
- There is no "Dismiss all": it would mark messages read that nobody read.
- The three kinds with no time (patched, notes updated, closed or reopened) can't be ordered by time; they go first. No time column is ever added next to the sealed row to order them.
- A new kind tied to an assignment must be worked out from a marker too, or it contradicts this ADR.
