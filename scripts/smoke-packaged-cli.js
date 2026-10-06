#!/usr/bin/env node
"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const os = require("node:os");
const path = require("node:path");
const { spawn } = require("node:child_process");

const executable = process.argv[2];
if (!executable) throw new Error("Usage: node scripts/smoke-packaged-cli.js <Codev.Cli executable>");
const cliPath = path.resolve(executable);
assert.ok(fs.existsSync(cliPath), `Packaged CLI not found: ${cliPath}`);

// macOS exposes /var as a symlink to /private/var. Resolve the temporary
// directory first because Codev deliberately rejects workspace paths that
// traverse symbolic links.
const root = fs.mkdtempSync(path.join(fs.realpathSync(os.tmpdir()), "codev-cli-provider-smoke-"));
const workspace = path.join(root, "workspace");
const fixturePath = path.join(workspace, "smoke.txt");
const fixture = "CLI_PROVIDER_ROUND_TRIP_7E31A4\n";
const prompt = "Read smoke.txt and report its exact marker.";
const finalAnswer = `Provider request round trip passed: ${fixture.trim()}`;
fs.mkdirSync(workspace);
fs.writeFileSync(fixturePath, fixture, "utf8");

let requestCount = 0;
let protocolError = null;
const server = http.createServer((request, response) => {
  const chunks = [];
  request.on("data", chunk => chunks.push(chunk));
  request.on("end", () => {
    try {
      assert.equal(request.method, "POST");
      assert.equal(request.url, "/api/chat");
      const payload = JSON.parse(Buffer.concat(chunks).toString("utf8"));
      requestCount++;
      assert.equal(payload.model, "codev-cli-smoke:latest");
      assert.equal(payload.stream, false);
      assert.equal(payload.keep_alive, "30m");
      const tools = payload.tools.map(tool => tool.function.name);
      assert.deepEqual(tools, ["list_files", "read_file", "search_files"]);
      assert.ok(!tools.some(name => /write|create|patch|command|exec/i.test(name)),
        "Default CLI schema exposed a mutation or command tool.");

      const userMessage = payload.messages.find(message => message.role === "user");
      assert.equal(userMessage?.content, prompt);
      const contentMessages = payload.messages.filter(message => message.role === "tool");
      if (requestCount === 1) {
        assert.equal(contentMessages.length, 0);
        response.writeHead(200, { "content-type": "application/json" });
        response.end(JSON.stringify({
          model: payload.model,
          message: {
            role: "assistant",
            content: "",
            tool_calls: [{ function: { name: "read_file", arguments: { relative_path: "smoke.txt" } } }]
          },
          done: true
        }));
        return;
      }

      assert.equal(requestCount, 2, "CLI sent an unexpected number of provider requests.");
      assert.equal(contentMessages.length, 1);
      assert.ok(contentMessages[0].content.includes(fixture.trim()),
        "The read-only file tool result did not return the fixture marker to the provider.");
      response.writeHead(200, { "content-type": "application/json" });
      response.end(JSON.stringify({ model: payload.model, message: { role: "assistant", content: finalAnswer }, done: true }));
    } catch (error) {
      protocolError = error;
      response.writeHead(500, { "content-type": "application/json" });
      response.end(JSON.stringify({ error: error.message }));
    }
  });
});

function runCli(endpoint) {
  return new Promise((resolve, reject) => {
    const child = spawn(cliPath, ["--provider", "ollama", "--endpoint", endpoint,
      "--model", "codev-cli-smoke:latest", "--workspace", workspace, "-p", prompt], {
      cwd: workspace,
      windowsHide: true,
      stdio: ["ignore", "pipe", "pipe"]
    });
    let stdout = "";
    let stderr = "";
    const timeout = setTimeout(() => child.kill(), 45_000);
    child.stdout.setEncoding("utf8").on("data", chunk => { stdout += chunk; });
    child.stderr.setEncoding("utf8").on("data", chunk => { stderr += chunk; });
    child.once("error", error => {
      clearTimeout(timeout);
      reject(error);
    });
    child.once("close", (code, signal) => {
      clearTimeout(timeout);
      resolve({ code, signal, stdout, stderr });
    });
  });
}

async function main() {
  try {
    await new Promise((resolve, reject) => {
      server.once("error", reject);
      server.listen(0, "127.0.0.1", resolve);
    });
    const { port } = server.address();
    const result = await runCli(`http://127.0.0.1:${port}/`);
    if (protocolError) throw protocolError;
    assert.equal(result.code, 0, `Packaged CLI failed (${result.code ?? result.signal}).\n${result.stderr}\n${result.stdout}`);
    assert.equal(requestCount, 2, "CLI did not complete exactly one provider/tool/provider round trip.");
    assert.ok(result.stdout.includes(finalAnswer), `CLI did not print the provider's final answer.\n${result.stdout}`);
    assert.equal(fs.readFileSync(fixturePath, "utf8"), fixture, "Read-only CLI smoke changed the fixture.");
    assert.deepEqual(fs.readdirSync(workspace), ["smoke.txt"], "Read-only CLI smoke created unexpected workspace files.");
    process.stdout.write("Packaged CLI provider smoke passed: loopback request, read_file result, read-only tool schema, 30m keep_alive, and final response.\n");
  } finally {
    await new Promise(resolve => server.close(() => resolve()));
    fs.rmSync(root, { recursive: true, force: true });
  }
}

main().catch(error => {
  process.stderr.write(`${error.stack || error}\n`);
  process.exitCode = 1;
});
