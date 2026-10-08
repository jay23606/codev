#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const http = require("node:http");

function option(name) {
  const index = process.argv.indexOf(name);
  if (index < 0 || !process.argv[index + 1]) throw new Error(`Missing ${name} argument.`);
  return process.argv[index + 1];
}

const portFile = option("--port-file");
const callLogPath = option("--call-log");
const server = http.createServer((request, response) => {
  if (request.method !== "POST" || new URL(request.url, "http://127.0.0.1").pathname !== "/mcp") {
    response.writeHead(404);
    response.end();
    return;
  }

  const chunks = [];
  request.on("data", chunk => chunks.push(chunk));
  request.on("end", () => {
    let message;
    try { message = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
    catch { response.writeHead(400); response.end("invalid JSON"); return; }

    if (message.method?.startsWith("notifications/")) {
      response.writeHead(202);
      response.end();
      return;
    }

    let result;
    if (message.method === "initialize") {
      result = {
        protocolVersion: message.params?.protocolVersion ?? "2025-03-26",
        capabilities: { tools: { listChanged: false } },
        serverInfo: { name: "codev-packaged-http-smoke", version: "1.0.0" },
      };
    } else if (message.method === "ping") {
      result = {};
    } else if (message.method === "tools/list") {
      result = { tools: [{
        name: "echo",
        description: "Return a deterministic marker from the packaged Streamable HTTP smoke server.",
        inputSchema: {
          type: "object",
          properties: { message: { type: "string", minLength: 1, maxLength: 100 } },
          required: ["message"],
          additionalProperties: false,
        },
      }] };
    } else if (message.method === "tools/call") {
      const name = message.params?.name;
      const text = message.params?.arguments?.message;
      if (name !== "echo" || typeof text !== "string" || text.length < 1 || text.length > 100) {
        sendJson(response, { jsonrpc: "2.0", id: message.id, error: { code: -32602, message: "Invalid echo arguments." } });
        return;
      }
      fs.appendFileSync(callLogPath, `${JSON.stringify({ name, message: text })}\n`, "utf8");
      result = { content: [{ type: "text", text: `MCP_HTTP_SMOKE_ECHO:${text}` }], isError: false };
    } else {
      sendJson(response, { jsonrpc: "2.0", id: message.id, error: { code: -32601, message: `Unsupported method: ${message.method}` } });
      return;
    }

    sendJson(response, { jsonrpc: "2.0", id: message.id, result });
  });
});

server.listen(0, "127.0.0.1", () => {
  const address = server.address();
  fs.writeFileSync(portFile, String(address.port), "ascii");
});

function sendJson(response, message) {
  const body = JSON.stringify(message);
  response.writeHead(200, {
    "Content-Type": "application/json",
    "Content-Length": Buffer.byteLength(body),
  });
  response.end(body);
}
