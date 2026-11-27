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
- Fully self-hosted: the app relies only on services the operator chooses to
  configure. Its only network dependencies are the identity providers the
  operator configures (§2, §3), the hosts that serve people's profile
  pictures and, if email notifications are turned on (§15), a mail relay the
  operator chooses. External providers such as Google are optional: the
  family's own provider can be the only one.

**Non-goals (v1)**
- Wishlists with item links, claiming, or price tracking.
- Web push and app push. They depend on a browser vendor's push service the
  operator didn't choose. Notifications reach people by email (§15) and in
  the app, through the "What's new" menu and pages that update while open
  (§16, which lifted the old "no in-app notification list" non-goal on map
  #82, November 27, 2026).
- Participants without an identity-provider account (proxies). Everyone who
  takes part, children included, signs in with their own account at one of
  the configured providers (ADR-0006).
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
| Provider label / admission (optional) | The unnamed provider's button label (default: the issuer's host) and admission (default: `trusted`) |
| More providers (optional) | `POLLYANNA_OIDC_PROVIDERS` lists extra provider keys; each key has its own issuer, client ID, secret (value or file), label and admission, all required |
| Provider icons (optional) | `POLLYANNA_OIDC_ICON_FILE`, or `POLLYANNA_OIDC_<KEY>_ICON_FILE` for a listed provider: an image the operator supplies for that provider's buttons |
| Draw key location | Where the draw key lives (§5.3); generated on first start if absent |
| Data location | Where the app's data is stored |
| Mail relay (optional) | SMTP host, port, TLS mode, login and From address for email notifications (§15); the password is kept out of plain config. Unset: no email. |

- Providers are set by environment variables only, with no settings file:
  - The unnamed set (`POLLYANNA_OIDC_ISSUER`, `_CLIENT_ID`, `_CLIENT_SECRET`
    or `_CLIENT_SECRET_FILE`) is a full provider with the key `default`. It
    gains an optional `POLLYANNA_OIDC_LABEL` (default: the issuer's host), an
    optional `POLLYANNA_OIDC_ADMISSION` (default: `trusted`) and an optional
    `POLLYANNA_OIDC_ICON_FILE`. It may be
    the only provider or sit next to listed ones. At least one provider must
    exist, so with nothing listed the unnamed set is required.
  - `POLLYANNA_OIDC_PROVIDERS` lists extra keys, separated by commas or
    spaces. Keys match `[a-z][a-z0-9]*` and are uppercased in setting names;
    `default` is reserved. Each key needs `POLLYANNA_OIDC_<KEY>_ISSUER`,
    `_CLIENT_ID`, exactly one of `_CLIENT_SECRET` or `_CLIENT_SECRET_FILE`,
    `_LABEL` and `_ADMISSION` (`trusted` or `open`, no default).
    `_ICON_FILE` is optional.
  - An icon file is an image the operator supplies, such as a provider's
    downloadable button artwork; the repo ships no third-party logos. It is
    read and checked at startup: it must be readable, at most 1 MB, and a
    PNG, JPEG, GIF or WebP (by its first bytes) or an SVG (well-formed XML
    with an `svg` root in the SVG namespace and no DTD).
  - Order: the unnamed provider first, then the list's order.
  - Admission is `trusted` (anyone it authenticates gets in) or `open` (only
    a verified email that matches a person or a pending invitation gets in,
    §3). A switch between them takes effect at the next start and affects
    only logins no person holds yet.
  - Settings errors, each naming its setting in full: two providers with the
    same issuer once trailing slashes are trimmed (naming both settings); a
    `POLLYANNA_OIDC_<KEY>_*` setting whose key isn't listed; an unknown
    field of a listed key, such as a misspelled `..._LABLE` ("isn't a setting
    Pollyanna knows"); a repeated,
    malformed or reserved key; any provider field missing or invalid, under
    the same secret and https rules for every provider; an icon file that is
    missing or isn't a usable image. When the public URL is
    valid, the closing hint lists the redirect URI of every provider the
    settings name.
  - The startup line `Pollyanna <version> is ready at <url>` is followed by
    one line per provider, e.g. `sign-in provider "Google" (google, trusted):
    redirect URI https://pollyanna.example/auth/callback/google`. The unnamed
    provider shows as `(default, trusted)`.

## 3. Authentication and authorization

- Every page requires authentication, except `/healthz` and the sign-in
  routes themselves (`/auth/...`).
- The only way to authenticate is OpenID Connect: authorization-code flow
  with PKCE against one or more configured providers. The app must work with
  any standards-compliant provider, and no provider is special-cased in code.
  Which providers are used is deployment configuration (§2), not part of the
  app. Development and tests use local test providers.
- One module, the provider registry, owns the configured providers: key,
  label, issuer, admission, callback path and optional icon, in configured
  order. Each
  provider has its own OpenID Connect authentication scheme. PKCE, the scopes,
  the `state` and `nonce` rules, and a pushed authorization request when the
  provider advertises one apply to every provider alike.
- `state` and `nonce` are random values of at least 128 bits.
- Scopes: `openid profile email`. Callback: `<public URL>/auth/callback` for
  the unnamed provider, `<public URL>/auth/callback/<key>` for a listed one,
  so each client gets its own redirect URI and an existing registration still
  matches. No post-logout redirect URI is needed. The startup log and the
  settings error report every provider's callback URL so the clients can be
  registered before the first sign-in (§2, §11).
- `/auth/login`: with one provider, it goes straight to that provider. With
  several, it shows a chooser: "Sign in to Pollyanna", the short about text
  (§7), then one equal, full-width "Continue with <label>" button per
  provider in configured order. Each button links to
  `/auth/login/<key>` and carries the return URL. The chooser remembers
  nothing and stores no cookie.
- Provider icons: with an icon file (§2), the provider's logo sits in an
  `<img>` (empty `alt`, since the label names the provider) before the label
  on every button for that provider: the chooser, the refused page and the
  account page's "Add" buttons. Without one, the button is text only.
  Pollyanna serves the icon itself at `/auth/icon/<key>` to anyone, signed in
  or not, with its checked content type, `nosniff` and a CSP that blocks
  everything (so an SVG opened on its own runs nothing). Buttons link to it
  with a content hash (`?v=`) and it may be cached for a day, so a changed
  file reaches browsers at the next start. No provider is special-cased: Google's logo is
  just a file the operator names.
- Sign-in failures go to `/auth/failed` ("Sign-in didn't work"), never the
  general error page. That includes failures before the redirect to the
  provider (discovery, metadata, a rejected pushed authorization request).
  When the provider rejected Pollyanna's client or couldn't be reached, the page
  adds: "The sign-in service turned Pollyanna away. Whoever runs this site
  needs to check its settings." The log names the provider's `error` code
  (e.g. `invalid_client`) and the settings to check; the page shows no
  details, and the client secret is never logged. The log names the provider
  that failed, and the settings it names are that provider's (e.g.
  `POLLYANNA_OIDC_GOOGLE_CLIENT_ID`).
- A person signs in through one or more **logins** (§4). A login is one
  provider account: the provider's issuer plus `sub` claim, unique across all
  logins. It maps to Pollyanna's own person ID. Everything else in the app
  (participation, sealed pairings, history) uses the Pollyanna person ID, so
  a change of provider account doesn't lose anything.
- Linking by email: if a sign-in has an unknown login but a verified email
  that matches the current email of exactly one person (tombstones never
  count), the login is added to that person instead of a new person being
  created. It is added next to their other logins and never replaces one, so
  the earlier login keeps working. This covers a recreated account or a
  switch of provider.
  - "Verified" here means the provider sent `email_verified: true`. An email
    with no `email_verified` claim is never used for linking (unlike
    invitations, §4). Matching is case-insensitive.
  - If the email matches two or more people, nothing is linked and a new
    person is created. One info line gives the number of matches, naming
    nobody.
  - The first sign-in through each provider in a process run that has no
    `email_verified` claim logs one info line, naming the provider and
    nobody else. For a trusted provider: invitations by email still match,
    but adding a login by verified email won't happen with this provider.
    For an open provider: new sign-ins through it are refused and it accepts
    no invitations, because a missing claim counts as unverified.
- On every sign-in, display name, email and picture take the values just
  received, but a claim that isn't sent never clears a stored value. The
  login's last email is updated the same way.
- Managing logins ("Ways to sign in" on `/account`, §7):
  - **Adding.** With more than one provider, each "Add <label>" button
    (a POST) challenges that provider with the intent "add a login" and the
    signed-in person carried in the protected authentication properties. On
    return, the callback checks that the session still belongs to that
    person; if not, nothing changes and the person lands on `/auth/failed`.
    A fresh round trip proves control, so the login is attached with no
    email match, no `email_verified` check and no open-provider admission
    check. It counts as a sign-in for name, email and picture, and accepts
    invitations to the new email under the same rule as any sign-in (§4):
    through an open provider, only with `email_verified: true`. Two logins
    from the same provider are
    allowed. No new session is made; the current one carries on. Notice:
    "Added."
  - A login another (living) person already holds is refused with "That
    sign-in already belongs to another Pollyanna account." Nothing is merged.
    A login the person already holds gives "That's already one of your ways
    to sign in." and changes nothing.
  - **Removing.** A plain POST per login, no confirmation page, notice
    "Removed." Any login can be removed, including the one the session was
    made through (the session belongs to the person and carries on) and
    logins from removed providers. Removing the last login is refused with
    an error notice that points to deleting the account instead, and the
    list's own text links to `/account/delete`. Removed logins aren't
    remembered or blocked: a matching verified email adds one back on its
    next sign-in, and the page says so.
  - One info log line each for a login added, removed, or refused as another
    person's, giving the provider key and the person ID, never the email. No
    email is sent about any of these.
- Profile pictures: on login, the server downloads the `picture` URL and
  stores the image; the browser only ever loads it from Pollyanna
  (`/people/{id}/picture`), so the strict CSP holds.
  - Fetch rules: `http`/`https` only, 5-second timeout, at most 1 MB, PNG,
    JPEG, WebP or GIF only (no SVG). Served with its checked content type and
    `X-Content-Type-Options: nosniff`.
  - Any failure keeps the previous image, and so does a sign-in that sends no
    `picture`. Initials show only when no picture was ever stored. A failed
    fetch never blocks sign-in.
  - Only people who share an event with the person, or an organizer adding
    people (an organizer of any event in `draft` or `open`), can load it. It
    is deleted with the account.
  - Pictures appear small beside names in the people lists (event page,
    manage page): the picture, or the initials on pale blue.
- Admission: anyone a **trusted provider** authenticates may use the app.
  The app does no group checks of its own; limiting who can sign in is the
  provider's job. An **open provider** lets in only someone whose verified
  email matches a person or a pending invitation. The gate runs only for a
  login no person holds: a known login always signs in, whatever the
  provider's admission, even after every invitation and event that let it in
  is gone. For an unknown login through an open provider:
  - `email_verified` must be `true`; a missing claim counts as `false`.
  - An email that matches exactly one person's current email (tombstones
    never count) adds the login to that person, as in linking by email
    above.
  - Two or more matching people refuse the sign-in, even when an invitation
    for the address is also pending.
  - Otherwise at least one pending invitation for the address must exist
    (pending invitations exist only in `draft` and `open` events, and a
    canceled one is deleted, so it never counts). A new person is created
    and the invitations turn into participants (§4).
  - Someone admitted this way is a full person, and nothing records which
    provider admitted them.
- Refusals (open providers only): no session, person or login is stored. One
  info log line names the provider key and the reason (`no invitation or
  person`, `email not verified`, `ambiguous: N matches`), never the email or
  `sub`. The person lands on `/auth/failed` with a heading and text per
  reason, then every provider's "Sign in with <label>" button again,
  outlined, in configured order and carrying the return URL:
  - No invitation or person: "Pollyanna doesn't know you yet" / "Pollyanna
    doesn't know **<address>** yet. Ask whoever organizes your family's
    exchange to invite that address, or sign in another way."
  - Email not verified: "<Provider> hasn't confirmed this address" /
    "<Provider> says it hasn't verified **<address>**, so Pollyanna can't use
    it to let you in. Confirm it with <Provider>, or sign in another way."
  - Ambiguous: "Pollyanna can't tell who you are" / "More than one person in
    Pollyanna uses **<address>**. Ask whoever organizes your family's
    exchange to sort it out, or sign in another way."
  - The provider, reason, address and return URL reach the page as
    protected data that expires after 10 minutes, never as plain query
    values, so a crafted URL can't make the page show an address. A missing,
    altered or expired one shows the general "Sign-in didn't work" page.
- There are no site-wide admins. All management is per event, by that
  event's organizers (§4).
- Devices are assumed to be personal. "Sign out" ends only the Pollyanna
  session, not the provider's, and shared-device use is not designed for
  beyond the "Untie the bow" guard (§7).
- No local passwords and no fallback login. If the provider is down, the app is
  down.
- Server-side sessions (random 256-bit ID in an `HttpOnly`, `Secure`,
  `SameSite=Lax` cookie), 30-day sliding expiry. CSRF token on every form.
- Organizers get no extra visibility into draws (§5.5). An organizer may also
  be a participant, and is then treated like any other participant.

