# 06 Telemetry reference

[Portal API guide](README.md) › Telemetry reference

Everything a [metrics batch](05-endpoints/metrics-batch.md) carries, with the source on the
Windows side, the units, how often it changes and how to show it. Two kinds of data:

- **Series** (`cpuMetrics`, `ramMetrics`): every sample is in exactly one batch. Append to a time
  series keyed by timestamp.
- **Snapshots** (everything else): the last-known reading of each resource, repeated in every
  batch until a newer reading replaces it. Keep "current state per resource" and append to
  history only when the reading's `timestamp` changes (or, for an endpoint check, its `status`
  becomes `ConfigurationError`, which keeps the `timestamp`; see [Endpoints](#endpoints-endpointstatuses-after-1019)).
  Take the stored `isStale` from the newest batch (latest `batchedAt`), even when the
  `timestamp` is unchanged: it turns `true` without a new reading (see [Staleness](#staleness-isstale)).

A snapshot entry is the resource's last-known reading, repeated until a newer reading replaces it
or the resource is removed:

- **Drives, physical disks, network interfaces**: removed once the resource has been absent from 3
  consecutive successful inventories AND for at least 10 minutes, whether or not it has an open
  alert. A failed read never counts, and neither does an empty disk listing (an empty
  network-interface listing does).
- **Services, application pools, endpoint checks**: removed on the agent's next poll after the
  name leaves `Monitoring:Services:WatchedServices`,
  `Monitoring:ApplicationPools:WatchedApplicationPools` or `Monitoring:Endpoints`. Service and
  application-pool watch lists can change locally or through [remote config](09-remote-config.md);
  endpoint checks can change only locally. Every configured endpoint check counts, valid or not. Names are compared case-insensitively: renaming only the letter case keeps the entry, its
  last reading and its schedule, and the entry is reported under the new spelling. Key these
  resources case-insensitively.
- The next batch is built from what remains, and a resource that comes back starts a fresh
  entry. Do not expect a resource to vanish from the very next batch.
- Nothing else removes an entry: the agent's daily report purge deletes only history
  (delivered CPU/RAM samples and incidents). A resource that is still present but can't be read
  (for example a fixed drive that is not ready) keeps its last reading and `timestamp` until it
  is removed. Current agents mark such an entry `isStale: true`; see [Staleness](#staleness-isstale).

All timestamps are the agent's clock in UTC. See [clock skew](10-reliability.md#clock-skew).

## Staleness (`isStale`)

Every snapshot entry (`diskMetrics`, `diskQueueMetrics`, `networkMetrics`, `serviceStatuses`,
`applicationPoolStatuses`, `endpointStatuses`) carries `isStale` from current agents. The agent
computes it when it builds the batch:

- `true`: the entry's reading is older than its freshness window, i.e.
  `batchedAt - timestamp > window`. The resource is still listed, but hasn't been read within its
  window.
- `false`: it isn't. A reading exactly at the edge of its window, or with a `timestamp` after
  `batchedAt`, is fresh.
- Nothing else changes. A stale entry still carries the last reading and its original
  `timestamp`. An endpoint check with status `ConfigurationError` keeps that status and gets
  `isStale` like any other entry. It goes stale once its last result is older than the window,
  because the check doesn't run.

| Entries | Freshness window |
| --- | --- |
| drives, physical disks | max(3 × `Monitoring:Disk:PollIntervalSeconds`, 10 min) |
| network interfaces | max(3 × `Monitoring:Network:PollIntervalSeconds`, 10 min) |
| services | max(3 × `Monitoring:Services:PollIntervalSeconds`, 10 min) |
| application pools | max(3 × `Monitoring:ApplicationPools:PollIntervalSeconds`, 10 min) |
| endpoint checks | max(2 × `IntervalSeconds` + `TimeoutSeconds`, 10 min) of that check (interval clamped to 5 to 86400 s, timeout to 1 to 120 s) |

Monitor poll intervals are the agent's current values, configured locally or through
[remote config](09-remote-config.md). Endpoint check intervals and timeouts are configured locally only.
With the defaults every window is 10 minutes. An endpoint check's window is longer once
2 × interval + timeout exceeds 600 s: a daily certificate check stays fresh for 48 hours plus
its timeout.

Typical causes of a stale entry: a fixed drive that is not ready, a performance counter that
stopped returning a disk or adapter, a monitor that keeps failing, an endpoint check left with
an invalid configuration. The agent logs a warning on the server once when an entry first goes
stale, and an information message when it is read again (see
[Troubleshooting](13-troubleshooting-faq.md#a-resource-shows-an-old-reading-or-isstale-true)).

How to use it:

- `isStale: true`: show the entry as stale, for example grey with "no reading since
  `<timestamp>`". Don't alert on its last reading as if it were current, and don't count it as
  up or down.
- `isStale: false`: the reading is current as of `batchedAt`.
- **`isStale` missing: unknown, not fresh.** Agents built before this field (1.0.x, 1.0.19
  included, and earlier unreleased builds) don't send it. Fall back to the timestamp: treat the
  entry as stale when `batchedAt - timestamp` exceeds the window above (or your own fixed
  threshold, such as 10 minutes, if you don't track the agent's intervals).
- `isStale` describes the batch when it was built (`batchedAt`). A batch that arrives late
  describes the past, like the rest of it. Just after an agent restart, the first batch can
  mark entries read before the restart as stale until the monitors read them again (seconds
  later).

## CPU (`cpuMetrics[]`)

| Field | Unit | Source | Notes |
| --- | --- | --- | --- |
| `timestamp` | UTC | sample time | every `Monitoring:Cpu:PollIntervalSeconds` (default 5 s) |
| `usagePercent` | % of all logical processors, 0 to 100 | `\Processor(_Total)\% Processor Time`, falling back to `GetSystemTimes` | samples that can't be read are skipped (gaps, never `null`) |

Display: a line chart. For long ranges, aggregate per bucket as **average and maximum**: averages
hide the spikes that alerts are about. A 5 s series is 17 280 points per day per server.
Downsample (for example 1 min averages and maxima after 24 h) for long-term storage.

## Memory (`ramMetrics[]`)

| Field | Unit | Source | Notes |
| --- | --- | --- | --- |
| `timestamp` | UTC | sample time | every `Monitoring:Ram:PollIntervalSeconds` (default 5 s) |
| `usagePercent` | %, 0 to 100 | `(totalMb - availableMb) / totalMb × 100` | |
| `availableMb` | MiB | `\Memory\Available MBytes`, falling back to `GlobalMemoryStatusEx` | memory available to processes without paging, including standby cache |
| `totalMb` | MiB | installed physical memory | constant until hardware changes |

Display: percent line with an "available" tooltip. Aggregate as average and maximum of
`usagePercent`, or minimum of `availableMb`.

## Disks (`diskMetrics[]`)

One entry per **fixed** drive that is ready (no removable, network or CD drives), read every
`Monitoring:Disk:PollIntervalSeconds` (default 60 s).

| Field | Unit | Notes |
| --- | --- | --- |
| `driveName` | | `C:\` style root with trailing backslash (JSON-escaped as `"C:\\"`); also mount roots |
| `totalGb` | GiB (2³⁰ bytes) | drive capacity |
| `freeGb` | GiB | free space for all users (not quota-limited) |
| `usagePercent` | %, 0 to 100 | `(total - free) / total × 100`; 0 for a zero-size drive |
| `isStale` | | `true` once the reading is older than its [window](#staleness-isstale); absent from older agents (unknown) |

Display: a bar per drive with used/free; warn at the agent's thresholds (`Monitoring:Disk:WarningThresholdPercent`
70, `CriticalThresholdPercent` 85 by default, or what you pushed in [remote config](09-remote-config.md)).
A removed drive stays in the list with its last reading until it has been absent from 3
consecutive successful inventories and for at least 10 minutes; then it disappears from the list,
and an open alert for it is retired (see [Events and alerts](07-events-and-alerts.md#retirement)).
A drive that is still present but not ready keeps its last reading and `timestamp`. Once that
reading is older than max(3 × `PollIntervalSeconds`, 10 min), current agents send it with
`isStale: true`. Show it as stale. Without `isStale` (older agents), decide from the `timestamp`
yourself (see [Staleness](#staleness-isstale)).

## Disk queues (`diskQueueMetrics[]`)

| Field | Unit | Notes |
| --- | --- | --- |
| `instanceName` | | PhysicalDisk counter instance, e.g. `0 C:` (disk number and its volumes); `_Total` is excluded |
| `queueLength` | outstanding requests | `\PhysicalDisk(*)\Current Disk Queue Length`, an instantaneous value |
| `isStale` | | `true` once the reading is older than its [window](#staleness-isstale) (disk poll interval); absent from older agents |

Read with the disk poll (60 s). Instantaneous queue lengths are noisy: show a maximum over a
window, and compare with `Monitoring:Disk:QueueLengthWarningThreshold` (default 2).

## Network interfaces (`networkMetrics[]`)

| Field | Unit | Notes |
| --- | --- | --- |
| `interfaceName` | | `\Network Interface(*)` instance name (adapter description; `#` and `/` characters are replaced by Windows, e.g. `Intel[R] ...`) |
| `outputQueueLength` | packets | `\Network Interface(*)\Output Queue Length`; normally 0 on modern NICs |
| `isStale` | | `true` once the reading is older than its [window](#staleness-isstale); absent from older agents |

Every `Monitoring:Network:PollIntervalSeconds` (default 30 s). Any value above
`OutputQueueWarningThreshold` (default 0) raises a network alert. Virtual and disconnected
adapters appear too.

## Windows services (`serviceStatuses[]`)

One entry per name in `Monitoring:Services:WatchedServices`, every `PollIntervalSeconds` (30 s).

| Field | Notes |
| --- | --- |
| `serviceName` | service key name as configured (e.g. `W3SVC`) |
| `displayName` | display name from the Service Control Manager; equals `serviceName` when not found |
| `status` | `Running`, `Stopped`, `StartPending`, `StopPending`, `ContinuePending`, `PausePending`, `Paused`, or `NotFound` (the service does not exist or can't be queried) |
| `isRunning` | `true` only for `Running` |
| `isStale` | `true` once the status is older than its [window](#staleness-isstale): `status` and `isRunning` are the last ones read; absent from older agents |

Display: green for `Running`, amber for `*Pending`/`Paused`, red for `Stopped`/`NotFound`. The
agent restarts stopped services automatically (unless they are disabled or a maintenance window is
active), so a `Stopped` that turns `Running` 10 s later is normal and comes with events.

## IIS application pools (`applicationPoolStatuses[]`)

One entry per name in `Monitoring:ApplicationPools:WatchedApplicationPools`, every 30 s.

| Field | Notes |
| --- | --- |
| `applicationPoolName` | pool name as configured |
| `status` | `Started`, `Stopped`, `Starting`, `Stopping`, `NotFound` (no pool with that name exists) or `Unknown` (IIS unavailable or the query failed). Treat the set as open: accept and display values you don't know |
| `isRunning` | `true` only for `Started` |
| `isStale` | `true` once the status is older than its [window](#staleness-isstale): `status` and `isRunning` are the last ones read; absent from older agents |

## Endpoints (`endpointStatuses[]`, after-1.0.19)

One entry per configured check (`Monitoring:Endpoints`), each on its own interval (default 60 s).
Absent from the batch until at least one check produced a result.

| Field | Unit | Notes |
| --- | --- | --- |
| `name` | | unique per agent; the alert key is `endpoint:<name>` |
| `type` | | `Http` (status code and optional body match), `Tcp` (connect), `Certificate` (TLS handshake and certificate lifetime) |
| `target` | | URL or `host:port`, credentials and query string removed |
| `status` | | `Up`; `Down` (failed, or certificate expired or invalid); `Expiring` (reachable, certificate inside its warning window, default 21 days); `ConfigurationError` (the check is still configured but its configuration is invalid, so it does not run; see below). Open set: accept others |
| `isUp` | | `false` for `Down`; for `ConfigurationError`, the value of the last result |
| `responseTimeMs` | ms | absent when the check didn't get far enough to measure |
| `certificateDaysLeft` | days, 0.1 precision | absent when no certificate was inspected; negative when expired |
| `detail` | | e.g. `HTTP 200`, `Connection refused`, certificate summary; for `ConfigurationError`, `Configuration error: <error> Last result: <status>[: <detail>]` |
| `isStale` | | `true` once the result is older than max(2 × `IntervalSeconds` + `TimeoutSeconds`, 10 min) of the check, `ConfigurationError` included; absent from older agents (unknown). See [Staleness](#staleness-isstale) |

The agent alerts only after `FailuresBeforeAlert` (default 2) consecutive failures, but every
single result is reported here.

**`ConfigurationError`.** Endpoint checks are configured locally by the operator (remote
configuration can't change them). When the operator leaves a check
configured but invalid, even only for a moment while editing it, the check stops running, its
open `endpoint:<name>` alert is retired and its entry keeps the **last result**: `timestamp`,
`type`, `target`, `isUp`, `responseTimeMs` and `certificateDaysLeft` stay as they were, `status`
becomes `ConfigurationError` and `detail` carries the error and the last result, for example
`Configuration error: Monitoring:Endpoints:0:Target must be an absolute http(s) URL. Last result:
Down: HTTP 503`. A check that never produced a result gets no entry. Once the check is fixed it
runs at once and reports a real result with a new `timestamp`; once it is removed, its entry
goes. Display: grey or "not checked", with the detail. Don't treat it as up or down, and don't
alert on `isUp` for it. Because the `timestamp` doesn't change, a portal that keeps the newest
entry per name must let an equal `timestamp` from a later batch (`batchedAt`) replace it. The
same goes for `isStale`, which turns `true` on a later batch once the kept result is older than
the check's window.

## The agent object

`agent` (after-1.0.19) describes the agent itself and is in every batch from current agents.

| Field | Notes | Use it for |
| --- | --- | --- |
| `version` | e.g. `1.6.0`; `0.0.0-dev` for development builds | fleet version overview; feature detection ([Versioning](12-versioning-and-compatibility.md#detecting-agent-version-and-capabilities)) |
| `machineName` | Windows computer name | display name default (don't use it as identity) |
| `os` | e.g. `Microsoft Windows 10.0.20348` | OS inventory (`10.0.20348` = Windows Server 2022) |
| `startedAt` | agent **process** start, `+00:00` offset | a change means the agent restarted (update, crash, AgentRestart, reboot) |
| `uptimeSeconds` | seconds since `startedAt` | agent uptime; not machine uptime |
| `updatePhase` | `Idle`, `Checking`, `UpToDate`, `Available`, `Downloading`, `ReadyToInstall`, `Installing`, `Failed` | update status column; `Available` plus `availableVersion` = an update is offered |
| `availableVersion` | present when the feed offers a newer version | "update to x.y.z" button (command 304) |
| `lastUpdateResult` | `{toVersion, success, completedAt}` of the last install | show failed updates; `completedAt` carries an offset that may be local |
| `maintenance` | `{active, untilUtc?, reason?}` | suppress paging while `active`; show the window and reason. `untilUtc` can be present and past while `active` is false (an expired window) |

### Liveness

There is no heartbeat endpoint. The batch **is** the heartbeat: a current agent sends one every
flush interval (30 s by default; your remote config may change it). An agent with nothing to
report at all sends no batch. In practice that is never the case, because CPU and RAM are sampled
every 5 s. A portal SHOULD consider an agent stale after about 3 flush intervals without any
request, and offline after about 10. It SHOULD use the time of the last authenticated request of
any kind, since command polls also prove the agent is alive. Mind that `batchedAt` of a batch
delivered late is old even though the agent is alive.

## Aggregation cheat sheet

| Metric | Rollup | Alert-style view |
| --- | --- | --- |
| CPU %, RAM % | avg + max per bucket | max per bucket |
| RAM available MB | min per bucket | min |
| Disk usage % | last per bucket | last |
| Disk queue, NIC queue | max per bucket | max |
| Service, pool, endpoint status | last state + transitions | time in `Down` / `Stopped` |
| Endpoint response time | avg, p95 per bucket | p95 |
