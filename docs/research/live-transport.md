# How live updates reach the browser

Research for #83 (map #82, "Real-time notifications"), November 27, 2026. Throwaway branch; never merged.

## Question

How should live updates reach the browser: SignalR, server-sent events (SSE), polling or something else? Judged against Pollyanna's constraints: no front-end build step and nothing remote (§1, §2, §11), a TLS-terminating reverse proxy (§2), one instance on SQLite, the test seams in `docs/agents/testing.md`, image size, phones, and cookie authentication with antiforgery.

## Recommendation

**Server-sent events**, from one authenticated `GET` endpoint that returns `TypedResults.ServerSentEvents`, read in the browser with the built-in `EventSource`. Each event is a **hint with no content**, for example `event: changed` / `data: questions`. On a hint the page fetches the fresh HTML for the changed part (or the whole page) with an ordinary `GET`. Pages without script keep working as they do today.

Why this one:

- **Nothing new to ship.** The server API is in the ASP.NET Core shared framework since .NET 10 and `EventSource` is built into every current browser. No package, no vendored JavaScript, no image growth.
- **The proxy needs no new setting** for Caddy, Traefik or nginx with default settings, as long as the app sends `X-Accel-Buffering: no` and a comment line every 15 seconds (details below). SignalR's preferred transport, WebSockets, needs explicit `Upgrade`/`Connection` lines in nginx.
- **Secrecy stays where it is.** Hints carry no names or text, so the stream doesn't need its own secrecy tests. Everything a person sees still arrives as a normal page request, under the authorization and the `PairingSecrecyTests` that cover pages today.
- **Reconnect and catch-up are simple.** `EventSource` reconnects by itself. After a reconnect, the page refetches once, so no missed event needs replaying.
- **It fits the test seams.** A test can open the stream through the existing in-process `TestServer` and read events with `SseParser` (prototyped, below). The keep-alive timer can run on the app's `TimeProvider`, so `app.Clock` drives it.

Trade-offs accepted: one way only (enough, since every action is already a form post); one open request per tab (the HTTP/1.1 per-browser limit of 6, see below); and a little hand-written plumbing that SignalR would provide (fan-out per person, keep-alive, closing on sign-out).

## Options compared

| | SSE (`EventSource`) | SignalR | Polling (`fetch` on a timer) |
|---|---|---|---|
| Server | `TypedResults.ServerSentEvents`, shared framework [1][2] | Shared framework [6] | Plain pages or endpoints |
| Browser code | Built in, Baseline since January 2020 [3] | `@microsoft/signalr` 11.0.0, MIT: `signalr.min.js` is 53,056 bytes (15,108 gzipped), vendored into `wwwroot` because nothing may load from a CDN (§2) [5][14] | Built in |
| Proxy | Must not buffer; idle timeout must be longer than the keep-alive interval | WebSocket upgrade must be forwarded; for the SSE fallback, no buffering [7][8][9] | Nothing |
| Direction | Server to browser | Both ways | Browser asks |
| Latency | Immediate | Immediate | Up to one interval |
| Reconnect | Automatic, with `Last-Event-ID` [4] | `withAutomaticReconnect`: 0, 2, 10 and 30 seconds, then stops; a start failure isn't retried [5] | Next tick |
| Tests | Read the stream through `TestServer` (works, below) | `TestServer` WebSocket client or the .NET SignalR client package in tests | Ordinary page tests |
| Cost on the server | One open request per tab | One connection per tab plus SignalR's buffers (32 KB per connection by default) [10] | One request per tab per interval |

Long polling and raw WebSockets were also considered. Long polling has SSE's proxy concerns plus more requests and hand-written reconnect logic. Raw WebSockets have SignalR's proxy needs without its client library; Microsoft recommends SignalR over raw WebSockets for most apps [11].

## Findings

### The server API (.NET 10 and 11)

