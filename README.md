# Pebble Index → Super Productivity, Joplin, Calendar, Beeper

[![Build](https://github.com/BigWebstas/PebbleIndex2SuperProd/actions/workflows/build.yml/badge.svg)](https://github.com/BigWebstas/PebbleIndex2SuperProd/actions/workflows/build.yml)

A tray app (Avalonia, .NET 8, Windows + Linux) that turns [Pebble Index 01](https://repebble.com/index)
voice notes into [Super Productivity](https://super-productivity.com/) tasks — and, optionally,
lets an AI classifier (Claude, Gemini, OpenAI, or a local Ollama model) route each one further:
shopping items split one-per-task, notes saved to [Joplin](https://joplinapp.org/), dated items
added to Google Calendar, "send a message to X" sent through [Beeper](https://www.beeper.com/).

```
Pebble Index 01 ──HTTPS──▶ your tunnel ──▶ Index2SP :8787/pebble ──▶ Super Productivity :3876
```

## What it does

- Every note becomes an SP task — title from the transcription, full text + metadata in notes.
- SP unreachable? Queued to a local outbox, retried until it lands.
- With `aiClassifier` on, the AI also: picks project/tags, splits multi-item shopping lists,
  sets due dates, and — per destination toggle — files notes to Joplin, adds calendar events,
  or sends a Beeper message. **Beeper sends immediately, with no confirmation step.**
- Uncaught errors are logged and reported as an SP task instead of crashing silently.
  Integration outages get their own SP task too.

## Install

Grab a [release](https://github.com/BigWebstas/PebbleIndex2SuperProd/releases/latest).

- **Windows** — run the installer, or unzip the portable build.
- **Linux** — extract the tarball, run `./install.sh` (or the binary directly).

## Set up

1. Enable **Settings → Misc → Local REST API** in Super Productivity; copy its token.
2. Tunnel the listener: `cloudflared tunnel --url http://127.0.0.1:8787` (already run nginx,
   Caddy, or IIS on a public box instead? see [`reverse-proxy/`](reverse-proxy/)).
3. In Pebble's webhook settings, set the URL to `https://<tunnel-host>/pebble`.
4. Send Pebble's test event, then record a real note.

## Configure

Tray → **Edit config…** opens `config.json` (`%APPDATA%\Index2SP\` on Windows,
`~/.config/Index2SP/` on Linux); **Reload config** applies changes. Every destination —
Super Productivity, AI classifier, Joplin, Google Calendar, Beeper — has its own tray submenu:
enable, credentials, and **Test connection** (or **Test all connections** for one combined
check). Full field reference: [`config.example.json`](config.example.json).

**AI classifier → Provider** picks the backend: Claude, Gemini, OpenAI, or a local Ollama server.
Each has its own credential/model submenu; only the selected provider's needs to be filled in.
Ollama needs no key — just a server URL and a model, picked from whatever's actually pulled on
that host (**Refresh projects, tags & notebooks** fetches the list) — but, unlike the three
hosted providers, can't be forced to call the tool: an unsuited model may just not classify,
falling back to the static config like any other failure. The background health check and
**Test all connections** cover whichever provider is currently selected too.

By default a note/event/message is filed to Joplin/Calendar/Beeper **and** still becomes an SP
task. **AI classifier → Route to one destination only** skips the SP task when that other
destination actually succeeds, so you don't get both — falling back to the SP task if it fails.

Two setups need an extra step first:

- **Google Calendar** — create a Google Cloud OAuth client (type **Desktop app**), paste its
  ID/secret into the tray, then **Connect…** for a one-time browser sign-in.
- **Beeper** — create a personal access token in Beeper Desktop's API settings, paste it in.
  Naming a platform ("text Abbie on Telegram") picks that chat when the recipient has several;
  with no platform named, it only sends when the recipient matches exactly one chat.

## Build from source

```bash
dotnet build -c Release
pwsh ./build.ps1 -Version X.Y.Z        # Windows: installer + portable zip
bash scripts/package-linux.sh X.Y.Z    # Linux: tarballs
```

CI builds every push/PR; pushing a `v*` tag cuts a [GitHub Release](https://github.com/BigWebstas/PebbleIndex2SuperProd/releases).

## Limitations

- Audio-only webhooks are rejected (422) — no text, no task.
- No recurring tasks or subtasks (the SP REST API doesn't support them).
- Outbox retries aren't deduplicated — a lost reply can occasionally create a duplicate.
- No OS-level crash-restart — a hard crash stays down until you relaunch (in-process bugs are
  caught and don't crash the app; see above).
