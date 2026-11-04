# 03 Authentication and enrollment

[Portal API guide](README.md) › Authentication and enrollment

## Credentials

An agent has two portal credentials, both issued by the portal:

| Setting on the agent | Sent as | Format the agent accepts | Portal SHOULD use |
| --- | --- | --- | --- |
| `VisibilityPortal:AgentId` | `X-Agent-Id: <agentId>` header, and `agentId` in every metrics batch body | Any non-empty string | 1 to 64 ASCII characters from `A-Z a-z 0-9 . _ -` (a UUID, or a host-like name) |
| `VisibilityPortal:AgentToken` | `Authorization: Bearer <token>` header | Any non-empty string | 32 random bytes, base64url without padding (43 characters) |

Both headers are on **every** request, GET and POST alike. With the gRPC transport they are
gRPC metadata (`x-agent-id`, `authorization`) and also an `AgentIdentity` message in every
request (see [Versioning and compatibility](12-versioning-and-compatibility.md#the-grpc-alternative)).

The portal:

- MUST authenticate the pair on every request: look the agent up by `X-Agent-Id`, then verify the
  token.
- MUST derive the agent's identity from the authenticated credentials. It MUST NOT trust `agentId`
  in the batch body or any other body field for authorization. The body value is informational,
  and an outbox replay after re-enrollment can carry an old one.
- MUST compare tokens in constant time, and SHOULD store only a hash. Tokens are high-entropy
  random strings, so a fast hash (SHA-256) is enough. Slow password hashes such as bcrypt are not
  needed and would cost CPU on every request.
- MUST answer `401 Unauthorized` when credentials are missing or wrong, and SHOULD add
  `WWW-Authenticate: Bearer`.
- MUST NOT put the token in URLs, logs or error messages.

On the agent the token is stored in the operator settings file encrypted with Windows DPAPI
(LocalMachine scope), and it is never shown in the agent's web console.

### How the agent reacts to 401 and 403

The agent has no special handling for any status besides "2xx or not":

| Call | On 401 | On 403 or any other non-2xx |
| --- | --- | --- |
| POST metrics/batch, events | Logged at **Error** ("Portal returned 401 Unauthorized on {path}. Check AgentToken."); the message is retried with the normal backoff | Logged at Warning with the status; retried the same way |
| GET commands/pending, config | Logged at Warning ("PollCommands returned 401" / "FetchConfiguration returned 401"); treated as "nothing new" | Same |
| POST acknowledge (command) | Logged at Error; not retried | Not retried |
| POST command result, config acknowledge | Logged at Error; retried (a result with its backoff, for up to 7 days; a config acknowledgement every config cycle) | Retried the same way |

The agent never re-enrolls, never asks for a new token and never stops trying. A revoked agent
keeps calling every 30 s until an operator changes its settings or uninstalls it. Use 401 for
bad credentials. 403 is treated exactly the same way, so there is no benefit in using it for
"known agent, not allowed".

## Issuing, rotating and revoking tokens

```mermaid
sequenceDiagram
    participant Op as Operator
    participant P as Portal
    participant A as Agent
    Op->>P: Add server SRV-WEB-01
    P->>P: token = random(32 bytes); store SHA-256(token)
    P-->>Op: enrollment code (token shown once)
    Op->>A: enroll WT1.…
    A->>P: requests with Bearer token
    Note over Op,P: Rotation
    Op->>P: Rotate token
    P->>P: new token; keep old hash valid until the new one is seen
    P-->>Op: new enrollment code
    Op->>A: enroll WT1.(new)
    A->>P: requests with the new token
    P->>P: first request with the new token: delete the old hash
```

- **Issue:** generate the token server-side from a CSPRNG and show it only inside the enrollment
  code (or once, for a seed file). Bind it to exactly one agentId.
- **Rotate:** the agent can't rotate by itself, and no endpoint lets the portal hand it a new
  token. Rotation means a new enrollment code that the operator applies (`enroll`, the console
  or a new seed). A portal SHOULD accept both the old and the new token for one agentId until it
  sees the new one used, then revoke the old one. Otherwise the agent is locked out between your
  rotation and the operator's re-enrollment.
- **Revoke:** delete the hash. From then on every request gets 401. Events queued on the agent
  are kept for up to seven days, so if the agent is re-enrolled within that time it delivers them
  with its new credentials.
- **Decommission:** revoking does not silence the agent (see above). Tell the operator to
  uninstall it (`WatchTower.exe uninstall`) or disable the portal connection
  (`VisibilityPortal:Enabled = false`) on the server.

## Enrollment codes

An enrollment code puts the four connection settings into one string an operator can paste:

```text
WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0
```

### Format, byte by byte

1. The four ASCII characters `W` `T` `1` `.` (case-sensitive). Anything else and the agent
   answers *An enrollment code starts with "WT1."*.
2. The **payload**: base64url (RFC 4648 §5: `-` instead of `+`, `_` instead of `/`) of the UTF-8
   bytes of a JSON object. The portal SHOULD strip the `=` padding. The agent accepts it with or
   without padding.
3. Nothing else. The agent trims whitespace around the whole code, but none is allowed inside.

The JSON object:

<!-- schema: EnrollmentCodePayload -->
```json
{
  "v": 1,
  "url": "https://portal.example.com",
  "agentId": "SRV-WEB-01",
  "token": "q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp",
  "requireTls": true
}
```

| Field | Type | Required | Agent behaviour |
| --- | --- | --- | --- |
| `v` | integer | yes | MUST be `1`. Any other value gives *This enrollment code was made by a newer portal; update WatchTower first.* A string `"1"` makes the code "damaged". |
| `url` | string | yes | Absolute `http://` or `https://` URL, the agent's BaseUrl. It MAY contain a path prefix. The agent normalises it (lower-case scheme and host, default port removed, trailing `/` removed). |
| `agentId` | string | yes | Trimmed; MUST NOT be empty. |
| `token` | string | yes | Trimmed; MUST NOT be empty. |
| `requireTls` | boolean | no | Becomes `Tls:RequireTls`. When absent: `true` for `https` URLs, `false` for `http` URLs. `true` with an `http://` URL is an invalid configuration, which `enroll` refuses to save. |
| `acceptsGzip` | boolean | no | after-1.0.20. `true`: your portal accepts gzip-compressed request bodies, so `enroll` (CLI and console) also sets `VisibilityPortal:CompressRequests=Gzip`. Absent, `false` or any non-boolean value leaves that setting as it is: a code never turns compression off for an operator who turned it on. Only send `true` if you decode gzip, see [Compressed request bodies](04-transport-and-security.md#compressed-request-bodies-opt-in-after-1020). |

A portal that accepts compressed requests adds the flag:

<!-- schema: EnrollmentCodePayload -->
```json
{
  "v": 1,
  "url": "https://portal.example.com",
  "agentId": "SRV-WEB-01",
  "token": "q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp",
  "requireTls": true,
  "acceptsGzip": true
}
```

Unknown properties are ignored (1.0.20 and earlier ignore `acceptsGzip` too, so the same code
works for every agent). A payload that is not valid base64url, not UTF-8 JSON, not an
object, or has a value of the wrong type gives *The enrollment code is damaged (was it copied
completely?)*.

A code contains a live credential. Show it once, only to the person adding the server. Never
email it to a list, and SHOULD let it expire if it was not used (for example by rotating the
token after 24 h when no request with it has been seen).

### Encoding and decoding

C#:

```csharp
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

static string Encode(string url, string agentId, string token, bool requireTls)
{
    var json = new JsonObject
    {
        ["v"] = 1, ["url"] = url, ["agentId"] = agentId, ["token"] = token, ["requireTls"] = requireTls,
    }.ToJsonString();
    return "WT1." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

static JsonObject Decode(string code)
{
    if (!code.StartsWith("WT1.", StringComparison.Ordinal)) throw new FormatException("not a WT1 code");
    var b64 = code[4..].Trim().Replace('-', '+').Replace('_', '/');
    b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
    return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64)))!.AsObject();
}
```

Python 3:

```python
import base64, json

def encode(url: str, agent_id: str, token: str, require_tls: bool) -> str:
    payload = json.dumps({"v": 1, "url": url, "agentId": agent_id, "token": token,
                          "requireTls": require_tls}, separators=(",", ":")).encode("utf-8")
    return "WT1." + base64.urlsafe_b64encode(payload).decode("ascii").rstrip("=")

def decode(code: str) -> dict:
    code = code.strip()
    if not code.startswith("WT1."):
        raise ValueError("not a WT1 code")
    b64 = code[4:]
    return json.loads(base64.urlsafe_b64decode(b64 + "=" * (-len(b64) % 4)).decode("utf-8"))
```

JavaScript (Node.js 16+):

```javascript
function encode(url, agentId, token, requireTls) {
  const json = JSON.stringify({ v: 1, url, agentId, token, requireTls });
  return "WT1." + Buffer.from(json, "utf8").toString("base64url"); // base64url has no padding
}

function decode(code) {
  code = code.trim();
  if (!code.startsWith("WT1.")) throw new Error("not a WT1 code");
  return JSON.parse(Buffer.from(code.slice(4), "base64url").toString("utf8"));
}
```

The agent's own parser is `src/WatchTower/Portal/EnrollmentCode.cs`; the contract tests decode
every code that appears in these pages with it.

### What the operator runs

Give your users these commands next to the code:

```powershell
# New server: install and connect (elevated PowerShell, in the extracted release folder)
.\install.ps1 -EnrollmentCode WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0

# Installed agent (elevated)
& "C:\Program Files\WatchTower\WatchTower.exe" enroll WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0

# Only check a code, change nothing (no elevation needed)
WatchTower.exe enroll WT1.eyJ2IjoxLCJ1cmwiOiJodHRwczovL3BvcnRhbC5leGFtcGxlLmNvbSIsImFnZW50SWQiOiJTUlYtV0VCLTAxIiwidG9rZW4iOiJxNlhyMGRaMXQ4eUptMktwNFZiN05jOUx3M0hzNUdmMEFlMVVpMk9vM1BwIiwicmVxdWlyZVRscyI6dHJ1ZX0 --check
```

Operators can also paste the code into the agent's web console (pairing with a portal).
`enroll`:

- validates the code, then merges `VisibilityPortal: { Enabled: true, Protocol: "Https", BaseUrl,
  AgentId, AgentToken, Tls: { RequireTls } }` into the operator settings, after checking the
  resulting configuration is valid. The token is encrypted on write. With `"acceptsGzip": true`
  it also sets `CompressRequests: "Gzip"` (after 1.0.20); without it, that setting is untouched.
- warns when the URL is `http://` ("the agent token will travel unencrypted").
- restarts the service if it is running (`--no-restart` skips that; `install.ps1` uses it and
  starts the service itself). Data starts arriving within about a minute.
- always selects the HTTPS/JSON transport, even if the agent was on gRPC before.

`--service-name` and `--data-dir` select a non-default instance, as for every WatchTower
command.

## Seed files (unattended installs)

Fleet tools (GPO, Intune, SCCM, Ansible) can pass a JSON settings file instead of a code:

```powershell
.\install.ps1 -ConfigFile .\watchtower.seed.json -AdminPassword (ConvertTo-SecureString $pw -AsPlainText -Force)
```

The seed file uses the agent's settings keys (the same shape as `appsettings.json`). For a portal:

<!-- schema: none (agent settings file, not an API payload) -->
```json
{
  "VisibilityPortal": {
    "Enabled": true,
    "Protocol": "Https",
    "BaseUrl": "https://portal.example.com",
    "AgentId": "SRV-WEB-01",
    "AgentToken": "q6Xr0dZ1t8yJm2Kp4Vb7Nc9Lw3Hs5Gf0Ae1Ui2Oo3Pp",
    "Tls": { "RequireTls": true }
  }
}
```

The installer validates the merged configuration before writing anything and encrypts the token
on import. Tell users to delete the seed file afterwards, because it holds the token in plain
text. Settings names are PascalCase here (they are agent settings, not API JSON). Generate one seed
file per server, because each server needs its own agentId and token. `WatchTower.exe config
validate <file>` checks a seed file without applying it.

Next: [Transport and security](04-transport-and-security.md).
