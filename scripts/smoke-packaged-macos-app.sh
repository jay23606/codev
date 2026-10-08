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
node ./scripts/mock-ollama-server.js --activity-summary --port-file "$mock_port_path" --request-log "$mock_request_log" --model-enabled-file "$model_enabled_path" >"$smoke_root/mock-ollama.log" 2>&1 &
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
printf '{"Theme":"dark","OllamaEndpoint":"http://127.0.0.1:%s/","DefaultProjectCommandPermissionMode":"Auto"}\n' "$mock_port" >"$CODEV_DATA_ROOT/Codev/avalonia-settings.json"
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

select_build_agent() {
  osascript <<APPLESCRIPT
tell application "System Events"
  set targetProcess to first process whose unix id is ${app_pid}
  set frontmost of targetProcess to true
  tell targetProcess
    set optionsButton to missing value
    set windowContents to entire contents of window 1
    repeat with currentElement in windowContents
      try
        if (name of currentElement as text) is "Conversation options" then
          set optionsButton to currentElement
          exit repeat
        end if
      end try
    end repeat
    if optionsButton is missing value then error "Could not find the conversation options button."
    click optionsButton
    delay 0.25
    set profilePicker to missing value
    set windowContents to entire contents of window 1
    repeat with currentElement in windowContents
      try
        if (name of currentElement as text) is "Primary agent profile" then
          set profilePicker to currentElement
          exit repeat
        end if
      end try
    end repeat
    if profilePicker is missing value then error "Could not find the primary agent profile picker."
    click profilePicker
  end tell
  delay 0.25
  key code 115
  key code 36
  key code 53
end tell
APPLESCRIPT
}

read_selected_agent_profile() {
  python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
if conversation is None:
    raise SystemExit("The active macOS conversation disappeared during the Auto command smoke.")
print(conversation.get("AgentProfileName") or "")
PY
}

report_mode_button_state() {
  local automation_id="$1"
  osascript - "$app_pid" "$automation_id" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  set targetIdentifier to item 2 of argv as text
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    tell targetProcess
      set windowContents to entire contents of window 1
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
        if buttonIdentifier is not missing value then
          if (buttonIdentifier as text) is targetIdentifier and buttonRole is "AXButton" then
            try
              set buttonName to name of currentElement as text
            on error
              set buttonName to "missing"
            end try
            try
              set buttonHelp to value of attribute "AXHelp" of currentElement as text
            on error
              set buttonHelp to "missing"
            end try
            if buttonHelp is "missing" then
              try
                set buttonHelp to value of attribute "AXDescription" of currentElement as text
              on error
                set buttonHelp to "missing"
              end try
            end if
            return "name=" & buttonName & ", help=" & buttonHelp & ", enabled=" & (enabled of currentElement as text)
          end if
        end if
      end repeat
      return "Button " & targetIdentifier & " was not found in the macOS accessibility tree."
    end tell
  end tell
end run
APPLESCRIPT
}

assert_mode_button_state() {
  local automation_id="$1"
  local expected_name="$2"
  local expected_help="$3"
  local wait_seconds="${4:-0}"
  local state
  local deadline=$((SECONDS + wait_seconds))
  while true; do
    state="$(report_mode_button_state "$automation_id")"
    if [[ "$state" == *"name=$expected_name,"* && "$state" == *"$expected_help"* && "$state" == *"enabled=true"* ]]; then
      echo "macOS accessibility state passed for $automation_id: $state"
      return
    fi
    if (( SECONDS >= deadline )); then break; fi
    sleep 0.25
  done
  echo "macOS accessibility state for $automation_id was incomplete after ${wait_seconds}s: $state" >&2
  exit 1
}

send_mode_shortcut
assert_mode true false 'Packaged macOS app switched Chat → Plan with Command+Shift+M.'
assert_mode_button_state PlanModeButton 'Plan mode' 'switches Plan to Chat'
send_mode_shortcut
assert_mode false false 'Packaged macOS app switched Plan → Chat when Code task was unavailable.'
assert_mode_button_state PlanModeButton 'Chat mode' 'switches Chat to Plan'

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
assert_mode_button_state PlanModeButton 'Chat mode' 'switches Chat to Plan'
assert_mode_button_state CodeTaskModeButton 'Enable Code task' 'Enable Code task' 20

