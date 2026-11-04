# 13 Troubleshooting and FAQ

[Portal API guide](README.md) › Troubleshooting and FAQ

## Where the agent tells you what went wrong

| Place | What |
| --- | --- |
| `%ProgramData%\WatchTower\Logs\watchtower-YYYYMMDD.log` | Main log, one file per day, kept 30 days. Every portal failure is logged here with the path and status code. |
| `%ProgramData%\WatchTower\Logs\startup-YYYYMMDD.log` | Start-up problems (invalid configuration, data directory) |
| Agent web console → Logs | The same log, in the browser (default `https://<server>:<port>/`, see `WatchTower.exe status`) |
| Agent web console → Dashboard / Settings | Portal connection settings (BaseUrl, agentId, protocol), maintenance and update state; which settings come from the portal |
| `WatchTower.exe status` | Service state, data directory, logs path, console address |
| `%ProgramData%\WatchTower\config\remote-config.json` | The overlay written from your last applied configuration |
| Windows Event Viewer → Application | Service start and stop failures |

A different data directory (`--data-dir`, `WATCHTOWER_DATA_DIR`) moves all of these. Useful log
lines to search for:

| Log text | Meaning |
| --- | --- |
| `Portal returned 401 Unauthorized on api/v1/agent/... Check AgentToken.` | Credentials refused on a POST |
| `Portal request to {path} returned {status}` | Any other non-2xx on a POST |
| `Portal request to {path} failed` | Network, TLS or timeout error on a POST (exception attached) |
| `PollCommands returned {status}` / `PollCommands failed` | Command poll refused / unreadable or network error |
| `FetchConfiguration returned {status}` / `FetchConfiguration failed` | Config poll refused / unreadable or network error |
| `Portal send failed; deferring N remaining outbox message(s) to the next cycle` | Outbox stopped at a failure |
| `Dropping metrics batch ... after 5 failed attempts` | Metrics lost |
| `Rejecting command {id} (ActionType=...): ...` | Command refused as stale, future, with an unparseable `issuedAt`, or without one while `RequireCommandIssuedAt` is set; never acknowledged or run, a failed result `Rejected: ...` is sent |
| `Command {id} was still executing when the agent stopped; marked failed` | After a restart: the command is not run again; its result is `Interrupted: ...` |
| `Running command {id} (ActionType=...): received ... but not run before the agent stopped` | After a restart: a stored command that never started runs now; its result follows |
| `Rejecting stored command {id} (ActionType=..., received ...) that was never run: ...` | After a restart: a stored command that never started is too old now; its result is `Rejected: command expired before it could run (agent restarted)` |
| `Gave up delivering the result of command {id}: the portal did not accept it in N attempt(s) over 168 h (produced ...)` | The result endpoint refused that result (or was unreachable) for `ResultRetryMaxHours`; the result is lost |
| `More than 1000 rejected commands are stored; dropped the N oldest ...` | The portal keeps sending commands the agent rejects; the oldest rejections (and their results, if undelivered) are deleted |
| `Portal sends commands without issuedAt; replay protection is reduced. Update the portal.` | Commands without `issuedAt` (accepted; logged once per process or BaseUrl) |
| `Portal redirected {path} to {Location}; update VisibilityPortal:BaseUrl. ...` | The portal answered 3xx; the call failed (at most once per 15 min per endpoint) |
| `Portal does not accept compressed requests; sending uncompressed (the portal answered {status} to a compressed request) ...` | `CompressRequests=Gzip`, but the portal refused the encoding (415, gRPC UNIMPLEMENTED, or a 400 three times in a row that the plain body then passed). That request was resent uncompressed; compression stays off until a restart or a change of the setting or BaseUrl. Fix the portal or set `CompressRequests=Off` |
| `Portal refused a compressed request (400 Bad Request) but accepted it uncompressed; sending uncompressed for 60 min ...` | A single ambiguous 400: compression is suspended for an hour and then tried again. Repeated, it switches off (row above) |
| `Ignoring command without a CommandId` | Missing or wrongly cased `commandId` |
| `Refusing unauthorized command ...` | Allow-list refusal (a result is sent) |
| `Remote config version {v} failed validation: ...` | Configuration rejected (no acknowledgement) |
| `Remote configuration updated to version {v}` | Configuration applied |
| `{Drive\|Physical disk\|Network interface\|Service\|Application pool\|Endpoint check} {name} has had no new reading since {time} (freshness window N min): it is still reported with that reading, marked stale. {reason}` | That entry is sent with `isStale: true`. Logged once when it goes stale, at most once per hour per resource; the reason says what to check |
| `{kind} {name} is being read again (reading at {time}); it is no longer reported as stale` | The entry is fresh again (`isStale: false`) |

