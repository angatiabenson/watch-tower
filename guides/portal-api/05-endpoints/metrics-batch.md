| `pendingAlerts[]` | array | one per breach and severity | optional (always from agents after 1.0.21, possibly empty) | Snapshot of the breaches inside their waiting period at `batchedAt`. **Not alerts**: nothing was raised or sent. Replace the agent's pending set with each newer batch's list; absent = nothing pending. See [Pending alerts](../06-telemetry-reference.md#pending-alerts-pendingalerts). | after-1.0.21 |
| `pendingAlerts[].alertKey` | string | | always | The key the alert will have (`diskqueue:0 C:`, `svc:W3SVC`, ...), as in [events](events.md). | after-1.0.21 |
| `pendingAlerts[].severity` | string (open enum) | `Info`, `Warning`, `Critical` | always | Severity the alert will have. | after-1.0.21 |
| `pendingAlerts[].title` | string | | always | Latest headline, for display. | after-1.0.21 |
| `pendingAlerts[].since` | string | ISO-8601 UTC | always | When the condition was first seen at this severity. | after-1.0.21 |
| `pendingAlerts[].alertAfterSeconds` | integer | s, 0 to 86400 | always | The waiting period. | after-1.0.21 |
| `pendingAlerts[].alertsAt` | string | ISO-8601 UTC | always | `since` + `alertAfterSeconds`: when it is raised if still present. | after-1.0.21 |
# POST /api/v1/agent/metrics/batch

[Portal API guide](../README.md) › [Endpoints](README.md) › metrics/batch

## Purpose and timing

Delivers the agent's measurements and its self-description. The outbox worker runs a cycle every
`VisibilityPortal:Outbox:FlushIntervalSeconds` (default **30 s**). Each cycle:

1. collects the CPU and RAM samples taken since the previous batch (the agent samples both every
   5 s by default, `Monitoring:Cpu:PollIntervalSeconds` / `Monitoring:Ram:PollIntervalSeconds`),
   at most `Outbox:MaxBatchSize` (500) of each;
2. adds the latest reading of every disk, physical-disk queue, network interface, watched
   service, watched application pool and endpoint check, each with `isStale`: whether that
   reading is older than its freshness window at `batchedAt`;
3. adds the `agent` object;
4. stores that batch in the outbox with a new `batchId` (only if there is any CPU, RAM, disk,
   network, service, app-pool or endpoint data);
5. sends queued messages, events first, then oldest first, one request per message.

A ForceMetricSnapshot command (301) wakes the worker immediately. On start-up the first cycle runs
right away.

## Request

```http
POST /api/v1/agent/metrics/batch HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

With `VisibilityPortal:CompressRequests=Gzip` (opt-in, after 1.0.20) the request also carries
`Content-Encoding: gzip` and the body is the gzip of the same JSON; a portal that can't read it
answers 415 and the agent resends it uncompressed. See
[Compressed request bodies](../04-transport-and-security.md#compressed-request-bodies-opt-in-after-1020).

No query parameters. Body from a current agent. Drive `D:\` is still listed but hasn't been
read since 1 November (a fixed drive that is not ready), and the check `status-api` has been
left misconfigured for over 10 minutes. Both keep their last reading, marked `isStale: true`. The
disk queue of `0 C:` and the stopped Print Spooler are inside their waiting period (`pendingAlerts`,
not alerts yet):

<!-- schema: MetricsBatch -->
```json
{
  "agentId": "SRV-WEB-01",
  "batchId": "0b6f3c1e-8f6c-4d8e-9a43-3c2a7d5e9b11",
  "batchedAt": "2026-11-03T10:15:30.0000000Z",
  "cpuMetrics": [
    { "timestamp": "2026-11-03T10:15:00.0000000Z", "usagePercent": 12.5 },
    { "timestamp": "2026-11-03T10:15:05.0000000Z", "usagePercent": 14.1 }
  ],
  "ramMetrics": [
    { "timestamp": "2026-11-03T10:15:00.0000000Z", "usagePercent": 61.2, "availableMb": 6355.4, "totalMb": 16384 }
  ],
  "diskMetrics": [
    { "timestamp": "2026-11-03T10:14:30.0000000Z", "driveName": "C:\\", "totalGb": 126.4, "freeGb": 40.2, "usagePercent": 68.2, "isStale": false },
    { "timestamp": "2026-11-01T06:12:00.0000000Z", "driveName": "D:\\", "totalGb": 512, "freeGb": 301.7, "usagePercent": 41.1, "isStale": true }
  ],
  "diskQueueMetrics": [
    { "timestamp": "2026-11-03T10:14:30.0000000Z", "instanceName": "0 C:", "queueLength": 0.4, "isStale": false }
  ],
  "networkMetrics": [
    { "timestamp": "2026-11-03T10:15:00.0000000Z", "interfaceName": "Intel[R] 82574L Gigabit Network Connection", "outputQueueLength": 0, "isStale": false }
  ],
  "serviceStatuses": [
    { "timestamp": "2026-11-03T10:15:10.0000000Z", "serviceName": "W3SVC", "displayName": "World Wide Web Publishing Service", "status": "Running", "isRunning": true, "isStale": false },
    { "timestamp": "2026-11-03T10:15:10.0000000Z", "serviceName": "Spooler", "displayName": "Print Spooler", "status": "Stopped", "isRunning": false, "isStale": false }
  ],
  "applicationPoolStatuses": [
    { "timestamp": "2026-11-03T10:15:10.0000000Z", "applicationPoolName": "DefaultAppPool", "status": "Started", "isRunning": true, "isStale": false }
  ],
  "agent": {
    "version": "1.6.0",
    "machineName": "SRV-WEB-01",
    "os": "Microsoft Windows 10.0.20348",
    "startedAt": "2026-11-03T08:00:00.0000000+00:00",
    "uptimeSeconds": 8130,
    "updatePhase": "Available",
    "availableVersion": "1.7.0",
    "lastUpdateResult": { "toVersion": "1.6.0", "success": true, "completedAt": "2026-11-03T07:59:41.0000000+00:00" },
    "maintenance": { "active": true, "untilUtc": "2026-11-03T18:00:00.0000000Z", "reason": "Patch Tuesday" }
  },
  "endpointStatuses": [
    { "timestamp": "2026-11-03T10:15:20.0000000Z", "name": "shop-home", "type": "Http", "target": "https://shop.example.com/", "status": "Up", "isUp": true, "responseTimeMs": 182, "certificateDaysLeft": 61.4, "detail": "HTTP 200", "isStale": false },
    { "timestamp": "2026-11-03T10:15:20.0000000Z", "name": "sql-port", "type": "Tcp", "target": "sql01.corp.local:1433", "status": "Down", "isUp": false, "detail": "Connection refused", "isStale": false },
    { "timestamp": "2026-11-03T09:58:41.0000000Z", "name": "status-api", "type": "Http", "target": "https://api.example.com/health", "status": "ConfigurationError", "isUp": true, "responseTimeMs": 95, "detail": "Configuration error: Monitoring:Endpoints:2:ExpectStatus must be status codes or ranges between 100 and 599, e.g. 200-399 or 200,204. Last result: Up: HTTP 200", "isStale": true }
  ],
  "pendingAlerts": [
    { "alertKey": "diskqueue:0 C:", "severity": "Warning", "title": "Disk Queue High: 0 C: at 3.4", "since": "2026-11-03T10:13:30.0000000Z", "alertAfterSeconds": 300, "alertsAt": "2026-11-03T10:18:30.0000000Z" },
    { "alertKey": "svc:Spooler", "severity": "Critical", "title": "Service Down: Spooler", "since": "2026-11-03T10:14:40.0000000Z", "alertAfterSeconds": 120, "alertsAt": "2026-11-03T10:16:40.0000000Z" }
  ]
}
```

Body from an agent up to 1.0.18 (and the smallest body a current agent can send):

<!-- schema: MetricsBatch -->
```json
{
  "agentId": "SRV-WEB-01",
  "batchedAt": "2026-11-03T10:15:30.0000000Z",
  "cpuMetrics": [ { "timestamp": "2026-11-03T10:15:00.0000000Z", "usagePercent": 12.5 } ],
  "ramMetrics": [],
  "diskMetrics": [],
  "diskQueueMetrics": [],
  "networkMetrics": [],
  "serviceStatuses": [],
  "applicationPoolStatuses": []
}
```

### Fields

| Field | Type | Unit / range | Present | Meaning | Since |
| --- | --- | --- | --- | --- | --- |
| `agentId` | string | | always | The agent's configured AgentId. Informational: identify the agent by its credentials. | base |
| `batchId` | string (UUID) | | optional | Idempotency key, random, fixed when the batch was queued, the same on every retry. | 1.0.19 |
| `batchedAt` | string (date-time) | UTC, `Z` | always | When the batch was assembled. Can be minutes to days before arrival. | base |
| `cpuMetrics[]` | array | 0 to MaxBatchSize | always | New CPU samples, oldest first. | base |
| `cpuMetrics[].timestamp` | date-time | UTC | always | Sample time. | base |
| `cpuMetrics[].usagePercent` | number | %, 0 to 100 | always | Total processor time across all logical processors. | base |
| `ramMetrics[]` | array | 0 to MaxBatchSize | always | New memory samples, oldest first. | base |
| `ramMetrics[].timestamp` | date-time | UTC | always | Sample time. | base |
| `ramMetrics[].usagePercent` | number | %, 0 to 100 | always | Used physical memory. | base |
| `ramMetrics[].availableMb` | number | MiB, ≥ 0 | always | Available physical memory. | base |
| `ramMetrics[].totalMb` | number | MiB, > 0 | always | Installed physical memory. | base |
| `diskMetrics[]` | array | one per fixed drive | always | Snapshot (last-known reading, repeated every batch; see below for when an entry is removed). | base |
| `diskMetrics[].timestamp` | date-time | UTC | always | When the drive was read (default every 60 s). | base |
| `diskMetrics[].driveName` | string | e.g. `C:\` | always | Drive root, with trailing backslash (JSON `"C:\\"`). | base |
| `diskMetrics[].totalGb` / `freeGb` | number | GiB (1024³ bytes), ≥ 0 | always | Capacity and free space. | base |
| `diskMetrics[].usagePercent` | number | %, 0 to 100 | always | Used share; 0 when the capacity is 0. | base |
| `diskQueueMetrics[]` | array | one per physical disk | always | Snapshot. | base |
| `diskQueueMetrics[].instanceName` | string | e.g. `0 C:` | always | PhysicalDisk counter instance. | base |
| `diskQueueMetrics[].queueLength` | number | requests, ≥ 0 | always | Current disk queue length. | base |
| `networkMetrics[]` | array | one per interface | always | Snapshot. | base |
| `networkMetrics[].interfaceName` | string | | always | Network Interface counter instance. | base |
| `networkMetrics[].outputQueueLength` | number | packets, ≥ 0 | always | Output queue length. | base |
| `serviceStatuses[]` | array | one per watched service | always | Snapshot. | base |
| `serviceStatuses[].serviceName` / `displayName` | string | | always | Key name and display name (display = key when not found). | base |
| `serviceStatuses[].status` | string (open enum) | `Running`, `Stopped`, `StartPending`, `StopPending`, `ContinuePending`, `PausePending`, `Paused`, `NotFound` | always | Service Control Manager state. | base |
| `serviceStatuses[].isRunning` | boolean | | always | `status == Running`. | base |
| `applicationPoolStatuses[]` | array | one per watched pool | always | Snapshot. | base |
| `applicationPoolStatuses[].applicationPoolName` | string | | always | IIS pool name. | base |
| `applicationPoolStatuses[].status` | string (open enum) | `Started`, `Stopped`, `Starting`, `Stopping`, `Unknown`, `NotFound` | always | IIS state; `Unknown` when it could not be read, `NotFound` when IIS has no pool of that name. | base |
| `applicationPoolStatuses[].isRunning` | boolean | | always | `status == Started`. | base |
| `*.timestamp` (snapshots) | date-time | UTC | always | When that resource was last read. | base |
| `*.isStale` (snapshots, `endpointStatuses[]` included) | boolean | | optional (always from current agents) | `true` when that reading is older than its freshness window at `batchedAt`: max(3 × the monitor's poll interval, 10 min), or for an endpoint check max(2 × `IntervalSeconds` + `TimeoutSeconds`, 10 min). The reading and `timestamp` are the last ones. **Absent = unknown**: fall back to the `timestamp`. See [Staleness](../06-telemetry-reference.md#staleness-isstale). | 1.0.20 |
| `agent` | object | | optional (always from current agents) | Agent self-description, see [telemetry](../06-telemetry-reference.md#the-agent-object). | 1.0.20 |
| `agent.version` | string | semver, e.g. `1.6.0` | always in `agent` | Agent version (`0.0.0-dev` for development builds). | 1.0.20 |
| `agent.machineName` | string | | always in `agent` | Windows computer name. | 1.0.20 |
| `agent.os` | string | | always in `agent` | OS description. | 1.0.20 |
| `agent.startedAt` | date-time | `+00:00` offset | always in `agent` | Agent process start. | 1.0.20 |
| `agent.uptimeSeconds` | integer | s, ≥ 0 | always in `agent` | Agent process uptime (not the machine's). | 1.0.20 |
| `agent.updatePhase` | string (open enum) | `Idle`, `Checking`, `UpToDate`, `Available`, `Downloading`, `ReadyToInstall`, `Installing`, `Failed` | always in `agent` | Self-update state. | 1.0.20 |
| `agent.availableVersion` | string | | optional | Newer version offered by the feed. | 1.0.20 |
| `agent.lastUpdateResult` | object | | optional | `toVersion`, `success`, `completedAt` (any offset) of the last install. | 1.0.20 |
| `agent.maintenance` | object | | optional (always from current agents) | `active` (always), `untilUtc` (optional, `Z`), `reason` (optional, ≤ 500 chars, may be empty). | 1.0.20 |
| `endpointStatuses[]` | array | one per endpoint check | optional | Snapshot; absent when no endpoint has a result. | 1.0.20 |
| `endpointStatuses[].name` | string | | always | Check name, unique per agent. | 1.0.20 |
| `endpointStatuses[].type` | string (open enum) | `Http`, `Tcp`, `Certificate` | always | Check type. | 1.0.20 |
| `endpointStatuses[].target` | string | | always | URL or `host:port`, without credentials or query string. | 1.0.20 |
| `endpointStatuses[].status` | string (open enum) | `Up`, `Down`, `Expiring`, `ConfigurationError` | always | Result. `ConfigurationError`: the check is configured but invalid and does not run; the entry keeps its last result (`timestamp` included) and `detail` gives the error. | 1.0.20 |
| `endpointStatuses[].isUp` | boolean | | always | `false` for `Down`. With `ConfigurationError`, the last result's value (not a current health). | 1.0.20 |
| `endpointStatuses[].responseTimeMs` | number | ms, ≥ 0 | optional | Round-trip time. | 1.0.20 |
| `endpointStatuses[].certificateDaysLeft` | number | days, 0.1 precision, negative when expired | optional | TLS certificate lifetime left. | 1.0.20 |
| `endpointStatuses[].detail` | string | | optional | Human-readable result. | 1.0.20 |

Semantics, display and aggregation of every value: [Telemetry reference](../06-telemetry-reference.md).

## Responses and agent behaviour

| Response | Agent behaviour |
| --- | --- |
| any 2xx (200, 201, 202, 204) | Delivered. The body is ignored. |
| 401 | Failure; logged at Error ("Check AgentToken"). Retried as below. |
| 3xx, 400, 403, 404, 413, 429, other 4xx, 5xx | Failure; logged at Warning with the status (redirects are not followed; the warning includes the `Location`). Retried as below. |
| 415, 413, or 400 before any compressed request was accepted, to a **compressed** request | Resent once at once, uncompressed; that answer counts. 415 (and a 400 the uncompressed body doesn't get) switches compression off, see [Compressed request bodies](../04-transport-and-security.md#compressed-request-bodies-opt-in-after-1020). |
| timeout (30 s), DNS, TCP or TLS error | Failure; retried as below. |

Retry: the first failed message ends the cycle (the rest stay queued without counting an
attempt). A failed batch is not resent on the next cycle: it is due again after
`RetryBackoffSeconds × attempt` (60 s, 120 s, 180 s, 240 s) and is retried on the first cycle
after that, so other messages may be delivered before it. It is **dropped after `MaxRetryAttempts` (5) failed attempts**. Its CPU/RAM samples then never
reach the portal. Delivered and dropped messages are purged from the agent after 48 h. Details:
[Reliability](../10-reliability.md).

## Idempotency and deduplication

- **Key:** `(agent, batchId)`. A retried batch is byte-for-byte the same JSON, with the same
  `batchId`. The same `batchId` MUST NOT create a second set of samples. Answer duplicates 2xx.
- **Without `batchId`** (agents up to 1.0.18): deduplicate samples on `(agent, series, timestamp)`.
  This also works for current agents, and the reference portal does both.
- **Snapshots** are repeated in every batch by design. Store them as "latest state per resource"
  and append to history only when the `timestamp` changed. Never keep an older snapshot over a
  newer one: a batch delayed in the outbox arrives after newer ones. One exception: an endpoint
  check that becomes invalid is reported with status `ConfigurationError` and its **unchanged**
  `timestamp`, so let an entry with an equal `timestamp` from a batch with a later `batchedAt`
  replace the stored one (and record the status change if you keep history).
- **Removed resources**: A snapshot entry is the resource's last-known reading, repeated until a newer reading replaces it
  or the resource is removed:
  
  - **Drives, physical disks, network interfaces**: removed once the resource has been absent from 3
    consecutive successful inventories AND for at least 10 minutes, whether or not it has an open
    alert. A failed read never counts, and neither does an empty disk listing (an empty
    network-interface listing does).
  - **Services, application pools, endpoint checks**: removed on the agent's next poll after the
    name leaves `Monitoring:Services:WatchedServices`,
    `Monitoring:ApplicationPools:WatchedApplicationPools` or `Monitoring:Endpoints` (locally or
    through [remote config](../09-remote-config.md)). Every configured endpoint check counts,
    valid or not: an invalid one keeps its entry with status `ConfigurationError`. Names are
    compared case-insensitively: renaming only the letter case keeps the entry, its last reading
    and its schedule, and the entry is reported under the new spelling. Key these resources
    case-insensitively.
  - The next batch is built from what remains, and a resource that comes back starts a fresh
    entry. Do not expect a resource to vanish from the very next batch.
  - Nothing else removes an entry: the agent's daily report purge deletes only history
    (delivered CPU/RAM samples and incidents), never the current state. A resource that is still
    present but can't be read (for example a fixed drive that is not ready) keeps its last
    reading and `timestamp` until it is removed. Its presence in the batch never means it is
    fresh. Current agents send it with `isStale: true` once the reading is older than its
    window: show it as stale. When `isStale` is missing (older agents), it is unknown, not
    `false`: judge freshness from the entry's `timestamp` against `batchedAt` instead.
- **Staleness**: take `isStale` from the newest batch (latest `batchedAt`) even when the
  `timestamp` didn't change. It turns `true` without a new reading.

## Common mistakes

- Answering 400 or 409 to a duplicate `batchId`: the agent retries it five times and logs warnings.
- Requiring `batchId`, `agent` or `endpointStatuses`: agents up to 1.0.18 don't send `batchId`,
  agents up to 1.0.19 send neither `agent` nor `endpointStatuses`, and later ones omit
  `endpointStatuses` until an endpoint check has a result.
- Rejecting unknown properties (strict schema validation, `additionalProperties: false`, or
  Jackson `FAIL_ON_UNKNOWN_PROPERTIES`): the next agent version breaks your portal.
- Rejecting chunked bodies (some WAFs and serverless gateways), or a body limit below a few
  hundred kilobytes.
- Treating `batchedAt` or arrival time as the sample time; use each item's `timestamp`.
- Counting snapshot entries as new samples (disk history with duplicates every 30 s).
- Treating a missing `isStale` as `false` (older agents don't send it), or showing a stale
  entry's last `status` or usage as current.
- Showing `pendingAlerts` as open alerts, paging on them, or keeping an entry that a newer batch no
  longer lists: they are breaches that may still clear without any alert.
- Parsing `driveName` without the trailing backslash, or `"C:\\"` as an escape error.
- Using `agent.uptimeSeconds` as server uptime (it is the agent process; a reboot shows up as a
  ServerRestarted event instead).
- Using the body `agentId` for tenant lookup instead of the authenticated identity.
