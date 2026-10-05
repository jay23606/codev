#!/usr/bin/env node
"use strict";

const fs = require("node:fs");
const http = require("node:http");

const MODEL = "codev-smoke:latest";
const REPLY = "Packaged chat round-trip passed.";
const args = process.argv.slice(2);
function option(name) {
  const index = args.indexOf(name);
  if (index < 0 || !args[index + 1]) throw new Error(`Missing ${name} argument.`);
  return args[index + 1];
}
const portFile = option("--port-file");
const requestLog = option("--request-log");

const server = http.createServer((request, response) => {
  if (request.method === "GET" && request.url === "/api/tags") {
    const body = JSON.stringify({ models: [{ name: MODEL, model: MODEL, size: 1,
      details: { family: "smoke", parameter_size: "0", quantization_level: "Q4_0" } }] });
    response.writeHead(200, { "Content-Type": "application/json", "Content-Length": Buffer.byteLength(body) });
    response.end(body);
    return;
  }
  if (request.method === "GET" && request.url === "/api/ps") {
    const body = JSON.stringify({ models: [{ name: MODEL, model: MODEL, size_vram: 1 }] });
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
    const entry = { path: request.url, model: payload.model, stream: payload.stream, keep_alive: payload.keep_alive };
    fs.appendFileSync(requestLog, `${JSON.stringify(entry)}\n`, "utf8");
    if (entry.model !== MODEL || entry.stream !== true) {
      response.writeHead(400);
      response.end("unexpected chat request");
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
