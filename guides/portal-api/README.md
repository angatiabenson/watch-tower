# Building a WatchTower portal

WatchTower is a monitoring agent for Windows servers. It watches CPU, memory, disks, network
queues, Windows services, IIS application pools and HTTP/TCP/TLS endpoints, raises alerts and can
restart what failed. On its own it is managed from a local web console. Connected to a **portal**,
a fleet of agents reports to one place, and the portal can send commands and configuration back.

This guide is for people who want to **build their own portal**: a SaaS dashboard, an internal tool
or an integration into an existing monitoring product, in any language, on any OS. The agent's
portal protocol is public, versioned (v1) and tested. These pages, the OpenAPI file and the
reference portal are all you need. You don't need access to the WatchTower authors or a Windows
machine.

## What you build

An HTTPS server with **seven endpoints** under `/api/v1/agent/`. The agent calls them. Your
portal never connects to the agent.

| # | Call | Purpose | Page |
| --- | --- | --- | --- |
| 1 | `POST /api/v1/agent/metrics/batch` | Receive metrics and the agent's self-description | [metrics-batch](05-endpoints/metrics-batch.md) |
| 2 | `POST /api/v1/agent/events` | Receive incidents and alert open/resolve events | [events](05-endpoints/events.md) |
| 3 | `GET /api/v1/agent/commands/pending` | Hand out commands | [commands-pending](05-endpoints/commands-pending.md) |
| 4 | `POST /api/v1/agent/commands/{commandId}/acknowledge` | Learn that a command arrived | [command-acknowledge](05-endpoints/command-acknowledge.md) |
| 5 | `POST /api/v1/agent/commands/{commandId}/result` | Learn how a command ended | [command-result](05-endpoints/command-result.md) |
| 6 | `GET /api/v1/agent/config` | Hand out remote configuration | [config](05-endpoints/config.md) |
| 7 | `POST /api/v1/agent/config/acknowledge` | Learn that configuration was applied | [config-acknowledge](05-endpoints/config-acknowledge.md) |

You also issue **enrollment codes** (`WT1.…`) so an operator can connect a server with one paste.

## The contract in ten lines

1. The agent is the client; it only makes outbound HTTPS (HTTP/1.1, JSON, UTF-8) requests to `BaseUrl` + `/api/v1/agent/...`.
2. Every request carries `Authorization: Bearer <token>` and `X-Agent-Id: <agentId>`; authenticate the pair and answer 401 otherwise.
3. Any 2xx means "done". Everything else (3xx, 4xx, 5xx, timeouts after 30 s) is a failure the agent retries; no status means "reject permanently".
4. Metrics batches arrive every 30 s, events as they happen, both from a durable outbox: at-least-once, deduplicate on `batchId` and `eventId`.
5. JSON property names are camelCase and the agent matches them case-sensitively; it omits nulls; you MUST ignore unknown properties and enum values.
6. Commands: return unacknowledged ones from `GET commands/pending` (200 array or 204) with a unique `commandId`, an `actionType` number, string-only `parameters` and a recent `issuedAt`.
7. The agent acknowledges a command once (not retried), executes it, and retries the result until you answer 2xx; first result wins.
8. Service and app-pool commands only work on names the operator watches locally; the agent refuses the rest with a failed result.
9. Config: answer `GET config?version=v` with 204 when `v` is current, else 200 `{config, version}`; the agent validates, applies, then retries `POST config/acknowledge` until 2xx while that version is its current one (a newer applied version supersedes it). An invalid config is never acknowledged.
10. v1 only grows by optional additions; anything breaking becomes v2 under a new path, with both served side by side during a transition.

## Map of the documentation

| Page | Read it for |
| --- | --- |
| [01 Concepts](01-concepts.md) | Architecture, glossary, who is responsible for what, flow diagrams |
| [02 Quickstart](02-quickstart.md) | A minimum viable portal, step by step, with curl and raw HTTP |
| [03 Authentication and enrollment](03-authentication-and-enrollment.md) | Tokens, headers, issuing/rotating/revoking, the `WT1.` code byte by byte, seed files |
| [04 Transport and security](04-transport-and-security.md) | TLS, certificates, plain HTTP, path prefixes, proxies, limits, security checklist |
| [05 Endpoints](05-endpoints/README.md) | One page per endpoint: fields, status codes, retries, idempotency, mistakes |
| [06 Telemetry reference](06-telemetry-reference.md) | Every metric and agent field, units and how to display them |
| [07 Events and alerts](07-events-and-alerts.md) | Incident types 0 to 16, the alert lifecycle, maintenance |
| [08 Commands](08-commands.md) | The command state machine and the full action catalog |
| [09 Remote configuration](09-remote-config.md) | What a portal can change, layering, validation, versions |
| [10 Reliability](10-reliability.md) | Outbox, ordering, outages, clock skew, scaling |
| [11 Testing your portal](11-testing-your-portal.md) | Conformance checklist, MinimalPortal, AgentSimulator, contract tests |
| [12 Versioning and compatibility](12-versioning-and-compatibility.md) | Stability promise, feature/version table, gRPC alternative |
| [13 Troubleshooting and FAQ](13-troubleshooting-faq.md) | Symptoms, causes and where the agent logs them |
| [CHANGELOG](CHANGELOG.md) | Changes to the API |

Machine-readable reference: [`spec/portal-agent-api.v1.openapi.yaml`](../../spec/portal-agent-api.v1.openapi.yaml)
(OpenAPI 3.1, JSON Schema 2020-12). Runnable references:
[`samples/MinimalPortal`](../../samples/MinimalPortal/README.md) (a portal, C#) and
[`samples/AgentSimulator`](../../samples/AgentSimulator/README.md) (an agent stand-in that runs on
Linux and macOS).

## Reading order

1. [Concepts](01-concepts.md), then the [Quickstart](02-quickstart.md): enough for a portal that
   receives data.
2. [Authentication and enrollment](03-authentication-and-enrollment.md) and
   [Transport and security](04-transport-and-security.md) before you expose anything to the
   internet.
3. The [endpoint pages](05-endpoints/README.md) while you implement each call.
4. [Telemetry](06-telemetry-reference.md), [Events and alerts](07-events-and-alerts.md),
   [Commands](08-commands.md) and [Remote configuration](09-remote-config.md) for the features
   you build on top.
5. [Reliability](10-reliability.md) and [Testing](11-testing-your-portal.md) before production.

## Conventions used in these pages

- **MUST**, **MUST NOT**, **SHOULD**, **SHOULD NOT** and **MAY** are used as defined in
  [RFC 2119](https://www.rfc-editor.org/rfc/rfc2119). They are requirements on **the portal**
  unless the sentence says otherwise. Statements about the agent describe what the agent does
  today. They are facts, not requirements.
- "The agent" means the WatchTower Windows service. Setting names such as
  `VisibilityPortal:Outbox:FlushIntervalSeconds` are the agent's configuration keys. Operators
  set them in the agent's console or settings file, and some can be set by your portal (see
  [Remote configuration](09-remote-config.md)).
- **Since** columns say which agents send a field: `base` means every released v1 agent (up to
  and including 1.0.18), and `post-1.0.18` means agents built after 1.0.18 (the first release
  after 1.0.18 and later). Older agents simply omit those fields.
- Every JSON example is complete and is validated against the OpenAPI schemas by the
  repository's tests, so the examples cannot drift from the agent.
