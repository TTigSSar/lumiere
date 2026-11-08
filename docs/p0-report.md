# P0 — Discovery report

Date: 2026-11-08 · Status: **GO — G0 passed 2026-11-08** ([g0-results.md](g0-results.md)). Implementation waits for Tigran's approval of the P1 tasks.

## 1. Suisse Line capabilities

Sources are public pages only; nothing below is verified inside an account yet.

| Capability | Classification | Source |
|---|---|---|
| Trial: 7 days, 60 call minutes (browser calls count), Swiss test number, all Starter + Business features, no credit card | VERIFIED (public docs) | suisse-line.ch/preise, /faq |
| Test number released after trial unless subscribed | VERIFIED (terms §5a.8) | suisse-line.ch/agb |
| English agent and voice | VERIFIED (public docs) | suisse-line.ch/en |
| Agent config: system prompt, greeting, who speaks first, knowledge base, flow builder | VERIFIED (public docs) | suisse-line.ch/plattform |
| Live tools: custom HTTPS endpoint, GET/POST, custom headers, typed parameters (string/number/boolean only) | **DOCUMENTED (official API spec, 2026-11-09)**; not yet exercised → G0 | [suisse-line-api-notes.md](suisse-line-api-notes.md) |
| Tool request body shape, call metadata in tool requests, timeout, response handling | NOT VERIFIED → G0 | — |
| Platform API key in trial | VERIFIED (dashboard offers "Generate API Key") | dashboard |
| Agent config via API (prompt, tools, webhooks, timezone, versions/revert) | DOCUMENTED | API spec |
| Webhooks: 10 event types incl. `tool.called`/`tool.result`, custom headers, selectable fields; test endpoint without minutes | DOCUMENTED | API spec |
| Webhook HMAC signing | **NOT DOCUMENTED** in the API spec (marketing claim only) → use header secret | API spec |
| Built-in calendar link | VERIFIED as request capture only ("the team confirms the appointment"); set up by vendor → REQUIRES VENDOR CONFIRMATION; not used | /blog/automatische-terminbuchung-telefon |
| REST API / OpenAPI docs | REQUIRES VENDOR CONFIRMATION; not needed | /integrationen |
| SMS | NOT AVAILABLE | /preise |
| Data stored in Switzerland; deleted within 30 days after termination | VERIFIED (terms §9.5, §12.4) | /agb |
| Separate Suisse Speech API (WebSocket agent with tools) | VERIFIED as a different product without telephony; not a fallback | suisse-speech.ch/docs/reference |

No public developer documentation or independent user reports exist → G0 must answer the open rows.

## 2. Google Calendar — feasible, free, least privilege

Dedicated Google account + calendar, service account shared on that one calendar with
"Make changes to events", scope `calendar.events`. Details: [google-calendar-setup.md](google-calendar-setup.md).

## 3. Deployment — isolated

- Development: laptop + `cloudflared` quick tunnel (temporary `trycloudflare.com` URL).
- Demo: new Hetzner CX22, compose `app` + `caddy`, Let's Encrypt on an `sslip.io` hostname.
  Requires approval to provision. Deleted after the interview.
- Nothing is shared with any other project (ADR-001).

Local environment check (2026-11-08): Node 26 ✔ · .NET SDK 9 with ASP.NET Core 8.0.26 runtime ✔
(builds and runs `net8.0`) · `cloudflared` not installed · Docker Desktop not running ·
GitHub CLI not installed.

## 4. Final MVP architecture

See [README](../README.md) and [decisions.md](decisions.md) (ADR-002 … ADR-010).

## 5. Minimal API contract (live tools)

All responses: `{ ok, say, error_code? , ... }`. All requests authenticated by shared-secret header
(or URL-path secret if headers are unsupported).

