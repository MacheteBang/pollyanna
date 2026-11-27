# Testing: the seams

These are Pollyanna's agreed test seams. When the `tdd` skill asks to confirm seams with the user, these are the answer. Raise a new seam with the user only when none of these reaches the behaviour.

Tests are integration tests against the real app through `tests/Pollyanna.Tests/Support/`. Domain logic that has no database, such as `Events/Draw.cs`, is tested directly.

## In: requests through the pages

- `await using var app = await TestApp.StartAsync()` runs the whole app with a mock OIDC issuer and its own data directory.
- `app.SignInAsync(identity)` (in `Seed`) signs a person in through the real OIDC flow and returns a `TestBrowser` and the person's id.
- `TestBrowser` drives the pages:
  - `GetPageAsync` parses a page with AngleSharp.
  - `SubmitAsync` and `SubmitOnAsync` post a page's own form, so they carry its CSRF token.
  - `PostWithTokenAsync` posts to an action without submitting a form.
  - Assert on the parsed HTML, not on view models.

## Setup: seeding

- `Seed` builds state that would take many page steps: `PersonAsync`, `EventAsync`, `SealAsync` with `Loop`, `MessageAsync`, `HouseholdAsync`, `ExclusionAsync`.
- Use `app.WithDbAsync(db => …)` for anything else, and to read state back that the pages don't show.

## Out: what to observe

- **Pages:** the HTML the browser gets back, status codes, and redirects (refusals are 404).
- **Database:** `WithDbAsync`, for state the pages hide, such as outbox rows and assignment tags.
- **Logs:** `app.Logs.Entries` holds the category, message and level of each log line. Secrecy tests assert what logs must not contain.
- **Mail:** start with `TestApp.StartAsync(mail: true)`. The test decides when mail goes out by calling `await app.SendMailAsync()`. Then read `app.Mail.Sent`, or `app.Mail.To(address)`.
  - `app.Mail.Failure` makes the relay fail, to test retries.
  - `app.Mail.DuringNextSend` runs code while an email is being sent.
  - The background sender doesn't run in tests (`OutboxSenderOptions.SendInBackground` is off). Only the test that covers the background sender itself turns it back on.

- **Happenings:** `app.Happenings.Heard` holds every happening announced, in order, with its kind, event, actor and who it's about (spec §16). `app.Happenings.Clear()` forgets the setup's.

## Live: the real stream

- `await browser.OpenLiveAsync()` opens `/live` as one tab of that browser would and returns a `LiveReader` once the stream's opening frame is in. Call it twice for two tabs. `reader.Tab` is that connection's id, which a post names in the `Pollyanna-Tab` header to be left out of "your other tabs".
- `await app.Live.SettleAsync()` sends a marker down every open connection and waits until each reader has it. After it, `reader.Frames` (type and data) and `reader.Hints` (event and area) hold everything that arrived, so a test proves nothing else did, without a timeout. `reader.Clear()` forgets the setup's.
- A giver-recipient happening reaches its one person as a `content` frame: `reader.Contents` holds each one's event, area and pages (home, the event page and the questions page, by path, as that person would see them). `reader.Script(eventId)` turns every frame into a string with the event's ID and the times taken out, for the secrecy runs to compare.
- `reader.EndedAsync()` waits for the server to end the stream (sign-out, account deletion, expiry).
- The keep-alive is a comment, which `SseParser` skips: read the raw lines to see it, and move `app.Clock` 15 seconds.
- Browser behavior (sleep and wake, hidden tabs, reconnects, drafts, focus, screen readers, the proxies) has no test tooling. It is checked by hand from the list in SPEC.md §16, "Testing".

## Time

- `app.Clock` is a `FakeTimeProvider` that the app uses everywhere. Move it with `app.Clock.Advance(…)` or `SetUtcNow(…)`, for example to cross the hour boundary for question emails or to reach a retry's backoff.

## Settings and startup

- `TestApp.StartAsync(configure: b => b.UseSetting(…))` changes a setting.
- `pushedAuthorization: true` turns on the mock issuer's PAR endpoint.
- `ConfigurationTests` and `DeploymentTests` test the settings loader and the command-line commands without the web host.

## Secrecy

- `PairingSecrecyTests` and `NotificationSecrecyTests` render everything one viewer can reach twice, with everyone else's pairings shuffled, and require the output to be byte-identical.
- Extend them whenever a feature adds a page, a log line or an email that a participant can see.
