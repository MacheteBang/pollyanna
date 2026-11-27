# Question times are exact and shown at once

A message's time is stored at the moment it was sent and shown to the minute, and a new question appears on the recipient's open pages, home and the "What's new" menu the moment it's posted. This supersedes the reasoning of ADR-0004. Pages that update live (SPEC.md §16) make hour rounding on screen untenable: a question held off the recipient's screen until the hour, while a reload showed it straight away, would only protect people who never reload.

The cost is accepted: the operator's proxy log (a `POST` to the ask path at 14:07) together with the stored message time pairs a giver with their recipient from two passive sources, with no draw key. The operator already holds the database, and the proxy log is theirs. It is written down beside the draw-key limit (ADR-0002).

## Considered Options

- **Keep hour rounding and show a live question only at the hour.** Rejected: a reload shows it at once anyway, so it hides nothing from anyone who looks.
- **Show it only on reload.** Rejected for the same reason: it gives up the live page and hides nothing from someone who reloads.

## Consequences

- Messages stored before this keep their hour, with no migration and no marker.
- Read markers move only on a page load or a deliberate action, never when a message arrives live: a "read" post on arrival would follow the other side's post in the proxy log and pair them.
- No "seen", "typing…" or "online" indicators, ever.
- The hour hold on question emails (SPEC.md §15) stays, as batching only. It may be revisited later; its secrecy reason is gone.
