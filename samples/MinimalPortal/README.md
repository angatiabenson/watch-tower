# MinimalPortal

A small, self-contained ASP.NET Core minimal-API portal that implements every endpoint of the
WatchTower portal agent API v1 ([spec](../../spec/portal-agent-api.v1.openapi.yaml),
[guide](../../guides/portal-api/README.md)). Use it to:

- read a complete, commented implementation of the contract (`PortalApi.cs`);
- pair a real WatchTower agent, or the [AgentSimulator](../AgentSimulator/README.md), and watch
  the data arrive;
- compare your own portal's behaviour with a known-good one.

Everything is kept in memory. When you restart the process, every agent must be enrolled again.
It is a sample: there is no persistence, UI, user management or high availability.

The repository's contract tests (`tests/WatchTower.Tests/PortalContract`) run the agent's real
`HttpsPortalClient` and its outbox, command and configuration loops against this portal
in-process, so it is guaranteed to match the agent.

## Requirements

The .NET 10 SDK on Windows, Linux or macOS. Nothing else.

## Run it

```bash
dotnet run --project samples/MinimalPortal
```

The output shows the address, an admin key and an **enrollment code** for a first agent named
`agent-01`:

```text
MinimalPortal is listening on http://localhost:5080
Public URL (in enrollment codes): http://localhost:5080
Admin key (header X-Admin-Key):   3q2+7w...

Enrollment code for agent 'agent-01':
  WT1.eyJ2IjoxLCJ1cmwiOiJodHRwOi8vbG9jYWxob3N0OjUwODAiLC...
```

Options (command-line `--Name value`, or environment variables):

| Option | Default | Meaning |
| --- | --- | --- |
| `--urls` | `http://localhost:5080` | Where to listen (standard ASP.NET Core setting). Use `http://0.0.0.0:5080` to accept agents from other machines. |
| `--PublicUrl` | the first `--urls` value | The URL agents use to reach the portal; it goes into enrollment codes. Set it when listening on `0.0.0.0` or behind a proxy, e.g. `--PublicUrl http://devbox.lan:5080`. |
| `--PathBase` | none | Serve everything below a path prefix, e.g. `/watchtower`; the public URL then ends with it. Requests outside the prefix get 404, as they would behind a reverse proxy. |
| `--AgentId` | `agent-01` | ID of the agent created at start-up. |
| `--AdminKey` | random | Key for the `/admin` API. |

### HTTPS

An agent refuses plain HTTP unless the enrollment code says `requireTls: false`. Codes for an
`http://` public URL do, which is fine on a test network. For HTTPS, the certificate must be
trusted by the agent's Windows machine. The ASP.NET Core development certificate is only valid
for `localhost`:

```bash
dotnet dev-certs https --trust
dotnet run --project samples/MinimalPortal -- --urls https://localhost:5443
```

## Enroll an agent

On the Windows server, in an elevated prompt (the service restarts and starts reporting within
a minute):

```powershell
WatchTower.exe enroll WT1.eyJ2IjoxLC...
# or while installing:
.\install.ps1 -EnrollmentCode WT1.eyJ2IjoxLC...
```

Without a Windows server, run the simulator from a second terminal:

```bash
dotnet run --project samples/AgentSimulator -- --code WT1.eyJ2IjoxLC... --scenario smoke
```

## The admin API (sample only)

Not part of the agent contract. Every call needs `X-Admin-Key: <admin key>`.

| Call | Purpose |
| --- | --- |
| `GET /admin/agents` | List agents. |
| `POST /admin/agents` with `{"agentId":"srv-02"}` | Create an agent; returns its token and enrollment code. |
| `GET /admin/agents/{agentId}` | Everything stored for an agent: telemetry, snapshots, events, open alerts, commands, configuration state. |
| `POST /admin/agents/{agentId}/commands` with `{"actionType":103,"parameters":{"ServiceName":"W3SVC"}}` | Queue a command (`scheduledAt` optional). |
| `PUT /admin/agents/{agentId}/config` with a remote configuration object | Store a new configuration version (validated with the agent's rules first). |
| `POST /admin/agents/{agentId}/token` | Rotate the token; returns a new enrollment code (re-enroll the agent with it). |
| `DELETE /admin/agents/{agentId}` | Revoke the agent (its requests get 401 from then on). |

```bash
KEY=<admin key>
curl -s -H "X-Admin-Key: $KEY" http://localhost:5080/admin/agents/agent-01
curl -s -H "X-Admin-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"actionType":301}' http://localhost:5080/admin/agents/agent-01/commands
curl -s -X PUT -H "X-Admin-Key: $KEY" -H "Content-Type: application/json" \
  -d '{"cpu":{"thresholdPercent":90},"maintenance":{"untilUtc":"2026-11-03T18:00:00Z","reason":"Patching"}}' \
  http://localhost:5080/admin/agents/agent-01/config
```

## What it demonstrates

- Authentication with `X-Agent-Id` plus `Authorization: Bearer`, tokens stored as SHA-256 hashes
  and compared in constant time; 401 with `WWW-Authenticate: Bearer` on failure.
- Deduplication on `batchId`, `eventId` and `commandId`; 2xx for duplicates so the agent stops
  retrying.
- CPU and RAM samples stored by timestamp (also deduplicates batches from older agents without
  `batchId`); snapshots keep the newest reading even when an old batch arrives late, and are
  keyed case-insensitively (the agent may report a resource under a new letter case).
- Endpoint status `ConfigurationError` (a check left invalid keeps its last result and its
  timestamp): an equal timestamp is ordered by `batchedAt`, and the admin view shows each entry's
  `health` (`up`, `warning`, `down`, `not checked (configuration error)`, or `unknown (<status>)`
  for values from later agents) instead of trusting `isUp`.
- Stale snapshot entries: the admin view shows every snapshot entry's `freshness`. With
  `isStale: true` it is `stale (no reading since <timestamp>)`: the resource is still listed
  but the agent couldn't read it within its window, so the values are the last ones read. A
  missing `isStale` (agents that predate it) is treated as unknown and judged from the timestamp
  instead (`batchedAt - timestamp` over 10 minutes is stale), with the result marked "judged
  from the timestamp". An equal timestamp is ordered by `batchedAt` for every snapshot, so a
  late older batch can't make a stale entry fresh again.
- The alert lifecycle from `alertKey` and `resolved`, decided by event timestamps rather than
  arrival order: an opening or escalation older than the alert's current state is kept as
  history only (it never overwrites a newer severity or description), a close only ends the
  alert when it is newer than the alert's last transition, and a close and an opening with the
  same timestamp mean "closed, then opened again" in either arrival order (an endpoint going
  from Down to Expiring sends both at one instant). Timestamps cannot settle every reordering,
  so a real portal also reconciles open alerts against the snapshots in each metrics batch.
- Commands re-delivered until acknowledged, expired before the agent would reject them as stale,
  results accepted without a prior acknowledgement and first result wins.
- Remote configuration versions, 204 when the agent is current, acknowledgement tracking and
  validation with the agent's own rules (including the camelCase, case-sensitive names).

## Not in the agent's package

This project is in `WatchTower.slnx` so it builds and is tested with everything else, but
`build/package.ps1` publishes only `src/WatchTower`, so it never ships with the agent.
