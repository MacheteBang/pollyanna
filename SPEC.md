# Pollyanna — Specification v1

A small self-hosted web app for a family gift exchange, for any occasion: an
anonymous, pick-from-a-hat draw. Anyone with an account at the family's
OpenID Connect identity provider can sign in, create an event and add people
to it. Participants see the events they are part of and — after the draw —
see only who they are giving to.

Formerly hatpick (§14 "Done: rename to Pollyanna").

Status: v1 complete. Milestone 7 (email notifications) built on 2026-10-16, the rename to
Pollyanna on 2026-10-17; see §14 and DEPLOYMENT.md. Still pre-1.0: versions run from 0.0.1
(§14 "Versions").

## 1. Goals and non-goals

**Goals**
- Electronic "names in a hat": each participant learns only their own recipient.
- The draw stays sealed from the operator: no screen, API or log shows pairings,
  and the database alone is not enough to reconstruct them.
- One event per exchange ("Family gifts 2026"), with history across years.
- Fully self-hosted: the app relies on no service the family doesn't run
  itself. Its only network dependencies are the identity provider, which is
  also self-hosted, and, if email notifications are turned on (§15), a mail
  relay the operator chooses.

**Non-goals (v1)**
- Wishlists with item links, claiming, or price tracking.
- Notifications other than email (§15): no web push, no app push, no
  in-app notification list.
- Participants without an identity-provider account (proxies). Everyone who
  takes part, children included, gets their own account at the provider.
- Mobile apps. The web UI must work well on phones.
- Languages other than English. Text lives in the code; dates use one fixed
  English format (in the viewer's time zone, §4).

**Prior art (evaluated October 2026)**
- GiftManager (icbestCA/giftmanager) comes closest: draw, exclusions, named
  pools, generic OIDC. It was trialled and rejected. It stores pairings in
  plaintext JSON, has no no-repeat rule, and keeps all data in a JSON file.
- Wishlist (cmintey/wishlist) and Christmas Community have good OIDC support
  but no draw.
- No project found supports a draw sealed from the operator, a no-repeat rule,
  or mentions Pocket ID.

## 2. Deployment

- Designed to run behind a reverse proxy that terminates TLS. The app serves
  plain HTTP inside the network.
- How traffic reaches the proxy (LAN only, forwarded from the internet) is
  outside the app's scope. The app makes no assumptions either way.
- No external assets at runtime: no CDNs, web fonts, analytics or telemetry.
  Everything the browser loads is served by the app itself.
- Data is stored in a single SQLite file.
- Shipped as one Docker image, with an example Docker Compose file. The
  SQLite file lives in a data volume; the draw key is mounted from a separate
  location so it isn't in the data volume. Where and how it is deployed is
  up to whoever runs it.
- Settings the operator must be able to configure:

| Setting | Purpose |
|---|---|
| Public URL | e.g. `https://pollyanna.home.example` |
| OIDC issuer | Identity provider base URL (discovery at `/.well-known/openid-configuration`) |
| Client ID / secret | OIDC client credentials; the secret is kept out of plain config |
| Draw key location | Where the draw key lives (§5.3); generated on first start if absent |
| Data location | Where the app's data is stored |
| Mail relay (optional) | SMTP host, port, TLS mode, login and From address for email notifications (§15); the password is kept out of plain config. Unset: no email. |

## 3. Authentication and authorization

- Every page requires authentication, except `/healthz` and the sign-in
  routes themselves (`/auth/...`).
- The only way to authenticate is OpenID Connect: authorization-code flow
  with PKCE against one configured provider. The app must work with any
  standards-compliant provider. Which provider is used is deployment
  configuration (§2), not part of the app. Development and tests use a
  local test provider.
- `state` and `nonce` are random values of at least 128 bits.
- Scopes: `openid profile email`. Callback: `<public URL>/auth/callback`.
  No post-logout redirect URI is needed. The startup log and the settings
  error report the callback URL so the client can be registered before the
  first sign-in (§11).
- Sign-in failures go to `/auth/failed` ("Sign-in didn't work"), never the
  general error page. That includes failures before the redirect to the
  provider (discovery, metadata, a rejected pushed authorization request).
  When the provider rejected Pollyanna's client or couldn't be reached, the page
  adds: "The sign-in service turned Pollyanna away. Whoever runs this site
  needs to check its settings." The log names the provider's `error` code
  (e.g. `invalid_client`) and the settings to check; the page shows no
  details, and the client secret is never logged.
- A person is identified by the provider's issuer plus `sub` claim, mapped to
  Pollyanna's own person ID. Everything else in the app (participation,
  sealed pairings, history) uses the Pollyanna person ID, so a change of
  provider account doesn't lose anything.
- Re-linking: if a sign-in has an unknown issuer/`sub` but a verified email
  that matches exactly one existing person, that person is re-linked to the
  new account instead of a new person being created. This covers a recreated
  account or a switch of provider.
  - "Verified" here means the provider sent `email_verified: true`. An email
    with no `email_verified` claim is never used for re-linking (unlike
    invitations, §4). Matching is case-insensitive.
  - The first sign-in in a process run that has no `email_verified` claim
    logs one info line, naming nobody: invitations by email still match,
    but re-linking by email won't work with this provider.
- Display name, email and picture are refreshed from claims on every login.
- Profile pictures: on login, the server downloads the `picture` URL and
  stores the image; the browser only ever loads it from Pollyanna
  (`/people/{id}/picture`), so the strict CSP holds.
  - Fetch rules: `http`/`https` only, 5-second timeout, at most 1 MB, PNG,
    JPEG, WebP or GIF only (no SVG). Served with its checked content type and
    `X-Content-Type-Options: nosniff`.
  - Any failure keeps the previous image, or shows the person's initials if
    there is none. A failed fetch never blocks sign-in.
  - Only people who share an event with the person, or an organizer adding
    people (an organizer of any event in `draft` or `open`), can load it. It
    is deleted with the account.
  - Pictures appear small beside names in the people lists (event page,
    manage page, new-event form): the picture, or the initials on paper.
- Anyone the provider authenticates may use the app. The app does no group
  checks of its own; limiting who can sign in is the provider's job.
- There are no site-wide admins. All management is per event, by that
  event's organizers (§4).
- Devices are assumed to be personal. "Sign out" ends only the Pollyanna
  session, not the provider's, and shared-device use is not designed for
  beyond the "Tap to reveal" guard (§7).
- No local passwords and no fallback login. If the provider is down, the app is
  down.
- Server-side sessions (random 256-bit ID in an `HttpOnly`, `Secure`,
  `SameSite=Lax` cookie), 30-day sliding expiry. CSRF token on every form.
- Organizers get no extra visibility into draws (§5.5). An organizer may also
  be a participant, and is then treated like any other participant.

## 4. Domain model

**Person** — created on first login.
`id`, `oidc_issuer` + `oidc_sub` (unique together), `display_name`,
`email`, `email_verified`, `picture_url`, `picture` (stored image and its
type), `created_at`, `last_login_at`.

**Deleting an account.** A person can delete their own Pollyanna account:
- It is blocked while they are the last organizer of any event that isn't
  archived; they add another organizer first. Their organizer roles are
  removed everywhere (archived events need no management, so one may end up
  with no organizer). `created_by` and `invited_by` keep pointing at the
  tombstone, and pending invitations they sent stay.
- It applies the leaving rules (below) to every event that is neither
  revealed nor archived. If leaving any of them is held for a redraw,
  deletion waits until that is resolved. In revealed and archived events
  they stay in, as a tombstone.
  - All or nothing: every event is checked first. If any `drawn` event
    can't patch them out, nothing else changes: those events get the
    "wants to leave — redraw needed" request, and the account page says
    deletion waits for that redraw. Once nothing blocks it, deletion leaves
    every event at once.
- Their profile (name, email, picture, provider link) and all their notes
  are deleted. The person row itself stays as an empty tombstone (its `id`
  only), so foreign keys and sealed pairings stay valid. In archived and
  revealed events they appear as "Former participant", and revealed lists
  and the no-repeat rule keep working without a name.
- Messages they wrote in revealed and archived threads stay, so the other
  side keeps their conversation; the deleted side is shown as "Former
  participant".
- Signing in again later creates a fresh person, with no link to the old
  history.

**Event**
`id`, `name`, `year`, `budget_text` (free text, e.g. "$25"),
`exchange_date`, `status`, `previous_event_id` (nullable; used for the
no-repeat rule), `no_repeat_depth` (N, default 1), `pairings_revealed`
(set at reveal and kept through archive, so an event archived straight from
`drawn` stays sealed), `created_by`, `created_at`.
- An organizer can link an event only to a previous event they also
  organize.
- When an event is created linked to a previous one, its participants are
  pre-filled from that event; the organizer unticks anyone not joining.
  The new-event form preselects the most recent event the creator organizes.
  Anyone else is added on the manage page after "Create draft".
