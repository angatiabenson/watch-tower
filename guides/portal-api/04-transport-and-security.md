# 04 Transport and security

[Portal API guide](README.md) › Transport and security

## HTTPS

The agent uses .NET's `HttpClient` with a `SocketsHttpHandler` (`PortalTls.CreateHandler`), named
`PortalClient`, with the auth handler on top. What that means for a portal:

| Aspect | Agent behaviour | Portal requirement |
| --- | --- | --- |
| HTTP version | HTTP/1.1 | MUST serve HTTP/1.1 (HTTP/2-only endpoints don't work with the HTTPS transport) |
| TLS versions | Whatever Windows (Schannel) negotiates: TLS 1.2, and TLS 1.3 on Windows Server 2022 and later | MUST support TLS 1.2; SHOULD support TLS 1.3 |
| Server certificate | Validated against the Windows certificate store of the server (chain and host name). Revocation is not checked. Validation can't be turned off for HTTPS (`Tls:AllowUntrustedCert` is ignored there) | MUST present a certificate that chains to a root the server trusts, for the exact host name in BaseUrl |
| Client certificate (mutual TLS) | Optional: `Tls:ClientCertThumbprint` selects a certificate (with private key) from `LocalMachine\My`, presented on every connection | MAY require mTLS; it is an extra factor on top of the bearer token, not a replacement |
| Connection reuse | Keep-alive; pooled connections are recycled every 5 minutes | Keep-alive is fine; no sticky sessions needed |
| Timeout | 30 s per request, including the response body | SHOULD answer well within 30 s; a timeout counts as a failure and is retried |
| Request bodies | `Content-Type: application/json; charset=utf-8`, **chunked** (no `Content-Length`) | MUST accept chunked request bodies (check your reverse proxy and WAF) |
| Response bodies | Read as UTF-8 JSON; the `Content-Type` is not checked | SHOULD send `application/json`; MUST NOT compress (the agent sends no `Accept-Encoding` and does not decompress) |
| Redirects | Never followed (all transports). Any 3xx, 304 included, fails the call: POSTs count as not delivered (batches and events retried with backoff, results every poll cycle, acknowledgements never), `GET commands/pending` counts as no commands, `GET config` as no change. A warning names the target, at most once per 15 min per endpoint | MUST NOT redirect agent API requests (for example http→https or a trailing slash); publish the final URL as BaseUrl |
| Credentials scope | The bearer token and `X-Agent-Id` are only sent to URLs below BaseUrl (same scheme, host, port and path prefix); anything else is refused and logged | Serve the whole API below the published BaseUrl |
| Proxies | The .NET default proxy: `HTTPS_PROXY` / `HTTP_PROXY` / `NO_PROXY` environment variables of the service, otherwise the Windows proxy settings of the service account. No agent-specific proxy setting | Nothing; mention it to users behind proxies |
| User-Agent | None sent | Identify agents by credentials and `agent.version` in batches, not by headers |

### Certificate pinning

The agent does not support certificate or public-key pinning, and it cannot restrict which issuer
may vouch for your host name: it accepts any certificate that chains to a root the server trusts.
For a tighter trust relationship, a portal can:

- use a certificate from a private CA installed on your customers' servers. That makes your
  certificate trusted there without a public CA, but it is NOT pinning: every other root the
  server trusts can still vouch for your host name.
- require mutual TLS: the portal pins the *agent's* client certificate thumbprint per agentId.
- keep the bearer token as the authority: a stolen certificate alone is not enough to talk to
  your portal, and a stolen token alone is not enough when you require mTLS.

## Plain HTTP (opt-in)

`Tls:RequireTls` is `true` by default. With it, a BaseUrl that is not `https://` is an invalid
configuration: the console, `enroll` and the installer refuse to save it, and the service refuses
to start with it. The token and the commands can't travel in clear by accident. An operator can set `RequireTls = false` (or use an enrollment
code with `"requireTls": false`) to allow `http://`.

Risks of plain HTTP. Anyone on the path can:

- read the token and impersonate the agent,
- read everything the agent reports (server names, service names, endpoint URLs),
- inject commands (stop services, push configuration, trigger updates of the agent), because
  commands are authenticated only by the transport.

