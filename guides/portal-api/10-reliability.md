# 10 Reliability

[Portal API guide](README.md) › Reliability

## The outbox

Metrics batches and events go through a durable queue in the agent's SQLite database before they
are sent. Commands and configuration don't. They are polled, and their acknowledgements and
results have their own retry rules (see below).

```mermaid
stateDiagram-v2
    [*] --> Pending: queued (batch every flush cycle, event when it happens)
    Pending --> Sent: 2xx
    Pending --> Backoff: failure (non-2xx, timeout, network)
    Backoff --> Pending: retry time reached
    Backoff --> Failed: metrics batch: 5th failed attempt
    Pending --> Failed: event older than EventMaxAgeHours (168 h)
    Sent --> [*]: purged after 48 h
    Failed --> [*]: purged after 48 h
```

| Property | Metrics batch | Event |
| --- | --- | --- |
| Priority | 0 | 1 (sent first) |
| Idempotency key | `batchId` (post-1.0.18) | `eventId` |
| Backoff after the n-th failure | `RetryBackoffSeconds × n` = 60, 120, 180, 240 s | 30 s × 2ⁿ⁻¹, capped at 15 min |
| Gives up | after `MaxRetryAttempts` (5) failed attempts | when older than `EventMaxAgeHours` (168 h) |
| Survives agent restarts | yes | yes |

Each flush cycle (default every 30 s):

1. take up to `MaxBatchSize` (500) messages that are due, highest priority first, then oldest
   first;
2. send them one by one, one HTTP request each;
3. at the **first failure**, record an attempt for that message only, and end the cycle. The rest
   are untouched and are tried again next cycle. The failed message itself is only due again once
   its backoff has expired (a batch: `RetryBackoffSeconds` × attempt, so at least 60 s by default;
   an event: 30 s, 60 s, 120 s ... up to 15 min), so later cycles may deliver other messages
   before it. Delivery order is therefore not guaranteed after a failure.

### Guarantees and non-guarantees

- **At least once.** A message is removed only after a 2xx. A 2xx lost on the way back (timeout
  after your portal stored it) means the message is sent again. Deduplicate.
- **Not exactly once, not in order.** Events go before metrics. A message in backoff is overtaken
  by newer ones. Two agents' clocks differ. Always order by the timestamps in the payload.
- **Bounded for metrics.** A batch that failed five times is dropped. Its CPU/RAM samples never
  reach the portal (disk, service and other snapshots are repeated in later batches anyway).
  Expect gaps in CPU/RAM history after long portal outages.
- **A week for events.** Incidents survive a portal outage of up to seven days.

### The portal is down for an hour, a day, a week

While the portal fails, every flush cycle still queues a new batch, and the oldest due message
gets one failed attempt per cycle. With one attempt per cycle and batches waiting longer and
longer between attempts, how long a particular batch survives depends on how many messages are
queued. The agent doesn't promise any figure. In practice:

| Outage | What reaches the portal afterwards |
| --- | --- |
| a few minutes | everything: all batches and events, delivered in a burst |
| hours | all events; most batches, with gaps where batches ran out of attempts |
| days (up to 7) | all events; CPU/RAM history largely missing; current snapshots and the agent object as soon as the next batch gets through |
| more than 7 days | events older than 7 days are dropped too |

When the portal comes back, the agent sends up to 500 queued messages per cycle, one request at a
time, as fast as your portal answers. Your portal sees a burst of old timestamps. It MUST accept
them and SHOULD update "current state" only from data newer than what it has.

### Backpressure

If your portal is overloaded, answering `429` or `503` quickly is the correct reaction. The agent
stops the cycle at that message (it ignores `Retry-After`) and retries the message once its
backoff has expired, not on the very next cycle: the first retry of a metrics batch comes after at
least 60 s by default, later ones after 120 s, 180 s and so on. Other messages that are due may be
sent in the meantime. That limits each agent to one failed request per 30 s cycle, but it also
spends retry attempts: batches refused five times are lost. Prefer scaling out over refusing for
long periods.

## Commands and configuration under failure

