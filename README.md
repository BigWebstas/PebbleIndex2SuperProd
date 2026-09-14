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
- Audio with no transcription (Pebble sent audio only)? With `whisper` on, a local Whisper server
  transcribes it instead of the webhook being rejected.
- With `aiClassifier` on, the AI also: cleans the task title (strips "add a task to", "remind
  me to", "send a message to X saying", etc. down to just the content), picks project/tags,
  splits multi-item shopping lists, sets due dates, and — per destination toggle — files notes
  to Joplin, adds calendar events, sends a Beeper message, or runs a real web search (via
  Claude) and sends the summary through a Telegram bot. **Beeper sends immediately, with
  no confirmation step.**
- Uncaught errors are logged and reported as an SP task instead of crashing silently.
  Integration outages get their own SP task too.
- Checks GitHub for a newer release and flags it in the tray menu — downloads it on request,
  nothing runs without you clicking it.

## Install

Grab a [release](https://github.com/BigWebstas/PebbleIndex2SuperProd/releases/latest).

- **Windows** — run the installer, or unzip the portable build.
- **Linux** — several options per release: a `.tar.gz` (extract, run `./install.sh`), an
  `.AppImage` (make executable, run), a `.deb`, or a `.rpm`. Arch users get a `PKGBUILD` instead
  of a prebuilt package — `makepkg -si` it.

## Set up

1. Enable **Settings → Misc → Local REST API** in Super Productivity; copy its token.
2. Tunnel the listener: `cloudflared tunnel --url http://127.0.0.1:8787` (already run nginx,
   Caddy, or IIS on a public box instead? see [`reverse-proxy/`](reverse-proxy/)).
3. In Pebble's webhook settings, set the URL to `https://<tunnel-host>/pebble`.
4. Send Pebble's test event, then record a real note.

## Configure

Tray → **Edit config…** opens `config.json` (`%APPDATA%\Index2SP\` on Windows,
`~/.config/Index2SP/` on Linux); **Reload config** applies changes. Every destination —
Super Productivity, AI classifier, Joplin, Google Calendar, Beeper, Telegram, Whisper — has its
own tray submenu: enable, credentials, and **Test connection** (or **Test all connections** for
one combined check). Full field reference: [`config.example.json`](config.example.json).

**AI classifier → Provider** picks the backend: Claude, Gemini, OpenAI, or a local Ollama server.
Each has its own credential/model submenu; only the selected provider's needs to be filled in.
Claude and Gemini's model pickers pull your account's real available models once a key is set
(**Refresh projects, tags & notebooks** re-fetches them, alongside SP/Joplin/Calendar/Ollama —
a built-in shortlist shows until the first successful fetch). OpenAI's stays a fixed shortlist.
Ollama needs no key — just a server URL and a model, picked from whatever's actually pulled on
that host — but, unlike the three hosted providers, can't be forced to call the tool: an unsuited
model may just not classify, falling back to the static config like any other failure. The
background health check and **Test all connections** cover whichever provider is currently
selected too.

**AI classifier → Fallback provider** tries a second backend when the primary one fails
(no credential, network, timeout, bad response) before giving up and using the static config.

By default a note/event/message is filed to Joplin/Calendar/Beeper **and** still becomes an SP
task. **AI classifier → Route to one destination only** skips the SP task when that other
destination actually succeeds, so you don't get both — falling back to the SP task if it fails.

Two setups need an extra step first:

- **Google Calendar** — create a Google Cloud OAuth client (type **Desktop app**), paste its
  ID/secret into the tray, then **Connect…** for a one-time browser sign-in.
- **Beeper** — create a personal access token in Beeper Desktop's API settings, paste it in.
  Naming a platform ("text Abbie on Telegram") picks that chat when the recipient has several;
  with no platform named, it only sends when the recipient matches exactly one chat.
- **Telegram** — message [@BotFather](https://t.me/BotFather) to create a bot and get a token,
  paste it into the tray, then message your new bot once and check its `getUpdates` response
  (or ask [@userinfobot](https://t.me/userinfobot)) for the chat ID to send to.

**Whisper** needs a local, OpenAI-compatible speech-to-text server already running — whisper.cpp's
own `server` example, faster-whisper-server, or LocalAI all work, since they share the same
`POST /v1/audio/transcriptions` contract. It's a fallback only: Pebble's own transcription is
always used when present, and this only fires for a genuinely audio-only webhook.

**Web search** ("search the web for...", "google...", "what is the latest version of X") runs
through Claude's built-in web search tool and sends the summary through the **Telegram** bot.
Needs a Claude API key (regardless of which provider you picked for classification — none of the
others expose this) and Telegram enabled.

**Webhook receipt** sends a short Telegram message for every capture — "Webhook Received, Note
Created", "Webhook Received, Task Created", etc. It's a receipt, not a delivery confirmation: it
fires as soon as Index2SP knows what the AI decided, whether or not the underlying task or
destination actually succeeds. Needs Telegram enabled.

**Telegram** always sends to the one chat ID you configure — unlike Beeper, there's no per-capture
recipient matching, since a bot only knows chats it's already been messaged from. It's used only
for web search and webhook receipt; the general "send a message to X" feature stays on Beeper,
which is where matching a spoken name against your existing chats actually matters.

**Check for updates automatically** (on by default, bottom of the tray menu) pings GitHub once a
day; **Check for updates** runs it on demand. A found update can be downloaded straight from the
tray — click it again once it's ready to install: Windows launches the installer (still its own
click-through UI, and quits Index2SP first since the installer needs to replace the running exe);
Linux extracts the tarball and opens the folder so you run `./install.sh` yourself. Nothing is
ever downloaded or launched without you clicking it.

## Build from source

```bash
dotnet build -c Release
pwsh ./build.ps1 -Version X.Y.Z        # Windows: installer + portable zip
bash scripts/package-linux.sh X.Y.Z    # Linux: tarballs
```

CI builds every push/PR; pushing a `v*` tag cuts a [GitHub Release](https://github.com/BigWebstas/PebbleIndex2SuperProd/releases).

## Limitations

- Audio-only webhooks are rejected (422) — no text, no task — unless `whisper` is on.
- No recurring tasks or subtasks (the SP REST API doesn't support them).
- Outbox retries check for an exact title+notes match before recreating a task, which covers a
  lost reply after Super Productivity actually created it — but not two genuinely separate
  duplicate files on disk.
- No OS-level crash-restart — a hard crash stays down until you relaunch (in-process bugs are
  caught and don't crash the app; see above).

  <meta name="google-site-verification" content="if9JMXF3y3eDA310RTuN6rJy9UT2z6y74piwnNqEvhY" />
