#!/usr/bin/env node
"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const { spawn } = require("node:child_process");

const root = fs.mkdtempSync(path.join(os.tmpdir(), "codev-mock-ollama-activity-"));
const portFile = path.join(root, "port.txt");
const requestLog = path.join(root, "requests.jsonl");
const serverScript = path.join(__dirname, "mock-ollama-server.js");
const prompt = "Run the packaged multi-action activity-summary smoke.";
const expectedTools = ["read_file", "search_files", "create_file", "verify_command"];
const expectedArguments = [
  { relative_path: "activity-source.txt" },
  { query: "ACTIVITY_SOURCE_MARKER" },
  { relative_path: "activity-result.txt", content: "ACTIVITY_SOURCE_MARKER" },
  { command: "cargo check --manifest-path space-invaders-game/signaling/Cargo.toml" },
];

function post(port, payload) {
  return new Promise((resolve, reject) => {
    const request = http.request({ hostname: "127.0.0.1", port, path: "/api/chat", method: "POST",
      headers: { "content-type": "application/json" } }, response => {
      const chunks = [];
      response.on("data", chunk => chunks.push(chunk));
      response.on("end", () => {
        const body = Buffer.concat(chunks).toString("utf8");
        if (response.statusCode !== 200) return reject(new Error(`Mock returned ${response.statusCode}: ${body}`));
        try { resolve(JSON.parse(body)); } catch (error) { reject(error); }
      });
    });
    request.on("error", reject);
    request.end(JSON.stringify(payload));
  });
}

async function main() {
  const server = spawn(process.execPath, [serverScript, "--activity-summary", "--port-file", portFile,
    "--request-log", requestLog], { stdio: "ignore", windowsHide: true });
  try {
    const deadline = Date.now() + 10_000;
    while (!fs.existsSync(portFile) && Date.now() < deadline) {
      if (server.exitCode !== null) throw new Error(`Mock server exited with ${server.exitCode}.`);
      await new Promise(resolve => setTimeout(resolve, 25));
    }
    assert.ok(fs.existsSync(portFile), "Mock server did not publish its loopback port.");
    const port = Number(fs.readFileSync(portFile, "utf8"));
    const tools = expectedTools.map(name => ({ function: { name } }));
    const messages = [{ role: "user", content: prompt }];
    const requests = [];
    for (let round = 0; round <= expectedTools.length; round++) {
      const payload = { model: "codev-smoke:latest", stream: false, keep_alive: "30m", messages, tools };
      requests.push(await post(port, payload));
      if (round === expectedTools.length) {
        assert.equal(requests[round].message.content, "Packaged multi-action activity summary passed.");
        break;
      }
      const call = requests[round].message.tool_calls?.[0];
      assert.ok(call, `Mock response ${round} did not include a tool call.`);
      assert.equal(call.function.name, expectedTools[round]);
      assert.deepEqual(call.function.arguments, expectedArguments[round]);
      messages.push({ role: "assistant", content: "", tool_calls: [call] });
      messages.push({ role: "tool", tool_name: expectedTools[round], content: "fixture result" });
    }
    const entries = fs.readFileSync(requestLog, "utf8").trim().split(/\r?\n/).map(line => JSON.parse(line));
    assert.equal(entries.length, 5);
    assert.ok(entries.every(entry => entry.keep_alive === "30m"));
    assert.deepEqual(entries.map(entry => entry.last_tool_name), [null, ...expectedTools]);
    process.stdout.write("Mock activity sequence passed: read, search, create, verify.\n");
  } finally {
    if (server.exitCode === null) {
      server.kill();
      await new Promise(resolve => server.once("exit", resolve));
    }
    fs.rmSync(root, { recursive: true, force: true });
  }
}

main().catch(error => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
