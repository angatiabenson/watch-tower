# Portal agent API changelog

[Portal API guide](README.md) › Changelog

Changes to the contract between the WatchTower agent and a portal
([spec](../../spec/portal-agent-api.v1.openapi.yaml)). Agent releases that don't change the
contract aren't listed. Versioning rules: [Versioning and compatibility](12-versioning-and-compatibility.md).

## Unreleased (first agent release after 1.0.18)

The wire format changes are all additive (new optional properties and values), so portals
written for 1.0.x keep parsing everything. Some agent behaviour changed as well: a portal that
relied on the old behaviour (redirects, stale commands, lenient configuration checks, commands
for any service name) is affected. See "Changed" below.

### Added

- `batchId` in metrics batches, an idempotency key stable across retries.
- `agent` object in metrics batches: `version`, `machineName`, `os`, `startedAt`,
  `uptimeSeconds`, `updatePhase`, `availableVersion`, `lastUpdateResult`, `maintenance`.
- `endpointStatuses` in metrics batches (HTTP, TCP and certificate checks).
- `alertKey` and `resolved` on events (alert lifecycle, including retirement).
- Incident types 13 EndpointDown, 14 EndpointRecovered, 15 CertificateExpiring, 16 ServerRestarted.
- Action types 304 AgentUpdate (optional `Version` guard) and 501 SendTestNotification
  (optional `Channel`); 303 AgentRestart is now implemented.
- Remote configuration `maintenance { untilUtc, reason }`.
- Enrollment codes: `WT1.` + base64url JSON `{v, url, agentId, token, requireTls}`, accepted by
  `WatchTower.exe enroll`, `install.ps1 -EnrollmentCode` and the agent's console.
- Results for commands that were executing when the agent stopped.
- The contract itself: this OpenAPI file, the portal guide, MinimalPortal, AgentSimulator and
  contract tests.

### Changed (agent behaviour within the existing contract)

- Command freshness: when `issuedAt` is present, a command older than `MaxCommandAgeSeconds` +
  `ClockSkewSeconds` (default 3600 + 300 s), more than `ClockSkewSeconds` (300 s) in the future,
  or with an unparseable value is rejected: never acknowledged or executed, and reported with
  `success: false` and a message starting `Rejected: ` (retried until accepted). A command
  without `issuedAt` is accepted, acknowledged and executed, with a one-time warning in the agent
  log. Portals SHOULD send `issuedAt`; it becomes REQUIRED in v2.
- Redirects are never followed, on any transport: a 3xx (304 included) fails the call like any
  other failure, with a rate-limited warning naming the target. The bearer token and
  `X-Agent-Id` are only sent to URLs below BaseUrl. Portals MUST NOT redirect agent API calls.
- Snapshots no longer keep resources that are gone: drives, physical disks and network
  interfaces leave the batch after 3 consecutive successful inventories and 10 minutes without
  them; services, pools and endpoint checks on the next poll after they leave the configuration.
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

## 1.0.x (up to 1.0.18)

The original v1 contract: the seven endpoints under `/api/v1/agent/`, bearer token plus
`X-Agent-Id`, metrics batches without `batchId` or `agent`, events with incident types 0 to 12,
action types 101 to 103, 201 to 204, 301, 302, 401 (303 reserved), remote configuration for
thresholds, intervals, watch lists and portal timings, and the gRPC transport in
`watchtower.proto` (package `watchtower.portal.v1`).
