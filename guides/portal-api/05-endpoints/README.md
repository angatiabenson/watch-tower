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
| [`POST /api/v1/agent/commands/{commandId}/result`](command-result.md) | agent → portal | when a command ends | yes, every 30 s, indefinitely |
| [`GET /api/v1/agent/config`](config.md) | portal → agent | every 300 s, and right after command 302 | next poll |
| [`POST /api/v1/agent/config/acknowledge`](config-acknowledge.md) | agent → portal | after applying a version | yes, every 300 s, indefinitely |

The intervals are the agent's defaults. Operators can change them, and so can your portal through
[remote configuration](../09-remote-config.md) (`portal.*`).

## Status codes in one table

| Response | metrics/batch, events | commands/pending, config | acknowledge (command) | result, config/acknowledge |
| --- | --- | --- | --- | --- |
| 2xx | delivered, removed from the outbox | body parsed (204 = nothing) | done | done |
| 3xx | failure (redirect followed without the token) | as "nothing new" | ignored | failure, retried |
| 401 | failure, logged as an error, retried | as "nothing new", logged | ignored, logged as an error | failure, logged as an error, retried |
| other 4xx | failure, retried | as "nothing new" | ignored | failure, retried |
| 5xx | failure, retried | as "nothing new" | ignored | failure, retried |
| timeout (30 s), connection error | failure, retried | as "nothing new" | ignored | failure, retried |

The agent never reads response bodies of POSTs, and no status code means "stop retrying". A
portal that wants the agent to stop sending something answers 2xx and discards it.

## Field tables

Each endpoint page lists every field with:

- **Type**: JSON type (`number` is an IEEE 754 double, `integer` is a whole number).
- **Unit / range**: what the number measures and the values the agent produces or accepts.
- **Present**: `always` (every v1 agent sends it), `optional` (may be absent; the agent never sends
  `null`), or for portal-to-agent fields whether the agent requires it.
- **Since**: `base` (every released v1 agent, up to 1.0.18) or `post-1.0.18`.
