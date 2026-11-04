# POST /api/v1/agent/config/acknowledge

[Portal API guide](../README.md) › [Endpoints](README.md) › config/acknowledge

## Purpose and timing

Tells the portal that a configuration version is in effect on the agent. After the agent wrote a
new version (from [`GET config`](config.md) or an ApplyConfiguration command), it sends this on
the same config-poll cycle, and on every following cycle (default every 300 s) until the portal
answers 2xx. Only the latest applied version is acknowledged. If a newer version is applied before
an older one was acknowledged, the older acknowledgement is never sent.

## Request

```http
POST /api/v1/agent/config/acknowledge HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

<!-- schema: ConfigAcknowledge -->
```json
{ "version": "2026-11-03.5" }
```

| Field | Type | Present | Meaning | Since |
| --- | --- | --- | --- | --- |
| `version` | string | always | The version now in effect. | base |

There is no timestamp. Use the time you received it.

## Responses and agent behaviour

| Response | Agent behaviour |
| --- | --- |
| any 2xx | Marked acknowledged; not sent again. |
| 401 | Logged at Error; retried next config cycle. |
| anything else, timeout, network error | Retried next config cycle. |

## Idempotency

Record "agent X has version V since T" idempotently and answer 2xx to repeats. Answer 2xx as well
for a version you don't know (an ApplyConfiguration version, or one you deleted), and SHOULD
record it, because it tells you what the agent runs.

## What an acknowledgement means

- It means **written and recorded**: the overlay file was replaced and the agent's configuration
  reloads from it when the file changes. The monitors and the portal loops read their settings
  at the start of every cycle, so each change takes effect on the affected loop's next cycle. A
  new poll interval applies after the current wait ends.
- It does not mean the agent's local operator settings agree. Environment variables on the
  server still win over the portal (see [layering](../09-remote-config.md#layering)).
- No acknowledgement within two or three poll intervals after the agent fetched a version means
  it was rejected as invalid, or the agent can't reach this endpoint.

## Common mistakes

- Answering 404 for unknown versions: the agent retries every 300 s for as long as that version
  is its current one.
- Waiting for the acknowledgement of an older version after pushing a newer one: the agent keeps
  only its current version, so the older one is never acknowledged once the newer one is applied.
- Treating the acknowledgement of version N as "every earlier version was applied": it only
  means N is current.
