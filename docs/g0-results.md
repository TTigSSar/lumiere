# G0 results — 2026-11-09 (Zurich 2026-11-08 ~21:16–21:20)

**Verdict: PASS.** Suisse Line live tools call our HTTPS endpoint during an active call, with our
auth header and typed arguments, and the assistant uses the response.

Setup: probe agent `G0 Probe` (id 1555) created via API (ADR-013), local probe behind a
`trycloudflare.com` tunnel. Two browser test calls (104 s + 83 s ≈ 3.1 min of 60).
Assistant read out every expected answer (reported by Tigran) — both calls.

## Checklist

| # | Check | Result | Evidence |
|---|---|---|---|
| 1 | External HTTPS URL accepted | **VERIFIED** | agent created via API with 4 tools (HTTP 201) |
| 2 | Request arrives during the call | **VERIFIED** | tool requests at 20:16:24–20:17:28Z inside call 20:16:04Z + 104 s |
| 3 | Custom header forwarded | **VERIFIED** | `X-Tool-Secret` matched on every tool and webhook request |
| 4 | Arguments arrive as JSON | **VERIFIED** | `tool.arguments = {"service":"haircut"}` |
| 5 | Response reaches the assistant | **VERIFIED** | assistant said "pineapple"; `tool.result` webhook carries our body |
| 6 | Structured list read correctly | **VERIFIED** | 3 slot labels read out |
| 7 | 8 s response tolerated | **VERIFIED** | elapsed 8003–8011 ms, "tangerine 8000" read out. Upper limit unknown |
| 8 | Error handling | **VERIFIED, important nuance** | on HTTP 500 the model receives only `"Tool call failed with status 500"` — **our error body (`say`) is discarded** |
| 9 | Call metadata in tool requests | **PARTIAL** | see below |
| 10 | Timeout configurable | NOT AVAILABLE | no timeout field in `AgentTool` |
| 11 | Webhooks in trial | **VERIFIED** | `call.started`, `call.completed`, `tool.called`, `tool.result` received with our header |
| 12 | Minutes | ≈ 56 left | `GET /api/phone-agent/1555/calls` |

## Tool request format (what our endpoints receive)

```
POST <endpoint>
X-Tool-Secret: <ours>
X-Suisse-Agent-Id: 1555
X-Suisse-Call-Id: ""                 ← empty in browser tests
X-Suisse-Timestamp: 2026-11-08T20:16:23.790349   ← UTC, no offset
X-Suisse-Tool: g0_echo
X-Suisse-Tool-Call-Id: -1794168983.790343        ← unique per invocation
{
  "event": "tool.invocation", "agentId": 1555, "agentName": "...",
  "callId": null, "timestamp": "...",
  "phoneNumber": "unknown", "toNumber": null,      ← browser test: no caller number
  "startedAt": "...", "answeredAt": "...", "direction": "inbound", "status": "in_progress",
  "transcript": "<full transcript so far>",         ← PERSONAL DATA
  "agentPrompt": "<system prompt>", "agentGreeting": "...",
  "tool": { "name": "...", "id": "...", "callId": "<tool call id>",
            "arguments": { ... }, "conversation": [ ...messages... ] }   ← PERSONAL DATA
}
```

Webhooks: `call.started` / `call.completed` carry `callId` (UUID), `phoneNumber`
(`"Browser Test"` in tests), `duration`, `endReasonCode`. Tool requests carry `callId: null`
in browser tests, so tool requests cannot yet be linked to a call.

## Consequences for the design (proposed, need approval where marked)

1. **Business errors return HTTP 200 with `ok:false` + `say`** — a non-2xx status hides our message
   from the assistant. Only auth failures (401) and malformed requests use non-2xx. *(contract rule — approval)*
2. **Read only `tool.arguments` and the `X-Suisse-*` headers; never log or store the body.** It contains
   the full transcript, conversation and system prompt. *(ADR-010 consequence)*
3. **Caller number (ADR-006) is still open.** Browser tests send `"unknown"`. Must be checked on a
   real phone call (≈ 1 min) — combine with G1. Until proven, design for the fallback
   (caller states number; name must match).
4. **Idempotency key:** `X-Suisse-Tool-Call-Id` identifies one invocation, but a model that calls
   `book` twice produces two ids. Booking idempotency therefore keys on `slot_id + normalised phone`
   (same customer, same slot → same booking), not on the tool-call id. *(ADR-005 detail — approval)*
5. **Date:** the timestamp header is UTC without offset; the prompt gets no date. Our
   `check_availability` keeps returning `today` in Zurich time (as planned).
6. **Latency budget:** 8 s works; keep our p95 under 3 s anyway (conversation flow).
