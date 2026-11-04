# 12 Versioning and compatibility

[Portal API guide](README.md) › Versioning and compatibility

## The v1 promise

The protocol version is the path segment: `/api/v1/agent/`. **v1 is stable.** For as long as an
agent speaks v1:

- the seven endpoints, their methods and paths stay as they are;
- existing JSON properties keep their name, type and meaning;
- the meaning of status codes stays "2xx = done, anything else = retry";
- incident type numbers and action type numbers are never reused or renumbered;
- the enrollment code prefix `WT1.` and payload version `v: 1` keep their format.

### What may change within v1 (additive only)

| Change | Portal impact if it follows these docs |
| --- | --- |
| New optional properties in agent requests | none: unknown properties are ignored |
| New values of open enums (`incidentType`, `status`, `updatePhase`, endpoint `type`, alert key prefixes) | none: unknown values are accepted |
| New action types | none until the portal sends them; older agents answer `Unknown action type` |
| New optional properties in portal responses that the agent understands (commands, config) | none: older agents ignore them |
| New remote-config fields | none; older agents ignore them |
| Opt-in request encodings (`Content-Encoding: gzip`, after 1.0.20) | none unless the operator, or your enrollment code's `acceptsGzip`, turns it on; a portal that can't read it answers 415 and the agent falls back |
| Behaviour changes inside the agent (thresholds, retry timings, descriptions) | none; they are not part of the contract |
| Stricter agent-side checks of what a portal sends (command freshness, never re-executing a known command ID, configuration validation, the command allow-list, refusing redirects) | none for a portal that follows these docs; one that relied on the old leniency sees failed results, rejected configurations or failed deliveries. Listed under "Changed" in the [CHANGELOG](CHANGELOG.md) |

The agent MUST NOT start depending on a new endpoint, or on a new response property, within v1.
Anything a portal must implement to keep working is a breaking change.

### What is breaking (only in v2)

Removing or renaming a property or endpoint, changing a type, unit or meaning, making a portal
response property required, giving a status code a new meaning, or requiring a new endpoint.

### v2 and the transition

A breaking change creates `/api/v2/agent/`, with its own OpenAPI file
(`spec/portal-agent-api.v2.openapi.yaml`) and a guide section. During the transition, released
agents keep speaking v1 by default, v2 is opt-in per agent, and a portal can serve both paths
side by side from the same storage. The timeline is announced in the [CHANGELOG](CHANGELOG.md)
before v1 support is removed from any agent release. v1 is never changed into v2 in place.

### Deprecation

A v1 property that is superseded stays in every request for the rest of v1. It is marked
`deprecated: true` in the OpenAPI file and listed in the CHANGELOG with its replacement, and only
disappears in v2.

## Detecting agent version and capabilities

There is no capability negotiation. Use:

- `agent.version` in every batch from current agents (semantic version; `0.0.0-dev` and other
  prerelease suffixes for development builds);
- **no `agent` object at all** means an agent up to 1.0.19: 1.0.19 when the batch has a
  `batchId`, 1.0.18 or older when it hasn't;
