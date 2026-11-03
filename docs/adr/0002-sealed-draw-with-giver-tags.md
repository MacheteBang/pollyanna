# Seal the draw with HMAC giver tags and a separate draw key

An assignment stores its recipient in plaintext and its giver only as `HMAC-SHA256(draw_key, "giver" ‖ event_id ‖ giver_person_id)`, with a random ID, no timestamps, and rows written in shuffled order. The draw key lives outside the data location, so neither the database nor a data backup alone can recover who drew whom. The app finds someone's own recipient by computing their tag, and the no-repeat rule and the reveal recompute tags in memory; pairings are never written in plaintext. We accept the limit: an operator holding both the database and the key could recompute every tag, past years included. Peeking takes deliberate effort and can't happen by accident or through the app.

## Considered Options

- **Plaintext pairings, hidden by the UI** (as GiftManager does). Rejected: the operator is also a participant in a family exchange.
- **Encrypting each assignment to its giver** (a per-person key or one derived from sign-in). Rejected: the identity provider gives the app no secret the person holds, and every participant would need to be present for the no-repeat rule to read past years.

## Consequences

- Losing or changing the draw key makes current pairings unreadable; recovery is a redraw, and the no-repeat rule warns and goes ahead without the unreadable events.
- Anything stored about an assignment must not name the giver or record when they acted: read markers are counters, message times are rounded to the hour, the posting limit is counted in memory, and organizers see "viewed" and "done shopping" with no timestamps.
- There are no site-wide admins, and organizers see no pairings before the reveal: a role that could see them would undo the seal.
- Plaintext `recipient_id` is safe only while nothing else ties a giver to a recipient's thread, which is why ADR-0001 exists.
- Every read and write of assignments and messages in the app goes through one module, `SealedHat`, which alone computes giver tags. Callers pass in the policy (the draw rules, who is asking) and get back no tags or rows, so these rules can be checked in one file. Only tests that check what is stored read the tables and compute tags themselves.
