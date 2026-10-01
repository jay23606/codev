// Compare Codev's literal project search with local Ollama embedding retrieval.
// No project content leaves this process: both /api/embed and /api/chat must use
// a loopback Ollama endpoint. Embedding models are never pulled automatically.
//
// Usage:
//   node bench/a9-retrieval.cjs --selftest
//   node bench/a9-retrieval.cjs --model qwen3.6:35b-a3b --embedding nomic-embed-text --runs 3
//   node bench/a9-retrieval.cjs --model qwen3.8:27b --embedding nomic-embed-text --out bench/results/a9-qwen38.jsonl

'use strict'

const assert = require('node:assert/strict')
const fs = require('node:fs/promises')
const path = require('node:path')
const { spawnSync } = require('node:child_process')

const HOST = process.env.BENCH_OLLAMA_URL || 'http://127.0.0.1:11434'
const ROOT = path.resolve(__dirname, '..')
const CHUNK_CHARS = 1800
const CHUNK_OVERLAP = 240
const TOP_K = 8
const MAX_FILES = 8000
const MAX_BYTES = 80 * 1024 * 1024
const MAX_CHUNKS = 40000
const IGNORED_DIRS = new Set(['.git', '.vs', '.idea', 'bin', 'obj', 'node_modules', 'packages', 'dist', 'build', 'coverage'])
const EXTENSIONS = new Set(['.cs', '.xaml', '.csproj', '.sln', '.cshtml', '.razor', '.js', '.jsx', '.ts', '.tsx', '.html', '.css', '.scss', '.md', '.mdx', '.txt', '.json', '.xml', '.yml', '.yaml', '.toml', '.ini', '.cfg', '.conf', '.py', '.go', '.rs', '.java', '.kt', '.swift', '.c', '.h', '.cpp', '.hpp', '.php', '.rb', '.lua', '.sql', '.proto', '.graphql', '.tf', '.ps1', '.sh'])
const TASKS = [
  { id: 'workspace-boundaries', question: 'Which component validates project-relative paths and prevents traversal or symbolic-link access when tools read files?', targets: ['Codev.Core/WorkspaceFileService.cs'], literalProbe: 'symbolic links' },
  { id: 'auto-command-policy', question: 'Where does Auto mode resolve command approval, and how do saved exact deny rules take precedence?', targets: ['Codev.Core/ProjectCommandPermissionRegistry.cs', 'Codev.Core/ProjectCommandApprovalPolicy.cs'], literalProbe: 'Auto is an explicit trust decision' },
  { id: 'mcp-surface', question: 'Which component discovers MCP tools, prompts, resources and resource templates for Code tasks?', targets: ['Codev.Core/McpCodeTaskSession.cs'], literalProbe: 'resource template' },
  { id: 'agent-profiles', question: 'Where are reusable agent profiles parsed and their tool permissions evaluated?', targets: ['Codev.Core/AgentProfileCatalog.cs'], literalProbe: 'AgentProfilePolicy' },
  { id: 'parallel-worktrees', question: 'Which component creates isolated Git worktrees for parallel child sessions and manages their integration?', targets: ['Codev.Core/GitChildWorktreeManager.cs'], literalProbe: 'git worktree' },
  { id: 'strict-tool-schema', question: 'Where are function schemas converted to OpenAI strict mode, including optional argument handling?', targets: ['Codev.Core/OpenAiStrictFunctionToolAdapter.cs'], literalProbe: 'additionalProperties' }
]

const option = (name, fallback) => {
  const index = process.argv.indexOf(`--${name}`)
  return index < 0 ? fallback : process.argv[index + 1]
}
const loopback = value => {
  const url = new URL(value)
  return !url.username && !url.password && ['localhost', '127.0.0.1', '[::1]'].includes(url.hostname) && ['http:', 'https:'].includes(url.protocol)
}
const api = route => new URL(route, HOST.endsWith('/') ? HOST : HOST + '/')

function chunkText(text) {
  const chunks = []
  let start = 0
  while (start < text.length) {
    let end = Math.min(text.length, start + CHUNK_CHARS)
    if (end < text.length) {
      const boundary = text.lastIndexOf('\n', end - 1)
      if (boundary > start + CHUNK_CHARS / 2) end = boundary
    }
    const value = text.slice(start, end).trim()
    if (value) chunks.push({ content: value, start, end })
    if (end === text.length) break
    start = Math.max(start + 1, end - CHUNK_OVERLAP)
  }
  return chunks
}

