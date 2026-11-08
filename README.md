# Lumière Studio — AI Phone Receptionist

Hiring-challenge project: an AI phone receptionist for a fictional Zürich hair salon,
built on **Suisse Line** (telephony + voice AI) with a small **ASP.NET Core 8** service
that owns all booking rules and writes to **Google Calendar**.

> Status: **P0 discovery.** No application code yet — implementation starts only after
> gate G0 (live-tool connectivity) passes. See [docs/p0-report.md](docs/p0-report.md).

## Architecture (approved, conditional on G0)

```
Caller ── phone ──► Suisse Line (STT · LLM · TTS · system prompt)
                        │  live tools: HTTPS POST + shared-secret header
                        ▼
              ASP.NET Core 8 minimal API (single instance, modular monolith)
                ├─ Booking rules · signed slot IDs · serialized booking · idempotency
                ├─ Google Calendar ── source of truth for appointments
                ├─ SQLite ── waitlist + idempotency keys only
                └─ Cancellation Recovery ──► Telegram (salon staff only)
```

Decisions: [docs/decisions.md](docs/decisions.md).

## Repository layout

| Path | What |
|---|---|
| `docs/p0-report.md` | Discovery findings, verified vs unverified capabilities |
| `docs/decisions.md` | ADRs (accepted + proposed) |
| `docs/g0-live-tool-test.md` | The G0 experiment, step by step |
| `docs/google-calendar-setup.md` | Dedicated Google account, calendar, service account |
| `docs/vendor-questions.md` | Questions for Suisse Line support |
| `docs/assistant/system-prompt.md` | Assistant persona, salon facts, booking rules |
| `tools/g0-probe/` | Throwaway capture endpoint for G0 (deleted after P0) |

## Secrets

Nothing secret is committed. Copy `.env.example` → `.env` (gitignored).
