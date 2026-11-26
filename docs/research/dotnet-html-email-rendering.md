# Ways to render HTML email in .NET 11

Research for #32, part of map #30 ("Branded HTML email"). It lists facts and trade-offs only.
The choice belongs to the grilling ticket #34. Email-client CSS support is #31, and images are
#33. Both are touched here only where they bear on rendering.

Researched November 26, 2026, against .NET SDK 11.0.100 and ASP.NET Core 11.0.0.

## What the code does today

- A kind's `Compose` returns `NotificationContent(Subject, Body, Link)`, all plain strings
  (`Notifications/NotificationKindDefinition.cs`). `Body` can contain `\n`; the invitation adds
  a second line.
- `OutboxSender.Render(content, audience, ev, publicUrl)` is a static method. It builds the text
  as `Body`, a blank line, `Link`, `-- `, then `audience.Footer(ev, publicUrl)`. The footer is
  plain text with the `/account` URL written out (`Notifications/Audience.cs`).
- `OutgoingMail(To, Subject, Body)` is a record with one body. `SmtpMailSender.SendAsync` sets
  `message.Body = new TextPart("plain") { Text = mail.Body }` (`Notifications/MailSender.cs`).
- `OutboxSender` is a singleton `BackgroundService` (`Program.cs`:
  `AddSingleton<OutboxSender>()` plus `AddHostedService`). It runs with no `HttpContext`. Each
  run creates a DI scope with `IServiceScopeFactory.CreateAsyncScope()`, and that is where the
  database and `SealedHat` come from.
- Tests replace `IMailSender` with `FakeMailSender`, which queues each `OutgoingMail`. Tests read
  `app.Mail.Sent` or `app.Mail.To(...)`, about 70 call sites, and most of them assert on
  `mail.Body`. `NotificationSecrecyTests` concatenates `To`, `Subject` and `Body` and requires
  byte-identical output across shuffled pairings (`docs/agents/testing.md`). The tests already
  reference AngleSharp 1.8.4 to parse pages.
- The image is framework-dependent on `mcr.microsoft.com/dotnet/aspnet:11.0-resolute-chiseled`.
  It is built with the SDK's container support and has no Dockerfile on purpose (SPEC.md §11).
  SPEC.md §11 also says "no front-end build step". The project is AGPL-3.0, and every library
  named below is under a permissive license (MIT, Apache-2.0 or BSD-2-Clause) that can be used
  with it.

## The MIME side (the same for every candidate)

