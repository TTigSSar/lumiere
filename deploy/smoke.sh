#!/usr/bin/env bash
# Live smoke check over the public HTTPS URL (run locally from Git Bash). Read-only:
# check-availability never writes to the calendar, so no test events are created.
#   1. GET  /health                                   -> 200, ok:true, calendar google, notifier telegram
#   2. GET  http://…/health                            -> redirect to HTTPS
#   3. POST /tools/check-availability without secret   -> 401
#   4. POST /tools/check-availability with secret      -> 200, ok:true or a business code
#      (CALENDAR_UNAVAILABLE / INTERNAL count as failures: Google is not reachable/authorised)
# The secret is read from the local .env into a temp header file (mode 600) — never echoed, never on argv.
set -uo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
BASE_URL="${LUMIERE_BASE_URL:-https://91-98-170-128.sslip.io}"
ENV_FILE="$REPO_ROOT/.env"
FAIL=0

pass() { echo "PASS  $*"; }
fail() { echo "FAIL  $*"; FAIL=1; }

umask 077
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# 1. health
code=$(curl -sS --max-time 15 -o "$WORK/health" -w '%{http_code}' "$BASE_URL/health" || echo 000)
body=$(cat "$WORK/health" 2>/dev/null || true)
if [ "$code" = 200 ] && grep -q '"ok":true' <<<"$body" && grep -q '"calendar":"google"' <<<"$body" && grep -q '"notifier":"telegram"' <<<"$body"; then
  pass "GET /health -> 200 $body"
else
  fail "GET /health -> $code $body (expected ok:true, calendar google, notifier telegram)"
fi

# 2. HTTP -> HTTPS redirect
http_url="http://${BASE_URL#https://}/health"
code=$(curl -sS --max-time 15 -o /dev/null -w '%{http_code}' "$http_url" || echo 000)
case "$code" in
  301|302|307|308) pass "GET $http_url -> $code (redirect to HTTPS)" ;;
  *) fail "GET $http_url -> $code (expected a redirect)" ;;
esac

PAYLOAD='{"tool":{"name":"check_availability","arguments":{"service":"mens_cut","date_hint":"next friday","part_of_day":"morning"}}}'

# 3. without secret -> 401
code=$(curl -sS --max-time 15 -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
  --data-binary "$PAYLOAD" "$BASE_URL/tools/check-availability" || echo 000)
if [ "$code" = 401 ]; then pass "POST check-availability without secret -> 401"; else fail "POST check-availability without secret -> $code (expected 401)"; fi

# 4. with secret
secret=$(awk '{ sub(/\r$/, "") } /^[[:space:]]*TOOL_SHARED_SECRET[[:space:]]*=/ { v = substr($0, index($0, "=") + 1); gsub(/^[[:space:]"\047]+|[[:space:]"\047]+$/, "", v); if (v != "") { print v; exit } }' "$ENV_FILE" 2>/dev/null)
if [ -z "$secret" ]; then
  fail "TOOL_SHARED_SECRET not found in $ENV_FILE"
else
  printf 'X-Tool-Secret: %s\n' "$secret" > "$WORK/hdr"
  unset secret
  code=$(curl -sS --max-time 20 -o "$WORK/avail" -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
    -H @"$WORK/hdr" --data-binary "$PAYLOAD" "$BASE_URL/tools/check-availability" || echo 000)
  # Print only ok / error_code — slot data is not needed in the log.
  summary=$(grep -oE '"(ok|error_code)":("[A-Z_]+"|true|false)' "$WORK/avail" 2>/dev/null | tr '\n' ' ')
  if [ "$code" != 200 ]; then
    fail "POST check-availability with secret -> $code (expected 200)"
  elif grep -q '"ok":true' "$WORK/avail"; then
    pass "POST check-availability with secret -> 200 $summary"
  elif grep -qE '"error_code":"(CALENDAR_UNAVAILABLE|INTERNAL)"' "$WORK/avail"; then
    fail "POST check-availability with secret -> 200 $summary (calendar/backend failure)"
  elif grep -q '"ok":false' "$WORK/avail"; then
    pass "POST check-availability with secret -> 200 $summary (business code, acceptable)"
  else
    fail "POST check-availability with secret -> 200 but unexpected body"
  fi
fi

echo
if [ "$FAIL" -eq 0 ]; then echo "SMOKE: PASS ($BASE_URL)"; else echo "SMOKE: FAIL ($BASE_URL)"; fi
exit "$FAIL"
