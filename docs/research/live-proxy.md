# What a reverse proxy needs for the live connection

Research for #91, November 27, 2026. Builds on the #83 research (`docs/research/live-transport.md` on the `research/live-transport` branch) and the #90 decisions (one `/live` path, keep-alive, connections tracked by person and event). Throwaway branch; never merged.

## Question

What does a reverse proxy in front of Pollyanna need so the server-sent events stream at `/live` works and stays open: buffering, read and idle timeouts, compression, HTTP/2, and keep-alive spacing, for Caddy, nginx, Traefik, Cloudflare's proxy and Cloudflare Tunnel? What changes in `help`, DEPLOYMENT.md, README.md and `deploy/compose.yaml`, and does the app need a setting?

## Recommendation

**No operator setting, and no required proxy change for default Caddy, nginx, Traefik, Cloudflare or Cloudflare Tunnel setups**, as long as the app does four things on `/live`:

1. **Sends the headers straight away** (yield a first item, such as one that sets `retry`, as #83 found). Every proxy here times out a request that gets no response: nginx after 60 s (504), Cloudflare after 125 s (524).
2. **Writes a comment line every 15 seconds.** That is under the shortest default idle limit found (nginx's 60 s `proxy_read_timeout`) with a wide margin, and well under Cloudflare's 125 s.
3. **Keeps `Content-Encoding: identity`**, which `TypedResults.ServerSentEvents` already sets. It stops Caddy `encode`, nginx `gzip` and Traefik `compress` from compressing the stream (tested below). A hand-written stream would have to set it itself.
4. **Sends `X-Accel-Buffering: no`.** Default nginx passed the stream through promptly without it in testing, but it is the documented switch, and it keeps the stream flowing if an operator's nginx buffers or compresses more than the default.

A fixed 15-second interval is the right default everywhere, so it should be a constant, not a setting. Docs need two short additions to DEPLOYMENT.md (§4 and §9). `help`, README.md and the Compose file can stay as they are.

## Findings

### What the app already sends, and what it doesn't use

- `ServerSentEventsResult.ExecuteAsync` sets `Content-Type: text/event-stream`, `Cache-Control: no-cache,no-store`, `Pragma: no-cache` and **`Content-Encoding: identity`**, then calls `DisableBuffering()` on the response body feature. It doesn't set `X-Accel-Buffering` [1]. (The #83 note listed the first three headers but not `Content-Encoding`.)
- Pollyanna's `Program.cs` uses neither response compression nor request timeouts middleware, and configures no Kestrel limits. Nothing in the app today would buffer, compress or time out the stream.
- If either middleware is added later:
  - Response compression's default MIME types are `application/javascript`, `application/json`, `application/xml`, `text/css`, `text/html`, `text/json`, `text/plain` and `text/xml`, and wildcards like `text/*` aren't supported, so `text/event-stream` isn't compressed unless someone adds it [2].
  - Request timeouts do nothing until a policy is configured. A global `DefaultPolicy` would also cut `/live`, which then needs `.DisableRequestTimeout()` or `[DisableRequestTimeout]` [3].
- Kestrel limits that touch a long response:
  - `KeepAliveTimeout` defaults to 130 s [4]. It's the idle time between requests on a connection, not during a response.
  - `MinResponseDataRate` (240 bytes/s, 5 s grace) is "enforced per write operation": each write gets a timer, and the connection is aborted only if that write doesn't complete in time [5]. A stream that is quiet between writes isn't affected, and a 15-byte comment is a trivial write.
  - `MaxConcurrentConnections` is unlimited by default [6]. Each open tab is one proxy-to-app connection.
  - The proxy reaches Kestrel over plain HTTP on port 8080, and Kestrel's HTTP/2 needs TLS with ALPN [7], so that hop is HTTP/1.1. That's fine: the per-host connection limit is a browser rule, not a proxy one.

### Buffering

- **nginx.** `proxy_buffering` is on by default. "When buffering is disabled, the response is passed to a client synchronously, immediately as it is received." Buffering can be switched per response with `X-Accel-Buffering: yes|no`, unless the operator set `proxy_ignore_headers X-Accel-Buffering` [8]. In the test below, nginx 1.30.8 with default buffering still forwarded the first event and every comment immediately. Buffering only held the stream back when gzip applied (next section).
- **Caddy.** "Responses are flushed immediately to the client" when the response has `Content-Type: text/event-stream`, whatever `flush_interval` says [9].
- **Traefik.** `flushInterval` defaults to 100 ms and "is ignored when ReverseProxy recognizes a response as a streaming response; for such responses, writes are flushed to the client immediately" [10].
- **Cloudflare proxy.** The default "Standard" response body buffering lets Cloudflare features "inspect a prefix of the response body when necessary". "None" streams with no buffering [11]. Inspection "primarily" targets `text/html`, and the docs advise an accurate `Content-Type` for streams [12]. `text/event-stream` qualifies, so nothing is needed. If events ever arrive late through Cloudflare, a Configuration Rule setting Response Body Buffering to None for `/live` is the fix.
- **Cloudflare Tunnel.** "Proxied traffic through Cloudflare Tunnel is buffered by default unless the origin server includes the `Content-Type: text/event-stream` response header" [13]. Pollyanna sends that header.

### Timeouts

| Proxy | Default that matters | Effect on `/live` with a 15 s comment |
|---|---|---|
| nginx | `proxy_read_timeout 60s`, "between two successive read operations" [8] | Fine. A silent stream closed at exactly 60 s in testing, even with `X-Accel-Buffering: no`. |
| Caddy | Transport `read_timeout` and `write_timeout`: no timeout. `stream_timeout`: none [9] | Fine. A silent stream stayed open for 150 s in testing. |
| Traefik v3 | Entry point `readTimeout` 60 s ("reading the entire request, including the body"), `writeTimeout` 0, `idleTimeout` 180 s [14]. The 60 s `readTimeout` arrived in v2.11.2, when the default changed from no timeout [15]. | Fine. On Traefik 3.8.0, a **silent** stream stayed open for 150 s in testing, so the 60 s `readTimeout` doesn't cut a streaming response. |
| Cloudflare proxy | Proxy Read Timeout 125 s (524); Enterprise can raise it. Proxy Write Timeout 30 s. HTTP/1.1 keep-alive and HTTP/2 idle to the client: 400 s [16]. The 524 page: the origin "did not provide an HTTP response before the default 125 seconds" [17]. | Fine. Note that 125 s replaces the often-quoted 100 s. Headers must go out at once. |
| Cloudflare Tunnel | `keepAliveTimeout` 1m30s is for idle pooled connections to the origin, not active requests. With a tunnel, the CDN timeouts above also apply, and "the shorter timeout takes precedence" [18]. | Fine, same as Cloudflare proxy. |

- The HTML standard's own advice is a comment line "every 15 seconds or so" [19]. That's half nginx's default, about an eighth of Cloudflare's, and leaves room for an operator who lowered a timeout to 30 s.
- **Unverified:** Traefik's own docs describe `readTimeout` as covering the request. A forum report about uploads was cut by it [20], and another test on 3.7.13 also saw an idle WebSocket survive 100 s [21]. This research tested only 3.8.0.

### Compression

- **The stream is not compressed through any of the three proxies, because the app sends `Content-Encoding: identity`.** In testing, with `Accept-Encoding: gzip, zstd`:
  - Caddy `encode`, Traefik `compress` and nginx `gzip on; gzip_types text/event-stream;` all passed `Content-Encoding: identity` through and didn't compress.
  - Without that header, all three compressed the stream. Caddy and Traefik still flushed every event. nginx with gzip and default buffering **sent nothing at all, not even headers, for 35 s**. `X-Accel-Buffering: no` fixed that (gzip flushed each event).
- What the proxy docs say:
  - Caddy's default `encode` match includes `text/*` (so `text/event-stream`), but "their headers are sent to the client immediately, and the body is streamed as it is written" for SSE responses [22].
  - nginx's `gzip` is `off` by default, and `gzip_types` defaults to `text/html` [23].
  - Traefik's `compress` middleware has no default `excludedContentTypes`, and its docs don't mention `text/event-stream` [24].
  - Cloudflare's list of compressed content types doesn't include `text/event-stream`, and `Cache-Control: no-transform` turns its compression off [25].
- So compression needs nothing from operators, as long as the endpoint keeps using `TypedResults.ServerSentEvents` (or sets `Content-Encoding: identity` and `X-Accel-Buffering: no` itself).

### HTTP/2 and HTTP/3

- Over HTTP/1.1, browsers allow 6 connections per host, and each open `EventSource` holds one. Over HTTP/2 the limit is the negotiated stream count (100 by default) [26]. With six Pollyanna tabs open over HTTP/1.1, a seventh tab can't load **any** page from the host until a stream closes, not just its own stream. Closing the stream when a tab is hidden (#83) helps a lot but doesn't cover several visible windows.
- What browsers get by default:
  - Caddy: HTTP/1.1, HTTP/2 and HTTP/3 (`protocols` default `h1 h2 h3`) [27].
  - Traefik: HTTP/2 on TLS entry points, with `http2.maxConcurrentStreams` 250. HTTP/3 is opt-in [14].
  - Cloudflare: HTTP/2 on by default for all plans, and Free plans can't turn it off [28].
  - nginx: **`http2 off` by default** [29]. Only nginx operators need to do anything.
- HTTP/3 changes nothing for SSE beyond what HTTP/2 gives.

## Proposed doc and config changes

Minimal, for the ticket that ships `/live`:

**DEPLOYMENT.md §4, "The reverse proxy".** Replace "Nothing else is needed: no forwarded headers (they are ignored), no WebSockets, no special timeouts or body sizes." with:

> Nothing else is needed: no forwarded headers (they are ignored), no WebSockets, no special timeouts or body sizes. Live updates use one long-lived request per open tab, to `/live` (server-sent events). Pollyanna writes to it every 15 seconds and marks it unbuffered and uncompressed, so default Caddy, nginx, Traefik and Cloudflare setups pass it through. If you changed your proxy's defaults, keep its read or idle timeout above 15 seconds and don't buffer `text/event-stream` responses. Serve HTTP/2 to browsers if your proxy can: over HTTP/1.1 a browser opens at most six connections to a host, and each open Pollyanna tab holds one.

and in the nginx example, add one comment line:

```nginx
# nginx (inside the server block for pollyanna.example with your TLS settings;
# add "http2 on;" there too)
```

**DEPLOYMENT.md §9, Troubleshooting.** One entry:

> **Pages don't update by themselves.** Pages still work; reload to see changes. Check that the browser's request to `/live` stays open (developer tools, Network). If it ends after about 60 seconds or never shows an event, your proxy is buffering or timing it out. For nginx, check `proxy_ignore_headers` doesn't list `X-Accel-Buffering`. Behind Cloudflare, set Response Body Buffering to None for `/live`.

**`help`.** No change. It describes settings, mounts and the draw key, and points to DEPLOYMENT.md for the proxy. Nothing here is a setting.

**README.md.** No change. "What you need" already says "A reverse proxy that terminates TLS", and the examples link goes to §4.

**`deploy/compose.yaml`.** No change. It only publishes port 8080. It has no proxy in it and nothing in it affects the stream.

## Does the app need a setting?

**No.** The keep-alive interval is the only candidate. 15 s is below every default idle limit found (nginx 60 s, Cloudflare 125 s; Caddy and Traefik have none). The only proxy that would cut it is one an operator set below 15 s, which is unusual and better fixed in the proxy than in Pollyanna. A shorter interval costs a little more traffic per tab and buys nothing for the defaults. Keep it a constant run on the app's `TimeProvider`, so tests can drive it.

Implementation notes for the `/live` ticket (not operator-facing):

- Yield a first item at once, so headers reach the proxy before any read timeout.
- Add `X-Accel-Buffering: no`.
- Keep `TypedResults.ServerSentEvents`, which brings `Content-Encoding: identity`.
- If response compression or a global request timeout policy is ever added, exempt `/live`.

## Method: the proxy test

Run on November 27, 2026, with Docker and host networking:
- Two small SSE origins: one sent the first event and then nothing, the other a comment every 15 s. Query flags switched on `X-Accel-Buffering: no` and `Content-Encoding: identity`.
- The proxies: Traefik 3.8.0 (default entry point, file provider, one route with `compress`), Caddy 2.11.9 (`reverse_proxy`, and a site with `encode`), and nginx 1.30.8 (DEPLOYMENT.md's example, and a server with `gzip on; gzip_types text/event-stream;`).
- `curl -N` with a 150-second cap timed each line.

Results:
- Silent stream: closed at 60 s by nginx (with or without `X-Accel-Buffering: no`); open at 150 s through Caddy and Traefik.
- 15-second stream: open at 150 s through all three, every comment on time, including through nginx with default buffering.
- Compression: as described under Compression.

Cloudflare and cloudflared weren't tested; their rows rely on the documentation alone.

## Sources

1. `ServerSentEventsResult.cs`, dotnet/aspnetcore `main` (same lines on `release/10.0`): https://github.com/dotnet/aspnetcore/blob/main/src/Http/Http.Results/src/ServerSentEventsResult.cs
2. Microsoft Learn, "Response compression in ASP.NET Core", MIME types: https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression
3. Microsoft Learn, "Request timeouts middleware in ASP.NET Core": https://learn.microsoft.com/en-us/aspnet/core/performance/timeouts
4. .NET API, `KestrelServerLimits.KeepAliveTimeout` ("Defaults to 130 seconds"): https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.keepalivetimeout
5. .NET API, `KestrelServerLimits.MinResponseDataRate`: https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.minresponsedatarate
6. Microsoft Learn, "Configure options for the ASP.NET Core Kestrel web server": https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options
7. Microsoft Learn, "Use HTTP/2 with the ASP.NET Core Kestrel web server" (requires ALPN and TLS 1.2): https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/http2
8. nginx, `ngx_http_proxy_module` (`proxy_buffering`, `X-Accel-Buffering`, `proxy_read_timeout`, `proxy_ignore_headers`): https://nginx.org/en/docs/http/ngx_http_proxy_module.html
9. Caddy, `reverse_proxy` directive (`flush_interval`, transport timeouts, `stream_timeout`): https://caddyserver.com/docs/caddyfile/directives/reverse_proxy
10. Traefik, HTTP service load balancing, `responseForwarding.flushInterval`: https://doc.traefik.io/traefik/reference/routing-configuration/http/load-balancing/service/
11. Cloudflare, Configuration Rules settings, Response Body Buffering: https://developers.cloudflare.com/rules/configuration-rules/settings/
12. Cloudflare, "Response body inspection", Streaming considerations: https://developers.cloudflare.com/rules/configuration-rules/response-body-inspection/
13. Cloudflare Tunnel, common errors ("Cloudflare Tunnel is buffering my streaming response"): https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/troubleshoot-tunnels/common-errors/
14. Traefik, EntryPoints reference (`respondingTimeouts`, `http2`, `http3`): https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/
15. Traefik v2.11 migration guide, v2.11.2 (`readTimeout` default 60 s, previously none): https://doc.traefik.io/traefik/v2.11/migration/v2/
16. Cloudflare, Connection limits: https://developers.cloudflare.com/fundamentals/reference/connection-limits/
17. Cloudflare, Error 524: https://developers.cloudflare.com/support/troubleshooting/http-status-codes/cloudflare-5xx-errors/error-524/
18. Cloudflare Tunnel, origin parameters: https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/cloudflared-parameters/origin-parameters/
19. WHATWG HTML Standard, Server-sent events, authoring notes: https://html.spec.whatwg.org/multipage/server-sent-events.html
20. immich-app/immich discussion #8872 (uploads cut at 60 s by Traefik's `readTimeout`): https://github.com/immich-app/immich/discussions/8872
21. mcollovati/vaadin-reverse-proxy-tests PR #26 (Traefik 3.7.13: slow request body and idle WebSocket not cut at 60 s): https://github.com/mcollovati/vaadin-reverse-proxy-tests/pull/26
22. Caddy, `encode` directive: https://caddyserver.com/docs/caddyfile/directives/encode
23. nginx, `ngx_http_gzip_module`: https://nginx.org/en/docs/http/ngx_http_gzip_module.html
24. Traefik, Compress middleware: https://doc.traefik.io/traefik/reference/routing-configuration/http/middlewares/compress/
25. Cloudflare, Content compression: https://developers.cloudflare.com/speed/optimization/content/compression/
26. MDN, `EventSource`: https://developer.mozilla.org/en-US/docs/Web/API/EventSource
27. Caddy, global options (`protocols`, default `h1 h2 h3`): https://caddyserver.com/docs/caddyfile/options
28. Cloudflare, HTTP/2: https://developers.cloudflare.com/speed/optimization/protocol/http2/
29. nginx, `ngx_http_v2_module` (`http2 off` default): https://nginx.org/en/docs/http/ngx_http_v2_module.html
