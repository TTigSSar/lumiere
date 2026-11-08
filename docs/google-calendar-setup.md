# Google Calendar setup (ADR-003)

Everything happens in a **new, dedicated Google account** (e.g. `lumiere.studio.demo@gmail.com`).
Do not use a personal account or any other project's account. No paid resources are needed.

## Steps

1. **Account.** Create the dedicated Google account. Turn on 2-step verification.
2. **Calendar.** In Google Calendar → *Other calendars → + → Create new calendar*:
   name `Lumière Studio`, time zone **(GMT+01:00) Central European Time – Zurich**.
   Settings → *Integrate calendar* → copy the **Calendar ID** (`…@group.calendar.google.com`).
3. **Cloud project.** <https://console.cloud.google.com> (same account) → new project
   `lumiere-receptionist` → *APIs & Services → Library* → enable **Google Calendar API**.
   No billing account is required for the Calendar API.
4. **Service account.** *IAM & Admin → Service accounts → Create*: `lumiere-booking`. Grant **no**
   project roles. *Keys → Add key → JSON* → save the file **outside the repo**,
   e.g. `C:\Users\tigra\secrets\lumiere\service-account.json`.
5. **Share the calendar** with the service account's email
   (`lumiere-booking@<project>.iam.gserviceaccount.com`) → permission
   **"Make changes to events"**. This is the only access it gets.
6. **Fill `.env`**: `GOOGLE_CALENDAR_ID`, `GOOGLE_SERVICE_ACCOUNT_KEY_PATH`.

## Least privilege

- OAuth scope used by the service: `https://www.googleapis.com/auth/calendar.events` only.
- The service account can see only calendars explicitly shared with it — just this one.
- No domain-wide delegation; therefore no invitations are sent to customers (not needed).

## Proof of booking (for the demo)

Keep `calendar.google.com` (week view, Lumière Studio calendar) open next to the call.
The booking response also logs the event's `htmlLink` (no personal data) for evidence.

## Verification before G1 (done by the implementer, no trial minutes)

A one-off integration test lists today's events and creates + deletes a clearly labelled
`[TEST]` event in the calendar.
