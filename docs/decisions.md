# Architecture Decision Records

Status values: **Accepted** (approved by Tigran) · **Proposed** (awaiting approval) ·
**Conditional** (accepted, valid only if the named gate passes) · **Superseded**.

---

## ADR-001 — Full isolation from other projects
**Status:** Accepted (2026-11-08)
**Decision:** This project shares no infrastructure, domain, tunnel, credentials, email provider,
repository or decision log with any other project of the owner. Agent definitions were copied
once and adapted (ADR-011); the copies have no link to the originals.
**Why:** It is an independent hiring submission; coupling would risk an unrelated production system
(the candidate host was a 2 GB / 1 vCPU box already using swap) and muddy the story.
**Rejected:** Co-hosting on the existing production VPS behind a second tunnel (zero technical impact,
but still shared RAM/CPU and a shared DNS zone).

## ADR-002 — Architecture: Suisse Line live tools → ASP.NET Core 8 → Google Calendar
**Status:** Conditional on G0 (live tools can call an external HTTPS endpoint during a call)
**Decision:** Suisse Line handles telephony, speech and conversation. During a call it invokes live
tools over HTTPS on a single-instance ASP.NET Core 8 minimal API (modular monolith), which owns all
business rules and talks to Google Calendar. No frontend.
**Why:** The built-in calendar feature records appointment *requests* for staff to confirm; it does
not check availability or book in real time. A small service is the minimum that delivers real
booking, server-side rules and conflict safety.
**Rejected:** (a) Native request capture + Zapier/Make — no real-time availability, no conflict
prevention; kept as the G0-fail fallback. (b) Full backend + SQL Server + Angular dashboard — 2–3× the
effort, with the extra effort going into UI rather than the parts the challenge evaluates.

## ADR-003 — Google Calendar is the source of truth for appointments
**Status:** Accepted
**Decision:** One dedicated calendar ("Lumière Studio") in a dedicated Google account. A service
account, shared on that calendar only with "Make changes to events", scope
`https://www.googleapis.com/auth/calendar.events`. Availability = `events.list` (singleEvents) over the
day; booking metadata in private extended properties (`bookingId`, `phoneHash`, `service`, `source`).
**Why:** Staff see and edit bookings in a tool they already understand; least privilege; no second
store to keep in sync.
**Consequences:** No invitations to customers (no domain-wide delegation). Google stores data outside
Switzerland — documented limitation; production alternative is Microsoft 365 in a Swiss region.

## ADR-004 — SQLite only for waitlist and idempotency keys
**Status:** Accepted
**Decision:** One SQLite file holds `WaitlistEntry` and `IdempotencyKey`. Nothing else.
Waitlist rows are deleted 24 h after their last window; idempotency keys after 7 days.
**Rejected:** SQL Server (operational weight with no benefit); storing the waitlist in calendar
events (queries and state transitions become awkward).

## ADR-005 — Booking safety
**Status:** Accepted
**Decision:**
- Slot IDs returned by availability are HMAC-signed (`start|service|expiry`); booking accepts only a
  valid, unexpired signed slot and re-checks availability server-side.
- Bookings are serialized in-process (one async lock around "re-check → insert"), valid because the
  service runs as a single instance.
- Idempotency key `slot_id + normalised caller phone` returns the original result on retry
  (amended 2026-11-09 by ADR-014: tool-call ids change on every invocation, call ids are empty).
- The assistant confirms only from a successful tool response; any error returns a `say` text that
  never implies success.
- **No automatic deletion of competing calendar events** as a concurrency strategy.
**Known limitation:** an event added manually by staff in the same instant as a phone booking is not
serialized; documented, acceptable for a single-salon MVP. Multi-instance would need a booking store
with a unique constraint or a distributed lock.

## ADR-006 — Caller identity and appointment ownership
**Status:** Proposed — depends on G0 findings
**Decision (preferred):** `caller_phone` and `call_id` come from Suisse Line call metadata, never from
the LLM. A caller can list/cancel only bookings whose `phoneHash` matches; a foreign booking returns
`NOT_FOUND`.
**Fallback (if metadata is unavailable):** caller states their number (normalised to E.164) and the
name must also match; documented as weaker.

