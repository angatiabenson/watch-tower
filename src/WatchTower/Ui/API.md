# WatchTower console — HTTP API contract

The console is served by the WatchTower service itself (Kestrel, default
`http://127.0.0.1:8750`). Static assets live in `src/WatchTower/Ui/wwwroot/` and are embedded
in the executable. All API routes are under `/api`, JSON in/out (camelCase).

## Conventions

* **Auth**: cookie session (`wt_session`, HttpOnly, SameSite=Strict). Every `/api` route
  except `/api/auth/*` returns `401` when not signed in → the UI shows the sign-in screen.
* **CSRF guard**: every non-GET `/api` request MUST send header `X-WatchTower-Request: 1`
  (requests without it get `400`). Always send `Content-Type: application/json`.
* **Errors**: `4xx/5xx` bodies are `{ "error": "message" }` or, for validation,
  `{ "errors": { "<key or _>": "message" } }`.
* **CSP**: `default-src 'self'` — no inline scripts/styles, no external fonts/CDNs.
* Timestamps are ISO-8601 UTC strings; render them in the browser's local time.

## Auth

| Method & path | Body | Response |
|---|---|---|
| `GET /api/auth/state` | – | `{ setupRequired: bool, authenticated: bool, setupAllowedFromHere: bool }` |
| `POST /api/auth/setup` | `{ password }` | `204`. Only when `setupRequired`; only from the server itself (loopback). Signs the user in. `400 {error}` for weak password. |
| `POST /api/auth/login` | `{ password }` | `204` or `401 {error}`; `429` when rate-limited (10 attempts / 5 min / IP). |
| `POST /api/auth/logout` | – | `204` |
| `POST /api/auth/password` | `{ currentPassword, newPassword }` | `204` / `400 {error}`. Other sessions are signed out. |

Password rule: at least 10 characters (server enforces; show the hint in the form).

## Status (dashboard)

`GET /api/status` →

```jsonc
{
  "agent": {
    "version": "1.5.0", "machineName": "SRV-01", "os": "Microsoft Windows 10.0.20348",
    "serviceName": "WatchTower", "isWindowsService": true, "processId": 1234,
    "startedAt": "2026-11-03T09:00:00Z", "uptimeSeconds": 3600,
    "installDirectory": "C:\\Program Files\\WatchTower\\", "dataDirectory": "C:\\ProgramData\\WatchTower"
  },
  "health": { "errors": ["..."], "warnings": ["SMTP host is not configured. ..."] },
  "restartPending": false,          // a restart-required setting was saved but not yet applied
  "maintenance": { "active": true, "until": "2026-11-03T18:00:00+00:00", "reason": "Monthly patching" },
                                    // until/reason are null when none is set; until may be in the past (active: false)
  "alerts": [                       // currently open alerts, oldest first
    { "key": "disk:C:\\", "severity": "critical", "title": "Disk Critical: C:\\ at 91.0%",
      "description": "Drive C:\\ at 91.0% (9.7 GB free of 126.5 GB)", "openedAt": "2026-11-03T08:10:00Z",
      "lastNotifiedAt": "2026-11-03T08:10:00Z", "notifyCount": 1 }   // lastNotifiedAt null = held (maintenance / no channel)
  ],
  "pendingAlerts": [                // breaches inside their waiting period (…SustainedSeconds), oldest first:
                                    // nothing is sent; each becomes an alert at alertsAt if still present
    { "key": "diskqueue:0 C:", "severity": "warning", "title": "Disk Queue High: 0 C: at 3.4",
      "since": "2026-11-03T09:58:00Z", "alertAfterSeconds": 300, "alertsAt": "2026-11-03T10:03:00Z",
      "lastSeenAt": "2026-11-03T10:00:00Z" }
  ],
  "workers": [                      // monitoring worker liveness (feeds the health-gated heartbeat)
    { "name": "cpu", "lastSuccessAt": "2026-11-03T09:59:55Z", "intervalSeconds": 5, "stale": false }
  ],                                // stale = no successful cycle for 3x the interval (at least 2 min)
  "update": { "phase": "Available", "availableVersion": "1.6.0", "restartAt": null, "lastResult": null },  // see Updates
  "portal": { "enabled": false, "baseUrl": "", "agentId": "", "protocol": "Https" },
  "email": { "configured": false, "recipients": 0 },
  "metrics": {
    "cpu": { "current": 12.5, "average": 9.1, "max": 55.0, "threshold": 99,
             "series": [ { "t": "2026-11-03T09:59:00Z", "v": 12.5 } ] },          // last 60 min, ≤120 points, oldest first
    "ram": { "current": 61.2, "average": 60.0, "max": 70.1, "threshold": 90,
             "totalMb": 16384, "availableMb": 6350, "series": [ { "t": "...", "v": 61.2 } ] },
    "disks": [ { "drive": "C:\\", "totalGb": 126.5, "freeGb": 9.7, "usagePercent": 92.3,
                 "warningPercent": 70, "criticalPercent": 85 } ],
    "diskQueues": [ { "instance": "0 C:", "queueLength": 0.0 } ],
    "network": [ { "interface": "Ethernet", "outputQueueLength": 0 } ],
    "services": [ { "name": "W3SVC", "displayName": "World Wide Web Publishing Service", "status": "Running", "isRunning": true } ],
    "appPools": [ { "name": "DefaultAppPool", "status": "Started", "isRunning": true } ],
    "endpoints": [                  // Monitoring:Endpoints check results, by name (read-only, see below)
      { "name": "intranet", "type": "Http", "target": "https://intranet.example/health", "status": "Up",
        "isUp": true, "responseTimeMs": 84.2, "certificateDaysLeft": null, "detail": "HTTP 200",
        "checkedAt": "2026-11-03T09:59:30Z" },
      { "name": "intranet-tls", "type": "Certificate", "target": "intranet.example:443", "status": "Expiring",
        "isUp": true, "responseTimeMs": 31.0, "certificateDaysLeft": 12.5,
        "detail": "Certificate for intranet.example expires in 12.5 days (2026-11-16)", "checkedAt": "..." }
    ]                               // status: Up | Down | Expiring (certificate valid but inside its warning window)
  },
  "incidents": [ { "timestamp": "...", "type": "CpuSpikeDetected", "severity": "warning", "description": "..." } ]  // last 24 h, newest first, ≤50
}
```

