# Reverse proxy configs

Alternatives to a tunnel (`cloudflared`/`ngrok`) for exposing Index2SP's webhook listener,
for when you already run one of these on a public-facing box. Use **one** of these, not a
tunnel as well.

- [`nginx.conf`](nginx.conf) — nginx server block
- [`Caddyfile`](Caddyfile) — Caddy (automatic HTTPS)
- [`iis-web.config`](iis-web.config) — IIS, via Application Request Routing + URL Rewrite
  (needs a one-time server-wide setting enabled first — see the comments in the file)

All three assume Index2SP is running on the same machine with its config defaults
(`listenAddress: "127.0.0.1"`, `port: 8787`) and proxy everything through untouched — Index2SP's
own routing already handles `/health` and whatever `webhookPath` you've configured, and 404s
everything else. Each raises the proxy's own request-size limit above Index2SP's 30 MB cap
(Kestrel's own limit for Pebble's audio uploads), since every one of these three defaults lower
than that and would otherwise reject uploads before Index2SP ever sees them.

Point Pebble's webhook at `https://<your-domain><webhookPath>` once the proxy is up, same as
the [tunnel setup](../README.md#set-up) — the choice between tunnel and reverse proxy doesn't
change anything else in Index2SP's own configuration.
