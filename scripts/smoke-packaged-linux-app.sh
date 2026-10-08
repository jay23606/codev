#!/usr/bin/env bash
set -euo pipefail

app_path="${1:-./artifacts/publish/linux-x64/Codev.Avalonia}"
if [[ ! -x "$app_path" ]]; then
  echo "Packaged Avalonia app is missing or not executable: $app_path" >&2
  exit 1
fi
command -v xdotool >/dev/null || { echo 'xdotool is required for the packaged Linux interaction smoke.' >&2; exit 1; }
command -v jq >/dev/null || { echo 'jq is required for the packaged Linux interaction smoke.' >&2; exit 1; }
command -v node >/dev/null || { echo 'node is required for the packaged Linux chat smoke.' >&2; exit 1; }

smoke_root="$(mktemp -d)"
export CODEV_DATA_ROOT="$smoke_root/data"
log_path="$smoke_root/app.log"
app_pid=''
mock_pid=''
orca_pid=''
speech_dispatcher_pid=''
stop_helper() {
  local pid="$1"
  if [[ -z "$pid" ]] || ! kill -0 "$pid" 2>/dev/null; then return; fi
  kill "$pid" 2>/dev/null || true
  for _ in {1..20}; do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.1
  done
  kill -9 "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
}
cleanup() {
  stop_helper "$orca_pid"
  stop_helper "$speech_dispatcher_pid"
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

start_orca() {
  speech_log_dir="$smoke_root/speech-dispatcher-logs"
  speech_home="$smoke_root/speech-home"
  speech_config_dir="$speech_home/.config/speech-dispatcher"
  mkdir -p "$speech_config_dir" "$speech_log_dir"
  pulseaudio --start --exit-idle-time=-1 --log-target="file:$smoke_root/pulseaudio.log"
  for _ in {1..40}; do
    if pactl info >/dev/null 2>&1; then break; fi
    sleep 0.25
  done
  if ! pactl info >/dev/null 2>&1; then
    cat "$smoke_root/pulseaudio.log" >&2 || true
    echo 'PulseAudio did not start for the packaged Orca smoke.' >&2
    exit 1
  fi
  printf '%s\n' 'AddModule "espeak-ng" "sd_espeak-ng" "espeak-ng.conf"' >"$speech_config_dir/speechd.conf"
  HOME="$speech_home" speech-dispatcher --run-single --config-dir "$speech_config_dir" --log-level 4 --log-dir "$speech_log_dir" >"$smoke_root/speech-dispatcher.out" 2>&1 &
  speech_dispatcher_pid=$!
  sleep 0.5
  if ! kill -0 "$speech_dispatcher_pid" 2>/dev/null; then
    cat "$smoke_root/speech-dispatcher.out" >&2
    echo 'Speech Dispatcher did not start for the packaged Orca smoke.' >&2
    exit 1
  fi

  HOME="$speech_home" orca --replace --enable=speech --debug-file="$smoke_root/orca-debug.log" >"$smoke_root/orca.out" 2>&1 &
  orca_pid=$!
  for _ in {1..80}; do
    if ! kill -0 "$orca_pid" 2>/dev/null; then
      cat "$smoke_root/orca.out" "$smoke_root/speech-dispatcher.out" >&2
      echo 'Orca exited before it connected to the packaged application.' >&2
      exit 1
    fi
    if [[ -s "$smoke_root/orca-debug.log" ]]; then return; fi
    sleep 0.25
  done
  cat "$smoke_root/orca.out" "$smoke_root/speech-dispatcher.out" >&2
  echo 'Orca did not initialize within 20 seconds.' >&2
  exit 1
}

assert_orca_announcement() {
  local expected_text="$1"
  for _ in {1..120}; do
    if rg --hidden --fixed-strings "$expected_text" "$speech_log_dir" >/dev/null 2>&1; then
      echo "Orca sent this focus announcement to Speech Dispatcher: $expected_text"
      return
    fi
    sleep 0.25
  done
  echo "Orca did not send the expected focus announcement to Speech Dispatcher: $expected_text" >&2
  find "$speech_log_dir" -maxdepth 2 -type f -print -exec tail -n 60 {} \; >&2 || true
  cat "$smoke_root/orca.out" "$smoke_root/speech-dispatcher.out" >&2
  exit 1
}

mock_port_path="$smoke_root/mock-ollama.port"
mock_request_log="$smoke_root/mock-ollama-requests.jsonl"
node ./scripts/mock-ollama-server.js --activity-summary --port-file "$mock_port_path" --request-log "$mock_request_log" >"$smoke_root/mock-ollama.log" 2>&1 &
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

start_orca
python3 ./scripts/assert-linux-atspi.py

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

for _ in {1..100}; do
  if jq -e --arg id "$conversation_id" '.[] | select(.Id == $id) | .Model == "codev-smoke:latest"' \
    "$conversations_path" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited while discovering the Linux mock model.' >&2; exit 1; fi
  sleep 0.1
done
if ! jq -e --arg id "$conversation_id" '.[] | select(.Id == $id) | .Model == "codev-smoke:latest"' \
  "$conversations_path" >/dev/null 2>&1; then
  cat "$log_path" >&2
  echo 'Linux packaged app did not select the mock Ollama model before mode interaction.' >&2
  exit 1
fi

assert_mode() {
  local plan="$1"
  local code="$2"
  local description="$3"
  for _ in {1..200}; do
    if jq -e --arg id "$conversation_id" --argjson plan "$plan" --argjson code "$code" \
      '.[] | select(.Id == $id) | .IsPlanMode == $plan and .IsCodeTask == $code' \
      "$conversations_path" >/dev/null 2>&1; then
      echo "$description"
      return
    fi
    sleep 0.1
  done
  cat "$log_path" >&2
  jq --arg id "$conversation_id" '.[] | select(.Id == $id) | {Id, IsPlanMode, IsCodeTask, Provider, ProjectPath}' "$conversations_path" >&2 || true
  echo "Linux packaged-app mode transition failed: $description" >&2
  exit 1
}

xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode true false 'Linux packaged app switched Chat → Plan with Ctrl+Shift+M.'

# The mock server supplies one local model, so exercise the eligible Code task
# branch before restoring Chat for the plain streaming-chat round-trip.
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode false true 'Linux packaged app switched Plan → local Code task.'
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode false false 'Linux packaged app switched local Code task → Chat.'

# Confirm Ctrl+F routes subsequent typing away from the composer. The headless
# Avalonia regression separately asserts that the target is SearchTextBox.
search_focus_sentinel='Composer focus sentinel for Ctrl+F.'
search_focus_probe='CODEV_SEARCH_SHORTCUT_PROBE'
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+l
xdotool type --clearmodifiers --delay 1 "$search_focus_sentinel"
for _ in {1..50}; do
  if jq -e --arg id "$conversation_id" --arg draft "$search_focus_sentinel" \
    '.[] | select(.Id == $id) | .Draft == $draft' "$conversations_path" >/dev/null 2>&1; then break; fi
  sleep 0.1
done
if ! jq -e --arg id "$conversation_id" --arg draft "$search_focus_sentinel" \
  '.[] | select(.Id == $id) | .Draft == $draft' "$conversations_path" >/dev/null 2>&1; then
  echo 'Linux packaged app did not save the composer sentinel before the Ctrl+F check.' >&2
  exit 1
fi
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+f
python3 ./scripts/assert-linux-atspi.py --focused 'Search conversations'
assert_orca_announcement 'Search conversations'
xdotool type --clearmodifiers --delay 1 "$search_focus_probe"
if ! jq -e --arg id "$conversation_id" --arg draft "$search_focus_sentinel" \
  '.[] | select(.Id == $id) | .Draft == $draft' "$conversations_path" >/dev/null 2>&1; then
  jq --arg id "$conversation_id" '.[] | select(.Id == $id) | {Draft}' "$conversations_path" >&2 || true
  echo 'Linux Ctrl+F left keyboard input in the composer instead of moving it to conversation search.' >&2
  exit 1
fi
echo 'Linux packaged app Ctrl+F routed typing away from the composer without altering its draft.'
xdotool key --clearmodifiers ctrl+f ctrl+a BackSpace
xdotool key --clearmodifiers ctrl+l ctrl+a BackSpace
for _ in {1..50}; do
  if jq -e --arg id "$conversation_id" '.[] | select(.Id == $id) | .Draft == ""' "$conversations_path" >/dev/null 2>&1; then break; fi
  sleep 0.1
done

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
    break
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$log_path" >&2
    echo 'Packaged Avalonia app exited during the Linux /status composer smoke.' >&2
    exit 1
  fi
  sleep 0.1
done

if ! jq -e --arg id "$conversation_id" \
  '.[] | select(.Id == $id) | .Messages as $messages |
   ($messages | length) >= 2 and $messages[-2].Content == "/status" and
   $messages[-1].Role == "assistant" and
   ($messages[-1].Content | contains("Model: Ollama (local)")) and
   ($messages[-1].Content | contains("Project command permissions:"))' \
  "$conversations_path" >/dev/null 2>&1; then
  cat "$log_path" >&2
  echo 'Linux packaged app did not persist the expected /status report from its composer.' >&2
  exit 1
fi

before_chat_count="$(jq --arg id "$conversation_id" '[.[] | select(.Id == $id) | .Messages[]] | length' "$conversations_path")"
chat_first_line='Smoke-test packaged chat on Linux.'
chat_second_line='Shift+Enter keeps this second line in the draft.'
chat_prompt=$'Smoke-test packaged chat on Linux.\nShift+Enter keeps this second line in the draft.'
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+l
xdotool type --clearmodifiers --delay 1 "$chat_first_line"
xdotool key --clearmodifiers shift+Return
xdotool type --clearmodifiers --delay 1 "$chat_second_line"

for _ in {1..50}; do
  if jq -e --arg id "$conversation_id" --arg draft "$chat_prompt" \
    '.[] | select(.Id == $id) | .Draft == $draft' "$conversations_path" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited while persisting the Linux Shift+Enter draft.' >&2; exit 1; fi
  sleep 0.1
done
if ! jq -e --arg id "$conversation_id" --arg draft "$chat_prompt" \
  '.[] | select(.Id == $id) | .Draft == $draft' "$conversations_path" >/dev/null 2>&1; then
  jq --arg id "$conversation_id" '.[] | select(.Id == $id) | {Draft}' "$conversations_path" >&2 || true
  echo 'Linux Shift+Enter did not preserve the composer as a two-line draft.' >&2
  exit 1
fi
echo 'Linux packaged app inserted a newline with Shift+Enter and persisted both draft lines.'
xdotool key --clearmodifiers Return

for _ in {1..150}; do
  if jq -e --arg id "$conversation_id" --argjson before "$before_chat_count" --arg prompt "$chat_prompt" \
    '.[] | select(.Id == $id) | .Messages as $messages |
     ($messages | length) >= ($before + 2) and
     $messages[-2].Content == $prompt and
     $messages[-1].Role == "assistant" and
     $messages[-1].Content == "Packaged chat round-trip passed."' \
    "$conversations_path" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during the Linux mock chat.' >&2; exit 1; fi
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
    raise SystemExit("Packaged Linux chat did not persist the submitted user prompt.")
if not messages or messages[-1].get("Role") != "assistant" or messages[-1].get("Content") != "Packaged chat round-trip passed.":
    raise SystemExit(f"Packaged Linux chat did not persist the expected streamed reply: {messages[-2:]!r}")
with open(sys.argv[4], encoding="utf-8") as source:
    requests = [json.loads(line) for line in source if line.strip()]
if len(requests) != 1 or any(requests[0].get(key) != value for key, value in {
        "path": "/api/chat", "model": "codev-smoke:latest", "stream": True, "keep_alive": "30m"}.items()):
    raise SystemExit(f"Packaged Linux chat request did not match expected model/stream/keep_alive fields: {requests!r}")
PY

echo 'Linux packaged app completed a real composer → streamed Ollama chat → persisted reply round-trip against a loopback mock server.'

# Exercise the real packaged Auto command path with a deterministic model call.
# The private Code task workspace inherits Auto, and the harmless Node version
# inspection must complete without opening the inline command-approval panel.
auto_prompt='Run the packaged Auto mode command smoke.'
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode true false 'Linux packaged app entered Plan before the Auto Code task smoke.'
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode false true 'Linux packaged app entered Code task for the Auto command smoke.'
before_auto_count="$(jq --arg id "$conversation_id" '[.[] | select(.Id == $id) | .Messages[]] | length' "$conversations_path")"
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+l
xdotool type --clearmodifiers --delay 1 "$auto_prompt"
xdotool key --clearmodifiers Return

for _ in {1..300}; do
  if jq -e --arg id "$conversation_id" --argjson before "$before_auto_count" --arg prompt "$auto_prompt" \
    '.[] | select(.Id == $id) | .Messages as $messages |
     ($messages | length) >= ($before + 2) and
     $messages[-2].Content == $prompt and
     $messages[-1].Role == "assistant" and
     ($messages[-1].Content | contains("Verification PASSED (exit code 0)")) and
     ($messages[-1].Content | contains("node --version")) and
     ($messages[-1].Content | contains("Packaged Auto command round-trip passed."))' \
    "$conversations_path" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during the Linux Auto command smoke.' >&2; exit 1; fi
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
    raise SystemExit("The packaged Linux conversation did not persist Code task mode.")
project = conversation.get("ProjectPath") or ""
workspace_root = os.path.join(sys.argv[3], "Codev", "workspaces")
if not os.path.realpath(project).startswith(os.path.realpath(workspace_root) + os.sep):
    raise SystemExit(f"The Auto smoke did not use a private Codev workspace: {project!r}")
if not messages or messages[-1].get("Role") != "assistant":
    raise SystemExit("The packaged Linux Auto command did not finish its assistant turn.")
transcript = messages[-1].get("Content", "")
if not all(value in transcript for value in ("Verification PASSED (exit code 0)", "node --version", "Packaged Auto command round-trip passed.")):
    raise SystemExit(f"The packaged Linux Auto command transcript did not show successful execution: {transcript[-2000:]!r}")

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
    raise SystemExit(f"The packaged Linux Code task did not request the verification tool: {requests[1]!r}")
if requests[2].get("stream") is not False or requests[2].get("last_role") != "tool" or \
        requests[2].get("last_tool_name") != "verify_command" or requests[2].get("last_user_message") != expected_prompt:
    raise SystemExit(f"The packaged Linux model did not receive the executed verification result: {requests[2]!r}")
if any(request.get("keep_alive") != "30m" for request in requests):
    raise SystemExit(f"A packaged Linux Ollama request omitted the persistent keep-alive: {requests!r}")
PY

echo 'Linux packaged Auto mode ran node --version without command approval and persisted the successful tool result.'

# Exercise a deterministic read/search/create/verify Code task through the
# packaged app so the persisted transcript contains several real tool results
# for the collapsed-activity view.
activity_prompt='Run the packaged multi-action activity-summary smoke.'
private_project="$(jq -r --arg id "$conversation_id" '.[] | select(.Id == $id) | .ProjectPath' "$conversations_path")"
if [[ -z "$private_project" || "$private_project" == null ]]; then echo 'The Linux activity smoke has no private project.' >&2; exit 1; fi
printf 'Fixture marker: ACTIVITY_SOURCE_MARKER\n' >"$private_project/activity-source.txt"
before_activity_count="$(jq --arg id "$conversation_id" '[.[] | select(.Id == $id) | .Messages[]] | length' "$conversations_path")"
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+l
xdotool type --clearmodifiers --delay 1 "$activity_prompt"
xdotool key --clearmodifiers Return

for _ in {1..300}; do
  if jq -e --arg id "$conversation_id" --argjson before "$before_activity_count" --arg prompt "$activity_prompt" \
    '.[] | select(.Id == $id) | .Messages as $messages |
     ($messages | length) >= ($before + 2) and
     $messages[-2].Content == $prompt and
     $messages[-1].Role == "assistant" and
     ($messages[-1].Content | contains("Packaged multi-action activity summary passed."))' \
    "$conversations_path" >/dev/null 2>&1; then break; fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during the Linux multi-action smoke.' >&2; exit 1; fi
  sleep 0.1
done

python3 - "$conversations_path" "$conversation_id" "$mock_request_log" "$private_project" <<'PY'
import json
import os
import sys

with open(sys.argv[1], encoding="utf-8") as source:
    conversations = json.load(source)
conversation = next((item for item in conversations if item.get("Id") == sys.argv[2]), None)
if conversation is None:
    raise SystemExit("The Linux activity-summary conversation disappeared.")
messages = conversation.get("Messages", [])
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
    raise SystemExit(f"The packaged Linux multi-action sequence was unexpected: {actual!r}")
if any(request.get("keep_alive") != "30m" for request in turn):
    raise SystemExit(f"A Linux multi-action request omitted keep_alive: {turn!r}")
PY

echo 'Linux packaged Code task read, searched, created a file, and verified a command in one Auto turn.'

# Verify the platform primary-modifier shortcut creates and activates a fresh chat.
previous_conversation_id="$(jq -r '.' "$active_path")"
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+n
for _ in {1..100}; do
  current_conversation_id="$(jq -r '.' "$active_path" 2>/dev/null || true)"
  if [[ -n "$current_conversation_id" && "$current_conversation_id" != "$previous_conversation_id" ]] && \
     jq -e --arg id "$current_conversation_id" '.[] | select(.Id == $id)' "$conversations_path" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$app_pid" 2>/dev/null; then cat "$log_path" >&2; echo 'Packaged Avalonia app exited during the Linux Ctrl+N shortcut smoke.' >&2; exit 1; fi
  sleep 0.1
done
if [[ -z "$current_conversation_id" || "$current_conversation_id" == "$previous_conversation_id" ]] || \
   ! jq -e --arg id "$current_conversation_id" '.[] | select(.Id == $id)' "$conversations_path" >/dev/null 2>&1; then
  cat "$log_path" >&2
  echo 'Linux Ctrl+N did not create and activate a persisted conversation.' >&2
  exit 1
fi
echo 'Linux packaged app used Ctrl+N to create and activate a persisted conversation.'

cat "$log_path" >&2
jq --arg id "$conversation_id" '.[] | select(.Id == $id) | {Model, Messages: .Messages[-4:]}' "$conversations_path" >&2
