#!/usr/bin/env bash
# Deploys the committed HEAD to the demo server (run locally from Git Bash). Idempotent.
#   1. git archive HEAD (src/, deploy/, Dockerfile, .dockerignore) -> /opt/lumiere/src (previous kept as src.prev)
#   2. tag the running image lumiere-app:latest as lumiere-app:prev (rollback point)
#   3. docker compose -p lumiere -f deploy/compose.yml up -d --build
#   4. wait for the app container to report healthy
# Only committed code is deployed. Never runs `down -v`. Needs Tigran's approval per deploy.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HOST="${LUMIERE_HOST:-root@91.98.170.128}"
KEY="${LUMIERE_SSH_KEY:-C:/Users/tigra/secrets/lumiere/lumiere_demo_ed25519}"
SSH_OPTS=(-i "$KEY" -o StrictHostKeyChecking=accept-new -o BatchMode=yes)

cd "$REPO_ROOT"
SHA="$(git rev-parse --short HEAD)"
if [ -n "$(git status --porcelain)" ]; then
  echo "WARNING: working tree has uncommitted changes; deploying committed HEAD $SHA only"
fi
echo "==> deploying $SHA to $HOST"

echo "==> uploading source (git archive HEAD)"
git archive --format=tar HEAD src deploy Dockerfile .dockerignore \
  | ssh "${SSH_OPTS[@]}" "$HOST" "set -e
      test -s /opt/lumiere/app.env || { echo 'missing /opt/lumiere/app.env (run deploy/push-secrets.sh)' >&2; exit 1; }
      test -s /opt/lumiere/secrets/service-account.json || { echo 'missing service-account.json (run deploy/push-secrets.sh)' >&2; exit 1; }
      rm -rf /opt/lumiere/src.new && install -d -m 700 /opt/lumiere/src.new
      tar -x -C /opt/lumiere/src.new
      echo $SHA > /opt/lumiere/src.new/REVISION
      rm -rf /opt/lumiere/src.prev
      if [ -d /opt/lumiere/src ]; then mv /opt/lumiere/src /opt/lumiere/src.prev; fi
      mv /opt/lumiere/src.new /opt/lumiere/src"

echo "==> build + up"
ssh "${SSH_OPTS[@]}" "$HOST" "set -e
  cd /opt/lumiere/src
  # A broken Caddyfile would crash-loop caddy and burn Let's Encrypt attempts — validate first.
  docker run --rm -v /opt/lumiere/src/deploy/Caddyfile:/etc/caddy/Caddyfile:ro caddy:2 \
    caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
  if docker image inspect lumiere-app:latest >/dev/null 2>&1; then
    docker image tag lumiere-app:latest lumiere-app:prev
    echo 'previous image tagged lumiere-app:prev'
  fi
  docker compose -p lumiere -f deploy/compose.yml up -d --build --remove-orphans
  docker compose -p lumiere -f deploy/compose.yml ps"

echo "==> waiting for app health"
ssh "${SSH_OPTS[@]}" "$HOST" 'set -e
  cid=$(docker compose -p lumiere -f /opt/lumiere/src/deploy/compose.yml ps -q app)
  for i in $(seq 1 30); do
    s=$(docker inspect -f "{{.State.Health.Status}}" "$cid")
    echo "  app health: $s"
    [ "$s" = healthy ] && exit 0
    sleep 5
  done
  echo "app not healthy after 150 s; last log lines:" >&2
  docker logs --tail 40 "$cid" >&2
  exit 1'

echo "==> deployed $SHA. Next: deploy/smoke.sh"
