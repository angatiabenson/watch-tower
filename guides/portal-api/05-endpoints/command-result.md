# POST /api/v1/agent/commands/{commandId}/result

[Portal API guide](../README.md) › [Endpoints](README.md) › commands/{commandId}/result

## Purpose and timing

Reports how a command ended. Sent:

- right after the action handler returns (success or failure), or after the execution timeout
  (`CommandPoll:ExecutionTimeoutSeconds`, default 120 s);
- immediately when the agent refuses a command ("Not authorized: ...", or "Rejected: ..." for
  its `issuedAt`);
- after an agent restart (crash, kill, reboot, update), for the commands it had stored:
  - one that was **running** when the agent stopped is never run again (it may have had
    effects). At start-up it is failed with `Interrupted: the agent stopped while this command
    was running; its outcome is unknown`;
  - one that was stored but **not started yet** (no `scheduledAt`) is picked up at the start of
    every poll cycle. Still within the age limit (`MaxCommandAgeSeconds` + `ClockSkewSeconds`,
    3600 + 300 s, from `issuedAt`, or from when the agent received it if `issuedAt` was absent;
    no limit when `MaxCommandAgeSeconds` is 0), it runs normally and reports its result.
    Otherwise it is failed with `Rejected: command expired before it could run (agent
    restarted)`, retried like every rejection. Such a command was usually acknowledged already;
  - a deferred one (with `scheduledAt`) runs when due, as before.

  So results can arrive long after you sent the command, and after an agent restart (see
  `agent.startedAt`);
- again, until the portal answers 2xx, on a per-result backoff: after the 1st, 2nd, 3rd, 4th and
  5th failed attempt the result is due again 1, 2, 4, 8 and 16 minutes later, then every 30
  minutes. Due results go out on the next command-poll cycle (default 30 s), at most 50 per
  cycle, the longest-due first, so a result you keep refusing doesn't hold back newer ones.
  Results survive agent restarts.

The agent gives up on a result it could not deliver within
`VisibilityPortal:CommandPoll:ResultRetryMaxHours` (default 168 h, at least 1) of producing it,
and logs a warning: `Gave up delivering the result of command <id>: the portal did not accept it
in <n> attempt(s) over 168 h (produced <time>)`. It also keeps at most 1000 rejected commands
(`Rejected: ...`); beyond that the oldest are deleted, delivered or not. In both cases the
command ID stays known to the agent, so the command never runs later. A portal that was down
for longer than a week, or refused results for that long, can therefore miss results: show such
commands as "outcome unknown".

## Request

```http
POST /api/v1/agent/commands/6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10/result HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

Success:

<!-- schema: CommandResult -->
```json
{
  "commandId": "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10",
  "success": true,
  "message": "Service 'W3SVC' restarted successfully",
  "executedAt": "2026-11-03T10:40:12.4520000Z"
}
```

Refused by the local allow-list:

<!-- schema: CommandResult -->
```json
{
  "commandId": "8e1f3a5c-7b9d-4c2e-a4f6-0b8d2c4e6f43",
  "success": false,
  "message": "Not authorized: 'WinDefend' is not a locally watched service",
  "executedAt": "2026-11-03T10:42:00.1000000Z"
}
```

Rejected because it had no `issuedAt` while the operator set
`VisibilityPortal:RequireCommandIssuedAt` (never acknowledged or executed):

<!-- schema: CommandResult -->
```json
{
  "commandId": "3c5e7a9b-1d2f-4b6c-8e0a-2f4d6b8c0e65",
  "success": false,
  "message": "Rejected: issuedAt is required",
  "executedAt": "2026-11-03T10:44:30.2000000Z"
}
```

Interrupted by an agent restart (it was running; it is not run again):

<!-- schema: CommandResult -->
```json
{
  "commandId": "d2b4f6a8-0c1e-4a3b-8d5f-7e9a1c3b5d54",
  "success": false,
  "message": "Interrupted: the agent stopped while this command was running; its outcome is unknown",
  "executedAt": "2026-11-03T10:50:03.0000000Z"
}
```

Stored but not started before the agent stopped, and too old to run after the restart (it never
ran, although it was usually acknowledged):

<!-- schema: CommandResult -->
```json
{
  "commandId": "5e7a9c1d-3f2b-4d6e-8a0c-4b6d8f0a2c76",
  "success": false,
  "message": "Rejected: command expired before it could run (agent restarted)",
  "executedAt": "2026-11-03T12:02:41.5000000Z"
}
```

| Field | Type | Unit / range | Present | Meaning | Since |
| --- | --- | --- | --- | --- | --- |
| `commandId` | string | | always | Same as the path parameter. | base |
| `success` | boolean | | always | Whether the action did what was asked. Some no-ops count as success ("already running", "Already up to date"). | base |
| `message` | string | English, free text | always | Outcome as written by the handler. Show it; don't parse it. | base |
| `executedAt` | date-time | UTC, agent clock | always | When execution started, or when the command was refused or found interrupted. | base |

Per-action messages: [Commands](../08-commands.md#action-catalog).

## Responses and agent behaviour

| Response | Agent behaviour |
| --- | --- |
| any 2xx | The result is marked sent and not sent again. |
| 401 | Logged at Error; retried after the backoff above. |
| anything else, timeout, network error | Logged at Warning; retried after the backoff above. |

## Idempotency

- **Key:** `(agent, commandId)`. The first result for a command wins. Store it, and answer 2xx to
  every later copy without changing anything. A copy arrives whenever your 2xx was lost.
- A result can arrive without an acknowledgement (the acknowledgement is not retried).
- A result for a `commandId` you don't know (state lost, command deleted, a stray ID): the portal
  SHOULD answer 2xx and log it. Any other answer makes the agent retry it every 30 minutes for a
  week, then log that it gave up.

## Common mistakes

- Answering 404 or 409 to an unknown or duplicate result: a week of retries and a warning in the
  agent log for every such result.
- Assuming every command eventually gets a result: one undelivered for `ResultRetryMaxHours`
  (168 h) is given up on.
- Assuming `success: true` for AgentUpdate (304) means installed: it means the download (and
  restart countdown) started. Watch `agent.version`, `agent.updatePhase` and
  `agent.lastUpdateResult` in the following batches.
- Assuming `success: true` for AgentRestart (303) means the agent is back: the result is sent
  before the restart. Watch `agent.startedAt` change.
- Treating an `Interrupted: ...` result as a failure of the action: it may or may not have taken
  effect. Check the resource's state in the next batches before retrying with a new command.
- Expecting a `Rejected: ...` result only before an acknowledgement: `command expired before it
  could run (agent restarted)` follows one.
- Treating a `Rejected: ...` result (stale, future, unparseable or missing `issuedAt`, or expired
  before it could run) as an
  execution failure: the command never ran. Issue a new command (new `commandId`, current `issuedAt`) if it is still wanted.
