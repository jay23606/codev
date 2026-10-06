#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const http = require("node:http");

const MODEL = "codev-smoke:latest";
const REPLY = "Packaged chat round-trip passed.";
const AUTO_COMMAND_PROMPT = "Run the packaged Auto destructive-command smoke.";
const args = process.argv.slice(2);
function option(name) {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error(`Missing ${name} argument.`);
  return args[index + 1];
}
const portFile = option("--port-file");
const requestLog = option("--request-log");
const enabledFile = args.includes("--model-enabled-file") ? option("--model-enabled-file") : null;
const modelEnabled = () => enabledFile === null || fs.readFileSync(enabledFile, "utf8").trim() === "1";

const server = http.createServer((request, response) => {
  if (request.method === "GET" && request.url === "/api/tags") {
    const models = modelEnabled() ? [{ name: MODEL, model: MODEL, size: 1,
      details: { family: "smoke", parameter_size: "0", quantization_level: "Q4_0" } }] : [];
    const body = JSON.stringify({ models });
    response.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
    response.end(body);
    return;
  }
  if (request.method === "GET" && request.url === "/api/ps") {
    const body = JSON.stringify({ models: modelEnabled() ? [{ name: MODEL, model: MODEL, size_vram: 1 }] : [] });
    response.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
    response.end(body);
    return;
  }
  if (request.method === "GET" && request.url === "/api/version") {
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end('{"version":"0.0-smoke"}');
    return;
  }
  if (request.method !== "POST" || request.url !== "/api/chat") {
    response.writeHead(404);
    response.end();
    return;
  }

  const chunks = [];
  request.on("data", chunk => chunks.push(chunk));
  request.on("end", () => {
    let payload;
    try { payload = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
    catch { response.writeHead(400); response.end("invalid JSON"); return; }
    const messages = Array.isArray(payload.messages) ? payload.messages : [];
    const lastMessage = messages.at(-1) ?? {};
    const lastUserMessage = [...messages].reverse().find(message => message.role === "user");
    const tools = Array.isArray(payload.tools) ? payload.tools : [];
    const toolName = tool => {
      const functionSpec = tool?.function ?? tool?.Function;
      return functionSpec?.name ?? functionSpec?.Name ?? tool?.name ?? tool?.Name ?? null;
    };
    const entry = {
      path: request.url,
      model: payload.model,
      stream: payload.stream,
      keep_alive: payload.keep_alive,
      last_role: lastMessage.role ?? null,
      last_tool_name: lastMessage.tool_name ?? null,
      last_user_message: lastUserMessage?.content ?? null,
      tool_names: tools.map(toolName).filter(Boolean),
      tool_shapes: tools.slice(0, 8).map(tool => ({
        keys: tool && typeof tool === "object" ? Object.keys(tool) : [],
        function_keys: tool?.function && typeof tool.function === "object" ? Object.keys(tool.function) : [],
        function_name: toolName(tool),
      })),
    };
    fs.appendFileSync(requestLog, `${JSON.stringify(entry)}\n`, "utf8");
    if (!modelEnabled() || entry.model !== MODEL || ![true, false].includes(entry.stream)) {
      response.writeHead(400);
      response.end("unexpected chat request");
      return;
    }

    const writeJson = body => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(body));
    };
    if (entry.last_user_message === AUTO_COMMAND_PROMPT && entry.last_role === "user") {
      if (!entry.tool_names.includes("run_command")) {
        response.writeHead(400);
        response.end(`Code task request did not expose run_command; tool count=${tools.length}, shapes=${JSON.stringify(entry.tool_shapes)}`);
        return;
      }
      writeJson({ model: MODEL, message: { role: "assistant", content: "", tool_calls: [
        { function: { name: "run_command", arguments: { command: "Remove-Item -Recurse -Force signaling; git --version" } } },
      ] }, done: true });
      return;
    }
    if (entry.last_user_message === AUTO_COMMAND_PROMPT && entry.last_role === "tool") {
      if (entry.last_tool_name !== "run_command") {
        response.writeHead(400);
        response.end("Auto smoke returned an unexpected tool result");
        return;
      }
      writeJson({ model: MODEL, message: { role: "assistant", content: "Packaged Auto destructive command round-trip passed." }, done: true });
      return;
    }
    if (entry.stream !== true) {
      response.writeHead(400);
      response.end("unexpected non-streaming request");
      return;
    }

    const frames = [
      { model: MODEL, message: { role: "assistant", content: "Packaged " }, done: false },
      { model: MODEL, message: { role: "assistant", content: "chat round-trip passed." }, done: false },
      { model: MODEL, message: { role: "assistant", content: "" }, done: true,
        total_duration: 1000000, load_duration: 0, prompt_eval_count: 7, eval_count: 5, eval_duration: 1000000 },
    ];
    response.writeHead(200, { "Content-Type": "application/x-ndjson" });
    let index = 0;
    const writeNextFrame = () => {
      if (index === frames.length) { response.end(); return; }
      response.write(`${JSON.stringify(frames[index++])}\n`);
      setTimeout(writeNextFrame, 25);
    };
    writeNextFrame();
  });
});

console.log(`Mock Ollama fixture starting under Node ${process.version}.`);
server.listen(0, "127.0.0.1", () => {
  fs.writeFileSync(portFile, String(server.address().port), "ascii");
  console.log("Loopback Ollama fixture is ready.");
});
