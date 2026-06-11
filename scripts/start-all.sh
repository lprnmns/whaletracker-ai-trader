#!/usr/bin/env bash
set -euo pipefail

SESSION_NAME="${SESSION_NAME:-whaletracker}"
ADDRESS="${ADDRESS:-0xc82b2e484b161d20eae386877d57c4e5807b5581}"

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

if ! command -v tmux >/dev/null 2>&1; then
  echo "tmux is required. Install with: sudo apt-get install tmux"
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet is required."
  exit 1
fi

if command -v python3 >/dev/null 2>&1; then
  PYTHON_BIN="python3"
elif command -v python >/dev/null 2>&1; then
  PYTHON_BIN="python"
else
  echo "python is required."
  exit 1
fi

if ! command -v pwsh >/dev/null 2>&1; then
  echo "pwsh is required (PowerShell 7+)."
  exit 1
fi

if [ -f "$ROOT_DIR/.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "$ROOT_DIR/.env"
  set +a
fi

if tmux has-session -t "$SESSION_NAME" 2>/dev/null; then
  echo "tmux session '$SESSION_NAME' already exists. Attach with:"
  echo "  tmux attach -t $SESSION_NAME"
  exit 1
fi

tmux new-session -d -s "$SESSION_NAME" -n api "cd \"$ROOT_DIR\" && dotnet run --project src/WhaleTracker.API"
tmux new-window -t "$SESSION_NAME" -n market "cd \"$ROOT_DIR\" && $PYTHON_BIN scripts/market_comparison_service.py"
tmux new-window -t "$SESSION_NAME" -n watch "cd \"$ROOT_DIR\" && pwsh -File scripts/zerion-watch.ps1 -Address \"$ADDRESS\" -StartFromLatest -OnlyNonTrash -OperationTypes trade -SendToAi -Interactive -IncludePositions"

echo "Started tmux session '$SESSION_NAME' with windows: api, market, watch."
echo "Attach with: tmux attach -t $SESSION_NAME"
