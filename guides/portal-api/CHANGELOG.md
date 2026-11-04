# Portal agent API changelog

[Portal API guide](README.md) › Changelog

Changes to the contract between the WatchTower agent and a portal
([spec](../../spec/portal-agent-api.v1.openapi.yaml)). Agent releases that don't change the
contract aren't listed. Versioning rules: [Versioning and compatibility](12-versioning-and-compatibility.md).

## Unreleased (first agent release after 1.0.18)

The wire format changes are all additive (new optional properties and values), so portals
written for 1.0.x keep parsing everything. Some agent behaviour changed as well: a portal that
relied on the old behaviour (redirects, stale commands, lenient configuration checks, commands
for any service name, reused command IDs, results refused for more than a week) is affected.
See "Changed" below.

### Added

- `batchId` in metrics batches, an idempotency key stable across retries.
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
  before it could run (agent restarted)`, usually after an acknowledgement. 1.0.x left both
  without a result. Portals should expect results long after sending a command.
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
  is 0. Portals SHOULD send `issuedAt`; it becomes REQUIRED in v2.
- Replay protection by command ID: every `commandId` the agent receives (executed, failed,
  rejected or not authorized) is recorded before anything runs, and a known ID is never stored
  or executed again, even after `CommandPoll:RetentionDays` (30 days) purged the command; a
  re-delivery is still acknowledged. IDs are remembered for `CommandIdRetentionDays` (365 days)
  and at most `CommandIdMaxCount` (200 000), oldest forgotten first, pruned hourly, never while
  the command itself is still stored. 1.0.x never purged command records, so it never ran an ID
  twice either, but its database grew without bound; with the purge after `RetentionDays` the
  separate ID record keeps that guarantee, now bounded by those two limits. The guidance is
  stricter accordingly: a `commandId` MUST be globally unique and never reused
  (it was "unique per agent"); a portal that reuses IDs sees the repeats acknowledged but not
  executed.
- Command results are retried on a per-result backoff (1, 2, 4, 8, 16, then every 30 minutes),
  at most 50 due results per poll cycle, the longest-due first, instead of every cycle; a result
  not accepted within `CommandPoll:ResultRetryMaxHours` (168 h) is given up on and logged, and at
  most 1000 rejected commands are kept (the oldest deleted, delivered or not; their IDs stay
  known). 1.0.x retried every result on every cycle without limit. A portal that answers 2xx to
  every result, as documented, only notices after an outage of more than a week (those results
  are lost) or when it keeps sending commands the agent rejects.
- Redirects are never followed, on any transport: a 3xx (304 included) fails the call like any
  other failure, with a rate-limited warning naming the target. The bearer token and
  `X-Agent-Id` are only sent to URLs below BaseUrl. Portals MUST NOT redirect agent API calls.
- Snapshots no longer keep resources that are gone: drives, physical disks and network
  interfaces leave the batch after 3 consecutive successful inventories and 10 minutes without
  them; services, pools and endpoint checks on the next poll after they leave the configuration.
  Their names are compared case-insensitively, so renaming one only in letter case keeps its
  entry and last reading, reported under the new spelling. An endpoint check that is still
  configured but invalid keeps its entry, with status `ConfigurationError` (see "Added"). The
  daily report purge deletes only history (delivered CPU/RAM samples and incidents); 1.0.x also
  deleted every snapshot row with it, so resources vanished from the batches until they were
  read again. A resource that is still present but can't be read keeps its last reading and
  `timestamp`: the `timestamp` is the freshness indicator.
- Every occurrence of an alert key gets its own recovery event, in the order the alert opened
  and closed (an occurrence's recovery is no longer lost when the alert re-opens).
- Service and application-pool commands only act on names from the agent's local watch lists
  (opt-in `VisibilityPortal:CommandPoll:AllowPortalManagedTargets` to include portal-pushed
  lists); the agent's own service is never a target.
- Remote configuration validation covers every field (ranges, name syntax, list sizes, disk
  warning ≤ critical, maintenance at most 7 days ahead); 1.0.x checked only CPU/RAM poll interval
  and threshold and the disk critical threshold. Invalid versions are rejected as a whole,
  without acknowledgement.
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

## 1.0.x (up to 1.0.18)

The original v1 contract: the seven endpoints under `/api/v1/agent/`, bearer token plus
`X-Agent-Id`, metrics batches without `batchId` or `agent`, events with incident types 0 to 12,
action types 101 to 103, 201 to 204, 301, 302, 401 (303 reserved), remote configuration for
thresholds, intervals, watch lists and portal timings, and the gRPC transport in
`watchtower.proto` (package `watchtower.portal.v1`).
