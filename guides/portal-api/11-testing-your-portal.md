# 11 Testing your portal

[Portal API guide](README.md) › Testing your portal

Three tools, from the cheapest to the most realistic:

1. the **conformance checklist** below;
2. the **AgentSimulator** (any OS) against your portal: it sends exactly what the agent sends;
3. a **real agent** on a Windows server (or VM) paired with your portal.

The repository's own **contract tests** keep the spec, the docs, MinimalPortal and the agent in
sync. Run them if you change any of them.

## Conformance checklist

### Transport and authentication

- [ ] MUST serve HTTP/1.1 over TLS 1.2+ with a certificate the agent's Windows trusts (or plain HTTP only for `requireTls: false` lab setups).
- [ ] MUST accept chunked request bodies without `Content-Length`.
- [ ] MUST NOT redirect agent API requests, and MUST NOT compress responses.
- [ ] MUST serve the API under `BaseUrl` + `/api/v1/agent/` including any path prefix.
- [ ] MUST authenticate `X-Agent-Id` + `Authorization: Bearer` on every request and answer 401 on failure.
- [ ] MUST take the agent identity from the credentials only.
- [ ] SHOULD store token hashes and compare in constant time.
- [ ] SHOULD answer within a few seconds (the agent gives up after 30 s).

### JSON

- [ ] MUST ignore unknown properties in every request body.
- [ ] MUST accept unknown values of the open value sets (`incidentType`, `actionType`, `status`, `updatePhase`, `type`). The spec lists the known values in `x-extensible-enum`; they are not closed enums.
- [ ] MUST accept requests where optional properties are absent (1.0.x agents).
- [ ] MUST accept any RFC 3339 offset (`Z`, `+00:00`, `+03:00`) and up to 7 fractional digits.
- [ ] MUST send camelCase property names in responses.

### metrics/batch and events

- [ ] MUST answer 2xx when the data is stored, and to duplicates.
- [ ] MUST deduplicate on `batchId` and `eventId`.
- [ ] SHOULD deduplicate series samples on `(agent, series, timestamp)` (covers batches without `batchId`).
- [ ] MUST accept late data and keep the newest snapshot per resource.
- [ ] SHOULD show snapshot entries with `isStale: true` as stale, and MUST treat a missing `isStale` as unknown (fall back to the `timestamp`), never as fresh.
- [ ] MUST order events by `timestamp`, not arrival.
- [ ] SHOULD implement the alert lifecycle by `alertKey`/`resolved`, including escalation, out-of-order close and retirement.
- [ ] SHOULD accept bodies of at least 1 MB.

### Commands

- [ ] MUST answer `GET commands/pending` with 204 or a JSON array (camelCase, `actionType` a number, `parameters` string values only).
- [ ] MUST give every command a globally unique `commandId` that is never reused, and SHOULD give it a current UTC `issuedAt` (REQUIRED in v2, and already required by agents with `RequireCommandIssuedAt`), generated when the command is created.
- [ ] SHOULD return each command until acknowledged, then stop.
- [ ] SHOULD expire unacknowledged commands before 60 minutes.
- [ ] MUST answer 2xx to acknowledgements and results, including duplicates and unknown IDs.
- [ ] SHOULD keep the first result per command.
- [ ] SHOULD offer service and pool actions only for names the agent reports.

### Configuration

- [ ] MUST answer 204 when `version` matches the current version, else 200 `{config, version}` with both non-null.
- [ ] MUST create a new version for every change; a version never changes content.
- [ ] SHOULD validate configurations with the agent's rules before publishing.
- [ ] MUST answer 2xx to `config/acknowledge`, including unknown versions.
- [ ] SHOULD show acknowledged versus published version per agent.

### Operations

- [ ] SHOULD treat an agent as stale after ~3 flush intervals without any request.
- [ ] SHOULD suppress paging during `agent.maintenance.active`.
- [ ] SHOULD warn when an agent's clock is more than a few minutes off.
- [ ] MUST have a way to revoke an agent's token.

## AgentSimulator

[`samples/AgentSimulator`](../../samples/AgentSimulator/README.md) runs on Linux, macOS and
Windows with the .NET 10 SDK. It compiles the agent's own DTOs, `HttpsPortalClient`, auth handler,
TLS policy, enrollment-code parser and configuration validator, so its requests are the agent's
requests.

