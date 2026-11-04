# Security policy

WatchTower runs as LocalSystem on the servers it monitors, so security reports are taken
seriously.

## Reporting a vulnerability

Please **do not open a public issue**. Report privately through GitHub:
**Security → Report a vulnerability** on
[angatiabenson/watch-tower](https://github.com/angatiabenson/watch-tower/security/advisories/new).

Include the affected version (`WatchTower.exe version`), the configuration involved (with
secrets removed) and steps to reproduce. You should get an acknowledgement within a few days;
fixes ship as a new release, which agents can install through their built-in updater.

## Supported versions

Only the latest release receives security fixes. Keep automatic update checks enabled
(Settings → Updates) to be notified of new releases.

## Hardening notes for operators

- The web console listens on `127.0.0.1` only by default. Before exposing it to the network,
  enable HTTPS with a certificate and set `Ui:AllowedHosts`.
- Plain HTTP for the portal connection, remote console or update feed is opt-in and logged
  as a warning; prefer HTTPS.
- Configure `Updates:PublicKey` so only signed release packages are installed.
- Secrets are encrypted at rest with Windows DPAPI and the data directory
  (`%ProgramData%\WatchTower`) is restricted to SYSTEM and Administrators.