A portal SHOULD only issue `requireTls: false` codes for lab or loopback use, and SHOULD show a
warning next to agents that report over plain HTTP. (The portal can tell from its own listener,
because the agent does not report it.)

## BaseUrl, path prefixes and reverse proxies

The agent builds every URL as `BaseUrl` with trailing slashes removed, then `/` and the API path:

| BaseUrl | Metrics URL |
| --- | --- |
| `https://portal.example.com` | `https://portal.example.com/api/v1/agent/metrics/batch` |
| `https://portal.example.com/` | `https://portal.example.com/api/v1/agent/metrics/batch` |
| `https://example.com/watchtower` | `https://example.com/watchtower/api/v1/agent/metrics/batch` |
| `https://example.com:8443/tools/wt/` | `https://example.com:8443/tools/wt/api/v1/agent/metrics/batch` |

`commandId` values are percent-encoded in the path (`Uri.EscapeDataString`). Query strings are
only used by `GET config?version=…` (percent-encoded too).

Behind a reverse proxy, keep the prefix stable, pass `Authorization` and `X-Agent-Id` through
unchanged, allow chunked request bodies, and don't buffer responses for longer than the 30 s
timeout.

## Size limits

| Item | Default | Bound |
| --- | --- | --- |
| CPU samples per batch | about 6 (one per 5 s per 30 s flush) | `Outbox:MaxBatchSize` (default 500) |
| RAM samples per batch | about 6 | `Outbox:MaxBatchSize` |
| Disk, queue, NIC, service, pool, endpoint entries | one per resource | number of resources (watched lists hold at most 200 names each when set remotely) |
| `topProcesses` per event | 3 (CPU) or 5 (RAM) | `Monitoring:Cpu:TopProcessCount` / `Monitoring:Ram:TopProcessCount` |

A default batch is a few kilobytes. After an outage, batches stay small, because each holds 30 s
of samples, but there are many of them. A portal SHOULD accept request bodies of at least 1 MB
and MAY reject more. A rejection (413) is a failure to the agent: the batch is retried and dropped
after five attempts. MinimalPortal allows 16 MB.

## Rate limiting and backpressure

The agent sends one request at a time per loop. The outbox loop sends its queued messages one
after another and stops at the first failure until the next cycle (30 s). A portal can therefore
slow an agent down by answering `429` or `503`. The agent doesn't read `Retry-After`. The refused
message waits for its own backoff (a metrics batch at least `RetryBackoffSeconds`, 60 s by
default, for the first retry; an event 30 s, doubling) and is retried on the first cycle after
that; other messages that are due can be sent in the meantime. See
[Reliability](10-reliability.md) for what that does to queued data.

## Security checklist for portal authors

- [ ] TLS everywhere; HTTP only for explicit lab setups; HSTS on the portal host.
- [ ] Authenticate `X-Agent-Id` + bearer token on every agent request; 401 otherwise.
- [ ] Store token hashes, compare in constant time, never log tokens or full `Authorization` headers.
- [ ] Derive tenant and agent identity from the credentials only; never from `agentId`, `machineName` or other body fields.
- [ ] Treat everything an agent sends as untrusted input: validate types and sizes, cap array lengths, escape descriptions, names and `detail` text before rendering them in HTML (an agent can be compromised, or an operator can name a service `<script>`).
- [ ] Limit request body size, request rate and concurrent connections per agent and per IP address.
- [ ] Authorize every command a user issues (who may restart what on which server), log who issued it, and show the agent's result. The agent's local allow-list is a second line of defence, not a replacement.
- [ ] Issue commands only with fresh `issuedAt` and unique `commandId`s; never reuse IDs.
- [ ] Validate remote configuration with the agent's rules before storing it (see [Remote configuration](09-remote-config.md)).
- [ ] Protect enrollment codes like passwords: show once, expire unused ones, rotate on suspicion.
- [ ] Audit log: enrollment, rotation, revocation, every command and its result, every configuration version and its acknowledgement.
- [ ] Segregate tenants in storage; an agent's credentials MUST only ever reach its own tenant's data.
- [ ] Have a revocation path that works without the agent's cooperation (delete the hash).
- [ ] Keep the API on a separate host or path from your user UI, so a WAF or proxy rule for agents (chunked bodies, no redirects, bearer tokens) does not weaken the UI.

Next: [Endpoints](05-endpoints/README.md).
