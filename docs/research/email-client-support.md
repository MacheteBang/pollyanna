# Email client support for the "Paper slips" look

Research for issue #31 (part of map #30, "Branded HTML email"). Researched November 26, 2026.
The question: how much of the SPEC.md §12 look survives in Gmail (web, Android, iOS), Apple
Mail, iOS Mail, Outlook classic for Windows, new Outlook for Windows and Outlook.com.

This is facts and implications only. The product decisions belong to the grilling tickets.

## Sources and how far to trust them

- **caniemail.com** is the main source. Its raw data (the `_features/*.md` files in the
  `hteumeuleu/caniemail` repository on GitHub) was read directly, not a summary of it. Every
  cell below comes from that data and carries the feature's **last test date**. Several
  features were last tested in 2019–2021, so treat old "no" answers as likely but unconfirmed.
  - caniemail has **no separate column for new Outlook for Windows**. Its "Outlook Windows
    Mail" column is the old Windows Mail app, which Microsoft retired at the end of 2024
    ([Really Good Emails, "Classic Outlook vs the new Outlook"](https://reallygoodemails.com/school/blog/classic-vs-new-outlook-rendering-changes), undated).
    New Outlook runs on the same browser-based engine as Outlook.com (WebView2/Chromium per
    Really Good Emails; "a web browser engine … based on current Outlook web apps" per
    [Litmus, October 27, 2023](https://www.litmus.com/blog/a-guide-to-rendering-differences-in-microsoft-outlook-clients)).
    In the tables, the **Outlook.com column stands in for new Outlook**. That is an
    inference: no vendor publishes a feature table for new Outlook.
- **Google's own Gmail CSS reference**
  ([developers.google.com/workspace/gmail/design/css](https://developers.google.com/workspace/gmail/design/css), undated)
  and **Gmail Help** ([answer 145919](https://support.google.com/mail/answer/145919)).
- **Microsoft Support** on classic Outlook picture blocking
  ([support.microsoft.com](https://support.microsoft.com/en-us/outlook/block-or-unblock-automatic-picture-downloads-in-classic-outlook-email-messages)).
- **Litmus**, "Ultimate Guide to Dark Mode"
  ([litmus.com](https://www.litmus.com/blog/the-ultimate-guide-to-dark-mode-for-email-marketers),
  chart "as of February 2025").
- **Rémi Parmentier** (the caniemail author) on Outlook.com dark mode
  ([hteumeuleu.com, February 26, 2021](https://www.hteumeuleu.com/2021/emails-react-outlook-com-dark-mode/)).
- **AOSP** `fonts.xml` for Android's font aliases, and **Apple's** system font list
  ([developer.apple.com/fonts/system-fonts](https://developer.apple.com/fonts/system-fonts/)).

Key: **Y** supported, **N** not supported, **P** partial (note given), **?** not tested.

## Context that applies to every bullet

- **Gmail rewrites CSS hard.** It reads `<style>` only in `<head>`, keeps at most 16,384
  characters of it, and **drops a whole `<style>` block on any syntax it doesn't understand**
  (for example `:is()`, range media queries, or `rgb(0 0 0 / .3)` whitespace syntax). It
  removes `@font-face`, `@import`, `<link>`, and `prefers-color-scheme` queries, and names
  `transform` and `box-shadow` as unsupported. Clipped or printed messages lose `<style>`, so
  Google advises inlining essential styles ([Gmail CSS reference](https://developers.google.com/workspace/gmail/design/css)).
- **Gmail clips** messages whose HTML is over about 102 KB behind "[Message clipped] View
  entire message". Google doesn't document the figure; it is the commonly cited one
  ([Email on Acid](https://www.emailonacid.com/blog/article/email-development/gmail-email-clipping/),
  [SendGrid](https://support.sendgrid.com/hc/en-us/articles/9298249378075--Message-clipped-Emails-Sent-to-Gmail-Inboxes)).
- **"Non-Google accounts" in the Gmail apps.** Several caniemail cells say "only with non
  Google accounts": the Gmail Android or iOS app reading, say, a Yahoo mailbox gets better
  CSS support than the same app reading a Gmail address. For Pollyanna's guests, assume the
  Google-account behavior.
- **Classic Outlook for Windows renders with Microsoft Word.** That covers Outlook 2007–2019
  and classic Outlook for Microsoft 365 ([Litmus](https://www.litmus.com/blog/a-guide-to-rendering-differences-in-microsoft-outlook-clients)).
  Microsoft has committed to supporting classic Outlook "until at least 2029" for Microsoft
  365 and Office 2024 ([Microsoft Tech Community](https://techcommunity.microsoft.com/blog/outlook/new-outlook-for-windows-a-guide-to-product-availability/4078895),
  [Petri](https://petri.com/microsoft-classic-outlook-for-windows-2029/)), so it stays in
  the picture for years.

## 1. Web fonts (Courier Prime)

`@font-face`, last tested December 19, 2023; `<link>` not dated in the data.

| Client | `@font-face` | `<link>` stylesheet |
|---|---|---|
| Gmail web | N (only Roboto and Google Sans, which Gmail embeds itself) | N |
| Gmail Android | N | N |
| Gmail iOS | N | N |
| Apple Mail (macOS) | Y | Y |
| iOS Mail | Y | Y |
| Outlook classic (Windows) | P: declaration kept, **remote fonts ignored**; **buggy**: text set in the web font falls back to **Times New Roman**, ignoring the rest of the stack, unless `mso-generic-font-family` / `mso-font-alt` is set | Y (but the remote font still doesn't load) |
| New Outlook / Outlook.com | N (Outlook.com, tested 2019-07 and 2023-12) | N |

So Courier Prime loads only in **Apple Mail and iOS Mail** (and Outlook for Mac 2011/2016,
not 16.80). Everyone else sees the fallback stack.

**Fallback stack.** What each platform actually has:

- **Windows**: Courier New ships with Windows. (Classic Outlook needs the Times New Roman
  workaround above: hide the `@font-face` from Word, for example inside `<!--[if !mso]>`, or
  set `mso-font-alt`.)
- **macOS and iOS**: Courier New is a system font, Courier is a document-support font, and
  American Typewriter is a system font ([Apple](https://developer.apple.com/fonts/system-fonts/)).
- **Android**: `monospace` maps to Droid Sans Mono, but the names **`courier` and
  `courier new` are aliases of `serif-monospace`, which is Cutive Mono**, a typewriter face
  ([AOSP fonts.xml](https://android.googlesource.com/platform/frameworks/base/+/2a0135d19cab/data/fonts/fonts.xml)).
  Google warns that the file layout can change between releases, so this is a likely
  outcome, not a promise. Whether the Gmail Android app's WebView resolves those aliases
  wasn't verified.
- **Gmail web** uses whatever the reader's computer has, so Courier New on Windows and macOS.

That makes `"Courier Prime", "Courier New", Courier, monospace` the stack that lands on a
typewriter face almost everywhere: Courier Prime in Apple clients, Courier New on Windows
and macOS, and probably Cutive Mono on Android. Courier New is thinner and lighter than
Courier Prime, which was designed to fix exactly that, so body text gets paler wherever the
fallback is used.

## 2. Backgrounds, nested panels, borders, padding, width

| Client | `background-color` (2019-02-28) | `padding` (2019-07-25) | `max-width` (2019-08-02) | `border-radius` (2021-03-09) | `margin` (2019-10-01) | `display:flex` (2021-11-02) | `background-image` (2023-07-24) |
|---|---|---|---|---|---|---|---|
| Gmail web | Y | Y | Y | Y | P: no negative values | Y | Y |
| Gmail Android | Y | Y | Y | Y | P: no negative values | P: non-Google accounts only | Y |
| Gmail iOS | Y | Y | Y | Y | P: no negative values | P: non-Google accounts only | Y |
| Apple Mail | Y | Y | Y | Y | Y | Y | Y |
| iOS Mail | Y | Y | Y | Y | Y | Y | Y |
| Outlook classic | Y | P: **table cells only**; a row takes its tallest cell's vertical padding | N (2007–2016); P: tables only (2019) | N (VML only) | P: no negatives, no `auto`, not on `<span>`/`<body>`, background bleeds into margin | N | N (VML only) |
| New Outlook / Outlook.com | Y | Y | Y | Y | P: no negative values | Y (Outlook.com) | Y (Outlook.com) |

What this means for "a slip-paper panel on a kraft ground":

- **Solid background colors work in every target client**, on `<body>`, tables and cells.
  Both kraft `#C9A77C` and slip paper `#F4EDE0` are plain hex colors, so a slip-colored
  table centered on a kraft-colored outer table renders everywhere, classic Outlook included
  (dark mode aside, see section 5).
- **Borders and padding are safe on table cells everywhere.** On `<div>`s they fail in
  classic Outlook, where Word "ignores most div styling, including widths and padding"
  ([Litmus](https://www.litmus.com/blog/a-guide-to-rendering-differences-in-microsoft-outlook-clients)).
- **Max-width layouts** work everywhere except classic Outlook, which needs a fixed-width
  table (the usual "ghost table" in `<!--[if mso]>`). New Outlook handles `max-width`
  ([Really Good Emails](https://reallygoodemails.com/school/blog/classic-vs-new-outlook-rendering-changes)).
- **Tables versus divs:** only classic Outlook still forces tables. Every other target
  handles divs. Since classic Outlook is supported until at least 2029, a table skeleton
  (or divs with an Outlook-only table fallback) is still the norm.
- **Negative margins** (overlapping slips, a stamp hanging over an edge) are dropped by
  Gmail, Outlook.com and classic Outlook.
- **`rgba()`** (2021-05-14): Y in Apple clients and Outlook.com, N in classic Outlook, P in
  Gmail (comma syntax only; whitespace syntax **drops the whole `<style>` block** or the
  whole `style` attribute). So "ink at 30%" has to be a precomputed solid hex in CSS, or live
  inside an image.

## 3. Rotation, torn edges, shadows

| Client | `transform` (2019-02-28) | `box-shadow` (2023-12-20) | `clip-path` (2021-03-09) | `mask-image` (2024-11-27) | `border-image` (not dated) | `filter` (2024-01-03) |
|---|---|---|---|---|---|---|
| Gmail web | N | N | N | N | N | N |
| Gmail Android | N | P: non-Google accounts only | N | N | N | N |
| Gmail iOS | N | P: non-Google accounts only | N | N | N | N |
| Apple Mail | Y | Y | Y (13+) | Y (12+) | Y | Y |
| iOS Mail | Y | Y | Y (14+) | Y (15+) | Y | Y |
| Outlook classic | N | N | N | N | N | N |
| New Outlook / Outlook.com | N (also 2024-01) | **Y since 2023-12** (N before) | N | N | N | N |

- **Rotation only works in Apple Mail and iOS Mail.** Everywhere else the property is
  stripped and the slip renders straight. The failure is graceful: nothing breaks, the slips
  just stop being tilted. In Gmail, an unsupported *property* is removed on its own; only
  unparseable *syntax* takes the whole `<style>` block with it
  ([Gmail CSS reference](https://developers.google.com/workspace/gmail/design/css)).
- **Torn or irregular edges have no CSS route outside Apple.** `clip-path`, `mask-image`,
  `border-image` and `background-image` (in classic Outlook) all fail somewhere. The one
  technique that renders in all seven clients is an **ordinary `<img>`**: a strip image of a
  torn edge placed above or below the slip-colored cell, matching its color. It inherits the
  image caveats in section 4 (blocked in classic Outlook by default) and the dark-mode
  caveats in section 5 (a baked-in slip color won't follow an inverted background).
- **Box shadows** show in Apple clients and, since late 2023, Outlook.com (and so very likely
  new Outlook). They don't show in Gmail or classic Outlook. Dropping them is harmless.
- A tilted slip *as an image* (text in pixels) would rotate everywhere but would put content
  in images, which is exactly what image blocking and accessibility work against.

## 4. Images: SVG versus PNG, `data:` URIs, CID, remote

| Client | SVG as `<img src>` (2026-09-16) | Inline `<svg>` (2020-02-06) | `data:` base64 image (2020-02-06; Gmail web rechecked 2024-05) |
|---|---|---|---|
| Gmail web | P: rendered but **rasterized to PNG** (N through 2024-07) | N | N |
| Gmail Android | P: rasterized to PNG (2026-09) | N | N |
| Gmail iOS | P: rasterized to PNG (2026-09) | N | N |
| Apple Mail | Y (14+) | P: needs a background on `<body>` | Y |
| iOS Mail | Y (15+, also 26) | Y | Y |
| Outlook classic | N (2007–2016); Y (2019) | N | N (2007–2016); P: not GIF (2019) |
| New Outlook / Outlook.com | Y (also 2024-01) | N | Y (also 2024-01) |

- **PNG is the only format that works everywhere.** SVG `<img>` became workable in Gmail
  only recently, and only because Gmail turns it into a PNG. It still fails in classic Outlook
  before 2019. **Inline `<svg>` is removed by Gmail and both Outlooks**, so the bow and the
  girl can't be inline SVG the way the web pages use them (`_Girl.cshtml`).
- **`data:` URIs don't work in Gmail (any platform) or classic Outlook 2007–2016.** That rules
  them out as the only way to carry the marks.
- **CID (inline MIME parts, `src="cid:…"`):** caniemail doesn't track this, and the secondary
  sources conflict (Campaign Monitor's old table says Gmail and Outlook don't show them;
  current vendor posts disagree). No high-trust current source was found. If CID is a
  candidate, it needs testing in real clients. CID images also add to the message size and
  some clients list them as attachments.
- **Remote images by default:**
  - **Gmail** shows images automatically by default, after Google scans them, unless the
    message looks suspicious or the reader chose "Ask before displaying external images"
    ([Gmail Help](https://support.google.com/mail/answer/145919)). Gmail fetches images from
    Google's servers, so **an image URL must be reachable from the public internet**. A
    Pollyanna instance only reachable on a home network or VPN would show broken images in
    Gmail.
  - **Classic Outlook "is configured by default to block automatic picture downloads from
    the Internet"**, until the reader clicks "Download pictures" or adds the sender to Safe
    Senders ([Microsoft Support](https://support.microsoft.com/en-us/outlook/block-or-unblock-automatic-picture-downloads-in-classic-outlook-email-messages)).
  - **New Outlook** loads external images through "the Outlook service" (Microsoft's proxy)
    by default; the setting covers non-Microsoft mailboxes
    ([Office Watch, January 2, 2025](https://office-watch.com/2025/new-outlooks-blocked-images-problem/)).
    Outlook.com behaves the same way, as the same web app.
  - **Apple Mail / iOS Mail** load remote images; with Mail Privacy Protection on, they go
    through Apple's proxy ahead of time rather than being blocked.
- **Alt text matters in classic Outlook**, where it is what people see first
  ([Litmus](https://www.litmus.com/blog/a-guide-to-rendering-differences-in-microsoft-outlook-clients)).

## 5. Dark mode

Three behaviors ([Litmus, chart as of February 2025](https://www.litmus.com/blog/the-ultimate-guide-to-dark-mode-for-email-marketers)):
**no change**, **partial invert** (only light backgrounds are darkened and dark text
lightened), and **full invert** (light and dark colors both swapped).

| Client | What dark mode does to the email | `<meta name="color-scheme">` (2026-09-16) | CSS `color-scheme` (2023-09-18) | `@media (prefers-color-scheme)` (2023-03-08) |
|---|---|---|---|---|
| Gmail web | No change (Litmus: "Gmail Desktop") | N | N | N |
| Gmail Android | Partial invert | **P: only `light only` is honored** (new in the 2026-09 test) | N | N |
| Gmail iOS | Full invert | **P: only `light only` is honored** (2026-09) | N | N |
| Apple Mail | No change if no color-scheme meta; with the meta, pure white `#ffffff` backgrounds are inverted | Y | Y (root element only) | Y |
| iOS Mail | Same as Apple Mail | N (tested 12.4 only) | Y (root element only) | Y |
| Outlook classic | Full invert (Outlook 2021) | N | N | N |
| New Outlook / Outlook.com | Partial invert: recolors any element whose text/background contrast fails 4.5:1 against its dark theme, recording the originals in `data-ogsc`/`data-ogsb` attributes | N | N | Y on Outlook.com (2019-07, 2022-12) |

Notes:

- **`color-scheme: light` / `supported-color-schemes`:** neither the meta tag nor the CSS
  property stops inversion in Gmail apps, Outlook classic, new Outlook or Outlook.com, with
  one exception: **as of September 2026, the Gmail Android and iOS apps honor
  `<meta name="color-scheme" content="light only">`** (caniemail, citing a Google
  Issue Tracker comment that couldn't be read without signing in). Apple Mail honors the meta.
  So "light only" is now worth declaring, but it doesn't cover Outlook.
- **Apple Mail darkens plain-text emails** and emails without the meta tags behave as coded
  (Litmus). Explicit background colors on every panel keep Apple Mail light.
- **Outlook.com's recoloring** follows contrast, not a list of elements
  ([hteumeuleu.com](https://www.hteumeuleu.com/2021/emails-react-outlook-com-dark-mode/)).
  No sender-side opt-out was found.
- **What partial inversion does to the palette:** kraft `#C9A77C`, slip paper `#F4EDE0` and
  bright paper `#FFFDF8` are all light backgrounds, so partial inverters darken them into
  browns and grays, and ink `#1E1A16` text turns light. The relationship between ground and
  slip (darker kraft, lighter paper) can flatten or flip. Stamp red `#B3261E`/`#9E2019` and
  ribbon blue `#3F6E8C` are mid-dark; on a darkened slip their contrast drops, and a client
  that recolors for contrast may lighten them. Full inverters (Gmail iOS without
  `light only`, classic Outlook) swap everything, so red and blue shift hue.
- **Images aren't inverted.** A PNG mark with a baked-in slip-paper or kraft background
  stays light on a darkened panel and shows as a pale box. A transparent PNG with ink-dark
  strokes can vanish on a dark background; Litmus suggests a light outline or glow on
  transparent artwork. The ribbon-blue bow has the same problem to a lesser degree.

## What this means for Pollyanna

Facts, applied to the §12 look. No decisions are made here.

**Safe everywhere** (with a table skeleton and inline styles):

- The palette as **solid background colors**: a kraft outer ground, slip-paper panels,
  bright-paper insets, ink text, red and blue accents. Hex only, no `rgba()`.
- **Borders and padding on table cells**, a fixed or max width (with a classic-Outlook
  table fallback).
- A **typewriter look via the font stack**: Courier Prime in Apple Mail and iOS Mail,
  Courier New on Windows and macOS (including Gmail web and both Outlooks), very likely
  Cutive Mono on Android. Expect lighter, thinner text outside Apple clients.
- **Rubber stamps as text** (uppercase, red, bordered table cell) render everywhere;
  `border-radius` and rotation won't in classic Outlook, and rotation won't outside Apple.
- **PNG images** for the bow and the girl, as remote `<img>`s with alt text.

**Degrades gracefully** (fine to include; it just disappears):

- **Rotation** (`transform`): Apple Mail and iOS Mail only. Elsewhere slips sit straight.
- **Box shadows**: Apple clients and Outlook.com/new Outlook only.
- **Courier Prime itself**: Apple clients only.
- **Rounded corners**: missing in classic Outlook only.
- **Images in classic Outlook**: blocked until the reader allows them, so the email must read
  well with no images at all.

**Avoid or treat as risky**:

- **Inline `<svg>`** (stripped by Gmail and both Outlooks) and **`data:` URIs** (Gmail,
  classic Outlook). Note that the web app's marks are inline SVG today.
- **SVG `<img>`** as the only format: rasterized in Gmail, missing in classic Outlook before
  2019.
- **CSS-only torn edges** (`clip-path`, `mask-image`, `border-image`): Apple only. Torn edges
  that show everywhere would have to be images, which then carry the image-blocking and
  dark-mode problems above.
- **Modern CSS syntax in `<style>`** (`rgb()` whitespace syntax, `:is()`, range media
  queries): one such rule makes Gmail drop the whole block. Gmail also caps `<style>` at 16 KB
  and clips the message after about 102 KB.
- **Relying on "light only"**: §12 says "light only", but dark mode can't be refused in
  classic Outlook, Outlook.com or new Outlook, and only since 2026 in the Gmail apps. The
  palette will be partially or fully inverted for some readers whatever the email declares,
  so whether that's acceptable, or whether the email should plan a dark rendering, is a
  decision for the grilling tickets.
- **Images that only work on a private network**: Gmail and new Outlook fetch images through
  Google's and Microsoft's servers, so remote images must be publicly reachable from the
  instance. That bears on self-hosted instances behind a VPN or LAN.

**Not verified here** (worth checking by hand, as the map's "Hand-checking in Mailpit and
real clients" item already plans): CID images in Gmail and both Outlooks; whether the Gmail
Android app resolves `"Courier New"` to Cutive Mono; new Outlook's dark mode, which no
source covers separately from Outlook.com.
