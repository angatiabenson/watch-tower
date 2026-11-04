# Portal agent API changelog

[Portal API guide](README.md) › Changelog

Changes to the contract between the WatchTower agent and a portal
([spec](../../spec/portal-agent-api.v1.openapi.yaml)). Agent releases that don't change the
contract aren't listed. Versioning rules: [Versioning and compatibility](12-versioning-and-compatibility.md).

## Unreleased (after 1.0.20)

Everything here is marked `x-since: after-1.0.20` in the spec.

### Added

- Opt-in compressed request bodies. With the new agent option `VisibilityPortal:CompressRequests`
  set to `Gzip` (default `Off`), `POST metrics/batch` and `POST events` carry
  `Content-Encoding: gzip` (body: the gzip of the same JSON, still chunked). A typical batch
  shrinks about five-fold. Acknowledgements and command results stay uncompressed. Over gRPC the
  same option turns on `grpc-encoding: gzip` for `SendMetricsBatch` and `SendEvent`. Spec: the
  `Content-Encoding` header parameter on both operations and "Compressed request bodies" in the
  description.
- Fallback for portals without support: a 415 to a compressed request (or a 400 that the same body
  sent uncompressed doesn't get, before any compressed request was accepted; gRPC: `UNIMPLEMENTED`
  for the encoding) is answered by one uncompressed resend, one warning and no compression until
  the agent restarts or the setting or BaseUrl changes. A 413 is resent uncompressed once and
  compression stays on. Portals without support SHOULD answer 415 and MUST NOT store a body they
  could not read. Nothing changes for a portal whose agents keep the default.
- `acceptsGzip` (optional boolean) in the enrollment code payload: `true` makes `enroll` and the
  console's enrollment panel set `CompressRequests=Gzip`; absent or `false` leaves the setting
  as it is. Agents up to 1.0.20 ignore it, so a code with it works for every agent.
- MinimalPortal decodes gzip and br request bodies (decompressed size capped, 413 past it; 415
  for other encodings, or for all of them with `--AcceptCompressedRequests false`) and puts
  `acceptsGzip` in its codes. AgentSimulator follows the code's `acceptsGzip` and has `--gzip` and
  `--no-gzip`.

### Fixed

- gRPC: the channel was built once at startup, while each call's metadata was read at call time,
  so after a runtime change of `VisibilityPortal:BaseUrl` calls kept going to the old portal with
  the new portal's token (and the old portal's encoding refusal could turn compression off for
  the new one). Now each call takes its destination, metadata, `AgentIdentity` and compression
  state from one snapshot of the settings; a change of BaseUrl, agent id, token or TLS settings
  opens a new channel for the next call, and calls running on the old one finish there. The
  credentials are only sent to the snapshot's BaseUrl origin. Settings the agent can't connect
  with make calls fail unsent (retried as usual) instead of falling back to the old channel. A
  portal sees no wire change.

## 1.0.20 (2026-11-04)

Everything here is marked `x-since: 1.0.20` in the spec: 1.0.20 and later agents send it, 1.0.19
and older don't. The wire format changes are all additive (new optional properties and values),
so portals written for 1.0.19 and older keep parsing everything. Some agent
behaviour changed as well: a portal that relied on the old behaviour (redirects, stale commands,
lenient configuration checks, commands for any service name, reused command IDs, results
refused for more than a week) is affected. See "Changed" below.

### Added

- `agent` object in metrics batches: `version`, `machineName`, `os`, `startedAt`,
  `uptimeSeconds`, `updatePhase`, `availableVersion`, `lastUpdateResult`, `maintenance`.
