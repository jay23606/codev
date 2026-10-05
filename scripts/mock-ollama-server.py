#!/usr/bin/env python3
"""Tiny loopback-only Ollama fixture for packaged desktop interaction smokes."""

from __future__ import annotations

import sys

print(f"Mock Ollama fixture starting under {sys.version.split()[0]}.", flush=True)

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Lock

MODEL = "codev-smoke:latest"


class Handler(BaseHTTPRequestHandler):
    server_version = "CodevOllamaSmoke/1.0"

    def log_message(self, _format: str, *_args: object) -> None:
        return

    def _json(self, value: object) -> None:
        body = json.dumps(value, separators=(",", ":")).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path == "/api/tags":
            self._json({"models": [{"name": MODEL, "model": MODEL, "size": 1,
                                    "details": {"family": "smoke", "parameter_size": "0",
                                                "quantization_level": "Q4_0"}}]})
        elif self.path == "/api/ps":
            self._json({"models": []})
        elif self.path == "/api/version":
            self._json({"version": "0.0-smoke"})
        else:
            self.send_error(404)

    def do_POST(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler API
        if self.path != "/api/chat":
            self.send_error(404)
            return
        try:
            length = int(self.headers.get("Content-Length", "0"))
            payload = json.loads(self.rfile.read(length))
        except (ValueError, json.JSONDecodeError):
            self.send_error(400, "invalid JSON")
            return
        entry = {"path": self.path, "model": payload.get("model"),
                 "stream": payload.get("stream"), "keep_alive": payload.get("keep_alive")}
        with self.server.log_lock:  # type: ignore[attr-defined]
            with Path(self.server.request_log).open("a", encoding="utf-8") as target:  # type: ignore[attr-defined]
                target.write(json.dumps(entry, separators=(",", ":")) + "\n")
        if entry["model"] != MODEL or entry["stream"] is not True:
            self.send_error(400, "unexpected chat request")
            return
        chunks = [
            {"model": MODEL, "message": {"role": "assistant", "content": "Packaged "}, "done": False},
            {"model": MODEL, "message": {"role": "assistant", "content": "chat round-trip passed."}, "done": False},
            {"model": MODEL, "message": {"role": "assistant", "content": ""}, "done": True,
             "total_duration": 1000000, "load_duration": 0, "prompt_eval_count": 7,
             "eval_count": 5, "eval_duration": 1000000},
        ]
        self.send_response(200)
        self.send_header("Content-Type", "application/x-ndjson")
        self.end_headers()
        for chunk in chunks:
            self.wfile.write((json.dumps(chunk, separators=(",", ":")) + "\n").encode())
            self.wfile.flush()


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port-file", required=True)
    parser.add_argument("--request-log", required=True)
    args = parser.parse_args()
    print("Binding loopback Ollama fixture.", flush=True)
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    server.daemon_threads = True
    server.request_log = args.request_log  # type: ignore[attr-defined]
    server.log_lock = Lock()  # type: ignore[attr-defined]
    print("Writing loopback Ollama fixture port.", flush=True)
    Path(args.port_file).write_text(str(server.server_address[1]), encoding="ascii")
    print("Loopback Ollama fixture is ready.", flush=True)
    server.serve_forever(poll_interval=0.1)


if __name__ == "__main__":
    main()
