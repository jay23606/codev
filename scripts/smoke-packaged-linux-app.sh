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
node ./scripts/mock-ollama-server.js --port-file "$mock_port_path" --request-log "$mock_request_log" >"$smoke_root/mock-ollama.log" 2>&1 &
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

# The mock server supplies one local model, so exercise the eligible Code task
# branch before restoring Chat for the plain streaming-chat round-trip.
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode false true 'Linux packaged app switched Plan → local Code task.'
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+shift+m
assert_mode false false 'Linux packaged app switched local Code task → Chat.'

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
chat_prompt='Smoke-test packaged chat on Linux.'
xdotool windowfocus --sync "$window_id"
xdotool key --clearmodifiers ctrl+l
xdotool type --clearmodifiers --delay 1 "$chat_prompt"
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
if len(requests) != 1 or requests[0] != {"path": "/api/chat", "model": "codev-smoke:latest", "stream": True, "keep_alive": "30m"}:
    raise SystemExit(f"Packaged Linux chat request did not match expected model/stream/keep_alive fields: {requests!r}")
PY

echo 'Linux packaged app completed a real composer → streamed Ollama chat → persisted reply round-trip against a loopback mock server.'

cat "$log_path" >&2
jq --arg id "$conversation_id" '.[] | select(.Id == $id) | {Model, Messages: .Messages[-4:]}' "$conversations_path" >&2
