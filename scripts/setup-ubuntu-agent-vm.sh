#!/usr/bin/env bash
# setup-ubuntu-agent-vm.sh - turn a fresh Ubuntu (Hyper-V VM) into an agent automation box.
#
# What it does (idempotent: safe to run again, finished steps are skipped):
#   1. Disable ALL updates: unattended-upgrades, apt timers, packagekit, snapd,
#      release upgrade prompt, motd news (done first so apt/dpkg locks are freed).
#   2. Root access: optional root password, PermitRootLogin yes, password auth on,
#      optional SSH public key in /root/.ssh/authorized_keys.
#   3. Grow the root filesystem to the whole disk (growpart + LVM + ext4/xfs, online).
#   4. Hyper-V internal NIC: static IP via netplan (matched by MAC), no gateway;
#      cloud-init network config is disabled so the file survives reboots.
#   5. Claude Code CLI (latest, native installer) for root: /usr/local/bin/claude,
#      IS_SANDBOX=1 so --dangerously-skip-permissions works as root, auto-update off.
#   6. Verify everything and print PASS/FAIL. Exit 0 only when all checks pass.
#
# Usage (run as root on the target):
#   bash setup-ubuntu-agent-vm.sh --internal-ip 10.0.0.10/24 \
#        [--internal-if eth1] [--root-password 'xxx'] [--ssh-key 'ssh-ed25519 AAAA...'] \
#        [--no-internal] [--no-claude]
#   Prefer env ROOT_PASSWORD over --root-password: it stays out of the process list and,
#   read with `read -s`, out of the shell history too.
#
# One-line online install (on the target, as root):
#   read -rsp 'Root password: ' ROOT_PASSWORD; echo; export ROOT_PASSWORD
#   curl -fsSL <raw-url-of-this-file> | bash -s -- --internal-ip 10.0.0.10/24 --ssh-key '<pubkey>'
#
# From another machine over SSH, without copying the file first:
#   ssh root@<ip> "bash -s -- --internal-ip 10.0.0.10/24 --ssh-key '<pubkey>'" < setup-ubuntu-agent-vm.sh
#
# Notes:
#   - Everything below runs inside main(), so the whole file is read before anything executes
#     (safe for `curl | bash`: no command can swallow the rest of the script from stdin).
#   - --internal-ip is REQUIRED (each VM needs its own address) unless --no-internal is given.
#   - The internal NIC is auto-detected: a physical NIC that does not carry the default route.
#     If more than one candidate exists, pass --internal-if.
#   - Claude login is NOT done here; run `claude` once to log in (or copy ~/.claude from another box).

main() {
set -u
export DEBIAN_FRONTEND=noninteractive
export HOME=/root

INTERNAL_IP=""
INTERNAL_IF=""
ROOT_PASSWORD="${ROOT_PASSWORD:-}"
SSH_KEY=""
DO_INTERNAL=1
DO_CLAUDE=1

while [ $# -gt 0 ]; do
  case "$1" in
    --internal-ip)   INTERNAL_IP="$2"; shift 2 ;;
    --internal-if)   INTERNAL_IF="$2"; shift 2 ;;
    --root-password) ROOT_PASSWORD="$2"; shift 2 ;;
    --ssh-key)       SSH_KEY="$2"; shift 2 ;;
    --no-internal)   DO_INTERNAL=0; shift ;;
    --no-claude)     DO_CLAUDE=0; shift ;;
    -h|--help)       echo "See the header of setup-ubuntu-agent-vm.sh for usage."; exit 0 ;;
    *) echo "Unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ "$(id -u)" -ne 0 ]; then echo "Run as root." >&2; exit 2; fi
if [ "$DO_INTERNAL" -eq 1 ] && [ -z "$INTERNAL_IP" ]; then
  echo "--internal-ip is required (e.g. 10.0.0.10/24), or pass --no-internal." >&2; exit 2
