# Contributing to Pollyanna

Thanks for taking a look. Small fixes and improvements are welcome: a bug, a confusing
message, a gap in the docs. For anything larger, please open an issue first, so we can agree
on it before you spend time on code.

Pollyanna is deliberately small. It does one family gift exchange well and keeps the draw
sealed. Pull requests that add something [SPEC.md](SPEC.md) §1 lists as a non-goal, such as
wishlists, push notifications or accounts outside the identity provider, may be declined
even when they're well made.

## How Pollyanna is built

[SPEC.md](SPEC.md) is the source of truth for what Pollyanna does and how. Changes start
there: a new behaviour is written into the spec, then built, and decisions made along the
way are written back. §14 records what is done and why. `GLOSSARY.md` holds the domain
terms and `docs/adr/` the architecture decisions. `CODING_STANDARDS.md` lists the rules
reviews check.

Pollyanna is developed with AI coding agents. `CLAUDE.md` and `docs/agents/` are their
instructions: how to work from the spec, the issue tracker and its labels, and the agreed
test seams (`docs/agents/testing.md`). SPEC.md is the source of truth for humans and agents
alike, and the same rules apply to a pull request whoever wrote it.

Keep the repository free of deployment specifics: use generic examples such as
`pollyanna.example`, in code, docs, issues and pull requests alike.

## Try it locally

Needs the .NET 10 SDK with ASP.NET Core. No identity provider or mail server is needed: the
solution has a mock OpenID Connect provider for development.

Run the mock identity provider and the app in two terminals:

```sh
dotnet run --project src/Pollyanna.MockIssuer   # http://localhost:5021
dotnet run --project src/Pollyanna              # http://localhost:5010
```

Open http://localhost:5010. The mock provider shows a form where you choose the `sub`,
name, email and `email_verified` Pollyanna receives; change the `sub` but keep a verified
email to try re-linking. Sign in in a second browser profile as someone else to try
events from both sides: an organizer adds people on the manage page, and an email
invitation turns into a participant when someone signs in with that email. To try the
draw, open the event with at least 3 participants and no pending invitations, then press
"Draw names"; each participant unfolds their slip on the event page. Once names are drawn,
a giver asks their recipient a question from inside the slip, and the recipient answers on
"Questions from your giver" (`/events/{id}/questions`). An organizer then reveals
the event (everyone sees who drew whom, and threads turn read-only) and archives it, which
moves it to "Past events" (`/past`). Link a new event to the archived one to try the
no-repeat rule. `/account/delete` deletes your own account. Development settings
are in `src/Pollyanna/appsettings.Development.json`; the database and draw key go to
`src/Pollyanna/.dev/`.

To try email notifications, run [Mailpit](https://mailpit.axllent.org/) as a mail catcher
and point the app at it:

```sh
docker run -d --rm --name mailpit -p 1025:1025 -p 8025:8025 axllent/mailpit
POLLYANNA_SMTP_HOST=localhost POLLYANNA_SMTP_PORT=1025 POLLYANNA_SMTP_TLS=none \
  POLLYANNA_MAIL_FROM='Pollyanna <pollyanna@localhost>' dotnet run --project src/Pollyanna
```

Emails sent through the outbox (opening an event, for one) show at http://localhost:8025.
Sign in with an email in the mock provider's form, or nobody has an address to send to.

To try sign-in through pushed authorization requests (PAR), start the mock provider with
`dotnet run --project src/Pollyanna.MockIssuer -- --MockIssuer:PushedAuthorization=true`.

## Tests

Run them before opening a pull request; CI runs them too:

```sh
dotnet test
```

Most tests run the whole app against the mock provider and check the pages, the database,
the logs and the mail it sends. `docs/agents/testing.md` describes the seams. A change a
participant can see (a page, a log line or an email) extends the secrecy tests
(`PairingSecrecyTests`, `NotificationSecrecyTests`).

## Migrations

After changing the data model, add a migration:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Pollyanna -o Data/Migrations
```

CI checks that the migrations match the model.

## Layout

- `src/Pollyanna`: the app (Razor Pages, EF Core on SQLite).
  - `Auth/`, `People/`: sign-in, sessions, profile pictures.
  - `Events/`: event rules and management (who can see and change what), the draw
    algorithm (`Draw.cs`), everything after the draw (`EventManager.Draw.cs`), the
    anonymous question box (`EventManager.Questions.cs`, `PostingLimit.cs`), reveal and
    archive (`EventManager.Reveal.cs`) and account deletion (`EventManager.Accounts.cs`).
    `SealedHat.cs` is the only code that reads or writes assignments and their threads, and
    the only user of the draw key's giver tags (docs/adr/0002).
  - `Notifications/`: email notifications (spec §15): the outbox (`Outbox.cs`), the
    background sender (`OutboxSender.cs`), the MailKit relay (`MailSender.cs`) and one file
    per notification kind in `Kinds/`, holding all of that kind's rules.
  - `Configuration/`, `Infrastructure/`: settings and the draw key; startup checks,
    middleware, and the operator commands (`help`, `healthcheck`, `backup`).
  - `Pages/`: the screens; `Pages/Events/` holds new event, event page, manage, leave and
    questions; `Past` and `AccountDelete` sit beside the home and account pages.
  - `wwwroot/`: the stylesheet, fonts and the one small script (`js/site.js`); no build step.
- `src/Pollyanna.MockIssuer`: an OIDC provider for development and tests only; never shipped.
- `deploy/`: the example Compose file and `.env.example` (see DEPLOYMENT.md); the image itself
  is defined in `src/Pollyanna/Pollyanna.csproj`, and `.github/workflows/ci.yml` tests and publishes it.
- `tests/Pollyanna.Tests`: unit and integration tests, including the full OIDC flow against
  the mock issuer hosted in-process.
- `docs/`: the architecture decisions (`adr/`), the agents' instructions (`agents/`), and the
  README's banner (`assets/`) and screenshots (`screenshots/`).

Fonts (Courier Prime, Reenie Beanie) are served from `src/Pollyanna/wwwroot/fonts` under the
SIL Open Font License; the licence texts sit beside them. The bow mark is adapted from
Microsoft Fluent Emoji (MIT); see `THIRD-PARTY-NOTICES.md`.

## License

By contributing, you agree that your contributions are licensed under the project's
license, the [GNU Affero General Public License v3.0](LICENSE).
