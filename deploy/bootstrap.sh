#!/usr/bin/env bash
# One-time host setup for the Lumière demo server (Ubuntu 24.04, ADR-008).
# Run FROM THE LAPTOP (Git Bash), only after Tigran approved provisioning:
#   deploy/bootstrap-remote.sh        (guarded; target address from deploy/host.env)
# Idempotent: safe to re-run. Touches only this host.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

step() { echo; echo "==> $*"; }

[ "$(id -u)" -eq 0 ] || { echo "must run as root" >&2; exit 1; }

# On-host isolation guard (ADR-001): never touch another project's server.
if hostname -I 2>/dev/null | tr ' ' '\n' | grep -qxF '91.98.170.128' || [ -e /opt/dorent ]; then
  echo "ABORT: this host is not the Lumiere demo server (91.98.170.128 / /opt/dorent present)" >&2
  exit 97
fi

step "SSH hardening: key-only login (drop-in 10-lumiere.conf, validated before reload)"
# Only once the Lumière key is authorised — otherwise turning passwords off would lock us out.
LUMIERE_KEY_FP='SHA256:raU3xnahAV7lczGdFqiSN7sbQVFLJkpvhDjlo5nEia4'
SSHD_DROPIN=/etc/ssh/sshd_config.d/10-lumiere.conf
if [ ! -s /root/.ssh/authorized_keys ] || ! ssh-keygen -lf /root/.ssh/authorized_keys 2>/dev/null | grep -qF "$LUMIERE_KEY_FP"; then
  echo "ABORT: the lumiere-demo key ($LUMIERE_KEY_FP) is not in /root/.ssh/authorized_keys; sshd left unchanged" >&2
  exit 1
fi
# 10- sorts before cloud-init's 50-cloud-init.conf (PasswordAuthentication yes); sshd keeps the first value.
want='# Managed by deploy/bootstrap.sh (Lumiere demo): key-only SSH.
PasswordAuthentication no
KbdInteractiveAuthentication no
PermitRootLogin prohibit-password'
if [ "$(cat "$SSHD_DROPIN" 2>/dev/null)" != "$want" ]; then
  backup=""
  [ -f "$SSHD_DROPIN" ] && { backup="$SSHD_DROPIN.bak"; cp -p "$SSHD_DROPIN" "$backup"; }
  printf '%s\n' "$want" > "$SSHD_DROPIN"
  chmod 644 "$SSHD_DROPIN"
  if ! sshd -t; then
    echo "sshd -t rejected the config; reverting $SSHD_DROPIN" >&2
    if [ -n "$backup" ]; then mv -f "$backup" "$SSHD_DROPIN"; else rm -f "$SSHD_DROPIN"; fi
    exit 1
  fi
  systemctl reload ssh
  echo "sshd reloaded with $SSHD_DROPIN"
else
  echo "$SSHD_DROPIN already in place"
fi
sshd -T | grep -Ei '^(passwordauthentication|kbdinteractiveauthentication|permitrootlogin) '

step "Swap: 2 GB swapfile if the host has < 3 GB RAM and no swap (the .NET SDK build needs headroom)"
mem_kb=$(awk '/^MemTotal:/ { print $2 }' /proc/meminfo)
if [ -n "$(swapon --noheadings --show=NAME 2>/dev/null)" ]; then
  echo "swap already active:"; swapon --show
elif [ "$mem_kb" -ge 3000000 ]; then
  echo "RAM ${mem_kb} kB >= 3 GB: no swap needed"
else
  if [ ! -f /swapfile ]; then
    fallocate -l 2G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=2048 status=none
    chmod 600 /swapfile
    mkswap /swapfile
  fi
  swapon /swapfile
  grep -qE '^/swapfile[[:space:]]' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
  swapon --show
fi

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
