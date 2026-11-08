# Suisse Line API — findings from the OpenAPI spec

Source: the dashboard's Interactive API Docs (`https://app.suisse-line.ch/api/docs/`, login required),
OpenAPI 3.0.3, "Suisse Line API 1.0.0", read 2026-11-09. The raw spec is not committed (vendor material).
Classification: **DOCUMENTED** = stated in the official spec (not yet exercised by us).

## Live tools — `PhoneAgent.tools[]` (`AgentTool`)

| Field | Type | Notes |
|---|---|---|
| `name` | string | "Tool name for AI to reference" |
| `description` | string | "What the tool does (for AI)" |
| `endpoint` | uri | our HTTPS URL |
| `method` | `GET` \| `POST` | |
| `headers` | map string→string | **custom headers — shared-secret auth is possible** |
| `parameters[]` | `{name, type: string\|number\|boolean, description, required}` | **primitives only — no arrays/objects** |

DOCUMENTED: external HTTPS endpoint, GET/POST, custom headers, typed parameters.
**NOT DOCUMENTED → G0:** request body shape (flat params? wrapped?), whether any call metadata
(caller number, call ID, agent ID) is added, timeout, how the response body is passed to the LLM,
behaviour on non-2xx.

## Webhooks — `PhoneAgent.webhooks[]` (`AgentWebhook`)

- `url`, `events[]`, `headers` (custom), `enabled`, `fields` (payload field selection).
- Events: `call.started`, `call.ended`, `call.transferred`, `transcript.ready`, `call.completed`,
  `transcript.user`, `transcript.assistant`, **`tool.called`, `tool.result`**, `status.update`.
- Payload fields (toggle): transcript, summary, duration, phoneNumber, toNumber, startedAt, endedAt,
  endReason/endReasonCode, direction, recordingUrl, callRef.
- **No HMAC signing in the schema** (the marketing page mentions HMAC-SHA256). Authentication via a
  custom header secret is documented. → ADR-010 amendment proposed.
- `POST /api/webhooks/test` sends a simulated event to any public URL and reports latency —
  **no trial minutes needed** to test our webhook endpoint.
- Privacy: we will disable `transcript` and `recordingUrl` in our webhook fields.

## Agent configuration via API (`PATCH /api/phone-agent/{id}`)

Relevant fields: `systemPrompt` (max 20 000 chars), `greetingMessage`, `firstSpeaker`,
`defaultLanguage` (e.g. `en`), **`timezone` (IANA, e.g. `Europe/Zurich`)**, `realtimeTurnDetection`,
`enableRecording`, `tools`, `webhooks`, `openingHoursEnabled`, `emailSummaryEnabled`.
Also: versions + revert (`/versions`, `/revert/{versionId}`), deploy/suspend.

Implication: the agent (prompt, tools, webhooks) can be kept **as code in this repo** and applied
via the API — reproducible, reviewable, revertible. → proposed ADR-013.

## Other relevant endpoints

- Calls: `GET /api/phone-agent/{id}/calls`, `/calls/{callId}` (PhoneCall has `fromNumber` =
  caller number), `/usage`, export JSON/CSV — evidence collection without recordings.
- `POST /api/phone-agent/{id}/test` — browser test session; returns **402 "Insufficient credits"**
  when minutes are exhausted (browser tests consume minutes, as the FAQ says).
- Outbound: `POST /api/outbound-agents/{id}/dial`, campaigns — exist; not used (ADR-007).
- Auth: `X-API-Key` (`sv_...`), Bearer, or session cookie. Rate limit 100 req/min/user.
- Key management: `POST /auth/api-key/generate` — available in the trial dashboard.

## Impact on our design

1. **Tool parameters are primitives.** `join_waitlist.windows[]` becomes string parameters
   (`preferred_days`, `part_of_day`) parsed server-side. Contract change → needs approval.
2. **Caller identity:** still depends on G0. If tool requests carry no caller number, ADR-006 fallback
   applies (spoken number + name match). Webhook `call.started` with `phoneNumber` cannot bind to a
   tool request unless a shared call reference is present — verify in G0.
3. **Webhook auth:** shared-secret header (+ constant-time compare), not HMAC.
4. **Agent timezone** set to `Europe/Zurich`; whether the agent prompt gets the current date is still
   unknown → our `check_availability` keeps returning `today`.
