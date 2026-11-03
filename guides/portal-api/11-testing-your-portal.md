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
- [ ] MUST accept unknown enum values (`incidentType`, `status`, `updatePhase`, `type`).
- [ ] MUST accept requests where optional properties are absent (1.0.x agents).
- [ ] MUST accept any RFC 3339 offset (`Z`, `+00:00`, `+03:00`) and up to 7 fractional digits.
- [ ] MUST send camelCase property names in responses.

### metrics/batch and events

- [ ] MUST answer 2xx when the data is stored, and to duplicates.
- [ ] MUST deduplicate on `batchId` and `eventId`.
- [ ] SHOULD deduplicate series samples on `(agent, series, timestamp)` (covers batches without `batchId`).
- [ ] MUST accept late data and keep the newest snapshot per resource.
- [ ] MUST order events by `timestamp`, not arrival.
- [ ] SHOULD implement the alert lifecycle by `alertKey`/`resolved`, including escalation, out-of-order close and retirement.
- [ ] SHOULD accept bodies of at least 1 MB.

### Commands

- [ ] MUST answer `GET commands/pending` with 204 or a JSON array (camelCase, `actionType` a number, `parameters` string values only).
- [ ] MUST give every command a unique `commandId` and a current UTC `issuedAt`.
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
# every endpoint once; exit code 0 = all checks passed (CI-friendly)
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
  [PASS] POST metrics/batch accepted (2xx)
  [PASS] Same batchId again answered 2xx (deduplicate, do not reject)
  [PASS] Batch from a 1.0.x agent (no batchId, agent, endpointStatuses) accepted
  [PASS] Unknown JSON properties ignored (forward compatibility)
  [PASS] POST events accepted (alert opened)
  [PASS] Same eventId again answered 2xx
  [PASS] Alert resolved event accepted
  [PASS] One-shot event accepted (alertKey reboot, no resolved)
  [PASS] Incident without alert accepted (no alertKey)
  [PASS] GET commands/pending answered (200 array or 204) - 2 command(s) received and handled
  [PASS] Command results accepted
  [PASS] GET config answered (200 or 204) - applied version agent-01.1
  [PASS] POST config/acknowledge accepted
  [PASS] GET config?version=<current> answered 204
  [PASS] Wrong token rejected

All checks passed.
```

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
| `AgentPayloadSchemaTests` | every request body the agent's real `HttpsPortalClient` sends (fully populated and minimal) validates against the OpenAPI schemas, with unknown properties forbidden, so a new agent field without a spec update fails the build |
| `SpecPathsTests` | the set of `(method, path)` the client calls equals the spec's paths |
| `SpecExamplesTests` | every example in the spec validates against its schema, and the agent parses the spec's command and config examples |
| `DocsExamplesTests` | every JSON and HTTP example in these pages validates against the schema it is labelled with, and every `WT1.` code decodes with the agent's parser |
| `MinimalPortalEndToEndTests` | the agent's real client and its real outbox, command and config loops talk to MinimalPortal in-process: metrics, events, a command round-trip, a config fetch and acknowledgement, duplicates, bad tokens |

Labelling convention for examples in these pages: a line `<!-- schema: Name -->` (or `Name[]` for an
array) right before a ` ```json ` or ` ```http ` block. JSON that is not an API payload is labelled
`<!-- schema: none (reason) -->`.
