#!/usr/bin/env node
"use strict";

const fs = require("node:fs");

function option(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1]) throw new Error(`Missing ${name} argument.`);
  return process.argv[index + 1];
}

const callLogPath = option("--call-log");
const processLogPath = option("--process-log");
fs.appendFileSync(processLogPath, `${process.pid}\n`, "ascii");

let pending = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", chunk => {
  pending += chunk;
  while (true) {
    const newline = pending.indexOf("\n");
    if (newline < 0) break;
    const line = pending.slice(0, newline).trim();
    pending = pending.slice(newline + 1);
    if (!line) continue;
    let request;
    try { request = JSON.parse(line); }
    catch (error) {
      process.stderr.write(`Invalid JSON-RPC request: ${error.message}\n`);
      process.exitCode = 2;
      continue;
    }
    if (request.method === "notifications/initialized" || request.method?.startsWith("notifications/")) continue;
    if (request.method === "initialize") {
      respond(request.id, {
        protocolVersion: request.params?.protocolVersion ?? "2025-03-26",
        capabilities: {
          tools: { listChanged: false },
          prompts: { listChanged: false },
          resources: { listChanged: false, subscribe: false },
        },
        serverInfo: { name: "codev-packaged-smoke", version: "1.0.0" },
      });
      continue;
    }
    if (request.method === "ping") {
      respond(request.id, {});
      continue;
    }
    if (request.method === "tools/list") {
      const messageSchema = {
        type: "object",
        properties: { message: { type: "string", minLength: 1, maxLength: 100 } },
        required: ["message"],
        additionalProperties: false,
      };
      respond(request.id, { tools: [
        {
          name: "echo",
          description: "Return a deterministic text marker for the packaged Codev smoke.",
          inputSchema: messageSchema,
        },
        {
          name: "deny_me",
          description: "A fixture operation that must be denied before server invocation.",
          inputSchema: messageSchema,
        },
      ] });
      continue;
    }
    if (request.method === "prompts/list") {
      respond(request.id, { prompts: [{ name: "release_review", description: "Review release notes for risks." }] });
      continue;
    }
    if (request.method === "prompts/get") {
      if (request.params?.name !== "release_review") {
        respondError(request.id, -32602, "Unknown prompt.");
        continue;
      }
      fs.appendFileSync(callLogPath, `${JSON.stringify({ operation: "prompt", name: request.params.name })}\n`, "utf8");
      respond(request.id, { description: "Packaged prompt activity fixture.", messages: [
        { role: "user", content: { type: "text", text: "MCP_SMOKE_PROMPT_BODY: release review marker" } },
      ] });
      continue;
    }
    if (request.method === "resources/list") {
      respond(request.id, { resources: [{ uri: "codev://release-notes", name: "release-notes", mimeType: "text/plain" }] });
      continue;
    }
    if (request.method === "resources/read") {
      const uri = request.params?.uri;
      const text = uri === "codev://release-notes"
        ? "MCP_SMOKE_RESOURCE_BODY: release notes marker"
        : uri === "v://i/42" ? "MCP_SMOKE_TEMPLATE_BODY: issue 42 marker" : null;
      if (text === null) {
        respondError(request.id, -32602, "Unknown resource.");
        continue;
      }
      fs.appendFileSync(callLogPath, `${JSON.stringify({ operation: "resource", uri })}\n`, "utf8");
      respond(request.id, { contents: [{ uri, mimeType: "text/plain", text }] });
      continue;
    }
    if (request.method === "resources/templates/list") {
      respond(request.id, { resourceTemplates: [{
        uriTemplate: "v://i/{id}", name: "issue", description: "Read an issue by ID.", mimeType: "text/plain",
      }] });
      continue;
    }
    if (request.method === "tools/call") {
      const message = request.params?.arguments?.message;
      fs.appendFileSync(callLogPath, `${JSON.stringify({ name: request.params?.name, message })}\n`, "utf8");
      respond(request.id, { content: [{ type: "text", text: `MCP_SMOKE_ECHO:${message}` }], isError: false });
      continue;
    }
    respondError(request.id, -32601, `Unsupported MCP method: ${request.method}`);
  }
});

function respond(id, result) {
  process.stdout.write(`${JSON.stringify({ jsonrpc: "2.0", id, result })}\n`);
}

function respondError(id, code, message) {
  process.stdout.write(`${JSON.stringify({ jsonrpc: "2.0", id, error: { code, message } })}\n`);
}
