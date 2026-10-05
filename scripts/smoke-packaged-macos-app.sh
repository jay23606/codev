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
smoke_root="$(cd "$smoke_root" && pwd -P)"
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
  osascript - "$app_pid" >&2 <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  set reportText to ""
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    tell targetProcess
      set windowContents to entire contents of window 1
      repeat with elementIndex from 1 to count of windowContents
        set currentElement to item elementIndex of windowContents
        try
          set elementRole to value of attribute "AXRole" of currentElement
          if elementRole is "AXStaticText" or elementRole is "AXTextField" or elementRole is "AXTextArea" then
            try
              set elementValue to value of attribute "AXValue" of currentElement as text
            on error
              set elementValue to ""
            end try
            try
              set elementName to name of currentElement as text
            on error
              set elementName to ""
            end try
            if elementValue contains "Code task" or elementValue contains "workspace" or elementValue contains "model" or elementName contains "Code task" or elementName contains "workspace" or elementName contains "model" then
              set reportText to reportText & "UI status: name=" & elementName & ", value=" & elementValue & linefeed
            end if
          end if
        end try
      end repeat
    end tell
  end tell
  return reportText
end run
APPLESCRIPT
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

report_code_task_button_state() {
  osascript - "$app_pid" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    tell targetProcess
      set windowContents to entire contents of window 1
      set buttonReport to ""
      repeat with elementIndex from 1 to count of windowContents
        set currentElement to item elementIndex of windowContents
        try
          set buttonIdentifier to value of attribute "AXIdentifier" of currentElement
        on error
          set buttonIdentifier to missing value
        end try
        try
          set buttonRole to value of attribute "AXRole" of currentElement
        on error
          set buttonRole to "missing"
        end try
        if buttonRole is "AXButton" then
          try
            set buttonName to name of currentElement as text
          on error
            set buttonName to "missing"
          end try
          set identifierLabel to "missing"
          if buttonIdentifier is not missing value then set identifierLabel to buttonIdentifier as text
          set buttonReport to buttonReport & elementIndex & ": id=" & identifierLabel & ", name=" & buttonName & linefeed
        end if
        if buttonIdentifier is not missing value then
          if (buttonIdentifier as text) is "CodeTaskModeButton" then
            try
              set buttonName to name of currentElement as text
            on error
              set buttonName to "missing"
            end try
            try
              set buttonHelp to value of attribute "AXDescription" of currentElement as text
            on error
              set buttonHelp to "missing"
            end try
            return "Code task mode button: name=" & buttonName & ", help=" & buttonHelp & ", enabled=" & (enabled of currentElement as text)
          end if
        end if
      end repeat
      return "CodeTaskModeButton was not found in the macOS accessibility tree." & linefeed & buttonReport
    end tell
  end tell
end run
APPLESCRIPT
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
sleep 2
report_code_task_button_state

# Exercise the complete mode cycle now that the local model is available.
send_mode_shortcut
assert_mode true false 'Packaged macOS app switched Chat → Plan with a local model available.'
send_mode_shortcut
assert_mode false true 'Packaged macOS app switched Plan → local Code task.'
send_mode_shortcut
assert_mode false false 'Packaged macOS app switched local Code task → Chat.'

await_saved_draft() {
  local expected="$1"
  local description="$2"
  for _ in {1..100}; do
    if python3 - "$conversations_path" "$conversation_id" "$expected" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
raise SystemExit(0 if conversation and conversation.get("Draft") == sys.argv[3] else 1)
PY
    then return 0; fi
    if ! kill -0 "$app_pid" 2>/dev/null; then
      cat "$log_path" >&2
      echo "Packaged Avalonia app exited while entering $description on macOS." >&2
      return 1
    fi
    sleep 0.1
  done
  python3 - "$conversations_path" "$conversation_id" <<'PY' >&2
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
print({"Draft": conversation.get("Draft") if conversation else None})
PY
  cat "$log_path" >&2
  echo "The macOS composer did not persist $description before the smoke submitted it." >&2
  return 1
}

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
    delay 0.5
    keystroke "/status"
  end tell
end run
APPLESCRIPT
await_saved_draft "/status" "/status"
osascript - "$app_pid" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
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
    delay 0.5
    keystroke promptText
  end tell
end run
APPLESCRIPT
await_saved_draft "$chat_prompt" "the mock chat prompt"
osascript - "$app_pid" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
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