# Exercise the complete mode cycle now that the local model is available.
send_mode_shortcut
assert_mode true false 'Packaged macOS app switched Chat → Plan with a local model available.'
assert_mode_button_state PlanModeButton 'Plan mode' 'switches Plan to Code task'
send_mode_shortcut
assert_mode false true 'Packaged macOS app switched Plan → local Code task.'
assert_mode_button_state CodeTaskModeButton 'Code task on' 'Code task is on'
send_mode_shortcut
assert_mode false false 'Packaged macOS app switched local Code task → Chat.'
assert_mode_button_state PlanModeButton 'Chat mode' 'switches Chat to Plan'

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

# Prove Command+F transfers typing away from the composer in the packaged
# window. The cross-platform Avalonia regression separately asserts that the
# destination is SearchTextBox.
search_focus_sentinel='Composer focus sentinel for Command+F.'
search_focus_probe='CODEV_SEARCH_SHORTCUT_PROBE'
osascript - "$app_pid" "$search_focus_sentinel" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  set sentinel to item 2 of argv
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    delay 0.25
    key code 37 using {command down}
    keystroke sentinel
  end tell
end run
APPLESCRIPT
await_saved_draft "$search_focus_sentinel" "the composer sentinel before the Command+F check"
osascript - "$app_pid" "$search_focus_probe" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  set probe to item 2 of argv
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    key code 3 using {command down}
    delay 0.25
    keystroke probe
  end tell
end run
APPLESCRIPT
await_saved_draft "$search_focus_sentinel" "an unchanged composer draft after Command+F"
echo 'macOS packaged app Command+F routed typing away from the composer without altering its draft.'
osascript - "$app_pid" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    key code 3 using {command down}
    key code 0 using {command down}
    key code 51
    key code 37 using {command down}
    key code 0 using {command down}
    key code 51
  end tell
end run
APPLESCRIPT
await_saved_draft "" "the cleared composer after the Command+F check"

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
chat_first_line='Smoke-test packaged chat on macOS.'
chat_second_line='Shift+Enter keeps this second line in the draft.'
chat_prompt=$'Smoke-test packaged chat on macOS.\nShift+Enter keeps this second line in the draft.'
osascript - "$app_pid" "$chat_first_line" "$chat_second_line" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  set firstLine to item 2 of argv
  set secondLine to item 3 of argv
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    delay 0.25
    key code 37 using {command down}
    delay 0.5
    keystroke firstLine
    key code 36 using {shift down}
    keystroke secondLine
  end tell
end run
APPLESCRIPT
await_saved_draft "$chat_prompt" "the two-line mock chat prompt after Shift+Enter"
echo 'macOS packaged app inserted a newline with Shift+Enter and persisted both draft lines.'
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
if len(requests) != 1 or any(requests[0].get(key) != value for key, value in {
        "path": "/api/chat", "model": "codev-smoke:latest", "stream": True, "keep_alive": "30m"}.items()):
    raise SystemExit(f"Packaged macOS chat request did not match expected model/stream/keep_alive fields: {requests!r}")
PY

echo 'macOS packaged app completed a real composer → streamed Ollama chat → persisted reply round-trip against a loopback mock server.'

# Exercise the real packaged Auto command path with a deterministic model call.
# The private Code task workspace inherits Auto, and the harmless Node version
# inspection must complete without opening the inline command-approval panel.
auto_prompt='Run the packaged Auto mode command smoke.'
send_mode_shortcut
assert_mode true false 'Packaged macOS app entered Plan before the Auto Code task smoke.'
send_mode_shortcut
assert_mode false true 'Packaged macOS app entered Code task for the Auto command smoke.'
selected_agent_profile="$(read_selected_agent_profile)"
if [[ "$selected_agent_profile" == "Plan" ]]; then
  echo 'The macOS smoke selected the read-only Plan agent; switching to Build before verifying Auto command execution.'
  for attempt in {1..5}; do
    select_build_agent
    for _ in {1..20}; do
      selected_agent_profile="$(read_selected_agent_profile)"
      [[ -z "$selected_agent_profile" ]] && break 2
      sleep 0.1
    done
  done
fi
if [[ -n "$selected_agent_profile" ]]; then
  echo "Expected the default Build agent for the packaged Auto command smoke, got '$selected_agent_profile'." >&2
  exit 1
