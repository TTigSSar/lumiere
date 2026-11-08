# G0 — Live-tool connectivity test

**Question:** Can Suisse Line make a real-time HTTPS request to our endpoint during an active call,
with a configurable schema and authentication, and use the response in the conversation?

**Budget:** ≤ 3 trial minutes (one browser test call). Everything else is setup.

## 1. Prepare locally (no trial minutes)

```bash
cd C:\Users\tigra\Projects\lumiere-receptionist
copy .env.example .env
node -e "console.log(require('crypto').randomBytes(24).toString('base64url'))"   # paste as G0_SECRET in .env
node tools/g0-probe/probe.mjs
```

In a second terminal, open a temporary HTTPS tunnel (no account needed):

```bash
winget install --id Cloudflare.cloudflared      # once
cloudflared tunnel --url http://localhost:8787
# → prints https://<random>.trycloudflare.com
curl https://<random>.trycloudflare.com/health    # must return {"ok":true}
```

The URL changes every time cloudflared restarts — keep the terminal open for the whole test.

## 2. Configure a probe agent in Suisse Line

Create a separate agent **"G0 Probe"**, language English. System prompt:

```
You are a test assistant for a connectivity check. Speak English. When the user says
"test one", call the tool g0_echo with service set to "haircut" and read out the "say" field.
When the user says "test two", call g0_slots and read out every spoken_label.
When the user says "test three", call g0_slow and read out the "say" field.
When the user says "test four", call g0_error and tell the user exactly what happened.
Never invent a code word; only read what a tool returned.
```

Add these live tools (record a screenshot of the form for each — **hide the secret value**):

| Tool name | Method / URL | Header | Parameters |
|---|---|---|---|
| `g0_echo` | POST `https://<tunnel>/g0/echo` | `X-Tool-Secret: <G0_SECRET>` | `service` (string, required) |
| `g0_slots` | POST `https://<tunnel>/g0/slots` | same | none |
| `g0_slow` | POST `https://<tunnel>/g0/slow?ms=8000` | same | none |
| `g0_error` | POST `https://<tunnel>/g0/error` | same | none |

If the form has no header field, note it and put the secret nowhere yet — test without auth.

## 3. One browser test call (≤ 3 minutes)

Say in order: "test one", "test two", "test three", "test four", then hang up.
Watch the probe terminal while speaking.

## 4. Record results

Fill in after the call (classify each: VERIFIED / NOT VERIFIED / NOT AVAILABLE / REQUIRES VENDOR CONFIRMATION):

| # | Check | Evidence | Result |
|---|---|---|---|
| 1 | External HTTPS URL accepted in tool config | screenshot | |
| 2 | Request arrives **during** the call | probe log timestamp vs. speech | |
| 3 | Custom header forwarded (`shared-secret header matched: true`) | probe log | |
| 4 | Tool parameters arrive as JSON (`body keys` shows `service`) | capture file | |
| 5 | Response reaches the assistant (it says "pineapple") | what you heard | |
| 6 | Structured list read correctly (3 slot labels) | what you heard | |
| 7 | 8 s response tolerated (says "tangerine 8000") | what you heard + elapsedMs | |
| 8 | Error handling (500) — assistant does not pretend success | what you heard | |
| 9 | Call metadata in request: caller number? call ID? agent ID? timestamp? | capture file (keys only in report) | |
| 10 | Configurable timeout value in the UI | screenshot | |
| 11 | Webhook configuration available in trial; signing secret offered | screenshot | |
| 12 | Trial test number assigned; minutes remaining | screenshot | |

## 5. Gate decision

- **PASS** = 1, 2, 4, 5 verified and 7 or a ≥ 5 s timeout. Auth via header (3) is preferred; if
  headers are unsupported, a secret URL path segment is the documented fallback (ADR-010 amendment).
- Check 9 does not block G0; it decides ADR-006 (metadata vs. spoken phone number).
- **FAIL** → stop. Fallback options: (a) vendor enablement of live tools; (b) native request capture
  + signed webhook → backend books after the call and staff confirm (honestly presented as
  "booking after the call"); report to Tigran before any code.

## 6. Clean up

Stop the probe and tunnel. Delete the probe agent or its tools in Suisse Line. Keep the capture file
locally (gitignored) — it becomes the first replay fixture after removing personal data.