`severity` is one of `critical | warning | info`. Any metric may be `null`/empty before the
first sample is taken — render an empty state, not an error.

Endpoint checks (`Monitoring:Endpoints`: HTTP, TCP and TLS-certificate checks) are edited in
the "Endpoint checks" settings section (an `ObjectList` field, see below; `WatchTower config
import` still works too). Their results are shown from `metrics.endpoints` (an "Endpoints" card:
name, type, target, status badge, response time, certificate days left, detail, and an "Edit"
link to `#/config/endpoints`). Render `Down` as critical and `Expiring` as warning.

## Configuration

`GET /api/config` →

```jsonc
{
  "sections": [
    { "id": "email", "title": "Email alerts", "description": "...",
      "fields": [
        { "key": "Email:SmtpPort", "label": "SMTP port", "type": "Int", "help": null,
          "min": 1, "max": 65535, "options": null, "placeholder": null,
          "restartRequired": false, "format": null, "advanced": false }
      ] }
  ],
  "values": {
    "Email:SmtpPort": { "value": 587, "source": "default", "overridden": false, "hasValue": true },
    "Email:Password": { "value": null, "source": "operator", "overridden": false, "hasValue": true },
    "Monitoring:Services:WatchedServices": { "value": ["W3SVC"], "source": "portal", "overridden": true, "hasValue": true }
  },
  "warnings": [ "Secret 'Email:Password' could not be decrypted on this machine ..." ]
}
```

Field `type` (string enum) → editor:

| type | editor | value |
|---|---|---|
| `String` | text input (validate `format: "email"` client-side if set) | string |
| `Url` | url input | string |
| `Secret` | password input, **never pre-filled**. Show "•••• set" badge when `hasValue`. Leave empty = unchanged; offer a "Clear" action. | `null` on read |
| `Int` / `Number` | number input with `min`/`max` (step 1 for Int, any for Number) | number |
| `Bool` | toggle switch | boolean |
| `Enum` | select from `options` | string |
| `Time` | `HH:mm` time input | string |
| `TimeZone` | searchable select, options from `GET /api/system/timezones` | string |
| `List` | editable chip/tag list (add/remove, validate items by `format`) | string[] |
| `Services` | list + picker dialog from `GET /api/system/services` (search by name/display name; allow free text too) | string[] |
| `AppPools` | list + picker from `GET /api/system/apppools` (if `iisInstalled` is false, show a note and allow free text) | string[] |
| `ObjectList` | collapsible entries with add/remove; each entry's properties use the editors above, described by `fields` | object[] |

