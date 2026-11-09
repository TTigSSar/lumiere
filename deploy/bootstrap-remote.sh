#!/usr/bin/env bash
# Runs deploy/bootstrap.sh on the demo server (run locally from Git Bash). Guarded: the target comes
# from deploy/host.env and is refused if it is 91.98.170.128 / ssh alias dorent-prod (ADR-001).
# Needs Tigran's approval (provisioning).
set -euo pipefail
# shellcheck disable=SC1091
. "$(dirname "$0")/lib.sh"
lumiere_guard_host "$HOST"
echo "==> bootstrapping $HOST"
ssh "${SSH_OPTS[@]}" "$HOST" 'bash -s' < "$DEPLOY_DIR/bootstrap.sh"