| Message | Retried? | Effect of a long portal outage |
| --- | --- | --- |
| command poll | next cycle | commands issued during the outage are picked up afterwards; one whose `issuedAt` is older than 3600 + 300 s is not executed and gets a failed result giving the reason |
| command acknowledgement | no | lost; the result still comes |
| command result | with a per-result backoff (1, 2, 4, 8, 16, then every 30 min), at most 50 due results per cycle, survives restarts; given up after `CommandPoll:ResultRetryMaxHours` (168 h) | delivered within 30 minutes after the outage ends; a result produced more than 168 h earlier is lost (logged by the agent). At most 1000 rejected commands are kept, so in a long outage the oldest `Rejected: ...` results can be lost too |
| command IDs | not a message: remembered for `CommandIdRetentionDays` (365 days), at most `CommandIdMaxCount` (200 000) | none; a command re-delivered after the outage is acknowledged but not executed again |
| config fetch | next cycle | applied afterwards |
| config acknowledgement | every config cycle until 2xx, survives restarts, but only while that version is the agent's current one | delivered afterwards; if a newer version was applied meanwhile, only the newer one is acknowledged |

The agent stores one remote configuration: the version it applied last. Applying a newer version
replaces it and its acknowledgement state, so an older version that was never acknowledged is
never acknowledged later. A missing acknowledgement therefore does not prove that a version was
not applied: the `version` query parameter of the next `GET config` says what the agent has.

## Agent restarts

Restarts (updates, AgentRestart, reboots, crashes) lose nothing that was queued: the outbox,
command records and results (with their retry schedule), the command IDs seen, and the applied
configuration version are in the agent's database.

- `agent.startedAt` and `agent.uptimeSeconds` reset.
- A command that was executing when the agent stopped is never run again; it is reported at the
  next start as failed: `Interrupted: the agent stopped while this command was running; its
  outcome is unknown`.
- A command that was stored but not started yet is picked up at the start of every poll cycle:
  run if still within `MaxCommandAgeSeconds` + `ClockSkewSeconds` of its `issuedAt` (or of when
  it was received), otherwise failed with `Rejected: command expired before it could run (agent
  restarted)`. Its result can come long after the command was sent, and after its
  acknowledgement.
- Snapshot entries survive restarts and the agent's daily purge (which deletes only history):
  the first batch after a restart repeats the last readings with their old timestamps.
- After a machine reboot the agent sends a ServerRestarted event (type 16).
- The CPU and RAM samples taken while the agent was stopped don't exist: expect a gap.

## Clock skew

All timestamps come from the server's clock, which can be wrong.

- Agent-generated timestamps (`timestamp`, `batchedAt`, `acknowledgedAt`, `executedAt`) are stored
  as received. A portal MAY record its own receive time next to them, and SHOULD NOT reject data
  for being "in the future". Clamp for display if needed.
- Portal-generated timestamps the agent checks: `issuedAt` (rejected when more than 300 s in the
  future or older than 3600 + 300 s by the agent's clock) and `scheduledAt` (run when the agent's
  clock reaches it). If the portal's clock is more than 5 minutes ahead of the agent's, the agent
  refuses every command (`Rejected: issuedAt <t> is more than 300s in the future ...`); if it is
  more than an hour behind, every command is stale. Detect it by comparing `agent.startedAt + uptimeSeconds` or `batchedAt` with your
  receive time (allowing for delivery delay), and warn the user.
- `maintenance.untilUtc` is evaluated with the agent's clock.

## Scaling a portal

Load per agent with default settings:

| Request | Rate per agent | Typical body |
| --- | --- | --- |
| metrics batch | 2 / min | 2 to 10 KB |
| command poll | 2 / min | none (204) |
| config poll | 0.2 / min | none (204) |
| events | rare; bursts during incidents | < 2 KB |

That's roughly 4.2 requests/min per agent. 10 000 agents is about 700 requests/s, mostly tiny
GETs answered with 204. Tips:

- Make `commands/pending` and `config` cheap: an indexed lookup or a cache keyed by agent, with a
  204 fast path. Your remote configuration can lower poll frequency for large fleets
  (`portal.commandPollIntervalSeconds`).
- Accept writes fast and process them asynchronously: validate, deduplicate on `batchId`/`eventId`
  with a unique index, store the raw body and acknowledge. Aggregate in the background.
- Agents are not synchronised, but after a portal outage or a network change many come back at
  once. Size for the replay burst (500 messages per agent per cycle).
- No session affinity is needed. Every request is self-authenticating.
- Storage sizing (CPU + RAM at 5 s): about 34 560 samples per agent per day, about 1 to 2 MB/day
  uncompressed in a row store, much less in a time-series store. Snapshots: store only on change.
  Events: a few KB per agent per day. Plan retention and downsampling.
- Index events by `(agent, timestamp)` and `(agent, alertKey)`; commands by `(agent, state)`.