## 4. Domain model

**Person** — created on first sign-in.
`id`, `display_name`, `email`, `email_verified`, `picture_url`, `picture`
(stored image and its type), `created_at`, `last_login_at`. A person has one
or more logins.

**Login** — one provider account linked to a person (§3).
`id`, `person_id`, `issuer` + `sub` (unique together across all logins),
`last_email` (the email that login last sent), `created_at`, `last_used_at`.
- A person holds at least one login; only a tombstone (below) has none, and
  "has no logins" is how a tombstone is told apart: it can't sign in, be
  picked, match an email or be emailed.
- Upgrading from a single issuer and `sub` per person turned each person's
  pair into one login whose last email is the person's current email.
  Tombstones got no login.

**Deleting an account.** A person can delete their own Pollyanna account:
- It is blocked while they are the last organizer of any event that isn't
  archived or canceled; they add another organizer first. Their organizer roles are
  removed everywhere (archived events need no management, so one may end up
  with no organizer). `created_by` and `invited_by` keep pointing at the
  tombstone, and pending invitations they sent stay.
- It applies the leaving rules (below) to every event that is neither
  revealed nor archived. If leaving any of them is held for a redraw,
  deletion waits until that is resolved. In revealed, archived and
  canceled events they stay in, as a tombstone.
  - All or nothing: every event is checked first. If any `drawn` event
    can't patch them out, nothing else changes: those events get the
    "wants to leave — redraw needed" request, and the account page says
    deletion waits for that redraw. Once nothing blocks it, deletion leaves
    every event at once.
- Their profile (name, email, picture), every login and all their notes are
  deleted. The person row itself stays as an empty tombstone (its `id`
  only), so foreign keys and sealed pairings stay valid. In archived and
  revealed events they appear as "Former participant", and revealed lists
  and the no-repeat rule keep working without a name.
- Messages they wrote in revealed and archived threads stay, so the other
  side keeps their conversation; the deleted side is shown as "Former
  participant".
- Signing in again later, through any of their old logins, creates a fresh
  person, with no link to the old history.

**Event**
`id`, `name`, `year`, `budget_text` (free text, e.g. "$25"),
`exchange_date`, `status`, `previous_event_id` (nullable; used for the
no-repeat rule), `no_repeat_depth` (N, default 1), `pairings_revealed`
(set at reveal and kept through archive, so an event archived straight from
`drawn` stays sealed), `created_by`, `created_at`.
- An organizer can link an event only to a previous event they also
  organize, and never to a canceled one.
- When an event is created linked to a previous one, its participants are
  pre-filled from that event.
  The new-event form preselects the most recent event the creator organizes.
  The form itself adds nobody else (decided on #53, 2026-11-26, user,
  replacing the add-people control the form carried): it creates the draft
  with the linked event's participants, if one is picked, and lands on the
  manage page, where the add-people control lives (Participant, below) and
  anyone not joining is taken out.
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
  The manage page's "Add an organizer" picker lists the same people as the
  add-people control (§4 Participant), names and pictures only, minus people
  who already organize the event, and they pick one. A typed address only
  finds an existing person and never creates an invitation, since an
  organizer must already have an account. An address that matches nobody, or
  more than one person, gives a notice naming nobody, and nothing changes. A
  picked person who isn't on the list is refused (not found). Like the
  add-people control, it works as a plain form, and the script adds the
  filter box.
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

draft, open, drawn, revealed ──► canceled   (any organizer, irreversible)
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

- `canceled`: the event is gone for everyone (decided on November 26,
  2026, so organizers can get rid of test and called-off exchanges).
  - Any organizer can cancel an event that is `draft`, `open`, `drawn` or
    `revealed`; an archived one is already out of the way. It is
    irreversible.
  - The manage page's "Bigger changes" group (§7) holds "Cancel this event". It
    opens a confirmation naming the event: canceling is permanent and the
    event disappears for everyone; how many participants and pending
    invitations it has; nobody is emailed; for a drawn or revealed event,
    the slips and any question-box messages go with it. A red "Cancel
    this event" button does it; "Keep it" leaves things as they were.
  - A canceled event is on no list (home page, past events, previous-event
    pickers) and every page of it is "not found", organizers included.
  - Canceling deletes the event's pending invitations, pending leave
    requests, question-box messages and waiting emails. Everything else
    (participants, notes, households, exclusions, sealed pairings) stays
    in the database, unseen.
  - It never counts for the no-repeat rule (§5.1). An event already linked
    to it keeps the link, shown as "a canceled event".
  - Account deletion treats it like an archived event (above).

Dates never trigger anything: every status change is made by an organizer.
There is no notes deadline. Revealing before the exchange date is allowed
after a confirmation step. Since `exchange_date` has no time zone, "before"
errs towards asking: until the date has begun everywhere (UTC−12). The
event page and home page show a countdown to the exchange date ("12 days to
go", "1 day to go", "Today's the day"; nothing once the date has passed).

Times are shown in each viewer's browser time zone. The server stores UTC
(message times exactly since map #82; older messages keep the hour they were
stored with, §6) and renders `<time datetime="…">`
elements; a small script formats them and computes the countdown from the
viewer's local date. `exchange_date` is a plain date with no time zone.
Without the script, the page falls back to the date alone.

**Participant** — `event_id`, `person_id`, `household_id` (nullable),
`notes` (likes, sizes, no-gos; editable until `archived`), `notes_version`
(counter, bumped on each edit), `done_shopping` (bool), `joined_at`.
- Only the participant edits their own notes and `done_shopping`. The
  "done shopping" toggle is on the event page once the event is `drawn`.
- Before the draw, organizers add participants with one control on the
  manage page (and the same control on the new-event form): a ticklist of the people they know, and an address that adds
  or invites (below). One post takes the ticked people and an optional
  address, and adds them all at once.
  - **The people they know:** every person whose account still exists and
    who is or was a participant or organizer in any event where the
    organizer is a participant or organizer, at any status, archived
    included. People already in the event and the organizer themselves are
    left out. The list shows names and pictures only; no email reaches the
    page. One query builds this list, and every people picker uses it.
  - A ticked person who isn't on the organizer's list is refused like any
    other bad input (not found), and nothing changes, so the list can't be
    bypassed by posting IDs.
  - It works as a plain form: without the script, the ticklist and a visible
    email field. The script adds a filter box that narrows the list by name;
    a full address typed into it shows a ticked "Add or invite <address>" row
    in place of the email field.
  - After the draw, late joiners are added one at a time (§5.4), through a
    picker that lists the same people.

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
  as a participant immediately and no invitation is stored, whether or not
  the organizer knows them. If it belongs to more than one person, nothing is
  stored, nobody is added (not even people ticked in the same post), and the
  notice reads "More than one person uses that address. Pick them from the
  list if they're there, or ask them which one to use." It names nobody.
  (Decided on 2026-11-25.)
- Otherwise, when someone signs in with a matching email address, each matching
  invitation turns into a Participant row and is deleted. Matching is
  case-insensitive, and uses only an email the provider marks as verified
  when it says so.
  - Through a trusted provider this leniency holds: an email with
    `email_verified: true` or no such claim matches, and `false` doesn't.
  - Through an open provider, only `email_verified: true` counts (a missing
    claim counts as `false`), on every sign-in: a known login, one the gate
    just admitted, and a login added from `/account`. A pending invitation is
    what lets an unknown login in at all (§3). Pending invitations exist only
    in `draft` and `open` events.
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
   A canceled event (§4) is skipped the same way, pairings or not.

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
| `thread_change_unseen` | bool: the thread was closed or reopened since the giver last opened the card or dismissed the notification (§16) |
| `giver_read_upto` / `recipient_read_upto` | `seq` of the last message each side has seen (§6), for the unread lines and the "What's new" menu (§16) |

None of these columns name the giver, and none hold timestamps. They are
also what the "What's new" menu works out a giver's and recipient's
notifications from (§16); no other row records them (docs/adr/0008).

- Each assignment also stores `notes_version_seen`: the recipient's
  `notes_version` when the giver last opened the reveal card. If the
  recipient's notes have changed since, the card shows "Notes updated since
  you last looked" and the home page's row for the event says so (§7). Opening the
  card clears it. A counter, not a timestamp, so it records nothing about
  when anyone looked. Before the giver has opened the card at all there is
  nothing to mark.
- The home page's line never names the recipient ("Your recipient updated
  their notes", "You're giving to someone new"): the home page isn't behind
  the bow. There is no dot (§12).
- Opening the card is recorded when it unfolds (the script posts it), or,
  without the script, when "Hide the name" is pressed.
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

- The "Someone joining late?" picker lists the same people as the add-people
  control (§4 Participant), names and pictures only, minus people already in
  the event, and the organizer picks one. A typed address only finds an
  existing person and never creates an invitation. An address that matches
  nobody, or more than one person, gives a notice naming nobody, and nothing
  changes. A picked person who isn't on the list is refused (not found). It
  works as a plain form, and the script adds the filter box.

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
viewed their assignment (bool, no timestamp stored), who has marked done
shopping, and the draw status. They cannot see any pairing until the event
is `revealed`. The organizing page updates these live (§16), so an
organizer with it open sees roughly when someone looked or marked done
shopping. That pairs no one and is accepted (decided on #90, November 27,
2026, user); nothing stores the time.

Organizers are trusted not to steer the draw. An organizer who also takes
part could tighten exclusions or redraw until they get a recipient they want;
the app neither blocks nor records this.

## 6. Anonymous question box

Lets a giver ask their recipient a question ("shoe size?") without revealing
who is asking.

- A thread belongs to one assignment. Messages store `assignment_id`, `seq`,
  `sender` (`giver` or `recipient` — a role, not a person), `body`,
  `created_at`, the exact moment it was sent (docs/adr/0007, superseding the
  hour rounding of ADR-0004). Messages stored before that keep their hour,
  with no migration and no marker.
- `seq` is a per-thread counter (1, 2, 3…) that orders the thread. The read
  markers `giver_read_upto` / `recipient_read_upto` (§5.2) hold the last
  `seq` each side has seen. A counter only counts within its own thread, so it
  says nothing about activity elsewhere.
- Read markers move only on a page load or a deliberate action (sending,
  closing or reopening the thread, dismissing its notification, §16). A
  message that arrives on an open page while it's open (§16) moves nothing,
  and neither does a live refetch: a "read" post sent on arrival would
  follow the other side's post in the proxy log and pair them.
- There are no "seen", "typing…" or "online" indicators in the question
  box, ever.
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
- An unread question shows first on the event page and as the event's line
  on the home page ("Your giver asked you something", §7), with no badge or
  dot. It shows the moment it is posted, everywhere: the questions page, the
  event page, home's line and the "What's new" menu (§16). A new question is
  also emailed to the recipient, held to the hour as batching (§15); a
  reply is never emailed to the giver. Replies, closing and reopening reach
  the giver's open page at once (§16).
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
| `/` | signed in | One panel per event the user takes part in or organizes: its name and the one thing waiting on them; then "New event" and "Past events". With no events, the empty home |
| `/events/new` | signed in | Create an event as a draft; the creator becomes its organizer and lands on the organizing page, where people are added |
| `/events/{id}` | participant | Before the draw, the notes editor; after it, the gift that unties to show who the user is giving to, with that person's notes and the question box; once the event is over, the pairings |
| `/events/{id}/untie` | participant | "Untie the bow" without the script: the event page with the gift already untied and nothing moving (§12 The reveal) |
| `/events/{id}/questions` | participant | The conversation with the user's giver |
| `/events/{id}/manage` | organizer | The organizing page: the next step; people and invitations; who can't draw whom; details and organizers; the bigger changes (dropout, late joiner, redraw, archive, cancel); once revealed, the full pairing list |
| `/past` | signed in | Archived events the user took part in or organized |
| `/whats-new` | signed in | "What's new" without the script: the "Home" back link, the title, then the same rows as the masthead panel, each with a small "Dismiss" form button that posts to `/whats-new/dismiss` (§16) |
| `/live` | signed in | The live connection (§16): a server-sent events stream every signed-in page holds open; no page or event in its path. A missing session gets a status, never the redirect to `/auth/login` |
| `/account` | signed in | "Ways to sign in" (§3): one row per login with its provider's label, the email it last sent, "(no longer set up)" when its issuer isn't configured, and "Remove"; with more than one provider, an outlined "Add <label>" button per provider in configured order; a note that a removed login whose verified email matches is added back on its next sign-in. Then "Emails" with the "Email me about Pollyanna events" switch (§15); "Sign out"; "Delete my account" |
| `/auth/login` | anyone | Starts sign-in. With one provider, goes straight to it; with several, the chooser (§3), whose buttons go to `/auth/login/<key>` |
| `/auth/icon/<key>` | anyone | A provider's logo for its buttons (§3); 404 without one |
| `/healthz` | anyone | Liveness, no data |

- `/events/{id}` also holds "Leave this event" (its confirmation step is the
  page `/events/{id}/leave`). An organizer who isn't a participant is sent to
  `/events/{id}/manage` instead.
- After sign-in, a user returns to the page they first asked for.
- The page set, the flows and every rule about who sees what are unchanged
  by the "Tablecloth" identity (§12). What changed on map #50 (decided
  2026-11-26 and 27, user) is what each screen shows, in what order, and its
  words; that is the rest of this section. How it looks is §12.

