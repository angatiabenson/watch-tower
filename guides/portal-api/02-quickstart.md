# 02 Quickstart: a minimum viable portal

[Portal API guide](README.md) › Quickstart

This walkthrough builds a portal that a real agent can connect to, in any language. Each step
shows the exact HTTP the agent sends and the answer it expects, plus a `curl` command to test
your server before an agent is involved. The C# reference for every step is
[`samples/MinimalPortal/PortalApi.cs`](../../samples/MinimalPortal/PortalApi.cs).

What the agent sends on the wire (true for every request below):

- HTTP/1.1 over TLS (or plain HTTP if the operator opted in), to `BaseUrl` + the path.
- Headers: `Host`, `Authorization: Bearer <token>`, `X-Agent-Id: <agentId>` and, on POSTs,
  `Content-Type: application/json; charset=utf-8`. There is no `User-Agent`, `Accept` or
  `Accept-Encoding` header.
- POST bodies are sent with `Transfer-Encoding: chunked` and no `Content-Length`. The raw HTTP
  below shows the decoded body. Your server or proxy MUST accept chunked request bodies.
- A 30 s timeout per request.

## Step 0: pick an agentId and a token

The portal chooses both. An agentId is any non-empty string that is unique in your portal (for
example a UUID or the server name). A token is a random secret, at least 128 bits. Store only a
hash of it (see [Authentication](03-authentication-and-enrollment.md)).

```bash
AGENT=SRV-WEB-01
TOKEN=$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')
PORTAL=https://portal.example.com
```

The examples below use `TOKEN=q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp`.

## Step 1: authenticate every request

Read `X-Agent-Id`, look the agent up, compare the bearer token with the stored hash in constant
time. On any mismatch answer `401` (and SHOULD add `WWW-Authenticate: Bearer`). The agent keeps
retrying with the same credentials: it never re-enrolls by itself.

```http
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Bearer
Content-Length: 0
```

## Step 2: receive metrics batches

Every 30 s the agent posts one batch.

```http
POST /api/v1/agent/metrics/batch HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

Body (save it as `batch.json` to replay it with curl):

<!-- schema: MetricsBatch -->
```json
{
  "agentId": "SRV-WEB-01",
  "batchId": "0b6f3c1e-8f6c-4d8e-9a43-3c2a7d5e9b11",
  "batchedAt": "2026-11-03T10:15:30.0000000Z",
  "cpuMetrics": [
    { "timestamp": "2026-11-03T10:15:00.0000000Z", "usagePercent": 12.5 },
    { "timestamp": "2026-11-03T10:15:05.0000000Z", "usagePercent": 14.1 }
  ],
  "ramMetrics": [
    { "timestamp": "2026-11-03T10:15:00.0000000Z", "usagePercent": 61.2, "availableMb": 6355.4, "totalMb": 16384 }
  ],
  "diskMetrics": [
    { "timestamp": "2026-11-03T10:14:30.0000000Z", "driveName": "C:\\", "totalGb": 126.4, "freeGb": 40.2, "usagePercent": 68.2 }
  ],
  "diskQueueMetrics": [
    { "timestamp": "2026-11-03T10:14:30.0000000Z", "instanceName": "0 C:", "queueLength": 0.4 }
  ],
  "networkMetrics": [
    { "timestamp": "2026-11-03T10:15:00.0000000Z", "interfaceName": "Intel[R] 82574L Gigabit Network Connection", "outputQueueLength": 0 }
  ],
  "serviceStatuses": [
    { "timestamp": "2026-11-03T10:15:10.0000000Z", "serviceName": "W3SVC", "displayName": "World Wide Web Publishing Service", "status": "Running", "isRunning": true }
  ],
  "applicationPoolStatuses": [
    { "timestamp": "2026-11-03T10:15:10.0000000Z", "applicationPoolName": "DefaultAppPool", "status": "Started", "isRunning": true }
  ],
  "agent": {
    "version": "1.6.0",
    "machineName": "SRV-WEB-01",
    "os": "Microsoft Windows 10.0.20348",
    "startedAt": "2026-11-03T08:00:00.0000000+00:00",
    "uptimeSeconds": 8130,
    "updatePhase": "UpToDate",
    "maintenance": { "active": false }
  },
  "endpointStatuses": [
    { "timestamp": "2026-11-03T10:15:20.0000000Z", "name": "shop-home", "type": "Http", "target": "https://shop.example.com/", "status": "Up", "isUp": true, "responseTimeMs": 182, "certificateDaysLeft": 61.4, "detail": "HTTP 200" }
  ]
}
```

Store it. If you have seen this `batchId` before, store nothing, but still answer 2xx:

```http
HTTP/1.1 202 Accepted
Content-Length: 0
```

Test:

```bash
curl -i -H "Authorization: Bearer $TOKEN" -H "X-Agent-Id: $AGENT" \
  -H "Content-Type: application/json" --data-binary @batch.json \
  "$PORTAL/api/v1/agent/metrics/batch"
