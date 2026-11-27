# Deploying Pollyanna

Pollyanna ships as one Docker image. This guide runs it with Docker Compose behind a reverse
proxy you already have. Where it runs and how traffic reaches the proxy is up to you.

You need:

- A Linux x86-64 host with Docker and the Compose plugin.
- A reverse proxy that terminates TLS (Caddy, nginx, Traefik, Nginx Proxy Manager, …).
- An OpenID Connect provider (Pocket ID, Authelia, Authentik, Keycloak, …) where you can
  register a client. Everyone who takes part needs an account there, or at another provider
  you add next to it, such as Google (section 1, "More than one provider"). Extra providers
  are optional: Pollyanna relies only on the services you choose to configure.

What the container does on start: it checks every setting and refuses to start (listing every
problem) if one is missing or invalid, creates the draw key if there is none, brings the
database schema up to date, then serves plain HTTP on port 8080. It needs no internet
access beyond the identity providers you configure, the hosts that serve people's profile
pictures and, if you set one up, the mail relay.

The image carries its own short reference and this guide, for when you have only the image:

```sh
docker run --rm <image> help                     # settings, mounts, draw key, OIDC client, backup
docker run --rm --entrypoint cat <image> /app/DEPLOYMENT.md
```

Contents: [1. Register Pollyanna with your provider](#1-register-pollyanna-with-your-provider) ·
[2. Install](#2-install) · [3. Settings](#3-settings) · [4. The reverse proxy](#4-the-reverse-proxy) ·
[5. Backups](#5-backups) · [6. The draw key](#6-the-draw-key-read-this) ·
[7. Upgrading](#7-upgrading) · [8. Check it end to end](#8-check-it-end-to-end-before-first-use) ·
[9. Troubleshooting](#9-troubleshooting) · [10. Building and publishing the image](#10-building-and-publishing-the-image)

## 1. Register Pollyanna with your provider

Create an OIDC client (often called an "application" or "OIDC client") with:

| Provider setting | Value |
|---|---|
| Client type | Confidential: Pollyanna has a client secret. Not a "public client". |
| Redirect / callback URI | `https://pollyanna.example/auth/callback`, your public URL plus `/auth/callback`, exactly |
| Grant / flow | Authorization code. Pollyanna always uses PKCE (S256), so you can require it. |
| Scopes | `openid profile email` |
| Client authentication | Client secret (Pollyanna sends it in the token request body, `client_secret_post`) |
| Logout / post-logout URI | None. Signing out ends only the Pollyanna session, not the provider's. |
| Pushed authorization requests (PAR) | Optional. Pollyanna uses PAR when the provider advertises it. |

Pollyanna prints its redirect URI in the startup log, on the line after `Pollyanna … is ready
at …` (`sign-in provider "…" (default, trusted): redirect URI https://pollyanna.example/auth/callback`)
and, if other settings are still missing, at the end of the settings error, so you can copy it
from `docker compose logs` before anyone signs in.

Note the client ID and secret for `.env`, and the provider's issuer URL: the base URL whose
`/.well-known/openid-configuration` returns its discovery document. It must be `https`.

Claims Pollyanna reads: `sub`, `name` (or `preferred_username`), `email`, `email_verified` and
`picture`. The two email claims matter for two features:

- **Invitations by email** (an organizer invites someone before they first sign in) need the
  provider to send `email`. Through a trusted provider they match unless the provider sends
  `email_verified: false`; with no `email_verified` claim at all they still match.
- **Linking by email** (a recreated provider account, or another provider, finding its
  existing Pollyanna person by email and adding a login to them) needs `email_verified: true`.
  So does every first sign-in through an open provider (below).

So if your provider has a setting for sending `email_verified` (some make it optional), turn it
on. If it never sends the claim, Pollyanna logs one line naming the provider on the first such
sign-in after each start.

Pollyanna does no group or role checks of its own. Anyone a trusted provider (the default)
lets in can use it, so limit who can sign in at the provider (e.g. allow the Pollyanna client
only for a "family" group).

Each person has one or more **logins**: a provider's issuer plus their `sub` there. If you
later move to a different provider or issuer URL, the new login is added to the existing
person by verified email on their next sign-in, next to the old one. Anyone without a
verified email would arrive as a new, empty person. Once there's more than one provider,
people can also add a login themselves, under "Ways to sign in" on `/account`.

### More than one provider

You can configure several providers side by side: your own, plus a public one such as Google
for relatives who don't have an account on yours. Extra providers are optional, and none is
built in: Pollyanna talks only to the providers you configure. With one provider, `/auth/login`
goes straight to it, as before. With several, it shows "Sign in to Pollyanna" with one equal
"Sign in with <label>" button per provider.

The `POLLYANNA_OIDC_*` provider above keeps its settings and its `/auth/callback` redirect URI.
Each extra provider gets a key in `POLLYANNA_OIDC_PROVIDERS` (lowercase letters and digits,
starting with a letter, e.g. `google`), its own settings named `POLLYANNA_OIDC_<KEY>_*`, and its
own redirect URI, `https://pollyanna.example/auth/callback/<key>`. Register one client per
provider, as in the table above but with that provider's redirect URI. The startup log prints
one line per provider with its label, key, admission and redirect URI. The settings are in
section 3.

Every provider is either **trusted** or **open**:

- **Trusted:** anyone the provider authenticates gets in, as described above. This is the
  default for the `POLLYANNA_OIDC_*` provider.
- **Open:** a sign-in through it gets in only with a login a person already holds, or when
  the provider says the email is verified (`email_verified: true`) and that email belongs to a
  Pollyanna person or has a pending invitation. Anyone else is refused, with nothing about
  them stored, and sees a page saying why and offering every way to sign in again.

> [!WARNING]
> **With an open provider configured, anyone a Pollyanna person invites can sign in.** Any
> person can create an event and invite any address, so with an open provider the people in
> Pollyanna, not you, decide who gets in.

Configure only providers whose `email_verified` claim is honest. A verified email links a new
login to the person who already has that email, through trusted and open providers alike, so
a provider that calls an address verified without checking it could let someone into another
person's account.

Changing a provider between `trusted` and `open` takes effect at the next start and affects
only logins nobody holds yet, so it never locks anyone out.

Removing a provider leaves people whose only login came through it unable to sign in until
they use another provider with the same verified email. Their logins stay stored (marked
"no longer set up" on `/account`), so adding the provider back restores them.

### Adding Google

Google accepts a redirect URI only on a public domain over HTTPS, or `http://localhost` for
development. LAN-only names (`.lan`, `.local`, `.internal`) and raw IP addresses are refused,
so this needs a public host name for Pollyanna, even if only your own network can reach it.
(`pollyanna.example` below stands for your real host name; Google would refuse it too.)

1. In the [Google Cloud console](https://console.cloud.google.com/), create a project for
   Pollyanna (or pick one).
2. Under **Google Auth Platform**, set up the app: a name, a support email, the audience
   **External** (any Google account) and a developer contact email.
3. Under **Data access**, keep only the scopes `openid`, `email` and `profile`. With only
   these, Google needs no app verification and no test-user list, whether the app is in
   Testing or In production.
4. Under **Clients**, create a client of type **Web application** and add the authorized
   redirect URI `https://pollyanna.example/auth/callback/google`, exactly. The last part is
   the key you choose in `POLLYANNA_OIDC_PROVIDERS`.
5. Copy the client ID and the client secret. Google may show the secret only once, so keep it
   somewhere safe (e.g. in a file for `POLLYANNA_OIDC_GOOGLE_CLIENT_SECRET_FILE`).
6. Add the provider to `.env` and run `docker compose up -d`:

   ```sh
   POLLYANNA_OIDC_PROVIDERS=google
   POLLYANNA_OIDC_GOOGLE_ISSUER=https://accounts.google.com
   POLLYANNA_OIDC_GOOGLE_CLIENT_ID=<client ID>.apps.googleusercontent.com
   POLLYANNA_OIDC_GOOGLE_CLIENT_SECRET=<client secret>
   POLLYANNA_OIDC_GOOGLE_LABEL=Google
   POLLYANNA_OIDC_GOOGLE_ADMISSION=open
   ```

Google doesn't limit who signs in to an External app, so as a trusted provider it would let
in every Google account. Make it `open` unless that's what you want. Google always sends
`email_verified`, and it doesn't support PAR, so Pollyanna sends it a plain authorization
request.

### Button icons

Without an icon, a provider's button shows only its label. To put the provider's logo beside
it, set `POLLYANNA_OIDC_ICON_FILE` (the `POLLYANNA_OIDC_*` provider) or
`POLLYANNA_OIDC_<KEY>_ICON_FILE` (a listed one) to the container path of an image file you
mount read-only (readable by uid 1654): PNG, JPEG, GIF, WebP or SVG, at most 1 MB. Pollyanna checks it at start and
serves it itself, so the strict CSP holds. The repository ships no third-party logos: use the
provider's official artwork under its branding rules. For Google, the
[branding guidelines](https://developers.google.com/identity/branding-guidelines) link a
download of its sign-in assets (SVG and PNG); use the standard-color "G" logo from it,
unchanged, as those rules require.

```yaml
    volumes:
      - ./icons:/icons:ro
```

```sh
POLLYANNA_OIDC_GOOGLE_ICON_FILE=/icons/google.svg
```

## 2. Install

Pick a directory on the host, e.g. `/srv/pollyanna`, and copy in the two files from this
repository's `deploy/` directory:

```sh
mkdir -p /srv/pollyanna && cd /srv/pollyanna
# copy deploy/compose.yaml and deploy/.env.example here, then:
cp .env.example .env
chmod 600 .env
```

Edit `.env` and set the image, your public URL, the issuer, client ID and secret (section 3).

Create the two directories the container writes to. The app runs as uid/gid 1654 (the
image's non-root user), so they must belong to it:

```sh
sudo install -d -o 1654 -g 1654 -m 700 data draw-key
```

`data/` holds the database and the sign-in key ring. `draw-key/` holds only the draw key and
is deliberately separate, so a copy of `data/` never contains it (section 6). If you put
either somewhere else (e.g. the draw key on another disk or in a secret store's mount), change
the left-hand side of its line in `compose.yaml`.

Start it and watch the first start:

```sh
docker compose up -d
docker compose logs -f
```

You should see `Created a new draw key at /draw-key/draw.key` (the only warning a healthy
first start logs), the migrations, then `Pollyanna 0.0.1 is ready at https://pollyanna.example`
followed by one line per sign-in provider, e.g. `sign-in provider "id.example" (default,
trusted): redirect URI https://pollyanna.example/auth/callback`. `docker compose ps` shows the container as
`healthy` once `/healthz` answers. **Back up the draw key now** (section 6).

If you use rootless Docker or user-namespace remapping, uid 1654 inside the container is a
different uid on the host. Give the directories to that mapped uid instead.

## 3. Settings

All settings are environment variables, and all are required except the optional provider
settings and the email settings further down. In the Compose setup the first four come from
`.env`, and the two paths are fixed in `compose.yaml` to match its mounts.

| Variable | What it is |
|---|---|
| `POLLYANNA_PUBLIC_URL` | The address people open, e.g. `https://pollyanna.example`: a bare origin with no path. Pollyanna takes the browser's scheme from it (the proxy terminates TLS) and never trusts forwarded headers, so it must be the `https` URL people actually use. The OIDC callback is this plus `/auth/callback` (or `/auth/callback/<key>` for a listed provider). |
| `POLLYANNA_OIDC_ISSUER` | The provider's issuer URL (discovery at `/.well-known/openid-configuration`). Must be `https`; plain `http` is accepted only for a provider on localhost, which is for development. |
| `POLLYANNA_OIDC_CLIENT_ID` | The client ID from section 1. |
| `POLLYANNA_OIDC_CLIENT_SECRET` | The client secret. **Or** `POLLYANNA_OIDC_CLIENT_SECRET_FILE`: the path, inside the container, of a file holding only the secret (surrounding whitespace is ignored). Set exactly one of the two. |
| `POLLYANNA_DRAW_KEY_FILE` | Path of the draw key: 32 raw bytes, created with mode 0600 if absent, in which case its directory must be writable. Compose: `/draw-key/draw.key`. |
| `POLLYANNA_DATA_DIR` | Directory for `pollyanna.db` (SQLite in WAL mode, so `pollyanna.db-wal` and `pollyanna.db-shm` sit beside it) and `keys/` (the key ring that protects sign-in state and form tokens; stored unencrypted on purpose, so keep the directory private). Must be writable. Compose: `/data`. |

### Sign-in providers (optional)

These add to the `POLLYANNA_OIDC_*` provider above, or describe more providers next to it
(section 1, "More than one provider"). With at least one listed provider, the
`POLLYANNA_OIDC_*` provider itself becomes optional, but at least one provider must exist.

| Variable | What it is |
|---|---|
| `POLLYANNA_OIDC_LABEL` | The name on the `POLLYANNA_OIDC_*` provider's sign-in button, e.g. `Family account`. Default: the issuer's host name. |
| `POLLYANNA_OIDC_ADMISSION` | `trusted` (the default: anyone the provider authenticates gets in) or `open` (only a verified email that matches a person or a pending invitation gets in). |
| `POLLYANNA_OIDC_ICON_FILE` | The container path of a logo for its button (section 1, "Button icons"). Without it, the button is text only. |
| `POLLYANNA_OIDC_PROVIDERS` | The keys of the extra providers, separated by commas or spaces, e.g. `google`. A key is lowercase letters and digits, starting with a letter; `default` is reserved. They follow the `POLLYANNA_OIDC_*` provider in this order on every list of buttons. |
| `POLLYANNA_OIDC_<KEY>_ISSUER` | For each listed key, uppercased (e.g. `POLLYANNA_OIDC_GOOGLE_ISSUER`): the provider's issuer URL, under the same `https` rule. Required. |
| `POLLYANNA_OIDC_<KEY>_CLIENT_ID` | The client ID registered there. Required. |
| `POLLYANNA_OIDC_<KEY>_CLIENT_SECRET` | The client secret, **or** `POLLYANNA_OIDC_<KEY>_CLIENT_SECRET_FILE`, the path of a file holding it. Set exactly one. |
| `POLLYANNA_OIDC_<KEY>_LABEL` | The name on its button, e.g. `Google`. Required. |
| `POLLYANNA_OIDC_<KEY>_ADMISSION` | `trusted` or `open`, as above. Required: there's no default for a listed provider. |
| `POLLYANNA_OIDC_<KEY>_ICON_FILE` | Optional: a logo for its button, as for `POLLYANNA_OIDC_ICON_FILE`. |

Two providers can't share an issuer, and a `POLLYANNA_OIDC_<KEY>_*` setting whose key isn't
listed in `POLLYANNA_OIDC_PROVIDERS` stops the start, so a misspelled key can't silently drop a
provider.

### Email notifications (optional)

With a mail relay, Pollyanna emails people when something in an event needs them: "Event is
open — write your notes" and so on. Emails carry an HTML part and a plain-text part. The
HTML's one image, a small bow, travels inside the message as an inline attachment, so
opening an email loads nothing remote (no images, fonts or tracking). Emails link to the
page in Pollyanna, and never contain a message, anyone's notes, or who drew whom. Without a relay there are no emails and
Pollyanna works as before.

| Variable | What it is |
|---|---|
| `POLLYANNA_SMTP_HOST` | The relay's host name. Unset: email is off, and every other setting in this table must be unset too. |
| `POLLYANNA_SMTP_PORT` | The relay's port, e.g. `587` (with `starttls`) or `465` (with `tls`). Required with a host. |
| `POLLYANNA_SMTP_TLS` | `starttls` (upgrade the connection, and refuse to send without it), `tls` (implicit TLS from the start) or `none` (plain SMTP: only for a relay on the same host or a private network). Required with a host. |
| `POLLYANNA_SMTP_USERNAME` | The relay login, if it needs one. Needs a password with it. |
| `POLLYANNA_SMTP_PASSWORD` | The relay password. **Or** `POLLYANNA_SMTP_PASSWORD_FILE`: the path, inside the container, of a file holding only the password. Set at most one, and only with a username. |
| `POLLYANNA_MAIL_FROM` | The from address, e.g. `Pollyanna <pollyanna@home.example>`. Required with a host. Use an address your relay is allowed to send as. |

An incomplete set stops the start with the other settings problems. A relay that can't be
reached doesn't: the log says `The mail relay at <host>:<port> can't be reached (…)` and
Pollyanna starts anyway. With it reachable, the log says `Email notifications are on`.

Emails wait in an outbox table in the database until they are sent, are retried with backoff
(from a minute up to an hour apart), and are given up after 24 hours of failures. Pollyanna keeps
no record of sent mail, and logs a failure only by its kind and SMTP error, with no addresses;
to find out whether a particular email went out, look at the relay's own logs. Each email goes
to the address from the person's last sign-in, unless the provider marked it
`email_verified: false`; people without an email claim get none.

`POLLYANNA_IMAGE` in `.env` is read by Compose only: the image and version to run.

To keep the secret out of `.env`, put it in its own file (`chmod 600`, readable by uid
1654), mount it read-only, and point `POLLYANNA_OIDC_CLIENT_SECRET_FILE` at it:

```yaml
    volumes:
      - ./client-secret:/run/secrets/pollyanna-client-secret:ro
```

Optional, standard ASP.NET Core variables:

- `ASPNETCORE_HTTP_PORTS`: the port inside the container (default `8080`). If you change it,
  change the `ports:` line too; the health check follows it automatically.
- `Logging__LogLevel__Default`: `Warning` for quieter logs. At the default `Information`, the
  access log records method, route pattern and status only, never paths, which can contain
  event or assignment IDs. Successful health checks aren't logged. A healthy first start logs
  one warning, the new draw key (section 6); after that, warnings mean something.

Run one container per database. Pollyanna keeps some state in memory (the question-box rate
limit) and SQLite is a single file, so don't scale it to several replicas.

## 4. The reverse proxy

The proxy must:

- Terminate TLS for the public host name and forward requests to the container's port 8080
  (`http://<docker-host>:8080`), path unchanged.
- Be the only way to reach port 8080. The app trusts that every request came through the
  proxy as `https`. In `compose.yaml`, publish the port on `127.0.0.1` if the proxy runs on
  the same host, or firewall it to the proxy's address.

Nothing else is needed: no forwarded headers (they are ignored), no WebSockets, no special
timeouts or body sizes. Don't let the proxy cache responses. Pollyanna sends its own security
headers (strict CSP, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`); a proxy may add
HSTS.

Minimal examples, with Pollyanna on the same host:

```
# Caddy
pollyanna.example {
    reverse_proxy 127.0.0.1:8080
}
```

```nginx
# nginx (inside the server block for pollyanna.example with your TLS settings)
location / {
    proxy_pass http://127.0.0.1:8080;
    proxy_set_header Host $host;
}
```

`GET /healthz` answers `ok` without sign-in, for the proxy's or your monitoring's checks.

## 5. Backups

There are two things to back up, **separately**:

1. **The data**: `pollyanna.db` and `keys/` from the data directory. Back it up regularly.
2. **The draw key**: once, right after the first start, and somewhere else (section 6). It
   never changes after that.

Keeping them apart is the point. With the data alone, nobody can work out who drew whom, and
the operator's backups are part of what the draw is sealed against.

### Backing up the data

Don't copy `pollyanna.db` while the app runs: with WAL, a plain file copy can be inconsistent.
Use the built-in backup command, which writes a consistent copy with SQLite's online backup
API while Pollyanna keeps serving:

```sh
cd /srv/pollyanna
stamp=$(date +%F)
docker compose exec -T pollyanna /app/Pollyanna backup /data/backups/pollyanna-$stamp.db
sudo tar -C data -czf /path/to/backups/pollyanna-data-$stamp.tar.gz backups/pollyanna-$stamp.db keys
sudo rm data/backups/pollyanna-$stamp.db
```

The command refuses to overwrite an existing file. The copy lands inside `data/` because
that's what the container can write to; the last two lines move it, with `keys/`, to your
backup location. Run it from cron or a systemd timer (`-T` makes `exec` work without a
terminal), and copy the archives off the host as you would any other backup.

Losing `keys/` is harmless: everyone is signed out, and any form that was open fails once.

Stopping the container (`docker compose stop`) and copying `data/` whole also gives a
consistent backup, if a minute of downtime doesn't matter.

### Restoring the data

```sh
cd /srv/pollyanna
docker compose stop
sudo mv data data.old
sudo install -d -o 1654 -g 1654 -m 700 data
sudo tar -C data -xzf /path/to/backups/pollyanna-data-2026-12-01.tar.gz
sudo mv data/backups/pollyanna-2026-12-01.db data/pollyanna.db
sudo rmdir data/backups
sudo chown -R 1654:1654 data
docker compose start
```

Use the same draw key as before. Restoring data under a different key makes current slips
unreadable (section 6). If the backup is from an older Pollyanna version, the newer image
updates its schema on start.

## 6. The draw key (read this)

The draw key seals who drew whom. Pairings are stored so that only someone with both the
database and the key can link a giver to a recipient. The app creates it on first start and
never shows or logs it.

**If the key is lost, the current draw is lost.** Every slip in an event that has been drawn
but not yet revealed becomes unreadable: participants see a notice on the event page telling
them to ask an organizer to redraw, and the only recovery is a redraw. Past events lose their
"who drew whom" givers (shown as "Someone"), and the no-repeat rule can no longer see earlier
years (the manage page warns about this, and draws still go ahead). Nothing else is affected.

So:

- **Back it up once, right after the first start**, somewhere other than your data backups:
  a password manager, an encrypted USB stick, a secret store. For a password manager, store it
  as text: `sudo base64 draw-key/draw.key`.
- **Never let the app create a second key by accident.** If the draw-key mount is missing or
  points at an empty directory (say after moving `compose.yaml` to a new host and forgetting
  `draw-key/`), Pollyanna starts with a brand-new key. It logs
  `Created a new draw key at …` as a warning when it does. That line should appear exactly
  once in the life of an installation. If events are drawn but not yet revealed when it
  happens, it's logged as an error that counts them. Either way, if you see it again, stop the
  container and put the old key back before anyone draws.
- **The file is 32 raw bytes**, not base64 or hex. A file of any other size stops Pollyanna at
  start. Before the first draw, deleting it is harmless (Pollyanna makes a new one); after that,
  put the original back.
- **Restoring it** from base64:

  ```sh
  base64 -d > draw.key   # paste, then Ctrl-D
  sudo install -o 1654 -g 1654 -m 600 draw.key draw-key/draw.key && rm draw.key
  ```

- Moving to a new host: copy `data/` (or restore a backup) **and** the draw key, both owned by
  uid 1654.

## 7. Upgrading

1. Take a data backup (section 5).
2. Set the new version in `POLLYANNA_IMAGE` in `.env`.
3. `docker compose pull && docker compose up -d`, then check `docker compose logs` for
   `Pollyanna is ready`.

Schema migrations apply on start. There is no downgrade path once a newer version has
migrated the database: to go back, restore the backup from step 1 with the old image.

## 8. Check it end to end before first use

Automated tests cover the sign-in flow against a mock provider, not your real one. Before the
family uses it, do this once with the production provider, with at least three real accounts
(test accounts at the provider are fine), using a phone for at least one of them:

1. **Health and headers.** `curl -si https://pollyanna.example/healthz` returns `200 ok` with
   `Content-Security-Policy`, `X-Frame-Options` and `Referrer-Policy` headers.
2. **Sign-in.** Open the public URL. You are sent to the provider, back to
   `/auth/callback`, then land on the home page. Your name, email and picture (if the provider
   has one) show on `/account`. If sign-in fails, see section 9. With several providers,
   sign in once through each, and add a second login on `/account` under "Ways to sign in".
   With an open provider, also try an address nobody invited: it must be refused, with the
   reason, on Pollyanna's page.
3. **Access control.** Try with an account the provider shouldn't let in, if you restricted
   access. It must be refused at the provider.
4. **Sessions.** Sign out (ends the Pollyanna session only), then sign in again. Restart the
   container (`docker compose restart`); you stay signed in.
5. **Events.** Create an event, add the other accounts (one by email invitation before they
   ever signed in), and check that each of them sees it after signing in.
6. **The draw.** Draw names. Each participant unfolds their own slip and sees a recipient.
   Nobody, organizers included, sees anyone else's.
7. **Draw key survives a restart.** Restart the container; slips still unfold.
8. **Question box.** A giver asks their recipient a question from the slip, and the
   recipient answers on "Questions from your giver".
9. **Reveal and archive.** Reveal the event (everyone sees who drew whom), then archive it.
   It moves to "Past events". Archived test events stay in the participants' history; use
   test accounts if you don't want that, and delete them afterwards on `/account/delete`.
10. **Backups.** Run the data backup from section 5 and confirm the draw key backup from
    section 6 restores (`base64 -d` gives the same 32 bytes: `sudo sha256sum draw-key/draw.key`).
11. **Logs.** `docker compose logs` shows routes like `GET /events/{id:guid}/{handler?} 200` with no
    real IDs, and never the client secret or draw key.

## 9. Troubleshooting

- **The container keeps restarting.** `docker compose logs` starts with
  `Pollyanna is not configured correctly:` and one line per problem: missing settings, a
  non-`https` issuer, a public URL with a path, both or neither client-secret variable, an
  unreadable secret file, a draw key that isn't 32 raw bytes, or a data or draw-key directory the
  app can't write to (`sudo chown 1654:1654 data draw-key`). With several providers it can also
  be a key `POLLYANNA_OIDC_PROVIDERS` doesn't list, a misspelled setting of a listed provider,
  two providers with the same issuer, or an
  icon file that's missing or isn't a usable image.
- **"Sign-in didn't work" straight away, before the provider's page.** Pollyanna couldn't start
  the sign-in. The log has `Sign-in through provider <key> could not start:` and says why: the
  provider rejected the client (`invalid_client`: check the client ID, secret and
  registration), answered with another error, or its configuration couldn't be loaded from its
  issuer. The settings it names are that provider's.
- **"Sign-in didn't work" after the provider.** The log has `Sign-in through provider <key>
  failed:` with the provider's error code (or the error type). Check that the redirect URI at
  the provider is exactly the one the startup log prints for it, that the client ID and secret
  match, that `POLLYANNA_PUBLIC_URL` is the `https` address the browser shows, and that the
  host's clock is right (tokens are time-limited).
- **Redirect-URI error at the provider before sign-in.** The provider's registered callback
  doesn't match `POLLYANNA_PUBLIC_URL` + `/auth/callback` (or `/auth/callback/<key>` for a
  listed provider). Google calls this `redirect_uri_mismatch`, and refuses LAN-only names and
  raw IPs outright (section 1, "Adding Google").
- **Someone is refused through an open provider.** The log has `Refused a sign-in through
  provider <key>:` with the reason, never the address: `no invitation or person` (invite the
  address they used), `email not verified` (they confirm it with the provider, or sign in
  another way) or `ambiguous: N matches` (more than one person has that email).
- **The container can't reach the provider at start or sign-in.** It fetches discovery and
  keys from each provider's issuer itself, so the provider's host name must resolve and be
  reachable from inside the container, with a certificate it trusts.
- **No emails arrive.** Check the log for `Email notifications are off` (no
  `POLLYANNA_SMTP_HOST`), `can't be reached` (host, port or network) or `Couldn't send a
  notification (<kind>): <error>`: `the relay refused the login` is the username or password,
  `TLS handshake failed` usually a `POLLYANNA_SMTP_TLS` that doesn't match the port, and
  `the relay answered 5xx` the relay's refusal (often a from address it won't send as). The
  relay's own logs say more; Pollyanna records nothing about sent mail. People with an email
  the provider marked unverified, or none, get no email.
- **No profile pictures.** The container downloads them from the URL in the `picture` claim.
  Without access to that host, people show initials instead; sign-in is not affected.

## 10. Building and publishing the image

The image is built with the .NET SDK's container support (no Dockerfile). Its settings (the
chiseled ASP.NET base image, the non-root user, port 8080) are in `src/Pollyanna/Pollyanna.csproj`.
It contains only the app, plus this guide and `THIRD-PARTY-NOTICES.md` in `/app`: the mock
identity provider and the tests are separate projects and never part of it.

**Building from source**, with the .NET 11 SDK and Docker on the build machine:

```sh
dotnet publish src/Pollyanna -c Release -t:PublishContainer -p:ContainerImageTags=local
```

This loads `pollyanna:local` into the local Docker. A local build reports its version as
`0.0.0-dev`. Set `POLLYANNA_IMAGE=pollyanna:local` in `.env`
to run it.

**Releases** are published by CI (`.github/workflows/ci.yml`, on GitHub Actions). Every push
to `main` runs the tests. Pushing a tag `vX.Y.Z` runs the tests, then pushes the image to the
GitHub Container Registry as `ghcr.io/machetebang/pollyanna`, tagged `X.Y.Z`, the commit SHA
and `latest`:

```sh
git tag v0.0.3 && git push origin v0.0.3
```

CI also stamps the version into the app and the image's labels, and sets the
`org.opencontainers.image.source` and `documentation` labels to this repository and that
tag's copy of this guide.

CI pushes with the workflow's own `GITHUB_TOKEN`, so the repository needs no secret. The
repository is public, so the image is too: pulling it needs no `docker login`.