When adding people, organizers pick from the people they've shared an event
with, by name and picture only, and reach anyone else only by typing their
exact address (§4 Participant). No page lists everyone who has signed in, and
no people picker shows another person's email.

### What each screen shows

Every screen is ranked for a participant on a phone, who comes once or twice
a season, usually from an email. Each screen opens on the one thing that is
theirs to do; statuses and rare actions are demoted.

**Masthead.** One row: the girl and the wordmark "pollyanna" on the left,
linking to `/`; on the right the bell that opens "What's new" (§16), then
the account control, the person's first name linking to `/account`.
"Sign out" is on the account page only. Signed-out pages show the mark and
wordmark only.

**"What's new"** (decided on #89, November 27, 2026, user; what's in it is
§16, how it looks §12).
- The bell is a button labeled "What's new, 5 notifications" ("What's new,
  nothing new" when empty), with `aria-expanded`. It shows no number, word,
  badge or dot. Without the script it is a link to `/whats-new`.
- It opens a panel under the masthead headed "What's new". Each row is the
  notification's line as a link to the page it points to, with the event's
  name and the time in small soft ink under it (the three kinds with no
  time show the event only), and ends with a "×" labeled "Dismiss: <the
  line>". There is no "Dismiss all": seeing a page clears its
  notifications, and "Dismiss all" would mark messages read that nobody
  read.
- Empty: "Nothing new right now." and, small, "When something happens in
  one of your events, it shows up here."
- The panel is a disclosure, not a dialog. Focus stays on the bell when it
  opens and Tab goes into the panel; Escape or a tap outside closes it and
  puts focus back on the bell. After a dismiss, focus moves to the next
  row's "×" (the previous one at the end, the panel's heading when the
  panel is empty).
