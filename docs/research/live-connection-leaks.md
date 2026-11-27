# What leaks through a live connection

Research for #84, part of map #82 (real-time notifications). It lists what a live
connection (WebSocket or server-sent events) would reveal that today's request-and-response
pages don't, who learns it, how bad it is, and the mitigations. It decides nothing: the
options feed #86 (stored or derived notifications) and #87 (live question box vs the hour
rule).

Examples use Bob as Carol's giver, in one event, at `pollyanna.example`.

## Starting point: what today's pages already give away

These facts frame every leak below.

- **The server finds a giver only by computing their tag.** `SealedHat.FindAsync` reaches a
  giver's row by `GiverTag(event, person)` and a recipient's by plaintext `recipient_id`
  (`src/Pollyanna/Events/SealedHat.cs`). So when Carol replies, the server holds her
  assignment row but doesn't know who Bob is. To push to Bob it must compute the tag of each
  connected person and match it in memory, or have Bob's connection subscribed under
  something tied to that row.
- **The waiting lines are already derived, not stored.** `EventManager.SlipUpdatesAsync`
  (`Events/EventManager.Draw.cs`) builds "Your recipient has changed", "Your recipient updated
  their notes", "Your recipient replied" and "Your giver asked you something" on each request
  from `recipient_changed`, `notes_version_seen` against the recipient's `notes_version`, and
  the read markers `giver_read_upto` / `recipient_read_upto` compared with `seq`. None of these
  names the giver or holds a time (SPEC §5.2, ADR-0002).
- **Message times are rounded down to the hour** (`Message.HourOf`, SPEC §6), and question
  emails are held to the next full hour (ADR-0004). Replies and every other giver-directed
  change are never emailed (ADR-0001).
- **Pollyanna's own access log** records method, route pattern and status only
  (`Infrastructure/Middleware.cs`, SPEC §8), and ASP.NET Core's per-request hosting log is
  switched off (`Program.cs`, `Microsoft.AspNetCore.Hosting.Diagnostics` at `None`).
