# Known limitations (MVP, single salon)

Accepted deliberately; each is a candidate "next step" in the presentation.

| # | Limitation | Impact | Production fix |
|---|---|---|---|
| 1 | Single instance; booking serialized by an in-process lock (ADR-005) | No horizontal scaling | Booking store with unique constraint or distributed lock |
| 2 | Manual staff edits in Google at the same instant as a phone booking are not serialized | Rare overlap | Same as 1 |
| 3 | If Google commits an insert later than the request budget, another caller could book an overlapping time | Needs >4.5 s Google latency + competing booking | Reconciliation job; booking store |
| 4 | Cancel that times out and is retried may not trigger Cancellation Recovery; recovery is fire-and-forget (lost on restart) | Staff not told about a freed slot | Outbox table for notifications |
| 5 | Retry within slot lifetime confirms the original time even if staff moved the event | Rare confusion | Compare event start on idempotent hit |
| 6 | One server key (`SLOT_SIGNING_KEY`) signs slots, phone hashes and event IDs — **never rotate** it during the demo | Rotation breaks ownership/idempotency of existing bookings | Derived per-purpose keys with key versioning |
| 7 | Speech recognition can mishear names (G1: "Eigran") | Wrong name on event; phone is correct | Name spelling confirmation / CRM lookup |
| 8 | Without caller ID (browser test, withheld number) callers cannot list/cancel by phone | Must call from the booking number | SMS/OTP verification |
| 9 | Google Calendar stores data outside Switzerland | nDSG consideration | Microsoft 365 in a Swiss region |
| 10 | Holiday list hard-coded until 2027-12-31; one chair, no stylists | — | Configurable calendar of closures; resources per stylist |
| 11 | Webhook endpoint deferred; no HMAC signing offered by the platform | — | Build `/webhooks/suisse`; request HMAC from vendor |
| 12 | Two simultaneous waitlist joins for the same phone+service may create two rows | Duplicate entry | Unique index |