- A notification arriving while the panel is open takes its place in the
  order, without motion, and is announced politely ("Your giver asked you
  something, Cousins' Swap").
- The `/whats-new` page has no cloth, like the other pages with nothing to
  do.

**Page head.** Under the masthead: a small back link ("Home", or the event's
name on its sub-pages); the page title; one small soft line. An organizer
who also takes part gets the two-way switch, "My page" / "Organizing", on
both the event page and the organizing page, in place of the back link.
The event's name comes first there: the switch sits right under the title
(decided 2026-11-27, user). An organizer who doesn't take part sees no
switch and keeps the "Home" back link above the title.

**Home, with events.**
- One panel per event: the event's name and the one thing waiting on the
  viewer, which links to where it is done. Then the date and countdown.
  Status, budget and "You organize · N people" are small text.
- The waiting line:
  - `open`, no notes: "Write your notes"
  - `open`, notes written: "Nothing to do until names are drawn"
  - `drawn`, not yet looked: "See who you're giving to"
  - `drawn`, recipient changed by a patch: "You're giving to someone new"
  - `drawn`, the recipient's notes changed since the viewer last looked:
    "Your recipient updated their notes" (§5.2)
  - an unread question: "Your giver asked you something"
  - for an organizer: "Ready to open", "Ready to draw names", "Someone wants
    to leave"
- When several apply, the first of these shows: an unread question, a
  changed recipient, an organizer's line, updated notes, then the status's
  own line. No line names the recipient: the home page isn't behind the bow.
- There is no dot or badge; the line itself is the signal.
- "New event" is a quiet secondary action, then "Past events". No about
  text.

**Empty home.** Title "No events yet". One panel in two equal halves: "I was
invited", with the viewer's email address and a "Copy" button, to pass to
whoever organizes; and "I'm organizing one", with "New event". Below: the
about text (long version, under "About text" below), then "Past events".

**Event page, before the draw** (`open`).
1. Head: date, budget and countdown, the countdown the prominent part; the
   small status line (§12 Statuses).
2. One line, "Write your notes so your giver knows what you'd like. You'll
   see who you're giving to once names are drawn.", then the notes editor,
   labeled "Likes, sizes, no-gos. Everyone taking part can read them."
   After a redraw the line is "Names are being drawn again. You'll see who
   you're giving to once they are."
3. "Taking part (N)", the people.
4. "Leave this event", a small link at the very bottom.

There is no separate status block.

**Event page, after the draw** (`drawn`).
1. An unread question from the viewer's giver, when there is one, above
   everything: "Your giver asked you something", linking to the
   conversation.
2. The gift, alone until untied (§12 The reveal). The button is "Untie the
   bow". After a redraw, "Names were drawn again, so this may be someone
   new." sits under the date line, above the gift.
3. Once untied, one group: under "You're giving to", the name; their notes
   ("Notes updated since you last looked" when they changed, §5.2); the
   question box; "Done shopping" as a small control; "Hide the name".
4. "Questions from your giver", a quiet link when nothing is unread.
5. The viewer's own notes, collapsed to a preview with an edit control.
6. Everyone taking part, collapsed to a count that opens the list.
7. "Leave this event", a small link at the very bottom.

Every visit starts tied, so a phone left on the table gives nothing away.
When the assignment can't be read: heading "We can't show who you're giving
to", body "Names have been drawn, but yours can't be read any more. Ask an
organizer to draw names again."

**Event page, once the event is over** (`revealed` or `archived`).
1. The viewer's own pairing: who they gave to and who gave to them.
2. The full "Who drew whom" list ("Ana gave to Kofi"). Where pairings are
   lost: "Some pairings can't be read any more, so their giver isn't
   shown."
3. The old question threads and the viewer's notes, collapsed.

There is no separate people list; the pairing list names everyone. An event
archived straight from `drawn` stays sealed (§4): the viewer sees only who
they were giving to, and no list.

**Organizing an event** (`/events/{id}/manage`).
1. Head: a "Home" back link (an organizer who doesn't take part); the
   title; the switch (an organizer who takes part); date and budget; the
   row of steps (§12 Statuses).
2. The next step: one sentence on where the event stands and one button.
   - `draft`: "Only organizers can see this event. Open it so the people
     you've added can write their notes." / "Open the event"
   - `open`, ready: how many people are in and how many have written notes,
     then "Draw names whenever you're ready." / "Draw names"
   - `drawn`: how many have looked, then "Reveal who drew whom once the
     gifts are opened." / "Reveal who drew whom"
   - `revealed`: "Everyone can see who drew whom. Archive the event when
     you're done with it, and it moves to past events." / "Archive this
     event"
   - A blocker replaces both the sentence and the button: "Can't draw yet:
     at least N people need to take part." with "Add people"; "X wants to
     leave, and that needs a redraw. Drawing names again takes them out."
     with "Draw names again" and a small "Keep X in instead" link. The
     other reasons a draw is refused (§5.1) are stated in the same place,
     such as "The household X has more than half the people taking part, so
     they can't all draw someone outside it.", with "Change who can't draw
     whom", which opens that fold below and moves to it. "Add people" here
     likewise opens the "Add people" fold and moves to it; without the
     script both are links to the fold, which the page renders open while
     the blocker stands.
3. "Taking part (N)": one row per person. A row says in small words what is
   missing ("no notes yet", "hasn't looked yet", "wants to leave") or
   "done shopping"; a row with nothing to say stays quiet. With nobody:
   "Nobody's taking part yet." Then "Invited, not signed in yet (N)", with
   "Take back" per row, and "Add people", a fold row (§12 Controls). Once
   names are drawn: "Invitations by email wait until names are drawn
   again."
4. "Who can't draw whom" (households and extra rules) and "Details and
   organizers": closed rows with a summary on the right ("2 households, 1
   rule", "2 organizers") that open in place. The organizers hint reads
   "Organizers manage the event but don't see who drew whom. Being one
   doesn't make them a participant."
5. "Bigger changes", always open at the bottom. Each is a real button that
   opens its confirmation, with one small line on what it does:
   - "Someone is dropping out": "Only that one person's recipient changes,
     and nobody, you included, is told whose."
   - "Someone is joining late": "One person gives to someone new to make
     room. Nobody else changes."
   - "Draw names again": "Every assignment is deleted and the event goes
     back to open. Everyone is told names were drawn again." Its
     confirmation says in full: "Every assignment is deleted and the event
     goes back to open, so people and rules can change before you draw
     again. Everyone is told names were drawn again."
   - "Archive this event": "Pairings that haven't been revealed stay hidden
     for good."
   - "Cancel this event": "Everyone's assignments go, and any question-box
     messages are deleted. This can't be undone."

   Before the draw the group holds cancel alone. Once revealed, archiving is
   the next step and the group holds cancel and "Nothing to change now that
   everyone's been revealed."
6. Once revealed, the "Who drew whom" list takes the place of the people
   list.

Notices after an action: "The draw is undone. Draw again when you're
ready."; "Took X out. One person is now giving to someone new, and they'll
see a notice. Nobody else changed."; "Added X. One person is now giving to
someone new to make room, and they'll see a notice. Nobody else changed.";
"X is already taking part."

The three checkbox columns (notes, seen, done) are gone; the words on each
row replace them. What organizers may see is unchanged (§5.5).

**Questions for you** (`/events/{id}/questions`). Title "Questions for you",
small line "Your giver asks, you answer. Their name stays a secret." The
thread reads as a conversation, oldest at the top: a day line between days,
and under each message its time to the minute ("7:42 PM"; older messages
stored to the hour keep it, §6). The reply
is a one-line box, "Reply to your giver", with "Send" beside it; "Close
this conversation" is a small link under it. A question that hasn't been
answered carries the word "new". Empty: "No questions yet. When your giver
asks you something, it shows up here and you can reply." The rules of §6
are unchanged.

**New event.** Name, Exchange date, Budget, "Yes, I'm taking part too", and
"Same group as an earlier event" as one optional choice; the don't-repeat
choice shows only once an earlier event is picked. Then "Create event" and
a small line: "It starts as a draft only organizers can see. You add people
next." The form no longer adds people: picking an earlier event still
brings its people along (§4), and everyone else is added on the organizing
page.

**Sign-in chooser, signed out, account deleted.** A heading ("Sign in to
Pollyanna" on the chooser), the short about text, then the buttons:
"Continue with <label>" per provider, or "Sign in again". The about text
comes above the buttons, as the answer to "what is this?" for someone
arriving from an invitation.

**Past events.** A plain list. Each row is one link: the event's name; date
and budget; the viewer's part in it ("You took part · 9 people · pairings
revealed"). No status word. Empty: "No past events yet. Events move here
once an organizer archives them."

**Account.** Title "Your account", with name and email in the small line.
Three groups: "Ways to sign in"; "Emails", whose switch saves when it is
flipped, with no Save button (without the script a Save button shows, §15);
then "Sign out" and "Delete my account".

**Confirmations** (leaving, deleting the account). The question is the page
title ("Leave this event?", "Delete your account?"). What it costs follows
as plain text: "Your notes for this event are deleted."; once drawn, "Names
are drawn, so one other person will be giving to someone new." Then one
button that repeats the action's name and the way out as a link ("Stay
in", "Keep my account"). When deleting is blocked there is no action
button, only what to do first.

**Error pages.** One or two sentences and one way out.
- Not found: "We can't find that page" / "It may have moved, or the event
  may have been canceled."
- Server error: "Something went wrong" / "That one's on Pollyanna, not you.
  Nothing was changed. Try again in a minute."
- Way out: "Back to your events". Sign-in failure pages keep their words
  (§3) and offer the provider buttons again.

### Words

Decided on #60 (2026-11-27, user). The paper metaphor is gone from the
interface.

- "Hat", "slip" and every stamp, fold and paper word leave the interface.
  Operator-facing text (`--help`, startup logs) and the code keep the
  glossary's internal terms.
- "Draw names" and its family (draw, redraw, names drawn) stay.
- Being a participant is "taking part". Undoing a draw is "draw names
  again".
- "Slip" gets no replacement noun. The assignment is "who you're giving
  to", everywhere, paired with "your giver". "Recipient" is for
  organizer-side explanations.
- "Event" is the word for one event. "Gift exchange" only describes what
  Pollyanna is or the activity itself.
- Bow and gift words appear only where the bow or the gift is on screen.
- No all-caps labels.
- "Happening" is an internal word only (GLOSSARY.md) for the thing that
  happens in an event and makes notifications; it never appears in the
  interface. The menu's title is "What's new", never "Inbox" or
  "Notifications"; only the bell's accessible label counts them.
- Any string not listed in this section that says "hat", "slip" or
  "exchange" is reworded by these rules. The question box's words ("Your
  giver asks…", "Their name stays a secret", "Close this conversation",
  "Reopen it"), "Done shopping", "Leave this event", "Who can't draw whom",
  the household and rule labels, and the account, delete-account and
  sign-in failure pages are otherwise left as they are.

### About text

Says what Pollyanna is and nods to the name. Two versions:

- Long, on the empty home only: "Every family has a version of this: names
  on folded slips, a hat going round the table, someone squinting at the
  handwriting. Around Philadelphia they call it a Pollyanna. This is that,
  minus the squinting." Here the hat and slips describe the real tradition,
  so they stay.
- Short, on the sign-in chooser, `/auth/signed-out` and the account-deleted
  page: "Pollyanna is your family's gift exchange: everyone draws one name
  and nobody sees who drew whom. Around Philadelphia, that's what they call
  it."

Home with events and the failure pages carry neither.

### Accessibility

Text contrast at least 4.5:1 (3:1 for large text), tap targets at least
44 px, everything usable by keyboard, real form controls with labels, and
no reveal animation under "reduce motion" (§12).

## 8. Security notes

- Every management action checks that the user is an organizer of that
  event, and requires a CSRF token.
- Per-person rate limiting on message posting (§6).
- No request logging of paths that contain assignment or message IDs; access
  logs record method, route pattern and status only.
- Security headers: strict CSP (`default-src 'self'`), `X-Frame-Options: DENY`,
  `Referrer-Policy: no-referrer`. The live connection is a same-origin `GET`
  under `default-src 'self'`; being read-only, it needs no CSRF token.
- The draw key never appears in logs, error messages or the UI.
- Logs stay quiet when nothing is wrong. Successful `/healthz` requests are
  not logged. The live connection (§16) logs one line per connection, never
  one per keep-alive, frame or hint, and no line names a person, event or
  assignment. Each provider's OIDC callback is logged by its path
  (`/auth/callback` or `/auth/callback/<key>`). EF Core's
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
  through an open provider only with `email_verified: true` (a known login,
  an admitted one and an added one alike), and that linking by verified
  email adds a login to the right person.
- Tests for several providers: settings errors name their setting in full
  (an unlisted key, a misspelled field of a listed key, a repeated issuer),
  the chooser and each provider's callback, and the `email_verified` hint
  once per provider per run, worded for its admission.
- Tests for open-provider refusals: each reason (`no invitation or person`,
  `email not verified`, `ambiguous: N matches`) shows its page and stores no
  session, person or login, its log line names nobody, and the refused page
  can't be made to show an address by a crafted or altered URL.
- Tests for managing logins on `/account`: the list and its labels, adding a
  login (attached with no email match or admission, refused when another
  person holds it, unchanged when it's already the person's, dropped when
  the session changed hands), and removing any login but the last.
- Tests for the add-people lists: a picker lists and accepts only people the
  viewer shares an event with, never emails; a ticked ID outside the list is
  a 404; a typed address adds the one person who uses it, invites nobody's
  address, and stores nothing when several people use it.
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
  first (unchanged by §16); no email is ever queued for a giver because of their own
  assignment (docs/adr/0001); no email contains message bodies,
  notes or a recipient's name; and the outbox rules (retry, give up, drop
  on opt-out, account deletion, a cancelled or accepted invitation, or a
  lost role) hold.
- Tests for the HTML part of emails (§15, decided 2026-11-26, user). The
  outgoing mail carries the HTML next to the plain text, so the test mail
  relay sees both; building the message is a separate step with its own test
  (`multipart/alternative` with the plain text first, exactly the text, then
  `multipart/related` holding the HTML and the bow as an inline `image/png`
  whose `Content-ID` the HTML's `cid:` names; text parts UTF-8). The secrecy tests cover the HTML
  part too: the shuffled-pairings run requires it byte-identical, and the
  content checks (no message bodies, notes or giver's or recipient's names,
  no Santa or stockings) read the raw markup, hidden preview text and links
  included. Over every kind: each paragraph of the plain text appears in
  the HTML's text, and the preview text is the kind's text word for word;
  nothing remote (no script, frame, media or stylesheet link, no
  `background`, `url(` or `@import`, exactly one `<img>`, whose `src` is the
  `cid:` of the attached bow and whose `alt` is empty, no other `src`, and
  every link starts with the public URL); there is no status stamp; the
  button's label follows its link, whose URL matches the one printed under it and
  the plain text's; the markup carries `lang="en"`, every `<table>` has
  `role="presentation"`, and the preview text is `aria-hidden="true"`
  (contrast isn't tested; the palette fixes it). One test puts markup in an event's name and a person's
  name and requires it shown as typed, never as elements. No snapshot
  (golden-file) tests of the whole HTML.
- Tests for the "Tablecloth" identity (§7, §12; decided 2026-11-27, user),
  through the pages as a signed-in person sees them. Each state of the home
  row shows its one waiting line and links to where it is done, and no line
  names a recipient. The event page puts first what §7 puts first in each
  status (the notes editor; an unread question, then the tied gift; the
  viewer's own pairing). The organizing page shows one next step per
  status, each blocker in its place, people rows in words with no checkbox
  columns, and "Bigger changes" with the right buttons per status. The
  switch between "My page" and "Organizing" shows only for an organizer who
  takes part. The new-event form adds nobody and lands on the manage page.
  The masthead has no "Sign out"; the account page does. The about text is
  long on the empty home, short on the chooser and signed-out pages, and
  absent from home with events. No page a person can reach says "hat" or
  "slip" outside the long about text, or "exchanges" for events. The fonts,
  the favicon and the touch icon are served by the app, and no page loads
  anything from another origin. The pairing secrecy tests cover every
  reworked page. Layout, color and the reveal's motion are checked by hand
  against the mockups, on a phone, with and without "reduce motion", and
  without the script.
- Tests for in-app notifications and live pages (§16, decided on #92,
  November 27, 2026, user): who gets which live frame, the secrecy of
  frames, the menu and `/whats-new`, a live refetch moving nothing, the
  connection's lifecycle and quiet logs, and exact message times. §16
  **Testing** lists them, with what is checked by hand.
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

- Back end: .NET 11. Pollyanna follows the newest .NET release, LTS or STS,
  moving each November when a new one comes out (decided 2026-11-25, user).
  STS releases now get 24 months, so an STS release is supported as long as
  the LTS before it, so staying current costs no support time.
- Front end: server-rendered Razor Pages with a little plain JavaScript; no
  front-end build step. Pages work before scripts load where possible
  ("Untie the bow" can be a `<details>` element enhanced by script).
- Live updates (§16; decided on #83 and #91, November 27, 2026, user):
  server-sent events from `TypedResults.ServerSentEvents` in the shared
  framework, read with the browser's built-in `EventSource`. No package, no
  vendored client, no WebSockets and no proxy setting. SignalR was set
  aside (a vendored client, WebSocket forwarding at the proxy, scale-out
  Pollyanna doesn't need); polling is the fallback if server-sent events
  ever disappoint.
  - The stream flushes its headers at once (they otherwise wait for the
    first item), sends a comment every 15 seconds on the app's
    `TimeProvider` (a constant, not a setting), keeps
    `Content-Encoding: identity` and adds `X-Accel-Buffering: no`. With
    those, default Caddy, nginx, Traefik, Cloudflare and Cloudflare Tunnel
    setups carry it unchanged.
  - If response compression or a global request timeout is ever added,
    `/live` must be exempt.
  - Over HTTP/1.1 a browser holds at most six connections per host, so
    seven tabs would stall; HTTP/2 at the proxy is preferred (DEPLOYMENT.md
    §4).
  - The script that reads it stays small and plain: the server marks each
    live region with a stable id, and the script swaps regions by id.
- Data: SQLite, a single file, accessed through EF Core. Code avoids
  provider-specific SQL and features so another EF Core provider (e.g.
  PostgreSQL) can replace SQLite later; schema migrations are per provider
  and would be regenerated on a switch.
- Settings come from environment variables (documented in the deployment
  notes); the client secret can be read from a file. All are required except
  the optional provider settings and the SMTP settings; the app refuses to start and lists every problem if any
  is missing or invalid.

| Variable | Setting (§2) |
|---|---|
| `POLLYANNA_PUBLIC_URL` | Public URL: a bare origin, no path. Its scheme is taken as the browser's (the proxy terminates TLS); forwarded headers are not trusted. |
| `POLLYANNA_OIDC_ISSUER` | OIDC issuer; must be `https` unless the host is loopback (development). |
| `POLLYANNA_OIDC_CLIENT_ID` | Client ID. |
| `POLLYANNA_OIDC_CLIENT_SECRET` or `POLLYANNA_OIDC_CLIENT_SECRET_FILE` | Client secret, or a file holding it (exactly one of the two). |
| `POLLYANNA_OIDC_LABEL`, `POLLYANNA_OIDC_ADMISSION`, `POLLYANNA_OIDC_ICON_FILE` | Optional: the unnamed provider's button label (default: the issuer's host), admission (`trusted` by default, or `open`) and icon file. |
| `POLLYANNA_OIDC_PROVIDERS` | Optional: extra provider keys, separated by commas or spaces. With at least one, the unnamed set becomes optional. |
| `POLLYANNA_OIDC_<KEY>_ISSUER`, `_CLIENT_ID`, `_CLIENT_SECRET` or `_CLIENT_SECRET_FILE`, `_LABEL`, `_ADMISSION`, `_ICON_FILE` | Per listed key, all required except `_ICON_FILE`, under the same rules as the unnamed set; `_ADMISSION` has no default (§2). |
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
- The HTML part of an email (§15) is a Razor component rendered to a
  string with `HtmlRenderer`, which is in the shared framework (no package,
  no image growth) and HTML-encodes every value by default, so event and
  person names can't inject markup (decided 2026-11-26, user). Mjml.Net was
  set aside because its values must be encoded by hand. The component holds
  the one email-safe table layout, written by hand. Styles are inline
  `style` attributes written in the component, with no CSS inliner;
  a `<style>` block may carry only extras that can be lost without harm
  (dark mode, client-specific touches), since Gmail drops it in places.
  Each kind still composes the same plain subject, text and link; the
  layout takes those and the audience footer, the same inputs as the plain
  text, so no kind holds markup.

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
  - The settings error ends with every known provider's callback URL (when
    the public URL is valid) and "Run `/app/Pollyanna help` for every
    setting". The ready line reads `Pollyanna <version> is ready at <url>`,
    followed by one line per provider with its redirect URI (§2).
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

## 12. Visual design: "Tablecloth"

Decided on map #50 (tickets #51 to #64, 2026-11-26 and 27, user). It
replaces "Paper slips" entirely: no kraft, slips, stamps, tears, tilt or
typewriter face. The mockups are the prototypes on the throwaway
`prototype/*` branches those tickets name (never merged); each ticket says
which options on its page show the decided version. Whether it is built yet
is in §14.

- Concept: a table laid for the family. Each screen has one white panel
  holding the one thing that is yours to do, on a band of blue gingham, the
  tablecloth, with quiet rows on white beneath it. The gingham is the
  identity's one distinctive device and is not optional: without it the
  design read as a generic app. The reveal is a gift wrapped in the cloth
  and tied with the bow. The feel is homey and family. Nothing references
  the novel or the film, and nothing is tied to one holiday. The name's
  regional meaning ("a Pollyanna" is a gift exchange around Philadelphia)
  may be mentioned, as the about text does (§7).
- Color, six and no others:

  | Token | Hex | Use |
  |---|---|---|
  | White | `#FFFFFF` | Ground and panels, text on ink |
  | Ink | `#1E1A16` | Text, the filled button, field borders, the bow's knot |
  | Soft ink | `#5B5650` | Secondary text |
  | Ribbon blue | `#3F6E8C` | Bows, the cloth's thread, the focus ring, link underlines |
  | Pale blue | `#DCE7EE` | Row dividers, the email band, the girl's shirt, the other side's message bubbles |
  | Red | `#9E2019` | Error text, "Cancel this event" and "Delete my account" only |

  - Contrast: ink on white 17.29, soft ink on white 7.26, red on white
    7.85, ink on pale blue 13.76, soft ink on pale blue 5.78, ribbon blue on
    white 5.50, all passing AA for text. Ribbon blue on pale blue is 4.37:
    shapes only, never small text.
  - Red marks nothing else. "Leave this event" stays ink. Unread questions
    are shown by position and words, never a red dot.
  - Statuses get no color. No red-and-green pairing.
- Gingham: two CSS layers of ribbon blue at 50% opacity on white, 14px
  checks (a 28px repeat), so one thread reads `#9FB7C6` and a crossing
  `#6F92A9`. No image file.
  - It appears once per screen, as a full-width band under the one white
    panel; around the gift at the reveal; and behind the mark on the touch
    icon, the README banner and the social preview.
  - Never behind text, links or fields; never as a border, divider, button
    fill or page background; never in email.
  - Nothing but a white panel sits on the cloth, the marks included (the
    reveal's gift is the one exception, below).
  - Screens with the cloth: home, the event page while there is something
    to do on it, the organizing page while it has a next step, questions
    while one is unanswered, new event, the sign-in chooser, signed out and
    account deleted. Without: past events, account, the leave and
    delete-account confirmations, error and sign-in failure pages,
    questions with nothing new, and an event that is over.
- Type: Figtree for everything, at weights 400, 600 and 800. Playpen Sans
  500 for the revealed name only. Both are SIL OFL 1.1 and served by the
  app.
  - Scale: page title 29px/1.1 at 800; panel title 23px/1.15 at 800; action
    and countdown 18px/1.3 at 800; body 16px/1.5 at 400; links 600 and row
    labels 800 at 16px; small 14px/1.5; the revealed name 52px/1.1, down to
    35px for long names.
  - Titles are tightened by 0.02em. No italics, no all-caps labels, nothing
    below 14px.
- Space and shape: steps of 4, 8, 12, 16, 24, 32 and 56px. 16 is the page
  gutter, 24 the panel padding, 56 the cloth showing below a panel. One
  corner, 10px, on panels, buttons and fields. Nothing fully rounded, no
  shadows. Lines are 2px: ink around fields and outlined buttons, pale blue
  between rows. On wider screens the page is one 560px column; the masthead
  and the cloth run the full width, and the panel and everything under it
  stay in the column.
- Controls: one filled ink button per screen, for the one thing to do.
  Other actions are outlined buttons or plain links; the action that can't
  be undone has a red outline. Links are ink at weight 600 with a 2px
  ribbon-blue underline. Keyboard focus is a 3px ribbon-blue ring, offset
  2px. The empty home is the one screen with no filled button: its two
  halves are equal. Home with events is the one screen with several: each
  event's panel has its own waiting line, and that line is the panel's
  filled button (decided 2026-11-27, user).
  - A control that only opens something in place is a fold row, the same
    closed row with a chevron as "Who can't draw whom", never a button:
    "Add people", "Add a household", "Add a rule", "Edit details" and "Add
    an organizer" on the organizing page. The form it opens holds the one
    button, its submit. "Bigger changes" are the exception: each is a real
    button that opens its confirmation (§7). Every fold opens without the
    script (decided 2026-11-27, user). With several sign-in providers every provider button is
  outlined, each with its logo (§3); a single button is filled.
- Wordmark: the girl at 40px, then "pollyanna" in lowercase Figtree 800,
  tightened by 0.02em. There is no separate logo drawing. The product's
  name in running text stays "Pollyanna".
- Marks, kept as drawn:
  - Bow (the favicon, the touch icon, the reveal, the email): adapted from
    Microsoft Fluent Emoji "Ribbon" (Flat, MIT): ribbon-blue loops and
    tails, the inner folds in ink at 30%, an ink knot. At 16px the folds are
    left out; the favicon and any 16px use is the bow.
  - Girl (the masthead, the banner, error pages): seen from behind, two
    braids each tied with a small ribbon-blue bow, no face. Generated with
    Recraft by the project owner, who holds the rights to it; no credit is
    needed. Her gaps take the color of the ground she stands on; her shirt
    is pale blue on white and white on pale blue. Never shown below 32px.
    She appears large, about 130px, on error and sign-in failure pages and
    nowhere else in the app.
  - The gift box of "Paper slips" is retired.
  - The Fluent credit lives in the repo (`THIRD-PARTY-NOTICES.md`, plus a
    comment in `favicon.svg`), not on the site.
- The white edge: a mark or ribbon that sits on the cloth, or on a ground a
  mail client may darken, carries a white edge so it stays legible. Three
  places only: the reveal (3px, on the bow and both ribbons), the touch
  icon, and the email's bow (1.5px at 26px, §15).
- Statuses are states, shown as plain words. No tags, stamps or badges.
  - A participant sees a small line under the date: `open` "names not drawn
    yet"; `drawn` "names drawn"; `revealed` "everyone's been revealed";
    `archived` "over"; `draft` (organizers only) "draft, only organizers can
    see it". Past-event rows carry no status word.
  - The organizing page adds a row of steps, Draft, Open, Names drawn,
    Revealed, read as a trail (decided 2026-11-27, user): the steps in
    order, joined by chevrons drawn in the 2px line. Done steps are ink at
    600 with ribbon-blue chevrons after them; the current one is ink at 800
    over a 2px ribbon-blue line; later steps are soft ink at 400 with
    pale-blue chevrons. The row fits a 390px phone on one line.
- The reveal is the one orchestrated motion; nothing else on the page
  moves.
  - Tied: the whole gingham band is the gift, and no white panel shows. A
    ribbon-blue ribbon, 30px wide, runs edge to edge across the band and
    top to bottom, crossing under the bow, which is the kept mark at 150px.
    Bow and ribbons carry the 3px white edge. The one filled button, "Untie
    the bow", sits below the band on white; tapping the gift does the same.
  - Untying, the first time:

    | Beat | Starts | Lasts |
    |---|---|---|
    | Tails pull longer while the loops shrink into the knot | 0 ms | 350 ms |
    | The knot lets go; the bow drops away and fades | 330 ms | 290 ms |
    | Both ribbons slide off the cloth | 450 ms | 350 ms |
    | The cloth parts from the middle to both sides, showing the white panel | 750 ms | 500 ms |
    | The name is written left to right | 900 ms | 550 ms |
    | Notes, the question box, "Done shopping" and "Hide the name" open below | 1250 ms | 350 ms |

    Everything is settled by about 1.6 s.
  - Untied: the band stays as the cloth under one white panel holding the
    name (Playpen Sans, under the small line "You're giving to"), the
    recipient's notes, the question box, "Done shopping" and "Hide the
    name". No bow or ribbon remains on screen.
  - Later visits: the same sequence at 0.4× the length (about 0.65 s), with
    the name already there when the cloth parts, not written again. "First
    time" is the assignment's `viewed` flag (§5.2), which a redraw or a
    patch resets with the new assignment.
  - "Hide the name" ties it back at once, with no motion.
  - Reduce motion: no animation; the name and notes replace the gift at
    once.
- Questions read as a conversation (§7). The giver's messages sit on the
  left in pale blue with ink text, the viewer's own on the right in ink
  with white text. A bubble is at most 84% wide and has the 10px corner,
  except the bottom corner nearest its speaker, which is 2px: the one
  exception to the single corner. A centered day line sits between days
  ("Today", the weekday, or the date after a week) and the time under each
  message to the minute ("7:42 PM"; messages stored to the hour before map
  #82 show "about 7 PM"). While a question is unanswered the whole
  conversation and the reply box sit in the white panel on the cloth and
  "Send" is the filled button; with nothing new there is no cloth, the
  reply box sits in a bar under the conversation and "Send" is outlined.
- The bell (§7, §16; decided on #89, November 27, 2026, user) sits in the
  masthead left of the first name. With something new it is filled ribbon
  blue with an ink clapper; with nothing it is a soft-ink outline. No
  number, word, badge or dot beside it. (The bow was tried and ruled out:
  it reads as the brand, not as notifications.)
  - The "What's new" panel is the one thing in the app that sits over the
    page: white, just under the masthead, 8px in from each edge on a phone
    (in the 560px column on wider screens), a 2px ink border and the 10px
    corner, no shadow. Rows are divided by pale-blue lines; the event and
    time under each line are small soft ink. Each row's "×" is a 44px
    target.
  - Nothing in it moves: a new row takes its place without motion, and
    rows above the one in view don't push it down (scroll anchoring).
- Live changes on a page (§16) have no highlight flash or animation.
- Light only: no dark mode and no switch. Pages declare
  `color-scheme: light` so browsers don't auto-darken them. The email
  declares light only too, has no designed dark version, and must stay
  readable when a mail client darkens it anyway (§15).
- Outside the app:
  - Touch icon, 180px: the bow, with folds and the white edge, tied on
    gingham that fills the square. Its cloth has six checks across (30px
    checks), so it still reads as gingham at 60px: an exception to the 14px
    check.
  - README banner, 1280 × 320 (`docs/assets/banner.svg`): gingham edge to
    edge at 20px checks, with one centered white panel holding the girl and
    the lowercase wordmark, Figtree 800 converted to outlines. Light only;
    it fills its own rectangle on a dark page.
  - Social preview, 1280 × 640: the same composition, with no line of words
    in the image. A PNG kept in `docs/assets/`; the owner uploads it in the
    repository settings.
  - README screenshots: three phones in a row at the top (home, the event
    tied, the event untied); further down, organizing (wide), questions
    (phone) and a GIF of the untying. Phone shots are 390 CSS px wide and
    wide shots 640, both at 2×, captured from the built app with the demo
    people and the "Cousins' Swap" event.
- Avoid: gradients, pill buttons and badges, emoji, icons above headings,
  card grids, shadows, all-caps labels, unread counts or dots, a cream + serif + coral (or
  terracotta) look, Christmas red-and-green. The checklist on #51 is run on
  any new screen or mark.

## 13. Open items

None. Decisions made while building are recorded back into this spec.

## 14. Implementation status

Read this section first when picking the work up in a new session. All six
v1 milestones are done, and milestone 7 (email notifications) is
built. Branch, commit and PR rules are in CLAUDE.md. Questions about anything the spec
doesn't cover go to the user one per turn, and each answer is written back into
this spec.

### Next: in-app notifications and live pages (specified, not built)

Map #82, decided with the user on November 27, 2026 over #83 to #92. §16
describes it, with follow-ons in §1, §4, §5.2, §5.5, §6, §7, §8, §9, §11,
§12 and §15, docs/adr/0007 (superseding ADR-0004) and docs/adr/0008. The
research and the menu mockup are on throwaway branches named in those
tickets (`research/live-transport`, `research/live-connection-leaks`,
`research/live-proxy`, `prototype/notification-menu`; never merged).

- What changes: the masthead's bell and "What's new" panel, the
  `/whats-new` page, the `/live` stream, four pages that update while
  open, exact message times shown to the minute, one new yes/no column on
  the assignment row and one new table of stored notifications.
- What doesn't: who is emailed and when (§15), the question-email hour
  hold, the draw, who sees what, and every page without the script.
- Operator docs: DEPLOYMENT.md §4 gains a paragraph on `/live` (a
  long-lived stream that default setups pass through; keep read and idle
  timeouts above 15 seconds, don't buffer `text/event-stream`, prefer
  HTTP/2), the nginx example an `http2 on;` hint, and §9 an entry for
  "pages don't update by themselves". No new setting, and no change to
  `help`, README.md or the example Compose file.
- Built so far:
  - Exact message times (#96): a new message's `CreatedAt` is the app
    clock's time, no longer floored to the hour (`Message.HourOf` is gone;
    `Question.NextFullHour` keeps the email hold on its own). The pages
    carry `<time datetime>` to the minute (`Format.IsoMinute`) and say
    "7:42 PM UTC" without the script (`Format.Said`); `site.js` shows
    "7:42 PM" in the viewer's zone. A message stored exactly on the hour
    (`Note.OnTheHour`, so every one stored before this) is marked
    `data-said="hour"` and keeps "about 7 PM"; nothing rewrites it. The
    giver's card drops "Times are rounded to the hour." Tests:
    `QuestionTests` (exact times on both sides, older hour-stored
    messages), `QuestionConversationTests`; `Seed.MessageAsync` stores the
    clock's time or a given `at`.

### Done: the "Tablecloth" identity

Spec issue #65 (map #50), decided with the user on 2026-11-26 and 27 over
#51 to #64 and built as #67 to #80. §7 (what each screen shows, its words,
the about text), §12 and §15 (**Content**, **Nothing remote**, **Layout**,
**Opting out**) describe it, and §9 its tests. The "Done" entries below
describe what was built at the time, in the old "Paper slips" look and
words, and are left as written.

- What changed: every page's look and layout, the interface words (no
  "hat" or "slip"), the reveal's motion, the HTML email's layout with its
  one embedded bow, and the fonts and icons the app serves. What didn't:
  the page set and paths, the flows, who sees what, the draw, the question
  box's rules, who is emailed and when, and the emails' words apart from
  three "slip" phrases (§15 **Content**). No setting, no migration.
- Decided while writing the spec (2026-11-27, user): the placeholder words
  left by the mockups stand as drawn (the not-found and server-error pages,
  "Bigger changes" and the lines under its buttons, "Invited, not signed in
  yet", "Take back", "Keep X in instead", "Sign in to Pollyanna", "Reply to
  your giver"), and the README's line under the banner stays as it is.
- **Foundation (#67).** `Pages/Shared/_Tablecloth.cshtml` is every page's
  layout (`_ViewStart`): the one-row masthead (the girl, "pollyanna", an
  empty reserved slot and the first name linking to `/account`, no "Sign
  out"), then `<main>`; a page decides where the cloth goes. All styles
  are in `wwwroot/css/tablecloth.css`: the six colors and the gingham's
  thread as tokens, Figtree and Playpen Sans served from `wwwroot/fonts`
  (Fontsource 5.3.0), `.cloth > .panel`, `.column`, `.below`, outlined
  `.button` with `.button-fill` for the one thing to do and `.button-red`
  for the two destructive actions, `_PageHead`, the girl as
  `_TableclothGirl`, and the 180px touch icon from
  `docs/assets/touch-icon.svg`. The signed-out pages carry the short
  about text (`_AboutShort`); the chooser says "Continue with <label>".
- **Email (#68).** `EmailLayout.razor` is one white panel on a pale-blue
  band, with the bow PNG carried in the message as an inline `cid:` image
  (`EmailBow`, `InlineImage`); `SmtpMailSender.Build` adds
  `multipart/related`. No stamp.
- **Error and sign-in failure pages (#69):** the girl large, one or two
  sentences and one way out (`.lone`).
- **Home, empty home and past events (#70).** One panel per event, its
  waiting line (`EventManager.WaitingAsync`) the panel's filled button, so
  home is the one screen with several (§12 Controls);
  `Format.StatusLine` gives the status words used everywhere.
- **Account and delete confirmation (#71).** Three groups; the email switch
  saves on change (`site.js saveOnChange()`), with a Save button without
  the script.
- **Event page (#72, #76, #77).** `Event.cshtml` renders `_EventHead` (the
  switch "My page" / "Organizing" for an organizer who takes part,
  `_EventSwitch`; the countdown; date, budget and status) and then
  `_EventBeforeDraw`, `_EventDrawn` or `_EventOver`. Before the draw the
  notes editor comes first. Drawn, the gift (`section#gift`) unties with
  one class, `.gift.untied`, its beats timed by `--k` (`.gift-later`
  0.4); `/events/{id}/untie` shows it untied without the script; "reduce
  motion" turns the motion off. The page preloads Playpen Sans while it
  can write the name. Over, the viewer's own pairing first, then
  `_WhoDrewWhom`, then folds (`.fold`) for threads and notes.
- **Organizing (#73, #78).** One next step on the cloth per status, with
  its blocker and the way to fix it in place, including "Keep X in
  instead" (`EventManager.KeepInAsync`); the steps row; people rows in
  words; "Who can't draw whom" and "Details and organizers" as folds with
  a summary; "Bigger changes" at the bottom, each a button over one line.
- **New event (#74).** Name, date, budget, taking part and an optional
  earlier event; the form adds nobody else and lands on the organizing
  page.
- **Questions (#75).** Message bubbles (`ol.conversation`), "new" until a
  reply, on the cloth with a filled "Send" only while unanswered;
  `site.js conversations()` puts days and times in the viewer's zone.
- **"Paper slips" removed (#79).** `_Layout`, `site.css`, `_Girl`,
  `_Notes`/`NotesView`, `Format.Stamp`, Courier Prime and Reenie Beanie,
  `messageTimes()` and the new-event prefill hooks are deleted. The last
  "slip" strings were reworded: leaving after the draw says "One other
  person is now giving to someone new.", and asking without a readable
  assignment says "We can't find who you're giving to. Ask an organizer to
  draw names again." The delete-account page's "Hand over first" names the
  Organizing page instead of the manage page. THIRD-PARTY-NOTICES.md lists
  Figtree and Playpen Sans.
- **Tests.** `TableclothTests`, `HomeTests`, `EventPageTests`,
  `EventOverTests`, `OrganizingPageTests`, `OrganizingChangesTests`,
  `QuestionConversationTests`, `AccountPageTests`, `ErrorPagesTests`,
  `EventCreationTests`, `HtmlEmailTests`, and `OneLookTests`, which walks
  every page a person can reach, as participant and organizer in each
  status (leaving included), and finds no "hat" or "slip" outside the long
  about text, no "exchanges", one stylesheet and nothing from another
  origin; it also checks that the old stylesheet and fonts are gone and
  the favicon is served. `PairingSecrecyTests` renders every reworked page,
  the new-event form and the untied gift included.
- **Checked by hand:** one sample of the new email over a real relay to a
  real inbox (#64; the user found it good, clients not recorded). Each
  page was rendered in headless Chromium at 390px and 900px by its ticket.
  Not yet done: walking every screen on a real phone against the mockups,
  with and without "reduce motion" and the script, and the #51 checklist
  on states the mockups didn't cover.
- **Review fixes (2026-11-27).** The event's name now comes before the
  switch on the event and organizing pages; the steps row is a chevron
  trail; "Add people", "Add a household", "Add a rule", "Edit details" and
  "Add an organizer" are fold rows, not buttons, and a link to a fold
  (`a[data-opens]`, `site.js foldLinks()`) opens it; a draw refused over
  households or rules offers "Change who can't draw whom"; "Questions from
  your giver" shows before the first question too. `tablecloth.css` keeps
  to the §12 spacing steps, and `PageHeadView` takes one `BackLink`.
- **README, banner, screenshots and social preview (#80).**
  `docs/assets/banner.svg` is redrawn as §12 says (the girl from
  `_TableclothGirl` and the wordmark as Figtree 800 outlines on a 680 × 200
  panel, its 16px corner the 10px one at the README's 820px, over 20px
  gingham), and
  `docs/assets/social-preview.png` is the same at 1280 × 640 (an 880 × 280
  panel); the owner uploads it in the repository settings. Courier Prime
  left `THIRD-PARTY-NOTICES.md`, and the bow's credit no longer names the
  banner. The screenshots were taken with headless Chromium from a locally
  run app with the demo people (Mary Johnson organizing, James & John and
  Robert & Linda as households, `example.com` addresses), "Cousins' Swap"
  drawn and "Book Club Swap" open, after `document.fonts.ready`:
  `home.png`, `event-tied.png`, `event-untied.png` and
  `questions-phone.png` at 390 CSS px, `organizing.png` at 640 (cropped
  under the folds), all at 2×, and `untie.gif`, the first untying slowed
  ten times through the DevTools animation clock and replayed at its real
  timing. The old screenshots are gone. The README's alt texts describe the
  new look, and README, CONTRIBUTING.md and DEPLOYMENT.md no longer speak of
  slips or the manage page (the README's line under the banner and its
  "names in the hat" description of the tradition stay). Checked by hand:
  the rendered banner, social preview, screenshots and frames of the GIF;
  not the README as GitHub shows it in its light and dark themes.

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
  Base `mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled` (Ubuntu 26.04;
  no shell, no package manager), non-root uid 1654, plain HTTP on port 8080.
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
- 1.0.0 is the user's call, when they declare the app finished. The
  user called it on 2026-11-26: 1.0.0 is tagged from `main` after the
  branded HTML email (#30).

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

### Done: canceling an event

Decided with the user on November 26, 2026; the rules are in §4 (`canceled`),
§5.1, §7, §12 and §15.

- `EventStatus.Canceled`, stored by name like the others, so no migration.
  `EventLifecycle.Standing` lists every status but canceled; the other sets
  leave it out, and every transition refuses it ("This event is
  canceled."). `EventLifecycle.Cancel` goes from `Active` to canceled.
- Gone for everyone: `EventAccess.FindAsync` only finds standing events, so
  every event page is "not found", and `Membership.CanView` agrees. Home
  and `/past` already list only `Active` and `Archived`.
- `EventManager.CancelAsync` (`EventManager.Reveal.cs`) claims the status
  and deletes the event's threads, leave requests, invitations and outbox
  rows in one transaction. `OutboxSender` also drops any row for an event
  that isn't standing.
- Previous events: `PreviousCandidatesAsync` and `CreateAsync` skip
  canceled events; an existing link stays and the manage page's picker
  labels it "a canceled event". `NoRepeatPairsAsync` walks through a
  canceled event without counting it.
- Account deletion needed no change: canceled is neither `Active` nor
  `BeforeReveal`.
- Manage page: `_Cancel.cshtml` at the end of the Status section, with
  `.button-red` in `site.css`. Success goes home with the notice "<name> is
  canceled."
- Tests: `CancelTests`, a canceled column in `EventLifecycleTests`, and a
  canceled-in-the-chain case in `NoRepeatTests`.

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
  up the draw key, upgrading and backups, contributing and the license. The
  user dropped the planned "Why Pollyanna?" alternatives section. It links into
  DEPLOYMENT.md, which stays the full operator guide. Badges: CI, latest release and license only. No hosted
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

### Done: .NET 11

Decided with the user on 2026-11-25, released as 0.0.4:

- Framework-only: every project targets `net11.0`; the Microsoft packages and
  `dotnet-ef` move to 11.0.x; the other packages stay where they were. New .NET
  11 and C# features are left for later work. No `global.json`: the target
  framework already stops an older SDK, and CI picks its SDK with
  `setup-dotnet` (`11.0.x`).
- The image base moves to `aspnet:11.0-resolute-chiseled`, since .NET 11
  images ship on Ubuntu 26.04 and there's no `11.0-noble-chiseled`. Still
  shell-less, uid 1654, port 8080.
- The `System.Security.Cryptography.Pkcs` pin against the advisories in the
  10.0.0 that MailKit pulls in moved from the app to the test project: .NET 11's
  ASP.NET Core framework carries it, so the app no longer needs the pin (NuGet
  refuses it as prunable), but the test project still resolves 10.0.0.
- No database migration: the EF Core 11 model matches the last migration.
- Released right away rather than after the holidays, at the user's call.

### Done: several providers

Spec issue #17, built as #18 to #27 and decided with the user on 2026-11-25.
The rules are in §1, §2, §3, §4, §7 and §15; the terms **Login**, **Trusted
provider** and **Open provider** are in GLOSSARY.md. It's a feature, so it
gets a minor bump, and the release notes must say that the logins migration
has no way back. Existing `POLLYANNA_OIDC_*` settings keep working unchanged.

- **Logins (#18).** `Login` (person, issuer, `sub`, last email, created and
  last-used times; issuer plus `sub` unique) replaces `Person`'s issuer and
  `sub` columns. The `Logins` migration turns each living person's pair into
  one login; tombstones get none. "Has no logins" is now the tombstone
  marker, asked in one place (`PersonQueries.Living` in `Data/Entities.cs`),
  so a tombstone still keeps only its `id` (ADR-0005, with a dated note).
  Account deletion deletes every login. Sessions still record only the
  person.
- **Sign-in resolution.** `Auth/PersonSignIn.cs` takes the provider and the
  claims and returns one outcome: a known login signs in whatever the
  admission; a verified email matching exactly one living person adds a
  login (never replacing one); otherwise a trusted provider creates a person
  and an open provider admits only a verified email with a pending
  invitation, or refuses. A claim that isn't sent never clears a stored
  name, email or picture. `EmailVerifiedHint` logs once per provider per run.
- **Providers (#20).** `Auth/ProviderRegistry.cs` reads the settings and owns
  the providers (key, label, issuer, admission, callback path, icon) in
  configured order. `Auth/ProviderSchemes.cs` adds one OpenID Connect scheme
  per provider on first use: `oidc` with `/auth/callback` for the unnamed
  one, `oidc-<key>` with `/auth/callback/<key>` for a listed one. The ready
  line is followed by one line per provider, and the settings error lists
  every known redirect URI. `/auth/login` goes straight to a lone provider
  and otherwise renders the chooser, whose buttons go to
  `/auth/login/<key>`. Sign-in failure logs name the provider and its
  settings.
- **Open providers (#21).** The admission gate is in `PersonSignIn`;
  `Auth/RefusedSignIn.cs` carries the refusal (provider, reason, address,
  return URL) to `/auth/failed` as protected data that expires after 10
  minutes. Nothing is stored for a refused sign-in, and its log line names
  only the provider key and the reason.
- **Icons (#22).** `Auth/ProviderIcon.cs` checks an icon file at startup
  (readable, at most 1 MB, PNG, JPEG, GIF or WebP by its first bytes, or an
  SVG with no DTD), and `Auth/ProviderIconEndpoint.cs` serves it at
  `/auth/icon/<key>`. Every provider button is drawn by
  `Pages/Shared/_ProviderButton.cshtml`.
- **Ways to sign in (#23).** `/account` lists each login with its provider
  label (or "(no longer set up)", from `ProviderRegistry.LabelFor`), its last
  email and "Remove", then an "Add <label>" button per provider when there
  are several. `Auth/AddLogin.cs` carries the intent and the person through
  the challenge, and attaching skips email matching and admission.
  `PersonSignIn.AddLoginAsync` and `RemoveLoginAsync` hold the rules (the
  last login stays); the page only maps their results to notices.
- **Invitation email (#24).** With an open provider configured, the
  invitation email names every open provider and the invited address (§15).
- **Adding people (#19, #25, #26).** `EventManager.KnownTo`
  (`Events/EventManager.KnownPeople.cs`) is the one query for whom a picker
  lists and accepts: people the viewer shares any event with, names and
  pictures only. The add-people control (`Pages/Events/_AddPeople.cshtml`,
  fields in `AddPeopleControl`) is on the manage page and the new-event
  form; a ticked ID outside the list is a 404, and a typed address that
  matches several people stores nothing. The organizer and late-joiner
  pickers (`Pages/Shared/_PickOne.cshtml`, `Events/EventManager.Pickers.cs`)
  use the same list, and their address field only finds a person. The
  script in `wwwroot/js/site.js` only filters and adds the address row.
- **Operator docs (#27).** DEPLOYMENT.md has the provider settings, a Google
  walkthrough (public HTTPS host names only), the open-provider warning, the
  honest-`email_verified` rule, icon files and the cost of removing a
  provider. README.md says extra providers are optional, and `help` and
  `deploy/.env.example` show a listed provider.
- **Tests.** `TestApp.StartAsync(providers: …)` takes extra mock providers
  (`TestProvider`: key, label, admission, icon), each its own mock issuer;
  `SignInAsync(identity, provider: "<key>")` picks one, and
  `Seed.PersonAsync` takes logins. New classes: `SeveralProvidersTests`,
  `OpenProviderTests`, `ProviderIconTests`, `AccountLoginsTests`,
  `LoginMigrationTests` and `AddPeopleTests`; `ConfigurationTests`,
  `DeploymentTests`, `RelinkTests`, `PictureTests`, `ManageTests`,
  `EventCreationTests`, `AfterDrawTests` and the secrecy tests are extended.

### Done: branded HTML email

Spec issue #40 (map #30), built as #41 to #44 and decided with the user on
2026-11-26. The rules are in §9, §11 and §15 (**Channel**, **Nothing
remote**, **Layout**). Presentation only: who is emailed, when, and every
kind's words are unchanged, and there is no new setting.

- **Every email is multipart (#41).** `OutgoingMail` carries `Html` next to
  `Body`. `OutboxSender` renders both from the same inputs (the kind's
  `NotificationContent`, its audience, the event, the public URL):
  `Render` for the plain text, unchanged, and `MailHtml.RenderAsync` for
  the HTML. `SmtpMailSender.Build` is the pure step that makes the
  `multipart/alternative` message (plain text first, HTML second, both
  UTF-8); sending and its logging are unchanged. The footer is now
  structured lines (`AudienceRules.FooterLines`, `FooterLine`), joined
  into the same plain text as before.
- **Gift tag (#42).** `Notifications/EmailLayout.razor` is the one layout
  of §15 **Layout**, rendered by `HtmlRenderer` (no package). The stamp
  comes from `Format.Stamp`, the text is one paragraph per line of the
  plain text, and the button's label is chosen from the link.
- **Preview text (#43).** The layout opens with the kind's text and a run
  of spacer characters, both hidden and `aria-hidden`.
- **Tests.** `HtmlEmailTests` (the MIME step; every kind: same words,
  preview text, nothing remote, stamp, button and printed URL,
  accessibility markup; markup in event and person names shown as typed).
  `NotificationSecrecyTests` covers the HTML (byte-identical across
  shuffled pairings; content checks over the raw markup). The existing
  per-kind tests still read `Body` unchanged, which shows the plain text
  didn't change.
- **Docs (#44).** DEPLOYMENT.md says emails carry an HTML part and a
  plain-text part and load nothing remote.
- **Checked by hand (#44).** Every kind sent through Mailpit
  (CONTRIBUTING.md): both parts present in order, the plain text as
  before, and the HTML with the stamp, button, printed URL and footer tag;
  inbox snippets show the kind's text. Mailpit's compatibility report
  flags only gaps already expected: no stamp tilt or `<style>` in Gmail
  (§15 **Layout**), and Outlook for Windows ignoring the tag's width and
  some spacing, the plainest rendering, which #40 accepts. Then every kind
  went through a real relay to a real inbox, where the user checked them
  in real clients and found them good; in Gmail the inbox snippet shows
  the kind's text and stops at the spacers. Notes are on #44.

## 15. Email notifications

Built in milestone 7. Email tells people when something in an
event needs them, without weakening the sealed draw (§5.2).

**Channel.** Email with an HTML part and a plain-text part
(`multipart/alternative`, plain text first) over a mail relay the
operator configures (§2, §11). With no relay configured the feature is
off: nothing is queued, the `/account` switch is hidden, and the app
behaves as in v1. The in-app "What's new" menu (§16) is a separate channel
that works with or without email; nothing here changes because of it, and
no email is skipped because it was seen in the app.

**What is emailed.**

| Kind | To | When |
|---|---|---|
| Added to an event | the new participant | when added: by an organizer (also by inviting the address of someone who has signed in, and as a late joiner by patch or redraw), or by an invitation accepted at sign-in; for a `draft` event, not until it opens (the "open" email covers it). The wording follows the status when it is sent: "write your notes" while `open`, "see who you're giving to and write your notes" once drawn. It doesn't name who added them. |
| Invitation | an invited email address with no account yet | when the invitation is made, or when the event opens if it is still a `draft`. Names the organizer who invited ("Someone" if their account is gone). When at least one open provider is configured (§3), it adds one line naming every open provider by label, in configured order, and the invited address: "Sign in with Google using this address (sue@example.com).", or "Sign in with A or B …" ("A, B or C …") for several. Trusted providers are never named, and with only trusted providers the line is left out. Inviting the address again while its email still waits adds nothing. |
| Made an organizer | the new organizer | when made one, in any status, `draft` included; names who did it and links to the manage page |
| Event is open | every participant | `draft → open`: subject "<event> is open: write your notes" |
| Names are drawn | every participant | after a draw; after a redraw's new draw the wording is "Names were drawn again, take another look" (chosen when sent, from the event's redrawn flag, which stays set). The redraw itself (back to `open`) sends nothing and deletes the "Names are drawn" rows still waiting for the event. |
| Event is revealed | every participant and organizer | `drawn → revealed` |
| A question for you | the recipient | a giver posts in their thread; held to the next full hour (below) |
| Redraw needed | every organizer | a participant's request to leave needs a redraw (§4), from leaving or from deleting an account: "<Name> wants to leave <event>; a redraw is needed", plus a sentence pointing at the manage page, which it links to. One waiting row per organizer and leaver; dropped unsent once the request is no longer waiting (withdrawn, or cleared by a redraw or the reveal). |

**What is never emailed:** a recipient's reply to their giver, "your
recipient has changed" after a patch, removals (as participant or
organizer), archiving, canceling (which also drops every email still
waiting for the event), accepted invitations (to the organizer), and note
edits. Those stay in-app (the "What's new" menu and home's lines, §16) or
aren't shown at all.

**The rule behind it:** no email is ever addressed to a giver because of
their own assignment (a reply, a closed or reopened thread, a patch, a
recipient's note edit). Givers get only the emails sent to every
participant at once. A new kind that would reach one giver, or some
givers but not all, is out (docs/adr/0001).

- **Why replies aren't emailed.** The operator runs the relay and can read
  the database. Thread rows carry `recipient_id` in plaintext and exact
  message times (docs/adr/0007), so "your recipient replied" mailed to Bob
  just after Carol's reply was stored would show that Bob is Carol's giver,
  with no draw key needed. Even the mail log alone (for example at a third-party
  relay) would pair people off from the back and forth of question and
  reply emails. Emails to recipients are safe: the database already says
  whose thread it is, and the email names no giver. The same reasoning
  keeps "your recipient has changed" in-app only.
- **Questions are held to the hour.** A question email is sent at the top
  of the next full hour, and questions from the same thread within that
  hour make one email. Since message times are exact and shown at once
  (docs/adr/0007), the hold is batching only, not secrecy; it stays as it
  is and may be revisited in a later effort. "Dropped if read by then"
  now also fires when the recipient sees the question on a page first.
- **Not for your own actions.** Nobody is emailed about something they did
  themselves (an organizer adding themselves, the organizer who ran the
  draw).

**Content.** Each email says what happened, names the event and links to
the page (`<public URL>/events/{id}` and so on). It never contains a
message body, anyone's notes, or the name of a giver or recipient; draw
emails say "you can see who you're giving to", not who (the three "slip"
phrases of the draw and added-to-an-event emails were reworded on #60,
2026-11-27, user: "…and you can see who you're giving to", "Open it to see
who you're giving to and write your notes", "take another look at who
you're giving to in …"; no other email words changed). Organizer emails may name the
person concerned where the manage page already does (redraw needed). Who
acted may be named where it isn't secret ("Alice made you an organizer").
The footer says why the email came and links to `/account` to turn emails
off. An invitation's footer instead says to ignore the email if it wasn't
meant for the reader. Emails don't greet the reader by name. The text is
built when the email is sent, so it uses the event's name at that moment;
a named person whose account is gone appears as "Former participant".

**Nothing remote.** An email loads nothing from the server or anywhere
else: no remote images, no web fonts, no tracking pixels. Fetching one
would tell the server (and its access logs) roughly when someone opened an
email, and from where, which is the timing record ADR-0003 avoids. The
HTML part carries exactly one image, the bow beside the wordmark, and it
travels inside the message as a CID attachment (decided on #61 to #64,
2026-11-27, user, replacing the earlier "no images, not even embedded").
`data:` URIs and inline `<svg>` aren't used: Gmail and Outlook drop them.
Type is the system font stack (`-apple-system, "Segoe UI", Roboto,
Helvetica, Arial, sans-serif`), since Figtree can't be loaded.

**Layout.** All eight kinds share one HTML layout, "the panel on the band"
(decided on #61, 2026-11-27, user, replacing the gift tag of 2026-11-26).
From the top: the wordmark on white, which is the bow image and then
"pollyanna" in bold lowercase text; a pale-blue band holding one white
panel; the footer on white under the band. The panel holds, in order, the
event's name as the heading, the kind's text, a full-width ink button, and
the link's URL in small soft type under the button for clients where the
button fails. There is no status stamp: the text already says what
happened. The button's label follows where the link goes, not the kind, in
sentence case: "Open the event", "Open the manage page", "Read the
question". The footer, the invitation's included, is soft-ink text on
white, with the account page as a link. No text sits on the band: it is
the email's stand-in for the cloth, and the gingham itself never appears
in email (§12).
- Colors are the six of §12, all solid, and every surface sets its own
  background, so a client that darkens the email swaps whole surfaces and
  the text stays readable. The panel and the button have the 10px corner;
  where a client squares them nothing is lost. The `<style>` block holds
  only extras that can be lost without harm.
- The bow image: the bow as drawn (§12), folds kept, with a 1.5px white
  edge around the whole silhouette, the knot included, and everything
  outside it transparent. On white the edge can't be seen; on a darkened
  ground it outlines the bow (17.0:1 against `#1C1C1C`, where the loops
  alone are 3.1:1 and the knot 1.0:1). A 52 × 52px PNG shown at
  `width="26" height="26"`, 8px before the 22px wordmark, with `alt=""`:
  it is decoration, and the text wordmark carries the name, so a screen
  reader or a client that doesn't draw the image says "pollyanna" once.
  About 2.5 KB, kept with the app.
- The message is `multipart/alternative` holding `text/plain` first, then
  `multipart/related` holding `text/html` and the `image/png` part. The
  image part carries a `Content-ID` and
  `Content-Disposition: inline; filename="bow.png"`, and the HTML refers
  to it as `<img src="cid:…">`. This is the structure that was sent in the
  hand check (#64).
- The plain-text part is the kind's text and footer, as before. The HTML
  part has the same text and footer and adds only the wordmark and bow,
  the event-name heading, the button label and the URL.
- The HTML part opens with hidden preview text, the snippet inbox lists
  show after the subject: the kind's text, word for word, followed by a
  run of invisible spacer characters so clients don't carry on into the
  wordmark and heading (decided 2026-11-26, user). Nothing new is written
  for it. The plain-text part needs none, since it already starts with
  the same text.
- Emails declare `color-scheme: light only` (and the matching meta tags),
  as the pages do (§12). That keeps them light in Apple Mail and the Gmail
  phone apps. Clients that recolor a message whatever it declares (classic
  Outlook, new Outlook, Outlook.com) are accepted, and the email must stay
  readable there: explicit backgrounds on every surface, the white edge on
  the bow, and no text baked into an image.
- Accessibility (decided 2026-11-26, user). `lang="en" dir="ltr"` sits on
  `<html>` and again on the outer wrapper, since webmail such as Gmail
  strips `<html>`. `<title>` is the subject. Every table is a layout table
  with `role="presentation"`. The hidden preview text and its spacer run
  carry `aria-hidden="true"`, so screen readers read the kind's text once,
  in the body. Every piece of text meets WCAG AA contrast (4.5:1) against
  its own background (ink on white 17.3 for the wordmark, heading, text
  and footer link, and white on the ink button the same; soft ink on white
  7.3 for the small URL and the footer). The email-only softened ink
  `#5A5048` is gone; soft ink `#5B5650` replaces it. The pale-blue band
  holds no text and is decoration. There is no `role="article"` wrapper:
  the title and the event-name heading are the structure.

**Address.** The email from the person's last sign-in, unless the provider
sent `email_verified: false` for it (the same leniency as invitations,
§4). Someone with no email claim gets nothing. Invitation emails go to the
invited address.

**Opting out.** One switch on `/account`, "Email me about Pollyanna events",
on by default. There are no per-kind settings. Every page needs sign-in
(§3), so the footer link leads to `/account` rather than to a one-click
unsubscribe. It is a switch under the heading "Emails" that saves when it
is flipped, posting to `/account/emails` (decided on #59, 2026-11-27, user;
without the script it is a checkbox with a Save button, since every page
works without the script); the notice reads "Emails are on."
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
  queued (docs/adr/0003).
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


## 16. In-app notifications and live pages

Decided on map #82 (tickets #83 to #92, November 27, 2026, user). Every
signed-in page holds one live connection to `/live`. Through it, the
masthead's "What's new" menu (§7, §12) and four pages (home, the event
page, questions, organizing) change the moment something happens, without
weakening the sealed draw (§5.2) or the reasoning of ADR-0001. Email is
unchanged (§15). Whether it is built yet is in §14.

**Words.** The thing that happens in an event is a *happening* (an internal
word, GLOSSARY.md). One happening makes zero or more notifications and
live updates. A *notification* is now by email or in the app.

### Which happenings make a notification

Decided on #85. The seven account-holder email kinds, with the same people
and moments, plus accepted invitations and four giver-side kinds.
Invitations have no in-app form: the address has no account.

| Kind | To | Line | Goes to | Kept as |
|---|---|---|---|---|
| Added to an event | the new participant; for a `draft`, not until it opens | "You're taking part. Write your notes" (`open`) / "You're taking part. See who you're giving to" (`drawn`) | event page | stored |
| Made an organizer | the new organizer | "Alice made you an organizer" ("Someone" if their account is gone) | organizing page | stored |
| Event is open | every participant | "It's open: write your notes" | event page | stored |
| Names are drawn | every participant | "Names are drawn: see who you're giving to" / "Names were drawn again: take another look" | event page | stored |
| Event is revealed | participants and organizers | "See who drew whom" | event page (organizing page for an organizer who doesn't take part) | stored |
| Redraw needed | every organizer | "Sue wants to leave, and that needs a redraw" | organizing page | stored |
| Accepted invitation | every organizer | "Sue is taking part" | organizing page | stored |
| A question for you | the recipient | "Your giver asked you something" | questions page | worked out |
| Reply | the giver | "Your recipient replied" | event page (the gift tied) | worked out |
| Closed or reopened | the giver | "Your recipient closed the conversation" / "Your recipient reopened the conversation" | event page | worked out |
| Patched | the giver | "You're giving to someone new" | event page | worked out |
| Notes updated | the giver | "Your recipient updated their notes" | event page | worked out |

- Nobody gets a notification about their own action, as with email.
- One row per kind per event, with no counts (counts would show how much a
  pair talks). The words follow the event's current name and state when
  shown. Where a stored line names someone ("Sue"), it is the person of
  the latest happening of that kind; a person whose account is gone is
  "Former participant".
- Independent of email: it works with no mail relay and with the email
  switch off, and has no setting.
- Left out: being taken out of an event (as participant or organizer),
  canceling, archiving, other people's notes edits, "done shopping" and
  "has looked". "Ready to open" and "Ready to draw names" are states, not
  happenings, and stay as home's waiting lines only.
- The giver-side lines say "Your recipient", as home already does, though
  §7 otherwise keeps "recipient" for organizer-side text. They never name
  the recipient, and they link to the plain event page, so the gift opens
  tied.

### Stored and worked-out notifications

Decided on #86 (docs/adr/0008).

- **Worked out on each request:** the five kinds tied to an assignment
  come from the sealed row's markers, as home's waiting lines do. A
  question is unread giver messages past `recipient_read_upto`; a reply is
  recipient messages past `giver_read_upto`; notes updated is
  `notes_version_seen` behind the recipient's `notes_version`; patched is
  `recipient_changed`; closed or reopened is `thread_change_unseen` (§5.2),
  which closing or reopening sets. No stored row names a person next to an
  assignment.
- **Stored:** the other seven kinds, one row per person, event and kind,
  holding the kind, any name the line shows and the time it happened.
  These rows hold nothing that email doesn't already send that person. A
  new happening of the same kind replaces the row with new words and a new
  time, even if the old one was dismissed ("Names were drawn again").
- **Dismissing** is a deliberate post, from the panel's "×" or the
  `/whats-new` page's "Dismiss". It uses the same markers home and the
  thread use, so "seen" means one thing everywhere, and it clears home's
  matching waiting line:
  - a question moves `recipient_read_upto`; a reply moves `giver_read_upto`
  - notes updated moves `notes_version_seen`; patched clears
    `recipient_changed`; closed or reopened clears `thread_change_unseen`
  - a stored kind deletes its row
- **Seeing the thing clears it, by any route.** Opening the page a
  notification points to clears it, whether from the menu, home or a
  bookmark: the markers move as they do today (opening the card clears
  `thread_change_unseen` too), and a stored row is deleted inside that same
  page request, so no extra request shows in the proxy log.
- **No read state.** A notification is in the menu or gone. Opening the
  panel marks nothing, and the menu isn't a history.
- **Stored rows never expire by age.** A row is deleted, never flagged,
  when it's dismissed or its page is seen; all of an event's rows when it
  is canceled or archived; a person's rows for an event when they are
  taken out of it, or delete their account; and a row whose condition no
  longer holds (a redraw-needed row once the request is withdrawn or the
  redraw done).
- **Order.** The three kinds with no time come first: patched, notes
  updated, then closed or reopened. Everything else follows newest first:
  questions and replies by their newest unread message's exact time,
  stored rows by their own time. Nothing adds a time next to the sealed
  row.

### Who a live signal goes to

Decided on #90. Everything is instant: every happening reaches the people
it concerns the moment it happens. How it travels depends on what it
reveals.

| Happening | Goes to | Carried as |
|---|---|---|
| Public to the event: opened, names drawn or drawn again, revealed, archived, canceled, someone's notes edited, someone joining or leaving, details changed | every connected participant and organizer of the event | a hint naming the event and the area, never a person; the page refetches |
| For organizers only: has looked, done shopping, wants to leave, invitations taken up, people, organizers and rules changed | the event's connected organizers | a hint, then a refetch |
| Not about an assignment: made an organizer, added to an event, accepted invitation, redraw needed, taken out of an event | just the person it's for, or the organizers | a hint, then a refetch |
| Between a giver and a recipient: question, reply, closed or reopened, patched, "your recipient updated their notes" | just that person | the content itself (each affected region's HTML and the menu's rows), inside the connection, with no follow-up fetch |
| Your own action: a sent message, a dismissal, saved notes, "Done shopping" | your other tabs and devices | a hint, then a refetch |

- A hint followed by a fetch from only one giver would pair them in the
  proxy log, response size included. That's why giver-recipient
  happenings carry their content.
- One live address for every page, with no event or page in its path, so a
  log line says "online", not where. A keep-alive comment every 15
  seconds, so a visit is one log line, not one a minute.
- Connections are tracked by person and event only. The target of an
  assignment happening is worked out in memory when it's sent (as
  `SealedHat` does) and never stored or subscribed under an assignment.
- Left out: decoy refetches broadcast to the whole event, and releasing
  on a tick or the hour. Both give up instant feedback.

### What each page does live

Decided on #88.

| Page | Updates live |
|---|---|
| Home | each panel's waiting line; a new panel when you're added to an event or an invitation is accepted; a panel goes when its event is canceled or archived, or you're taken out; name, date, budget and status |
| Event page, `open` | "Taking part"; details; state changes |
| Event page, `drawn` | the giver's question box (replies, closed or reopened); the recipient's notes and "Notes updated since you last looked"; the "Your giver asked you something" line; patched to someone new; the people list; state changes |
| Event page, over | state changes only (archived, canceled) |
| Questions | new questions; replies, closing and reopening from your other tabs; the reveal (a state change: read-only, giver named) |
| Organizing | the next step's sentence and button (counts, blockers); each person's row words; invitations taken up; people and organizers changed by another organizer; the "Who can't draw whom" summaries |

Every other signed-in page holds the connection for the menu only. Not
live: the countdown turning over at midnight, and past events.

- **Small changes patch in place,** region by region: a new message, a
  counter, someone's notes, a waiting line, a row's words. Counters, row
  words, waiting lines and notes change silently.
- **State changes** (opened, drawn, drawn again, revealed, archived,
  canceled, you were taken out) change the page's shape:
  - With nothing on the page being edited, the page body is swapped.
  - With something being edited (a draft, an open confirmation, a form in
    flight), a quiet line appears in its place instead and the draft stays
    put: "Names have been drawn." / "Names have been drawn again." / "The
    event is open." / "Everyone's been revealed." / "This event was
    archived.", each with "Show", which loads the page.
  - Canceled and taken out show "This event was canceled" / "You're no
    longer in this event" with "Back to your events", since a reload would
    give the not-found page.
- **A live refetch isn't a visit.** It uses the page's own URL, marked
  live by a request header, with no new endpoints. The server treats it as
  a read-only view: it moves no read marker or `notes_version_seen`,
  clears no `recipient_changed` or `thread_change_unseen`, deletes no
  stored notification and sets no `viewed`. Only a real navigation or a
  deliberate post counts as seeing something.
- **Drafts are never touched.** A form with focus or unsaved text is left
  alone while the regions around it update. Notes edited in one tab while
  another tab saves them: the draft is kept silently and the last save
  wins, as today.
- **The gift keeps its tied or untied state,** held in the browser, since
  a refetch always comes back tied. Notes update in place under "You're
  giving to". Patched to someone new while the gift is open, it ties
  itself again and shows "You're giving to someone new", so an old name is
  never left on screen.
- **Open folds stay open** (the collapsed notes and people list, "Who
  can't draw whom", "Details and organizers"). An open confirmation from
  "Bigger changes" counts as editing; submitted after a state change, the
  server's existing refusal answers it.
- **While a post is in flight, hints are ignored.** Its response is a
  fresh page.
- **Threads** (the questions page and the giver's question box): a new
  message is added at the bottom with its time to the minute, and a
  question carries "new". At or near the bottom, the view stays there;
  scrolled up, a quiet "New message below" link appears instead. When the
  conversation is closed, the giver's box shows "Your recipient has closed
  this conversation" and Send is turned off, but a draft stays visible;
  reopening turns Send back on. Live arrival never moves a read marker
  (§6).
- **Screen readers and motion.** New messages, state-change lines and new
  notifications are announced politely through a live region. No
  highlight or animation; content inserted above the view doesn't push it
  down (scroll anchoring).
- **Catching up.** A hidden page closes its connection (on `pagehide` or
  when hidden) and reopens it when shown, then does one live refetch under
  the same rules. The same refetch follows a network drop and
  `EventSource`'s own reconnect. Nothing is replayed by the server.
- **The connection ends** on sign-out, account deletion and session
  expiry; nothing else happens on the page until the next click.
- **Without the script** pages are unchanged (§11), the bell is a link to
  `/whats-new`, and dismissing is a form post.

### Accepted limits

Written next to the draw key's limit (§5.2, ADR-0002):

- Someone who deliberately captures traffic at the proxy can match the
  moment a frame goes to Bob with Carol's post (#90).
- The proxy's access log (`POST /events/X/ask` at 14:07) plus the stored
  exact message time pairs a giver with their recipient from two passive
  sources, with no draw key (docs/adr/0007). The operator already holds the
  database, and the proxy log is theirs.
- An organizer with the organizing page open sees roughly when someone
  looked or marked done shopping (§5.5).

### Testing

Decided on #92. The live seam joins `docs/agents/testing.md`, which points
back here for the hand checks.

- **The real stream.** `TestBrowser.OpenLiveAsync()` opens `/live` through
  `TestServer` and reads frames with `SseParser`. `await
  app.Live.SettleAsync()` sends a marker frame down every open connection,
  and each reader collects everything up to it, so a test proves nothing
  else arrived without a timeout.
- **Who gets what:** one theory-style test per row of the routing table,
  driven by a per-kind data table so a new kind can't skip its row. The
  cast is a giver, the recipient, a bystander, an organizer, the actor's
  second tab and someone in another event. Public happenings give the
  whole event a hint naming no person; personal ones reach only their
  person; giver-recipient ones reach only that person, carrying the
  region's HTML; other events get nothing.
- **Secrecy:** `PairingSecrecyTests` and `NotificationSecrecyTests` run a
  fixed script of happenings with everyone else's pairings shuffled; each
  viewer's whole live frame sequence, masthead menu and `/whats-new` page
  must come out byte-identical. Beside them: no stored notification row is
  ever of the five worked-out kinds; no hint frame holds a person's id or
  name; a giver-recipient happening causes no request from anyone.
- **A live refetch isn't a visit:** for home, the event page, questions
  and organizing, a live `GetPageAsync` moves no read marker, sets no
  `viewed`, clears no flag and deletes no stored row, and a plain
  navigation to the same page then does.
- **Menu and "What's new":** the panel's rows are rendered by the server
  into every signed-in page's masthead, hidden until the bell opens them;
  the script only toggles and refreshes them, with no separate fragment
  endpoint. HTML tests cover the bell's state and label, the rows, their
  order, the empty state and `/whats-new`. Dismissing through the form
  removes the row, moves the marker or deletes the stored row, clears
  home's waiting line and hints the actor's other tabs.
- **Connection:** headers arrive before any event; `X-Accel-Buffering:
  no` is set; a keep-alive comment comes after 15 seconds of `app.Clock`; a
  missing session gets a status, not a redirect; the stream ends on
  sign-out, account deletion and session expiry; the log holds one line
  per connection (`QuietLogTests`).
- **Questions:** message times are stored exactly and shown to the minute
  (older hour-rounded messages unchanged).
- **Checked by hand,** with no browser test tooling (no Playwright, no
  JavaScript test runner):
  - a phone sleeping and waking, then catching up
  - a tab hidden and then shown
  - a network drop and reconnect
  - drafts, folds and the gift's tied state kept
  - "New message below"
  - screen-reader announcements
  - focus after a dismiss
  - the panel with "reduce motion", and `/whats-new` without the script
  - one run behind each of Caddy, nginx and Traefik, with the settings of
    DEPLOYMENT.md §4