| Endpoint | Input | Output | Errors | Idempotency |
|---|---|---|---|---|
| `POST /tools/check-availability` | `service`, `date_hint`, `part_of_day?` | `today`, ≤ 3 `{slot_id, spoken_label}` | `UNKNOWN_SERVICE`, `CLOSED` (+ next open day), `CALENDAR_UNAVAILABLE` | read-only |
| `POST /tools/book` | `slot_id`, `first_name`, `last_name`, `caller_phone?`² | `booking_id`, `confirmation_text` | `SLOT_INVALID`, `SLOT_TAKEN` (+ 2 alternatives), `PHONE_REQUIRED`, `CALENDAR_UNAVAILABLE` | `slot_id + caller phone` |
| `POST /tools/my-appointments` | `caller_phone`¹ | ≤ 3 `{booking_id, spoken_label}` | — | read-only |
| `POST /tools/cancel` | `booking_id`, `caller_phone`¹ | `cancelled` | `NOT_FOUND` (incl. foreign bookings) | repeat-safe |
| `POST /tools/join-waitlist` | `service`, `preferred_days`, `part_of_day`, `first_name`, `caller_phone`¹ | `waitlist_id` | `INVALID_WINDOW` | `service + caller_phone` |
| `POST /webhooks/suisse` | vendor event | 204 | 401 bad signature | event id |
| `GET /health` | — | 200 | — | — |

¹ From call metadata if G0 shows it is available; otherwise spoken + normalised (ADR-006).
² Caller = top-level body field `phoneNumber` (call metadata) when it is a valid number — it overrides
`caller_phone`; otherwise (`unknown`, `Browser Test`, missing) the optional spoken `caller_phone`;
neither valid → `PHONE_REQUIRED` (ADR-014 / ADR-006 amendment).
Rescheduling = book new slot, then cancel old (no extra endpoint). Cancellation triggers
Cancellation Recovery internally → Telegram message to staff.

## 6. Roadmap (≈ 11 h)

| Phase | Time | Gate / acceptance |
|---|---|---|
| P0 Discovery | 1 h | **G0**: live tool reaches our HTTPS endpoint and its response is used |
| P1 Core: check-availability + book + Google Calendar, via tunnel, one real call | 3 h | **G1**: a real call creates a correctly timed event |
| P2 Reliability: rules, DST, signed slots, serialization, idempotency, ownership | 2 h | unit tests green; 20 parallel bookings → exactly 1 |
| P3 Cancellation Recovery: cancel, waitlist, Telegram | 2 h | cancellation → staff get the right customer |
| P4 Testing + demo host (CX22, with approval) | 1.5 h | evidence for each scenario |
| P5 Presentation + backup video | 1.5 h | dry run < 20 min |

## 7. Risks

| Risk | P | Impact | Mitigation / fallback |
|---|---|---|---|
| Live tools unavailable or not self-service | M | Critical | G0 first; vendor email in parallel; fallback = request capture + webhook booking after call |
| No caller number / call ID for tools | M | Medium | spoken number + name match (ADR-006 fallback) |
| Tool timeout too short | L | High | 3 s internal budget, warm Google client, `say` fallback |
| 60 minutes exhausted | M | High | replay tests; minute budget below |
| Trial expiry before interview | L | Critical | start today; backup video by day 5 |
| ASR mishears names/dates | M | Medium | weekday read-back, explicit yes |
| Live demo telephony failure | M | High | recorded backup video |

## 8. Call-minute budget (60)

G0 3 · first booking 5 · functional matrix 25 · rehearsal 10 · interview reserve 15 · slack 2.
Log every real call in `docs/evidence/call-log.md`.

## 9. Manual actions for Tigran

1. Create the Suisse Line trial (starts the 7-day clock) — note the date/time.
2. Install `cloudflared`, run G0 per [g0-live-tool-test.md](g0-live-tool-test.md), send the results table + screenshots (secrets hidden).
3. Send [vendor-questions.md](vendor-questions.md) to Suisse Line support.
4. Do the Google setup per [google-calendar-setup.md](google-calendar-setup.md).
5. Create a Telegram bot via @BotFather and a private staff group; keep the token secret.
6. Create the empty private GitHub repo `lumiere-receptionist` (no README) and share the URL.

## 10. Go / no-go

**Conditional GO.** Calendar, architecture, deployment and budget are feasible at zero Suisse Line
cost. The single open blocker is G0. No application code before G0 passes and Tigran approves the
implementation tasks.