- The previous-event link can never form a loop (an event whose own chain
  leads back to this one isn't offered). It and `no_repeat_depth` (1 to 3)
  can't change after the draw.
- `year` is taken from `exchange_date`, not entered separately.
- Any signed-in person can create an event and becomes its first organizer.
- An event is visible only to its organizers and participants.

**Organizer** — `event_id`, `person_id`. Organizers manage the event: edit
details, add and remove participants, households, exclusions, and run the
status changes below.
- Any organizer can make another signed-in person an organizer, or remove one.
- An event always has at least one organizer; the last one cannot be removed.
- There is no recovery path if every organizer is gone; the event stays as
  it is, and a new one can be created. The manage page suggests adding a
  co-organizer while an event has only one.
- Being an organizer does not make someone a participant. The creator chooses
  whether to take part, like anyone else.

Status lifecycle:

```
draft ──► open ──► drawn ──► revealed ──► archived
            ▲        │  │                    ▲
            └────────┘  └────────────────────┘  (reveal is optional)
   (redraw: organizer resets to open; old pairings deleted)
```

- `draft`: only organizers see it; participants can be added.
- `open`: participants see the event and write their notes.
- `drawn`: each participant sees their recipient. Participant list is frozen,
  except for dropouts and late joiners (§5.4). Households and exclusions are
  frozen too: a rule changed after the draw couldn't be checked against the
  current pairings without hinting at them. To change them, redraw (back to
  `open`), edit, and draw again. The one exception is a late joiner, who can
  be put in an existing household as they are added (§5.4).
- A redraw deletes all assignments and their question-box threads, sets
  everyone's "done shopping" back to off, and returns the event to `open`;
  the organizer then draws again (seeing the predictability warning again).
  After the new draw, every participant's reveal card is hidden again and
  shows "Names were drawn again" until they open it.
- `revealed` (optional): every participant and organizer can see the full
  pairing list. Irreversible.
- `archived`: read-only; hidden from the home page unless "show past".
  Reached from `drawn` or `revealed` only; a `draft` or `open` event that
  never happens just stays as it is. Archiving is irreversible.
  An event archived straight from `drawn` stays sealed for good: each
  participant still sees their own slip (read-only), nobody sees the full
  list, and organizers see no pairings. One archived from `revealed` keeps
  showing the full list.

Dates never trigger anything: every status change is made by an organizer.
There is no notes deadline. Revealing before the exchange date is allowed
after a confirmation step. Since `exchange_date` has no time zone, "before"
errs towards asking: until the date has begun everywhere (UTC−12). The
event page and home page show a countdown to the exchange date ("12 days to
go", "1 day to go", "Today's the day"; nothing once the date has passed).

Times are shown in each viewer's browser time zone. The server stores UTC
(message times rounded to the hour) and renders `<time datetime="…">`
elements; a small script formats them and computes the countdown from the
viewer's local date. `exchange_date` is a plain date with no time zone.
Without the script, the page falls back to the date alone.

**Participant** — `event_id`, `person_id`, `household_id` (nullable),
`notes` (likes, sizes, no-gos; editable until `archived`), `notes_version`
(counter, bumped on each edit), `done_shopping` (bool), `joined_at`.
- Only the participant edits their own notes and `done_shopping`. The
  "done shopping" toggle is on the event page once the event is `drawn`.
- Organizers add participants in two ways: pick from people who have already
  signed in, or invite by email (below). The manage page has a separate
  control for each: a dropdown of signed-in people ("Name — email") and an
  "Invite by email" box.

**Visibility within an event** (for its participants):
- The participant list is visible to all participants.
- Every participant's notes are readable by all participants.
- "Done shopping" and "viewed assignment" are visible only to organizers
  (§5.5); showing them more widely could hint at pairings.

**Leaving.** A participant can leave an event themselves at any time before
it is revealed, after a confirmation step. Once it is revealed the exchange
has happened, and leaving isn't offered. (An invited person joins
automatically on sign-in, so declining an invitation means leaving.)
- Before the draw, they are simply removed.
- After the draw, leaving asks for a second confirmation explaining that
  someone else's assignment will change, then applies the dropout patch
  (§5.4). If the patch isn't possible, or leaving would bring the event
  below 3 participants, the person stays in and the organizers see a
  "wants to leave — redraw needed" notice on the manage page.
  - The leaver hears the same thing whichever of the two stopped the patch,
    so it hints at nobody's assignment.
  - Any redraw takes out everyone with a pending request to leave (the
    organizer is told who left). Until then the leaver keeps their slip and
    can withdraw the request ("Stay in after all") on the event page.
  - Revealing or archiving the event instead is allowed: the person stays
    in and the request is cleared. The manage page warns about it beside
    those buttons.
- Leaving as a participant doesn't remove someone's organizer role.

**Invitation** — `event_id`, `email`, `invited_by`, `created_at`.
- If the email already belongs to a person who has signed in (same matching
  rules as below, against the email from their last sign-in), they are added
  as a participant immediately and no invitation is stored. If it belongs to
  more than one person, nothing is stored and the organizer is asked to pick
  them by name instead.
- Otherwise, when someone signs in with a matching email address, each matching
  invitation turns into a Participant row and is deleted. Matching is
  case-insensitive, and uses only an email the provider marks as verified
  when it says so.
- Organizers see pending invitations and can cancel them.
- The draw cannot run while invitations are pending: every participant needs
  an account (§5.2). The organizer waits or cancels the invitation.

**Household** — event-scoped named group used for exclusions, e.g.
"Mom & Dad": `event_id`, `name`. Organizers manage them; a participant is in
at most one household per event. When an event is linked to a previous one,
households are copied from it for the people in both events, and can then be
edited.

**Exclusion** — event-scoped extra rule: `event_id`, `person_a`, `person_b`,
always symmetric. (Household exclusions are applied automatically and do not
need rows here.)

**Assignment** — see §5.2.

**Message** — anonymous question box, see §6.

## 5. The draw

### 5.1 Algorithm

Input: participants P (n ≥ 3), constraints:
1. Nobody draws themselves.
2. Nobody draws a member of their own household in that event.
3. Nobody draws someone listed with them in an event exclusion.
4. If `previous_event_id` is set, nobody draws a recipient they had in any
   of the last N events, following the `previous_event_id` chain back
   `no_repeat_depth` steps. It applies only to people who were in both
   events. If any event in the chain has unreadable pairings (for example
   the draw key changed), the draw warns that the rule couldn't be fully
   applied and goes ahead with the rest.
   Linked events without pairings (never drawn, or back in `open` after a
   redraw) are skipped: N counts only events that have pairings, so the
   chain is followed further back until N such events are found or it
   ends. An event whose pairings are unreadable still counts as one of the N.

Procedure: build the allowed-pairs graph, then find a random perfect matching
by shuffling P with a CSPRNG and backtracking through candidates in random
order. Prefer a single cycle (everyone in one loop) when one exists — try up to
10,000 single-cycle attempts, then fall back to any valid derangement.

With fewer than 6 participants, skip the single-cycle preference and pick
uniformly among all valid assignments; a forced single loop makes tiny draws
more predictable.

**Predictability warning.** Before drawing, the manage page warns the
organizer when pairings may be guessable: fewer than 5 participants, or
fewer than 20 valid assignments under the current rules (counting stops once
the threshold is reached). The warning explains that participants, or an
organizer who set the rules, may be able to work out pairings. The organizer
can loosen the rules or draw anyway. Thresholds are defaults to tune.

If no valid assignment exists, the draw fails with a message naming which rule
makes it impossible where that can be determined (e.g. "a household has more
than half the participants"), and nothing is stored.

The draw runs in one transaction. Pairings exist in plaintext only in memory
during the draw and are never logged.

### 5.2 Sealed storage

Each assignment is stored as:

| Column | Content |
|---|---|
| `event_id` | event |
| `recipient_id` | the recipient (plaintext — everyone is a recipient, so this reveals nothing) |
| `giver_tag` | `HMAC-SHA256(draw_key, "giver" ‖ event_id ‖ giver_person_id)` |
| `id` | random (not autoincrement) |
| `viewed` | bool: the giver has opened the reveal card at least once (shown to organizers as "viewed assignment") |
| `notes_version_seen` | see below |
| `recipient_changed` | bool: a patch changed this giver's recipient; shows the notice until the card is opened |
| `thread_closed` | bool: the recipient closed the question thread (§6) |
| `giver_read_upto` / `recipient_read_upto` | `seq` of the last message each side has seen (§6), for unread badges |

None of these columns name the giver, and none hold timestamps.

- Each assignment also stores `notes_version_seen`: the recipient's
  `notes_version` when the giver last opened the reveal card. If the
  recipient's notes have changed since, the card shows "Notes updated since
  you last looked" and the home page marks the event with a dot. Opening the
  card clears it. A counter, not a timestamp, so it records nothing about
  when anyone looked. Before the giver has opened the card at all there is
  nothing to mark.
- The home page's dot comes with a line that never names the recipient
  ("Your recipient updated their notes", "Your recipient has changed"):
  the home page isn't behind "Tap to reveal".
- Opening the card is recorded when it unfolds (the script posts it), or,
  without the script, when "Tie it back up" is pressed.
- To show a logged-in user their recipient, the server computes their
  `giver_tag` and looks it up. Nothing else maps tags back to people.
- Rows are inserted in shuffled order, and `id`s are random (not
  autoincrement), so row order and timestamps leak nothing.
- With only the database, pairings cannot be recovered. With the database
  **and** the draw key, a determined operator could recompute every tag. This is
  the accepted limit: peeking takes deliberate, manual effort; it cannot happen
  by accident or through the app.
- The same applies to backups: anyone holding a data backup and the key can
  recover every pairing in that backup, past years included. This is
  accepted. The deployment notes say so, and advise keeping any key backup
  apart from data backups (or not backing up the key at all; losing it only
  costs a redraw, §5.3).
- The no-repeat rule (constraint 4) recomputes the earlier events' tags in
  memory at draw time.
- On `revealed`, the server computes the full mapping on request for display;
  it is still not written to the database in plaintext.

### 5.3 Draw key

- 32 random bytes, created on first start and readable only by the app.
- Stored separately from the app's data (e.g. in a secret store) so that a
  data backup alone does not contain it.
- Losing the key makes current assignments unreadable. Recovery is a redraw.
  If a participant's assignment can't be found under the current key, their
  event page says so and tells them to ask an organizer to redraw. The
  deployment notes warn the operator about this.
- Messages about the key:
  - A file of the wrong size: the key is 32 raw bytes (not base64 or hex);
    deleting the file lets Pollyanna create a new one on start, but only safely
    before any draw has been made.
  - Creating a key logs a warning: back it up now, separately from the data;
    losing it makes unrevealed draws unreadable, and recovery is a redraw.
  - Creating a key while events are drawn but not yet revealed logs an
    error instead, with the number of those events: their slips can't be
    read and organizers must redraw; if the old key exists, stop Pollyanna and
    put it back. Startup still continues, so organizers can redraw.

### 5.4 Changes after the draw

**Dropouts.** When a participant drops out of a `drawn` event, the organizer
removes them and chooses one of two options. (A participant who leaves on
their own gets the patch automatically; see "Leaving" in §4.)

- **Patch**: the dropout's giver takes over the dropout's recipient. Only
  that one giver's assignment changes. The server works this out without
  showing anyone the pairings, including the organizer.
  - The patch must still satisfy every draw rule (§5.1). If it can't (for
    example the giver and the dropout had drawn each other, or the new
    pairing breaks a household or exclusion rule), the organizer is told the
    patch isn't possible and can redraw everyone instead.
  - The affected giver sees a "your recipient has changed" notice on the
    event page.
  - Nothing the organizer can see changes for the affected giver: their
    "viewed assignment" flag is not reset, so it can't reveal who was the
    dropout's giver.
  - Question-box threads involving the dropout are deleted.
- **Redraw everyone**: the existing redraw (status back to `open`, old
  pairings deleted, draw again without the dropout).

Either way, the event must still have at least 3 participants. With 3, the
organizer redraws first (back to `open`) and removes the person there.

**Late joiners.** When someone joins a `drawn` event, the organizer adds them
and chooses one of two options. The newcomer must already have signed in;
an email invitation can't be added after the draw while it is pending.

- The organizer can put the newcomer in one of the event's existing
  households as they add them; both options respect it.
- **Patch**: the server picks, at random, one existing pairing (A gives to B)
  where A may give to the newcomer and the newcomer may give to B under the
  draw rules (§5.1). It becomes A gives to the newcomer, and the newcomer
  gives to B. Only A's assignment changes.
  - If no existing pairing allows this, the organizer is told the patch isn't
    possible and can redraw everyone instead.
  - A sees the same "your recipient has changed" notice, and A's "viewed
    assignment" flag is not reset.
  - A's question-box thread with B is deleted.
  - All of the event's assignment rows are then rewritten, same IDs, in a
    new random order, so the newcomer's row isn't simply the last one
    written.
- **Redraw everyone**: the existing redraw, with the newcomer included.

### 5.5 Organizer visibility

Organizers can see, for their event: participant list, who has written notes, who has
viewed their assignment (bool, no timestamp), who has marked done shopping,
and the draw status. They cannot see any pairing until the event is
`revealed`.

Organizers are trusted not to steer the draw. An organizer who also takes
part could tighten exclusions or redraw until they get a recipient they want;
the app neither blocks nor records this.

## 6. Anonymous question box

Lets a giver ask their recipient a question ("shoe size?") without revealing
who is asking.

- A thread belongs to one assignment. Messages store `assignment_id`, `seq`,
  `sender` (`giver` or `recipient` — a role, not a person), `body`,
  `created_at` (rounded to the hour to blunt timing guesses).
- `seq` is a per-thread counter (1, 2, 3…) that orders the thread. The read
  markers `giver_read_upto` / `recipient_read_upto` (§5.2) hold the last
  `seq` each side has seen. A counter only counts within its own thread, so it
  says nothing about activity elsewhere.
- Each person has exactly one giver per event, so a recipient has at most one
  thread per event.
- The giver reaches the thread through their own assignment (via `giver_tag`).
- The recipient sees "Your giver asks…" threads for all assignments where
  they are `recipient_id`, and replies in place.
- Only the giver starts a thread. Until the giver writes, the recipient sees
  that there are no questions yet and has no reply box. (The recipient's notes
  are for telling everyone things.)
- Bodies are plain text, max 1,000 characters, rendered escaped. Messages are
  final: nobody edits or deletes one once it's sent (a redraw, a patch or
  archiving still deletes threads as described).
