# POST /api/v1/agent/commands/{commandId}/acknowledge

[Portal API guide](../README.md) › [Endpoints](README.md) › commands/{commandId}/acknowledge

## Purpose and timing

Tells the portal that the agent received and stored a command and will execute it now or when
it is due (`scheduledAt`). Sent once per delivery, immediately after the command passed the
freshness check and was stored, **before** it runs. If the portal returns the command again, the
agent acknowledges it again (and does not run it again).

## Request

```http
POST /api/v1/agent/commands/6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10/acknowledge HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

Path parameter `commandId`: the command's ID, percent-encoded (`Uri.EscapeDataString`, so
unreserved characters `A-Z a-z 0-9 - . _ ~` are sent as is).

<!-- schema: AcknowledgeCommand -->
```json
{
  "commandId": "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10",
  "acknowledgedAt": "2026-11-03T10:40:12.4410000Z"
}
```

| Field | Type | Unit / range | Present | Meaning | Since |
| --- | --- | --- | --- | --- | --- |
| `commandId` | string | | always | Same as the path parameter. | base |
| `acknowledgedAt` | date-time | UTC, agent clock | always | When the agent stored the command. | base |

## Responses and agent behaviour

| Response | Agent behaviour |
| --- | --- |
| any 2xx | Done. |
| anything else, timeout, network error | Logged (401 at Error, others at Warning) and **ignored**. The acknowledgement is never retried, and the command runs anyway. |

## Idempotency

Acknowledgements for the same command can arrive more than once (re-delivery). Make the transition
"pending → acknowledged" idempotent and answer 2xx every time. For a `commandId` you don't know
(for example after your portal lost state), the portal SHOULD answer 2xx as well. Nothing useful
happens on a 404.

## Common mistakes

- Treating "no acknowledgement" as "not received": the acknowledgement is best-effort. Use the
  result as the source of truth, and keep re-delivering unacknowledged commands only until they
  expire.
- Rejecting a result because no acknowledgement was recorded.
- Comparing `acknowledgedAt` with portal time without allowing for clock skew.
