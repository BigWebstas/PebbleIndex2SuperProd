# Index2SP

[![Build](https://github.com/BigWebstas/Index2SP/actions/workflows/build.yml/badge.svg)](https://github.com/BigWebstas/Index2SP/actions/workflows/build.yml)

A small **system-tray** app (Avalonia, .NET 8 — **Windows and Linux**) that receives the
[Pebble Index 01](https://repebble.com/index) voice-note webhook and turns each transcription
into a task in [Super Productivity](https://super-productivity.com/) via its **Local REST API**.

```
Pebble Index 01  --(HTTPS multipart/form-data)-->  your HTTPS tunnel
                                                        |
                                                        v
                              Index2SP tray app  (http://127.0.0.1:8787/pebble)
                                                        |
                                                        v
                   Super Productivity Local REST API  (POST http://127.0.0.1:3876/tasks)
```

## What it does

1. Hosts a local HTTP listener (Kestrel) on `127.0.0.1:8787/pebble` (configurable).
2. Accepts the Pebble Index 01 webhook: `multipart/form-data` with fields
   `transcription`, `recordedAt`, `client`, and optionally an `audio` file
   ([Pebble docs](https://help.repebble.com/en/articles/15724406-index-advanced-features-mcp-webhook)).
3. Optionally checks an `Authorization: Bearer <token>` header (shared secret).
4. Converts the payload:
   - **title** = the transcription, trimmed and truncated to 300 chars,
   - **notes** = the full transcription plus capture metadata (recorded-at timestamp, client, audio size),
   - optional **projectId** / **tagIds** from config applied to every task,
   - optional **capture tag** (`captureTagId` / `captureTagName`) added to mark it as a voice note.
5. `POST`s it to the Super Productivity Local REST API and shows a tray notification.

## Requirements

- **Windows 10/11** or **Linux** with a system tray (KDE, GNOME + AppIndicator extension,
  most tiling-WM trays). Linux notifications use `notify-send` (libnotify) when available,
  otherwise a small in-app toast.
- Super Productivity **desktop** with **Settings → Misc → Enable local REST API** turned on.
  Copy the access token from that same screen.
- A way for Pebble's cloud to reach this machine over HTTPS — e.g.
  [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/)
  or [`ngrok`](https://ngrok.com/). Index2SP only listens on plain HTTP locally; the tunnel terminates TLS.

## Install

Each release ships **self-contained** builds (no runtime needed, ~80–90 MB) and
**framework-dependent** builds (`-fd`, ~a few MB, need the **ASP.NET Core Runtime 8** installed).

### Windows

[Inno Setup](https://jrsoftware.org/isinfo.php) per-user installer (no admin / UAC prompt —
installs under `%LOCALAPPDATA%\Programs\Index2SP`):

| File | Prerequisite |
|---|---|
| `Index2SP-Setup-<v>.exe` | none |
| `Index2SP-Setup-fd-<v>.exe` | [ASP.NET Core Runtime 8 (x64)](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) — the installer checks and links it |

Portable `Index2SP-portable[-fd]-<v>.zip` is also provided. The wizard offers **Start at sign-in**
(writes `HKCU\…\CurrentVersion\Run\Index2SP`, the same value the tray toggle manages) and
**Run now**. Uninstalling leaves `config.json` and logs in `%APPDATA%\Index2SP\`.

### Linux

Download `Index2SP-linux-x64[-fd]-<v>.tar.gz`, extract, and run `./install.sh` (per-user: copies
the binary to `~/.local/bin/index2sp` and adds a desktop entry; `./uninstall.sh` reverses it).
Or just run `./Index2SP` from the extracted folder. The `-fd` tarball needs
`aspnetcore-runtime-8.0` from your distro or Microsoft.

## Build from source

```bash
# .NET 8 SDK required
dotnet build -c Release

# Windows: both variants + Inno Setup installers (needs Inno Setup 6 / ISCC.exe)
pwsh ./build.ps1 -Version 1.1.2                      # -> dist\
pwsh ./build.ps1 -Version 1.1.2 -Mode self-contained
pwsh ./build.ps1 -SkipInstaller

# Linux: both variants as tarballs
bash scripts/package-linux.sh 1.1.2                  # -> dist/
```

### CI / releases

`.github/workflows/build.yml` builds the Windows variants + installers on `windows-latest` and the
Linux variants on `ubuntu-latest` for every push and PR, uploading them as workflow artifacts.
Pushing a tag like `v1.2.0` publishes a **GitHub Release** with every file attached:

```bash
git tag v1.2.0 && git push origin v1.2.0
```

## Run at login

Tray menu → **Start at login** (checkmark = on), no admin rights:

- **Windows** — the per-user `Run` registry value pointing at the current executable.
- **Linux** — `~/.config/autostart/index2sp.desktop` (XDG autostart).

If you move the executable, re-toggle it (or re-run the installer) so the stored path refreshes.

## Configure

On first run the app writes `config.json` to `%APPDATA%\Index2SP\` (Windows) or
`~/.config/Index2SP/` (Linux). Use the tray menu → **Edit config…**, then **Reload config**.
See [`config.example.json`](config.example.json) for all fields.

| Field | Meaning |
|---|---|
| `listenAddress` / `port` | Where the local webhook listener binds. `127.0.0.1` is right when a tunnel runs on the same PC; use `0.0.0.0` for LAN/containers. |
| `webhookPath` | Path Pebble posts to. Full URL Pebble needs = `https://<your-tunnel-host><webhookPath>`. |
| `inboundAuthToken` | Optional shared secret. If set, Pebble must send `Authorization: Bearer <token>` (add it as a custom header in the Pebble webhook settings). Strongly recommended since the endpoint is internet-facing. |
| `titleMaxLength` | Title cap (SP rejects > 300). |
| `notifications` | Toggle desktop notifications for routine events (task created, listener started). Errors and test events still notify. |
| `healthCheckSeconds` | Background probe interval for the Super Productivity connection (keeps the tray status/icon fresh). `0` disables; min 15, default 60. |
| `testEventPhrase` | A webhook whose transcription matches this (trimmed, case-insensitive) shows a **"Test received"** notification instead of creating a task — this is what Pebble's *send test event* produces. Blank disables. Default `Index webhook test event`. |
| `superProductivity.baseUrl` | Default `http://127.0.0.1:3876`. |
| `superProductivity.accessToken` | Token from SP Settings → Misc → Local REST API. Sent as `Authorization: Bearer`. |
| `superProductivity.projectId` | Optional existing active project id for every task. Blank = inbox. Set it from the tray → **Default project**. |
| `superProductivity.tagIds` | Optional tag ids applied to every task. Manage from the tray → **Default tags**. |
| `superProductivity.captureTagId` | Optional: one tag id applied to every task created from a Pebble capture (e.g. a "voice-note" tag to filter on). |
| `superProductivity.captureTagName` | Alternative to `captureTagId` — the tag's **name**; resolved to an id via `GET /tags` (the tag must already exist). Ignored when `captureTagId` is set. |

## Wire up Pebble

1. Start a tunnel to the local listener, e.g.:
   ```powershell
   cloudflared tunnel --url http://127.0.0.1:8787
   ```
2. In the Pebble app → webhook settings:
   - **URL**: `https://<tunnel-host>/pebble`
   - **Custom header**: `Authorization: Bearer <your inboundAuthToken>`
   - **Send**: transcription (or both).
3. Send Pebble's **test event** first — Index2SP pops a "Test received" notification (no task).
4. Then record a real note on the ring. Watch the tray notification and **View log**.

## Tray menu

- **Start / Stop listener**
- **Copy webhook URL** – the local URL; prepend your tunnel host for Pebble
- **Test Super Productivity connection** – probes `GET /health` then `GET /tasks` (also runs
  automatically every `healthCheckSeconds`; the icon's centre dot is blue when SP is reachable,
  orange when not)
- **Default project ▸** / **Default tags ▸** – pick from your Super Productivity projects/tags;
  writes `projectId` / `tagIds` to config
- **Refresh projects & tags** – reloads those lists from Super Productivity
- **Edit config… / Reload config**
- **Start at login** – per-user autostart (registry on Windows, XDG autostart on Linux)
- **View log / Open log folder** – logs live in `%APPDATA%\Index2SP\logs\` or `~/.config/Index2SP/logs/`
- **Quit**

## Endpoints exposed locally

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/pebble` (configurable) | Pebble Index 01 webhook receiver |
| `GET`  | `/health` | Liveness probe for your tunnel / monitoring |

## Notes & limitations

- "Audio only" webhooks are rejected with `422` — there's no text to name a task.
- The audio file itself is **not** stored or attached (the SP REST API has no attachment field);
  its presence and size are recorded in the task notes.
- The SP Local REST API cannot create recurring tasks or subtasks-with-hierarchy through this app.
- Linux tray support depends on your desktop (StatusNotifierItem / AppIndicator). If no tray
  appears, the app still runs headless — edit `~/.config/Index2SP/config.json` and check the logs.
- Framework-dependent builds need the **ASP.NET Core** runtime 8 (Kestrel), not just the base runtime.
