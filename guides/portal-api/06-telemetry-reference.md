# 06 Telemetry reference

[Portal API guide](README.md) › Telemetry reference

Everything a [metrics batch](05-endpoints/metrics-batch.md) carries, with the source on the
Windows side, the units, how often it changes and how to show it. Two kinds of data:

- **Series** (`cpuMetrics`, `ramMetrics`): every sample is in exactly one batch. Append to a time
  series keyed by timestamp.
- **Snapshots** (everything else): the latest reading of each resource, repeated in every batch.
  Keep "current state per resource" and append to history only when the reading's `timestamp`
  changes.

All timestamps are the agent's clock in UTC. See [clock skew](10-reliability.md#clock-skew).

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

Display: a bar per drive with used/free; warn at the agent's thresholds (`Monitoring:Disk:WarningThresholdPercent`
70, `CriticalThresholdPercent` 85 by default, or what you pushed in [remote config](09-remote-config.md)).
A drive that disappears from the list was removed or is not ready. Its open alert is retired
(see [Events and alerts](07-events-and-alerts.md#retirement)).

## Disk queues (`diskQueueMetrics[]`)

| Field | Unit | Notes |
| --- | --- | --- |
| `instanceName` | | PhysicalDisk counter instance, e.g. `0 C:` (disk number and its volumes); `_Total` is excluded |
| `queueLength` | outstanding requests | `\PhysicalDisk(*)\Current Disk Queue Length`, an instantaneous value |

Read with the disk poll (60 s). Instantaneous queue lengths are noisy: show a maximum over a
window, and compare with `Monitoring:Disk:QueueLengthWarningThreshold` (default 2).

## Network interfaces (`networkMetrics[]`)

| Field | Unit | Notes |
| --- | --- | --- |
| `interfaceName` | | `\Network Interface(*)` instance name (adapter description; `#` and `/` characters are replaced by Windows, e.g. `Intel[R] ...`) |
| `outputQueueLength` | packets | `\Network Interface(*)\Output Queue Length`; normally 0 on modern NICs |

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

Display: green for `Running`, amber for `*Pending`/`Paused`, red for `Stopped`/`NotFound`. The
agent restarts stopped services automatically (unless they are disabled or a maintenance window is
active), so a `Stopped` that turns `Running` 10 s later is normal and comes with events.

## IIS application pools (`applicationPoolStatuses[]`)

One entry per name in `Monitoring:ApplicationPools:WatchedApplicationPools`, every 30 s.

| Field | Notes |
| --- | --- |
| `applicationPoolName` | pool name as configured |
| `status` | `Started`, `Stopped`, `Starting`, `Stopping`, or `Unknown` (IIS not installed, pool missing, query failed) |
| `isRunning` | `true` only for `Started` |

## Endpoints (`endpointStatuses[]`, post-1.0.18)

One entry per configured check (`Monitoring:Endpoints`), each on its own interval (default 60 s).
Absent from the batch until at least one check produced a result.

| Field | Unit | Notes |
| --- | --- | --- |
| `name` | | unique per agent; the alert key is `endpoint:<name>` |
| `type` | | `Http` (status code and optional body match), `Tcp` (connect), `Certificate` (TLS handshake and certificate lifetime) |
| `target` | | URL or `host:port`, credentials and query string removed |
| `status` | | `Up`; `Down` (failed, or certificate expired or invalid); `Expiring` (reachable, certificate inside its warning window, default 21 days) |
| `isUp` | | `false` only for `Down` |
| `responseTimeMs` | ms | absent when the check didn't get far enough to measure |
| `certificateDaysLeft` | days, 0.1 precision | absent when no certificate was inspected; negative when expired |
| `detail` | | e.g. `HTTP 200`, `Connection refused`, certificate summary |

The agent alerts only after `FailuresBeforeAlert` (default 2) consecutive failures, but every
single result is reported here.

## The agent object

`agent` (post-1.0.18) describes the agent itself and is in every batch from current agents.

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
