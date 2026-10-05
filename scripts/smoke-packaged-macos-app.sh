#!/usr/bin/env bash
set -euo pipefail

app_path="${1:-./artifacts/publish/osx-arm64/Codev.Avalonia}"
if [[ ! -x "$app_path" ]]; then
  echo "Packaged Avalonia app is missing or not executable: $app_path" >&2
  exit 1
fi
command -v osascript >/dev/null || { echo 'osascript is required for the packaged macOS interaction smoke.' >&2; exit 1; }
command -v python3 >/dev/null || { echo 'python3 is required for the packaged macOS interaction smoke.' >&2; exit 1; }

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

active_path="$CODEV_DATA_ROOT/Codev/avalonia-active-conversation.json"
conversations_path="$CODEV_DATA_ROOT/Codev/avalonia-conversations.json"
for _ in {1..120}; do
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$log_path" >&2
    echo 'Packaged Avalonia app exited before opening its macOS window.' >&2
    exit 1
  fi
  if [[ -s "$active_path" && -s "$conversations_path" ]]; then break; fi
  sleep 0.25
done
if [[ ! -s "$active_path" || ! -s "$conversations_path" ]]; then
  cat "$log_path" >&2
  echo 'The macOS app did not persist its initial conversation in the isolated profile.' >&2
  exit 1
fi

conversation_id="$(python3 - "$active_path" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversation_id = json.load(source)
if not isinstance(conversation_id, str) or not conversation_id:
    raise SystemExit("The macOS app persisted an invalid active conversation ID.")
print(conversation_id)
PY
)"

assert_mode() {
  local plan="$1"
  local code="$2"
  local description="$3"
  for _ in {1..60}; do
    if python3 - "$conversations_path" "$conversation_id" "$plan" "$code" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
expected_plan = sys.argv[3] == "true"
expected_code = sys.argv[4] == "true"
raise SystemExit(0 if conversation and
                 conversation.get("IsPlanMode") is expected_plan and
                 conversation.get("IsCodeTask") is expected_code else 1)
PY
    then
      echo "$description"
      return
    fi
    sleep 0.1
  done
  cat "$log_path" >&2
  python3 - "$conversations_path" "$conversation_id" >&2 <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
print({key: conversation.get(key) for key in ("Id", "IsPlanMode", "IsCodeTask", "Provider")} if conversation else "conversation missing")
PY
  echo "macOS packaged-app mode transition failed: $description" >&2
  exit 1
}

assert_mode false false 'Packaged macOS app started in Chat mode with a fresh isolated profile.'

send_mode_shortcut() {
  osascript <<APPLESCRIPT
tell application "System Events"
  set targetProcess to first process whose unix id is ${app_pid}
  set frontmost of targetProcess to true
  delay 0.25
  key code 46 using {command down, shift down}
end tell
APPLESCRIPT
}

send_mode_shortcut
assert_mode true false 'Packaged macOS app switched Chat → Plan with Command+Shift+M.'

# The hosted runner has no Ollama model or hosted credentials, so Plan returns
# to Chat on the next cycle instead of entering Code task.
send_mode_shortcut
assert_mode false false 'Packaged macOS app switched Plan → Chat when Code task was unavailable.'

# /status is handled locally and exercises the composer/send path without a model request.
before_message_count="$(python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
if conversation is None:
    raise SystemExit("The active macOS conversation disappeared before the /status smoke.")
print(len(conversation.get("Messages", [])))
PY
)"

osascript - "$app_pid" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    delay 0.25
    key code 37 using {command down}
    keystroke "/status"
    key code 36
  end tell
end run
APPLESCRIPT

for _ in {1..100}; do
  if python3 - "$conversations_path" "$conversation_id" "$before_message_count" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
before = int(sys.argv[3])
report = messages[-1].get("Content", "") if messages else ""
success = (len(messages) >= before + 2 and
           messages[-2].get("Content") == "/status" and
           messages[-1].get("Role") == "assistant" and
           "Model: Ollama (local)" in report and
           "Project command permissions:" in report)
raise SystemExit(0 if success else 1)
PY
  then
    echo 'Packaged macOS app sent /status from the composer and persisted the local status report without a model request.'
    exit 0
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$log_path" >&2
    echo 'Packaged Avalonia app exited during the macOS /status composer smoke.' >&2
    exit 1
  fi
  sleep 0.1
done

cat "$log_path" >&2
python3 - "$conversations_path" "$conversation_id" <<'PY' >&2 || true
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
print({"Messages": conversation.get("Messages", [])[-4:]} if conversation else "conversation missing")
PY
echo 'Packaged macOS app did not persist the expected /status report from its composer.' >&2
exit 1
