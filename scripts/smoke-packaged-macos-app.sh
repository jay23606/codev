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
app_pid=''
mock_pid=''
cleanup() {
  if [[ -n "$app_pid" ]] && kill -0 "$app_pid" 2>/dev/null; then
    kill "$app_pid" 2>/dev/null || true
    for _ in {1..20}; do
      kill -0 "$app_pid" 2>/dev/null || break
      sleep 0.1
    done
    kill -9 "$app_pid" 2>/dev/null || true
  fi
  if [[ -n "$app_pid" ]]; then wait "$app_pid" 2>/dev/null || true; fi
  if [[ -n "$mock_pid" ]]; then kill "$mock_pid" 2>/dev/null || true; wait "$mock_pid" 2>/dev/null || true; fi
  rm -rf -- "$smoke_root"
}
trap cleanup EXIT

mock_port_path="$smoke_root/mock-ollama.port"
mock_request_log="$smoke_root/mock-ollama-requests.jsonl"
model_enabled_path="$smoke_root/mock-ollama-model.enabled"
printf '0' >"$model_enabled_path"
node ./scripts/mock-ollama-server.js --port-file "$mock_port_path" --request-log "$mock_request_log" --model-enabled-file "$model_enabled_path" >"$smoke_root/mock-ollama.log" 2>&1 &
mock_pid=$!
for _ in {1..200}; do
  if [[ -s "$mock_port_path" ]]; then break; fi
  mock_state="$(ps -p "$mock_pid" -o stat= 2>/dev/null || true)"
  if [[ -z "$mock_state" || "$mock_state" == Z* ]]; then
    if wait "$mock_pid"; then mock_status=0; else mock_status=$?; fi
    mock_pid=''
    cat "$smoke_root/mock-ollama.log" >&2
    echo "Mock Ollama exited before startup completed (exit $mock_status)." >&2
    exit 1
  fi
  sleep 0.1
done
if [[ ! -s "$mock_port_path" ]]; then cat "$smoke_root/mock-ollama.log" >&2; echo 'Mock Ollama did not report its loopback port within 20 seconds.' >&2; exit 1; fi
mock_port="$(cat "$mock_port_path")"
mkdir -p "$CODEV_DATA_ROOT/Codev"
printf '{"Theme":"dark","OllamaEndpoint":"http://127.0.0.1:%s/"}\n' "$mock_port" >"$CODEV_DATA_ROOT/Codev/avalonia-settings.json"
"$app_path" >"$log_path" 2>&1 &
app_pid=$!

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
  for _ in {1..200}; do
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
print({key: conversation.get(key) for key in ("Id", "IsPlanMode", "IsCodeTask", "Provider", "ProjectPath")} if conversation else "conversation missing")
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
  delay 1
  key code 46 using {command down, shift down}
end tell
APPLESCRIPT
}

click_mode_button() {
  local button_name="$1"
  local result
  result="$(osascript - "$app_pid" "$button_name" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  set buttonName to item 2 of argv
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    delay 1
    tell targetProcess
      set availableNames to name of every button of window 1
      if buttonName is not in availableNames then
        set buttonReport to ""
        repeat with buttonIndex from 1 to count of buttons of window 1
          set targetButton to button buttonIndex of window 1
          try
            set buttonLabel to name of targetButton as text
          on error
            set buttonLabel to "missing"
          end try
          set buttonReport to buttonReport & buttonIndex & ": name=" & buttonLabel & ", position=" & (position of targetButton as text) & ", size=" & (size of targetButton as text) & linefeed
        end repeat
        return "Could not find button '" & buttonName & "'. Window position=" & (position of window 1 as text) & ", size=" & (size of window 1 as text) & linefeed & buttonReport
      end if
      click (first button of window 1 whose name is buttonName)
      return "Clicked '" & buttonName & "'."
    end tell
  end tell
end run
APPLESCRIPT
  )" || return 1
  printf '%s\n' "$result"
  [[ "$result" == Clicked* ]]
}

send_mode_shortcut
assert_mode true false 'Packaged macOS app switched Chat → Plan with Command+Shift+M.'
send_mode_shortcut
assert_mode false false 'Packaged macOS app switched Plan → Chat when Code task was unavailable.'

# Keep the mode-cycle check independent from model startup, then relaunch with
# the model enabled for the local chat round-trip.
kill "$app_pid" 2>/dev/null || true
wait "$app_pid" 2>/dev/null || true
app_pid=''
printf '1' >"$model_enabled_path"
"$app_path" >"$log_path" 2>&1 &
app_pid=$!
for _ in {1..120}; do
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during its model-enabled macOS relaunch.' >&2; exit 1; fi
  if [[ -s "$active_path" && -s "$conversations_path" ]]; then break; fi
  sleep 0.25
