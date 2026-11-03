# POST /api/v1/agent/events

[Portal API guide](../README.md) › [Endpoints](README.md) › events

## Purpose and timing

Delivers one incident per request: a threshold breach, a spike, a service or pool outage, an
endpoint result, a reboot, or the resolution of an alert. Events are written to the outbox when
they happen and sent on the next outbox cycle (default every 30 s), **before** any metrics
batch (they have the higher priority). How events and alerts relate:
[Events and alerts](../07-events-and-alerts.md).

## Request

```http
POST /api/v1/agent/events HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

An alert opening, with the busiest processes:

<!-- schema: AgentEvent -->
```json
{
  "eventId": "7d0c2f8e-1b5a-4f0e-8c3d-2a9e6b4f1c22",
  "timestamp": "2026-11-03T10:20:00.0000000Z",
  "incidentType": 0,
  "description": "CPU at 99.6% for over 300s",
  "topProcesses": [
    { "name": "w3wp", "pid": 4412, "cpuSeconds": 1890.2, "memoryMb": 812.5, "cpuPercent": 71.3 },
    { "name": "sqlservr", "pid": 2280, "cpuSeconds": 9120.7, "memoryMb": 4096, "cpuPercent": 19.9 }
  ],
  "alertKey": "cpu",
  "resolved": false
}
```

The same alert resolving:

<!-- schema: AgentEvent -->
```json
{
  "eventId": "1e4b7a90-6c2d-4e8f-b1a3-5d7c9e0f2a33",
  "timestamp": "2026-11-03T10:31:00.0000000Z",
  "incidentType": 0,
  "description": "Resolved after 11 min: CPU at 99.6% for over 300s",
  "alertKey": "cpu",
  "resolved": true
}
```

An incident outside any alert (also what every 1.0.x agent sends):

<!-- schema: AgentEvent -->
```json
{
  "eventId": "5f2d8b3a-7c1e-4a9f-b6d0-3e8a1c5f7b66",
  "timestamp": "2026-11-03T11:00:30.0000000Z",
  "incidentType": 7,
  "description": "Service 'World Wide Web Publishing Service' (W3SVC) is Stopped"
}
```

A one-shot event (an alert key, no `resolved`):

<!-- schema: AgentEvent -->
```json
{
  "eventId": "2c8f1a6d-9e3b-4c7a-a5d2-6f0e8b1c3d55",
  "timestamp": "2026-11-03T06:00:12.0000000Z",
  "incidentType": 16,
  "description": "The server restarted at 2026-11-03 05:58 UTC. WatchTower was not stopped cleanly before it: possible crash, power loss or forced reset.",
  "alertKey": "reboot"
}
```

### Fields

| Field | Type | Unit / range | Present | Meaning | Since |
| --- | --- | --- | --- | --- | --- |
| `eventId` | string (UUID) | | always | Idempotency key, random, fixed when queued, the same on every retry. | base |
| `timestamp` | date-time | UTC, `Z` | always | When it happened. Can be days before arrival. | base |
| `incidentType` | integer (open enum) | 0 to 16 today | always | What happened; table in [Events and alerts](../07-events-and-alerts.md#incident-types). | base |
| `description` | string | English, free text | always | Human-readable. Do not parse it; it may change in any release. | base |
| `topProcesses[]` | array | 3 (CPU) or 5 (RAM) by default | optional | Busiest processes at the time: by CPU on types 0 and 1, by working set on types 2 and 3. | base |
| `topProcesses[].name` | string | | always | Process name without `.exe`. | base |
| `topProcesses[].pid` | integer | | always | Process ID. | base |
| `topProcesses[].cpuSeconds` | number | s | always | Processor time used since the process started. | base |
| `topProcesses[].memoryMb` | number | MiB | always | Working set. | base |
| `topProcesses[].cpuPercent` | number | % of all logical processors | always | CPU share over the sampling interval. | base |
| `alertKey` | string | e.g. `cpu`, `disk:C:\`, `svc:W3SVC` | optional | The alert this event belongs to. Compare case-insensitively. | post-1.0.18 |
| `resolved` | boolean | | optional | `false` opened or escalated the alert, `true` closed it. Absent for one-shot events and incidents without an alert. | post-1.0.18 |

## Responses and agent behaviour

| Response | Agent behaviour |
| --- | --- |
| any 2xx | Delivered; body ignored. |
| 401 | Failure, logged at Error; retried. |
| any other status, timeout, network error | Failure, logged at Warning; retried. |

Retry: exponential backoff per event, 30 s, 60 s, 120 s, ... capped at 15 minutes, **without an
attempt limit**. An event is only dropped once it has waited longer than
`Outbox:EventMaxAgeHours` (default 168 h, one week) undelivered. The first failure in a cycle ends
the cycle. An event waiting in backoff does not block newer events, so they can overtake it.

## Idempotency and deduplication

- **Key:** `(agent, eventId)`. Answer duplicates 2xx and store them once.
- Do not deduplicate on `(alertKey, incidentType)` or on the description: an alert can open, close
  and open again with identical text.
- **Ordering:** order by `timestamp`, never by arrival. A `resolved: true` can arrive before the
  `resolved: false` it closes (the open was in backoff). See
  [the alert lifecycle](../07-events-and-alerts.md#alert-lifecycle) for handling that.

## Common mistakes

- Treating `resolved: false` as "always a new alert": a second `false` for an open key is an
  **escalation** (for example an endpoint going from Expiring to Down) or the same alert seen
  again. Update the open alert.
- Expecting the `resolved: true` event to have a "recovery" `incidentType`: CPU, RAM, disk,
  disk-queue and network alerts close with the **same** type that opened them.
- Requiring `alertKey`: 1.0.x agents never send it, and current agents omit it for incidents
  outside an alert (spikes, ServiceDown, ApplicationPoolDown).
- Paging someone during a maintenance window: the agent still sends events while
  `agent.maintenance.active` is true (only its own notifications are held). Decide in the portal.
- Answering 4xx to an event you can't parse: the agent retries it for a week.
- Rendering `description` as HTML.