- ASP.NET Core 10 added `TypedResults.ServerSentEvents`, which takes an `IAsyncEnumerable<SseItem<T>>`. An `SseItem<T>` carries an event type, an ID and a payload, and can set `ReconnectionInterval` (the `retry` field). It works in Minimal APIs and controllers [1][2].
- "Always return the stream through `TypedResults.ServerSentEvents`." A handler that returns `IAsyncEnumerable<SseItem<T>>` directly sends JSON instead [1].
- Prototype on .NET SDK 11.0.100 with `Microsoft.AspNetCore.TestHost` 11.0.0 (scratch code, not committed):
  - No package was needed for `System.Net.ServerSentEvents` or `TypedResults.ServerSentEvents` in a `Microsoft.NET.Sdk.Web` project.
  - The response carried `text/event-stream` and `Cache-Control: no-store, no-cache`, `Pragma: no-cache`. It didn't set `X-Accel-Buffering`.
  - **The headers weren't sent until the first event.** A client awaiting the headers (`HttpCompletionOption.ResponseHeadersRead`) blocked until an item was yielded. The endpoint should therefore yield something straight away, such as an initial item that sets `retry`, or flush the response before the first event. Otherwise `EventSource` stays in the connecting state and a test waiting for the headers hangs.
  - A test client read the first event with `SseParser.Create(stream).EnumerateAsync()` while the stream stayed open.

### The browser

- `EventSource` is Baseline, widely available since January 2020 [3].
- Reconnecting: the user agent waits for the reconnection time (implementation-defined, "probably in the region of a few seconds", changed by `retry`) and sends `Last-Event-ID` when it reconnects [4].
- A non-200 status or a content type other than `text/event-stream` fails the connection, "Once the user agent has failed the connection, it does not attempt to reconnect." 301 and 307 redirects are followed [4]. So the stream endpoint must answer a missing session with a status, not with Pollyanna's usual redirect to `/auth/login`: `SessionAuthenticationHandler.HandleChallengeAsync` always redirects, and the login page would come back as HTML and silently end the stream. The page's script can then treat a closed `EventSource` as "signed out" and reload.
- The connection limit: "When not used over HTTP/2, SSE suffers from a limitation to the maximum number of open connections… set to a very low number (6)… per browser + domain." Over HTTP/2 the limit is negotiated (100 by default) [3]. The limit is between the browser and the proxy, so it depends on whether the operator's proxy speaks HTTP/2 to browsers. Closing the stream while a tab is hidden keeps the count down either way.
- CSP: `connect-src` (which falls back to `default-src 'self'`) governs `EventSource`, `WebSocket` and `fetch`. MDN notes that "`connect-src 'self'` does not resolve to websocket schemes in all browsers" [12]. A same-origin `EventSource` is covered by Pollyanna's current `default-src 'self'`. SignalR over WebSockets may need the `wss:` origin added to the policy explicitly.

### Phones: background tabs, sleep, the back/forward cache

- Frozen pages: "the browser suspends execution of freezable tasks… JavaScript timers and fetch callbacks don't run." Developers should "tear down any connections" on freeze. "The transition to hidden is also often the last state change that's reliably observable." Discarded tabs run no code at all [13].
- bfcache: some browsers don't cache pages with an open WebSocket ("Chrome (as of 149) and Safari do no block on open WebSockets but other browsers do"). EventSource isn't mentioned. The advice is to close connections on `pagehide` or `freeze` and reopen on `pageshow` or `resume` [15].
- SignalR's own docs warn that tab sleeping "can cause SignalR connections to close", and suggest holding a Web Lock to keep the tab awake [5]. For Pollyanna, letting the tab sleep is the better trade.
- What this means for any transport: close on `visibilitychange` to hidden (and `pagehide`), and reopen on visible (and `pageshow`) **followed by one refetch**. The refetch covers everything missed while asleep, so the server needs no replay buffer. SignalR's stateful reconnect (a server and client buffer, 100,000 bytes by default) [16] wouldn't help across a long sleep anyway.

### The reverse proxy