- **The operator's reverse proxy is outside that rule.** nginx's default is
  `access_log logs/access.log combined`, and `combined` logs `$remote_addr`, `$time_local`,
  the full request line and the user agent
  ([nginx log module](https://nginx.org/en/docs/http/ngx_http_log_module.html)). Traefik keeps
  all access-log fields and drops headers by default, and its default format has client
  address, start time, path and duration
  ([Traefik logs](https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/)).
  Caddy logs a site's requests once its `log` directive is present, with `ts`, `duration`,
  `request.uri` and `request.remote_ip`, and cookies redacted
  ([Caddy log directive](https://caddyserver.com/docs/caddyfile/directives/log),
  [Caddy logging](https://caddyserver.com/docs/logging)).
- **The routes carry the event and the action.** The giver asks with a POST to
  `/events/{id}/ask` (`Pages/Events/Event.cshtml`, route `/events/{id:guid}/{handler?}`), and
  the recipient's thread is `/events/{id}/questions`, with replies posted to
  `/events/{id}/questions/reply` (`Pages/Events/Questions.cshtml`).

**A gap that exists already (not new, but the yardstick):** an operator who keeps nginx's
default log *and* diffs the database can match "an IP posted `/events/X/ask` at 14:03" with
"Carol's thread got a giver message stored under 14:00" whenever only one question was asked
in that hour. That takes two sources and deliberate effort, in the same class as using the
draw key (ADR-0002's accepted limit). The question for live updates is which leaks get
*cheaper* than that: visible in one passive log, on someone's screen, or in a stored row.

## Leaks

Severity: **High** means it pairs a giver with a recipient from one passive source, with no
database and no key. **Medium** means it pairs with deliberate effort beyond today's, or tells
a participant something the spec deliberately withholds. **Low** means it reveals presence or
timing but not a pairing.

### 1. Push-then-fetch pairs people in the proxy log (operator) — High

If a live update tells only Bob "something changed, reload", Bob's browser then makes an
ordinary request. The proxy logs Carol's `POST /events/X/questions/reply` and, a fraction of a
second later, a `GET /events/X` from Bob's IP. Every exchange repeats the pattern, so a few
replies pair Bob with Carol from the access log alone. It is exactly the email pairing
ADR-0001 forbids, with the proxy log in place of the mail log and to the second instead of to
the hour.

The same happens in the other direction: Bob's `POST /events/X/ask` followed at once by
Carol's fetch of `/events/X/questions`. It also covers every other targeted change: a closed
or reopened thread, a patch ("You're giving to someone new" fetched only by the patched
giver, which shows who the dropout's giver was, the very thing §5.4 keeps from the
organizer), and a recipient's note edit if the push goes only to her giver.

A "mark as read" request sent automatically when an update arrives has the same effect: the
giver's `POST` lands right after the recipient's.

Mitigations:
- **Broadcast to everyone in the event.** Every connected participant gets the same signal and
  every page reloads, so the log shows the whole event fetching, not one person. Costs one
  request per connected participant per change; small for a family.
- **Carry the update inside the connection** (the rendered line or message in the frame) so
  there is no follow-up request to log. Frames inside an open WebSocket or event stream aren't
  logged by any of the proxies above; only the connection itself is (leak 2). Leaves leak 3.
- **Hold giver-directed updates** to a tick (the hour, or a shorter fixed tick for everyone)
  so the fetch no longer follows the action. See #87.
- **Don't push giver-directed changes at all**, mirroring ADR-0001 for email: a giver's page
  learns about a reply, close or patch on its next ordinary load, and only everyone-at-once
  changes are live.
- Mark as read on the next ordinary page load or a deliberate action, not on receipt.

### 2. The connection itself records presence (operator) — Low, Medium with page-specific paths

nginx writes a request's log line when processing ends, and `$request_time` is the "time
elapsed between the first bytes were read from the client and the log write after the last
bytes were sent" ([nginx log module](https://nginx.org/en/docs/http/ngx_http_log_module.html)).
For a WebSocket or event stream that is one line at disconnect giving its start and its
length: "this IP had this page open from 14:02 to 16:40". Caddy (`duration`) and Traefik
(duration, start time) record the same. Request-and-response logs today show only the moments
of each request.

The connection adds:
- **Time on page**, not just arrival times.
- **Which page**, if the connection's URL says so (`/events/X/questions/live`). A recipient's
  thread page open for an hour is a recipient waiting for her giver, which on its own pairs
  nothing (the database already says whose thread it is), but alongside leak 1 or 3 it narrows
  the field.
- **Reconnect churn.** nginx closes a proxied WebSocket "if the proxied server does not
  transmit any data within 60 seconds" ([nginx WebSocket proxying](https://nginx.org/en/docs/http/websocket.html)),
  and the HTML standard notes that "legacy proxy servers are known to … drop HTTP connections
  after a short timeout" and suggests a comment line "every 15 seconds or so"
  ([WHATWG server-sent events §9.2.7](https://html.spec.whatwg.org/multipage/server-sent-events.html)).
  Without a heartbeat, every reconnect is another log line, a presence timeline at one-minute
  resolution.
- Pollyanna's own access log writes the same single line at disconnect (route pattern only),
  and Docker stamps it with the time.

Mitigations:
- **One connection endpoint for every page** (`/live`, no event ID or page in the path); the
  page tells the server what it shows inside the stream. The log then says "online", not
  "online on the question page of event X".
- **A server heartbeat** shorter than the common idle timeouts, so there is one log line per
  visit instead of one a minute.
- The operator guide can suggest turning off access logging for that one path; that is
  operator documentation (map #82's "Not yet specified"), not something the app can enforce.
- Not a mitigation: the session cookie is redacted by Caddy and not in nginx's `combined`, so
  the log names an IP and user agent, not a person; but in a family deployment an IP or a
  device is usually a person.

### 3. Push timing on the wire (operator with a capture; any observer who sees both clients) — Medium

Even with the content inside the frame (leak 1's second mitigation), a frame goes to Bob's
connection milliseconds after Carol's request. The proxy terminates TLS, so the operator can
capture plaintext there; that is deliberate, like using the draw key. Outside the proxy, TLS
hides content but not timing or size: anyone who sees both Carol's and Bob's traffic (a
shared home network or VPN, an ISP for a household) can match Carol's upload to Bob's
download. A message-sized frame is also bigger than a "line changed" frame.

Mitigations:
- **Same frame to everyone in the event**, padded to a fixed size, so nothing singles out Bob.
  With content in the frame this means the frame can't hold Bob's private message; it can
  only say "the event changed", and then leak 1 returns unless everyone fetches.
- **Release on a fixed tick** for everyone (every minute, every 15 minutes, the hour), so a
  frame doesn't follow its cause.
- Accept it as the same class as the draw key: it needs deliberate capture, not a default log.

### 4. A live pairing table in server memory (operator) — Low

To push to Bob, the server either computes each connected person's tag when Carol posts
(in memory, discarded, like the request-time lookup it does today), or keeps Bob's connection
subscribed under Carol's assignment row or his tag for as long as his page is open. The second
is a giver↔recipient map in RAM for hours, visible to a memory dump, a debugger, or a
framework's debug logging of group names. SignalR's own docs warn that some data in
connection URLs ends up in logs ("Many web servers log the URL for each request, including the
query string") ([ASP.NET Core SignalR security](https://learn.microsoft.com/en-us/aspnet/core/signalr/security)).

Mitigations:
- Subscribe connections by **person and event only**, and work out the target at push time by
  computing tags in memory (`SealedHat` already does this for patches and the reveal).
- Keep the real-time framework's loggers at `Warning`, as `Program.cs` already does for
  hosting and routing; check what its `Information` and `Debug` lines contain before choosing
  one.
- Moot if updates are broadcast to the event (no per-assignment targeting at all).

### 5. The recipient learns when their giver was online (participants) — Medium, against ADR-0004

A question that appears on Carol's open page the minute Bob sends it gives back the exact
minute that the hour rounding of `created_at` and the held email hide (ADR-0004: "an instant
email would give the exact minute back"). In a family that is often enough: Carol sees the
question arrive while Bob is the one across the room putting his phone down. The stored
time stays rounded, but the screen isn't.

The reverse (Bob sees Carol's reply the minute she sends it) tells Bob when Carol was online.
Carol's identity isn't secret from Bob, so this is a privacy nicety, not a seal leak; but in
the same room, Carol replying and Bob's page changing at that moment tells Carol who her
giver is (leak 6).

A live "seen" or "typing" indicator would add the same timing; none exists today
(`QuestionThread` exposes only the viewer's own unread count), and the other side's read marker
should stay off-screen.

Mitigations (the #87 decision):
- Questions reach the recipient's page at the top of the next hour, like the email; the
  giver's own page updates at once (their own action leaks nothing).
- A shorter fixed tick (5 or 15 minutes, for everyone), accepting coarser blurring than the
  hour, which would mean revisiting ADR-0004 explicitly.
- Instant delivery, superseding ADR-0004 for the screen; the email hold then protects only the
  mail log.

### 6. What someone learns from their own screen (participants, organizers) — Low to Medium

- **Same-room timing.** Any targeted live change can be watched across a room: Carol replies,
  Bob glances at his updating page. Broadcasts and ticks blunt it; nothing removes it
  entirely while anything is instant.
- **Organizer counters.** The manage page shows how many have looked and who is done shopping,
  "bool, no timestamp" (§5.5). Updated live, the organizer sees the moment Bob untied the bow.
  That alone pairs nothing, but it is timing §5.5 withholds, and for an organizer who also
  takes part it adds to same-room guessing. Option: don't make those counters live, or tick
  them.
- **Broadcast content is already public.** Note edits, people joining or leaving and status
  changes are visible to the whole event, so broadcasting them leaks nothing new. A broadcast
  must never carry a per-person bit worked out from someone else's assignment (for example
  "someone's recipient replied" to all), or the count itself becomes the signal.

### 7. Stored notification or dismissal rows (operator, from the database alone) — High if keyed by person

A row such as `(person = Bob, event = X, kind = "recipient replied", created_at, dismissed)`
names the giver next to an event and a kind. With `recipient_id` plaintext on threads and
replies stored to the hour, a database read pairs Bob with Carol without the key: find the
thread with a recipient message in that hour. Even with no timestamp, "Bob has a reply
notification" pairs him whenever only one thread in the event has a reply, and counts across
rows narrow the rest. The same holds for "your recipient changed" and "your recipient updated
their notes". It contradicts ADR-0002's consequence that nothing stored about an assignment may
name the giver or record when they acted.

Deleting a row doesn't remove it from the file: SQLite's `secure_delete` "is normally off",
leaving deleted content in pages until overwritten or a `VACUUM`
([SQLite pragmas](https://www.sqlite.org/pragma.html)).

Mitigations (the #86 decision):
- **Derive assignment-related entries** each time, as `SlipUpdatesAsync` does now. "Dismiss"
  becomes advancing the markers that already exist and name no giver: `giver_read_upto` for a
  reply, `notes_version_seen` for a note change, clearing `recipient_changed` for a patch.
  The menu has no time to sort these by except the message hour already stored.
- **Store only kinds that are everyone-at-once or not about an assignment** (event open, names
  drawn, revealed, made an organizer, added to an event): the same kinds email already sends
  per person (§15), so a row adds nothing the outbox doesn't already hold briefly.
- **A sealed dismissal table**, keyed by a giver tag like `assignments.giver_tag`, with random
  IDs and no times. Possible, but it duplicates the existing markers and every new kind needs
  the same care; row insertion moments are still visible to someone watching the database live.
- Avoid a per-person "unread count" or "last seen" column; it is a timestamp or a counter of
  assignment events by another name (cf. ADR-0003's ban on a "last emailed" column).

## Summary for the grilling tickets

| # | Leak | Who | Severity | Main options |
|---|---|---|---|---|
| 1 | Targeted push, then a logged fetch or mark-read | operator, passive proxy log | High | broadcast to the event; content in the frame; hold to a tick; don't push giver-directed changes |
| 2 | Connection open and close times, page in path, reconnect churn | operator, passive proxy log | Low (Medium with page paths) | one `/live` path; heartbeat; operator docs |
| 3 | Frame timing and size on the wire | operator with a capture; shared-network observer | Medium | same padded frame to all; fixed tick; accept as key-class |
| 4 | Connection subscribed per assignment in memory | operator, deliberate | Low | subscribe by person and event; compute tags at push time |
| 5 | Recipient sees the minute a question arrives | recipient | Medium, against ADR-0004 | hour (or tick) for the recipient's view; or supersede ADR-0004 |
| 6 | Same-room timing; live organizer counters | participants, organizers | Low to Medium | broadcasts and ticks; organizer counters not live |
| 7 | Notification or dismissal row naming a person | operator, database alone | High | derive assignment kinds and dismiss via existing markers; store only everyone-at-once kinds |

For **#86 (stored or derived):** leak 7 rules out per-person rows for anything tied to an
assignment unless they are sealed like `giver_tag`; deriving from the existing markers is the
one option that adds no new stored state. Non-assignment kinds can be stored either way.

For **#87 (live question box vs the hour rule):** leaks 1, 3 and 5 all come from a
giver-directed or recipient-directed update that follows its cause at once. The choices are
instant (supersede ADR-0004 and accept leak 5, while still avoiding leak 1 with content in the
frame or a broadcast), a fixed tick shorter than the hour, or the hour for both the screen and
the email. Each side can be decided separately: the giver's own sent question can appear at
once in any option.

## Sources

- SPEC.md §1, §5.2, §5.4, §5.5, §6, §7, §8, §15; GLOSSARY.md; docs/adr/0001 to 0004.
- Code: `src/Pollyanna/Events/SealedHat.cs`, `Events/EventManager.Questions.cs`,
  `Events/EventManager.Draw.cs` (`SlipUpdatesAsync`), `Pages/Index.cshtml.cs`,
  `Pages/Events/Event.cshtml`, `Pages/Events/Questions.cshtml`,
  `Infrastructure/Middleware.cs`, `Data/Entities.cs`, `Program.cs`.
- nginx, `ngx_http_log_module`: https://nginx.org/en/docs/http/ngx_http_log_module.html
- nginx, WebSocket proxying: https://nginx.org/en/docs/http/websocket.html
- Caddy, `log` directive: https://caddyserver.com/docs/caddyfile/directives/log; logging:
  https://caddyserver.com/docs/logging
- Traefik, logs and access logs:
  https://doc.traefik.io/traefik/reference/install-configuration/observability/logs-and-accesslogs/
- WHATWG HTML, server-sent events: https://html.spec.whatwg.org/multipage/server-sent-events.html
- Microsoft, ASP.NET Core SignalR security:
  https://learn.microsoft.com/en-us/aspnet/core/signalr/security
- SQLite, `PRAGMA secure_delete`: https://www.sqlite.org/pragma.html