fi
case "$INTERNAL_IP" in */*) ;; "") ;; *) INTERNAL_IP="$INTERNAL_IP/24" ;; esac

log() { printf '\n==> %s\n' "$*"; }

wait_for_apt() {
  local i=0
  while pgrep -x apt >/dev/null || pgrep -x apt-get >/dev/null || pgrep -x dpkg >/dev/null \
        || pgrep -f unattended-upgrade >/dev/null; do
    [ $i -eq 0 ] && echo "Waiting for running apt/dpkg to finish..."
    i=$((i + 1)); [ $i -gt 150 ] && { echo "apt still busy after 300s, continuing"; break; }
    sleep 2
  done
}

ensure_pkg() {
  # ensure_pkg <command> <package>
  command -v "$1" >/dev/null && return 0
  wait_for_apt
  apt-get update -qq </dev/null && apt-get install -y -qq "$2" </dev/null >/dev/null
}

# ---------------------------------------------------------------- 1. updates off
log "1/5 Disable all updates"
UPDATE_UNITS="unattended-upgrades.service apt-daily.timer apt-daily-upgrade.timer apt-daily.service
apt-daily-upgrade.service update-notifier-download.timer update-notifier-motd.timer motd-news.timer
packagekit.service fwupd-refresh.timer snapd.service snapd.socket snapd.seeded.service"
for u in $UPDATE_UNITS; do
  systemctl disable --now "$u" >/dev/null 2>&1
  systemctl mask "$u" >/dev/null 2>&1
done
pkill -f unattended-upgrade 2>/dev/null
wait_for_apt
for f in /etc/apt/apt.conf.d/20auto-upgrades /etc/apt/apt.conf.d/99-no-auto-upgrades; do
  cat > "$f" <<'APT'
APT::Periodic::Update-Package-Lists "0";
APT::Periodic::Download-Upgradeable-Packages "0";
APT::Periodic::AutocleanInterval "0";
APT::Periodic::Unattended-Upgrade "0";
APT
done
[ -f /etc/update-manager/release-upgrades ] && sed -i 's/^Prompt=.*/Prompt=never/' /etc/update-manager/release-upgrades
[ -f /etc/default/motd-news ] && sed -i 's/^ENABLED=.*/ENABLED=0/' /etc/default/motd-news
command -v pro >/dev/null && pro config set apt_news=false >/dev/null 2>&1
echo "update units masked"

# ---------------------------------------------------------------- 2. root access
log "2/5 Root access"
if [ -n "$ROOT_PASSWORD" ]; then
  passwd -u root >/dev/null 2>&1
  echo "root:$ROOT_PASSWORD" | chpasswd && echo "root password set"
fi
for f in /etc/ssh/sshd_config.d/*.conf; do
  [ -f "$f" ] || continue
  grep -qiE '^\s*PermitRootLogin\s+(no|prohibit-password|without-password)' "$f" \
    && sed -i -E 's/^\s*PermitRootLogin\s+.*/PermitRootLogin yes/I' "$f"
  grep -qiE '^\s*PasswordAuthentication\s+no' "$f" \
    && sed -i -E 's/^\s*PasswordAuthentication\s+.*/PasswordAuthentication yes/I' "$f"
done
cat > /etc/ssh/sshd_config.d/00-agent-root.conf <<'SSHD'
PermitRootLogin yes
PasswordAuthentication yes
SSHD
if [ -n "$SSH_KEY" ]; then
  install -d -m 700 /root/.ssh
  touch /root/.ssh/authorized_keys && chmod 600 /root/.ssh/authorized_keys
  grep -qF "$SSH_KEY" /root/.ssh/authorized_keys || echo "$SSH_KEY" >> /root/.ssh/authorized_keys
  echo "ssh key installed"
fi
if sshd -t; then
  systemctl reload ssh 2>/dev/null || systemctl reload sshd 2>/dev/null
  echo "sshd reloaded"
else
  echo "sshd config test FAILED, not reloaded" >&2
fi

# ---------------------------------------------------------------- 3. disk
log "3/5 Grow root filesystem"
ROOT_SRC=$(findmnt -no SOURCE /)
ROOT_FS=$(findmnt -no FSTYPE /)
ensure_pkg growpart cloud-guest-utils
if lvs "$ROOT_SRC" >/dev/null 2>&1; then
  VG=$(lvs --noheadings -o vg_name "$ROOT_SRC" | tr -d ' ')
  PART=$(pvs --noheadings -o pv_name -S vg_name="$VG" | head -1 | tr -d ' ')
else
  VG=""; PART="$ROOT_SRC"
