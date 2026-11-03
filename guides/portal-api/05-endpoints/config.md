# GET /api/v1/agent/config

[Portal API guide](../README.md) › [Endpoints](README.md) › config

## Purpose and timing

The agent asks whether its remote configuration is current. Polled every
`VisibilityPortal:RemoteConfig:PollIntervalSeconds` (default **300 s**), right after start-up, and
immediately after a ForceConfigSync (302) command. Not polled at all when the operator set
`VisibilityPortal:RemoteConfig:Enabled = false`. What the configuration can contain and how it is
applied: [Remote configuration](../09-remote-config.md).

## Request

```http
GET /api/v1/agent/config?version=2026-11-03.4 HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
```

| Query parameter | Present | Meaning |
| --- | --- | --- |
| `version` | optional | The version the agent applied last (percent-encoded). Absent until the agent has applied one. It can also be a version pushed by an ApplyConfiguration (401) command, or one the portal no longer knows. |

## Response

`204 No Content` when `version` is your current version for this agent, or when you have no
configuration for it. Otherwise `200`:

<!-- schema: ConfigFetchResponse -->
```json
{
  "version": "2026-11-03.5",
  "config": {
    "cpu": { "pollIntervalSeconds": 5, "thresholdPercent": 90, "sustainedSeconds": 600 },
    "ram": { "thresholdPercent": 92 },
    "disk": { "warningThresholdPercent": 75, "criticalThresholdPercent": 90, "queueLengthWarningThreshold": 4 },
    "network": { "outputQueueWarningThreshold": 10 },
    "services": { "watchedServices": ["W3SVC", "MSSQLSERVER"] },
    "appPools": { "watchedApplicationPools": ["DefaultAppPool", "ShopPool"] },
    "portal": { "outboxFlushIntervalSeconds": 30, "commandPollIntervalSeconds": 15, "remoteConfigPollIntervalSeconds": 120 },
    "maintenance": { "untilUtc": "2026-11-03T18:00:00Z", "reason": "Patch Tuesday" }
  }
}
```

| Field | Type | Required by the agent | Rules | Meaning |
| --- | --- | --- | --- | --- |
| `version` | string | yes | Non-empty; opaque to the agent. | Label of this configuration; echoed in later polls and in the acknowledgement. |
| `config` | object | yes | See [Remote configuration](../09-remote-config.md#fields-and-validation) for every field and range. `{}` clears all remote overrides. | The settings to apply. |

If either is null or missing, the agent treats the response as "no change". Property names are
camelCase and case-sensitive (`cpu`, not `Cpu`); unknown names are ignored by the agent, which
makes typos silent. Validate on your side.

## Responses and agent behaviour

| Response | Agent behaviour |
| --- | --- |
| 204 | Nothing to apply. |
| 200 with `config` and `version` | Validates the whole object with the agent's rules. If valid: writes it as the agent's portal overlay (`remote-config.json`), records `version`, then acknowledges on the same cycle (see [config-acknowledge](config-acknowledge.md)). If invalid: logs a warning with the first problem, keeps the previous version and does **not** acknowledge. |
| 200 with `config` or `version` null, or a body that does not parse (for example a fractional number where a whole number is required) | Logged; no change. |
| any other status, timeout, network error | Logged; no change; next poll on schedule. |

The agent does not compare the returned `version` with its own. A 200 is always applied, even
with the same version. Answer 204 when the agent is current. Otherwise the agent rewrites its
overlay and acknowledges again on every poll.

## Versioning and idempotency

- `version` is the idempotency key. The same version MUST always mean the same content; create
  a new version for every change.
- Versions are opaque strings to the agent and are only compared for equality. A counter, a
  timestamp or a content hash all work.
- When the agent reports a version you don't know (it was set by an ApplyConfiguration command,
  or belongs to a deleted configuration), answer with your current one, or with an empty
  configuration (`{}`) if you have none and want to clear remote overrides. Answering 204 leaves
  the agent on what it has.

## Common mistakes

- Answering 200 with the same version on every poll (works, but every 300 s the agent rewrites
  its configuration and you get an acknowledgement).
- Sending only the changed setting: the configuration **replaces** the previous remote one, so
  everything not in the new version falls back to local settings.
- Waiting for an acknowledgement that never comes: the configuration was invalid. The agent logs
  why, but the portal is not told. Validate with the agent's rules before publishing.
- Fractional numbers for whole-second settings (`"pollIntervalSeconds": 5.0` is fine in JSON
  only if your serializer writes `5`).
- Sending `maintenance.untilUtc` more than 7 days ahead.