- An unread badge appears on the event page, and the home page marks the
  event with a dot (the same dot as for updated notes). A new question is
  also emailed to the recipient, held to the hour (§15); a reply is never
  emailed to the giver.
- Rate limit on posting: 20 messages per hour per person, across all their
  sessions (so signing in again doesn't reset it), as a sliding window. It is
  a constant in the code (tests override it) and counted in memory only, so
  nothing records when anyone posted.
- The recipient can close a thread. The giver can then no longer post, and
  sees "Your recipient has closed this conversation". The recipient can
  reopen it. No one else is told. A closed thread takes no messages from
  either side; to say one last thing, the recipient reopens it first.
- Posting, closing and reopening work only while the event is `drawn`. Once
  it is `revealed` the threads are read-only (shown on the revealed view,
  milestone 5) until archiving.
- After reveal each side's thread names the other ("Carol asked…" instead of
  "Your giver asks…"), since the pairing list already says who it
  was. Threads stay private to their giver and recipient: other
  participants and organizers never read them, before or after reveal.
- Messages are deleted when the event is archived, unless an organizer chooses
  to keep them. Kept threads stay readable, read-only, by their own giver
  and recipient only, from the archived event's page. They name the giver
  only if the event was revealed before it was archived.

## 7. Pages

| Path | Who | Content |
|---|---|---|
| `/` | signed in | Events the user participates in or organizes (status, date, budget), a "past events" link, "New event", and the about line |
| `/events/new` | signed in | Create an event; the creator becomes its organizer |
| `/events/{id}` | participant | Event details; own notes editor; after the draw, the reveal card: "You're getting **Carol** a gift", her notes, budget, date, question thread |
| `/events/{id}/questions` | participant | Threads where the user is the recipient |
| `/events/{id}/manage` | organizer | Edit details; participants and invitations; organizers; households; exclusions; open, draw, redraw, reveal, archive; once revealed, the full pairing list |
| `/past` | signed in | Archived events the user took part in or organized |
| `/account` | signed in | Name and email as the provider sent them; the "Email me about Pollyanna events" switch (§15); sign out; delete account |
| `/healthz` | anyone | Liveness, no data |

- `/events/{id}` also holds, by status: "Leave this event" (its confirmation
  step is the page `/events/{id}/leave`); the "done
  shopping" toggle (`drawn`); the full pairing list (`revealed`). An organizer
  who isn't a participant is sent to `/events/{id}/manage` instead.
- After sign-in, a user returns to the page they first asked for.
- The about line says what Pollyanna is and nods to the name. It sits at the
  bottom of `/` (with or without events) and of `/auth/signed-out`, the one
  signed-out page that isn't about a failure or a deletion; `/` itself still
  needs sign-in (§3). The text: "Every family has a version of this: names on
  folded slips, a hat going round the table, someone squinting at the
  handwriting. Around Philadelphia they call it a Pollyanna. This is the hat,
  minus the squinting."

When adding people, organizers pick from everyone who has signed in (name and
email), which is acceptable for a family-sized deployment.

The reveal card is hidden behind a "Tap to reveal" button so a phone left on
the table does not give it away.

Accessibility: text contrast at least 4.5:1 (3:1 for large text), tap
targets at least 44 px, everything usable by keyboard, real form controls
with labels, and the reveal animation turned off under "reduce motion".

## 8. Security notes

- Every management action checks that the user is an organizer of that
  event, and requires a CSRF token.
- Per-person rate limiting on message posting (§6).
- No request logging of paths that contain assignment or message IDs; access
  logs record method, route pattern and status only.
- Security headers: strict CSP (`default-src 'self'`), `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`.
- The draw key never appears in logs, error messages or the UI.
- Logs stay quiet when nothing is wrong. Successful `/healthz` requests are
  not logged. The OIDC callback is logged as `/auth/callback`. EF Core's
  warning about the SQLite table rebuild (`PRAGMA foreign_keys` outside a
  transaction) is ignored by its event ID; other EF warnings still log. The
  key ring is stored unencrypted on purpose, protected by the data
  directory's permissions, and is configured that way explicitly
  (`NullXmlEncryptor`), so Data Protection doesn't warn "No XML encryptor
  configured". The settings are checked before the host starts, so a
  settings error prints only Pollyanna's list of problems, without the host's
  stack trace; other startup failures keep theirs. Queries that limit rows
  have an `OrderBy` rather than a filtered warning.

## 9. Testing

- Unit tests for the draw: every constraint, impossibility detection, and a
  statistical check that every valid recipient is reachable for a giver.
- Tests that no handler returns another person's recipient in any status
  before `revealed`, including to organizers.
- Tests that non-organizers cannot manage an event, and that people outside
  an event cannot see it.
- Tests that invitations turn into participants on matching sign-in only,
  and that re-linking by verified email attaches to the right person.
- Tests for changes after the draw: dropout and late-joiner patches keep
  every rule, change only one giver, and fall back correctly when a patch
  isn't possible; self-service leaving and account deletion follow the same
  rules.
- Tests that the predictability warning fires at its thresholds.
- Integration test of the OIDC flow against a local mock issuer.
- Tests for email notifications (§15): each kind is queued for the right
  people and nobody else, never for the person who acted, never while the
  event is a draft except "Made an organizer", and not at all with emails switched off or SMTP not
  configured; questions are held to the hour, grouped, and dropped if read
  first; no email is ever queued for a giver because of their own
  assignment (docs/adr/0001); no email contains message bodies,
  notes or a recipient's name; and the outbox rules (retry, give up, drop
  on opt-out, account deletion, a cancelled or accepted invitation, or a
  lost role) hold.
- Manual end-to-end check against the production provider before first use.

## 10. Milestones