fi
PART_NAME=$(basename "$(readlink -f "$PART")")
DISK="/dev/$(lsblk -no PKNAME "/dev/$PART_NAME" | head -1)"
PART_NUM=$(cat "/sys/class/block/$PART_NAME/partition")
growpart "$DISK" "$PART_NUM" 2>&1 | grep -v '^NOCHANGE' || true
if [ -n "$VG" ]; then
  pvresize "/dev/$PART_NAME" >/dev/null
  if [ "$(vgs --noheadings --units b --nosuffix -o vg_free "$VG" | tr -d ' ')" -gt 0 ]; then
    lvextend -l +100%FREE "$ROOT_SRC" >/dev/null
  fi
fi
case "$ROOT_FS" in
  ext4|ext3|ext2) resize2fs "$ROOT_SRC" >/dev/null 2>&1 ;;
  xfs)            xfs_growfs / >/dev/null ;;
  *)              echo "unknown root fs $ROOT_FS, not resized" ;;
esac
df -h / | tail -1

# ---------------------------------------------------------------- 4. internal NIC
NETPLAN_FILE=/etc/netplan/60-hyperv-internal.yaml
if [ "$DO_INTERNAL" -eq 1 ]; then
  log "4/5 Hyper-V internal network -> $INTERNAL_IP"
  DEF_IF=$(ip route show default | awk '{for(i=1;i<=NF;i++) if($i=="dev"){print $(i+1); exit}}')
  if [ -z "$INTERNAL_IF" ]; then
    CANDS=""
    for d in /sys/class/net/*; do
      n=$(basename "$d")
      [ "$n" = lo ] && continue
      [ "$n" = "$DEF_IF" ] && continue
      [ -e "$d/device" ] || continue
      CANDS="$CANDS $n"
    done
    set -- $CANDS
    if [ $# -eq 1 ]; then
      INTERNAL_IF=$1
    elif [ $# -gt 1 ]; then
      # Prefer the one that already holds the requested IP, else the one with no IPv4 yet.
      for n in "$@"; do
        ip -4 -o addr show dev "$n" | grep -q " ${INTERNAL_IP%/*}/" && { INTERNAL_IF=$n; break; }
      done
      if [ -z "$INTERNAL_IF" ]; then
        NOIP=""
        for n in "$@"; do ip -4 -o addr show dev "$n" | grep -q inet || NOIP="$NOIP $n"; done
        set -- $NOIP
        [ $# -eq 1 ] && INTERNAL_IF=$1
      fi
    fi
  fi
  if [ -z "$INTERNAL_IF" ]; then
    echo "Could not pick the internal NIC (default route on $DEF_IF; candidates:${CANDS:- none})." >&2
    echo "Re-run with --internal-if <name>." >&2
  else
    MAC=$(cat "/sys/class/net/$INTERNAL_IF/address")
    echo "internal NIC = $INTERNAL_IF ($MAC), default route stays on $DEF_IF"
    mkdir -p /etc/cloud/cloud.cfg.d
    echo 'network: {config: disabled}' > /etc/cloud/cloud.cfg.d/99-disable-network-config.cfg
    cat > "$NETPLAN_FILE" <<NETPLAN
network:
  version: 2
  ethernets:
    $INTERNAL_IF:
      match:
        macaddress: "$MAC"
      set-name: $INTERNAL_IF
      dhcp4: false
      dhcp6: false
      addresses: [$INTERNAL_IP]
NETPLAN
    chmod 600 /etc/netplan/*.yaml
    ip link set "$INTERNAL_IF" up
    if netplan generate; then netplan apply; else echo "netplan generate FAILED" >&2; fi
    sleep 2
    ip -br addr show "$INTERNAL_IF"
  fi
else
  log "4/5 Internal network skipped (--no-internal)"
fi

# ---------------------------------------------------------------- 5. Claude Code
if [ "$DO_CLAUDE" -eq 1 ]; then
  log "5/5 Claude Code CLI (latest) for root"
  ensure_pkg curl curl
  curl -fsSL https://claude.ai/install.sh | bash 2>&1 | grep -vE 'not in your PATH|echo .export PATH|^\s*$' | tail -3
  ln -sf /root/.local/bin/claude /usr/local/bin/claude
  # These lines must sit at the TOP of .bashrc, before the early return of non-interactive shells.
  touch /root/.bashrc
  [ -f /root/.bashrc.bak-agent-vm ] || cp /root/.bashrc /root/.bashrc.bak-agent-vm
  sed -i '/^export IS_SANDBOX=1$/d; /^export PATH="\$HOME\/.local\/bin:\$PATH"$/d' /root/.bashrc
  sed -i '1i export PATH="$HOME/.local/bin:$PATH"' /root/.bashrc
  sed -i '1i export IS_SANDBOX=1' /root/.bashrc
  grep -q '^IS_SANDBOX=1' /etc/environment || echo 'IS_SANDBOX=1' >> /etc/environment
  echo 'export PATH="$HOME/.local/bin:$PATH"' > /etc/profile.d/claude-path.sh
  mkdir -p /root/.claude
  python3 - <<'PY'
import json, os
p = "/root/.claude/settings.json"
try:
    data = json.load(open(p)) if os.path.exists(p) else {}
except ValueError:
    data = {}
data.setdefault("env", {})["DISABLE_AUTOUPDATER"] = "1"
json.dump(data, open(p, "w"), indent=2)
PY
  claude --version
else
  log "5/5 Claude skipped (--no-claude)"
fi

# ---------------------------------------------------------------- verify
log "Verify"
FAILS=0
check() {
  # check <label> <command...>
  local label=$1; shift
  if "$@" >/dev/null 2>&1; then printf '  PASS  %s\n' "$label"
  else printf '  FAIL  %s\n' "$label"; FAILS=$((FAILS + 1)); fi
}
check "root password usable (not locked)"   sh -c 'passwd -S root | grep -q " P "'
check "sshd: PermitRootLogin yes"           sh -c 'sshd -T | grep -qx "permitrootlogin yes"'
check "sshd: PasswordAuthentication yes"    sh -c 'sshd -T | grep -qx "passwordauthentication yes"'
[ -n "$SSH_KEY" ] && check "ssh key in authorized_keys" grep -qF "$SSH_KEY" /root/.ssh/authorized_keys
check "partition fills the disk"            sh -c "growpart --dry-run $DISK $PART_NUM | grep -q NOCHANGE"
[ -n "$VG" ] && check "LVM volume group has no free space" \
  sh -c "[ \"\$(vgs --noheadings --units b --nosuffix -o vg_free $VG | tr -d ' ')\" -eq 0 ]"
if [ "$DO_INTERNAL" -eq 1 ]; then
  check "internal IP $INTERNAL_IP on ${INTERNAL_IF:-?}" \
    sh -c "[ -n '$INTERNAL_IF' ] && ip -4 -o addr show dev '$INTERNAL_IF' | grep -q ' $INTERNAL_IP '"
  check "netplan file persists the IP"     grep -q "$INTERNAL_IP" "$NETPLAN_FILE"
fi
for u in unattended-upgrades.service apt-daily.timer apt-daily-upgrade.timer packagekit.service snapd.service; do
  if systemctl list-unit-files "$u" --no-legend 2>/dev/null | grep -q .; then
    check "$u masked" sh -c "[ \"\$(systemctl is-enabled $u 2>&1)\" = masked ]"
  fi
done
check "APT periodic upgrades = 0"           grep -q 'Unattended-Upgrade "0"' /etc/apt/apt.conf.d/20auto-upgrades
if [ "$DO_CLAUDE" -eq 1 ]; then
  check "claude in login shell"            bash -lc 'claude --version'
  check "claude in non-interactive sh"     env -i PATH=/usr/bin:/bin:/usr/local/bin HOME=/root sh -c 'claude --version'
  check "IS_SANDBOX=1 in /etc/environment" grep -qx 'IS_SANDBOX=1' /etc/environment
  check "IS_SANDBOX=1 at top of .bashrc"   sh -c 'head -3 /root/.bashrc | grep -qx "export IS_SANDBOX=1"'
  check "claude auto-update disabled"      grep -q DISABLE_AUTOUPDATER /root/.claude/settings.json
fi

echo
if [ "$FAILS" -eq 0 ]; then
  echo "ALL CHECKS PASSED. Next: run 'claude' once as root to log in."
  exit 0
fi
echo "$FAILS CHECK(S) FAILED - see above."
exit 1
}

main "$@"
