# Index2SP

[![Build](https://github.com/BigWebstas/Index2SP/actions/workflows/build.yml/badge.svg)](https://github.com/BigWebstas/Index2SP/actions/workflows/build.yml)

A small Windows **system-tray** app that receives the [Pebble Index 01](https://repebble.com/index)
voice-note webhook and turns each transcription into a task in
[Super Productivity](https://super-productivity.com/) via its **Local REST API**.

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

- Windows 10/11, .NET 8 (bundled if you publish self-contained).
- Super Productivity **desktop** with **Settings → Misc → Enable local REST API** turned on.
  Copy the access token from that same screen.
- A way for Pebble's cloud to reach this PC over HTTPS — e.g.
  [`cloudflared`](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/)
  or [`ngrok`](https://ngrok.com/). Index2SP only listens on plain HTTP locally; the tunnel terminates TLS.

## Build

```powershell
# from the repo root, with the .NET 8 SDK installed
dotnet build -c Release

# or a single self-contained exe (no .NET needed on the target machine):
dotnet publish src/Index2SP/Index2SP.csproj -c Release -r win-x64 --self-contained true
# -> src/Index2SP/bin/Release/net8.0-windows/win-x64/publish/Index2SP.exe
```

Open `Index2SP.sln` in Visual Studio or Rider to build/debug interactively.

## Installer

[Inno Setup](https://jrsoftware.org/isinfo.php) per-user setup `.exe` (no admin / UAC prompt —
installs under `%LOCALAPPDATA%\Programs\Index2SP`), in **two variants**:

| Variant | Size | Prerequisite |
|---|---|---|
| **self-contained** (`Index2SP-Setup-<v>.exe`) | ~80 MB | none — bundles the .NET runtime |
| **framework-dependent** (`Index2SP-Setup-fd-<v>.exe`) | ~3 MB | [.NET Desktop Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0/runtime) **and** ASP.NET Core Runtime 8 (x64) |

The `-fd` installer checks for both runtimes on launch and points you to the download if either
is missing. Portable `.zip`s of each variant are produced too.

```powershell
# needs the .NET 8 SDK + Inno Setup 6 (ISCC.exe)
.\build.ps1 -Version 1.0.0                       # both variants + installers -> dist\
.\build.ps1 -Version 1.0.0 -Mode self-contained  # just one
.\build.ps1 -SkipInstaller                       # publish + zip, no installer
```

`build.ps1` runs `dotnet publish` (single file) per variant then `ISCC installer\Index2SP.iss`
(passing `/DFrameworkDependent` for the `-fd` build).

### CI / releases

`.github/workflows/build.yml` builds **both variants + installers** on `windows-latest` for every
push and PR (installs Inno Setup via Chocolatey, runs `build.ps1 -Mode both`) and uploads all four
files (`*-Setup-*.exe`, `*-portable-*.zip`) as a workflow artifact.

Pushing a tag like `v1.2.0` builds with that version and publishes a **GitHub Release** with all
files attached:

```bash
git tag v1.2.0 && git push origin v1.2.0
```

The setup wizard offers two checkboxes:

- **Start Index2SP automatically when I sign in to Windows** — writes
  `HKCU\…\CurrentVersion\Run\Index2SP`. This is the *same* value the tray toggle manages,
  so the two stay in sync. Removed on uninstall.
- **Run Index2SP now**.

Uninstalling removes the program and the Run entry but leaves your `config.json` and logs
in `%APPDATA%\Index2SP\`.

## Run at login

Toggle it any time from the tray menu → **Start at login** (checkmark = on). It sets/clears
the per-user `Run` registry value pointing at the current executable — no admin rights, and it
survives moving/reinstalling as long as you re-toggle after the path changes.

## Configure

On first run the app writes `config.json` to `%APPDATA%\Index2SP\`.
Use the tray menu → **Edit config…**, then **Reload config**.
See [`config.example.json`](config.example.json) for all fields.

| Field | Meaning |
|---|---|
| `listenAddress` / `port` | Where the local webhook listener binds. `127.0.0.1` is right when a tunnel runs on the same PC; use `0.0.0.0` for LAN/containers. |
| `webhookPath` | Path Pebble posts to. Full URL Pebble needs = `https://<your-tunnel-host><webhookPath>`. |
| `inboundAuthToken` | Optional shared secret. If set, Pebble must send `Authorization: Bearer <token>` (add it as a custom header in the Pebble webhook settings). Strongly recommended since the endpoint is internet-facing. |
| `titleMaxLength` | Title cap (SP rejects > 300). |
| `notifications` | Toggle Windows balloon tips. |
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
- **Default project ▸** – pick from your Super Productivity projects; writes `projectId` to config
- **Default tags ▸** – toggle which tags every task gets; writes `tagIds` to config
- **Edit config… / Reload config**
- **Start at login** – toggles the per-user autostart registry entry
- **View log / Open log folder** – logs live in `%APPDATA%\Index2SP\logs\`
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
- Autostart uses the per-user `Run` key. If you move the executable, re-toggle **Start at login**
  (or re-run the installer) so the stored path is refreshed.
