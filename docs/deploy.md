# Demo deployment (ADR-008)

A dedicated Hetzner server runs one Docker Compose project, `lumiere`, with two containers.
It shares nothing with any other project (ADR-001). The server is deleted after the interview.

| Fact | Value |
|---|---|
| Hetzner project | `lumiere-demo` (own project, nothing else in it) |
| Server | `178.104.181.220`, Falkenstein, 2 GB RAM / 40 GB disk, x86 (host facts: `.claude/agents/platform-engineer.md`) |
| Public URL | `https://178-104-181-220.sslip.io` (Let's Encrypt via Caddy) |
| Firewall | Hetzner firewall + ufw: inbound 22, 80, 443 only |
| SSH | `ssh -i C:/Users/tigra/secrets/lumiere/lumiere_demo_ed25519 -o IdentitiesOnly=yes root@178.104.181.220` |
| Address config | `deploy/host.env` — the only place the IP/hostname lives; all scripts source it via `deploy/lib.sh` |

## Architecture

```
Suisse Line live tools ── HTTPS :443 ──► caddy (caddy:2, ports 80/443, Let's Encrypt, 1 MB body limit)
                                            │ compose network only
                                            ▼
                                          app (lumiere-app, ASP.NET Core 8, :8080, not published, 512 MB)
                                            ├─ /data            ← volume lumiere_app-data (SQLite waitlist)
                                            ├─ /run/secrets/service-account.json  ← bind mount, read-only
                                            └─ env              ← /opt/lumiere/app.env
```

The image is built on the server from the committed source (`Dockerfile`: sdk:8.0 build →
aspnet:8.0 runtime, non-root user `app` UID 1654, `ASPNETCORE_ENVIRONMENT=Production`). In
Production the app refuses to start without the Google calendar config, `SLOT_SIGNING_KEY` and
`TOOL_SHARED_SECRET` (ADR-018 C-1).

Server layout:

| Path | What | Perms |
|---|---|---|
| `/opt/lumiere/src` | `git archive` of the deployed commit (`REVISION` holds the SHA) | root 700 |
| `/opt/lumiere/src.prev` | previous deploy, for rollback | root 700 |
| `/opt/lumiere/app.env` | runtime env (secrets) | root 600 |
| `/opt/lumiere/secrets/service-account.json` | Google key | UID 1654, 400 |
| volumes `lumiere_app-data`, `lumiere_caddy-data`, `lumiere_caddy-config` | waitlist DB, certificates | Docker |

## Secrets — what and where

| Secret | Laptop | Server |
|---|---|---|
| `TOOL_SHARED_SECRET`, `SLOT_SIGNING_KEY`, `GOOGLE_CALENDAR_ID`, `TELEGRAM_BOT_TOKEN`, `TELEGRAM_STAFF_CHAT_ID` | `.env` (gitignored) | `/opt/lumiere/app.env` |
| Google service-account key | `C:\Users\tigra\secrets\lumiere\service-account.json` | `/opt/lumiere/secrets/service-account.json` |
| SSH key | `C:\Users\tigra\secrets\lumiere\lumiere_demo_ed25519` | public half in `/root/.ssh/authorized_keys` |
| `SUISSE_API_KEY`, `LUMIERE_AGENT_ID` | `.env` only | not needed |

`deploy/push-secrets.sh` builds `app.env` from the allow-listed keys of `.env` (first non-empty
value per key, as `DotEnv.cs` does) and copies both files with scp; it never prints a value.
`SLOT_SIGNING_KEY` must stay the same as on the laptop and must never be rotated during the demo
(`docs/limitations.md` #6). Nothing secret is in Git, the image or the build context (`.dockerignore`).

## First-time setup (each step needs Tigran's approval)

From Git Bash at the repo root:

```bash
KEY=C:/Users/tigra/secrets/lumiere/lumiere_demo_ed25519
# 0. SSH access works
ssh -i $KEY -o IdentitiesOnly=yes root@178.104.181.220 true
# 1. Host: key-only sshd (10-lumiere.conf), 2 GB swap (if < 3 GB RAM, no swap), Docker + compose plugin, ufw 22/80/443,
#    unattended-upgrades, /opt/lumiere/{src,secrets}
deploy/bootstrap-remote.sh
# 2. Secrets
deploy/push-secrets.sh
# 3. Build + start (first build takes a few minutes; Caddy obtains the certificate on first start)
deploy/deploy.sh
# 4. Live smoke over the public URL
deploy/smoke.sh
# 5. Point the Suisse Line agent at the server (laptop demo stays up until this step)
node agent/apply.mjs --base-url https://178-104-181-220.sslip.io --dry-run
node agent/apply.mjs --base-url https://178-104-181-220.sslip.io
```

## Update

Commit on `dev`, then `deploy/deploy.sh` and `deploy/smoke.sh`. Only committed `HEAD` is deployed.
`deploy.sh` tags the running image `lumiere-app:prev` and keeps the previous source in
`/opt/lumiere/src.prev` before building. Volumes are never removed.
Changing secrets: edit `.env`, run `deploy/push-secrets.sh`, then `deploy/deploy.sh` (recreates the
container with the new env).

## Rollback

- **App regression after a deploy** (on the server):
  ```bash
  cd /opt/lumiere && rm -rf src.bad && mv src src.bad && mv src.prev src
  docker image tag lumiere-app:prev lumiere-app:latest
  docker compose -p lumiere -f src/deploy/compose.yml up -d --no-build
  ```
  then `deploy/smoke.sh`.
- **Server unusable during the demo:** switch the agent back to the laptop
  (`node agent/apply.mjs --base-url <laptop tunnel URL>`), then on the server
  `docker compose -p lumiere -f /opt/lumiere/src/deploy/compose.yml down` (never `-v`).

## Logs and diagnostics

```bash
docker compose -p lumiere -f /opt/lumiere/src/deploy/compose.yml ps
docker compose -p lumiere -f /opt/lumiere/src/deploy/compose.yml logs --tail 100 app
docker compose -p lumiere -f /opt/lumiere/src/deploy/compose.yml logs --tail 100 caddy
```

Logs are rotated (json-file, 3 × 10 MB per container). The app logs phone numbers masked and never
logs request bodies (ADR-010); Caddy access logging is off.

## Teardown (after the interview)

1. Switch or delete the Suisse Line agent so nothing calls the server.
2. Optional: `docker compose -p lumiere -f /opt/lumiere/src/deploy/compose.yml down` (never needed for deletion).
3. Delete the server in the Hetzner project `lumiere-demo` (with its firewall and SSH key). This
   destroys the waitlist volume and the copied secrets.
4. Rotate `TOOL_SHARED_SECRET` and revoke the Google service-account key if the project continues.

## Notes

- **Isolation guard (ADR-001).** `91.98.170.128` (ssh alias `dorent-prod`) is another project's
  production server. `deploy/lib.sh` aborts every connecting script (exit 97) if the target is that IP,
  that alias, or whatever the alias resolves to, and refuses any host other than `LUMIERE_IP` unless
  `LUMIERE_ALLOW_OTHER_HOST=1`. `bootstrap.sh` repeats the check on the host itself. All ssh/scp calls
  use `IdentitiesOnly=yes` so only the Lumière key is offered.

- Docker-published ports bypass ufw; only caddy publishes 80/443, which are allowed anyway, and the
  Hetzner firewall in front enforces 22/80/443.
- Let's Encrypt limits are per hostname: `deploy.sh` validates the Caddyfile before `up`, and the
  `caddy-data` volume keeps the certificate across restarts — do not delete it.
- The healthcheck uses bash `/dev/tcp` against `127.0.0.1:8080` (the aspnet image has no curl).

## Deploy log

| Date | Commit | Smoke | Notes |
|---|---|---|---|
| 2026-11-09 10:43 (Europe/Zurich) | `1a105b1` | PASS (health 200, HTTP→HTTPS 308, 401 without secret, check-availability `ok:true`) | First deploy. Bootstrap 34 s (key-only sshd verified: password login refused, key login OK; 2 GB swap; Docker 29.9.3, Compose v5.7.0; ufw 22/80/443). `deploy.sh` 37 s wall-clock (`dotnet publish` 6.2 s on 1 vCPU; swap barely touched). Let's Encrypt certificate issued on first Caddy start. Local `dotnet test -c Release`: 262/262. Suisse Line agent not switched yet (dry-run only). |
