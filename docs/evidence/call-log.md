# Real-call log

Every real Suisse Line call (trial minutes are limited to 60).

| # | Date (Zurich) | Minutes | Scenario | Result | Evidence |
|---|---|---|---|---|---|
| 1 | 2026-11-08 21:16 | 1.7 | G0 browser test (agent 1555), tests 1–4 | PASS | docs/g0-results.md |
| 2 | 2026-11-08 21:18 | 1.4 | G0 browser test repeat, tests 1–4 | PASS | docs/g0-results.md |
| 3 | 2026-11-08 22:14 | 1.8 | **G1** real phone call (mobile → +41 22 539 09 44, agent 1557): men's cut Tue afternoon | **PASS** — event Tue 10 Nov 12:00–12:30 Europe/Zurich, confirmed; check 622 ms, book 639 ms. Issue: name misheard ("Eigran"). Assistant knew the caller number. | Google event + service log + Suisse call record |
| 4 | 2026-11-08 22:35 | 1.5 | Caller-ID check (real phone): men's cut Wed morning | **PASS** — `source=metadata` on both tools, assistant did not ask for the number; event Wed 11 Nov 09:00–09:30 "Men's cut — Tigran T."; check 688 ms, book 632 ms | service log + Google event |
