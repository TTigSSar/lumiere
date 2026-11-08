# Lumière Studio AI Receptionist — Project Guide

Hiring-challenge project. Goal: a real caller books an appointment by phone and it appears,
correctly timed, in Google Calendar. Everything else is secondary. Owner: Tigran.

This project is **fully independent**. It shares nothing with any other project of the owner:
no servers, domains, tunnels, credentials, repositories, agent rules or decision logs.

## Stack (see docs/decisions.md)

- Suisse Line — telephony, speech, LLM, system prompt, live tools (free trial: 7 days / 60 min).
- ASP.NET Core 8 minimal API — single instance; owns ALL business rules.
- Google Calendar — source of truth for appointments (service account, `calendar.events` scope, one calendar).
- SQLite — waitlist and idempotency keys only.
- Telegram bot — internal salon-staff notifications only.
- No frontend, no SQL Server, no Redis, no brokers, no microservices.

## Rules

1. **Decisions are owner-approved.** Architecture, data model, API shape, security boundaries,
   libraries and external services are recorded in `docs/decisions.md`. Work that contradicts or
   widens an ADR stops and goes to Tigran with options and a recommendation. An area without an ADR
   is a decision to surface, not to pick silently. Agents report such questions; they never resolve them.
2. **The LLM never decides facts.** It cannot invent slots or declare a booking made. Only the
   backend's tool responses are truth; every rule is re-validated server-side.
3. **Never claim an integration works until it has been exercised for real.** Mocks and replays
   are labelled as such.
4. **Trial minutes are scarce.** Prefer unit tests and HTTP replay of captured Suisse Line
   payloads over phone calls. Log every real call in `docs/evidence/call-log.md` (date, minutes, scenario, result).
5. **Secrets** live only in `.env` / server env files, never in Git, logs or reports. Mask phone
   numbers in logs (`+41 79 *** ** 12`). No transcripts or recordings stored by us.
6. **Smallest working slice first.** Gate G1 (a real call creates a correct event) before anything else.
7. **Infrastructure** (VPS, DNS, deploy) changes only with Tigran's explicit approval for that action.

## Agents (`.claude/agents/`)

- `implementer` — writes code + unit tests next to it. Commits its own work; never pushes.
- `qa` — integration tests, HTTP replay fixtures, concurrency tests, real-call test matrix bookkeeping. Never edits app code.
- `reviewer` — read-only security / reliability / ADR-conformance review before a milestone.

Use built-in `Explore`/`Plan` for read-only questions. No other agents. Keep orchestration minimal:
the main session plans, approves diffs and pushes.

### Report format (all agents)

First line `STATUS: DONE | BLOCKED | NEEDS_INPUT | FAILED`, then `SUMMARY` (1–3 lines),
`FILES_MODIFIED`, `CHECKS` (command → pass/fail; required for writers), `OBSTACLES`, `NEXT_STEP`.
An environment failure is `BLOCKED` immediately. Two failed fixes of the same cause → `BLOCKED`.

## Commands (fill in when code exists)

| Command | What |
|---|---|
| `node tools/g0-probe/probe.mjs` | G0 capture endpoint (discovery only) |

## Salon facts

Canonical salon data (hours, services, prices, durations) lives in `docs/assistant/system-prompt.md`
and must match the backend configuration exactly.
