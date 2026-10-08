#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const http = require("node:http");

const MODEL = "codev-smoke:latest";
const REPLY = "Packaged chat round-trip passed.";
const args = process.argv.slice(2);
const autoDestructive = args.includes("--auto-destructive");
const activitySummary = args.includes("--activity-summary");
const mcpToolSmoke = args.includes("--mcp-tool");
const mcpHttpSmoke = args.includes("--mcp-http");
const AUTO_COMMAND_PROMPT = autoDestructive
  ? "Run the packaged Auto destructive-command smoke."
  : "Run the packaged Auto mode command smoke.";
const ASK_DENY_PROMPT = "Run the packaged exact-deny smoke in Ask mode.";
const AUTO_DENY_PROMPT = "Run the packaged exact-deny smoke in Auto mode.";
const ACTIVITY_SUMMARY_PROMPT = "Run the packaged multi-action activity-summary smoke.";
const AUTO_DESTRUCTIVE_COMMAND = "Remove-Item -Recurse -Force space-invaders-game/signaling; git -C space-invaders-game status --short";
const MCP_TOOL_PROMPT = "Run the packaged MCP tool smoke.";
const MCP_TOOL_NAME_PREFIX = "mcp_smoke-mcp_echo_";
const MCP_TOOL_MESSAGE = "packaged MCP marker";
const MCP_ASK_DENY_PROMPT = "Run the packaged MCP Ask-denial smoke.";
const MCP_DENY_TOOL_NAME_PREFIX = "mcp_smoke-mcp_deny_me_";
const MCP_DENY_TOOL_MESSAGE = "must not reach the server";
const MCP_HTTP_PROMPT = "Run the packaged MCP Streamable HTTP smoke.";
const MCP_HTTP_TOOL_NAME_PREFIX = "mcp_smoke-http_echo_";
const MCP_HTTP_TOOL_MESSAGE = "packaged HTTP marker";
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
      last_content: lastMessage.content ?? null,
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
    const callTool = (name, args) => writeJson({ model: MODEL, message: { role: "assistant", content: "", tool_calls: [
      { function: { name, arguments: args } },
    ] }, done: true });
    if (mcpToolSmoke && entry.last_user_message === MCP_ASK_DENY_PROMPT && entry.last_role === "user") {
      const mcpToolName = entry.tool_names.find(name => name.startsWith(MCP_DENY_TOOL_NAME_PREFIX));
      if (!mcpToolName) {
        response.writeHead(400);
        response.end(`The packaged MCP Ask-denial smoke did not discover its deny_me tool: ${JSON.stringify(entry.tool_names)}`);
        return;
      }
      callTool(mcpToolName, { message: MCP_DENY_TOOL_MESSAGE });
      return;
    }
    if (mcpToolSmoke && entry.last_user_message === MCP_ASK_DENY_PROMPT && entry.last_role === "tool") {
      if (!entry.last_tool_name?.startsWith(MCP_DENY_TOOL_NAME_PREFIX)) {
        response.writeHead(400);
        response.end(`The packaged MCP Ask-denial smoke returned an unexpected tool: ${entry.last_tool_name}`);
        return;
      }
      let toolOutput;
      try { toolOutput = JSON.parse(entry.last_content); }
      catch { toolOutput = entry.last_content; }
      const expectedDenial = "Denied by a saved project MCP tool permission rule; the tool was not called.";
      const denialWasReturned = typeof toolOutput === "string"
        ? toolOutput === expectedDenial
        : toolOutput?.type === "untrusted_tool_output" && toolOutput.source === "MCP tool output" &&
          toolOutput.activity === "mcp_tool" && toolOutput.content.includes(expectedDenial);
      if (!denialWasReturned) {
        response.writeHead(400);
        response.end(`The packaged MCP call was not denied before server invocation: ${JSON.stringify(toolOutput)}`);
        return;
      }
      writeJson({ model: MODEL, message: { role: "assistant", content: "Packaged MCP Ask denial passed." }, done: true });
      return;
    }
    if (mcpToolSmoke && entry.last_user_message === MCP_TOOL_PROMPT && entry.last_role === "user") {
      const mcpToolName = entry.tool_names.find(name => name.startsWith(MCP_TOOL_NAME_PREFIX));
      if (!mcpToolName) {
        response.writeHead(400);
        response.end(`The packaged MCP smoke did not discover its echo tool: ${JSON.stringify(entry.tool_names)}`);
        return;
      }
      callTool(mcpToolName, { message: MCP_TOOL_MESSAGE });
      return;
    }
    if (mcpToolSmoke && entry.last_user_message === MCP_TOOL_PROMPT && entry.last_role === "tool") {
      if (!entry.last_tool_name?.startsWith(MCP_TOOL_NAME_PREFIX)) {
        response.writeHead(400);
        response.end(`The packaged MCP smoke returned an unexpected tool: ${entry.last_tool_name}`);
        return;
      }
      let toolOutput;
      try { toolOutput = JSON.parse(entry.last_content); }
      catch { response.writeHead(400); response.end("The packaged MCP result was malformed."); return; }
      if (toolOutput?.type !== "untrusted_tool_output" || toolOutput.source !== "MCP tool output" ||
          toolOutput.activity !== "mcp_tool" || !toolOutput.content.includes(`MCP_SMOKE_ECHO:${MCP_TOOL_MESSAGE}`)) {
        response.writeHead(400);
        response.end(`The packaged MCP result was not bounded untrusted tool output: ${JSON.stringify(toolOutput)}`);
        return;
      }
      writeJson({ model: MODEL, message: { role: "assistant", content: "Packaged MCP tool call passed." }, done: true });
      return;
    }
    if (mcpHttpSmoke && entry.last_user_message === MCP_HTTP_PROMPT && entry.last_role === "user") {
      const mcpToolName = entry.tool_names.find(name => name.startsWith(MCP_HTTP_TOOL_NAME_PREFIX));
      if (!mcpToolName) {
        response.writeHead(400);
        response.end(`The packaged MCP Streamable HTTP smoke did not discover its echo tool: ${JSON.stringify(entry.tool_names)}`);
        return;
      }
      callTool(mcpToolName, { message: MCP_HTTP_TOOL_MESSAGE });
      return;
    }
    if (mcpHttpSmoke && entry.last_user_message === MCP_HTTP_PROMPT && entry.last_role === "tool") {
      if (!entry.last_tool_name?.startsWith(MCP_HTTP_TOOL_NAME_PREFIX)) {
        response.writeHead(400);
        response.end(`The packaged Streamable HTTP MCP smoke returned an unexpected tool: ${entry.last_tool_name}`);
        return;
      }
      let toolOutput;
      try { toolOutput = JSON.parse(entry.last_content); }
      catch { response.writeHead(400); response.end("The packaged Streamable HTTP MCP result was malformed."); return; }
      if (toolOutput?.type !== "untrusted_tool_output" || toolOutput.source !== "MCP tool output" ||
          toolOutput.activity !== "mcp_tool" || !toolOutput.content.includes(`MCP_HTTP_SMOKE_ECHO:${MCP_HTTP_TOOL_MESSAGE}`)) {
        response.writeHead(400);
        response.end(`The packaged Streamable HTTP MCP result was not bounded untrusted tool output: ${JSON.stringify(toolOutput)}`);
        return;
      }
      writeJson({ model: MODEL, message: { role: "assistant", content: "Packaged MCP Streamable HTTP call passed." }, done: true });
      return;
    }
    if (activitySummary && entry.last_user_message === ACTIVITY_SUMMARY_PROMPT && entry.last_role === "user") {
      if (!entry.tool_names.includes("read_file")) {
        response.writeHead(400);
        response.end("The activity-summary smoke did not expose read_file");
        return;
      }
      callTool("read_file", { relative_path: "activity-source.txt" });
      return;
    }
    if (activitySummary && entry.last_user_message === ACTIVITY_SUMMARY_PROMPT && entry.last_role === "tool") {
      const nextTool = {
        read_file: ["search_files", { query: "ACTIVITY_SOURCE_MARKER" }],
        search_files: ["create_file", { relative_path: "activity-result.txt", content: "ACTIVITY_SOURCE_MARKER" }],
        create_file: ["verify_command", { command: "node --version" }],
      }[entry.last_tool_name];
      if (nextTool) {
        if (!entry.tool_names.includes(nextTool[0])) {
          response.writeHead(400);
          response.end(`The activity-summary smoke did not expose ${nextTool[0]}`);
          return;
        }
        callTool(nextTool[0], nextTool[1]);
        return;
      }
      if (entry.last_tool_name === "verify_command") {
        writeJson({ model: MODEL, message: { role: "assistant", content: "Packaged multi-action activity summary passed." }, done: true });
        return;
      }
      response.writeHead(400);
      response.end(`Unexpected activity-summary tool result: ${entry.last_tool_name}`);
      return;
    }
    if (entry.last_user_message === AUTO_COMMAND_PROMPT && entry.last_role === "user") {
      const autoCommandTool = autoDestructive ? "run_command" : "verify_command";
      if (!entry.tool_names.includes(autoCommandTool)) {
        response.writeHead(400);
        response.end(`Code task request did not expose ${autoCommandTool}; tool count=${tools.length}, shapes=${JSON.stringify(entry.tool_shapes)}`);
        return;
      }
      writeJson({ model: MODEL, message: { role: "assistant", content: "", tool_calls: [
        { function: { name: autoCommandTool, arguments: { command: autoDestructive
          ? AUTO_DESTRUCTIVE_COMMAND
          : "node --version" } } },
      ] }, done: true });
      return;
    }
    if (entry.last_user_message === AUTO_COMMAND_PROMPT && entry.last_role === "tool") {
      const autoCommandTool = autoDestructive ? "run_command" : "verify_command";
      if (entry.last_tool_name !== autoCommandTool) {
        response.writeHead(400);
        response.end("Auto smoke returned an unexpected tool result");
        return;
      }
      if (autoDestructive) {
        let toolOutput;
        try { toolOutput = JSON.parse(entry.last_content); }
        catch { response.writeHead(400); response.end("Auto smoke returned malformed command output"); return; }
        if (toolOutput?.type !== "untrusted_tool_output" ||
            toolOutput.command !== AUTO_DESTRUCTIVE_COMMAND ||
            !toolOutput.content.includes("Exit code: 0") ||
            !toolOutput.content.includes("signaling/smoke-marker.txt")) {
          response.writeHead(400);
          response.end(`Auto smoke did not return the expected successful Git deletion status: ${JSON.stringify(toolOutput)}`);
          return;
        }
      }
      const autoReply = autoDestructive
        ? "Packaged Auto destructive command round-trip passed."
        : "Packaged Auto command round-trip passed.";
      writeJson({ model: MODEL, message: { role: "assistant", content: autoReply }, done: true });
      return;
    }
    if (autoDestructive &&
        (entry.last_user_message === ASK_DENY_PROMPT || entry.last_user_message === AUTO_DENY_PROMPT) &&
        entry.last_role === "user") {
      if (!entry.tool_names.includes("run_command")) {
        response.writeHead(400);
        response.end("The Auto exact-deny smoke did not expose run_command");
        return;
      }
      callTool("run_command", { command: AUTO_DESTRUCTIVE_COMMAND });
      return;
    }
    if (autoDestructive &&
        (entry.last_user_message === ASK_DENY_PROMPT || entry.last_user_message === AUTO_DENY_PROMPT) &&
        entry.last_role === "tool") {
      if (entry.last_tool_name !== "run_command") {
        response.writeHead(400);
        response.end("Auto exact-deny smoke returned an unexpected tool result");
        return;
      }
      const result = entry.last_user_message === ASK_DENY_PROMPT
        ? "Packaged Ask exact-deny command passed."
        : "Packaged Auto exact-deny command passed.";
      writeJson({ model: MODEL, message: { role: "assistant", content: result }, done: true });
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