fi
before_auto_count="$(python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
if conversation is None:
    raise SystemExit("The active macOS conversation disappeared before the Auto command smoke.")
print(len(conversation.get("Messages", [])))
PY
)"
osascript - "$app_pid" "$auto_prompt" <<'APPLESCRIPT'
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
await_saved_draft "$auto_prompt" "the Auto command smoke prompt"
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

for _ in {1..300}; do
  if python3 - "$conversations_path" "$conversation_id" "$before_auto_count" "$auto_prompt" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
before = int(sys.argv[3])
success = (len(messages) >= before + 2 and messages[-2].get("Content") == sys.argv[4] and
          messages[-1].get("Role") == "assistant" and
          all(value in messages[-1].get("Content", "") for value in
              ("Verification PASSED (exit code 0)", "node --version", "Packaged Auto command round-trip passed.")))
raise SystemExit(0 if success else 1)
PY
  then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during the macOS Auto command smoke.' >&2; exit 1; fi
  sleep 0.1
done

python3 - "$conversations_path" "$conversation_id" "$CODEV_DATA_ROOT" "$mock_request_log" <<'PY'
import json
import os
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
if not conversation or not conversation.get("IsCodeTask"):
    raise SystemExit("The packaged macOS conversation did not persist Code task mode.")
project = conversation.get("ProjectPath") or ""
workspace_root = os.path.join(sys.argv[3], "Codev", "workspaces")
if not os.path.realpath(project).startswith(os.path.realpath(workspace_root) + os.sep):
    raise SystemExit(f"The Auto smoke did not use a private Codev workspace: {project!r}")
if not messages or messages[-1].get("Role") != "assistant":
    raise SystemExit("The packaged macOS Auto command did not finish its assistant turn.")
transcript = messages[-1].get("Content", "")
if not all(value in transcript for value in ("Verification PASSED (exit code 0)", "node --version", "Packaged Auto command round-trip passed.")):
    raise SystemExit(f"The packaged macOS Auto command transcript did not show successful execution: {transcript[-2000:]!r}")

with open(os.path.join(sys.argv[3], "Codev", "avalonia-command-permissions.json"), encoding="utf-8") as source:
    permissions = json.load(source)
entry = next((item for item in permissions if os.path.normcase(os.path.realpath(item.get("ProjectPath", ""))) ==
             os.path.normcase(os.path.realpath(project))), None)
if entry is None or entry.get("Mode") != "Auto":
    raise SystemExit(f"The private workspace did not persist inherited Auto mode: {entry!r}")

with open(sys.argv[4], encoding="utf-8") as source:
    requests = [json.loads(line) for line in source if line.strip()]
expected_prompt = "Run the packaged Auto mode command smoke."
if len(requests) != 3:
    raise SystemExit(f"Expected one streamed chat and two Code task requests, received {requests!r}")
if requests[1].get("stream") is not False or requests[1].get("last_role") != "user" or \
        requests[1].get("last_user_message") != expected_prompt or "verify_command" not in requests[1].get("tool_names", []):
    raise SystemExit(f"The packaged macOS Code task did not request the verification tool: {requests[1]!r}")
if requests[2].get("stream") is not False or requests[2].get("last_role") != "tool" or \
        requests[2].get("last_tool_name") != "verify_command" or requests[2].get("last_user_message") != expected_prompt:
    raise SystemExit(f"The packaged macOS model did not receive the executed verification result: {requests[2]!r}")
if any(request.get("keep_alive") != "30m" for request in requests):
    raise SystemExit(f"A packaged macOS Ollama request omitted the persistent keep-alive: {requests!r}")
PY

echo 'macOS packaged Auto mode ran node --version without command approval and persisted the successful tool result.'

# Exercise multiple real tool results through the packaged app so the activity
# summary is validated on the macOS window-server build as well.
activity_prompt='Run the packaged multi-action activity-summary smoke.'
private_project="$(python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next(item for item in conversations if item.get("Id") == sys.argv[2])
print(conversation.get("ProjectPath") or "")
PY
)"
if [[ -z "$private_project" ]]; then echo 'The packaged macOS activity smoke has no private project.' >&2; exit 1; fi
printf 'Fixture marker: ACTIVITY_SOURCE_MARKER\n' >"$private_project/activity-source.txt"
before_activity_count="$(python3 - "$conversations_path" "$conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next(item for item in conversations if item.get("Id") == sys.argv[2])
print(len(conversation.get("Messages", [])))
PY
)"
osascript - "$app_pid" "$activity_prompt" <<'APPLESCRIPT'
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
await_saved_draft "$activity_prompt" "the multi-action activity-summary prompt"
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