function cosine(a, b) {
  if (!Array.isArray(a) || !Array.isArray(b) || a.length !== b.length || a.length === 0) return -Infinity
  let dot = 0, an = 0, bn = 0
  for (let i = 0; i < a.length; i++) { dot += a[i] * b[i]; an += a[i] * a[i]; bn += b[i] * b[i] }
  return an && bn ? dot / Math.sqrt(an * bn) : -Infinity
}

function literalSearch(files, query, limit = 50) {
  if (!query) return []
  const matches = []
  const needle = query.toLowerCase()
  for (const file of files) {
    let offset = 0
    const lines = file.content.split(/\r\n|\r|\n/)
    for (let index = 0; index < lines.length; index++) {
      if (matches.length >= limit) return matches
      const line = lines[index]
      if (line.toLowerCase().includes(needle)) {
        const chunk = file.ranges.findIndex(range => offset >= range.start && offset <= range.end)
        const trimmed = line.trim()
        const excerpt = trimmed.length > 320 ? `${trimmed.slice(0, 320)}…` : trimmed
        matches.push({ relativePath: file.relativePath, chunk: Math.max(0, chunk), content: excerpt, line: index + 1 })
        if (matches.length >= limit) return matches
      }
      offset += line.length
      if (index < lines.length - 1) {
        if (file.content.startsWith('\r\n', offset)) offset += 2
        else if (file.content[offset] === '\r' || file.content[offset] === '\n') offset++
      }
    }
  }
  return matches
}

function semanticSearch(chunks, vector, limit = TOP_K) {
  return chunks.map(chunk => ({ ...chunk, score: cosine(vector, chunk.embedding) }))
    .filter(item => Number.isFinite(item.score))
    .sort((a, b) => b.score - a.score || a.relativePath.localeCompare(b.relativePath))
    .slice(0, limit)
}

function combineResults(left, right, limit = TOP_K) {
  const out = [], seen = new Set()
  for (const item of [...left, ...right]) {
    const key = `${item.relativePath}\0${item.chunk}`
    if (!seen.has(key)) { seen.add(key); out.push(item) }
    if (out.length >= limit) break
  }
  return out
}

function safeName(name) {
  const lower = name.toLowerCase()
  return lower !== '.env' && !lower.startsWith('.env.') &&
    !['secret', 'credential'].some(part => lower.includes(part)) &&
    !['.pem', '.pfx', '.key'].some(ext => lower.endsWith(ext)) &&
    !['id_rsa', 'id_ed25519'].includes(lower)
}

async function readCorpus(root) {
  const rootInfo = await fs.lstat(root)
  if (!rootInfo.isDirectory() || rootInfo.isSymbolicLink()) throw new Error('Source root must be a real directory, not a symbolic link.')
  const chunks = [], files = []
  let fileCount = 0, byteCount = 0
  async function visit(relativeDir) {
    const fullDir = path.join(root, relativeDir)
    const entries = await fs.readdir(fullDir, { withFileTypes: true })
    entries.sort((a, b) => a.name.localeCompare(b.name))
    for (const entry of entries) {
      if (entry.isSymbolicLink() || entry.name.startsWith('.') || !safeName(entry.name)) continue
      const relative = relativeDir ? `${relativeDir}/${entry.name}` : entry.name
      if (entry.isDirectory()) {
        if (!IGNORED_DIRS.has(entry.name.toLowerCase())) await visit(relative)
        continue
      }
      if (!entry.isFile() || !EXTENSIONS.has(path.extname(entry.name).toLowerCase())) continue
      fileCount++
      if (fileCount > MAX_FILES) throw new Error(`Source corpus exceeds the ${MAX_FILES} file limit.`)
      const full = path.join(root, relative)
      const info = await fs.stat(full)
      if (info.size > 500000) continue
      byteCount += info.size
      if (byteCount > MAX_BYTES) throw new Error(`Source corpus exceeds the ${MAX_BYTES} byte limit.`)
      const content = await fs.readFile(full, 'utf8')
      if (content.includes('\0')) continue
      const pieces = chunkText(content)
      files.push({ relativePath: relative, content, ranges: pieces.map(({ start, end }) => ({ start, end })) })
      for (let ordinal = 0; ordinal < pieces.length; ordinal++) {
        chunks.push({ relativePath: relative, chunk: ordinal, content: pieces[ordinal].content, embedding: null })
        if (chunks.length > MAX_CHUNKS) throw new Error(`Source corpus exceeds the ${MAX_CHUNKS} chunk limit.`)
      }
    }
  }
  await visit('')
  return { chunks, files, fileCount, byteCount }
}

