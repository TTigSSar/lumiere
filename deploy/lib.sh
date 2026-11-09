#!/usr/bin/env bash
# Shared settings + safety guard for the scripts that reach the demo server. Source, don't run.
# Exposes: HOST (user@ip), KEY, SSH_OPTS (array), BASE_URL, LUMIERE_IP, LUMIERE_HOSTNAME,
#          lumiere_guard_host <host-or-user@host>, lumiere_guard_url <url>.
# shellcheck disable=SC1091
DEPLOY_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$DEPLOY_DIR/host.env"

# 91.98.170.128 (ssh alias dorent-prod) is ANOTHER PROJECT'S PRODUCTION SERVER (ADR-001).
# These scripts must never connect to it, whatever the overrides say.
FORBIDDEN_IP="91.98.170.128"
FORBIDDEN_ALIAS="dorent-prod"

_lumiere_abort() { echo "ABORT: $* — refusing to connect (ADR-001 isolation)." >&2; exit 97; }

lumiere_guard_host() {
  local target="${1##*@}" resolved forbidden_resolved=""
  [ -n "$target" ] || _lumiere_abort "empty target host"
  [ "$target" = "$FORBIDDEN_ALIAS" ] && _lumiere_abort "target is the ssh alias $FORBIDDEN_ALIAS"
  # Resolve ssh-config aliases the way ssh itself would.
  resolved="$(ssh -G "$target" 2>/dev/null | awk 'tolower($1) == "hostname" { print $2; exit }')"
  forbidden_resolved="$(ssh -G "$FORBIDDEN_ALIAS" 2>/dev/null | awk 'tolower($1) == "hostname" { print $2; exit }')"
  local h
  for h in "$target" "$resolved"; do
    [ -n "$h" ] || continue
    case "$h" in
      "$FORBIDDEN_IP"|*91-98-170-128*) _lumiere_abort "target $h is $FORBIDDEN_IP" ;;
    esac
    if [ -n "$forbidden_resolved" ] && [ "$forbidden_resolved" != "$FORBIDDEN_ALIAS" ] && [ "$h" = "$forbidden_resolved" ]; then
      _lumiere_abort "target $h is what ssh alias $FORBIDDEN_ALIAS resolves to"
    fi
  done
  # Belt and braces: the target must be the configured Lumière server unless explicitly overridden.
  if [ "$resolved" != "$LUMIERE_IP" ] && [ "$target" != "$LUMIERE_IP" ] && [ "${LUMIERE_ALLOW_OTHER_HOST:-}" != 1 ]; then
    _lumiere_abort "target $target (resolves to ${resolved:-?}) is not LUMIERE_IP $LUMIERE_IP from deploy/host.env (set LUMIERE_ALLOW_OTHER_HOST=1 to override)"
  fi
}

lumiere_guard_url() {
  case "$1" in
    *"$FORBIDDEN_IP"*|*91-98-170-128*) _lumiere_abort "URL $1 points at $FORBIDDEN_IP" ;;
  esac
}

HOST="${LUMIERE_HOST:-root@$LUMIERE_IP}"
KEY="${LUMIERE_SSH_KEY:-C:/Users/tigra/secrets/lumiere/lumiere_demo_ed25519}"
# IdentitiesOnly: offer only the Lumière key, never agent/default keys.
SSH_OPTS=(-i "$KEY" -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new -o BatchMode=yes)
BASE_URL="${LUMIERE_BASE_URL:-https://$LUMIERE_HOSTNAME}"
