# How the bow travels inside the email

Research for issue #62 (part of map #50, "A new visual identity"). Researched November 27, 2026.
The question: the HTML email gets one small bow image beside the text wordmark, and nothing
remote may be loaded (docs/adr/0003). How can the image travel inside the message, how does it
show in Gmail (web, Android, iOS), Apple Mail, iOS Mail, classic Outlook for Windows, new
Outlook and Outlook.com, what happens when it doesn't show, what dark mode does to it, and what
it costs.

This builds on `docs/research/email-client-support.md` (branch `research/email-client-support`),
whose section 4 covered SVG, PNG, `data:` URIs and remote images and left CID unverified. It is
facts and implications only. Remote loading is excluded and not researched here.

## Sources and how far to trust them

- **RFCs**: [RFC 2392](https://www.rfc-editor.org/rfc/rfc2392) (the `cid:` URL, August 1998),
  [RFC 2387](https://www.rfc-editor.org/rfc/rfc2387) (`multipart/related`, August 1998),
  [RFC 2183](https://www.rfc-editor.org/rfc/rfc2183) (`Content-Disposition`) and
  [RFC 2045](https://www.rfc-editor.org/rfc/rfc2045) (base64).
- **Microsoft**, first party:
  - [MS-OXCMAIL section 2.2.3.4.1.4.1, "Inline Attachments"](https://learn.microsoft.com/en-us/openspecs/exchange_server_protocols/ms-oxcmail/35b0a1ea-56b4-4bff-b9df-0c35c883a22b)
    (Exchange's MIME conversion spec, page updated May 20, 2025).
  - [Microsoft Graph `message` resource](https://learn.microsoft.com/en-us/graph/api/resources/message?view=graph-rest-1.0)
    (dated August 23, 2024, page updated November 10, 2026).
  - [Microsoft 365 Developer Blog, "Changes to inline image representation in Outlook on the web and new Outlook for Windows"](https://devblogs.microsoft.com/microsoft365dev/?p=24785)
    (October 6, 2025).
  - [Microsoft Support known issue, "Images are not displayed in emails, newsletters, or signatures in classic Outlook"](https://support.microsoft.com/en-us/support/known-issues/images-are-not-displayed-in-emails-newsletters-or-signatures-in-classic-outlook)
    (last updated June 5, 2026).
  - Microsoft Support on [classic Outlook picture blocking](https://support.microsoft.com/en-us/outlook/block-or-unblock-automatic-picture-downloads-in-classic-outlook-email-messages),
    [external image protection in Outlook.com](https://support.microsoft.com/en-us/outlook/external-image-protection-in-outlook-com)
    and [images in dark mode](https://support.microsoft.com/en-us/office/i-m-seeing-email-content-like-images-and-highlighting-that-doesn-t-look-great-in-dark-mode-bda3c57e-68b0-480b-8bf5-17f61ec95845)
    (all undated).
  - [Azure Communication Services, "Inline attachments"](https://learn.microsoft.com/en-us/azure/communication-services/concepts/email/email-attachment-inline)
    (dated September 30, 2024).
- **Google**, first party: the [Apps Script `GmailApp` reference](https://developers.google.com/apps-script/reference/gmail/gmail-app),
  [Gmail Help answer 145919](https://support.google.com/mail/answer/145919) on images and the
  [email sender guidelines](https://support.google.com/a/answer/81126) (all undated). **Google
  publishes nothing on how Gmail renders a received `cid:` image or what Gmail's dark mode does
  to images.**
- **Apple**, first party: [Mail User Guide, "Protect email privacy"](https://support.apple.com/guide/mail/protect-email-privacy-mlhlp1205/mac)
  and the [WebKit blog, "Dark Mode Support in WebKit"](https://webkit.org/blog/8840/dark-mode-support-in-webkit/)
  (May 7, 2019). **Apple publishes nothing on `cid:` images in Mail.**
- **caniemail** raw data (`hteumeuleu/caniemail`, `_features/*.md`, repository state of
  September 16, 2026). It has **no feature for `cid:` images or `multipart/related`** (a search
  of the whole repository for `cid:` and `content-id` finds nothing), and its image features
  don't cover dark mode.
- **SpamAssassin** source code (`apache/spamassassin` trunk, read November 27, 2026):
  `rules/20_html_tests.cf`, `rules/50_scores.cf`, `lib/Mail/SpamAssassin/Plugin/HTMLEval.pm`
  and `lib/Mail/SpamAssassin/HTML.pm`.
- **Lower trust**, used only where nothing better exists and marked where used: Rémi
  Parmentier (the caniemail author) on [Gmail dark mode, April 9, 2021](https://www.hteumeuleu.com/2021/fixing-gmail-dark-mode-css-blend-modes/)
  and [Outlook.com dark mode, February 26, 2021](https://www.hteumeuleu.com/2021/emails-react-outlook-com-dark-mode/);
  [Litmus, "Ultimate Guide to Dark Mode", February 27, 2025](https://www.litmus.com/blog/the-ultimate-guide-to-dark-mode-for-email-marketers);
  a tested fix in the [GAM project, pull request 1999, October 7, 2026](https://github.com/GAM-team/GAM/pull/1999);
  Apple Support Community threads.

## 1. Ways to carry an image inside the message

| Way | What it is | Status |
|---|---|---|
| **CID** | The PNG is its own MIME part with a `Content-ID` header, and the HTML says `<img src="cid:…">` | The only way that is not ruled out by a tested "no" in a target client. Details below. |
| **`data:` URI** | The PNG is base64 text inside the `src` attribute | **Not shown in Gmail (web, Android, iOS) or classic Outlook 2007–2016.** Shown in Apple Mail, iOS Mail, Outlook.com; partial in classic Outlook 2019 (caniemail `image-base64`, tested February 6, 2020, Gmail web rechecked May 2024). Where it fails the `src` is dropped, so the image is simply missing. |
| **Inline `<svg>`** | The drawing as markup in the HTML | Removed by Gmail and both Outlooks (earlier research, section 4). |
| **Remote `<img>`** | A URL on the instance | Excluded by docs/adr/0003. |

A bow that is not an image at all (a text glyph, or shapes made of table cells and background
colors) also travels inside the message. That was not researched here, since the decision in
#61 is an image.

**How CID works, per the RFCs:**

- A `cid:` URL "refers to a specific body part of a message". The URL is the part's
  `Content-ID` without the angle brackets, so `cid:bow@pollyanna.example` points at the part
  with `Content-ID: <bow@pollyanna.example>`. The ID has the form of an email address and must
  be globally unique (RFC 2392, section 2).
- The HTML and the image sit together in a `multipart/related` container, whose first part
  (the HTML) is the root (RFC 2387). With a plain-text alternative, the usual shape is
  `multipart/alternative` holding `text/plain` and a `multipart/related` that holds
  `text/html` and `image/png`.
- A client that understands `multipart/related` ignores the image part's
  `Content-Disposition`; one that doesn't treats the whole thing as `multipart/mixed` and can
  then use the disposition (RFC 2387). So `Content-Disposition: inline` matters only as a
  fallback.

### Does a CID image display?

| Client | Displays a CID image? | Basis |
|---|---|---|
| Gmail web | **Yes, on indirect evidence** | No Google page describes receiving one. Google's own sending API documents inline images in "the format `<img src="cid:imageKey" />`" (Apps Script reference), so Gmail itself produces this structure. An October 7, 2026 test in the GAM project built `multipart/related` (HTML plus an inline PNG) and reports that Gmail "displays the image inline" (lower trust: one project's test). |
| Gmail Android | **Not verified** | No source found for the app specifically. Likely the same as web, since the image is resolved from the stored message, but untested. |
| Gmail iOS | **Not verified** | Same as Android. |
| Apple Mail (macOS) | **Not verified by a primary source** | Apple documents nothing. Long-standing common knowledge says yes; no dated first-party or test source was found. |
| iOS Mail | **Not verified by a primary source** | Same as Apple Mail. |
| Classic Outlook (Windows) | **Yes** | Microsoft's known-issue article (updated June 5, 2026) treats `cid:` embedded images as normal content that **stopped displaying after the May 12, 2026 updates** in "all supported versions of classic Outlook" and was fixed in builds released in late May and June 2026. So they display, except on an install stuck on a May 2026 build. |
| New Outlook (Windows) | **Yes** | Microsoft's developer blog (October 6, 2025): Outlook on the web and new Outlook represent inline images with a `cid:` value in `src`, rolling out from November 15, 2025. |
| Outlook.com | **Yes** | Same web app as new Outlook. The Graph API models inline images as attachments with `isInline` and a `contentId` referenced by `<img src="cid:…">`. |

**What makes Microsoft's stack treat the part as inline** (MS-OXCMAIL): the part must be a
child of `multipart/related`, must have a `Content-ID` (or `Content-Location`), should be
referenced from an `<img>` in the HTML body, and should have a type from a short list that
includes `image/png` and `image/jpeg`. The spec warns that a part failing these is an ordinary
attachment. SVG is not on that list, which is one more reason the part should be PNG.

## 2. When the image is blocked or dropped, and whether it shows as an attachment

### Image blocking

Every vendor's blocking setting is worded for images fetched from elsewhere, and none of the
pages mentions images inside the message:

- **Classic Outlook** "is configured by default to block automatic picture downloads from the
  Internet", and the unblock action covers "linked pictures and other external content"
  (Microsoft Support).
- **Outlook.com** loads "external images using an image proxy" (Microsoft Support).
- **Gmail** offers "Ask before displaying external images" (Gmail Help).
- **Apple Mail** downloads or blocks "remote content" (Mail User Guide).

So by the vendors' own wording a CID image is outside what gets blocked, which removes the
main blocking case from the earlier research (classic Outlook's default). **No vendor states
outright that embedded images are exempt**, so this is a reading of the wording, not a quoted
guarantee. Microsoft's Azure page does give "reducing the risk of external content being
blocked" as a reason to use CID.

### What the reader sees if it doesn't display

- **Classic Outlook on an affected May 2026 build:** nothing, or a red X with "The linked
  image cannot be displayed. The file may have been moved, renamed, or deleted." Microsoft
  adds that a reply or forward made while the image was missing may lack it for good
  (Microsoft known issue).
- **Any HTML client that can't resolve the reference:** the standard broken-image treatment,
  which is the `alt` text or an empty box of the declared width and height. An empty `alt`
  leaves no text behind. Which of these each client draws was not verified.
- **A client showing the plain-text part:** no image, and by the RFCs no reason to list one,
  though what plain-text readers do with the unused part was not verified.
- **A client that doesn't understand `multipart/related`:** it treats the message as
  `multipart/mixed` (RFC 2387), so the PNG shows as a separate file after the body.
- The wordmark is text, so the name survives all of these.

### Does it show as an attachment?

- **Outlook.com, new Outlook, and classic Outlook on a Microsoft mailbox: no, when the part
  qualifies as inline.** The mailbox property behind the paperclip excludes inline images:
  `hasAttachments` "doesn't include inline attachments, so if a message contains only inline
  attachments, this property is false" (Microsoft Graph). MS-OXCMAIL also lets the server flag
  inline parts as invisible in HTML. That the paperclip in each Outlook follows this property
  is an inference; classic Outlook on a non-Microsoft account (plain IMAP or POP) was not
  verified.
- **Gmail: not verified for a correctly built message.** The only dated evidence is the GAM
  report: images sent as `Content-Disposition: attachment` in `multipart/mixed` showed "as
  attachments instead of inline", and the `multipart/related` form displayed inline. Whether
  Gmail's inbox list shows a paperclip, or a file chip under the message, for the correct
  form is not documented anywhere found.
- **Apple Mail and iOS Mail: not verified, and the anecdotes point to "yes".** Several Apple
  Support Community threads say Mail shows the paperclip for any embedded image, a signature
  logo included, while still drawing it in place. These are undated user reports, low trust,
  and may not match current versions.
- **A file name is visible wherever the part is listed**, so a neutral name (for example
  `bow.png`) is what a reader would see.

## 3. Dark mode: are images recolored, and what the bow needs

The earlier research (section 5) established which clients darken the email whatever it
declares: Gmail Android (partial invert) and Gmail iOS (full invert) unless they honor
`light only`, classic Outlook (full invert), and Outlook.com and new Outlook (partial invert).
Apple Mail and iOS Mail leave an email with `light only` alone.

**Do they change the image itself?** The evidence says no, with gaps:

- **Outlook for Android and iOS:** "We currently don't convert images in emails into Dark
  Mode" (Microsoft Support, undated).
- **Outlook.com and new Outlook:** the recoloring works by "changing either the element's
  `color` and `background-color` in CSS or `color` and `bgcolor` attributes in HTML"
  (Parmentier, February 26, 2021). Images are not in that list. Not stated outright.
- **Apple Mail and iOS Mail:** WebKit leaves images unchanged in dark mode, and `light only`
  tells Mail not to transform the message (WebKit blog, May 7, 2019).
- **Gmail apps:** Google documents nothing; Parmentier writes that it has "never been clear"
  how Gmail changes colors. His April 9, 2021 example shows Gmail iOS swapping the background
  under a logo image while the image keeps its colors, so white lettering inside the image
  "becomes almost invisible". That supports "images untouched". Some vendor help pages and
  blog posts claim Gmail sometimes inverts images; none shows a test, and they were not
  relied on. **Not verified.**
- **Classic Outlook:** no primary source found on images under its dark mode. **Not
  verified.**

**What follows for the bow.** If the image is untouched and only the ground behind it
changes from white to near-black, the outcome depends on how the PNG is made. The contrast
figures below are calculated here with the WCAG formula against sample dark grounds; the
actual dark colors each client uses are not published.

| Bow color | on white `#FFFFFF` | on black `#000000` | on `#121212` | on `#1F1F1F` | on `#292929` |
|---|---|---|---|---|---|
| Ribbon blue `#3F6E8C` | 5.5:1 | 3.8:1 | 3.4:1 | 3.0:1 | 2.7:1 |
| Ink knot `#1E1A16` | 17.3:1 | 1.2:1 | 1.1:1 | 1.05:1 | 1.2:1 |

For reference, WCAG 2.1 success criterion 1.4.11 asks for 3:1 for graphics
([W3C](https://www.w3.org/WAI/WCAG21/Understanding/non-text-contrast.html)).

- **Opaque PNG with a white ground baked in:** the bow looks exactly as designed, inside a
  white square of about 26px on the darkened ground. Nothing is lost; the square is visible.
- **Transparent PNG as is:** the ribbon stays readable but dimmer (around 3:1, at or just
  under the 3:1 line depending on the client's gray). **The ink knot disappears** (close to
  1:1), so the bow reads as two loops with a hole in the middle.
- **Transparent PNG with a white edge baked in:** a white stroke around the shape is
  invisible on white and outlines both ribbon and knot on a dark ground. Litmus recommends
  exactly this for transparent artwork with dark content: "a glow or solid white stroke". At
  26px a stroke is only a pixel or two wide, so how it looks is a design matter.
- **Transparent PNG with a knot that isn't ink:** any knot color light enough to hold
  contrast on near-black (the ribbon blue, for instance, at 3:1 to 3.8:1) survives without an
  edge, at the cost of the ink accent.
- **Swapping to a second image in dark mode is not available** where it matters: Gmail
  strips `prefers-color-scheme` queries and classic Outlook ignores them (earlier research,
  section 5).
- If a client did invert the image (unverified for Gmail and classic Outlook), a baked white
  ground would turn dark with the page and the blue would shift toward orange. Only a hand
  check settles that.

## 4. Deliverability and size

- **Size.** Base64 turns every 3 bytes into 4 characters plus line breaks (RFC 2045), so the
  PNG adds about 1.37 times its file size, plus a few hundred bytes of MIME headers and
  boundaries. The bow's actual file size was not measured; a small flat-color PNG is normally
  a few kilobytes. Unlike a remote image, the bytes ride in **every** message and are never
  cached.
- **Gmail clipping.** The roughly 102 KB limit is undocumented by Google. Secondary sources
  say it counts the HTML part only, not images or attachments
  ([Higher Logic](https://support.higherlogic.com/hc/en-us/articles/40187262318100-Avoid-Gmail-Email-Clipping-on-Message-Content);
  [Spam Resource, January 2022](https://www.spamresource.com/2022/01/what-is-gmail-clipping-and-what-to-do.html),
  from its own testing). If so, a CID image doesn't move the email toward clipping, while a
  `data:` URI would, since it sits in the HTML. Not verified against Google.
- **Google's sender guidelines** say nothing about images or attachments. Their content rules
  are to follow HTML standards and not to hide content with HTML and CSS.
- **SpamAssassin** (common on self-hosted and smaller mail servers) has two families of
  image rules, and both react to the `<img>` tag, not to how the image is carried:
  - `HTML_IMAGE_ONLY_04` to `_32` fire when the HTML has an image and its length falls in
    bands up to 3,200 bytes, scoring from about 0.3 to 2.8 points against a default spam
    threshold of 5. The check is on the HTML's length, so a full branded layout is well past
    the bands, but a very short HTML email with an image would score.
  - `HTML_IMAGE_RATIO_02` to `_08` compare text length to image area taken from the `width`
    and `height` attributes, and score 0.001. A 26 by 26 image is 676 square pixels, far too
    small to matter. The source substitutes a large default when a dimension is missing, so
    declared dimensions keep the area honest.
- **Microsoft's Azure page** presents CID as the better choice than base64 in the HTML for
  size and deliverability, and as avoiding externally hosted images. It is product
  documentation, not a filtering policy.
- **No source was found saying that Gmail, Outlook.com or iCloud penalize a small inline
  image.** No source says they don't, either. The providers don't publish content scoring.

## What this means for Pollyanna

Facts applied to the bow. No decisions are made here.

- **CID is the one in-message route with no tested "no"** among the target clients. `data:`
  URIs fail in Gmail and older classic Outlook, and inline SVG fails in Gmail and both
  Outlooks.
- **It displays in classic Outlook, new Outlook and Outlook.com on Microsoft's own word**,
  and in Gmail web on indirect evidence. Gmail's apps, Apple Mail and iOS Mail are expected
  to show it but have no primary source.
- **The part should be a PNG inside `multipart/related`, with a `Content-ID`, referenced by
  the `<img>`**: those are the conditions under which Microsoft's stack treats it as inline
  and keeps the paperclip off.
- **Image blocking as documented is about remote images**, so the "blocked until the reader
  allows it" case from the earlier research should not apply to the bow. The `<img>` still
  needs sensible `alt`, `width` and `height` for the cases where it doesn't draw.
- **A paperclip is the open risk**: ruled out for Microsoft mailboxes, unknown for Gmail, and
  reported by users for Apple Mail and iOS Mail.
- **Dark mode most likely leaves the image alone and darkens the ground**, which makes the
  ink knot vanish in a plain transparent PNG. A baked white ground, a white edge, or a
  lighter knot each avoid that, with different looks.
- **The cost is small**: a few kilobytes per message, no known deliverability penalty, and no
  effect on Gmail clipping if the secondary sources are right.

## Not verified

- CID display in **Gmail Android, Gmail iOS, Apple Mail and iOS Mail** (no primary source).
- Whether **Gmail, Apple Mail and iOS Mail show a paperclip or list the file** for a correct
  inline image; classic Outlook on a non-Microsoft account; new Outlook reading a
  non-Microsoft mailbox.
- Whether **Gmail's apps and classic Outlook leave image pixels untouched** in dark mode, and
  the dark ground colors each client uses.
- That embedded images are exempt from each client's image blocking (inferred from wording).
- What each client draws for an unresolved `cid:` reference, and what plain-text readers do
  with the unused image part.
- Gmail's clipping rule and any provider-side content scoring.
- The bow PNG's real file size.