async function postJson(route, payload) {
  const response = await fetch(api(route), { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(payload) })
  const body = await response.text()
  if (!response.ok) throw new Error(`${route} returned HTTP ${response.status}: ${body.slice(0, 1000)}`)
  return JSON.parse(body)
}

async function embedBatch(model, input) {
  const body = await postJson('/api/embed', { model, input, truncate: false })
  if (!Array.isArray(body.embeddings) || body.embeddings.length !== input.length || !body.embeddings.every(Array.isArray))
    throw new Error('Ollama returned an invalid embeddings array. Install a local embedding model; this script does not pull models.')
  const dims = body.embeddings[0]?.length
  if (!dims || body.embeddings.some(vector => vector.length !== dims)) throw new Error('Ollama returned empty or inconsistent embedding dimensions.')
  return body.embeddings
}

async function buildIndex(chunks, model) {
  const start = performance.now()
  for (let offset = 0; offset < chunks.length; offset += 32) {
    const batch = chunks.slice(offset, offset + 32)
    const vectors = await embedBatch(model, batch.map(item => `File: ${item.relativePath}\n\n${item.content}`))
    batch.forEach((item, index) => { item.embedding = vectors[index] })
    process.stderr.write(`\rEmbedded ${Math.min(offset + batch.length, chunks.length)}/${chunks.length} chunks`)
  }
  process.stderr.write('\n')
  return Math.round(performance.now() - start)
}

function relevant(item, task) { return task.targets.includes(item.relativePath) }
function resultMetrics(results, task) {
  const targetMatches = results.filter(item => relevant(item, task)).length
  return { targetInTopK: targetMatches > 0, targetChunks: targetMatches, returnedChunks: results.length, irrelevantChunks: results.length - targetMatches }
}

function schemas(mode) {
  const literal = { type: 'function', function: { name: 'search_files', description: 'Find literal text in project source files. Query is a case-insensitive exact substring.', parameters: { type: 'object', properties: { query: { type: 'string' } }, required: ['query'] } } }
  const semantic = { type: 'function', function: { name: 'semantic_search', description: 'Find conceptually related project source chunks using a local embeddings index.', parameters: { type: 'object', properties: { query: { type: 'string' } }, required: ['query'] } } }
  return mode === 'literal-only' ? [literal] : mode === 'semantic-only' ? [semantic] : [literal, semantic]
}

function formatResults(results) {
  if (!results.length) return 'No matches.'
  return results.map(item => `${item.relativePath}${Number.isInteger(item.line) ? `:${item.line}` : ` [chunk ${item.chunk + 1}]`}\n${item.content}`).join('\n\n').slice(0, 8000)
}

