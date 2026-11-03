# GET /api/v1/agent/commands/pending

[Portal API guide](../README.md) › [Endpoints](README.md) › commands/pending

## Purpose and timing

The agent asks for work. Polled every `VisibilityPortal:CommandPoll:PollIntervalSeconds` (default
**30 s**; `0` or less disables command polling), starting right after the service starts. Each
cycle, in order: poll, handle each returned command, run deferred commands that are due, retry
unsent results, purge old command records. The full lifecycle and the action catalog:
[Commands](../08-commands.md).

## Request

```http
GET /api/v1/agent/commands/pending HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
```

No query parameters, no body.

## Response

`204 No Content` when there is nothing, or `200` with a JSON array:

<!-- schema: AgentCommand[] -->
```json
[
  {
    "commandId": "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10",
    "actionType": 103,
    "parameters": { "ServiceName": "W3SVC", "TimeoutSeconds": "60" },
    "issuedAt": "2026-11-03T10:40:00Z"
  },
  {
    "commandId": "3b7e2d14-8c5f-4a61-9e2b-1d4f6a8c0e21",
    "actionType": 301,
    "parameters": {},
    "issuedAt": "2026-11-03T10:40:05Z"
  },
  {
    "commandId": "c4a8e6f2-1b3d-4e5f-8a7c-9d0b2e4f6a32",
    "actionType": 204,
    "parameters": { "AppPoolName": "ShopPool" },
    "scheduledAt": "2026-11-03T23:00:00Z",
    "issuedAt": "2026-11-03T10:41:00Z"
  }
]
```

`[]` and the JSON literal `null` also mean "nothing".

### Fields (portal to agent)

| Field | Type | Required by the agent | Rules | Meaning |
| --- | --- | --- | --- | --- |
| `commandId` | string | yes | Non-blank. Unique per agent for at least `CommandPoll:RetentionDays` (30 days); use a UUID and never reuse. | Identity and dedup key. Blank: the command is ignored (logged). |
| `actionType` | integer | yes | A JSON number. A string (`"103"`) makes the **whole response** unreadable. | What to do; see the [catalog](../08-commands.md#action-catalog). Unknown numbers produce a failed result. |
| `parameters` | object of string → string, or null | no | Every value MUST be a JSON string (`"60"`, `"true"`). Keys are case-sensitive (`ServiceName`, not `serviceName`). A non-string value makes the whole response unreadable. | Action arguments. `null`/absent = none. |
| `scheduledAt` | date-time string or null | no | No offset = UTC. | Run at or after this time. Not more than 10 s in the future (or absent, null, unparseable) = run now. |
| `issuedAt` | date-time string | yes (with the default settings) | No offset = UTC. Within `MaxCommandAgeSeconds` (3600 s) + `ClockSkewSeconds` (300 s) in the past and at most `ClockSkewSeconds` in the future. | Replay protection. Missing, unparseable, too old or future commands are **silently dropped**: no acknowledgement, no result. |

Property names are camelCase and matched **case-sensitively**: `CommandId` is not read (the command
then has no ID and is ignored). Unknown properties are ignored.

## Responses and agent behaviour

| Response | Agent behaviour |
| --- | --- |
| 200 with an array | Each element is handled in order, isolated: one failing command doesn't stop the others. |
| 200 with `[]` or `null` | Nothing to do. |
| 204 | Nothing to do. |
| 200 with a body that is not a JSON array of commands (bad types, empty body, HTML) | Logged ("PollCommands failed"); **none** of the commands are handled; retried at the next poll. |
| 401, any other status | Logged ("PollCommands returned N"); nothing to do; next poll on schedule. |
| timeout, network error | Logged; next poll on schedule. |

## Idempotency and deduplication

The agent stores each accepted command by `commandId` and ignores repeats. A repeated command is
acknowledged again but never executed twice, including after an agent restart, for as long as
the record is kept (30 days after it finished).

So the portal SHOULD return every command that is not acknowledged yet on **every** poll. That is
the only way to survive a lost acknowledgement or a response that was cut off. Once a command is
acknowledged, stop returning it.

## Common mistakes

- Numbers or booleans in `parameters` (`"TimeoutSeconds": 60`): the whole poll fails, every poll,
  until that command is removed. One bad command blocks all commands for that agent.
- PascalCase property names, or `actionType` as a name (`"ServiceRestart"`).
- An old `issuedAt` (a command created hours ago and only now released, or a server clock that is
  wrong): the agent drops it without any answer. Expire commands in the portal after about 50
  minutes without acknowledgement, and run NTP.
- `issuedAt` in local time without an offset: the agent reads it as UTC.
- Re-issuing a retry of a failed command with the same `commandId`: the agent ignores it. Use a new
  ID.
- Returning commands for every agent of a tenant instead of the authenticated agent.
- Expecting the agent to respect array order across polls: within one response it handles
  commands in order, but deferred ones run when due.
