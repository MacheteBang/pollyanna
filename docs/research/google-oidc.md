# Google as an OIDC provider: the facts

Research for [#8](https://github.com/MacheteBang/pollyanna/issues/8), part of the map in
[#6](https://github.com/MacheteBang/pollyanna/issues/6). Checked November 25, 2026, against
Google's own documentation and its live discovery document. This note records facts only;
the decisions are the user's.

Context: Pollyanna would use Google as an additional OIDC provider with the
authorization-code flow, PKCE, scopes `openid profile email`, and pushed authorization
requests (PAR) when discovery advertises them.

## Summary

| Question | Answer |
|---|---|
| `email_verified` always sent? | Yes, whenever the `email` scope is requested. |
| Can it be `false`? | Google defines it as a boolean that is false for unverified addresses, but documents no case where that happens. Google Accounts made with a non-Gmail address must verify that address at creation. |
| PAR | Not supported. Discovery has no `pushed_authorization_request_endpoint`. |
| `sub` stable? | Yes: "unique among all Google Accounts and never reused", and it never changes. |
| Same `sub` across clients and projects? | Yes. Google's only subject type is `public`, which by OIDC definition gives every client the same `sub`. |
| Testing mode with only `openid email profile` | No test-user list needed, no warning screen, and authorizations don't expire after 7 days. |
| "In production" with only those scopes | Verification isn't mandatory. Showing an app name and logo on the consent screen needs the lighter "brand verification". |
| Picture | A URL on `lh3.googleusercontent.com`, not always present. Format isn't documented; the default avatar was served as a small PNG. |
| Custom button | Allowed if it follows the branding rules. Google ships self-contained SVG and PNG buttons. |
| Redirect URIs | Exact match; HTTPS (except localhost); no raw IPs (except localhost); TLD must be on the public suffix list. |

## 1. `email` and `email_verified`

- If the `email` scope is requested, both claims are included: "If the `email` scope value is
  present, the ID token includes `email` and `email_verified` claims."
  ([OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect))
- Definition: `email_verified` is "True if the user's email address has been verified;
  otherwise false." ([OpenID Connect reference](https://developers.google.com/identity/openid-connect/reference))
  The tokeninfo endpoint returns it as the string `"true"` or `"false"` (same page); in the ID
  token it is a boolean.
- Google's docs give no case where a signed-in account gets `email_verified: false`.
- Google Accounts made for a non-Gmail address: when one is set up with a non-Google email,
  "we'll send a verification code to the email address you used to create the account", and
  without verifying "you won't be able to create a Google Account."
  ([Verify your Google Account](https://support.google.com/accounts/answer/27447))
  So such accounts start out with a verified address. Google says nothing about whether the
  flag can later become false (for example, after the address changes).
- Email isn't an identifier: "you **shouldn't** use the `email` field in the ID token as a
  unique identifier for a user"; its value "may not be unique to this account and could
  change over time". You also "can't rely on the domain of the `email` claim to identify
  users of Google Workspace or Cloud organizations"; the `hd` claim covers that.
  ([OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect))
- `email`, `email_verified`, `name`, `given_name`, `family_name` and `picture` are all in
  discovery's `claims_supported`.

## 2. Pushed authorization requests (PAR)

- Google's live discovery document
  ([accounts.google.com/.well-known/openid-configuration](https://accounts.google.com/.well-known/openid-configuration),
  fetched November 25, 2026) has no `pushed_authorization_request_endpoint` and no
  `require_pushed_authorization_requests`. Its endpoints are authorization, device
  authorization, token, userinfo, revocation and JWKS.
- Google's OIDC docs and reference don't mention PAR.
- Under Pollyanna's rule ("use PAR when discovery advertises it"), Google sign-in would use a
  plain front-channel authorization request.

Other discovery facts that bear on the flow:

- `code_challenge_methods_supported`: `plain`, `S256` (PKCE with S256 is accepted).
- `token_endpoint_auth_methods_supported`: `client_secret_post`, `client_secret_basic`
  only. The token reference marks `client_secret` "Required" for the authorization-code
  exchange ([reference](https://developers.google.com/identity/openid-connect/reference)), so
  a web client is a confidential client with a secret.
- `authorization_response_iss_parameter_supported`: `true`.
- `id_token_signing_alg_values_supported`: `RS256`. Issuer: `https://accounts.google.com`.
- `response_modes_supported`: `query`, `fragment`, `form_post`.

## 3. `sub`

- "An identifier for the user, unique among all Google Accounts and never reused." "A
  Google Account can have multiple email addresses at different points in time, but the
  `sub` value is never changed."
  ([OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect))
- "Use `sub` within your application as the unique-identifier key for the user."
  ([reference](https://developers.google.com/identity/openid-connect/reference))
- Across clients and projects: discovery's `subject_types_supported` is `["public"]` only.
  OpenID Connect Core §8 defines `public` as: "This provides the same sub (subject) value to
  all Clients." ([OIDC Core 1.0 §8](https://openid.net/specs/openid-connect-core-1_0.html#SubjectIDTypes))
  Google's own pages don't say this in words, but by its advertised subject type a person
  gets the same `sub` from every client in every project. Changing the client ID or Cloud
  project doesn't change it.

## 4. OAuth consent screen

Source unless noted: [Manage App Audience](https://support.google.com/cloud/answer/15549945).

**User types.**
- External: available to "any user with a Google Account". The publishing status (Testing or
  In production) applies.
- Internal: needs a Google Cloud organization (Google Workspace or Cloud Identity) and limits
  sign-in to its members; others get an `org_internal` error.
- Personal: limited "exclusively to the single Google Account that created the
  application". No verification, no user cap, no 7-day expiry, no unverified-app screen. It
  is a one-time choice and can't be changed to External or Internal later.

**Testing mode in general.**
- Access is "limited to up to 100 test users listed in the OAuth consent screen".
- "Authorizations by a test user will expire seven days from the time of consent." "If
  your OAuth client requests an offline access type and receives a refresh token, that token
  will also expire."

**Testing mode with only `openid email profile`.** These rules have an exception:
- "The only exception to this behavior is if your app requests a subset of the following:
  name, email address, and user profile". "For such requests, your users do not need to be in
  the trusted user list, they will not see a warning message", "and their authorizations will
  not expire after 7 days."
- So with only these scopes, the 100-user test list and the 7-day expiry don't apply. Any
  Google user can sign in. (Pollyanna keeps its own 30-day session and requests no refresh
  token, so Google token lifetime doesn't affect its sessions.)

**In production with only `openid email profile`.**
- "If your app utilizes only non-sensitive scopes, it is not mandatory for your app to
  complete the app verification process."
  ([OAuth App Verification Help Center](https://support.google.com/cloud/answer/13463073))
- To "display an app name and logo on the OAuth consent screen" an app needs "a
  lighter-weight verification process known as 'brand-verification'" (same page). Brand
  verification asks for a homepage that represents the app, on a domain the developer owns
  ([Verification requirements](https://support.google.com/cloud/answer/13464321)).

**Unverified-app screen and user cap.**
- The unverified-app warning and the "100 new users in total" lifetime cap (which "cannot
  be reset") apply to apps that request sensitive or restricted scopes before verification
  ([Manage App Audience](https://support.google.com/cloud/answer/15549945);
  [FAQ](https://support.google.com/cloud/answer/13463817)). `openid`, `email` and `profile`
  are not sensitive scopes, so they don't trigger them.

**What this means for a family-sized deployment (facts, no recommendation).**
- Each operator creates their own Google Cloud project and OAuth client (a confidential web
  client with a secret).
- User type: External covers any Google account. Internal only works for members of the
  operator's own Workspace or Cloud Identity organization. Personal allows one account only.
- With External and only `openid email profile`, both Testing and In production let any
  Google user sign in without a test-user list, warning screen or 7-day expiry. Neither needs
  app verification. Brand verification is only needed to show the app's name and logo on the
  consent screen.
- Limiting who signs in stays the provider's job (SPEC §3). With External, Google doesn't
  limit sign-in to a list (the test-user list doesn't apply to these scopes).

## 5. The `picture` claim

- "The URL of the user's profile picture." It "might be provided when the request scope
  included the string `profile`", and "this claim is never guaranteed to be present."
  ([OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect);
  [reference](https://developers.google.com/identity/openid-connect/reference))
- Host: Google's sample ID token payload uses
  `https://lh3.googleusercontent.com/a-/e2718281828459045235360uler`
  ([GIS JavaScript reference](https://developers.google.com/identity/gsi/web/reference/js-reference)).
  Google doesn't document the host as fixed.
- Format: Google doesn't document the image type, size or URL parameters.
- Observed (not documented), November 25, 2026: `https://lh3.googleusercontent.com/a/default-user=s96-c`
  returned HTTP 200, `content-type: image/png`, 567 bytes, with `x-content-type-options:
  nosniff`, and still PNG when the request sent `Accept: image/avif,image/webp,*/*`. The
  `=s96-c` suffix is a size and crop parameter (96 px, square crop). The URL is HTTPS and
  needs no authentication.
- Against SPEC §3's fetch rules (`http`/`https`, 5 s, ≤ 1 MB, PNG/JPEG/WebP/GIF): the
  observed URL and response pass. Since Google documents no format, real profile photos
  may be JPEG or another type; the fetcher's checks still decide.

## 6. "Sign in with Google" button branding

Source: [Sign in with Google branding guidelines](https://developers.google.com/identity/branding-guidelines).

- Google "strongly" recommends its Google Identity Services SDK button, but allows a custom
  one: "If you, however, need to adapt the button to match your app design, adhere to the
  following guidelines."
- Pre-approved assets: [signin-assets.zip](https://developers.google.com/static/identity/images/signin-assets.zip)
  (PNG at 1x to 4x, and SVG; Light, Dark and Neutral themes; pill and square shapes; with or
  without text). The web SVGs, checked November 25, 2026, have their text drawn as paths (no
  `<text>`, no font) and reference no external resources, so they can be served from the app's
  own origin under a strict CSP.
- Text: "Sign in with Google", "Sign up with Google" or "Continue with Google".
  "Localization of this text to match the language of your app or website is permitted and
  encouraged."
- Colors (web):
  - Light: fill `#FFFFFF`, 1 px inside stroke `#747775`, text `#1F1F1F`.
  - Dark: fill `#131314`, 1 px inside stroke `#8E918F`, text `#E3E3E3`.
  - Neutral: fill `#F2F2F2`, no stroke, text `#1F1F1F`.
- Font: "The button font is Google Sans Medium" (14/20). The page doesn't name a
  fallback.
- Padding (web): 12 px left of the logo, 10 px between logo and text, 12 px right of the
  text.
- Logo: "The button must always include the standard color for the Google 'G'." "You can't
  change the size or color of the Google 'G' logo"; it must sit on a white background. Don't
  create your own icon, use an outdated "G", or use a monochrome "G". Keep the aspect ratio
  when scaling.

So a self-hosted app with a strict CSP and no external assets can draw its own button, as
long as it follows these rules. It doesn't need Google's script.

## 7. Redirect URIs

Sources: [OAuth 2.0 for Web Server Applications, "Redirect URI validation rules"](https://developers.google.com/identity/protocols/oauth2/web-server);
[OpenID Connect](https://developers.google.com/identity/openid-connect/openid-connect).

- Exact match: the value "must exactly match one of the authorized redirect URIs for the
  OAuth 2.0 client", "including the HTTP or HTTPS scheme, case, and trailing '/', if any."
  Otherwise the request fails with `redirect_uri_mismatch`. Each URI is registered in the
  Cloud console.
- Scheme: "Redirect URIs must use the HTTPS scheme, not plain HTTP. Localhost URIs
  (including localhost IP address URIs) are exempt from this rule."
- Host: "Hosts cannot be raw IP addresses. Localhost IP addresses are exempted from this
  rule." A LAN address such as `192.168.x.x` is therefore not allowed.
- Domain: "Host TLDs (Top Level Domains) must belong to the public suffix list." Hosts can't
  be `googleusercontent.com` or URL shorteners the app doesn't own.
- Also banned: userinfo (`user@`), path traversal (`/..`), open redirects, fragments,
  wildcards (`*`), non-printable characters, invalid percent-encoding and null characters.
- Private hosts, checked against the [public suffix list](https://publicsuffix.org/list/public_suffix_list.dat)
  on November 25, 2026: `lan`, `local`, `internal`, `test` and `example` are not on it, so
  names like `pollyanna.lan` or `pollyanna.internal` fail the TLD rule (and so would
  `pollyanna.example` itself; it is used here only as a documentation name). `home.arpa` and
  `dev` are on the list. Google doesn't say whether it accepts `home.arpa` hosts; that is
  untested here. A private host under a real public domain (for example, a split-horizon
  name) passes the TLD rule; Google never contacts the redirect URI, the browser does.
- Development: `http://localhost:<port>/...` and `http://127.0.0.1:<port>/...` are allowed.
  The registered URI must still match exactly, port included.

## Sources

- Google discovery document: https://accounts.google.com/.well-known/openid-configuration
- OpenID Connect (Sign in with Google): https://developers.google.com/identity/openid-connect/openid-connect
- OpenID Connect reference: https://developers.google.com/identity/openid-connect/reference
- GIS JavaScript reference (sample payload): https://developers.google.com/identity/gsi/web/reference/js-reference
- OAuth 2.0 for Web Server Applications: https://developers.google.com/identity/protocols/oauth2/web-server
- Branding guidelines: https://developers.google.com/identity/branding-guidelines
- Manage App Audience: https://support.google.com/cloud/answer/15549945
- OAuth App Verification Help Center: https://support.google.com/cloud/answer/13463073
- Verification requirements: https://support.google.com/cloud/answer/13464321
- When is verification not needed: https://support.google.com/cloud/answer/13464323
- OAuth verification FAQ: https://support.google.com/cloud/answer/13463817
- Verify your Google Account: https://support.google.com/accounts/answer/27447
- OpenID Connect Core 1.0 §8: https://openid.net/specs/openid-connect-core-1_0.html#SubjectIDTypes
- Public suffix list: https://publicsuffix.org/list/public_suffix_list.dat
