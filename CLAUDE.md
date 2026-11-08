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

| Agent | Role | Writes? |
|---|---|---|
| `backend-dev` | Application code + unit tests next to it | yes, `src/**`, unit tests |
| `verifier` | Proves each change works: build, tests, live walk of endpoints + test calendar | no |
| `qa-engineer` | Durable suites: integration, replay, concurrency, failure injection; call log | yes, `tests/**`, `docs/evidence/**` |
| `reviewer` | Read-only security / reliability / ADR review before milestones | no |
| `platform-engineer` | Demo VPS, Docker, Caddy, tunnel, deploys, smoke | yes, infra files; server only with approval |

Built-in `Explore`/`Plan` for read-only questions. No other agents.

**Routing:** code change → `backend-dev` → `verifier` (fast). Milestone (G1, feature complete,
demo) → `qa-engineer` suites + `verifier` (full) + `reviewer`. Infra/deploy → `platform-engineer`
(approval first). Confirmed bug → `qa-engineer` verdict (regression test required / not justified).
Writers run sequentially (one codebase). The main session plans, reads each diff and pushes.

### Report contract (all agents)

First line exactly `STATUS: DONE | BLOCKED | NEEDS_INPUT | FAILED | APPROVAL_REQUIRED`, then
`SUMMARY` (1–3 lines), `FILES_MODIFIED` (list or `none`), `CHECKS` (command → pass/fail; mandatory
for writers and verifier), `OBSTACLES`, `NEXT_STEP`. `WHAT_IS_NEEDED` for BLOCKED/NEEDS_INPUT;
`REASON / PROPOSED_ACTION / RISK / EXPECTED_RESULT` for APPROVAL_REQUIRED. Role sections extend this.

**Stop and retry:** a retry names what changed since the last attempt. Two failed fixes of the same
cause → `BLOCKED`. An environment failure (credentials, port, tool missing) → `BLOCKED` at once.
A re-run that turns green is not evidence. A structural question → `NEEDS_INPUT`.

## Commands (fill in when code exists)

| Command | What |
|---|---|
| `node tools/g0-probe/probe.mjs` | G0 capture endpoint (discovery only) |

## Salon facts

Canonical salon data (hours, services, prices, durations) lives in `docs/assistant/system-prompt.md`
and must match the backend configuration exactly.
