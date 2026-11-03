# POST /api/v1/agent/commands/{commandId}/result

[Portal API guide](../README.md) › [Endpoints](README.md) › commands/{commandId}/result

## Purpose and timing

Reports how a command ended. Sent:

- right after the action handler returns (success or failure), or after the execution timeout
  (`CommandPoll:ExecutionTimeoutSeconds`, default 120 s);
- immediately when the agent refuses a command ("Not authorized: ...");
- at start-up, for commands that were still executing when the agent stopped (crash, reboot,
  update): they are failed with the message *Agent stopped while the command was executing; its
  outcome is unknown*;
- again on **every** command-poll cycle (default 30 s) until the portal answers 2xx, with no
  retry limit and no expiry. Up to 50 unsent results are retried per cycle, oldest first.

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

Interrupted by an agent restart:

<!-- schema: CommandResult -->
```json
{
  "commandId": "d2b4f6a8-0c1e-4a3b-8d5f-7e9a1c3b5d54",
  "success": false,
  "message": "Agent stopped while the command was executing; its outcome is unknown",
  "executedAt": "2026-11-03T10:50:03.0000000Z"
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
| 401 | Logged at Error; retried next cycle. |
| anything else, timeout, network error | Logged at Warning; retried next cycle. |

## Idempotency

- **Key:** `(agent, commandId)`. The first result for a command wins. Store it, and answer 2xx to
  every later copy without changing anything. A copy arrives whenever your 2xx was lost.
- A result can arrive without an acknowledgement (the acknowledgement is not retried).
- A result for a `commandId` you don't know (state lost, command deleted, a stray ID): the portal
  SHOULD answer 2xx and log it. Any other answer makes the agent retry it every 30 s, forever.

## Common mistakes

- Answering 404 or 409 to an unknown or duplicate result: an endless retry loop.
- Assuming `success: true` for AgentUpdate (304) means installed: it means the download (and
  restart countdown) started. Watch `agent.version`, `agent.updatePhase` and
  `agent.lastUpdateResult` in the following batches.
- Assuming `success: true` for AgentRestart (303) means the agent is back: the result is sent
  before the restart. Watch `agent.startedAt` change.
- Expecting a result for a command the agent dropped as stale: none is ever sent.
