# Where the auth code assumes a single provider

Research for [#7](https://github.com/MacheteBang/pollyanna/issues/7), part of the map in
[#6](https://github.com/MacheteBang/pollyanna/issues/6) (several OIDC providers side by side).
Facts only: what the code does today, with file:line references against `main` at `b459410`
(November 25, 2026). No design recommendations.

## Summary

- **Configuration** has exactly one issuer, one client ID and one client secret
  (`POLLYANNA_OIDC_ISSUER`, `_CLIENT_ID`, `_CLIENT_SECRET[_FILE]`), all required, with one fixed
  callback path `/auth/callback`.
- **Program.cs** registers one OpenID Connect scheme, named `"oidc"`, bound to those settings.
  `/auth/login` always challenges that one scheme; there is no provider choice anywhere in the UI.
- **The data model** already stores issuer + `sub` per person (unique together), so two issuers
  can coexist in the table. But a person has exactly **one** issuer/`sub` pair: re-linking
  *overwrites* it, so a person can't hold two logins at once.
- **Admission** has no gate: any sign-in that the configured provider authenticates creates a
  person (§3, "limiting who can sign in is the provider's job").
- **Re-linking** and **invitation acceptance** both match on email, and trust whatever
  `email_verified` the (single) provider sends. Nothing records which provider vouched for the email.
- **Tests** go through one in-process mock issuer (`https://issuer.test`) with one client; helpers
  and several tests look people up by `TestApp.Issuer`, and two tests configure the `"oidc"`
  scheme's options by name.

## 1. Settings and validation (`src/Pollyanna/Configuration/PollyannaSettings.cs`)

| # | Assumption | Where |
|---|---|---|
| S1 | One fixed set of setting names, no per-provider prefix or list: `POLLYANNA_OIDC_ISSUER`, `POLLYANNA_OIDC_CLIENT_ID`, `POLLYANNA_OIDC_CLIENT_SECRET`, `POLLYANNA_OIDC_CLIENT_SECRET_FILE`. | `PollyannaSettings.cs:13-16` |
| S2 | The settings object holds a single `Issuer`, `ClientId` and `ClientSecret` (scalar `required` properties). | `PollyannaSettings.cs:29-31` |
| S3 | All three are mandatory: a missing issuer or client ID is a startup error (`Required`), and so is a missing secret. So there is no configuration without the operator's provider, and no way to add a second. | `PollyannaSettings.cs:64, 75, 77-80` |
| S4 | Issuer validation (absolute URL, `https` unless loopback, trailing `/` trimmed) runs on the one issuer. | `PollyannaSettings.cs:64-73` |
| S5 | `IssuerIsLoopback` is a single flag derived from the one issuer; Program.cs uses it for `RequireHttpsMetadata`. | `PollyannaSettings.cs:44`; `Program.cs:78` |
| S6 | One callback path, a constant `/auth/callback`, and one `CallbackUrl`. | `PollyannaSettings.cs:37, 39` |
| S7 | The settings error report names "The OIDC redirect URI to register with the provider" (singular); `CallbackUrlFrom` returns one URL. | `PollyannaSettings.cs:191-194, 244-245` |
| S8 | `ToString()` prints one issuer and client ID (and keeps the secret out). | `PollyannaSettings.cs:212` |
| S9 | The same single-provider settings are documented in `help`, under "SETTINGS" and "REGISTERING THE OIDC CLIENT" (including "Limiting who may sign in is the provider's job; Pollyanna checks no groups") and "EMAIL CLAIMS". | `src/Pollyanna/Infrastructure/Help.cs:30-34, 75-90` |
| S10 | The startup log line reports one redirect URI. | `src/Pollyanna/Infrastructure/StartupTasks.cs:26-27` |
| S11 | Operator-facing copies of the same settings: `deploy/.env.example:12-18`, `src/Pollyanna/appsettings.Development.json:9-11` (points at the mock issuer on localhost), DEPLOYMENT.md (settings table, around lines 123-125, and troubleshooting around 352-361), SPEC.md §2 table (line 64) and §11 table (lines 643-645). | as listed |

## 2. Authentication wiring (`src/Pollyanna/Program.cs`)

| # | Assumption | Where |
|---|---|---|
| P1 | One OIDC scheme name, a local constant `Oidc = "oidc"`. | `Program.cs:71` |
| P2 | `AddOpenIdConnect` is called once; the session scheme is the default for everything (authenticate, challenge, sign-in). | `Program.cs:72-74` |
| P3 | The options for that one scheme are bound to the one `Issuer` / `ClientId` / `ClientSecret`. | `Program.cs:75-80` |
| P4 | Protocol details are hard-coded for that scheme: code flow, `response_mode=query`, PKCE, scopes `openid profile email`, `GetClaimsFromUserInfoEndpoint = true`, `MapInboundClaims = false`, claim actions for `email_verified` and `picture`. | `Program.cs:81-101` |
| P5 | `CallbackPath` is the single `/auth/callback`, and `OnRedirectToIdentityProvider` overwrites `redirect_uri` with the one public `CallbackUrl`. | `Program.cs:88, 103-109` |
| P6 | `OnTicketReceived` turns the provider principal into a `ProviderIdentity` and calls `PersonSignIn.SignInAsync` with no notion of which configured provider it came from (the issuer comes from the `sub` claim's `Issuer`, see A1). The resulting principal's authentication type is the constant `"oidc"`. | `Program.cs:110-117` |
| P7 | `OnRemoteFailure` sends every remote failure to `SignInFailure.RemoteFailed`, which has no provider argument. | `Program.cs:118-124` |
| P8 | `EmailVerifiedHint` is a process-wide singleton (one note per process run, not per provider). | `Program.cs:66`; see A8 |
| P9 | Framework fact relevant to sizing: in ASP.NET Core each remote handler checks whether the request path equals its own `CallbackPath`, and the authentication middleware lets the first scheme that claims the request handle it. With one shared `/auth/callback`, only one OIDC scheme can receive callbacks. Today there is only one, so this is not yet a problem. | `Program.cs:88`; ASP.NET Core `RemoteAuthenticationHandler.ShouldHandleRequestAsync` and `AuthenticationMiddleware` |
| P10 | The access log special-cases the one callback path, because the OIDC handler answers it before routing. | `src/Pollyanna/Infrastructure/Middleware.cs:56-57` |

## 3. Starting sign-in and the pages (`src/Pollyanna/Pages/Auth/`)

| # | Assumption | Where |
|---|---|---|
| L1 | `/auth/login` challenges the literal scheme `"oidc"` (a string literal, not the Program.cs constant). There is no parameter for choosing a provider. | `Pages/Auth/Login.cshtml.cs:22` |
| L2 | A start failure is reported against `settings.Issuer`, the one issuer. | `Pages/Auth/Login.cshtml.cs:10, 27` |
| L3 | `Login.cshtml` has no markup: the page always redirects straight to the provider, so there is no provider-picker page today. | `Pages/Auth/Login.cshtml:1-2` |
| L4 | The session handler's challenge always redirects to `/auth/login?returnUrl=...`. | `Auth/SessionAuthenticationHandler.cs:67-75` |
| L5 | `/auth/failed` speaks of "Your sign-in provider" and "The sign-in service" (singular), and "Try again" links back to `/auth/login`. | `Pages/Auth/Failed.cshtml:9, 12, 16` |
| L6 | Sign-out ends only the Pollyanna session (no provider sign-out), so it doesn't depend on which provider was used. | `Pages/Auth/SignOut.cshtml.cs:10-19` |

## 4. Failure logging (`src/Pollyanna/Auth/SignInFailure.cs`)

| # | Assumption | Where |
|---|---|---|
| F1 | The remedy text names the single setting names: "Check POLLYANNA_OIDC_CLIENT_ID, the client secret and the client registration." | `SignInFailure.cs:17` |
| F2 | `StartFailed(logger, e, issuer)` takes one issuer string and tells the operator to "Check POLLYANNA_OIDC_ISSUER". | `SignInFailure.cs:23-40` (text at 35-36) |
| F3 | `RemoteFailed(logger, e)` has no provider parameter at all; its log lines can't say which provider failed. | `SignInFailure.cs:43-53` |
| F4 | One logger category, `Pollyanna.Auth`, for all sign-in failures. | `SignInFailure.cs:13` |
| F5 | `/auth/failed?reason=provider` is one generic "the provider turned Pollyanna away" flag; it carries no provider identity. | `SignInFailure.cs:16` |

## 5. Session handler (`src/Pollyanna/Auth/SessionAuthenticationHandler.cs`)

| # | Assumption | Where |
|---|---|---|
| H1 | Sessions are per person, not per login: the `Session` row has only `PersonId` (no issuer). Nothing records which provider a session came from. | `SessionAuthenticationHandler.cs:87`; `Data/Entities.cs:40-47` |
| H2 | A session is valid only if the person's `OidcSub` is non-null; a null `sub` is how a deleted account's tombstone is recognized. This relies on the single `OidcSub` column meaning "this person can sign in". | `SessionAuthenticationHandler.cs:45-48` |
| H3 | The signed-in principal carries only the person ID and display name; no issuer. | `SessionAuthenticationHandler.cs:58-63` |

## 6. Person entity and other uses of `OidcIssuer` / `OidcSub`

| # | Assumption | Where |
|---|---|---|
| E1 | `Person` has one nullable `OidcIssuer` and one nullable `OidcSub`: **one login per person**. | `src/Pollyanna/Data/Entities.cs:10-11` |
| E2 | Unique index on (`OidcIssuer`, `OidcSub`). Different issuers with the same `sub` can already coexist. | `src/Pollyanna/Data/PollyannaDb.cs:35` |
| E3 | `Email`, `EmailNormalized` and `EmailVerified` are stored once per person and overwritten on every sign-in from whatever the provider sent, so with several providers the last sign-in's claims win. | `Entities.cs:13-17`; `PersonSignIn.cs:76-81` |
| E4 | `DisplayName` and `PictureUrl` are likewise refreshed from the latest sign-in's claims; the picture is re-downloaded or deleted to match. | `PersonSignIn.cs:76, 80, 86, 121-147` |
| E5 | Account deletion nulls the one issuer/`sub` pair (the tombstone, ADR-0005). | `src/Pollyanna/Events/EventManager.Accounts.cs:87-89` |
| E6 | "Can be picked" means `OidcSub != null`. Invite-by-email and the people pickers use this. | `src/Pollyanna/Events/EventManager.cs:46` |
| E7 | The outbox skips a person whose `OidcSub` is null (deleted account). | `src/Pollyanna/Notifications/OutboxSender.cs:170` |
| E8 | SPEC.md §4 defines Person with `oidc_issuer` + `oidc_sub` (unique together), and §3 says "A person is identified by the provider's issuer plus `sub` claim". | `SPEC.md:92-95, 137` |

## 7. Sign-in mapping (`src/Pollyanna/Auth/PersonSignIn.cs`)

| # | Assumption | Where |
|---|---|---|
| A1 | `ProviderIdentity.FromClaims` takes the issuer from the `sub` claim's `Claim.Issuer` (set by the handler from the token), not from configuration. So the stored issuer is whatever the provider's tokens say. | `PersonSignIn.cs:20-39` (issuer at 33) |
| A2 | The record has no field for which configured provider (scheme) produced it, or whether that provider is trusted. | `PersonSignIn.cs:12-18` |
| A3 | Lookup order: (1) exact issuer + `sub`; (2) re-link by verified email; (3) create a new person. Step 3 is unconditional: any authenticated sign-in gets in. There is no admission check anywhere. | `PersonSignIn.cs:54-74` |
| A4 | Re-linking **replaces** the person's issuer and `sub` (it doesn't add a second login). After that, the old provider account no longer maps to this person; signing in with it again would go through steps 2-3 again. | `PersonSignIn.cs:65-66` |
| A5 | The doc comment and SPEC.md §3 frame re-linking as covering "a recreated account or a switch of provider", i.e. one provider at a time. | `PersonSignIn.cs:42-46`; `SPEC.md:96-99` |
| A6 | Invitation acceptance runs after every sign-in, for the email that sign-in just wrote to the person. | `PersonSignIn.cs:84, 96-119` |
| A7 | The "email_verified missing" hint says "The OIDC provider sent no email_verified claim" (singular) and fires once per process, regardless of which provider. | `PersonSignIn.cs:52-53, 154-165` |

### How re-linking by verified email works today

`PersonSignIn.cs:57-68`

1. Runs only when no person matches the exact issuer + `sub`, **and** the provider sent
   `email_verified` equal to `true` (a missing claim or `false` never re-links), **and** the email
   is non-empty after trimming.
2. Normalizes the email (trim, lower-case invariant; `Entities.cs:24-25`) and looks for people with
   that `EmailNormalized`, taking at most two.
3. Exactly one match: that person's `OidcIssuer` and `OidcSub` are overwritten with the new ones, and
   "Re-linked person {PersonId} to a new provider account." is logged. Zero or two-plus matches:
   falls through and a new person is created.
4. The match is against **any** person row, including people last seen through a different
   issuer, and regardless of whether that person's stored `EmailVerified` was true. Tombstones have
   `EmailNormalized` null (`EventManager.Accounts.cs:92`), so they never match.
5. A known issuer + `sub` is never re-linked elsewhere, even if its email now matches someone else
   (tested in `RelinkTests.A_known_account_is_never_relinked_elsewhere`).

### How invitation acceptance works today

- **Inviting** (`src/Pollyanna/Events/EventManager.cs:232-268`): the organizer types an email. If
  exactly one signed-in person (`OidcSub != null`) has that normalized email and their stored
  `EmailVerified` isn't `false`, they are added as a participant at once, and no invitation is
  stored. Two or more: the organizer is asked to pick by name. Otherwise an `Invitation` row
  (`EventId`, `Email`, `EmailNormalized`, `InvitedBy`, `CreatedAt`; `Entities.cs:116-126`, unique per
  event + email, `PollyannaDb.cs:87-92`) is stored, and the invitation email is queued unless the
  event is a draft.
- **Accepting** (`PersonSignIn.cs:96-119`): on every sign-in, if the person's email is non-null
  and `EmailVerified` is not `false` (so `true` **or missing** counts, unlike re-linking), every
  invitation with that normalized email becomes a `Participant` row (skipping events they are
  already in), an "Added to an event" notification is queued per event, and the invitations are
  deleted.
- Invitations carry no provider. Any provider that asserts the address (and doesn't say
  `email_verified: false`) accepts the invitation.
- SPEC.md §4 describes the same rules (`SPEC.md:280-290`).

## 8. Tests and test-provider fixtures

### Fixtures

| # | Dependency | Where |
|---|---|---|
| T1 | `TestApp` has one `Issuer = "https://issuer.test"`, one `ClientId`, one `ClientSecret`, and sets exactly the single-provider settings. | `tests/Pollyanna.Tests/Support/TestApp.cs:26-28, 52-55` |
| T2 | `TestApp` starts **one** in-process `MockIssuerApp` with one client whose only redirect URI is `PublicUrl + "/auth/callback"`. | `TestApp.cs:85-97` |
| T3 | The backchannel is redirected to the mock issuer by configuring `OpenIdConnectOptions` for the scheme named `"oidc"`. | `TestApp.cs:68` |
| T4 | `TestBrowser` takes one issuer `HttpClient`; `SignInAsync` follows the single redirect from `/auth/login` straight to the issuer, and `AuthorizeAtIssuerAsync` asserts the redirect goes to `TestApp.Issuer + "/authorize"`. No provider choice step. | `Support/TestBrowser.cs:13, 46-54, 62-66`; `TestApp.cs:112-119` |
| T5 | `Seed.SignInAsync` finds the new person by `OidcIssuer == TestApp.Issuer && OidcSub == sub`. | `Support/Seed.cs:21` |
| T6 | `Seed.PersonAsync` seeds people at `"https://other.test"` so test sign-ins never match them by `sub`. | `Support/Seed.cs:25-31` |
| T7 | The mock issuer supports one issuer URL per instance (`MockIssuerOptions.Issuer`) and a list of clients; its sign-in form takes `sub`, name, email, `email_verified` (true/false/omit) and picture. | `src/Pollyanna.MockIssuer/MockIssuerApp.cs:11-35, 53`; `TestBrowser.cs:70-73` |
| T8 | The standalone dev mock issuer is configured with one issuer and one client (`pollyanna-dev`, redirect `http://localhost:5010/auth/callback`), matching `appsettings.Development.json`. | `src/Pollyanna.MockIssuer/appsettings.json:8-17`; `src/Pollyanna/appsettings.Development.json:9-11` |

### Tests that assert single-provider behavior

| Test file | What depends on one provider | Where |
|---|---|---|
| `OidcFlowTests.cs` | Authorization request goes to `TestApp.Issuer/authorize` with `redirect_uri` = `PublicUrl/auth/callback`; first sign-in stores `OidcIssuer == TestApp.Issuer`. | `:37, :40, :91-92` |
| `RelinkTests.cs` | Whole file. Seeds people at `OldIssuer = "https://old-provider.test"` or `TestApp.Issuer`, signs in via the one mock issuer, asserts that re-linking **overwrites** issuer + `sub` (`:52-53`), covers recreated accounts at the same issuer (`:61-65`), unverified/missing `email_verified` never re-links (`:76-87`), ambiguity creates a new person (`:89-100`), known accounts never move (`:102-114`), and the once-per-process hint wording "The OIDC provider sent no email_verified claim" (`:130-143`). | `:14, 20-41` and as noted |
| `SignInFailureTests.cs` | Bad `ClientIdKey` / `ClientSecretKey` values; reconfigures the `"oidc"` scheme's backchannel; asserts log text naming `TestApp.Issuer`, "Check POLLYANNA_OIDC_ISSUER" and "Check POLLYANNA_OIDC_CLIENT_ID". | `:52-53, 64, 71, 78-79, 85, 91` |
| `ConfigurationTests.cs` | Settings dictionary with one issuer/client/secret; secret vs. secret-file rules; issuer scheme and loopback checks; `CallbackUrl`. | `:17-19, 32, 46, 55-56, 64, 72-73, 84, 94-95` |
| `DeploymentTests.cs` | `help` mentions `/auth/callback`; the settings report names one redirect URI; the startup log line names one redirect URI. | `:68, :100, :112` |
| `QuietLogTests.cs` | Starts the real process with the single issuer/client/secret variables; asserts the access log shows `GET /auth/callback 302`. | `:51, :72-74` |
| `InvitationTests.cs` | Invitation matching for `email_verified` true/missing vs. `false`, through the one mock issuer. | `:59-73, 75-86` |
| `AccountDeletionTests.cs` | Tombstone has null `OidcIssuer` / `OidcSub`. | `:88, :116-117, :231` |
| `NoRepeatTests.cs` | Builds a tombstone by nulling `OidcSub` / `OidcIssuer` directly. | `:168` |
| `PageTests.cs`, `PictureTests.cs` | Look a person up by `OidcSub` alone, and seed one at `"https://other.test"`. | `PageTests.cs:21, 27`; `PictureTests.cs:92` |
| `EmailSwitchTests.cs` | Raw SQL insert into `People (Id, OidcIssuer, OidcSub, ...)`, i.e. depends on the column names. | `:163` |

Most other test files sign in through `Seed.SignInAsync` / `TestBrowser.SignInAsync`, so they
depend on T1-T5 indirectly but assert nothing provider-specific.

## 9. Related spec and ADR statements

- SPEC.md §1: "Participants without an identity-provider account (proxies)" are a non-goal;
  everyone "gets their own account at the provider" (`SPEC.md:31-32`). ADR-0006 records the same
  (`docs/adr/0006-every-participant-has-their-own-account.md`).
- SPEC.md §3: "authorization-code flow with PKCE against one configured provider" (`SPEC.md:73-77`);
  "Anyone the provider authenticates may use the app ... limiting who can sign in is the provider's
  job" (`SPEC.md:120-121`); "If the provider is down, the app is down" (`SPEC.md:127-128`).
- SPEC.md §9 and §11 describe the one local mock issuer used by development and tests
  (`SPEC.md:592, 688-693`).
- GLOSSARY.md defines a Person as someone who signed in "through the identity provider" (singular)
  (`GLOSSARY.md:10`).
