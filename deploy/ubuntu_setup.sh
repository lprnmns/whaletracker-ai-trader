#!/usr/bin/env bash
set -euo pipefail

APP_DIR="/opt/whaletracker"
REPO_DIR="${APP_DIR}/WhaleTracker"

if [[ ! -d "${REPO_DIR}" ]]; then
  echo "Repo not found at ${REPO_DIR}."
  echo "Copy the project to ${APP_DIR} first."
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y curl git nginx python3 python3-venv ca-certificates apt-transport-https

if ! command -v dotnet >/dev/null 2>&1; then
  wget -qO /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 9.0 --install-dir /usr/share/dotnet
  ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet
fi

if ! command -v pwsh >/dev/null 2>&1; then
  wget -qO- https://packages.microsoft.com/keys/microsoft.asc | gpg --dearmor > /etc/apt/trusted.gpg.d/microsoft.gpg
  wget -qO /etc/apt/sources.list.d/microsoft-powershell.list https://packages.microsoft.com/config/ubuntu/22.04/prod.list
  apt-get update -y
  apt-get install -y powershell
fi

if [[ ! -d "${REPO_DIR}/.venv" ]]; then
  python3 -m venv "${REPO_DIR}/.venv"
fi

"${REPO_DIR}/.venv/bin/pip" install --upgrade pip
"${REPO_DIR}/.venv/bin/pip" install -r "${REPO_DIR}/scripts/requirements.txt"

echo "Setup completed. Configure /etc/whaletracker.env and systemd units next."