`ObjectList` fields carry extra schema properties (omitted on other fields):

* `fields`: the properties of one entry, as field objects whose `key` is relative to the entry
  (e.g. `Name`). These may also have `required: true`, `default` (used for new entries and shown
  when the property is absent), and `visibleWhen: { "key": "Type", "values": ["Http"] }` (only
  relevant while another property of the entry has one of those values).
* `max`: the most entries allowed. `uniqueBy`: the entry property that must be unique
  (trimmed, case-insensitive). `itemNoun`: what one entry is called, e.g. "endpoint check".
* On a `String` entry property, `max` is a length limit. `format: "endpoint-target"` (depends on
  the entry's `Type`: Http = absolute http(s) URL, Tcp = `host:port`, Certificate = host,
  `host:port` or https URL) and `format: "status-ranges"` (e.g. `200-399,404`) are checked
  client-side and again by the server.

The value read is the whole list, in order, with every property filled in (defaults for absent
ones). Save the whole list: it replaces the previous one (`[]` removes every entry; `reset`
returns to the shipped default). Optional properties may be left out. Example for
`Monitoring:Endpoints`:

```jsonc
"Monitoring:Endpoints": [
  { "Name": "intranet", "Type": "Http", "Target": "https://intranet.example/health", "IntervalSeconds": 60,
    "TimeoutSeconds": 10, "FailuresBeforeAlert": 2, "ExpectStatus": "200-399", "ExpectBodyContains": "",
    "CertificateWarnDays": 21 },
  { "Name": "sql", "Type": "Tcp", "Target": "sql01.example:1433" }
]
```

Errors about one entry are keyed `<key>:<index>` (e.g. an unknown property) or
`<key>:<index>:<property>`, e.g. `"Monitoring:Endpoints:1:Target": "Target must be host:port."`
(index from 0). Show them on that entry.

`source`: `default | operator | portal | environment`. `overridden: true` means a higher layer
(portal or environment variable) controls the value, so editing it here has no effect until
that layer stops setting it — render the field read-only with a "Managed by portal" /
"Set by environment" badge. Fields with `advanced: true` go under an "Advanced" disclosure per
section. Fields with `restartRequired: true` get a small "restart required" hint.

`PUT /api/config` body:

```jsonc
{
  "changes": { "Email:SmtpHost": "smtp.example.com", "Email:Password": "new secret",
               "Email:ToAddresses": ["ops@example.com"], "Monitoring:Cpu:ThresholdPercent": 95 },
  "reset": [ "Display:Timezone" ]   // revert to the shipped default (remove from operator settings)
}
```

Send only fields the user changed. For secrets send the new value, or `""` to clear; omit to
keep. Responses:

* `200 { "restartRequired": bool, "warnings": [..] }` — saved and (unless restartRequired) live
  within ~1 s.
* `400 { "errors": { "Email:SmtpPort": "Must be between 1 and 65535.", "_": "general error" } }` —
  nothing saved; show messages next to fields.

## System lookups

* `GET /api/system/services` → `[ { "name": "W3SVC", "displayName": "...", "status": "Running", "startType": "Automatic" } ]`
* `GET /api/system/apppools` → `{ "iisInstalled": true, "pools": [ { "name": "DefaultAppPool", "state": "Started" } ] }`
* `GET /api/system/timezones` → `[ { "id": "Africa/Nairobi", "displayName": "(UTC+03:00) Nairobi" } ]`

## Actions

* `POST /api/actions/test-email` → `200 { "ok": true, "message": "..." }` / `200 { "ok": false, "message": "..." }`
  (uses the saved settings — save first).