1. **Sign-in** — configuration, OIDC login against a local test provider,
   re-linking, home page (including the empty state), account page with
   sign out, the visual foundation (palette, fonts, slip and stamp styles).
2. **Events** — create and edit events, organizers, participants and email
   invitations, leaving, households, exclusions, participant notes,
   visibility rules, countdown.
3. **Draw** — algorithm, predictability warning, sealed storage, reveal card,
   notes-updated marker, redraw, dropout and late-joiner patches.
4. **Question box** — threads, unread badges, closing threads.
5. **Reveal/archive and history** — `revealed` view, archive, past events
   page, no-repeat rule over the last N events, account deletion.
6. **Deployment** — Docker image, example Compose file and deployment notes,
   including what to back up and how to register Pollyanna as an OIDC client.
7. **Notifications** — email notifications (§15): SMTP settings,
   outbox and sender, the account switch, every notification kind.

## 11. Technology

- Back end: .NET 10.
- Front end: server-rendered Razor Pages with a little plain JavaScript; no
  front-end build step. Pages work before scripts load where possible
  ("Tap to reveal" can be a `<details>` element enhanced by script).
- Data: SQLite, a single file, accessed through EF Core. Code avoids
  provider-specific SQL and features so another EF Core provider (e.g.
  PostgreSQL) can replace SQLite later; schema migrations are per provider
  and would be regenerated on a switch.
- Settings come from environment variables (documented in the deployment
  notes); the client secret can be read from a file. All are required except
  the SMTP settings; the app refuses to start and lists every problem if any
  is missing or invalid.

| Variable | Setting (§2) |
|---|---|
| `POLLYANNA_PUBLIC_URL` | Public URL: a bare origin, no path. Its scheme is taken as the browser's (the proxy terminates TLS); forwarded headers are not trusted. |
| `POLLYANNA_OIDC_ISSUER` | OIDC issuer; must be `https` unless the host is loopback (development). |
| `POLLYANNA_OIDC_CLIENT_ID` | Client ID. |
| `POLLYANNA_OIDC_CLIENT_SECRET` or `POLLYANNA_OIDC_CLIENT_SECRET_FILE` | Client secret, or a file holding it (exactly one of the two). |
| `POLLYANNA_DRAW_KEY_FILE` | Draw key file: 32 raw bytes, created with mode 0600 if absent. |
| `POLLYANNA_DATA_DIR` | Data location: holds `pollyanna.db` and the key ring for sign-in state and form tokens. |
| `POLLYANNA_SMTP_HOST` | Mail relay host (§15). Unset: email notifications are off and the other SMTP settings must be unset too. |
| `POLLYANNA_SMTP_PORT` | Mail relay port; required when the host is set (no default). |
| `POLLYANNA_SMTP_TLS` | `starttls`, `tls` (implicit TLS) or `none`; required when the host is set (no default). |
| `POLLYANNA_SMTP_USERNAME` | Relay login, optional; needs a password with it. |
| `POLLYANNA_SMTP_PASSWORD` or `POLLYANNA_SMTP_PASSWORD_FILE` | Relay password, or a file holding it (at most one of the two; a password without a username is an error). |
| `POLLYANNA_MAIL_FROM` | From address, e.g. `Pollyanna <pollyanna@home.example>`; required when the host is set. |

- Email is sent with MailKit (`System.Net.Mail.SmtpClient` is discouraged
  by Microsoft). An incomplete SMTP configuration stops startup like any
  other setting; a relay that can't be reached is only logged, and the app
  starts. At startup a background check connects to the relay (and logs
  in, if a username is set) without sending anything, and logs whether it
  answered.

- The image describes itself, for an operator who has only the image:
  - `Pollyanna help` (also `--help`, `-h`) prints a short reference to stdout
    and exits 0: the version, every `POLLYANNA_*` setting, the mounts and run
    user (uid 1654; own host directories by number, and rootless Docker or
    user-namespace remapping changes that uid), the draw key's format and
    what losing it costs, the OIDC client requirements (callback URL,
    scopes, PKCE, no post-logout URI), which email claims invitations and
    re-linking need from the provider, and the `healthcheck` and `backup`
    commands with generic Compose and `docker exec` lines. A test checks
    that every setting in the table above appears in it. Unknown arguments
    print the usage error, which points at `help`. `DEPLOYMENT.md` stays the
    long-form guide.
  - `DEPLOYMENT.md` and `THIRD-PARTY-NOTICES.md` ship in `/app`.
  - The settings error ends with the callback URL (when the public URL is
    valid) and "Run `/app/Pollyanna help` for every setting". The ready line
    reads `Pollyanna <version> is ready at <url>; OIDC redirect URI:
    <url>/auth/callback`.
  - OCI labels: the csproj sets `title` and `description` (pointing at
    `help`). `licenses` is not set yet: the repository has a license now,
    and adding the label is a follow-up issue. CI sets
    `version`, `source` and `documentation` (that tag's `DEPLOYMENT.md` on
    the forge) from its context, so no host name is in the repo. CI passes
    the tag as `-p:Version`; local builds are `0.0.0-dev`.
  - The SDK's container support can't declare `VOLUME` or `HEALTHCHECK`, and
    Pollyanna keeps it rather than adding a Dockerfile; the mounts and health
    check are covered by `help` and the example Compose file.
- The local test provider (§3) is a small mock OIDC issuer in the solution
  itself: discovery, JWKS, authorization endpoint and token endpoint with
  PKCE checks. Its sign-in form takes `sub`, email, name and
  `email_verified` so re-linking can be tried by hand. The automated tests
  host it in-process. It is for development and tests only and is never part
  of the shipped image.
- Tests replace the email sender with an in-memory fake that records what
  would have been sent. For local development, CONTRIBUTING.md suggests
  Mailpit as a mail catcher, with the SMTP settings pointed at it.
- License: AGPL-3.0, in `LICENSE` (§14 "Done: README for operators"). The
  fonts (OFL) and the Fluent bow (MIT) keep their own licences, credited in
  `THIRD-PARTY-NOTICES.md`.

## 12. Visual design: "Paper slips"

Mockups: https://claude.ai/artifact/AxLjeyG2zj8wgSriRFESVb (the marks and the
2.x screens; the v1 mockups with the top hat stay at
https://claude.ai/artifact/VFStJPRMNMiqeZG5ZrVhgY, where rows B and C are
rejected alternatives).

- Concept: the app is a table with a gift on it. Events and sections are torn
  paper slips, slightly rotated; status labels are rubber stamps ("DRAFT",
  "OPEN", "DRAWN"); the reveal is a slip tied with ribbon: pulling the ribbon
  unties the bow, then the slip unfolds. Nothing references the novel or the
  film, and nothing is tied to one holiday. The name's regional meaning
  ("a Pollyanna" is a gift exchange around Philadelphia) may be mentioned,
  as the about line does (§7).
- Palette: kraft ground `#C9A77C`; slip paper `#F4EDE0`; bright paper (inputs,
  replies) `#FFFDF8`; ink `#1E1A16`; stamp red `#B3261E`, darker red for red
  text `#9E2019`; ribbon blue `#3F6E8C`. Ribbon blue means "the gift" and is
  used only for bows and ribbon (the marks, the gift box, the reveal ribbon);
  red stays for stamps, unread dots and errors. Folds and shadows inside a
  bow are ink at 30% over ribbon blue. No other colours, and no red-and-green
  pairing.
- Type: Courier Prime (typewriter) for everything; Reenie Beanie (pencil
  hand) only for the revealed name. Both are open-licensed and served by the
  app.
- Marks, one family:
  - Bow (favicon, apple-touch-icon on a kraft square, the reveal ribbon, the
    gift box): a ribbon bow adapted from Microsoft Fluent Emoji "Ribbon"
    (Flat, MIT), recoloured: ribbon-blue loops and tails, the inner folds in
    ink at 30%, an ink knot. At 16px the folds are dropped.
  - Girl (masthead, beside the wordmark, 40px): seen from behind, two
    braids each tied with a small ribbon-blue bow, no face. Generated with
    Recraft by the project owner, who holds the rights to it, then cleaned
    up and recoloured to the palette (`Pages/Shared/_Girl.cshtml`); no
    credit is needed.
  - Gift box (empty home page): slip-paper box outlined in ink, a
    ribbon-blue band and bow, and a bright-paper tag.
  The Fluent credit lives in the repo (`THIRD-PARTY-NOTICES.md`, plus a
  comment in `favicon.svg`), not on the site.
- Questions are passed notes, not chat bubbles: the viewer's own on slip
  paper, the other side's on bright paper with a red top edge; times read
  "MON, ABOUT 7 PM" ("TODAY" for today, the date after a week).
- Light only: no dark mode. Pages declare `color-scheme: light` so browsers
  don't auto-darken them.
- Avoid: gradients, pill badges, emoji, chat bubbles, card grids, a cream +
  serif + coral look, Christmas red-and-green.
- Screens in the v1 mockups: home, first sign-in (empty) home, event page with
  reveal, organizer manage page, ask your recipient, questions for you, new
  event. The 2.x mockups redo home, empty home and the event page with the
  reveal; the other screens change only in their masthead.

## 13. Open items

None. Decisions made while building are recorded back into this spec.

## 14. Implementation status

Read this section first when picking the work up in a new session. All six
v1 milestones are done, and milestone 7 (email notifications) is
built. Branch, commit and PR rules are in CLAUDE.md. Questions about anything the spec
doesn't cover go to the user one per turn, and each answer is written back into
this spec.

### Done: milestone 1 (sign-in)

- Solution `Pollyanna.slnx`: `src/Pollyanna` (the app), `src/Pollyanna.MockIssuer`
  (the dev/test OIDC provider, §11), `tests/Pollyanna.Tests` (xUnit, run with
  `dotnet test`). `CONTRIBUTING.md` has the run steps.
