# Pebble Index 01 to Super Productivity, Joplin, Google Calendar, and Beeper


Super productivity for Tasks and Shopping lists (Local API).

Joplin for items classified as Notes (Local API).

Google Calendar for items classified as meetings/Gatherings/Conferences (Googles Caledar API).

Beeper for items classified as a Message to a User/Person (Local API).


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

Optionally, Claude reads each transcription and decides where it actually
belongs — see [AI classification](#ai-classification) below for what that adds
(smarter project/tag picks, splitting a shopping list into one task per item,
saving notes to Joplin, adding dated/timed items to Google Calendar, and
sending chat messages through Beeper).

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

## AI classification

With `aiClassifier.enabled` on, every transcription is sent to Claude along
with your real Super Productivity projects and tags (titles only). Claude
decides, per task:

- **Project and tags** — picked from your actual lists by title match, instead
  of always applying the static `superProductivity.projectId` / `tagIds`.
- **Note vs. to-do** — a fact, idea, or reference to save gets flagged as a
  note. If `joplin.enabled` is also on, a copy is sent to Joplin's Web Clipper
  API (Tools → Options → Web Clipper). The Super Productivity task is still
  created either way — Joplin is additive, never a replacement. Whenever an
  auth token is set, the tray's background health check also probes Joplin
  alongside Super Productivity, and **Joplin notes → Test Joplin connection**
  checks it on demand.
- **Shopping / errands** — "add bread, milk, and eggs to my shopping list"
  becomes three separate tasks (`bread`, `milk`, `eggs`), each stripped down to
  just the item. If `superProductivity.shoppingProjectId` is set, all of them
  go there instead of wherever Claude would otherwise file them.
- **Dates and times** — "dinner with parents on the 18th at 5pm" sets the due
  date/time on the Super Productivity task itself (always, whether or not
  Google Calendar is set up), resolved against when the note was recorded
  ("the 18th" always means the next upcoming 18th). A bare date with no time
  ("mom's birthday is the 20th") sets a date-only due day. When
  `googleCalendar.enabled` is also on, the same date/time also creates an
  event on Google Calendar — additive, never a replacement for the task.
- **Sending a message** — "send a message to Abbie say hello" sends "hello" to
  Abbie right away through [Beeper](https://www.beeper.com/) when
  `beeper.enabled` is on, over whatever network (Telegram, WhatsApp, iMessage,
  etc.) that chat already uses. **This sends immediately, with no confirmation
  step** — a misheard name or garbled transcription reaches the other person
  before you see it. To limit damage from a bad match, it only sends when the
  recipient name matches **exactly one** existing single-person chat; zero or
  multiple matches are logged and skipped, never guessed. The Super
  Productivity task is still created either way.

Any failure — no/bad API key, network, timeout, a malformed reply — falls back
to the static config from the table below. A task is always created either way.

By default tags are best-effort: Claude may pick one, and if it doesn't,
`superProductivity.tagIds` stands for the task and Joplin notes get no tag at
all. Turn on `aiClassifier.requireTags` (tray: **AI classifier → Require at
least one tag**) to make tagging mandatory instead — Claude must pick at least
one Super Productivity tag for every task, and at least one Joplin tag for
every note, falling back to `superProductivity.tagIds` / `joplin.defaultTagIds`
only if it still can't. With it off, Joplin notes skip AI tagging entirely and
just get `joplin.defaultTagIds`.

### Connecting Google Calendar

Unlike Super Productivity and Joplin, Google Calendar needs a one-time OAuth
sign-in — there's no local API key to paste in.

1. In [Google Cloud Console](https://console.cloud.google.com/), create (or
   pick) a project, enable the **Google Calendar API**, then create an OAuth
   client under **APIs & Services → Credentials** with type **Desktop app**.
2. Copy its client ID and secret into the tray: **Google Calendar → Set client
   ID…** / **Set client secret…**.
3. **Google Calendar → Connect…** opens your browser to Google's consent
   screen and starts a temporary local listener to catch the redirect — sign
   in, and the tray shows "Google Calendar connected". The refresh token it
   receives is stored in `config.json`; **Disconnect** clears it.
4. Turn on **Google Calendar → Enabled**, and optionally pick a non-default
   calendar under **Default calendar**.

### Connecting Beeper

Beeper Desktop exposes a local API (`http://127.0.0.1:23373`) with its own
personal access tokens — no OAuth flow needed.

1. In Beeper Desktop's settings, find the API/developer access section and
   create a token with read + write scope.
2. Paste it into the tray: **Beeper messages → Set API token…**.
3. Turn on **Beeper messages → Enabled**.

Read the "Sending a message" note in [AI classification](#ai-classification)
above before turning this on — it sends immediately, with no review step.

## Configure

First run writes `config.json` to `%APPDATA%\Index2SP\` (Windows) or
`~/.config/Index2SP/` (Linux). Edit it from the tray menu, then **Reload config**.
Most settings also have a tray shortcut — start at login, **Test all
connections** (one combined check across every configured destination), and
(under **Super Productivity** / **AI classifier** / **Joplin notes** / **Google
Calendar** / **Beeper messages**) the enabled toggles, API key / auth token /
OAuth client prompts, model picker, default project, default tags, shopping
project, require-at-least-one-tag, default notebook, default tag, and default
calendar. See [`config.example.json`](config.example.json) for every field;
the ones that matter:

| Field | Meaning |
|---|---|
| `port` / `webhookPath` | Where the listener binds. Pebble's URL = `https://<tunnel-host><webhookPath>`. |
| `inboundAuthToken` | Optional shared secret. Strongly recommended — the endpoint is internet-facing. |
| `superProductivity.accessToken` | Token from SP Settings → Misc. Required. |
| `superProductivity.projectId` / `tagIds` | Applied to every task. Blank project = inbox. |
| `superProductivity.captureTagId` / `captureTagName` | Optional tag marking Pebble captures. |
| `aiClassifier.enabled` / `apiKey` | Turns on [AI classification](#ai-classification) above. Off by default; get a key at console.anthropic.com. |
| `aiClassifier.model` / `timeoutSeconds` | Anthropic model id (default `claude-haiku-4-5`) and how long to wait before falling back. Default 8s, clamped 2–30. |
| `aiClassifier.requireTags` | Makes tagging mandatory instead of best-effort, see above. Off by default. |
| `superProductivity.shoppingProjectId` | Shopping-item override project, see above. Blank = no override. |
| `joplin.enabled` / `authToken` | Turns on sending notes to Joplin, see above. Needs the Web Clipper service on in Joplin (Tools → Options → Web Clipper). |
| `joplin.notebookId` | Notebook to file notes under. Blank = Joplin's last-selected notebook. |
| `joplin.defaultTagIds` | Tag(s) applied to every note. Used as-is while `requireTags` is off; used as the fallback when it's on but Claude didn't pick one. |
| `googleCalendar.enabled` / `clientId` / `clientSecret` | Turns on adding calendar events, see [Connecting Google Calendar](#connecting-google-calendar) above. Off by default. |
| `googleCalendar.refreshToken` | Set by the tray's Connect… flow — don't hand-edit. Blank = not connected. |
| `googleCalendar.calendarId` | Calendar to create events on. Default `primary` (the account's main calendar). |
| `beeper.enabled` / `apiToken` | Turns on sending chat messages via Beeper, see [Connecting Beeper](#connecting-beeper) above. **Sends immediately, no confirmation step.** Off by default. |
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

## Crash reporting and outage alerts

Two global hooks catch what per-feature error handling can't. Most bugs would
run on the UI thread (timer ticks, menu rebuilds) and can't be caught by an
ordinary `try`/`catch` — an uncaught one there used to take the whole app down
silently. Now it's logged, a Super Productivity task is created with the
error and stack trace, and **the app keeps running**. A second hook catches
anything outside the UI thread as a backstop; that one can't prevent the
process from terminating, but still gets the crash logged and a best-effort
task out first. Neither restarts a fully crashed process — that needs OS-level
supervision (a systemd unit, a Scheduled Task), which isn't set up here.

The same background health checks that drive the tray status line (Super
Productivity, and Joplin/Google Calendar/Beeper whenever they're configured)
also create a Super Productivity task the moment one of them goes from
reachable (or not-yet-checked) to unreachable — once per outage, not on every
repeat check while it stays down. If Super Productivity itself is the one
that's down, that attempt just fails and logs, same as any other outage.

## Limitations

- Audio-only webhooks are rejected (422) — no text to name a task.
- The audio file is not stored; its size is noted in the task notes.
- No recurring tasks or subtask hierarchy (the SP REST API can't).
- Outbox retries are not deduplicated — if SP creates the task but the reply is
  lost, a later retry can make a second copy. Rare in practice.
- Full release notes: [Releases](https://github.com/BigWebstas/Index2SP/releases).