async function runModelTask(model, mode, task, chunks, files, embeddingsModel, run) {
  const allowed = new Set(schemas(mode).map(tool => tool.function.name))
  const messages = [
    { role: 'system', content: 'You are locating the implementation in a source repository. You receive no project files except through search tools. Use the available tools, then answer with the exact relative path(s) and a concise explanation. Do not guess.' },
    { role: 'user', content: task.question }
  ]
  let calls = 0, valid = true, returned = [], finalText = '', start = performance.now()
  for (let round = 0; round < 5; round++) {
    const body = await postJson('/api/chat', {
      model, stream: false, think: false, tools: schemas(mode), messages,
      options: { temperature: 0, seed: 42, num_ctx: 16384, num_predict: 1200 }
    })
    const message = body.message
    if (!message) throw new Error('Ollama returned no chat message.')
    finalText = message.content || ''
    if (!Array.isArray(message.tool_calls) || message.tool_calls.length === 0) break
    messages.push(message)
    for (const call of message.tool_calls) {
      calls++
      const name = call.function?.name
      let args
      try { args = typeof call.function?.arguments === 'string' ? JSON.parse(call.function.arguments) : call.function?.arguments } catch { args = null }
      if (!allowed.has(name) || !args || typeof args.query !== 'string' || args.query.length < 1 || args.query.length > 1000) {
        valid = false
        messages.push({ role: 'tool', tool_name: name || 'invalid', content: 'Invalid tool name or query. Use an available search tool with a 1–1,000 character query.' })
        continue
      }
      const results = name === 'search_files'
        ? literalSearch(files, args.query, 50)
        : semanticSearch(chunks, await embedBatch(embeddingsModel, [args.query]).then(value => value[0]))
      returned.push(...results.map(item => ({ ...item, sourceTool: name })))
      messages.push({ role: 'tool', tool_name: name, content: formatResults(results) })
    }
  }
  const elapsedMs = Math.round(performance.now() - start)
  const passed = task.targets.some(target => finalText.replaceAll('\\', '/').includes(target))
  const relevantCount = returned.filter(item => relevant(item, task)).length
  return {
    run, task: task.id, mode, model, toolCalls: calls, toolCallsValid: valid && calls > 0,
    taskPassed: passed, returnedChunks: returned.length, relevantChunks: relevantCount,
    irrelevantChunks: returned.length - relevantCount,
    finalText: finalText.slice(0, 3000), elapsedMs
  }
}

async function selftest() {
  const sample = 'class FileIndex {\n  // validates symbolic links and workspace paths\n  void Read() {}\n}'
  const chunked = chunkText(sample)
  assert.equal(chunked.length, 1)
  const files = [{ relativePath: 'WorkspaceFileService.cs', content: sample, ranges: [{ start: 0, end: sample.length }] }]
  const rows = [
    { relativePath: 'WorkspaceFileService.cs', chunk: 0, content: sample, embedding: [1, 0] },
    { relativePath: 'Other.cs', chunk: 0, content: 'unrelated text', embedding: [0, 1] }
  ]
  const literal = literalSearch(files, 'symbolic links')
  assert.equal(literal[0].relativePath, 'WorkspaceFileService.cs')
  assert.equal(literal[0].line, 2)
  assert.equal(literalSearch(files, 'SYMBOLIC LINKS')[0].line, 2)
  assert.equal(literalSearch(files, 'missing').length, 0)
  const crlfContent = `head\r\n${'x'.repeat(CHUNK_CHARS)}\r\ntarget after a long CRLF line`
  const crlfChunks = chunkText(crlfContent)
  const crlf = literalSearch([{ relativePath: 'Crlf.cs', content: crlfContent, ranges: crlfChunks.map(({ start, end }) => ({ start, end })) }], 'target after')
  assert.equal(crlf[0].chunk, crlfChunks.findIndex(chunk => crlfContent.indexOf('target after') >= chunk.start && crlfContent.indexOf('target after') <= chunk.end))
  const capped = literalSearch([{ relativePath: 'Many.cs', content: Array(60).fill('needle').join('\n'), ranges: [{ start: 0, end: 1000 }] }], 'needle', 50)
  assert.equal(capped.length, 50)
  assert.equal(capped[49].line, 50)
  assert.equal(formatResults(literal).startsWith('WorkspaceFileService.cs:2\n'), true)
  assert.equal(semanticSearch(rows, [0.9, 0.1])[0].relativePath, 'WorkspaceFileService.cs')
  const combined = combineResults(literal, semanticSearch(rows, [0.9, 0.1]))
  assert.equal(combined.length, 2)
  assert.equal(combined.filter(item => item.relativePath === 'WorkspaceFileService.cs').length, 1)
  assert.equal(cosine([1, 0], [0, 1]), 0)
  assert(safeName('WorkspaceFileService.cs'))
  assert(!safeName('.env.local'))
  assert(!safeName('credentials.json'))
  assert(loopback('http://127.0.0.1:11434'))
  assert(loopback('http://localhost:11434'))
  assert(!loopback('http://192.168.1.50:11434'))
  assert(!loopback('http://user:pass@127.0.0.1:11434'))
  assert.deepEqual(resultMetrics([rows[0], rows[1]], { targets: ['WorkspaceFileService.cs'] }), {
    targetInTopK: true, targetChunks: 1, returnedChunks: 2, irrelevantChunks: 1
  })
  process.stdout.write('A9 retrieval harness self-test passed.\n')
}

