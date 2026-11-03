# Portal agent API changelog

[Portal API guide](README.md) › Changelog

Changes to the contract between the WatchTower agent and a portal
([spec](../../spec/portal-agent-api.v1.openapi.yaml)). Agent releases that don't change the
contract aren't listed. Versioning rules: [Versioning and compatibility](12-versioning-and-compatibility.md).

## Unreleased (first agent release after 1.0.18)

All additive within v1; portals written for 1.0.x keep working.

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

- Commands are dropped unless `issuedAt` is present, parseable and recent (default: at most
  3600 s old and 300 s in the future). Portals MUST send `issuedAt`.
- Service and application-pool commands only act on names from the agent's local watch lists
  (opt-in `VisibilityPortal:CommandPoll:AllowPortalManagedTargets` to include portal-pushed
  lists); the agent's own service is never a target.
- Remote configuration validation covers every field (ranges, name syntax, list sizes, disk
  warning ≤ critical, maintenance at most 7 days ahead); 1.0.x checked only CPU/RAM poll interval
  and threshold and the disk critical threshold. Invalid versions are rejected as a whole,
  without acknowledgement.
- Incident events are retried with exponential backoff for up to `EventMaxAgeHours` (168 h)
  instead of a fixed number of attempts.

## 1.0.x (up to 1.0.18)

The original v1 contract: the seven endpoints under `/api/v1/agent/`, bearer token plus
`X-Agent-Id`, metrics batches without `batchId` or `agent`, events with incident types 0 to 12,
action types 101 to 103, 201 to 204, 301, 302, 401 (303 reserved), remote configuration for
thresholds, intervals, watch lists and portal timings, and the gRPC transport in
`watchtower.proto` (package `watchtower.portal.v1`).
