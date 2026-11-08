# Questions for Suisse Line support

Send from the trial account's email address to the support/contact address in the dashboard
(public: <https://suisse-line.ch/kontakt/>). Send it in parallel with G0 — don't wait for the answer.

---

**Subject:** Trial account — live tools (Live-Werkzeuge) technical questions

Hello,

I am evaluating Suisse Line during the free trial and am connecting a phone assistant to my own
HTTPS service using live tools. Could you help with these questions?

1. How are live tools configured: HTTP method, request body schema, and can I define parameters
   (name, type, required) that the assistant fills in?
2. Can I add custom request headers (e.g. a shared-secret header) for authentication? Do you
   publish the IP ranges your tool requests come from?
3. What is the timeout for a tool request, and what does the assistant do when it is exceeded?
4. Which call metadata is included in tool requests — caller phone number, call ID, agent ID,
   current date/time? Can I insert the current date into the system prompt as a variable?
5. Are webhooks (call.completed etc.) with HMAC signing available in the trial, and where is the
   signature header and payload format documented?
6. Is there developer documentation for live tools and webhooks I can access during the trial?

Thank you,
Tigran