- **nginx:** `proxy_buffering` is on by default, and "Buffering can also be enabled or disabled by passing `yes` or `no` in the `X-Accel-Buffering` response header field." `proxy_read_timeout` defaults to 60 s, "set only between two successive read operations". `proxy_http_version` defaults to 1.1 since nginx 1.29.7 (1.0 before) [8]. WebSockets need `Upgrade` and `Connection` set explicitly, because they are hop-by-hop headers. An idle WebSocket closes after 60 s unless the server sends pings [9]. Microsoft's SignalR nginx sample sets `proxy_buffering off` for its SSE transport and `proxy_read_timeout 100s` [7].
  So SSE through a default nginx works if the app sends `X-Accel-Buffering: no` and writes something more often than every 60 s. One gap: on nginx older than 1.29.7, the default `proxy_http_version 1.0` may limit long-lived streaming; not tested here.
- **Caddy:** responses are flushed immediately when the response has "`Content-Type: text/event-stream`". Read and write timeouts default to none. WebSockets are tunnelled, but "By default, WebSocket connections are forcibly closed" when the configuration reloads, unless `stream_close_delay` is set [17]. SSE through Caddy needs nothing.
- **Traefik (v3.8 docs):** the entry point's `readTimeout` (60 s) covers reading the request. `writeTimeout` defaults to 0 (no limit) and `idleTimeout` (180 s) covers idle keep-alive connections [18]. Nothing there should cut a streaming response. Not tested.
- The HTML spec's own advice: send "a comment line (one starting with a ':' character) every 15 seconds or so" for proxies that drop idle connections [4]. That one rule covers nginx's 60 s default.
- DEPLOYMENT.md §4 says today: "no WebSockets, no special timeouts or body sizes." With SSE, the app sending `X-Accel-Buffering: no` and a 15-second comment, that sentence can probably stay, plus a line asking operators not to buffer `text/event-stream` responses if their proxy does. With SignalR, it would have to name WebSocket upgrade forwarding.

### Authentication, antiforgery and secrecy

- The stream is a same-origin `GET`, so the browser sends the `__Host-pollyanna` session cookie (`SameSite=Lax`) and the existing session handler authenticates it. Antiforgery protects state-changing posts. A read-only `GET` that changes nothing needs no token, and a cross-origin page can't read the response without CORS, which Pollyanna doesn't enable.
- A long-lived request is authenticated **once**, at the start. SignalR documents the same for its connections: it "captures the authenticated user when a connection is established and caches it for the lifetime of the connection" and "doesn't automatically revalidate the user" [6]. Pollyanna would need to close a person's streams on sign-out, when their session expires and when their account is deleted. Content-free hints limit the damage of a stream that outlives its session, since every real read is a fresh, authenticated request.
- The session's sliding expiry is refreshed only by requests (`RefreshAfter` is one hour), and a long-lived stream is one request. The refetches that follow hints, and normal browsing, keep refreshing it.
- SignalR specifics if it were chosen: CORS doesn't apply to WebSockets, so Microsoft says to check the `Origin` header (`WebSocketOptions.AllowedOrigins`) [11][10]. The default user ID comes from `ClaimTypes.NameIdentifier`, which Pollyanna's session principal doesn't carry (it uses `pollyanna:person`), so an `IUserIdProvider` would be needed [6].
- Logs: `UseAccessLog` writes its line when the request ends, so a stream logs once when it closes, with its route pattern only (§8).

### Single instance, no extra services

- SignalR "requires the same server process handle all HTTP requests for a specific connection". Sticky sessions aren't needed when "Hosting on a single server in a single process". Scaling out needs Azure SignalR Service or a Redis backplane [10]. With one instance and no extra services, SignalR's main advantage, scale-out, doesn't apply.
- An SSE design needs an in-process fan-out: a singleton that keeps a `Channel` per open stream, keyed by person, which the code that changes state (posting a question, sealing a draw) writes hints to after its transaction commits. One instance is all §2 promises, so in-memory is enough. A second instance would need the same kind of backplane.
- Shutdown: an open stream has to end when the app stops (link its cancellation to `IHostApplicationLifetime.ApplicationStopping`), or it can hold up a graceful shutdown. Not measured.

