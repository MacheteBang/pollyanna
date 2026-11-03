# Pollyanna

A small self-hosted gift exchange for the family, for any occasion. Version 1 is complete:
see [SPEC.md](SPEC.md) for what it does and [DEPLOYMENT.md](DEPLOYMENT.md) to run it.
Formerly hatpick.

## Deployment

Pollyanna ships as a Docker image behind your own TLS-terminating reverse proxy and OpenID
Connect provider. [DEPLOYMENT.md](DEPLOYMENT.md) covers registering the OIDC client, every
setting, the example Compose file in `deploy/`, backups (the data and, separately, the draw
key), upgrades and the end-to-end check before first use.

## Development

Needs the .NET 10 SDK with ASP.NET Core.

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

Run the tests:

```sh
dotnet test
```

After changing the data model, add a migration:

```sh
dotnet tool restore
dotnet ef migrations add <Name> --project src/Pollyanna -o Data/Migrations
```

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

Fonts (Courier Prime, Reenie Beanie) are served from `src/Pollyanna/wwwroot/fonts` under the
SIL Open Font License; the licence texts sit beside them. The bow mark is adapted from
Microsoft Fluent Emoji (MIT); see `THIRD-PARTY-NOTICES.md`.