- `endpointStatuses` in metrics batches (HTTP, TCP and certificate checks).
- `ConfigurationError`, a fourth known value of the open set `endpointStatuses[].status` (HTTPS
  and gRPC): the check is configured but invalid and does not run, and its entry keeps the last
  result (`timestamp`, `type`, `target`, `isUp` and measurements unchanged) with `detail`
  `Configuration error: <error> Last result: <status>[: <detail>]`. No field was added or
  changed. A portal that accepts unknown status values keeps working; one that maps only
  `Up`/`Down`/`Expiring` should show it as "not checked" (`isUp` is the last result's), and one
  that keeps the newest entry per name must let an equal `timestamp` from a later batch replace
  it.
- `isStale` on every snapshot entry (`diskMetrics`, `diskQueueMetrics`, `networkMetrics`,
  `serviceStatuses`, `applicationPoolStatuses`, `endpointStatuses`), optional boolean; gRPC
  `optional bool is_stale` (field 6 of `DiskMetricProto` and `ServiceStatusProto`, 4 of
  `DiskQueueMetricProto` and `NetworkMetricProto`, 5 of `AppPoolStatusProto`, 10 of
  `EndpointStatusProto`). `true` when the entry's reading is older than its freshness window at
  `batchedAt`: max(3 × the monitor's poll interval, 10 min) for drives, physical disks, network
  interfaces, services and pools; max(2 × `IntervalSeconds` + `TimeoutSeconds`, 10 min) for an
  endpoint check, `ConfigurationError` included. The reading and its `timestamp` are unchanged.
  A missing `isStale` (1.0.19 and older) means unknown, and portals MUST fall back to the
  `timestamp`. The agent also logs a warning when an entry first goes stale (at most once per
  hour per resource) and greys stale entries out in its console.
- Agent options that affect what a portal sees (operator settings, not part of the wire format):
  `VisibilityPortal:RequireCommandIssuedAt` (default false), `VisibilityPortal:CommandIdRetentionDays`
  (365, 0 = no age limit), `VisibilityPortal:CommandIdMaxCount` (200 000, 0 = no limit) and
  `VisibilityPortal:CommandPoll:ResultRetryMaxHours` (168, at least 1). See "Changed".
- `alertKey` and `resolved` on events (alert lifecycle, including retirement).
- Incident types 13 EndpointDown, 14 EndpointRecovered, 15 CertificateExpiring, 16 ServerRestarted.
- Action types 304 AgentUpdate (optional `Version` guard) and 501 SendTestNotification
  (optional `Channel`); 303 AgentRestart is now implemented.
- Remote configuration `maintenance { untilUtc, reason }`.
- Enrollment codes: `WT1.` + base64url JSON `{v, url, agentId, token, requireTls}`, accepted by
  `WatchTower.exe enroll`, `install.ps1 -EnrollmentCode` and the agent's console.
- Results for commands an agent restart cut short. One that was running is never run again and
  is reported `Interrupted: the agent stopped while this command was running; its outcome is
  unknown`. One that was stored but not started is picked up at the start of every poll cycle
  and runs if it is still within `MaxCommandAgeSeconds` + `ClockSkewSeconds` of its `issuedAt`
  (or of its receipt without `issuedAt`); otherwise it is reported `Rejected: command expired
  before it could run (agent restarted)`, usually after an acknowledgement. 1.0.19 and older left
  both without a result. Portals should expect results long after sending a command.
- The contract itself: this OpenAPI file, the portal guide, MinimalPortal, AgentSimulator and
  contract tests.

### Changed (agent behaviour within the existing contract)

- Command freshness: when `issuedAt` is present, a command older than `MaxCommandAgeSeconds` +
  `ClockSkewSeconds` (default 3600 + 300 s), more than `ClockSkewSeconds` (300 s) in the future,
  or with an unparseable value is rejected: never acknowledged or executed, and reported with
  `success: false` and a message starting `Rejected: ` (retried like every result). A command
  without `issuedAt` is accepted, acknowledged and executed, with a one-time warning in the agent
  log, unless the operator sets `VisibilityPortal:RequireCommandIssuedAt = true`: it is then
  rejected the same way with `Rejected: issuedAt is required`, even when `MaxCommandAgeSeconds`
  is 0. Portals SHOULD send `issuedAt`; it becomes REQUIRED in v2. 1.0.19 dropped a stale,
  future or unparseable command without acknowledging or answering it; 1.0.18 and older
  accepted any `issuedAt`.
- Replay protection by command ID: every `commandId` the agent receives (executed, failed,
  rejected or not authorized) is recorded before anything runs, and a known ID is never stored
  or executed again, even after `CommandPoll:RetentionDays` (30 days) purged the command; a
  re-delivery is still acknowledged. IDs are remembered for `CommandIdRetentionDays` (365 days)
  and at most `CommandIdMaxCount` (200 000), oldest forgotten first, pruned hourly, never while
  the command itself is still stored. 1.0.19 and older never purged command records, so they
  never ran an ID twice either, but their database grew without bound; with the purge after
  `RetentionDays` the separate ID record keeps that guarantee, now bounded by those two limits.
  The guidance is stricter accordingly: a `commandId` MUST be globally unique and never reused
  (it was "unique per agent"); a portal that reuses IDs sees the repeats acknowledged but not
  executed.
- Command results are retried on a per-result backoff (1, 2, 4, 8, 16, then every 30 minutes),
  at most 50 due results per poll cycle, the longest-due first, instead of every cycle; a result
  not accepted within `CommandPoll:ResultRetryMaxHours` (168 h) is given up on and logged, and at
  most 1000 rejected commands are kept (the oldest deleted, delivered or not; their IDs stay
  known). 1.0.19 and older retried every result on every cycle without limit. A portal that
  answers 2xx to every result, as documented, only notices after an outage of more than a week
  (those results are lost) or when it keeps sending commands the agent rejects.
- Redirects are never followed, on any transport: a 3xx (304 included) fails the call like any
  other failure, with a rate-limited warning naming the target. The bearer token and
  `X-Agent-Id` are only sent to URLs below BaseUrl. Portals MUST NOT redirect agent API calls.
- Snapshots no longer keep resources that are gone: drives, physical disks and network
  interfaces leave the batch after 3 consecutive successful inventories and 10 minutes without
  them; services, pools and endpoint checks on the next poll after they leave the configuration.
  Their names are compared case-insensitively, so renaming one only in letter case keeps its
  entry and last reading, reported under the new spelling. An endpoint check that is still
  configured but invalid keeps its entry, with status `ConfigurationError` (see "Added"). The
  daily report purge deletes only history (delivered CPU/RAM samples and incidents); 1.0.19 and
  older also deleted every snapshot row with it, so resources vanished from the batches until
  they were read again. A resource that is still present but can't be read keeps its last
  reading and `timestamp`. 1.0.20 and later mark it `isStale: true` once that reading is older
  than its window (see "Added"); without `isStale`, the `timestamp` is the freshness indicator.
- Every occurrence of an alert key gets its own recovery event, in the order the alert opened
  and closed (an occurrence's recovery is no longer lost when the alert re-opens).
- Service and application-pool commands only act on names from the agent's local watch lists
  (opt-in `VisibilityPortal:CommandPoll:AllowPortalManagedTargets` to include portal-pushed
  lists); the agent's own service is never a target. 1.0.19 also accepted names on watch lists
  pushed by the portal; 1.0.18 and older acted on any name.
- Remote configuration validation covers every field (ranges, name syntax, list sizes, disk
  warning ≤ critical, maintenance at most 7 days ahead); 1.0.19 checked only the poll intervals,
  CPU/RAM sustained times and the CPU/RAM/disk thresholds, and 1.0.18 and older only the CPU/RAM
  poll interval and threshold and the disk critical threshold. Invalid versions are rejected as
  a whole, without acknowledgement.
- Incident events are retried with exponential backoff for up to `EventMaxAgeHours` (168 h)
  instead of a fixed number of attempts.

### Spec corrections

- Open value sets (`incidentType`, `actionType`, every `status`, `updatePhase`, endpoint `type`) are
  no longer closed JSON Schema `enum`s, which rejected unknown values: the known values are listed
  in `x-extensible-enum`, and the schemas accept any value of the type.
- `applicationPoolStatuses[].status` lists `NotFound`, which the agent reports for a pool IIS does
  not have.
- `AgentCommand.issuedAt` is no longer in `required` (see the freshness change above).
- Retries after a failure, snapshot removal, configuration acknowledgement superseding and
  redirects are described as the agent implements them.
- `AgentCommand.commandId` says "globally unique and never reused" instead of "unique per agent,
  forever (the agent keeps finished commands for RetentionDays)", and `EndpointStatus.isUp` no
  longer says "false only when status is Down" (with `ConfigurationError` it is the last
  result's value). The guide no longer says that a portal whose clock is off drops commands
  silently: they get a `Rejected: ...` result.

## 1.0.19 (2026-06-15)

### Added

- `batchId` in metrics batches (gRPC `batch_id`, field 9 of `MetricsBatchPayload`): a random
  UUID set when the batch is queued, the same on every retry. Marked `x-since: 1.0.19`.

### Changed (agent behaviour within the existing contract)

- A command whose `issuedAt` is older than `MaxCommandAgeSeconds` + `ClockSkewSeconds` (default
  3600 + 300 s), more than `ClockSkewSeconds` in the future or unparseable is dropped: not
  stored, acknowledged, executed or answered (only logged by the agent). A command without
  `issuedAt` is accepted; `MaxCommandAgeSeconds` = 0 turns the check off. 1.0.20 answers these
  commands with a `Rejected: ` result instead (see above).
- Service and application-pool commands only act on watched names (the watch lists in effect,
  including lists pushed by the portal). Other names, or a missing `ServiceName` /
  `AppPoolName`, fail with a result starting `Not authorized: `.
- Remote configuration validation also checks the disk warning threshold, CPU/RAM sustained
  times and every poll interval, portal timings included (percentages 1 to 100, intervals at
  least 1).

## 1.0.x (up to 1.0.18)

The original v1 contract: the seven endpoints under `/api/v1/agent/`, bearer token plus
`X-Agent-Id`, metrics batches without `batchId` or `agent`, events with incident types 0 to 12,
action types 101 to 103, 201 to 204, 301, 302, 401 (303 reserved), remote configuration for
thresholds, intervals, watch lists and portal timings, and the gRPC transport in
`watchtower.proto` (package `watchtower.portal.v1`).