- Configuration (§11 table) is read lazily and checked at startup by
  `Infrastructure/StartupTasks.cs`, which also loads or creates the draw key
  (`Configuration/DrawKey.cs`) and applies EF migrations.
- Sign-in: the OIDC handler (Program.cs) uses PKCE, `response_mode=query`,
  claims from userinfo, and the callback taken from the public URL. It uses PAR
  automatically when the provider advertises it (Pocket ID does).
  `Auth/PersonSignIn.cs` maps issuer + `sub` to a person, re-links by verified
  email, refreshes the profile and fetches the picture
  (`People/PictureFetcher.cs`). `Auth/SessionAuthenticationHandler.cs` holds the
  server-side sessions; the cookie is `__Host-pollyanna` and the DB stores only
  its SHA-256.
- Pages: `/` (event slips, or the empty state with the email to be invited
  at), `/account`, `/auth/login`, `/auth/signout` (POST), `/auth/signed-out`,
  `/auth/failed`, `/error/{code}`, `/healthz`, `/people/{id}/picture`.
- Every page needs sign-in (fallback authorization policy); `Pages/Auth` and
  the error page are anonymous. Static files are served before auth.
- Visual foundation in `wwwroot/css/site.css`: palette tokens, self-hosted
  fonts (OFL licence texts beside them), `.slip` (torn edge via
  `clip-path` on `::before`, so focus outlines aren't clipped), `.slip-plain`,
  `.stamp` / `.stamp-ink` (the dashed ink stamp is for DRAFT), `.button`,
  `.link-button`, form controls, `.dot`. There are no inline styles or
  scripts (strict CSP), and red text sits on paper only: on kraft it fails 4.5:1.
- Data model so far: Person (+ PersonPicture), Session, Event, Organizer,
  Participant. Ids are random GUIDs, and times are UTC `DateTime`.

### Done: milestone 2 (events)

- Domain logic lives in `src/Pollyanna/Events/`: `EventAccess.cs` (the
  `Membership` record: `CanView`, `PeopleEditable`, `Editable`, `CanLeave`,
  i.e. the visibility rules of §4) and `EventManager.cs` (create, details,
  open, participants, invitations, organizers, households, exclusions, notes,
  leaving). Each action returns an `Outcome` (ok + message). Pages check the
  role; `EventManager` checks statuses and the event's own rules.
- Pages in `Pages/Events/`: `New` (`/events/new`), `Event` (`/events/{id}`,
  notes post to `/events/{id}/notes`), `Manage` (`/events/{id}/manage`, each
  action posts to `/events/{id}/manage/{handler}`, e.g. `addperson`,
  `invite`, `savehousehold`, `details`, `open`), `Leave`
  (`/events/{id}/leave`). They share `EventPageModel`. Refusals are 404
  (`NotFound()`), so an event's existence isn't revealed.
- After each post the page redirects back with a one-shot notice in TempData
  (cookie `pollyanna-notice`), shown by `Pages/Shared/_Notice.cshtml` in the
  layout (or beside a named form, see milestone 4). Only the details forms re-render with what was typed.
- Shared partials: `_When` (date + countdown slot), `_Avatar` (picture or
  initials). `wwwroot/js/site.js` is the only script: countdown and the
  new-event form's earlier-event picker. CSS for these pieces is in the
  "Event pages" block of `site.css`.
- Invitations are accepted in `PersonSignIn.AcceptInvitationsAsync`. The
  picture rule in `PictureEndpoint` adds organizers of `draft`/`open` events.
- Data: migration `Events` adds Household, Exclusion (one row per pair,
  `PersonA` the smaller GUID, via `Exclusion.Between`), Invitation (unique per
  event + normalized email) and `Participant.HouseholdId` (set null when the
  household goes).
- Statuses reachable so far: `draft` and `open` (the manage page's "Open the
  event"). Everything that changes people or rules (participants,
  invitations, households, exclusions, earlier event, no-repeat depth) is
  allowed only in `draft`/`open` (`Membership.PeopleEditable`). In `drawn`
  those forms are hidden and the actions refuse, and leaving isn't offered.
- Tests: `EventAccessTests` (§9 visibility and manage permissions),
  `InvitationTests`, `EventCreationTests`, `ManageTests`, `ParticipantTests`.
  `Support/Seed.cs` has `app.SignInAsync`, `app.PersonAsync`,
  `app.EventAsync`, `Form(...)`. `TestBrowser` gained `SubmitOnAsync`,
  `PostWithTokenAsync`, repeated fields and a `match` for picking one of
  several forms with the same action.

### Done: milestone 3 (draw)

- The algorithm is `Events/Draw.cs`, pure and without the database:
  `DrawRules` (people, households, exclusions, plus a `Forbidden` set of
  giver → recipient pairs for the no-repeat rule), `Draw.Run`, `Draw.Count`,
  `Draw.IsPredictable`, `Draw.Problem`. Existence is a perfect-matching test.
  Under 6 people it picks uniformly among every valid assignment; from 6 it
  tries random single loops (10,000 attempts) and falls back to a random
  assignment that checks completability at each step. Impossibility messages
  name the person or household at fault when one is.
- `Events/EventManager.Draw.cs` (a partial of `EventManager`) holds
  everything with the database: `CheckDrawAsync` (blocker, problem,
  predictability), `DrawAsync` (one transaction; claims `open → drawn` with
  a status-conditioned `ExecuteUpdateAsync`, then inserts rows in shuffled
  order), `RedrawAsync`, the dropout and late-joiner patches, leaving after
  the draw (`LeaveAfterDrawAsync`, `StayAsync`), and the giver's own slip
  (`SlipAsync`, `MarkSlipSeenAsync`, `SetDoneShoppingAsync`,
  `SlipUpdatesAsync` for the home page). `NoRepeatPairsAsync` returns an
  empty set: milestone 5 fills it in.
- `DrawKey.GiverTag(eventId, giver)` computes the §5.2 tag (GUIDs as
  big-endian bytes). Only the giver's own lookup and the patches map tags
  back to people, in memory; organizers get "viewed" through
  `ViewedAsync`, which returns people, never pairings.
- Data: migration `Draw` adds Assignment (unique per event + giver tag and
  per event + recipient; random `Id`, no timestamps), `Participant.WantsToLeave`
  and `Event.Redrawn` (set by a redraw; each card shows "Names were drawn
  again" until its giver opens it).
- Manage page: `open` has "Draw names" (`/manage/draw`), disabled with the
  reason while blocked, or "Draw anyway" under the predictability warning.
  `drawn` has the people list with viewed/done marks, "Someone dropping
  out?" and "Someone joining late?" (each with Patch and Redraw buttons,
  `name="change"`), and "Redraw names…" (`/manage/redraw`), opened by
  itself when someone wants to leave. Households and rules are frozen while
  drawn (§4).
- Event page: the reveal card is `details.reveal#slip[data-reveal]`;
  `revealCards()` in `site.js` posts `/events/{id}/seen` when it unfolds,
  and the "Fold it back up" form is the no-script path. Also
  `/events/{id}/shopping` and `/events/{id}/stay`. The Leave page has a
  second step in `drawn` (hidden `confirmed=true`).
- Tests: `DrawTests` (the algorithm, uniformity, thresholds),
  `DrawFlowTests`, `AfterDrawTests` (patches, fallbacks, leaving, no
  pairings in logs), `PairingSecrecyTests` (every page a viewer can reach is
  byte-identical when everyone else's recipients are shuffled). `Seed` has
  `SealAsync`, `Loop`, `PairsAsync`, `AssignmentOfAsync`.

### Done: milestone 4 (question box)

- `Events/EventManager.Questions.cs` (a partial of `EventManager`) holds the
  question box: `GiverThreadAsync` (found through `MyAssignmentAsync`, i.e.
  the giver tag), `RecipientThreadAsync` (the one row with
  `RecipientId == me`), `AskAsync`, `ReplyAsync`, `SetThreadClosedAsync`
  and `ClearThreadAsync`. Threads come back as `QuestionThread` / `Note`
  records that carry no assignment or message ID; forms find the thread
  from the viewer's role, so no ID reaches a URL, a form or the HTML.
  `ThreadsOpen` / `Refusal` limit everything to participants of a `drawn`
  event.
- `Events/PostingLimit.cs`: the §6 limit, a singleton sliding window per
  person (`DefaultPerHour = 20`, `PerHour` settable by tests), using
  `TimeProvider`.
- Data: migration `Questions` adds Message (`Seq`, role-only `Sender`,
  `Body`, `CreatedAt` floored to the hour; unique per assignment + seq;
  cascade from Assignment) and Assignment's `ThreadClosed`,
  `GiverReadUpto`, `RecipientReadUpto`.
- Redraws and patches (`EventManager.Draw.cs`): `ResetToOpenAsync` deletes
  the event's messages explicitly; `PatchOutAsync` clears both threads
  involving the dropout (`ClearThreadAsync` also resets the closed flag and
  read markers); `PatchInAsync` sets every other thread's messages aside,
  deletes and re-inserts the rows shuffled, then adds the messages back in
  shuffled order, leaving out only the broken pairing's thread.
- Pages: the giver's thread is `section.thread#ask` inside the reveal card
  (posts to `/events/{id}/ask`; after asking, TempData `Unfold` renders the
  card open). Unfolding marks replies read up to the hidden `upto` the card
  was rendered with, so a reply that arrives later stays new. The folded
  cover says "A NEW REPLY INSIDE". The recipient's page is
  `Pages/Events/Questions` (`/events/{id}/questions`, handlers `reply`,
  `close`, `reopen`); it marks the thread read on GET and 404s for
  non-participants. The event page links to it once the giver has written.
  `_Notes.cshtml` (model `NotesView`) draws the passed notes for both sides;
  CSS is the "question box" block in `site.css`; `site.js` adds
  `messageTimes()` ("MON, ABOUT 7 PM") and `counters()`.
- Notices: `Finish(outcome, url, at)` can name a place; a `_Notice` partial
  given that `NoticeAt` (view data) shows it there, beside the thread form
  the redirect scrolls to, and the layout's copy shows it otherwise. Both
  thread forms use this (`ask`, `reply`).
- Home page: `SlipUpdatesAsync` returns several lines per event ("Your
  recipient replied", "Your giver asked you something" next to the
  notes and recipient-changed lines).
- Tests: `QuestionTests` (both sides, read markers, closing, validation and
  escaping, seq and hour rounding, the limit across sessions, access, no IDs
  in HTML or logs, notices beside the form), `AfterDrawTests` (threads
  through a redraw, both dropout paths and the late-joiner patch),
  `PairingSecrecyTests` (now with the questions page and everyone else's
  threads varied). `Seed` has `MessageAsync`, `UpdateAssignmentAsync`,
  `ThreadOfAsync`.

### Done: milestone 5 (reveal, archive and history)

- Status changes: `Events/EventManager.Reveal.cs` has `RevealAsync`
  (`drawn → revealed`, confirmation while `RevealNeedsConfirmation`, i.e.
  before the exchange date has begun anywhere) and `ArchiveAsync` (from
  `drawn` or `revealed`, deleting the event's messages unless
  `keepMessages`). Both claim the status with a status-conditioned
  `ExecuteUpdateAsync` and drop pending leave requests
  (`ClearLeaveRequestsAsync`). The manage page shows them in the Status
  section (`_LeaveRequests`, `_Archive` partials; handlers `reveal`,
  `archive`).
- Data: migration `Reveal` adds `Event.PairingsRevealed`. It is the only
  switch for showing pairings: `PairingsAsync` returns null unless it is set,
  so an event archived from `drawn` stays sealed (each participant still sees
  their own slip, `SlipAsync`).
- The pairing list: `PairingsAsync` maps giver tags to participants in
  memory; a row whose tag matches nobody comes back with a null giver and
  shows as "Someone". `_Pairings.cshtml` draws it on the event page ("Who
  drew whom") and on the manage page, for organizers who aren't
  participants. CSS is the "revealed list" block in `site.css`.
- Threads after reveal: `ThreadsReadable` allows `drawn`, `revealed` and
  `archived`; posting still needs `drawn`. Outside `drawn` nothing is marked
  read. The event page shows both threads read-only, named ("Carol asked
  you") only when pairings are revealed.
- No-repeat: `NoRepeatPairsAsync` (`EventManager.Draw.cs`) walks the
  previous-event chain, counting only events with assignment rows, and
  returns forbidden pairs among people in both events.
  `RulesWithNoRepeatAsync` also reports when an earlier event's tags can't
  be read: the manage page and the draw notice show
  `NoRepeatIncompleteWarning`, and the draw goes ahead. `Draw.WhyImpossible`
  mentions earlier years when the rule is what blocks the draw.
- `/past` (`Pages/Past`): archived events the viewer organizes or took part
  in, newest first.
- Account deletion: `Events/EventManager.Accounts.cs` (`CheckDeletionAsync`,
  `DeleteAccountAsync`, all or nothing in one transaction), page
  `Pages/AccountDelete` (`/account/delete`, linked from `/account`), then
  `/auth/deleted`. The tombstone keeps only the person's `id`;
  `SignedInPeople` already skips rows without `OidcSub`.
- Tests: `RevealArchiveTests`, `NoRepeatTests`, `AccountDeletionTests`;
  `PairingSecrecyTests` now also covers events archived from `drawn` (with
  kept threads), `/past` and `/account/delete`.

### Done: milestone 6 (deployment)

- The image: the .NET SDK's container support (`dotnet publish src/Pollyanna
  -t:PublishContainer`), no Dockerfile; settings in `Pollyanna.csproj`.
  Base `mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled` (no shell, no
  package manager), non-root uid 1654, plain HTTP on port 8080.
  `appsettings.Development.json` isn't published; `src/Pollyanna.MockIssuer`
  and the tests are never part of the image.
- Commands for the shell-less image (`Infrastructure/Commands.cs`, run
  before the web host is built): `Pollyanna healthcheck [url]` requests
  `/healthz` on the first `ASPNETCORE_HTTP_PORTS` port (8080) and exits 0
  or 1; `Pollyanna backup <file>` writes a consistent copy of `pollyanna.db`
  with SQLite's online backup API while the app runs, and never overwrites.
- Startup: `StartupTasks` also reports a data directory the app can't write
  to, and `DrawKey.LoadOrCreate` turns I/O failures into a configuration
  problem naming the path; both are listed together. `Program.cs` catches
  `PollyannaConfigurationException` around building the pipeline too, so
  missing settings print the list and exit 1 instead of a stack trace.
- Distribution (decided 2026-11-03, user, when the repo moved to GitHub):
  the GitHub Container Registry, `ghcr.io/machetebang/pollyanna`, by GitHub
  Actions (`.github/workflows/ci.yml`, authenticated with the
  workflow's `GITHUB_TOKEN`; the repository name comes from the CI
  context). Every push to `main` runs the tests; a tag `vX.Y.Z` runs them,
  then publishes `X.Y.Z`, the commit SHA and `latest`. The repository and
  the image are public, so pulling needs no `docker login`.
- `deploy/compose.yaml` + `deploy/.env.example`: one service with the
  built-in health check, `restart: unless-stopped`, read-only root
  filesystem with a `/tmp` tmpfs, dropped capabilities, port 8080 for the
  operator's proxy (no proxy ships). Settings and the client secret come
  from `.env` (gitignored); `POLLYANNA_OIDC_CLIENT_SECRET_FILE` is the
  documented alternative. Storage is two bind mounts the operator creates
  owned by uid 1654: `./data:/data` and `./draw-key:/draw-key`, kept apart
  so a data backup never holds the draw key. The image is pinned by
  version in `POLLYANNA_IMAGE`.
- `DEPLOYMENT.md`: OIDC client registration, every setting, the proxy's
  job, backup and restore (data with `Pollyanna backup` plus `keys/`; the draw
  key once, separately), the draw-key loss warning and the "Created a new
  draw key" log line to watch for, upgrades, the manual end-to-end check
  against the production provider (§9), troubleshooting, and building or
  publishing the image.
- Tests: `DeploymentTests` (backup, health check and usage exit codes,
  startup listing both directory problems) and a draw-key creation failure
  in `ConfigurationTests`. Verified by hand in Docker with the mock issuer:
  refusal on bad settings and root-owned mounts, first start and
  migrations, `/healthz` and the healthy status, OIDC sign-in with the
  secret from `.env` and from a file, backup during use, and restore.

### After v1

- Conventions: `CODING_STANDARDS.md` holds the rules reviews check, and
  `docs/agents/testing.md` the test seams. Add migrations with
  `dotnet tool restore` then
  `dotnet ef migrations add <Name> --project src/Pollyanna -o Data/Migrations`.
- The dev database in `src/Pollyanna/.dev` has the "Cousins' Swap" event
  drawn. Delete `.dev/` for a fresh start.
- Releases: see "Versions" below. CI runs on
  every pull request, every push to `main` and every tag, on GitHub-hosted
  `ubuntu-latest`; it checks that the migrations match the model, then
  runs the tests. Test steps set `DOTNET_USE_POLLING_FILE_WATCHER=1`: each
  test host watches files, and a runner with a low per-user inotify limit
  (128) failed most tests without it. To release, push a tag `vX.Y.Z` from a commit
  whose CI passed. The user pushes tags themselves when a tag has to be
  deleted or moved.
- Operator-experience fixes (gaps found when the first image was
  deployed cold from the image alone); see "Done: operator-experience
  fixes" below.
- Milestone 7, email notifications (§15), built on 2026-10-16; see "Done:
  milestone 7" below.

### Versions

Decided 2026-10-18 (user): Pollyanna is still being built, so versions
restarted at 0.0.1, cut from `main` after the visual refresh and the
EventLifecycle and transaction refactors. Earlier releases are not
published; the work they shipped is recorded in the "Done" sections
below. The first release from GitHub is 0.0.3.

- 0.x follows the usual convention: the patch number (0.0.x) for fixes,
  the minor number (0.x.0) for features and anything that breaks
  operators (renamed settings, a migration with no way back).
- 1.0.0 is the user's call, when they declare the app finished.

### Done: operator-experience fixes

Decided and built 2026-10-16. The rules are in §3, §5.3, §8 and §11.

- `Infrastructure/Help.cs` holds the help text (reviewed by the user before
  the rest was built) and `Help.Version`; `Commands.cs` runs `help`,
  `--help` and `-h` and prints the usage for an unknown first word.
  `PollyannaConfigurationException.Report(config)` is what operators see: the
  problems, the redirect URI (`PollyannaSettings.CallbackUrlFrom`) and the
  pointer to `help`.
- `StartupTasks.CheckConfiguration` runs in Program.cs before the host
  starts and again in `StartAsync`. `DrawKey.Created` lets `StartAsync`
  log the new-key warning, or the error counting drawn events.
- `Auth/SignInFailure.cs` decides the log line and the failure page for
  challenge failures (caught in `Pages/Auth/Login.cshtml.cs`) and remote
  failures (`OnRemoteFailure`). The provider variant of `/auth/failed` is
  `?reason=provider`.
- The mock issuer's `PushedAuthorization` option (off by default; in
  development, `--MockIssuer:PushedAuthorization=true`) advertises and
  serves `/par`. `TestApp.StartAsync(pushedAuthorization, configure)` turns
  it on or changes a setting or the backchannel.
- Data Protection uses `NullXmlEncryptor` rather than filtering its
  warning; the logging API can't filter one event ID. EF's rebuild warning
  is ignored with `ConfigureWarnings`.
- `LogCapture` entries carry their level. Tests: `DeploymentTests` (help,
  usage, report, draw-key messages, ready line), `SignInFailureTests`,
  `QuietLogTests` (including a subprocess run of `Pollyanna.dll` with a
  settings error), and the `email_verified` hint in `RelinkTests`.
- Checked by hand in Docker with the mock issuer: `help`, labels with
  CI-style properties, docs in `/app`, a first start whose only warning is
  the new draw key, quiet `/healthz`, PAR sign-in, a rejected PAR showing
  the provider variant of the failure page, backup, the usage error, and a
  settings error without a stack trace.
- Not settled: the `org.opencontainers.image.licenses` label, because the
  repository has no license yet.

### Done: milestone 7 (notifications)

Built 2026-10-16. The rules are in §15, the settings in §11, and the
reasons in docs/adr/0001, 0003 and 0004.

- Settings: `PollyannaSettings` reads `POLLYANNA_SMTP_*` and
  `POLLYANNA_MAIL_FROM` into `MailSettings` (null when no host is set),
  checked with the other settings at startup; `Help.cs` lists them, and
  `DEPLOYMENT.md`, `deploy/.env.example` and CONTRIBUTING.md (Mailpit) cover them.
- `NotificationKind` (the eight kinds, stored by name) sits with the
  entities in `Data/Entities.cs`. Everything else is in
  `src/Pollyanna/Notifications/`: `Audience.cs` (the `Audience` enum and
  `AudienceRules`: who is in it, whether a row's recipient still is, and
  the footer); `NotificationKindDefinition.cs` (`NotificationCause`, the
  `Recipient` person-or-address, `WaitingCover`, and
  `NotificationKindDefinition<TCause>`: `Audience`, `RecipientsAsync`
  (default: the audience), `NamedPerson`, `HoldUntil`,
  `CoveredByWaiting`, an optional `IsStillWantedAsync` drop rule,
  `Compose`), with one subclass per kind in `Kinds/` (`EventOpen`,
  `AddedToEvent`, `Invitation`, `MadeOrganizer`, `NamesDrawn`,
  `EventRevealed`, `Question`, `RedrawNeeded`), each with its own nested
  `Cause` record and found by reflection, so every rule about a kind
  is in its file; `Outbox` (scoped: `QueueAsync(cause)` runs the shared
  steps around the kind's answers: no-op without a relay, the actor
  always left out, the email switch, the covering lookup); `OutboxSender`
  (singleton hosted service: the startup relay check, the poll and
  wake-up loop, each row read again for its turn, the shared drop rules
  in `PrepareAsync`, `Render` for the link and footer, retries and giving
  up); `MailSender.cs` (`IMailSender`, MailKit's `SmtpMailSender`, and
  `Describe` for log-safe errors); `EmailSwitch` (scoped: sets
  `EmailsOn` and deletes waiting rows).
- Where causes are queued: `EventManager` (`OpenAsync`: open emails and a
  draft's invitations; adding people and organizers, inviting),
  `PersonSignIn` (accepted invitations),
  `EventManager.Draw.cs` (draw, late joiner, leaving; the redraw calls
  `NamesDrawn.WithdrawWaitingAsync`), `EventManager.Reveal.cs`,
  `EventManager.Questions.cs` (posting a question) and
  `EventManager.Accounts.cs` (account deletion asking for a redraw).
- Data: migration `Notifications` adds the outbox table (`Kind`,
  `EventId`, `PersonId` or `Email`, `NamedPersonId`, `NotBefore`,
  `FailedAttempts`, `FirstFailedAt`; no queue time, ADR-0004); migration
  `EmailSwitch` adds `People.EmailsOn` (on for everyone).
- `/account` shows the "Emails" switch only when a relay is configured
  (handler `emails`).
- Tests: `FakeMailSender` replaces the relay, and `TestApp.StartAsync(mail:
  true)` turns email on. The background sender doesn't run in tests
  (`OutboxSenderOptions.SendInBackground` is off), so mail goes out only when
  a test calls `app.SendMailAsync()`, at the fake clock's time.
  `NotificationTests` (open email, outbox rules, retry and give-up, logs,
  an unreachable relay), `OutboxRunTests` (changes in the middle of a
  run), `ConfigurationTests` (SMTP settings),
  `EmailSwitchTests`, `DrawNotificationTests`,
  `MembershipNotificationTests`, `InvitationEmailTests`,
  `QuestionNotificationTests`, `RedrawNeededNotificationTests`, and
  `NotificationSecrecyTests`: what is queued and sent is the same when
  everyone else's recipients are shuffled; across every kind no email
  holds a message, notes, or a giver's or recipient's name; nothing is
  queued with emails off or without a relay.
- Checked by hand against Mailpit (CONTRIBUTING.md): the "Event is open"
  email arrives as plain text with its link and footer.
- Accepted (§15 "Sending"): a change that would drop a row (switching
  emails off, losing the role, a cancelled invitation, reading the thread,
  a withdrawn request to leave) can't stop that row's email once the relay
  has it. The sender reads each row again when its turn comes, so a row
  deleted earlier in a run (a redraw, the switch) isn't sent, and one
  deleted while its email goes out doesn't fail the run.

### Done: rename to Pollyanna

Decided 2026-10-17 (user). The product was hatpick before it.

- The name is "Pollyanna", in Title Case wherever people read it: the
  header, page titles, page text, emails, logs, help and docs. Code uses
  `Pollyanna` (projects, namespaces, `PollyannaDb`, `PollyannaSettings`,
  the `/app/Pollyanna` binary); settings use `POLLYANNA_*`. Lowercase
  `pollyanna` is for identifiers: the image repository, the Compose
  service, `pollyanna.db`, the cookies (`__Host-pollyanna`,
  `pollyanna-af`, `pollyanna-notice`), the `pollyanna:person` claim, the
  data protection application name and example hosts.
- Only the name changes. The hat graphics (logo, favicon, touch icon) and
  hat words in the domain (`SealedHat`, "pick-from-a-hat", §12) stay.
- A hard cut: the `HATPICK_*` names are not read. The one hatpick install
  was migrated by hand, so when versions restarted at 0.0.1 (see
  "Versions") the `hatpick.db` startup guard and DEPLOYMENT.md's
  "Upgrading from hatpick" steps were removed. The new cookie name signs
  everyone out once; the new data protection application name only voids
  in-flight form tokens and sign-in attempts.
- Migrations keep their IDs (EF's history table stores those, not
  namespaces); only their namespaces and the snapshot class changed.
- The repository is `MacheteBang/pollyanna` on GitHub, and the image is
  `ghcr.io/machetebang/pollyanna`, linked to it.

### Done: visual refresh (bow and gift box)

Decided 2026-10-17 (user); §12 has the design.

- The top hat is gone. The favicon and apple-touch-icon are the bow
  (Fluent "Ribbon", recoloured; the favicon without its folds, the touch
  icon on a kraft square), the masthead is the girl, and the empty home
  page is a gift box with the bow and a tag under "Nothing to unwrap
  yet".
- Ribbon blue is the `--ribbon` token in `site.css`, used only by the
  girl's bows, the gift box and the reveal ribbon.
- The reveal card's folded slip is tied with a ribbon band and bow, and
  its button reads "Pull the ribbon". With the script, the bow lifts off
  and the band slips away (600ms, class `untying`) before the card opens
  and unfolds; with reduced motion or without the script, it opens at
  once. "Fold it back up" is now "Tie it back up".
- Hat words in the domain and copy elsewhere (`SealedHat`, "The hat
  needs at least…") are unchanged; "Secret Santa" in copy is gone too
  ("Done: neutral copy" below).

### Done: neutral copy

Pollyanna reads as a gift exchange for any occasion, not only Christmas.
Decided with the user on 2026-10-17:

- User-facing copy calls the giver "your giver", never "Secret Santa":
  "Your giver asks…", "Questions from your giver", "Your giver asked you
  something", and the label "Giver" on their notes in a thread.
- The stocking framing goes as well, in favour of gift wording. The reveal
  card reads "You're getting **Carol** a gift" ("You got **Carol** a gift"
  once archived); the event page, manage page and names-drawn email say
  "who you're giving to"; the questions page says "The person getting you a
  gift has a question". The new-event placeholder is "e.g. Family gifts
  2026". `--help` and the invitation email say "gift exchange".
- Tests: `PairingSecrecyTests` checks no page a viewer reaches mentions
  Santa or stockings, and `NotificationSecrecyTests` checks the same for
  every kind of email.

### Done: about line

Decided 2026-10-18 (user): one line of copy
saying what Pollyanna is, with a nod to the name (§7 has the text and where
it shows, §12 the rule on the name). It lives in `Pages/Shared/_About.cshtml`
and is styled by `.about`. No glossary entry: "a Pollyanna" is copy, not a
domain term. Tests: `PageTests` checks it on `/` with and without events and
on `/auth/signed-out`.

### Done: event lifecycle module

What each status allows, and for whom, is worked out in one place. Decided
with the user on 2026-10-18, as a pure refactor (no behaviour change):

- `Events/EventLifecycle.cs` holds the per-status rules as named status sets
  (`ShownToParticipants`, `BeforeDraw`, `BeforeReveal`, `NamesDrawn`, `Live`,
  `Over`, `Active`, `Archived`), each listing its statuses explicitly, and the
  transitions (open, draw, redraw, reveal, archive), each with the statuses it
  is allowed from, the status it goes to and the message when it's refused.
  Queries use the sets with `Contains`; status-conditioned updates claim a
  transition on its `From`.
- `Membership` adds who is asking: `CanView`, `CanLeave`, `ThreadsReadable`,
  `PeopleEditable`, `PeopleChangeable`, `IsLive`, `IsOver` and `Allows(transition)`.
  `EventManager`, the page models, the account and picture queries and the
  notification kinds ask these instead of comparing statuses. Views still
  switch on the status to choose what to show.
- The organizer check stays on the page (`Manage`); messages for refused
  actions other than transitions stay with each action in `EventManager`.
- Tests: `EventLifecycleTests` has a table per rule and transition with a
  column per status, so a new status fails there until it is placed.

### Done: one transaction helper

Every transaction in `EventManager` goes through `InTransactionAsync`. Decided
with the user on 2026-10-18 (no behaviour change):

- It begins the transaction, runs the work and commits only if the result says
  to (an `Outcome` overload commits when `Ok`). Otherwise, or if the work throws,
  it rolls back and clears EF's change tracker, so no entity tracked inside the
  transaction keeps values the rollback undid. Before, only account deletion
  cleared it; leaving after the draw carried on with stale entities.
- Work that follows a rollback starts from the database, not from entities read
  before it: leaving after the draw and account deletion both record the request
  to leave with an `ExecuteUpdate` in a transaction of their own.
- `SealedHat` keeps its own saves. It still never begins or commits a
  transaction, and saving inside the caller's transaction costs nothing; some of
  its saves (the patches, the message number retry) have to happen mid-way anyway.
- `EmailSwitch` keeps its own transaction: it tracks nothing.

### Done: README for operators

Decided with the user on 2026-11-03 (issue #3), once the repository was public:

- License: AGPL-3.0. `LICENSE` holds the text; the fonts and the bow keep
  their own licences in `THIRD-PARTY-NOTICES.md`.
- README.md is for people who want to run Pollyanna: what it is, features,
  screenshots, what you need, a six-step quick start ending in a warning to back
  up the draw key, upgrading and backups, alternatives (from §1 "Prior art"),
  contributing and the license. It links into DEPLOYMENT.md, which stays the
  full operator guide. Badges: CI, latest release and license only. No hosted
  demo, since sign-in needs a real provider; CONTRIBUTING.md's "Try it locally"
  stands in.
- CONTRIBUTING.md takes the old README's development and layout sections
  (mock provider, Mailpit, PAR, tests, migrations, code layout), and says that
  Pollyanna is developed with AI coding agents, whose instructions are
  `CLAUDE.md` and `docs/agents/`.
- The banner (`docs/assets/banner.svg`) is the girl on the kraft beside a slip
  tied with a ribbon band and bow, holding the wordmark in Courier Prime Bold,
  converted to outlines because GitHub won't load web fonts in an SVG. Light
  only. The user picked it from three mockups.
- Screenshots (`docs/screenshots/`) come from the dev app with demo people
  (`example.com` addresses) and the "Cousins' Swap" event: home, the event page,
  manage and the question box, two of them on a phone, plus a GIF of the reveal.
  Desktop shots are 640 CSS px wide at 2x.

## 15. Email notifications

Built in milestone 7. Email tells people when something in an
event needs them, without weakening the sealed draw (§5.2).

**Channel.** Plain-text email over a mail relay the operator configures
(§2, §11). With no relay configured the feature is off: nothing is queued,
the `/account` switch is hidden, and the app behaves as in v1. There is no
other channel (§1).

**What is emailed.**

| Kind | To | When |
|---|---|---|
| Added to an event | the new participant | when added: by an organizer (also by inviting the address of someone who has signed in, and as a late joiner by patch or redraw), or by an invitation accepted at sign-in; for a `draft` event, not until it opens (the "open" email covers it). The wording follows the status when it is sent: "write your notes" while `open`, "see your slip and write your notes" once drawn. It doesn't name who added them. |
| Invitation | an invited email address with no account yet | when the invitation is made, or when the event opens if it is still a `draft`. Names the organizer who invited ("Someone" if their account is gone). Inviting the address again while its email still waits adds nothing. |
| Made an organizer | the new organizer | when made one, in any status, `draft` included; names who did it and links to the manage page |
| Event is open | every participant | `draft → open`: subject "<event> is open: write your notes" |
| Names are drawn | every participant | after a draw; after a redraw's new draw the wording is "Names were drawn again, take another look" (chosen when sent, from the event's redrawn flag, which stays set). The redraw itself (back to `open`) sends nothing and deletes the "Names are drawn" rows still waiting for the event. |
| Event is revealed | every participant and organizer | `drawn → revealed` |
| A question for you | the recipient | a giver posts in their thread; held to the next full hour (below) |
| Redraw needed | every organizer | a participant's request to leave needs a redraw (§4), from leaving or from deleting an account: "<Name> wants to leave <event>; a redraw is needed", plus a sentence pointing at the manage page, which it links to. One waiting row per organizer and leaver; dropped unsent once the request is no longer waiting (withdrawn, or cleared by a redraw or the reveal). |

**What is never emailed:** a recipient's reply to their giver, "your
recipient has changed" after a patch, removals (as participant or
organizer), archiving, accepted invitations (to the organizer), and note
edits. Those stay in-app (badges, dots, notices) or aren't shown at all.

**The rule behind it:** no email is ever addressed to a giver because of
their own assignment (a reply, a closed or reopened thread, a patch, a
recipient's note edit). Givers get only the emails sent to every
participant at once. A new kind that would reach one giver, or some
givers but not all, is out (docs/adr/0001).

- **Why replies aren't emailed.** The operator runs the relay and can read
  the database. Thread rows carry `recipient_id` in plaintext and message
  times to the hour, so "your recipient replied" mailed to Bob at the hour
  Carol's reply was stored would show that Bob is Carol's giver, with no
  draw key needed. Even the mail log alone (for example at a third-party
  relay) would pair people off from the back and forth of question and
  reply emails. Emails to recipients are safe: the database already says
  whose thread it is, and the email names no giver. The same reasoning
  keeps "your recipient has changed" in-app only.
- **Questions are held to the hour.** A question email is sent at the top
  of the next full hour, matching the hour rounding of message times (§6),
  so it doesn't tell the recipient when their giver was online. Questions
  from the same thread within that hour make one email.
- **Not for your own actions.** Nobody is emailed about something they did
  themselves (an organizer adding themselves, the organizer who ran the
  draw).

**Content.** Each email says what happened, names the event and links to
the page (`<public URL>/events/{id}` and so on). It never contains a
message body, anyone's notes, or the name of a giver or recipient; draw
emails say "your slip is ready", not whose. Organizer emails may name the
person concerned where the manage page already does (redraw needed). Who
acted may be named where it isn't secret ("Alice made you an organizer").
The footer says why the email came and links to `/account` to turn emails
off. An invitation's footer instead says to ignore the email if it wasn't
meant for the reader. Emails don't greet the reader by name. The text is
built when the email is sent, so it uses the event's name at that moment;
a named person whose account is gone appears as "Former participant".

**Address.** The email from the person's last sign-in, unless the provider
sent `email_verified: false` for it (the same leniency as invitations,
§4). Someone with no email claim gets nothing. Invitation emails go to the
invited address.

**Opting out.** One switch on `/account`, "Email me about Pollyanna events",
on by default. There are no per-kind settings. Every page needs sign-in
(§3), so the footer link leads to `/account` rather than to a one-click
unsubscribe. It is a checkbox with a Save button under the heading
"Emails", posting to `/account/emails`; the notice reads "Emails are on."
or "Emails are off.". The switch is checked when a row would be queued
(nothing is queued while it is off) and again when it is sent; switching
off deletes the person's waiting rows at once, so switching back on only
affects later notifications. Invitation rows have no person and so no
switch.

**Sending.**
- Notifications go into an outbox table in the same database, written in
  the same transaction as the change that causes them. A background worker
  sends them through the relay, one connection per email. It is woken when
  rows are saved and otherwise looks every 15 seconds (for held and retried
  rows).
- A failed send is retried after 1, 2, 4 … minutes, at most an hour apart;
  the 24 hours count from the first failure. A row keeps no time it was
  queued (docs/adr/0004).
- A row holds the kind, the event, and the person (or, for an invitation,
  the email); the address is read when it is sent, and the switch is
  checked again then (it was already checked when the row was queued). A row
  is deleted once sent, dropped after 24 hours of failed attempts, and
  dropped if the person turns emails off or deletes their account first.
  An invitation row is dropped unsent if the invitation is no longer
  pending (cancelled, or accepted at sign-in, which sends "Added to an
  event" instead). Any other row is dropped unsent if the person no
  longer has the role it was addressed to in that event (participant or
  organizer), for example a dropout's waiting "Names are drawn".
  Question rows carry a not-before time (the next full hour), and a new
  question in a thread that already has a waiting row (held or being
  retried) adds nothing. If the recipient has read the thread by then
  (`recipient_read_upto` has caught up with every giver message), or the
  event is no longer `drawn`, the row is dropped unsent. Slip emails
  ("Names are drawn") get no status check when sent; the redraw deletes
  them and the role rule covers a dropout.
- These checks are made just before each email goes to the relay. A
  change that comes while that email is already being sent (switching off,
  losing the role, cancelling the invitation, reading the thread,
  withdrawing the request to leave) can't call it back: SMTP isn't
  transactional, so that one email still arrives. This is accepted.
- Nothing keeps a record of sent mail. Failures are logged by kind and SMTP
  error only, with no addresses and no event, assignment or message IDs
  (§8). Successful sends and rows dropped by a rule aren't logged; only a
  failure and giving up are.

