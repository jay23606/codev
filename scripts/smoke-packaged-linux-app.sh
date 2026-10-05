#!/usr/bin/env bash
set -euo pipefail

app_path="${1:-./artifacts/publish/linux-x64/Codev.Avalonia}"
if [[ ! -x "$app_path" ]]; then
  echo "Packaged Avalonia app is missing or not executable: $app_path" >&2
  exit 1
fi
command -v xdotool >/dev/null || { echo 'xdotool is required for the packaged Linux interaction smoke.' >&2; exit 1; }
command -v jq >/dev/null || { echo 'jq is required for the packaged Linux interaction smoke.' >&2; exit 1; }

smoke_root="$(mktemp -d)"
export CODEV_DATA_ROOT="$smoke_root/data"
log_path="$smoke_root/app.log"
"$app_path" >"$log_path" 2>&1 &
app_pid=$!

cleanup() {
  if kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    for _ in {1..20}; do
      kill -0 "$app_pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -9 "$app_pid" 2>/dev/null || true
  fi
  wait "$app_pid" 2>/dev/null || true
  rm -rf -- "$smoke_root"
}
trap cleanup EXIT

window_id=''
for _ in {1..120}; do
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$log_path" >&2
    echo "Packaged Avalonia app exited before opening its Linux window." >&2
    exit 1
  fi
  window_id="$(xdotool search --onlyvisible --name '^Codev$' 2>/dev/null | head -n 1 || true)"
  if [[ -n "$window_id" ]]; then break; fi
  sleep 0.25
done
if [[ -z "$window_id" ]]; then
  cat "$log_path" >&2
  echo 'Packaged Avalonia app did not create a visible Codev window under Xvfb.' >&2
  exit 1
fi

active_path="$CODEV_DATA_ROOT/Codev/avalonia-active-conversation.json"
conversations_path="$CODEV_DATA_ROOT/Codev/avalonia-conversations.json"
for _ in {1..80}; do
  if [[ -s "$active_path" && -s "$conversations_path" ]]; then break; fi
  sleep 0.25
done
if [[ ! -s "$active_path" || ! -s "$conversations_path" ]]; then
  cat "$log_path" >&2
  echo 'The Linux app did not persist its initial conversation in the isolated profile.' >&2
  exit 1
fi
conversation_id="$(jq -r '.' "$active_path")"
if [[ -z "$conversation_id" || "$conversation_id" == null ]]; then
  echo 'The Linux app persisted an invalid active conversation ID.' >&2
  exit 1
fi

assert_mode() {
  local plan="$1"
  local code="$2"
  local description="$3"
  for _ in {1..60}; do
    if jq -e --arg id "$conversation_id" --argjson plan "$plan" --argjson code "$code" \
      '.[] | select(.Id == $id) | .IsPlanMode == $plan and .IsCodeTask == $code' \
      "$conversations_path" >/dev/null 2>&1; then
      echo "$description"
      return
    fi
    sleep 0.1
  done
  cat "$log_path" >&2
  jq --arg id "$conversation_id" '.[] | select(.Id == $id) | {Id, IsPlanMode, IsCodeTask, Provider}' "$conversations_path" >&2 || true
  echo "Linux packaged-app mode transition failed: $description" >&2
  exit 1
}

xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode true false 'Linux packaged app switched Chat → Plan with Ctrl+Shift+M.'

# GitHub's Linux package runner has no Ollama model or hosted credentials, so
# Code task is intentionally unavailable and the next cycle must return to Chat.
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode false false 'Linux packaged app switched Plan → Chat when Code task was unavailable.'

# /status is handled locally and exercises the composer/send path without a model request.
before_message_count="$(jq --arg id "$conversation_id" '[.[] | select(.Id == $id) | .Messages[]] | length' "$conversations_path")"
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+l
xdotool type --clearmodifiers --delay 1 '/status'
xdotool key --clearmodifiers Return

for _ in {1..100}; do
  if jq -e --arg id "$conversation_id" --argjson before "$before_message_count" \
    '.[] | select(.Id == $id) | .Messages as $messages |
     ($messages | length) >= ($before + 2) and
     $messages[-2].Content == "/status" and
     $messages[-1].Role == "assistant" and
     ($messages[-1].Content | contains("Model: Ollama (local)")) and
     ($messages[-1].Content | contains("Project command permissions:"))' \
    "$conversations_path" >/dev/null 2>&1; then
    echo 'Linux packaged app sent /status from the composer and persisted the local status report without a model request.'
    exit 0
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$log_path" >&2
    echo 'Packaged Avalonia app exited during the Linux /status composer smoke.' >&2
    exit 1
  fi
  sleep 0.1
done

cat "$log_path" >&2
jq --arg id "$conversation_id" '.[] | select(.Id == $id) | {Messages: .Messages[-4:]}' "$conversations_path" >&2 || true
echo 'Linux packaged app did not persist the expected /status report from its composer.' >&2
exit 1