for _ in {1..300}; do
  if python3 - "$conversations_path" "$conversation_id" "$before_activity_count" "$activity_prompt" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
before = int(sys.argv[3])
success = (len(messages) >= before + 2 and messages[-2].get("Content") == sys.argv[4] and
          messages[-1].get("Role") == "assistant" and
          "Packaged multi-action activity summary passed." in messages[-1].get("Content", ""))
raise SystemExit(0 if success else 1)
PY
  then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during the macOS multi-action smoke.' >&2; exit 1; fi
  sleep 0.1
done

python3 - "$conversations_path" "$conversation_id" "$mock_request_log" "$private_project" <<'PY'
import json
import os
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
messages = conversation.get("Messages", []) if conversation else []
turns = [message for message in messages if message.get("Role") == "assistant" and
         "Packaged multi-action activity summary passed." in message.get("Content", "")]
if len(turns) != 1:
    raise SystemExit(f"Expected one completed multi-action transcript: {turns!r}")
content = turns[0]["Content"]
if not all(value in content for value in ("activity-source.txt", "ACTIVITY_SOURCE_MARKER", "activity-result.txt", "node --version")):
    raise SystemExit(f"The multi-action transcript omitted a tool result: {content[-4000:]!r}")
if not os.path.isfile(os.path.join(sys.argv[4], "activity-result.txt")):
    raise SystemExit("The Auto multi-action task did not create its result file.")
with open(sys.argv[3], encoding="utf-8") as source:
    requests = [json.loads(line) for line in source if line.strip()]
turn = [request for request in requests if request.get("last_user_message") == "Run the packaged multi-action activity-summary smoke."]
expected = [("user", None), ("tool", "read_file"), ("tool", "search_files"), ("tool", "create_file"), ("tool", "verify_command")]
actual = [(request.get("last_role"), request.get("last_tool_name")) for request in turn]
if actual != expected:
    raise SystemExit(f"The packaged macOS multi-action sequence was unexpected: {actual!r}")
if any(request.get("keep_alive") != "30m" for request in turn):
    raise SystemExit(f"A macOS multi-action request omitted keep_alive: {turn!r}")
PY

echo 'macOS packaged Code task read, searched, created a file, and verified a command in one Auto turn.'

# Verify the macOS primary-modifier shortcut creates and activates a fresh chat.
previous_conversation_id="$(python3 - "$active_path" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    print(json.load(source))
PY
)"
osascript - "$app_pid" <<'APPLESCRIPT'
on run argv
  set targetPid to item 1 of argv as integer
  tell application "System Events"
    set targetProcess to first process whose unix id is targetPid
    set frontmost of targetProcess to true
    delay 0.3
    keystroke "n" using {command down}
  end tell
end run
APPLESCRIPT
current_conversation_id=''
for _ in {1..100}; do
  current_conversation_id="$(python3 - "$active_path" <<'PY'
import json
import sys

try:
    with open(sys.argv[1], encoding="utf-8") as source:
        print(json.load(source))
except (OSError, json.JSONDecodeError):
    pass
PY
)"
  if [[ -n "$current_conversation_id" && "$current_conversation_id" != "$previous_conversation_id" ]]; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during the macOS Command+N shortcut smoke.' >&2; exit 1; fi
  sleep 0.1
done
if [[ -z "$current_conversation_id" || "$current_conversation_id" == "$previous_conversation_id" ]] || \
   ! python3 - "$conversations_path" "$current_conversation_id" <<'PY'
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
raise SystemExit(0 if any(item.get("Id") == sys.argv[2] for item in conversations) else 1)
PY
then
  cat "$log_path" >&2
  echo 'macOS Command+N did not create and activate a persisted conversation.' >&2
  exit 1
fi
echo 'macOS packaged app used Command+N to create and activate a persisted conversation.'

cat "$log_path" >&2
python3 - "$conversations_path" "$conversation_id" <<'PY' >&2
import json
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
print({"Model": conversation.get("Model"), "Messages": conversation.get("Messages", [])[-4:]} if conversation else "conversation missing")
PY
