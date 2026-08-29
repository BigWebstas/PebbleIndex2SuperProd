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
   - optional **projectId** / **tagIds** from config applied to every task.
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

An [Inno Setup](https://jrsoftware.org/isinfo.php) script builds a per-user setup `.exe`
(no admin / UAC prompt — installs under `%LOCALAPPDATA%\Programs\Index2SP`).

```powershell
# needs the .NET 8 SDK + Inno Setup 6 (ISCC.exe) on PATH or in Program Files
.\build.ps1 -Version 1.0.0
# -> dist\Index2SP-Setup-1.0.0.exe
```

`build.ps1` runs `dotnet publish` (self-contained, single file) then `ISCC installer\Index2SP.iss`.
Use `.\build.ps1 -SkipInstaller` for just the published exe.

### CI / releases

`.github/workflows/build.yml` builds the app + installer on `windows-latest` for every push and
PR (installs Inno Setup via Chocolatey, runs `build.ps1`) and uploads the setup `.exe` and a
portable `.zip` as workflow artifacts.

Pushing a tag like `v1.2.0` builds with that version and publishes a **GitHub Release** with both
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
| `superProductivity.baseUrl` | Default `http://127.0.0.1:3876`. |
| `superProductivity.accessToken` | Token from SP Settings → Misc → Local REST API. Sent as `Authorization: Bearer`. |
| `superProductivity.projectId` | Optional existing active project id for every task. Blank = inbox. |
| `superProductivity.tagIds` | Optional tag ids for every task. |

## Wire up Pebble

1. Start a tunnel to the local listener, e.g.:
   ```powershell
   cloudflared tunnel --url http://127.0.0.1:8787
   ```
2. In the Pebble app → webhook settings:
   - **URL**: `https://<tunnel-host>/pebble`
   - **Custom header**: `Authorization: Bearer <your inboundAuthToken>`
   - **Send**: transcription (or both).
3. Record a note on the ring. Watch the Index2SP tray notification and **View log**.

## Tray menu

- **Start / Stop listener**
- **Copy webhook URL** – the local URL; prepend your tunnel host for Pebble
- **Test Super Productivity connection** – probes `GET /health` then `GET /tasks`
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
