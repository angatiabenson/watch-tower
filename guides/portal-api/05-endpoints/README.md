# 05 Endpoints

[Portal API guide](../README.md) › Endpoints

One page per endpoint. All paths are relative to the agent's BaseUrl (which MAY include a path
prefix, see [Transport](../04-transport-and-security.md#baseurl-path-prefixes-and-reverse-proxies)).
Every request carries `Authorization: Bearer <token>` and `X-Agent-Id: <agentId>`
([Authentication](../03-authentication-and-enrollment.md)).

| Endpoint | Direction of data | Called | Retried by the agent |
| --- | --- | --- | --- |
| [`POST /api/v1/agent/metrics/batch`](metrics-batch.md) | agent → portal | every 30 s, and right after command 301 | yes, up to 5 attempts, then dropped |
| [`POST /api/v1/agent/events`](events.md) | agent → portal | when an incident happens (sent on the next 30 s cycle) | yes, for up to 7 days |
| [`GET /api/v1/agent/commands/pending`](commands-pending.md) | portal → agent | every 30 s | next poll |
| [`POST /api/v1/agent/commands/{commandId}/acknowledge`](command-acknowledge.md) | agent → portal | once per received command | no |
| [`POST /api/v1/agent/commands/{commandId}/result`](command-result.md) | agent → portal | when a command ends | yes, backing off from 1 min to every 30 min, for up to 7 days |
| [`GET /api/v1/agent/config`](config.md) | portal → agent | every 300 s, and right after command 302 | next poll |
| [`POST /api/v1/agent/config/acknowledge`](config-acknowledge.md) | agent → portal | after applying a version | yes, every 300 s, while that version is the current one (a newer applied version supersedes it) |

The intervals are the agent's defaults. Operators can change them, and so can your portal through
[remote configuration](../09-remote-config.md) (`portal.*`).

## Status codes in one table

| Response | metrics/batch, events | commands/pending, config | acknowledge (command) | result, config/acknowledge |
| --- | --- | --- | --- | --- |
| 2xx | delivered, removed from the outbox | body parsed (204 = nothing) | done | done |
| 3xx | failure (redirects are not followed; logged with the `Location`), retried after the backoff | as "nothing new" | ignored | failure, retried |
| 401 | failure, logged as an error, retried | as "nothing new", logged | ignored, logged as an error | failure, logged as an error, retried |
| other 4xx | failure, retried | as "nothing new" | ignored | failure, retried |
| 5xx | failure, retried | as "nothing new" | ignored | failure, retried |
| timeout (30 s), connection error | failure, retried | as "nothing new" | ignored | failure, retried |

A 3xx (any, 304 included) is never followed. The agent logs at most once per 15 minutes per
endpoint: `Portal redirected <path> to <absolute Location>; update VisibilityPortal:BaseUrl. The redirect
(<status>) was not followed and the call counts as failed (<n> more redirect(s) of this endpoint
since the last warning)`, with `(no Location header)` when there is none.

The agent never reads response bodies of POSTs, and no status code means "stop retrying". A
portal that wants the agent to stop sending something answers 2xx and discards it.

## Field tables

Each endpoint page lists every field with:

- **Type**: JSON type (`number` is an IEEE 754 double, `integer` is a whole number).
- **Unit / range**: what the number measures and the values the agent produces or accepts.
- **Present**: `always` (every v1 agent sends it), `optional` (may be absent; the agent never sends
  `null`), or for portal-to-agent fields whether the agent requires it.
- **Since**: `base` (every released v1 agent, up to and including 1.0.19), `1.0.19` (added in
  1.0.19) or `after-1.0.19` (added after 1.0.19, unreleased).