## Symptoms

### The agent gets 401 in a loop

- The token or agentId in your portal doesn't match the agent's: re-issue an enrollment code and
  run `enroll` again. The agent never re-enrolls by itself.
- You rotated the token and revoked the old one before the operator re-enrolled (accept both
  during rotation).
- A proxy in front of your portal strips `Authorization` or `X-Agent-Id`, or your portal
  **redirected** (for example http→https or a missing trailing slash). The agent never follows
  redirects: every 3xx fails the call. Look for `Portal redirected ... to ...; update
  VisibilityPortal:BaseUrl` in the log (at most once per 15 minutes per endpoint).
- You compare `X-Agent-Id` case-sensitively against a differently cased stored ID.

### No data arrives at all

- Is the portal connection enabled? `VisibilityPortal:Enabled` must be true (enroll sets it).
- The service wasn't restarted after the settings changed (`enroll --no-restart`).
- TLS: the server doesn't trust your certificate (private CA not installed, wrong host name,
  incomplete chain). The log shows `Portal request ... failed` with an
  `AuthenticationException`.
- `RequireTls` is true but BaseUrl is `http://`: the agent refuses to start. See the startup log.
- Wrong path prefix: the agent calls `BaseUrl/api/v1/agent/...`. Check your access log for 404s.
- A firewall or proxy blocks outbound traffic from the server. Proxy settings come from the
  service's environment or the service account's Windows proxy settings.
- Your server rejects chunked request bodies (411 Length Required, or 400 from a WAF).

### Data arrives twice

Expected: delivery is at least once. Deduplicate on `batchId` / `eventId` and on series
timestamps. Repeated snapshots (disks, services) are by design.

### Data arrives late, with old timestamps

The portal was unreachable or refused messages, and the outbox is replaying (up to 500 messages
per 30 s cycle). Events may come before the metrics of the same period. Use payload timestamps.
Gaps in CPU/RAM after a long outage are batches that ran out of attempts.

### An endpoint check reports `ConfigurationError`, or never changes

The check is still configured but invalid in the operator's local settings (remote configuration
can't change endpoint checks), so it
doesn't run. Its entry keeps the last result and its `timestamp`, and `detail` says what is
wrong: `Configuration error: <error> Last result: <status>[: <detail>]`. Fix the check: it then
runs at once. If your portal still shows the old status, it only replaces an entry when the
`timestamp` is newer; let an equal `timestamp` from a later batch replace it too. Once the last
result is older than the check's window (at least 10 minutes), the entry also comes with
`isStale: true`.

### A resource shows an old reading, or `isStale: true`

