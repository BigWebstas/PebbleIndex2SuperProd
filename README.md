# Index2SP

[![Build](https://github.com/BigWebstas/Index2SP/actions/workflows/build.yml/badge.svg)](https://github.com/BigWebstas/Index2SP/actions/workflows/build.yml)

A system-tray app (Avalonia, .NET 8, Windows and Linux) that receives the
[Pebble Index 01](https://repebble.com/index) voice-note webhook and creates a
task in [Super Productivity](https://super-productivity.com/) for each one.

```
Pebble Index 01  ──HTTPS──▶  your tunnel  ──▶  Index2SP :8787/pebble  ──▶  Super Productivity REST API :3876
```

The transcription becomes the task title (capped at 300 chars); the full text
plus capture metadata go in the notes. A configured project, tags, and a
"voice-note" capture tag can be applied to every task.

If Super Productivity is unreachable when a note arrives, the task is written to
a disk-backed **outbox** (`%APPDATA%\Index2SP\outbox\`) and retried in the
background until it lands, so nothing is lost while SP is closed.

## Requirements

- Windows 10/11, or Linux with a system tray. Without a tray the app still runs
  headless.
- Super Productivity **desktop**, with **Settings → Misc → Enable local REST API**
  on. Copy the access token from that screen.
- An HTTPS tunnel so Pebble's cloud can reach this machine — e.g. `cloudflared`
  or `ngrok`. Index2SP listens on plain HTTP locally; the tunnel handles TLS.

## Install

Grab the [latest release](https://github.com/BigWebstas/Index2SP/releases/latest).
Each build comes **self-contained** (~50 MB, no runtime needed) or
**framework-dependent** (`-fd`, ~9 MB, needs the ASP.NET Core Runtime 8).

- **Windows** — run the per-user installer (`Index2SP-Setup-<v>.exe`, no admin
  prompt), or unzip the portable build. Offers start-at-sign-in and run-now.
- **Linux** — extract the tarball and run `./install.sh` (copies to
  `~/.local/bin`, adds a desktop entry), or just run `./Index2SP`.

## Set up Pebble

1. Point a tunnel at the local listener: `cloudflared tunnel --url http://127.0.0.1:8787`
2. In the Pebble app's webhook settings:
   - **URL**: `https://<tunnel-host>/pebble`
   - **Custom header**: `Authorization: Bearer <inboundAuthToken>` (if you set one)
3. Send Pebble's **test event** — Index2SP shows "Test received", no task.
4. Record a real note. Watch the tray notification and **View log**.

## Configure

First run writes `config.json` to `%APPDATA%\Index2SP\` (Windows) or
`~/.config/Index2SP/` (Linux). Edit it from the tray menu, then **Reload config**.
Most settings also have a tray shortcut (default project, default tags, start at
login). See [`config.example.json`](config.example.json) for every field; the
ones that matter:

| Field | Meaning |
|---|---|
| `port` / `webhookPath` | Where the listener binds. Pebble's URL = `https://<tunnel-host><webhookPath>`. |
| `inboundAuthToken` | Optional shared secret. Strongly recommended — the endpoint is internet-facing. |
| `superProductivity.accessToken` | Token from SP Settings → Misc. Required. |
| `superProductivity.projectId` / `tagIds` | Applied to every task. Blank project = inbox. |
| `superProductivity.captureTagId` / `captureTagName` | Optional tag marking Pebble captures. |
| `outboxRetrySeconds` | Seconds between retry passes for queued tasks when SP was unreachable. Default 60, clamped 10–3600. |
| `outboxMaxAttempts` | Give up on a queued task after this many failed attempts and move it to `outbox\failed\`. Default `0` = retry forever. |
| `testEventPhrase` | Transcription that triggers "Test received" instead of a task. Default `Index webhook test event`. |

## Build from source

```bash
dotnet build -c Release

# Windows: both variants + Inno Setup installers (needs Inno Setup 6)
pwsh ./build.ps1 -Version 1.2.1

# Linux: both variants as tarballs
bash scripts/package-linux.sh 1.2.1
```

CI builds every push and PR. Pushing a `v*` tag publishes a GitHub Release with
all artifacts attached.

## Limitations

- Audio-only webhooks are rejected (422) — no text to name a task.
- The audio file is not stored; its size is noted in the task notes.
- No recurring tasks or subtask hierarchy (the SP REST API can't).
- Outbox retries are not deduplicated — if SP creates the task but the reply is
  lost, a later retry can make a second copy. Rare in practice.
- Full release notes: [Releases](https://github.com/BigWebstas/Index2SP/releases).