### Testing against the seams

- **In:** a test opens the stream with the signed-in `TestBrowser`'s `HttpClient` and `HttpCompletionOption.ResponseHeadersRead`. The prototype showed `TestServer` streams the response (once the first item is written).
- **Out:** a new observation, "events on the stream", read with `SseParser`. Since hints carry no content, the secrecy assertion is short: the hint a giver gets is the same whatever the pairings are, which extends `PairingSecrecyTests` cheaply. What the person then sees is checked the usual way, on the refetched page.
- **Time:** run the keep-alive comment on the app's `TimeProvider` (`Task.Delay(interval, timeProvider)`), so `app.Clock.Advance` produces a comment deterministically.
- **Browser behavior** (reconnect, visibility, bfcache) isn't covered by the integration tests. Like the reveal's motion today, it needs a manual check on a real phone.
- With SignalR, tests would add `Microsoft.AspNetCore.SignalR.Client` to the test project, or drive `TestServer`'s WebSocket client by hand.

## Open points for later tickets

- How the server learns what changed for whom (where hints are raised, and after which commit), and the secrecy review of who gets which hint (ADR-0001, ADR-0004: a hint about a question must not arrive before the hour the email would).
- Whether a hint triggers a fragment fetch or a full reload, and how the masthead menu fetches its list.
- The DEPLOYMENT.md wording, and whether `help` mentions proxy buffering.

## Sources

1. Microsoft Learn, "Create responses in Minimal API applications", Server-Sent Events section (aspnetcore-11.0, updated November 13, 2026): https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses
2. Microsoft Learn, "What's new in ASP.NET Core 10", Support for Server-Sent Events: https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-10.0
3. MDN, `EventSource`: https://developer.mozilla.org/en-US/docs/Web/API/EventSource
4. WHATWG HTML Standard, Server-sent events: https://html.spec.whatwg.org/multipage/server-sent-events.html
5. Microsoft Learn, "ASP.NET Core SignalR JavaScript client" (install, reconnect, browser sleeping tab): https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client
6. Microsoft Learn, "SignalR authentication and authorization": https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz; "Get started with ASP.NET Core SignalR" ("The SignalR server library is included in the ASP.NET Core shared framework. The JavaScript client library isn't automatically included"): https://learn.microsoft.com/en-us/aspnet/core/tutorials/signalr
7. Microsoft Learn, "ASP.NET Core SignalR production hosting and scaling", Linux with Nginx: https://learn.microsoft.com/en-us/aspnet/core/signalr/scale
8. nginx, `ngx_http_proxy_module`: https://nginx.org/en/docs/http/ngx_http_proxy_module.html
9. nginx, "WebSocket proxying": https://nginx.org/en/docs/http/websocket.html
10. Microsoft Learn, "Security considerations in ASP.NET Core SignalR" (WebSocket origin, buffers): https://learn.microsoft.com/en-us/aspnet/core/signalr/security; sticky sessions and scale-out: source 7
11. Microsoft Learn, "WebSockets support in ASP.NET Core" (SignalR recommended, origin restriction): https://learn.microsoft.com/en-us/aspnet/core/fundamentals/websockets
12. MDN, CSP `connect-src`: https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy/connect-src
13. Chrome for Developers, "Page Lifecycle API": https://developer.chrome.com/docs/web-platform/page-lifecycle-api
14. npm registry metadata for `@microsoft/signalr` (version 11.0.0, MIT); bundle measured from `https://unpkg.com/@microsoft/signalr@11.0.0/dist/browser/signalr.min.js`
15. web.dev, "Back/forward cache": https://web.dev/articles/bfcache
16. Microsoft Learn, "ASP.NET Core SignalR configuration" (keep-alive 15 s, client timeout 30 s, stateful reconnect): https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration
17. Caddy, `reverse_proxy` directive: https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
18. Traefik, EntryPoints reference (v3.8): https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/