done

for _ in {1..100}; do
  if python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
raise SystemExit(0 if conversation and conversation.get("Model") == "codev-smoke:latest" else 1)
PY
  then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited while discovering the macOS mock model.' >&2; exit 1; fi
  sleep 0.1
done
python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
if conversation is None or conversation.get("Model") != "codev-smoke:latest":
    raise SystemExit("macOS packaged app did not select the mock Ollama model before chat interaction.")
PY
assert_mode false false 'Packaged macOS app restored the same conversation in Chat mode after relaunch.'
# A fresh Intel-Mac process may still be compiling Skia shaders just after
# conversation/model discovery completes. Let the window settle before input.
sleep 1

# Also exercise the eligible path that previously failed on packaged macOS:
# Plan must advance into Code task once the local model is available.
send_mode_shortcut
assert_mode true false 'Packaged macOS app switched Chat → Plan with a local model available.'
click_mode_button 'Enable Code task'
assert_mode false true 'Packaged macOS app switched Plan → local Code task.'
click_mode_button 'Code task on'
assert_mode false false 'Packaged macOS app switched local Code task → Chat.'

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
    delay 1
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
    break
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$log_path" >&2
    echo 'Packaged Avalonia app exited during the macOS /status composer smoke.' >&2
    exit 1
  fi
  sleep 0.1
done

python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
if len(messages) < 2 or messages[-2].get("Content") != "/status" or messages[-1].get("Role") != "assistant":
    print(f"Last persisted messages: {messages[-4:]!r}", file=sys.stderr)
    raise SystemExit("Packaged macOS app did not persist the expected local /status response.")
if "Model: Ollama (local)" not in messages[-1].get("Content", "") or "Project command permissions:" not in messages[-1].get("Content", ""):
    print(f"Persisted /status response: {messages[-1]!r}", file=sys.stderr)
    raise SystemExit("Packaged macOS app persisted an incomplete /status report.")
PY

before_chat_count="$(python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
if conversation is None:
    raise SystemExit("The active macOS conversation disappeared before mock chat.")
print(len(conversation.get("Messages", [])))
PY
)"
chat_prompt='Smoke-test packaged chat on macOS.'
osascript - "$app_pid" "$chat_prompt" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  set promptText to item 2 of argv
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    delay 0.25
    key code 37 using {command down}
    keystroke promptText
    key code 36
  end tell
end run
APPLESCRIPT

for _ in {1..150}; do
  if python3 - "$conversations_path" "$conversation_id" "$before_chat_count" "$chat_prompt" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
before = int(sys.argv[3])
success = (len(messages) >= before + 2 and messages[-2].get("Content") == sys.argv[4] and
           messages[-1].get("Role") == "assistant" and
           messages[-1].get("Content") == "Packaged chat round-trip passed.")
raise SystemExit(0 if success else 1)
PY
  then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during macOS mock chat.' >&2; exit 1; fi
  sleep 0.1
done

python3 - "$conversations_path" "$conversation_id" "$chat_prompt" "$mock_request_log" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
if not any(message.get("Role") == "user" and message.get("Content") == sys.argv[3] for message in messages):
    raise SystemExit("Packaged macOS chat did not persist the submitted user prompt.")
if not messages or messages[-1].get("Role") != "assistant" or messages[-1].get("Content") != "Packaged chat round-trip passed.":
    raise SystemExit(f"Packaged macOS chat did not persist the expected streamed reply: {messages[-2:]!r}")
with open(sys.argv[4], encoding="utf-8") as source:
    requests = [json.loads(line) for line in source if line.strip()]
if len(requests) != 1 or requests[0] != {"path": "/api/chat", "model": "codev-smoke:latest", "stream": True, "keep_alive": "30m"}:
    raise SystemExit(f"Packaged macOS chat request did not match expected model/stream/keep_alive fields: {requests!r}")
PY

echo 'macOS packaged app completed a real composer → streamed Ollama chat → persisted reply round-trip against a loopback mock server.'

cat "$log_path" >&2
python3 - "$conversations_path" "$conversation_id" <<'PY' >&2
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
print({"Model": conversation.get("Model"), "Messages": conversation.get("Messages", [])[-4:]} if conversation else "conversation missing")
PY