## ADR-007 — Additional feature: Cancellation Recovery via Telegram
**Status:** Accepted
**Decision:** On cancellation the backend identifies the freed slot, ranks compatible waitlist entries
(service duration fits, inside the customer's window, earliest-joined first) and sends the salon
staff Telegram chat the best match (first name, phone, service, slot). Staff call the customer; the
rebooking goes through the normal booking flow. No outbound AI calls, no SMS.
**Why:** Recovers lost revenue, demonstrable live, no paid features.
**Rejected:** outbound AI offer call (Business plan / API access not in budget); email (more setup,
less visible in a demo). **Privacy:** the staff chat receives the customer's phone number by
necessity; the chat is private to staff.

## ADR-008 — Hosting and delivery
**Status:** Accepted
**Decision:** Development on the laptop behind a temporary HTTPS tunnel. Demo on a new, dedicated
Hetzner CX22 running one compose project (`app` + `caddy`), HTTPS via Let's Encrypt on an
`sslip.io` hostname derived from the server IP (no domain needed). Server deleted after the interview.
Provisioning, DNS and deploys each need explicit approval.

## ADR-009 — Salon time and scheduling rules
**Status:** Accepted
**Decision:** Time zone `Europe/Zurich`. Hours Tue–Fri 09:00–18:30, Sat 08:30–16:00, closed Sun/Mon
and Zürich public holidays. Service-specific durations; 10-minute buffer after each appointment;
15-minute slot grid; no slots in the past; minimum lead time 2 h; booking horizon 30 days; at most 3
slots offered. Single resource (one chair) in the MVP.

## ADR-010 — Privacy and logging
**Status:** Accepted
**Decision:** Data minimisation (first/last name + phone only). Phone numbers masked in logs; no
transcripts or recordings stored by us; call recording off in Suisse Line unless explicitly enabled
with consent. Webhooks verified with HMAC-SHA256 and timestamp. Secrets only in env files.

## ADR-011 — Agent-assisted workflow
**Status:** Accepted (amended 2026-11-08)
**Decision:** Main session (planning, approvals, pushes) + `backend-dev`, `verifier`,
`qa-engineer`, read-only `reviewer`, `platform-engineer`. The agent definitions were cloned from
the owner's existing workflow at Tigran's request and adapted: all references to the other project
(its stack, servers, domain, ADRs and lessons) were removed; role boundaries, report contract and
general lessons were kept. The copies are independent — no shared files, hooks or runtime links.
UI-related agents (frontend, contract sync, design) were not cloned: there is no frontend.
**Rejected:** three minimal custom agents (first draft) — replaced to reuse a proven workflow.

## ADR-013 — Suisse Line agent configuration as code
**Status:** Accepted (Tigran, 2026-11-09)
**Decision:** The agent's system prompt, greeting, language, timezone, tools and webhooks live in
`agent/` in this repo and are applied with `PATCH /api/phone-agent/{id}` using the account API key
(from `.env`). The dashboard is used for inspection only.
**Why:** reproducible, reviewable and revertible (the platform keeps versions); prompt and backend
salon facts change in the same commit; strong engineering story.
**Rejected:** manual dashboard editing only (drift, no history in Git).

## ADR-014 — Live-tool contract rules (from G0)
**Status:** Accepted (Tigran, 2026-11-09)
**Decision:**
1. Tool parameters are primitives only (platform limit). `join_waitlist` takes strings
   `preferred_days` and `part_of_day` (caller's words); the server parses them — replaces `windows[]`.
2. Business outcomes (`SLOT_TAKEN`, `CLOSED`, `NOT_FOUND`, `CALENDAR_UNAVAILABLE`, …) return
   **HTTP 200** with `ok:false`, `error_code` and `say`. Non-2xx only for auth (401) and malformed
   requests (400) — on non-2xx the platform hides our body from the assistant.
3. Booking idempotency key = `slot_id + normalised caller phone`.
4. Endpoints read only `tool.arguments` and `X-Suisse-*` headers; the request body (full transcript,
   conversation, system prompt) is never logged or stored.
**Evidence:** [g0-results.md](g0-results.md).

## ADR-015 — Deterministic calendar event IDs (booking idempotency at the calendar)
**Status:** Accepted (Tigran, 2026-11-09)
**Context:** live verification showed a cold first `events.insert` exceeding the 3 s budget: the caller
heard "not booked" while Google created the event, and the retry created a duplicate (confirmed bug).
**Decision:** each booking's Google event ID is derived deterministically from
`start + service + normalised phone` (HMAC with the server key, base32hex as Google requires).
A second insert of the same booking gets 409 from Google and is treated as the existing booking.
After an insert timeout the service looks the event up by that ID before answering. A previously
cancelled event with the same ID is restored, not re-created. Timeout budget unchanged (3 s);
no warm-up (option C not chosen).
**Rejected:** (A) list-after-timeout only — race window remains; (C) longer timeout + warm-up — does
not prevent duplicates on its own.
**Regression test:** required (QA verdict) — fake calendar whose insert completes after the timeout.

## ADR-016 — Calendar event semantics and extra error codes
**Status:** Accepted (Tigran, 2026-11-09)
- Events marked "Free" (transparent) never block time; opaque all-day events block the whole day;
  timed opaque events block their time plus the 10-minute buffer.
- Additional business codes (HTTP 200, `ok:false`, ADR-014): `DATE_IN_PAST`, `BEYOND_HORIZON`,
  `INVALID_NAME`, `NO_AVAILABILITY`.

## ADR-010 amendment — webhook authentication
**Status:** Accepted (Tigran, 2026-11-09)
The official API spec documents custom headers on webhooks but no HMAC signing. Decision: verify
webhooks with a shared-secret header (constant-time compare) instead of HMAC; disable `transcript`
and `recordingUrl` payload fields. Production note: request HMAC signing from the vendor.

## ADR-012 — G0 probe is throwaway Node.js
**Status:** Proposed
**Decision:** The G0 capture endpoint is a dependency-free Node script under `tools/g0-probe/`,
deleted after P0. It is a discovery instrument, not product code, so it does not set a precedent
for the stack.