The resource is still listed but the agent can't read it any more: for example a fixed drive
that is not ready, a disk or adapter the performance counters no longer return, a monitor that
keeps failing, or a misconfigured endpoint check. The entry keeps its last reading and
`timestamp` until the resource is removed, which can be indefinitely. Current agents mark it
`isStale: true` once that reading is older than its window (max(3 × poll interval, 10 min), or
max(2 × interval + timeout, 10 min) for an endpoint check; see
[Staleness](06-telemetry-reference.md#staleness-isstale)). The server's log has one warning
`... has had no new reading since ...` explaining the likely cause.

What to do in the portal:

- `isStale: true`: show the entry as stale (grey, "no reading since `<timestamp>`"). Don't treat
  its last value as current or alert on it.
- `isStale` missing: the agent is older and doesn't send it. That means unknown, not fresh. Fall
  back to the timestamp: show the entry as stale when `batchedAt - timestamp` exceeds the
  window (10 minutes with the default intervals).
- `isStale: true` on many entries in the first batch after the agent restarted: those readings
  predate the restart. The next batches clear it as the monitors read them again.

### Commands stay pending

- The agent can't parse your poll response: one command with a number in `parameters` (`60`
  instead of `"60"`) or a string `actionType` (`"103"`, `"ServiceRestart"`) makes the whole
  array unreadable, so NO command of that response runs, on every poll, until that command is
  gone. Look for `PollCommands failed` (with a JSON exception) in the log.
- PascalCase or misspelled property names are different: unknown properties are ignored, so each
  such command is read with defaults and fails on its own. Without `commandId` it is skipped
  (`Ignoring command without a CommandId (ActionType=...)`), and so is one with `CommandId`
  (wrong case). With `commandId` but `ActionType` it runs as action 0 and fails
  (`Unknown action type: 0`). `IssuedAt` is not read, so the command counts as one without `issuedAt` (accepted with a replay-protection warning, or rejected when
  the operator set `RequireCommandIssuedAt`).
- `issuedAt` is older than 3600 + 300 s, more than 300 s in the future (check the server's clock)
  or unparseable, or it is missing and the operator set `VisibilityPortal:RequireCommandIssuedAt`.
  Look for `Rejecting command` in the log; the portal gets a failed result `Rejected: ...`.
- You return the command to a different agentId than the one polling.
- Command polling is disabled (`CommandPoll:PollIntervalSeconds` ≤ 0) or the portal connection is
  off.

### A command was acknowledged but never got a result

- It is scheduled (`scheduledAt` in the future), or still running (up to 120 s).
- The agent is offline. The result comes when it is back: a command that was running when it
  stopped is reported as `Interrupted: ...` at the next start (it is not run again); one it had
  stored but not started runs then, or is reported as `Rejected: command expired before it could
  run (agent restarted)` if it has become too old.
- Your result endpoint answers non-2xx: the agent retries it 1, 2, 4, 8 and 16 minutes later, then
  every 30 minutes, and gives up after `CommandPoll:ResultRetryMaxHours` (168 h) with `Gave up
  delivering the result of command ...` in its log. Check your logs for repeated
  `POST .../result`.
- The `commandId` was used before (a reused ID, or an ID derived from something that repeats).
  The agent remembers every ID it has received (365 days and up to 200 000 IDs by default),
  acknowledges a repeat and never executes it again, so no new result comes. Use a new UUID per
  command.

### "Not authorized" results

The service or pool is not on the agent's **local** watch list, or it is WatchTower's own
service. A name pushed by remote configuration doesn't count unless the operator enabled
`AllowPortalManagedTargets`. See [Commands](08-commands.md#the-local-allow-list-allowportalmanagedtargets).

### A ServiceStop "worked" but the service is running again

The agent restarts watched services it finds stopped. Push a maintenance window first. See
[Commands](08-commands.md#101-servicestart-102-servicestop-103-servicerestart).

### Configuration is not applied

- No acknowledgement: this does not prove a rejection. The agent may be offline, the acknowledgement
  may have failed to deliver, or a newer applied version superseded it; check which version the agent
  reports in its later `GET config?version=...` requests. A real rejection logs
  `Remote config version ... failed validation` (naming the first bad value); JSON that didn't parse
  (wrong types) logs `FetchConfiguration failed`.
- Acknowledged but no effect:
  - you used PascalCase or misspelled names (silently ignored);
  - an environment variable on the server overrides the setting;
  - the setting isn't remote-configurable at all ([list](09-remote-config.md#what-a-portal-cannot-change)).
- The operator disabled remote configuration (`RemoteConfig:Enabled = false`): the agent never
  calls `GET config`.
- A later version left a section out, so the agent fell back to its local value.

### The same configuration is acknowledged again every few minutes

Your `GET config` answers 200 even when `version` is current. Answer 204.

### TLS errors

| Error in the agent log | Cause |
| --- | --- |
| `The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot` | private or self-signed CA not trusted by the server |
| `RemoteCertificateNameMismatch` | BaseUrl host name not in the certificate's SAN |
| `The SSL connection could not be established` (inner `Win32Exception`) | no common TLS version or cipher; very old Windows needs TLS 1.2 enabled |
| `... ClientCertThumbprint ... no such certificate` | mTLS configured on the agent but the certificate isn't in `LocalMachine\My` (or has no private key) |

## FAQ

**Can the portal push commands instantly?** No. The agent polls every 30 s by default. Lower it
with remote configuration (`portal.commandPollIntervalSeconds`, minimum 1 s) if you can afford the
load.

**Can one agent report to two portals?** No. One BaseUrl, one identity. Fan out on the portal side.

**Do I have to implement every endpoint?** For a working agent, yes. A read-only portal can answer
`commands/pending` and `config` with 204 forever and still MUST answer the POSTs with 2xx.

**Can I get historical data the agent collected before enrollment?** There is no history API.
But the agent forwards CPU and RAM samples from its local database starting at the oldest one it
still retains (the forwarding position starts at 0), so the first batches after the portal is
enabled can contain samples collected before that, possibly hours or days old. Store them by
their own timestamps. Events are only queued while the portal connection is enabled.

**Is there a heartbeat?** The batch every 30 s is the heartbeat (see
[Liveness](06-telemetry-reference.md#liveness)).

**Which time zone are timestamps in?** UTC, always. `lastUpdateResult.completedAt` may carry a
local offset. Parse offsets, never strip them.

**Is the API the same for the agent's local web console?** No. The console has its own private
API. Only the API in these pages is a public contract.