async function main() {
  if (process.argv.includes('--selftest')) return selftest()
  const url = new URL(HOST)
  if (!loopback(url.href)) throw new Error('A9 benchmark accepts only a loopback Ollama endpoint. Project source must stay local.')
  const model = option('model', '')
  const embeddingModel = option('embedding', 'nomic-embed-text')
  const runs = Number.parseInt(option('runs', '3'), 10)
  const outputPath = option('out', '')
  const root = path.resolve(option('root', ROOT))
  if (!model) throw new Error('Specify a local chat model with --model.')
  if (!Number.isInteger(runs) || runs < 1 || runs > 10) throw new Error('--runs must be an integer from 1 to 10.')

  process.stderr.write(`Reading bounded source corpus from ${root}\n`)
  const corpus = await readCorpus(root)
  if (!corpus.chunks.length) throw new Error('No supported source chunks were found.')
  process.stderr.write(`Embedding ${corpus.chunks.length} chunks with ${embeddingModel}; no model download will be attempted.\n`)
  const indexBuildMs = await buildIndex(corpus.chunks, embeddingModel)
  const benchmarkId = new Date().toISOString()
  const revision = spawnSync('git', ['-C', root, 'rev-parse', 'HEAD'], { encoding: 'utf8', windowsHide: true })
  const sourceCommit = revision.status === 0 ? revision.stdout.trim() : null
  const output = []
  const modes = ['literal-only', 'semantic-only', 'both']
  for (const run of Array.from({ length: runs }, (_, index) => index + 1)) {
    for (const task of TASKS) {
      const queryVector = (await embedBatch(embeddingModel, [task.question]))[0]
      const literalTop = literalSearch(corpus.files, task.literalProbe, TOP_K)
      const semanticTop = semanticSearch(corpus.chunks, queryVector)
      const retrieval = {
        literal: resultMetrics(literalTop, task),
        semantic: resultMetrics(semanticTop, task),
        combined: resultMetrics(combineResults(literalTop, semanticTop), task)
      }
      for (const mode of modes) {
        const result = await runModelTask(model, mode, task, corpus.chunks, corpus.files, embeddingModel, run)
        Object.assign(result, {
          benchmarkId, embeddingModel, sourceCommit, sourceFiles: corpus.fileCount,
          sourceChunks: corpus.chunks.length, sourceBytes: corpus.byteCount, indexBuildMs
        })
        result.directRetrieval = retrieval
        output.push(result)
        const line = JSON.stringify(result)
        if (outputPath) {
          await fs.mkdir(path.dirname(path.resolve(outputPath)), { recursive: true })
          await fs.appendFile(outputPath, line + '\n', 'utf8')
        }
        process.stdout.write(line + '\n')
      }
    }
  }

  const summary = modes.map(mode => {
    const rows = output.filter(item => item.mode === mode)
    return {
      mode, tasks: rows.length,
      taskPassRate: rows.filter(item => item.taskPassed).length / rows.length,
      validToolCallRate: rows.filter(item => item.toolCallsValid).length / rows.length,
      meanReturnedChunks: rows.reduce((sum, item) => sum + item.returnedChunks, 0) / rows.length,
      meanIrrelevantChunks: rows.reduce((sum, item) => sum + item.irrelevantChunks, 0) / rows.length,
      meanLatencyMs: Math.round(rows.reduce((sum, item) => sum + item.elapsedMs, 0) / rows.length)
    }
  })
  process.stderr.write(`\nLocal model: ${model}; embedding model: ${embeddingModel}; source files: ${corpus.fileCount}; chunks: ${corpus.chunks.length}; bytes: ${corpus.byteCount}.\n`)
  process.stderr.write('Summary: ' + JSON.stringify(summary) + '\n')
}

main().catch(error => {
  process.stderr.write(`${error.message}\n`)
  process.exitCode = 1
})