- the presence of a property, rather than version arithmetic, when you can (for example
  `endpointStatuses`, `alertKey`), checking each property on its own. A missing `isStale`
  means unknown: fall back to the entry's `timestamp`
  ([Staleness](06-telemetry-reference.md#staleness-isstale)). Development builds from before
  the 1.0.20 release may send an `agent` object without `isStale`.

## Feature compatibility

`base` = every released v1 agent. `1.0.19` = added in 1.0.19 (released 2026-06-15). `1.0.20` =
added in 1.0.20 (released 2026-11-04). `after-1.0.20` = added after 1.0.20 and not released
yet: builds from the current source, then the first release after 1.0.20 and later; not sent
by 1.0.20 or earlier.

| Feature | ≤ 1.0.18 | 1.0.19 | 1.0.20 |
| --- | --- | --- | --- |
| The seven endpoints, bearer + `X-Agent-Id`, BaseUrl path prefixes | ✓ | ✓ | ✓ |
| Metrics series and snapshots (CPU, RAM, disks, queues, NICs, services, pools) | ✓ | ✓ | ✓ |
| Incident types 0 to 12 | ✓ | ✓ | ✓ |
| `batchId` idempotency key (HTTPS `batchId`; gRPC `batch_id`) | ✗ | ✓ | ✓ |
| `agent` object (version, machine, OS, uptime, update phase, last update) | ✗ | ✗ | ✓ |
| `agent.maintenance`, remote `maintenance` | ✗ | ✗ | ✓ |
| `endpointStatuses`, incident types 13 to 15 | ✗ | ✗ | ✓ |
| ServerRestarted (16) | ✗ | ✗ | ✓ |
| `alertKey` / `resolved` alert lifecycle | ✗ | ✗ | ✓ |
| Command freshness check on `issuedAt` (stale, future or unparseable: failed result; absent: accepted with a warning, or rejected with `RequireCommandIssuedAt`) | ✗ (any `issuedAt`, even none, accepted) | partial (stale, future or unparseable: dropped without an acknowledgement or result; absent: accepted) | ✓ |
| A known `commandId` is never executed again | ✓ (command records kept forever) | ✓ (command records kept forever) | ✓ (IDs kept 365 days, at most 200 000, by default) |
| Command results retried with backoff, given up after `ResultRetryMaxHours` (168 h) | ✗ (every cycle, without limit) | ✗ (every cycle, without limit) | ✓ |
| `ConfigurationError` endpoint status (invalid check keeps its last result) | ✗ (no `endpointStatuses`) | ✗ (no `endpointStatuses`) | ✓ |
| Snapshot entries removed only when the resource is gone (absent past the grace period, or removed from the configuration; names compared case-insensitively) and kept through the daily report purge | ✗ (every snapshot row deleted by the daily report purge, then read again) | ✗ (as 1.0.18) | ✓ |
| `isStale` on every snapshot entry (HTTPS `isStale`; gRPC `is_stale`) | ✗ (absent: unknown, judge by `timestamp`) | ✗ (as 1.0.18) | ✓ |
| 3xx answered as a failure, redirects not followed | ✗ (followed by .NET, dropping the token) | ✗ (as 1.0.18) | ✓ |
| Local allow-list for service and pool commands | ✗ (any service or pool name is acted on) | partial (watched names only, but watch lists pushed by the portal count; others fail with `Not authorized: ...`) | ✓ (`AllowPortalManagedTargets` opt-in) |
| Remote configuration validation of every field (ranges, names, list sizes, all or nothing) | partial (CPU/RAM poll interval and threshold, disk critical threshold only) | partial (every poll interval, portal timings included, CPU/RAM sustained time, CPU/RAM/disk thresholds only) | ✓ |
| Events retried for up to 7 days | ✗ (5 attempts, like metrics) | ✗ (as 1.0.18) | ✓ |
| 303 AgentRestart | stub ("reserved and not yet implemented") | stub (as 1.0.18) | ✓ |
| 304 AgentUpdate, 501 SendTestNotification | ✗ (`Unknown action type`) | ✗ (as 1.0.18) | ✓ |
| Enrollment codes (`WT1.`, `enroll`, `install.ps1 -EnrollmentCode`) | ✗ (seed file or manual settings) | ✗ (as 1.0.18) | ✓ |
| Results for commands interrupted by a restart (`Interrupted: ...`) | ✗ | ✗ | ✓ |
| Commands stored but not started before a restart run afterwards, or expire (`Rejected: command expired before it could run (agent restarted)`) | ✗ (never run, no result) | ✗ (as 1.0.18) | ✓ |

Consequences for portals with 1.0.19 or older agents in the fleet: send a current
`issuedAt` anyway (it becomes REQUIRED in v2; 1.0.19 drops a command whose `issuedAt` is stale
without answering it), validate remote configuration yourself (those agents check only a few
values), and gate service and pool commands in the portal: 1.0.18 and older act on any name,
and 1.0.19 accepts any name on a watch list your portal pushed.

### After 1.0.20 (unreleased)

| Feature | ≤ 1.0.20 | after-1.0.20 |
| --- | --- | --- |
| Gzip-compressed `metrics/batch` and `events` bodies (`Content-Encoding: gzip`; gRPC `grpc-encoding: gzip`), opt-in with `VisibilityPortal:CompressRequests=Gzip`, with the 415 fallback | ✗ (always plain JSON) | ✓ (off by default) |
| `acceptsGzip` in enrollment codes turns compression on | ✗ (ignored like any unknown property) | ✓ |

A portal can't tell from a request whether the agent would compress: it sees `Content-Encoding`
only when compression is on. Put `"acceptsGzip": true` in codes only if you decode gzip; the same
code works for older agents, which ignore the flag and keep sending plain JSON.

## The gRPC alternative

An operator can set `VisibilityPortal:Protocol = Grpc`. The agent then calls the same seven
operations as unary gRPC methods over HTTP/2, defined in
[`src/WatchTower/Portal/Contracts/Protobuf/watchtower.proto`](../../src/WatchTower/Portal/Contracts/Protobuf/watchtower.proto)
(package `watchtower.portal.v1`, service `WatchTowerPortalService`). Enrollment codes always
configure HTTPS/JSON. gRPC is an operator choice for portals that offer it. A portal only needs
the HTTPS/JSON API. gRPC is optional.

| JSON operation | gRPC method | Success means |
| --- | --- | --- |
| `POST metrics/batch` | `SendMetricsBatch(MetricsBatchRequest)` | `MetricsBatchResponse.accepted = true` |
| `POST events` | `SendEvent(AgentEventRequest)` | `AgentEventResponse.accepted = true` |
| `GET commands/pending` | `PollCommands(PollCommandsRequest)` | `commands` (empty = none) |
| `POST commands/{id}/acknowledge` | `AcknowledgeCommand(AckCommandRequest)` | `ok = true` |
| `POST commands/{id}/result` | `ReportCommandResult(CommandResultRequest)` | `ok = true` |
| `GET config?version=` | `FetchConfig(FetchConfigRequest{current_version})` | `has_update` (false = the 204 case) |
| `POST config/acknowledge` | `AcknowledgeConfig(AckConfigRequest)` | `ok = true` |

Differences from the JSON API:

- **Identity** travels three ways: metadata `x-agent-id` and `authorization: Bearer <token>`, and
  an `AgentIdentity {agent_id, agent_token}` field in every request. Authenticate the metadata or
  the message consistently.
- **Failure** is any `RpcException` (any non-OK status, deadline of 30 s) or
  `accepted`/`ok = false`. The same retry rules apply.
- **Field names** are snake_case with fixed field numbers. The JSON name is the camelCase of
  the proto name, with these exceptions: `MetricsBatchPayload` has no `agent_id` (use the
  identity); `app_pool_statuses` ↔ `applicationPoolStatuses`; `AppPoolStatusProto.app_pool_name` ↔
  `applicationPoolName`.
- **Presence**: proto3 `optional` fields (`available_version`, `alert_key`, `resolved`,
  `response_time_ms`, `certificate_days_left`, `detail`, maintenance fields, every remote-config
  scalar) map to "absent" in JSON when unset. An empty `scheduled_at` means none. Repeated fields
  have no presence: an **empty `watched_services` list means "not specified"**, so over gRPC a
  portal can't push "watch nothing".
- **TLS**: same `RequireTls` rule. `Tls:AllowUntrustedCert` is honoured only by the gRPC transport
  and only outside the Production environment. HTTP/2 is required. `EnableMultipleHttp2Connections`
  is on.
- **Compression** (after 1.0.20): with `CompressRequests=Gzip`, `SendMetricsBatch` and `SendEvent`
  use gRPC message compression (`grpc-encoding: gzip`, compressed-flag set on the message), which
  ASP.NET Core gRPC and most gRPC servers read out of the box. A server that doesn't answers
  `UNIMPLEMENTED` with a message naming the encoding; the agent then resends the call once without
  compression and stops compressing, as for an HTTPS 415.

New v1 fields are added to both transports with new field numbers. Field numbers are never reused.
