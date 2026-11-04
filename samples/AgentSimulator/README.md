# AgentSimulator

A small cross-platform console app that behaves like a WatchTower agent, so you can test a portal
on Linux, macOS or Windows without a Windows server to install the agent on.

It does not re-implement the wire format: it compiles the agent's own files from `src/WatchTower`
(the DTOs, `HttpsPortalClient`, `AgentAuthHandler`, `PortalTls`, `EnrollmentCode` and
`RemoteConfigValidator`), and it uses the same HttpClient pipeline the agent registers. What
reaches your portal is byte for byte what a real agent sends. The behaviour around those calls
(outbox ordering and retries, command freshness, deduplication, allow-list, deferred commands,
result retries, configuration validation and acknowledgement) follows the agent's workers.
Only the timings are shorter by default (see below).

Guide: [Testing your portal](../../guides/portal-api/11-testing-your-portal.md).

## Run it

```bash
# terminal 1: a portal (yours, or the reference one)
dotnet run --project samples/MinimalPortal

# terminal 2: the enrollment code your portal issued
dotnet run --project samples/AgentSimulator -- --code WT1.eyJ2IjoxLC... --scenario smoke
```

The code can also come from the `WATCHTOWER_ENROLLMENT_CODE` environment variable. As with the
real agent, a code with `requireTls: true` and an `http://` URL is refused.

## Scenarios

| Scenario | What it does |
| --- | --- |
| `smoke` (default) | A conformance check of every operation, sent through the agent's own authenticated HttpClient pipeline with the real status code checked: batch, duplicate batch, 1.0.x-style batch, unknown fields and enum values, events (open, duplicate, resolve, one-shot, no alert, unknown type), command poll (200 array the agent can parse, or 204), acknowledgement, result and a repeated result, config fetch (200 with both `config` and `version`, or 204), the agent's validation, acknowledgement and 204 for the current version, and a wrong token that must get exactly 401. Prints PASS, FAIL or SKIP per check. Checks that need a fixture are SKIPped when it is missing: queue a command (e.g. 301) and push a configuration for the agent before the run. Exit code 0 when everything passed, 1 when a check failed, 3 (INCOMPLETE) when nothing failed but checks were skipped. |
| `run` | Behaves like an agent for `--duration` seconds: samples CPU/RAM every 5 s, flushes a batch every `--interval`, polls commands, polls config every `--config-interval`, and now and then plays a CPU alert or a service outage. |
| `alerts` | Sends one example of every incident type (0 to 16) and every alert lifecycle shape: opened, resolved, recovery types, retirement, one-shot, no alert key. |
| `duplicate` | Sends the same batch three times and an alert's close before its open, then the open twice. A correct portal stores one batch, two events and ends with the alert closed. |
| `outage` | Replays what an agent's outbox holds after the portal was unreachable for `--backlog-minutes` (default 120): one batch per 30 s with past timestamps, events first. Measures how fast your portal absorbs it. |
| `portal-down` | Like `run` (default 10 minutes) but logs the outbox after every flush. Stop and restart your portal while it runs and watch the queue grow and drain. |

## Commands

Queue commands in your portal while `run` or `portal-down` is running; the simulator acknowledges,
executes (simulated) and reports them with the agent's own result messages:

- 101 to 103 and 201 to 204 only for names on the LOCAL watch lists (`--services`, `--pools`;
  defaults `W3SVC,Spooler` and `DefaultAppPool`), otherwise
  `Not authorized: 'X' is not a locally watched service`. `--allow-portal-managed-targets`
  behaves like the agent setting of the same name. The service state change shows up in the next
  batch.
- 301 sends a batch immediately, 302 fetches config immediately.
- 303 simulates a restart (uptime starts again).
- 304 reports "Already up to date" unless you pass `--available-version 1.7.0`; then it
  "downloads", restarts and reports the new version in the `agent` object. `Version` pinning
  behaves like the agent.
- 401 validates `ConfigJson` with the agent's validator and acknowledges its `Version`.
- 501 succeeds for no `Channel` or `Channel=Email`, otherwise fails like an agent without that
  channel.
- Like the agent, a command without `issuedAt` is accepted with a warning that replay protection is
  reduced; one whose `issuedAt` is unparseable, older than 3600 + 300 s or more than 300 s in the
  future is not executed and is answered with a failed result giving the reason.

## Options

| Option | Default (agent default) | Meaning |
| --- | --- | --- |
| `--code` | | Enrollment code (`WT1.`...). |
| `--scenario` | `smoke` | See above. |
| `--duration` | 120 s for run, 600 s for portal-down | `0` runs until Ctrl+C. |
| `--interval` | 10 s (30 s) | Outbox flush and command poll interval. Remote config `portal.*` values override it, as on the agent. |
| `--config-interval` | 30 s (300 s) | Remote configuration poll interval. |
| `--backlog-minutes` | 120 | Outage length for `outage`. |
| `--machine`, `--version` | `SIM-<agentId>`, `0.0.0-sim` | Reported in the `agent` object. |
| `--available-version` | none | Version the simulated update feed offers. |
| `--services`, `--pools` | `W3SVC,Spooler`, `DefaultAppPool` | Local watch lists (also the command allow-list). |
| `--allow-portal-managed-targets` | off | Let portal-pushed watch lists widen the allow-list. |
| `--verbose` | off | Log every HTTP request. |

Retry timings are shortened too: metrics batches back off 10 s x attempt (agent: 60 s) and are
dropped after 5 failed attempts (agent: 5); events back off 5 s doubling to 60 s (agent: 30 s
doubling to 15 min) and are never dropped by the simulator (agent: after 7 days).

## Not in the agent's package

The project is in `WatchTower.slnx` so it is built with everything else, but
`build/package.ps1` publishes only `src/WatchTower`.
