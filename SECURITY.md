# Security

PortPilot can terminate processes on your machine, so security reports are taken seriously.

## Reporting a vulnerability

Please **don't open a public issue**. Use GitHub's private reporting instead:
[Report a vulnerability](https://github.com/Atrix21/port-pilot/security/advisories/new).

Include what you found, how to reproduce it and which version you tested. You'll get a reply within a week.

## What PortPilot does and doesn't do

- Runs with your normal user rights. It only asks for administrator rights when you click *Restart as administrator*.
- Makes no network connections. No telemetry, update checks or accounts.
- Writes only its settings (`%APPDATA%\PortPilot`) and logs (`%LOCALAPPDATA%\PortPilot\logs`).
- Terminates a process only after you confirm. It first checks that the PID still belongs to the process you selected, and it refuses System, itself and processes Windows marks as critical.

## Verifying a download

Every release file has a build-provenance attestation signed by GitHub:

```powershell
gh attestation verify PortPilot-1.0.0-Setup.exe --repo Atrix21/port-pilot
```
