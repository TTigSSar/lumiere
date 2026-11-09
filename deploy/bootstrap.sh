#!/usr/bin/env bash
# One-time host setup for the Lumière demo server (Ubuntu 24.04, ADR-008).
# Run FROM THE LAPTOP (Git Bash), only after Tigran approved provisioning:
#   ssh -i <key> root@91.98.170.128 'bash -s' < deploy/bootstrap.sh
# Idempotent: safe to re-run. Touches only this host.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

step() { echo; echo "==> $*"; }

[ "$(id -u)" -eq 0 ] || { echo "must run as root" >&2; exit 1; }

step "apt: base packages (ca-certificates, curl, gnupg, ufw, unattended-upgrades)"
apt-get update -q
apt-get install -y -q ca-certificates curl gnupg ufw unattended-upgrades

step "Docker: official apt repository"
install -m 0755 -d /etc/apt/keyrings
if [ ! -s /etc/apt/keyrings/docker.asc ]; then
  curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
  chmod a+r /etc/apt/keyrings/docker.asc
fi
# shellcheck disable=SC1091
. /etc/os-release
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] https://download.docker.com/linux/ubuntu ${VERSION_CODENAME} stable" \
  > /etc/apt/sources.list.d/docker.list

step "Docker: engine + compose plugin"
apt-get update -q
apt-get install -y -q docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker
docker --version
docker compose version

step "Firewall (ufw): deny inbound except 22/80/443"
ufw default deny incoming
ufw default allow outgoing
ufw allow 22/tcp
ufw allow 80/tcp
ufw allow 443/tcp
ufw --force enable
ufw status verbose

step "Unattended security upgrades: on"
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
systemctl enable --now unattended-upgrades
systemctl is-active unattended-upgrades

step "Layout: /opt/lumiere/{src,secrets} (root-only)"
install -d -m 700 -o root -g root /opt/lumiere /opt/lumiere/src /opt/lumiere/secrets
ls -ld /opt/lumiere /opt/lumiere/src /opt/lumiere/secrets

step "bootstrap done"
