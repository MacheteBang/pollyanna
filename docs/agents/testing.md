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

## Time

- `app.Clock` is a `FakeTimeProvider` that the app uses everywhere. Move it with `app.Clock.Advance(…)` or `SetUtcNow(…)`, for example to cross the hour boundary for question emails or to reach a retry's backoff.

## Settings and startup

- `TestApp.StartAsync(configure: b => b.UseSetting(…))` changes a setting.
- `pushedAuthorization: true` turns on the mock issuer's PAR endpoint.
- `ConfigurationTests` and `DeploymentTests` test the settings loader and the command-line commands without the web host.

## Secrecy

- `PairingSecrecyTests` and `NotificationSecrecyTests` render everything one viewer can reach twice, with everyone else's pairings shuffled, and require the output to be byte-identical.
- Extend them whenever a feature adds a page, a log line or an email that a participant can see.