MimeKit's `BodyBuilder.ToMessageBody()` with both `TextBody` and `HtmlBody` set produces
`multipart/alternative` with the `text/plain` part first and the `text/html` part second. If
`LinkedResources` is non-empty, the HTML part becomes `multipart/related` (HTML root plus the
resources) inside the alternative. Attachments wrap everything in `multipart/mixed`. Source:
[`BodyBuilder.ToMessageBody`](https://github.com/jstedfast/MimeKit/blob/master/MimeKit/BodyBuilder.cs),
API docs: [BodyBuilder (MimeKit 5.0.0)](https://mimekit.net/docs/html/T_MimeKit_BodyBuilder.htm).
RFC 2046 §5.1.4 orders alternatives from least to most preferred, so text-then-HTML is the
conventional order.

So the change at the sender is local. `OutgoingMail` gains an HTML string alongside `Body`, and
`SmtpMailSender` swaps `TextPart("plain")` for `new BodyBuilder { TextBody = …, HtmlBody = … }.ToMessageBody()`.
Existing tests that read `mail.Body` keep working. `FakeMailSender` records the HTML for free.

Version note: Pollyanna pins MailKit 4.18.1. MimeKit 5.0.0 came out on November 10, 2026, and
`BodyBuilder` is still in it, now in `MimeKit.Core.dll` ([NuGet](https://www.nuget.org/packages/MimeKit)).

## Candidates at a glance

Sizes were measured by publishing an empty `dotnet new web` app for `net11.0`
(`-r linux-x64 --self-contained false`), with and without each package. They are bytes added to
`/app`, before image compression. Every in-process candidate was also run from a
`BackgroundService` in an app that called only `AddRazorPages()`, with no request, and rendered
`Alice <script>& "Bob"` as model text.

| | Razor component + `HtmlRenderer` | RazorLight | Razor view to string | MJML (Mjml.Net) | MJML (Node CLI) | Hand-built strings |
|---|---|---|---|---|---|---|
| Package | none: `Microsoft.AspNetCore.Components.Web` is in the ASP.NET Core shared framework | `RazorLight` 2.3.1 | none: MVC view engine, already registered by `AddRazorPages()` | `Mjml.Net` 4.15.0 | `mjml` 5.4.1 (npm) + Node.js | none |
| License | MIT | Apache-2.0 | MIT | MIT (+ HtmlPerformanceKit, MIT) | MIT; Node.js MIT | – |
| Last release | ships with .NET 11.0.0 (November 10, 2026) | January 16, 2023; last commit July 6, 2024 | ships with .NET | September 16, 2026; repo active (pushed November 10, 2026) | September 10, 2026 (6.0.0-alpha.1 October 9, 2026) | – |
| Added to `/app` | 0 | +15.5 MB; +28.1 MB with the `PreserveCompilationContext` it needs (13 MB `refs/`) | 0 | +0.39 MB | Node 24 linux-x64: 201.8 MB unpacked (`node` binary 126.9 MB), plus `node_modules` | 0 |
| Runs in `OutboxSender` with no `HttpContext` | yes (verified) | yes (verified, after publish only with `PreserveCompilationContext`) | yes, with a made-up `ActionContext` (verified) | yes (verified) | only as a child process; the chiseled image has no Node | yes |
| HTML-encodes model text | yes, by default (verified) | yes, by default (verified) | yes, by default (verified) | no: MJML is markup, so interpolated text must be encoded first | no, same as Mjml.Net | only if each value is encoded by hand |
| Produces email-safe table layout | no, you write the markup | no | no | yes (verified: output is `<table>`-based) | yes | no |

## Razor components through `HtmlRenderer`

- **What it is.** Microsoft's documented way to render a `.razor` component to a string
  "outside of the context of an HTTP request". The docs name "generating email content" as a
  use. Source: [Render Razor components outside of ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/blazor/components/render-components-outside-of-aspnetcore?view=aspnetcore-11.0)
  (monikers 8.0 to 12.0, updated November 3, 2026). API:
  [`HtmlRenderer`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.components.web.htmlrenderer).
- **How it runs.** `new HtmlRenderer(IServiceProvider, ILoggerFactory)`. It is
  `IAsyncDisposable`. `RenderComponentAsync<T>(ParameterView)` must be called inside
  `renderer.Dispatcher.InvokeAsync(...)`, and the result is read with `output.ToHtmlString()` or
  `WriteHtmlTo(TextWriter)`. The task completes after async lifecycle methods finish. In
  `OutboxSender`, the renderer can be built from the run's `scope.ServiceProvider`, or from the
  root provider, which is what the probe did. No `AddRazorComponents()` is needed.
- **Dependencies, image.** None. `Microsoft.AspNetCore.Components.Web.dll` is part of
  `Microsoft.AspNetCore.App` 11.0.0, which the `aspnet` base image already carries (checked in
  the installed shared framework). The `Microsoft.NET.Sdk.Web` project SDK compiles `.razor`
  files at build time, so there is no runtime compilation.
- **Authoring.** Typed `[Parameter]` properties, so a layout component could take
  `NotificationContent`, the footer and the event name directly. Components can nest, for
  example a shared layout with a slot. In a `<style>` block, `@media` must be written `@@media`,
  because Razor escapes `@` with `@@`
  ([Razor syntax](https://learn.microsoft.com/en-us/aspnet/core/mvc/views/razor)). The output
  is plain HTML: no Blazor script and no interactivity unless you add it.
- **Maintenance.** Ships and is serviced with .NET, MIT.
- **Tests.** In-process and deterministic. The whole-app tests exercise it through the outbox.
  A component can also be rendered directly in a unit test with a `ServiceCollection`, as in
  the docs' console example.

## RazorLight

- **What it is.** A Razor templating engine that compiles `.cshtml` strings, files or embedded
  resources at runtime with Roslyn. [GitHub `toddams/RazorLight`](https://github.com/toddams/RazorLight),
  Apache-2.0, 1,663 stars, 77 open issues.
- **Maintenance.** The last NuGet release is 2.3.1, from January 16, 2023
  ([NuGet](https://www.nuget.org/packages/RazorLight)). Its newest target is `net6.0`. The last
  commit was July 6, 2024 (a sample). Open issues include "Change to .NET8 & .NET Standard
  Compatibility" (#560, July 2025), "Literal empty arrays `[]` … reported as syntax error"
  (#555, which is C# 12 collection-expression syntax) and "Unable to use 'media' queries in html
  templates" (#536).
- **What it pulls in on .NET 11.** Restoring on `net11.0` resolves
  `Microsoft.CodeAnalysis.CSharp` 4.0.0 and `Microsoft.AspNetCore.Razor.Language` 6.0.0 (from
  the published `deps.json`). Templates are therefore compiled with a C# 10-era compiler, no
  matter what the app uses. `dotnet list package --vulnerable` reported nothing on
  November 26, 2026.
- **How it runs.** `new RazorLightEngineBuilder()…Build()`, then
  `CompileRenderStringAsync(key, template, model)`. The engine is meant to be built once and
  reused (it caches compiled templates), and it needs no `HttpContext`. A published app fails
  with `RazorLightException: Can't load metadata reference from the entry assembly. Make sure
  PreserveCompilationContext is set to true` until the csproj sets
  `PreserveCompilationContext` (verified; the README's deployment FAQ says the same).
- **Image.** +15.5 MB without that setting: Roslyn 8.4 MB, Razor.Language 1 MB and about
  4 MB of Roslyn satellite resource assemblies. With it, the total is +28.1 MB, because it adds
  a 13 MB `refs/` folder.
- **Tests.** In-process. The first render of each template pays a Roslyn compile. Errors in a
  template show up at runtime, not at build.

## Razor views rendered to a string

- **What it is.** Using the MVC view engine that Razor Pages already registers
  (`IRazorViewEngine`) to find a `.cshtml` view and render it into a `StringWriter`. There is no
  Microsoft Learn article for this pattern. It is assembled from the APIs:
  [`IRazorViewEngine.GetView`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.razor.irazorviewengine),
  [`ViewContext`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.rendering.viewcontext)
  (which needs an `ActionContext`, a `ViewDataDictionary`, an `ITempDataDictionary`, a
  `TextWriter` and `HtmlHelperOptions`) and
  [`ActionContext`](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.actioncontext)
  (which needs an `HttpContext`, `RouteData` and an `ActionDescriptor`).
- **How it runs without a request.** Make a `DefaultHttpContext { RequestServices = scope.ServiceProvider }`,
  empty `RouteData` and `ActionDescriptor`, and a `TempDataDictionary`. The probe rendered
  `/Views/Email/Mail.cshtml` this way from a `BackgroundService`, with correct encoding. It is a
  fake request: anything that reads the request (tag helpers that generate URLs, such as
  `asp-page`, or antiforgery) has nothing real behind it. The emails' links are already absolute
  strings (`NotificationContext.EventUrl`), so they need none of that.
- **Gotcha.** `_ViewStart.cshtml` files on the view's path run before every full view
  ([Layout in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/mvc/views/layout)).
  Pollyanna has `Pages/_ViewStart.cshtml`, so email views kept outside `Pages/`, or ones that set
  `Layout = null`, avoid picking up the site layout. The `@@media` escaping applies as above.
- **Dependencies, image.** None beyond what the app has. Views are compiled at build.
- **Tests.** As for `HtmlRenderer`. About 15 lines of plumbing are needed to fake the action
  context.

## MJML

MJML is a markup language (`<mj-section>`, `<mj-column>`, `<mj-button>` and so on) that compiles
to table-based, responsive, inlined email HTML. Upstream is JavaScript:
[mjmlio/mjml](https://github.com/mjmlio/mjml), MIT, latest 5.4.1 (September 10, 2026).

MJML is not a templating language. Something still has to put the event name and body into the
MJML, either string interpolation or one of the Razor options above, and that step must
HTML-encode the values, because MJML treats `<mj-text>` content as HTML.

### Mjml.Net (.NET port)

- [GitHub `SebastianStehle/mjml-net`](https://github.com/SebastianStehle/mjml-net) and
  [NuGet `Mjml.Net`](https://www.nuget.org/packages/Mjml.Net): MIT, 4.15.0 released
  September 16, 2026, 2 open issues. It targets `net7.0` through `net10.0`; the `net10.0` build
  runs on .NET 11 (verified). Dependencies are `HtmlPerformanceKit` 1.0 (MIT) and
  `Microsoft.Extensions.ObjectPool`.
- It is a port of MJML **4** (the README says so, and benchmarks against MJML 4.18.0), while
  upstream is on 5.x. The README marks every MJML 4 component "Feature Complete".
- **How it runs.** `new MjmlRenderer().Render(mjml, new MjmlOptions { Beautify = false })`
  returns `(Html, Errors)`. It is synchronous, needs no context and is safe to call from the
  singleton. `mj-include` resolves through a pluggable `IFileLoader`.
- **CSS.** MJML's own output carries inline styles. The README calls `mj-style inline="inline"`
  and `mj-html-attributes` an "EXPERIMENTAL FEATURE … not recommended … in production yet". It
  needs the separate `Mjml.Net.PostProcessors` package (AngleSharp) and the async render path.
- **Determinism.** Only `mj-navbar` and `mj-carousel` use the random `IIdGenerator`. With those
  left out, output is stable, which the byte-identical secrecy tests need. A static generator
  can also be passed in `MjmlOptions.IdGenerator`.
- **Image.** +0.39 MB.
- **Tests.** In-process, like the Razor options. Render errors come back as a list, not as
  exceptions, so a test can assert that the list is empty.

### MJML CLI (Node)

- **At runtime.** The chiseled `aspnet` image has no Node.js and no shell or package manager.
  Node 24 for linux-x64 is 201.8 MB unpacked, 126.9 MB of it the `node` binary
  ([nodejs.org dist](https://nodejs.org/dist/latest-v24.x/)), before `mjml`'s `node_modules`.
  Adding it would mean a Dockerfile or a different base image, which SPEC.md §11 rules out
  today. Each email would be a child process.
- **At build or dev time only.** The alternative is to compile `.mjml` to `.html` (or
  `.cshtml`/`.razor`) with the CLI and commit the output, so the image is unchanged. That puts
  Node into the contributor toolchain, against SPEC.md §11's "no front-end build step". Tests
  would see only the committed output, and nothing would check it against the `.mjml` source
  unless CI also ran Node.

## Hand-built strings

- C# raw interpolated strings (`$$"""…"""`, where `{{x}}` is a hole and single braces are
  literal, so CSS braces need no escaping). Every value is encoded with
  `System.Net.WebUtility.HtmlEncode` or `HtmlEncoder.Default` (System.Text.Encodings.Web, in the
  shared framework). This is the same shape as today's `OutboxSender.Render`.
- No dependencies, nothing added to the image, deterministic and testable the same way.
- Encoding is opt-in per value. A forgotten `HtmlEncode` on an event or display name is an
  injection, and nothing at build time catches it. The three Razor options encode by default and
  need `Html.Raw`, `MarkupString` or `@Raw` to opt out.

## CSS inlining

Why it matters, from Gmail's own documentation
([CSS support](https://developers.google.com/workspace/gmail/design/css)): Gmail keeps `<style>`
only in `<head>`, up to 16,384 characters in all. It drops a whole block on a syntax error or
on newer selectors (`:is()`, `:not(.a, .b)`). It removes `<link>`, `@import` and `@font-face`,
which means no web fonts such as Courier Prime. It strips `<style>` altogether for non-Google
(IMAP/POP/Exchange) accounts in the mobile app, and when printing or viewing the full message,
applying "only inline styles". Wider client support is #31.

| Option | Package | License, last release | Added to `/app` | Notes |
|---|---|---|---|---|
| PreMailer.Net | `PreMailer.Net` 2.7.4 + `AngleSharp` | MIT; August 28, 2026; repo pushed November 25, 2026 ([GitHub](https://github.com/milkshakesoftware/PreMailer.Net)) | +1.12 MB (mostly AngleSharp 1 MB) | `PreMailer.MoveCssInline(html, removeStyleElements, ignoreElements, css, …, preserveMediaQueries)`, a static, synchronous call with no context (verified: `.slip{…}` became `style="background: #F4EDE0;color: #1E1A16"`). `preserveMediaQueries` keeps `@media` blocks in `<style>`. It **downloads** `<link href="….css">` stylesheets, so a layout with only inline `<style>` (or the `css:` argument) avoids a network fetch at send time. `-premailer-*` properties become HTML attributes (`cellspacing`, `width`). The tests already use AngleSharp 1.8.4, and PreMailer needs `>= 1.7.2`. |
| AngleSharp.Css, inlining by hand | `AngleSharp.Css` 1.6.0 | MIT; November 24, 2026 | about the same as PreMailer | A CSS object model and computed styles on top of AngleSharp. It is not an inliner: the cascade-to-`style` code would be ours. |
| MJML's own output | Mjml.Net | as above | as above | Components emit inline styles. `mj-style inline` is experimental in Mjml.Net. |
| Inline `style` by hand | none | – | 0 | Write `style="…"` in the template directly. With Razor, palette values can be C# constants or a small component per element. Media queries, if any, stay in a `<head><style>` block that clients may drop. |

## Not in the ticket's list (noted only)

General-purpose template engines that could stand in for Razor:
[Fluid](https://github.com/sebastienros/fluid) (Liquid, `Fluid.Core` 3.0.0, November 16, 2026,
MIT, ships a `net11.0` target) and [Scriban](https://scriban.github.io/) (7.6.1, November 8, 2026,
BSD-2-Clause). Neither was measured or run.

## Trade-offs in short

- **Zero new dependencies and zero image cost:** `HtmlRenderer`, Razor views to string, and
  hand-built strings. Of these, `HtmlRenderer` is the only one Microsoft documents for this job
  (including "generating email content"). Views to string needs a faked `ActionContext`. Hand-built
  strings encode only when each value is encoded by hand.
- **Email-safe layout for free:** only MJML, at +0.39 MB in-process (an MJML 4 port, with
  experimental CSS inlining), or with Node, which doesn't fit the image or the
  no-build-step rule. MJML still needs a templating step in front of it.
- **RazorLight** costs 15 to 28 MB, has had no release since January 2023, compiles templates
  with Roslyn 4.0, and needs `PreserveCompilationContext`. It does nothing the in-box Razor
  options can't.
- **Inlining** is a separate decision from templating. Either PreMailer.Net (+1.1 MB, active,
  mind the `<link>` download), MJML's built-in styles, or inline styles written by hand.
- **Tests:** every in-process option keeps today's seam. `OutgoingMail` gains an HTML field,
  `FakeMailSender` records it, AngleSharp parses it, and `NotificationSecrecyTests` can add it to
  the byte-identical comparison. That comparison needs deterministic output (watch Mjml.Net's
  random ids).