```bash
# every endpoint once, checking the real status codes. Queue a command (e.g. 301) and push a
# configuration for the agent first: without them those checks are SKIPped.
# Exit code 0 = all passed, 1 = a check failed, 3 = incomplete (skipped checks, no failure).
dotnet run --project samples/AgentSimulator -- --code "$WT_CODE" --scenario smoke

# one example of every incident type and alert shape
dotnet run --project samples/AgentSimulator -- --code "$WT_CODE" --scenario alerts

# duplicates and an out-of-order close; then check your portal stored each item once
dotnet run --project samples/AgentSimulator -- --code "$WT_CODE" --scenario duplicate

# two hours of outage backlog replayed as fast as your portal accepts it
dotnet run --project samples/AgentSimulator -- --code "$WT_CODE" --scenario outage --backlog-minutes 120

# a live agent for 10 minutes; stop and start your portal meanwhile
dotnet run --project samples/AgentSimulator -- --code "$WT_CODE" --scenario portal-down

# a live agent that "has" an update; queue commands 101-501 in your portal while it runs
dotnet run --project samples/AgentSimulator -- --code "$WT_CODE" --scenario run --duration 300 --available-version 1.7.0
```

Smoke output:

```text
  [PASS] POST metrics/batch accepted (2xx) - HTTP 202
  [PASS] Same batchId again answered 2xx (deduplicate, do not reject) - HTTP 202
  [PASS] Batch from a 1.0.x agent (no batchId, agent, endpointStatuses) accepted - HTTP 202
  [PASS] Unknown properties and enum values ignored (forward compatibility) - HTTP 202
  [PASS] POST events accepted (alert opened) - HTTP 202
  [PASS] Same eventId again answered 2xx - HTTP 202
  [PASS] Alert resolved event accepted - HTTP 202
  [PASS] One-shot event accepted (alertKey reboot, no resolved) - HTTP 202
  [PASS] Incident without alert accepted (no alertKey) - HTTP 202
  [PASS] Unknown incident type accepted - HTTP 202
  [PASS] GET commands/pending answered 200 (JSON array) or 204 - HTTP 200, 1 command(s)
  [PASS] Pending command is fresh (issuedAt) - 2026-11-04T00:40:47.4261882Z
  [PASS] POST commands/{id}/acknowledge accepted - HTTP 204
  [PASS] POST commands/{id}/result accepted - HTTP 204
  [PASS] Same result again answered 2xx - HTTP 204
  [PASS] GET config answered 200 ({config, version}) or 204 - HTTP 200, version agent-01.1
  [PASS] Configuration passes the agent's validation
  [PASS] POST config/acknowledge accepted - HTTP 204
  [PASS] GET config?version=<current> answered 204 - HTTP 204
  [PASS] Wrong token answered exactly 401 - HTTP 401

All checks passed.
```

Without a queued command or a pushed configuration, the checks that need them are reported as
`[SKIP] ... - queue a command for this agent first ...` and the run ends with `INCOMPLETE` and exit
code 3: a portal that never returns commands or configuration cannot pass. A poll or fetch that
answers anything but 200 or 204 (or a 200 the agent cannot parse) is a FAIL, and so is a wrong
token answered with anything but exactly 401.

The simulator can't see inside your portal. Check the stored results yourself, especially after
`duplicate` (one batch, two events, alert closed).

## MinimalPortal

[`samples/MinimalPortal`](../../samples/MinimalPortal/README.md) is the reference portal. Use it to
see what "correct" looks like, or to compare responses with yours:

```bash
dotnet run --project samples/MinimalPortal -- --urls http://0.0.0.0:5080 --PublicUrl http://devbox.lan:5080
```

It prints an enrollment code, and `GET /admin/agents/{id}` (with the printed admin key) shows
everything it stored.

## A real agent

On a Windows Server VM: install WatchTower with your enrollment code
(`install.ps1 -EnrollmentCode WT1.…`), then:

- watch your portal receive the first batch within a minute;
- stop a watched service (`Stop-Service Spooler`) and watch ServiceDown, then ServiceRestarted;
- queue a ForceMetricSnapshot (301) and see a batch arrive at once;
- push a configuration with `cpu.thresholdPercent: 1` and watch a CPU spike event, then an alert
  after `sustainedSeconds`;
- block the portal in the VM firewall for 10 minutes, then unblock it and watch the replay;
- read the agent log (`%ProgramData%\WatchTower\Logs\watchtower-YYYYMMDD.log`) for warnings about
  your responses.

## The repository's contract tests

`tests/WatchTower.Tests/PortalContract` (run with
`dotnet test --project tests/WatchTower.Tests/WatchTower.Tests.csproj`, on Windows) checks:

| Test | Guarantees |
| --- | --- |
| `AgentPayloadSchemaTests` | every request body the agent's real `HttpsPortalClient` sends (fully populated and minimal) validates against the OpenAPI schemas in strict mode (unknown properties forbidden, extensible enums limited to their known values); every wire DTO has exactly the properties of its schema and the same required set; every `x-extensible-enum` lists exactly the values the agent produces (endpoint `status` includes `ConfigurationError`, whose shape is pinned against the agent's own marking); the published schemas accept unknown properties and values. A new agent field or value without a spec update fails the build |
| `SpecPathsTests` | the set of `(method, path)` the client calls equals the spec's operations, with and without a BaseUrl path prefix; credentials and query parameters are the documented ones; command IDs are percent-encoded into one path segment |
| `SpecExamplesTests` | every request, response, schema and parameter example in the spec validates strictly against its schema, every component example is used, and the agent parses the spec's command and config examples |
| `DocsExamplesTests` | every ```` ```json ```` block in these pages is labelled and validates strictly against its schema; every ```` ```http ```` request line is a spec operation with the headers the agent really sends; every `WT1.` code decodes with the agent's parser |
| `AgentBehaviourContractTests` | the agent behaviour these pages promise, against the real client and command loop: `issuedAt` limits (the exact limits accepted), the `Rejected: ...` result of a stale, future or unparseable command, and of one without `issuedAt` under `RequireCommandIssuedAt` (validated against `CommandResult`, never acknowledged, reported once per `commandId`), the one-time warning for commands without `issuedAt`; a known `commandId` acknowledged but never executed again, even after its record was purged; the result retry backoff (1, 2, 4, 8, 16 min) and the give-up after `ResultRetryMaxHours`; the `Interrupted: ...` result of a command cut short by a restart, and a stored command that never started running after the restart or expiring with `Rejected: command expired before it could run (agent restarted)`; the documented defaults of those limits; every operation treating a 3xx as failure without following it, the redirect warning text and its 15-minute rate limit, and credentials sent only below BaseUrl |
| `SnapshotStalenessContractTests` | the `isStale` rule on the wire: a batch from the agent's real outbox loop marks every snapshot entry older than its window `isStale: true` and every other one `false` (each kind, long endpoint intervals, `ConfigurationError`, a 100-day-old drive reading), over HTTPS (validated strictly) and gRPC (a real HTTP/2 call decoded with the proto); the proto field numbers (new, optional, never reused); the spec's wording of the rule, windows and fallback; the spec example; a batch queued without `isStale` stays without it on both transports |
| `MinimalPortalEndToEndTests` | the agent's real client, and its real outbox, command and remote-config loops, against MinimalPortal in-process (at the root and below a path prefix): metrics, events and the alert lifecycle, a command round-trip, a config fetch, apply and acknowledgement, duplicates, an endpoint entry turning `ConfigurationError` (kept against a late older batch), a stale entry (shown stale, kept against a late older batch, judged from the timestamp without `isStale`), a wrong token, a BaseUrl missing the prefix |

Labelling convention for examples in these pages: a line `<!-- schema: Name -->` (or `Name[]` for an
array, `Name` being a `components/schemas` entry) right before a ` ```json ` block, or before a
` ```http ` block that has a body. Every ` ```json ` block needs a label. JSON that is not an API
payload (settings files, admin APIs) opts out with `<!-- schema: none (reason) -->`; the reason is
mandatory and the block must still be valid JSON. Use another fence language (` ```text `) for
pseudo-JSON with comments or `...`.