* `POST /api/actions/test-notification` body `{ "channel": "teams" }` (optional; omit or `{}` for every
  enabled channel) → `200 { "ok": bool, "message": "...", "results": [ { "channel": "teams", "type": "Teams",
  "ok": true, "message": "Delivered (HTTP 202)" } ] }`. Severity filters do not apply to tests. `ok: false`
  with an empty `results` when no channel is enabled; `404 { "error" }` when `channel` names no enabled
  channel. Channel names: `email`, `teams`, `slack`, `webhook` (the fixed fields of the "Notification
  channels" section) plus any `Notifications:Channels` entries. Uses the saved settings — save first.
  UI: a "Send test notification" button in the Notification channels section (and optionally one per channel).
* `POST /api/actions/maintenance` body `{ "minutes": 60, "reason": "Monthly patching" }` (minutes 1–10080,
  reason optional, ≤ 500 chars) → `200 { "maintenance": { "active", "until", "reason" } }`; `400 { "error" }`
  for bad input. Saves `Maintenance:Until`/`Reason` to the operator settings (same as
  `WatchTower maintenance start`).
* `POST /api/actions/maintenance/stop` → `200 { "maintenance": {..}, "message": "..." }`. If the portal set the
  window it stays active (`maintenance.active: true`) and the message says to end it from the portal.
  UI: a maintenance banner on the dashboard while `maintenance.active` ("Maintenance until 18:00 — notifications
  paused, services are not auto-restarted" + reason + **End now**), and a "Start maintenance" dialog
  (duration presets 30 min / 1 h / 2 h / 4 h + reason). The `Maintenance:Until`/`Reason` fields also appear in
  the settings schema for direct editing.
* `POST /api/actions/enroll` body `{ "code": "WT1.…" }` (enrollment code from the portal's "Add server"
  dialog) → `200 { "portalUrl", "agentId", "restartRequired": true, "warnings": [..] }` — saves and enables
  the portal connection (token encrypted); offer Restart now. `400 { "error" }` for a bad code,
  `400 { "errors" }` when the resulting configuration is invalid. UI: a "Connect with an enrollment code"
  box at the top of the Visibility Portal section.
* `POST /api/actions/restart` → `202 { "message": "..." }` (the console goes away for a few
  seconds; poll `GET /api/auth/state` until it answers again, then reload) or
  `409 { "error": "..." }` when not running as a service.

## Logs

`GET /api/logs?lines=300` → `{ "file": "watchtower-20261103.log", "lines": ["2026-11-03 ... [INF] ..."] }`

## Updates

State is held by the service (not the browser): a countdown keeps running and can be cancelled
from any session or by the portal. Poll `GET /api/updates` every 1 s while `phase` is
`Checking`, `Downloading` or a countdown is running (`restartAt` set), otherwise every 30 s.

`GET /api/updates` (also the body returned by `check` and `cancel`) →

```jsonc
{
  "currentVersion": "1.5.0",
  "phase": "Available",        // Idle | Checking | UpToDate | Available | Downloading | ReadyToInstall | Installing | Failed
  "available": {               // null when up to date
    "version": "1.6.0", "publishedAt": "2026-11-01T09:00:00Z", "prerelease": false,
    "notes": "markdown release notes",   // render as plain text with line breaks (do NOT inject HTML)
    "assets": [ { "rid": "win-x64", "url": "...", "size": 58200000, "sha256": "...", "signature": null } ]
  },
  "progress": 0.42,            // 0..1 while Downloading, else null
  "restartAt": "2026-11-03T10:00:30Z", // countdown deadline (server time, UTC) or null
  "lastCheckedAt": "2026-11-03T09:58:00Z",
  "error": null,               // last check/download error (show it, keep offering retry)
  "lastResult": {              // outcome of the most recent install (persisted across restarts)
    "fromVersion": "1.4.2", "toVersion": "1.5.0", "success": true,
    "message": "Updated from 1.4.2 to 1.5.0.", "completedAt": "..."
  },
  "canInstall": true, "cannotInstallReason": null
}
```

| Action | Response |
|---|---|
| `POST /api/updates/check` | `200` status |
| `POST /api/updates/download` | `202 { message, status }` — downloads, verifies (SHA-256, signature if configured), then starts the restart countdown. `409 {error}` when nothing is available or busy. |
| `POST /api/updates/apply` | `202 { message }` — install now (skips the remaining countdown). The service restarts: poll `GET /api/auth/state` until it is back, then reload. `409 {error}` with `cannotInstallReason`. |
| `POST /api/updates/cancel` | `200` status — stops the countdown; the downloaded update stays ready ("Install now" button). |

UI: show current version; a "Check for updates" button with "last checked" time; when
available, the version, date and release notes with **Download update**; a progress bar while
downloading; then a prominent countdown ("Restarting to install 1.6.0 in 27 s") with **Restart
now** and **Cancel**; when ReadyToInstall without a countdown, **Install now**. Show
`lastResult` as a success/failure notice (failures say the previous version was restored).
When `canInstall` is false, show `cannotInstallReason` instead of the install buttons. Compute
the countdown from `restartAt` against the server's clock: use the `Date` response header to
correct for client clock skew.