```

Details: [metrics-batch](05-endpoints/metrics-batch.md), [telemetry reference](06-telemetry-reference.md).

## Step 3: receive events

```http
POST /api/v1/agent/events HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

Body (`event.json`):

<!-- schema: AgentEvent -->
```json
{
  "eventId": "7d0c2f8e-1b5a-4f0e-8c3d-2a9e6b4f1c22",
  "timestamp": "2026-11-03T10:20:00.0000000Z",
  "incidentType": 0,
  "description": "CPU at 99.6% for over 300s",
  "topProcesses": [
    { "name": "w3wp", "pid": 4412, "cpuSeconds": 1890.2, "memoryMb": 812.5, "cpuPercent": 71.3 }
  ],
  "alertKey": "cpu",
  "resolved": false
}
```

Deduplicate on `eventId`, answer 2xx:

```http
HTTP/1.1 202 Accepted
Content-Length: 0
```

```bash
curl -i -H "Authorization: Bearer $TOKEN" -H "X-Agent-Id: $AGENT" \
  -H "Content-Type: application/json" --data-binary @event.json \
  "$PORTAL/api/v1/agent/events"
```

Details: [events](05-endpoints/events.md), [events and alerts](07-events-and-alerts.md).

## Step 4: hand out commands

When nothing is waiting, answer 204:

```http
GET /api/v1/agent/commands/pending HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
```

```http
HTTP/1.1 204 No Content
```

When commands are waiting, return them as a JSON array (`Content-Type: application/json`):

<!-- schema: AgentCommand[] -->
```json
[
  {
    "commandId": "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10",
    "actionType": 103,
    "parameters": { "ServiceName": "W3SVC", "TimeoutSeconds": "60" },
    "issuedAt": "2026-11-03T10:40:00Z"
  }
]
```

Three things the agent is strict about: property names are camelCase, every parameter value is a
string (`"60"`, not `60`), and `issuedAt` must be fresh: at most 3600 s + 300 s of clock-skew
allowance old (3900 s) and at most 300 s in the future, by default. A command outside those
limits is not executed; the agent answers it with a failed result giving the reason. The
`issuedAt` above is only an example: when you test, generate it at the moment you queue the
command (for example `date -u +%Y-%m-%dT%H:%M:%SZ`), never paste a literal date. Keep
returning a command on every poll until it is acknowledged.

```bash
curl -i -H "Authorization: Bearer $TOKEN" -H "X-Agent-Id: $AGENT" \
  "$PORTAL/api/v1/agent/commands/pending"
```

Details: [commands-pending](05-endpoints/commands-pending.md), [commands](08-commands.md).

## Step 5: record acknowledgements and results

Right after receiving the command, the agent acknowledges it, once:

```http
POST /api/v1/agent/commands/6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10/acknowledge HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

<!-- schema: AcknowledgeCommand -->
```json
{ "commandId": "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10", "acknowledgedAt": "2026-11-03T10:40:12.4410000Z" }
```

When it finished, it reports the result, and repeats that until you answer 2xx:

```http
POST /api/v1/agent/commands/6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10/result HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
Content-Type: application/json; charset=utf-8
Transfer-Encoding: chunked
```

<!-- schema: CommandResult -->
```json
{
  "commandId": "6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10",
  "success": true,
  "message": "Service 'W3SVC' restarted successfully",
  "executedAt": "2026-11-03T10:40:12.4520000Z"
}
```

Answer both with any 2xx (`204 No Content` is fine). The result can arrive without an
acknowledgement, and it can arrive more than once.

```bash
curl -i -H "Authorization: Bearer $TOKEN" -H "X-Agent-Id: $AGENT" -H "Content-Type: application/json" \
  --data-binary '{"commandId":"6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10","success":true,"message":"ok","executedAt":"2026-11-03T10:40:12Z"}' \
  "$PORTAL/api/v1/agent/commands/6f1c9a52-6a43-4f3c-9d55-0d9c2b7f4e10/result"
```

Details: [command-acknowledge](05-endpoints/command-acknowledge.md), [command-result](05-endpoints/command-result.md).

## Step 6: hand out configuration

The agent sends the version it has (none at first):

```http
GET /api/v1/agent/config?version=2026-11-03.4 HTTP/1.1
Host: portal.example.com
Authorization: Bearer q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp
X-Agent-Id: SRV-WEB-01
```

If that is your current version for this agent, or you have no configuration for it, answer
`204 No Content`. Otherwise answer 200 with the configuration and its version:

<!-- schema: ConfigFetchResponse -->
```json
{
  "version": "2026-11-03.5",
  "config": {
    "cpu": { "thresholdPercent": 90, "sustainedSeconds": 600 },
    "maintenance": { "untilUtc": "2026-11-03T18:00:00Z", "reason": "Patch Tuesday" }
  }
}
```

The agent validates and applies it, then acknowledges:

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

Answer 2xx. A missing acknowledgement does not prove rejection: the agent may be offline, the acknowledgement
may have failed to deliver, or a newer applied version may have superseded it. Record the version
the agent reports in later `GET config?version=...` requests, and use the agent's validation log
(`Remote config version ... failed validation`) to confirm a rejection (see [Remote configuration](09-remote-config.md)).

```bash
curl -i -H "Authorization: Bearer $TOKEN" -H "X-Agent-Id: $AGENT" "$PORTAL/api/v1/agent/config"
```

Details: [config](05-endpoints/config.md), [config-acknowledge](05-endpoints/config-acknowledge.md).

## Step 7: issue an enrollment code

Give the operator one string instead of four settings: `WT1.` followed by the base64url encoding
(no padding) of this JSON:

<!-- schema: EnrollmentCodePayload -->
```json
{"v":1,"url":"https://portal.example.com","agentId":"SRV-WEB-01","token":"q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp","requireTls":true}
```

```bash
printf '%s' '{"v":1,"url":"https://portal.example.com","agentId":"SRV-WEB-01","token":"q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp","requireTls":true}' \
  | base64 | tr '+/' '-_' | tr -d '=\n' | sed 's/^/WT1./'
```

gives

```text
WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0
```

More (encoders in C#, Python and JavaScript; seed files; rotation):
[Authentication and enrollment](03-authentication-and-enrollment.md).

## Step 8: pair a real agent and watch data arrive

On the Windows server, in an elevated PowerShell:

```powershell
# a new server: install and connect in one step
.\install.ps1 -EnrollmentCode WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0

# an installed agent
& "C:\Program Files\WatchTower\WatchTower.exe" enroll WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0
```

The service restarts, and within about 30 s the first batch arrives, followed by one every 30 s.
The first command poll and config fetch happen right after start-up.

No Windows server at hand? Use the simulator from any OS:

```bash
dotnet run --project samples/AgentSimulator -- --code WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0 --scenario smoke
```

`smoke` checks every endpoint and prints PASS/FAIL. Then work through the
[conformance checklist](11-testing-your-portal.md). If nothing arrives, see
[Troubleshooting](13-troubleshooting-faq.md).

## Or start from the reference portal

```bash
dotnet run --project samples/MinimalPortal
```

It prints an enrollment code for `http://localhost:5080`. Read
[`PortalApi.cs`](../../samples/MinimalPortal/PortalApi.cs) side by side with these pages.
