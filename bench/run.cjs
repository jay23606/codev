// Local-model coding benchmark for Ollama models: nine tasks, all checked automatically against hidden tests.
//   Routine (6): three algorithm tasks, a bug-fixing task, a multi-file change, and a short tool-using agent loop.
//   Hard (3): a search-filter parser, a behaviour-preserving refactor, and an agent fixing a four-module library with seven bugs.
//
// Usage:
//   node bench/run.cjs --selftest        check every task against a known-correct and a known-wrong answer (no model needed)
//   node bench/run.cjs --models a,b --think off|on|both --runs 1 --temp 0 --budget 80 --tasks all --out results.jsonl
//     --tasks: "all", or comma-separated id prefixes such as H (all hard tasks) or A1,B2
//     --budget: minutes allowed per model and thinking mode before the remaining tasks are skipped
//     --temp: used for thinking-off runs; thinking-on runs use the sampling Qwen publishes for thinking mode (temperature 1.0, top_p 0.95, top_k 20)
//   Environment: BENCH_OLLAMA_URL (default http://127.0.0.1:11434), BENCH_MAXTOK (thinking-off answer cap, default 6000),
//                BENCH_EXTRA_OPTIONS (JSON of extra Ollama options applied to every request),
//                BENCH_THINK_MAXTOK (thinking-on cap, default 8000 for single answers and 6000 per agent step), BENCH_CTX (context size, default 16384)
// Needs Node 20 or newer (global fetch, node:test). One JSON line per finished task is appended to --out.
const vm = require('node:vm')
const fs = require('node:fs')
const os = require('node:os')
const path = require('node:path')
const { spawnSync } = require('node:child_process')

const HOST = process.env.BENCH_OLLAMA_URL || 'http://127.0.0.1:11434'
const F = '```'
const NUM_CTX = 16384

// ---------- helpers ----------
const arg = (name, def) => { const i = process.argv.indexOf('--' + name); return i > -1 ? process.argv[i + 1] : def }
const flag = (name) => process.argv.includes('--' + name)
const ns = (x) => (x ?? 0) / 1e9

function fencedBlocks(text) {
  const out = []; const re = /```([^\n`]*)\n([\s\S]*?)```/g; let m
  while ((m = re.exec(text))) out.push({ info: m[1].trim(), code: m[2], index: m.index, end: m.index + m[0].length })
  return out
}
function firstCode(text) {
  const b = fencedBlocks(text)
  const js = b.find(x => /^(js|javascript)?$/i.test(x.info)) || b[0]
  return (js ? js.code : text).trim()
}
function filesFrom(text) {
  const files = {}; const blocks = fencedBlocks(text); let prevEnd = 0
  for (const b of blocks) {
    const hay = b.info + ' ' + text.slice(prevEnd, b.index).slice(-160)
    const names = [...hay.matchAll(/([\w-]+\.js)\b/g)].map(m => m[1])
    if (names.length) files[names[names.length - 1]] = b.code.trim()
    prevEnd = b.end
  }
  return files
}
function makeRequire(files) {
  const cache = {}
  const req = (from) => (spec) => {
    if (!spec.startsWith('.')) throw new Error('only relative requires: ' + spec)
    const name = path.posix.normalize(path.posix.join(path.posix.dirname(from), spec))
    const key = files[name] !== undefined ? name : (files[name + '.js'] !== undefined ? name + '.js' : null)
    if (!key) throw new Error('module not found: ' + spec)
    if (cache[key]) return cache[key].exports
    const mod = { exports: {} }; cache[key] = mod
    const ctx = vm.createContext({ module: mod, exports: mod.exports, require: req(key), console })
    vm.runInContext(files[key], ctx, { timeout: 3000 })
    return mod.exports
  }
  return req('index.js')
}
// checks: {desc, expr, eq} | {desc, expr, throws:true} | {desc, notContains:[...]} ; makeCtx returns a fresh vm context
function runChecks(makeCtx, checks, codeText) {
  let passed = 0; const failed = []
  for (const c of checks) {
    try {
      if (c.pred) { if (c.pred(codeText)) passed++; else failed.push(c.desc); continue }
      if (c.notContains) { if (c.notContains.some(s => codeText.includes(s))) failed.push(c.desc); else passed++; continue }
      const ctx = makeCtx()
      if (c.throws) {
        const r = vm.runInContext(`(()=>{try{ (${c.expr}); return 'NO' }catch(e){ return 'THROWS' }})()`, ctx, { timeout: 2000 })
        if (r === 'THROWS') passed++; else failed.push(c.desc)
      } else {
        const r = vm.runInContext(`JSON.stringify(${c.expr})`, ctx, { timeout: 2000 })
        if (r === JSON.stringify(c.eq)) passed++; else failed.push(c.desc + ' (got ' + String(r).slice(0, 40) + ')')
      }
    } catch (e) { failed.push(c.desc + ' [' + String(e.message).slice(0, 50) + ']') }
  }
  return { score: passed / checks.length, passed, total: checks.length, failed }
}
const globalCtx = (code, name) => () => {
  const ctx = vm.createContext({ console })
  vm.runInContext(code + `\n;try{globalThis.${name}=${name}}catch(e){}`, ctx, { timeout: 3000 })
  return ctx
}

// ---------- tasks: algorithm ----------
const mergeChecks = [
  { desc: 'basic', expr: 'mergeIntervals([[1,3],[2,6],[8,10],[15,18]])', eq: [[1, 6], [8, 10], [15, 18]] },
  { desc: 'touching', expr: 'mergeIntervals([[1,4],[4,5]])', eq: [[1, 5]] },
  { desc: 'unsorted', expr: 'mergeIntervals([[8,10],[1,3],[2,6]])', eq: [[1, 6], [8, 10]] },
  { desc: 'contained', expr: 'mergeIntervals([[1,10],[2,3],[4,5]])', eq: [[1, 10]] },
  { desc: 'empty', expr: 'mergeIntervals([])', eq: [] },
  { desc: 'single', expr: 'mergeIntervals([[5,7]])', eq: [[5, 7]] },
  { desc: 'no mutation', expr: '(()=>{const a=[[8,10],[1,3]];const c=JSON.stringify(a);mergeIntervals(a);return JSON.stringify(a)===c})()', eq: true },
  { desc: 'negatives', expr: 'mergeIntervals([[-5,-1],[-2,3]])', eq: [[-5, 3]] },
  { desc: 'numeric sort', expr: 'mergeIntervals([[10,12],[2,3],[9,11]])', eq: [[2, 3], [9, 12]] },
]
const lruChecks = [
  { desc: 'basic', expr: '(()=>{const c=new LRUCache(2);c.put(1,1);c.put(2,2);const a=c.get(1);c.put(3,3);return [a,c.get(2),c.get(3),c.get(1)]})()', eq: [1, -1, 3, 1] },
  { desc: 'update does not evict', expr: '(()=>{const c=new LRUCache(2);c.put(1,1);c.put(2,2);c.put(1,10);c.put(3,3);return [c.get(1),c.get(2),c.get(3)]})()', eq: [10, -1, 3] },
  { desc: 'capacity 1', expr: '(()=>{const c=new LRUCache(1);c.put(1,1);c.put(2,2);return [c.get(1),c.get(2)]})()', eq: [-1, 2] },
  { desc: 'get refreshes recency', expr: "(()=>{const c=new LRUCache(3);c.put('a',1);c.put('b',2);c.put('c',3);c.get('a');c.put('d',4);return [c.get('a'),c.get('b'),c.get('c'),c.get('d')]})()", eq: [1, -1, 3, 4] },
  { desc: 'falsy value', expr: "(()=>{const c=new LRUCache(2);c.put('z',0);return [c.get('z'),c.get('missing')]})()", eq: [0, -1] },
  { desc: '1000 puts', expr: '(()=>{const c=new LRUCache(100);for(let i=0;i<1000;i++)c.put(i,i);let ok=true;for(let i=0;i<900;i++)if(c.get(i)!==-1)ok=false;for(let i=900;i<1000;i++)if(c.get(i)!==i)ok=false;return ok})()', eq: true },
  { desc: 'update value', expr: '(()=>{const c=new LRUCache(2);c.put(1,1);c.put(1,5);return c.get(1)})()', eq: 5 },
]
const evalOk = [['1+2*3', 7], ['(1+2)*3', 9], ['2*-3', -6], ['-2*-3', 6], ['10/4', 2.5], [' 2 * (3 + 4) - 5 / 2 ', 11.5], ['8/2/2', 2], ['2-3-4', -5], ['3 - -2', 5], ['1.5+2.25', 3.75]]
const evalBad = ['', '2*(3', '(2+3))', '1+*2', '2+', '2 $ 3', '()', '1 2']
const evalChecks = [
  ...evalOk.map(([e, v]) => ({ desc: 'eval ' + JSON.stringify(e), expr: `evaluate(${JSON.stringify(e)})`, eq: v })),
  ...evalBad.map(e => ({ desc: 'throws on ' + JSON.stringify(e), expr: `evaluate(${JSON.stringify(e)})`, throws: true })),
  { desc: 'no eval/Function', notContains: ['eval(', 'new Function', 'Function('] },
]

// ---------- tasks: debug ----------
const inventorySrc = `// Utilities for a small shop dashboard.

/** Return one page of items. Pages are numbered from 1. */
function paginate(items, page, pageSize) {
  const start = page * pageSize;
  return items.slice(start, start + pageSize);
}

/** Format an amount in cents as a currency string, e.g. 1999 -> "$19.99", -500 -> "-$5.00". */
function formatMoney(cents) {
  return "$" + (cents / 100).toFixed(2);
}

/** Group items into an object keyed by keyFn(item). Each value is an array of items in original order. */
function groupBy(items, keyFn) {
  const groups = {};
  for (const item of items) {
    const key = keyFn(item);
    groups[key] = [item];
  }
  return groups;
}

/** Remove duplicate values, keeping the first occurrence and the original order. */
function unique(values) {
  return values.filter((v, i) => values.indexOf(v) === i);
}

/** Sum of price * quantity for all lines. */
function orderTotal(lines) {
  let total = 0;
  for (const line of lines) total += line.price * line.quantity;
  return total;
}

/** Split an array into chunks of the given size. */
function chunk(array, size) {
  const out = [];
  for (let i = 0; i < array.length; i += size) out.push(array.slice(i, i + size));
  return out;
}

/** Return a new array sorted by keyFn, without modifying the input. */
function sortBy(items, keyFn) {
  return [...items].sort((a, b) => (keyFn(a) < keyFn(b) ? -1 : keyFn(a) > keyFn(b) ? 1 : 0));
}

module.exports = { paginate, formatMoney, groupBy, unique, orderTotal, chunk, sortBy };
`
const inventoryRef = inventorySrc
  .replace('const start = page * pageSize;', 'const start = (page - 1) * pageSize;')
  .replace('return "$" + (cents / 100).toFixed(2);', 'return (cents < 0 ? "-" : "") + "$" + (Math.abs(cents) / 100).toFixed(2);')
  .replace('groups[key] = [item];', '(groups[key] = groups[key] || []).push(item);')
const M = (e) => `(()=>{const __m=module.exports; return ${e}})()`
const inventoryChecks = [
  { desc: 'page 1', expr: M('__m.paginate([1,2,3,4,5],1,2)'), eq: [1, 2] },
  { desc: 'page 2', expr: M('__m.paginate([1,2,3,4,5],2,2)'), eq: [3, 4] },
  { desc: 'last partial page', expr: M('__m.paginate([1,2,3,4,5],3,2)'), eq: [5] },
  { desc: 'page past end', expr: M('__m.paginate([1,2,3,4,5],4,2)'), eq: [] },
  { desc: 'money positive', expr: M('__m.formatMoney(1999)'), eq: '$19.99' },
  { desc: 'money negative', expr: M('__m.formatMoney(-500)'), eq: '-$5.00' },
  { desc: 'money small negative', expr: M('__m.formatMoney(-5)'), eq: '-$0.05' },
  { desc: 'money zero', expr: M('__m.formatMoney(0)'), eq: '$0.00' },
  { desc: 'groupBy keeps all items', expr: M("__m.groupBy([1,2,3,4,5],x=>x%2?'odd':'even')"), eq: { odd: [1, 3, 5], even: [2, 4] } },
  { desc: 'unique order', expr: M('__m.unique([3,1,3,2,1])'), eq: [3, 1, 2] },
  { desc: 'orderTotal', expr: M('__m.orderTotal([{price:2.5,quantity:2},{price:1,quantity:3}])'), eq: 8 },
  { desc: 'chunk', expr: M('__m.chunk([1,2,3,4,5],2)'), eq: [[1, 2], [3, 4], [5]] },
  { desc: 'sortBy no mutation', expr: M("(()=>{const a=[{n:2},{n:1}];const b=__m.sortBy(a,x=>x.n);return JSON.stringify(a)==='[{\"n\":2},{\"n\":1}]'&&JSON.stringify(b)==='[{\"n\":1},{\"n\":2}]'})()"), eq: true },
]
const debugCtx = (code) => () => {
  const mod = { exports: {} }
  const ctx = vm.createContext({ module: mod, exports: mod.exports, console })
  vm.runInContext(code, ctx, { timeout: 3000 })
  return ctx
}

// ---------- tasks: multi-file ----------
const projFiles = {
  'user.js': `function createUser(name, email) {
  return { id: Math.floor(Math.random() * 1e9), name, email };
}

module.exports = { createUser };
`,
  'store.js': `const { createUser } = require('./user');

function addUser(store, name, email) {
  const user = createUser(name, email);
  store.users.push(user);
  return user;
}

function findByEmail(store, email) {
  return store.users.find((u) => u.email === email);
}

module.exports = { addUser, findByEmail };
`,
  'report.js': `function formatUser(user) {
  return \`\${user.name} <\${user.email}>\`;
}

function emailDomains(users) {
  return [...new Set(users.map((u) => u.email.split('@')[1]))];
}

module.exports = { formatUser, emailDomains };
`,
}
const projRef = {
  'user.js': `function createUser(name, emailAddress) {
  if (typeof name !== 'string' || name.trim() === '') throw new Error('name required');
  if (typeof emailAddress !== 'string' || !emailAddress.includes('@')) throw new Error('invalid email');
  return { id: Math.floor(Math.random() * 1e9), name, emailAddress };
}

module.exports = { createUser };
`,
  'store.js': projFiles['store.js'].replace('u.email === email', 'u.emailAddress === email'),
  'report.js': projFiles['report.js'].replace('${user.email}', '${user.emailAddress}').replace('u.email.split', 'u.emailAddress.split'),
}
const R = (e) => `(()=>{ const u=require('./user'), s=require('./store'), r=require('./report'); return ${e} })()`
const multiChecks = [
  { desc: 'new field name', expr: R("require('./user').createUser('Ann','ann@x.com').emailAddress"), eq: 'ann@x.com' },
  { desc: 'old field gone', expr: R("'email' in u.createUser('Ann','ann@x.com')"), eq: false },
  { desc: 'name kept', expr: R("u.createUser('Ann','ann@x.com').name"), eq: 'Ann' },
  { desc: 'throws empty name', expr: R("u.createUser('','a@b.c')"), throws: true },
  { desc: 'throws blank name', expr: R("u.createUser('   ','a@b.c')"), throws: true },
  { desc: 'throws no @', expr: R("u.createUser('Ann','nope')"), throws: true },
  { desc: 'store add + find', expr: R("(()=>{const st={users:[]};s.addUser(st,'Ann','ann@x.com');return [st.users.length, s.findByEmail(st,'ann@x.com').name]})()"), eq: [1, 'Ann'] },
  { desc: 'store find misses', expr: R("(()=>{const st={users:[]};s.addUser(st,'Ann','ann@x.com');return s.findByEmail(st,'zzz@x.com')===undefined})()"), eq: true },
  { desc: 'formatUser', expr: R("r.formatUser({name:'Ann',emailAddress:'ann@x.com'})"), eq: 'Ann <ann@x.com>' },
  { desc: 'emailDomains', expr: R("r.emailDomains([{emailAddress:'a@x.com'},{emailAddress:'b@y.org'},{emailAddress:'c@x.com'}])"), eq: ['x.com', 'y.org'] },
]
const multiCtx = (files) => () => {
  const req = makeRequire(files)
  return vm.createContext({ require: req, console })
}

// ---------- tasks: agent loop ----------
const cartSrc = `function lineTotal(item) {
  return item.price;
}

function subtotal(items) {
  return items.reduce((sum, item) => sum + lineTotal(item), 0);
}

function applyDiscount(amount, percent) {
  return amount - (amount * percent) / 100 - (amount * percent) / 100;
}

function roundMoney(x) {
  return Math.floor(x * 100) / 100;
}

function cartTotal(items, discountPercent = 0) {
  return roundMoney(applyDiscount(subtotal(items), discountPercent));
}

module.exports = { lineTotal, subtotal, applyDiscount, roundMoney, cartTotal };
`
const cartRef = cartSrc
  .replace('return item.price;', 'return item.price * item.quantity;')
  .replace('return amount - (amount * percent) / 100 - (amount * percent) / 100;', 'return amount - (amount * percent) / 100;')
  .replace('return Math.floor(x * 100) / 100;', 'return Math.round(x * 100) / 100;')
const cartTestSrc = `const test = require('node:test');
const assert = require('node:assert');
const { subtotal, cartTotal, roundMoney } = require('../src/cart');

test('subtotal multiplies price by quantity', () => {
  assert.strictEqual(subtotal([{ price: 2, quantity: 3 }, { price: 1.5, quantity: 2 }]), 9);
});

test('cartTotal applies the discount once', () => {
  assert.strictEqual(cartTotal([{ price: 10, quantity: 2 }], 10), 18);
});

test('roundMoney rounds to the nearest cent', () => {
  assert.strictEqual(roundMoney(2.346), 2.35);
});
`
const cartHiddenSrc = `const test = require('node:test');
const assert = require('node:assert');
const c = require('../src/cart');

test('all functions still exported', () => {
  for (const n of ['lineTotal', 'subtotal', 'applyDiscount', 'roundMoney', 'cartTotal']) assert.strictEqual(typeof c[n], 'function');
});
test('lineTotal', () => { assert.strictEqual(c.lineTotal({ price: 4, quantity: 3 }), 12); assert.strictEqual(c.lineTotal({ price: 4, quantity: 0 }), 0); });
test('applyDiscount', () => { assert.strictEqual(c.applyDiscount(200, 25), 150); assert.strictEqual(c.applyDiscount(80, 0), 80); assert.strictEqual(c.applyDiscount(50, 100), 0); });
test('roundMoney', () => { assert.strictEqual(c.roundMoney(10.999), 11); assert.strictEqual(c.roundMoney(2.344), 2.34); assert.strictEqual(c.roundMoney(2.346), 2.35); });
test('cartTotal with discount', () => { assert.strictEqual(c.cartTotal([{ price: 5, quantity: 1 }], 50), 2.5); assert.strictEqual(c.cartTotal([{ price: 10, quantity: 2 }], 10), 18); });
test('cartTotal empty', () => { assert.strictEqual(c.cartTotal([], 10), 0); });
test('cartTotal rounds', () => { assert.strictEqual(c.cartTotal([{ price: 19.99, quantity: 3 }], 0), 59.97); assert.strictEqual(c.cartTotal([{ price: 3.33, quantity: 3 }], 15), 8.49); });
`
function runNodeTests(dir, file, reporter = 'tap') {
  const args = ['--test', '--test-reporter=' + reporter, ...(file ? [file] : [])]
  const r = spawnSync(process.execPath, args, { cwd: dir, encoding: 'utf8', timeout: 30000 })
  const out = (r.stdout || '') + (r.stderr || '')
  const pass = +(/# pass (\d+)/.exec(out)?.[1] ?? 0), fail = +(/# fail (\d+)/.exec(out)?.[1] ?? 0)
  return { out, pass, fail, ok: r.status === 0 }
}
function makeProject() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bench-cart-'))
  fs.mkdirSync(path.join(dir, 'src')); fs.mkdirSync(path.join(dir, 'test'))
  fs.writeFileSync(path.join(dir, 'src', 'cart.js'), cartSrc)
  fs.writeFileSync(path.join(dir, 'test', 'cart.test.js'), cartTestSrc)
  fs.writeFileSync(path.join(dir, 'package.json'), '{"name":"cart","version":"1.0.0"}')
  return dir
}
function scoreProject(dir) {
  fs.mkdirSync(path.join(dir, 'hidden'), { recursive: true })
  fs.mkdirSync(path.join(dir, 'test_hidden'), { recursive: true })
  fs.writeFileSync(path.join(dir, 'test_hidden', 'cart.hidden.test.js'), cartHiddenSrc.replace("'../src/cart'", "'../src/cart'"))
  const r = runNodeTests(dir, path.join('test_hidden', 'cart.hidden.test.js'))
  return { score: r.pass + r.fail ? r.pass / (r.pass + r.fail) : 0, pass: r.pass, fail: r.fail }
}
const agentTools = [
  { type: 'function', function: { name: 'list_files', description: 'List files and folders in a project directory', parameters: { type: 'object', properties: { path: { type: 'string', description: 'Project-relative directory, use "." for the root' } }, required: ['path'] } } },
  { type: 'function', function: { name: 'read_file', description: 'Read a text file from the project', parameters: { type: 'object', properties: { path: { type: 'string', description: 'Project-relative path' } }, required: ['path'] } } },
  { type: 'function', function: { name: 'write_file', description: 'Replace the entire content of a project file', parameters: { type: 'object', properties: { path: { type: 'string' }, content: { type: 'string', description: 'The complete new file content' } }, required: ['path', 'content'] } } },
  { type: 'function', function: { name: 'run_tests', description: 'Run the project test suite and return the output', parameters: { type: 'object', properties: {} } } },
]
function execTool(dir, name, args) {
  const inside = (p) => { if (typeof p !== 'string') return null; const full = path.resolve(dir, p); return full.startsWith(dir) && !full.includes('test_hidden') ? full : null }
  try {
    if (name === 'list_files') { const p = inside(args?.path); if (!p) return { invalid: true, text: 'error: invalid path' }; return { text: fs.readdirSync(p).filter(n => n !== 'test_hidden' && n !== 'hidden').join('\n') } }
    if (name === 'read_file') { const p = inside(args?.path); if (!p) return { invalid: true, text: 'error: invalid path' }; return { text: fs.readFileSync(p, 'utf8') } }
    if (name === 'write_file') { const p = inside(args?.path); if (!p || typeof args?.content !== 'string') return { invalid: true, text: 'error: need a valid path and string content' }; fs.writeFileSync(p, args.content); return { text: 'wrote ' + args.path, wrote: true } }
    if (name === 'run_tests') { const r = runNodeTests(dir, null, 'spec'); return { text: r.out.slice(0, 2500), ranTests: true, testsOk: r.ok } }
    return { invalid: true, text: 'error: unknown tool ' + name }
  } catch (e) { return { invalid: true, text: 'error: ' + e.message.slice(0, 120) } }
}

// ---------- model calls ----------
async function chat(model, body, timeoutMs = 45 * 60 * 1000) {
  // Streamed: a non-streaming request sends no headers until the whole answer is done, and Node's fetch gives up after 300 s of that.
  const ac = new AbortController(); const timer = setTimeout(() => ac.abort(), timeoutMs)
  try {
    const r = await fetch(HOST + '/api/chat', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ model, stream: true, keep_alive: '30m', ...body }), signal: ac.signal })
    if (!r.ok) throw new Error(r.status + ' ' + (await r.text()).slice(0, 160))
    const dec = new TextDecoder(); let buf = '', content = '', thinking = '', calls = [], last = null
    for await (const chunk of r.body) {
      buf += dec.decode(chunk, { stream: true }); let i
      while ((i = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, i).trim(); buf = buf.slice(i + 1); if (!line) continue
        const o = JSON.parse(line); if (o.error) throw new Error(o.error)
        const m = o.message || {}
        if (m.content) content += m.content
        if (m.thinking) thinking += m.thinking
        if (m.tool_calls) calls.push(...m.tool_calls)
        if (o.done) last = o
      }
    }
    if (!last) throw new Error('stream ended without a final message')
    return { ...last, message: { role: 'assistant', content, thinking, tool_calls: calls.length ? calls : undefined } }
  } finally { clearTimeout(timer) }
}
const stats = (res) => ({ evalTokens: res.eval_count || 0, genS: ns(res.eval_duration), promptTokens: res.prompt_eval_count || 0, promptS: ns(res.prompt_eval_duration), loadS: ns(res.load_duration), thinkChars: (res.message?.thinking || '').length })
// Thinking runs use the sampling settings Qwen publishes for its thinking mode (greedy decoding can make thinking models repeat endlessly).
// Extra Ollama options for every request, as JSON in BENCH_EXTRA_OPTIONS (for example {"presence_penalty":1.5,"top_k":20}). Use it to run two builds of a model under identical settings: a model's own default sampling settings apply unless overridden here.
const EXTRA_OPTIONS = process.env.BENCH_EXTRA_OPTIONS ? JSON.parse(process.env.BENCH_EXTRA_OPTIONS) : {}
const opts = (temp, run, think) => ({ ...EXTRA_OPTIONS, ...baseOpts(temp, run, think) })
const baseOpts = (temp, run, think) => think ? { temperature: 1.0, top_p: 0.95, top_k: 20, seed: 100 + run, num_ctx: Number(process.env.BENCH_CTX) || NUM_CTX, num_predict: Number(process.env.BENCH_THINK_MAXTOK) || 8000 } : { temperature: temp, seed: 100 + run, num_ctx: NUM_CTX, num_predict: Number(process.env.BENCH_MAXTOK) || 6000 }

async function singleShot(model, think, temp, run, prompt) {
  const res = await chat(model, { think, messages: [{ role: 'user', content: prompt }], options: opts(temp, run, think) })
  return { text: res.message?.content || '', ...stats(res), truncated: res.done_reason === 'length' }
}

const tasks = [
  { id: 'A1-merge', kind: 'shot', prompt: `Write a JavaScript function mergeIntervals(intervals) that takes an array of [start, end] pairs (start <= end) and returns a new array of merged intervals sorted by start. Intervals that overlap or touch (for example [1,4] and [4,5]) must be merged. Do not modify the input array. Output only the code in one ${F}javascript block, no explanation.`, score: (t) => { const c = firstCode(t); return runChecks(globalCtx(c, 'mergeIntervals'), mergeChecks, c) }, ref: `${F}javascript\nfunction mergeIntervals(iv){const s=iv.map(x=>[x[0],x[1]]).sort((a,b)=>a[0]-b[0]);const out=[];for(const x of s){const l=out[out.length-1];if(l&&x[0]<=l[1]){if(x[1]>l[1])l[1]=x[1]}else out.push(x)}return out}\n${F}`, wrong: `${F}javascript\nfunction mergeIntervals(x){return x}\n${F}` },
  { id: 'A2-lru', kind: 'shot', prompt: `Write a JavaScript class LRUCache. constructor(capacity) with capacity >= 1. get(key) returns the value, or -1 if the key is absent, and marks the key as most recently used. put(key, value) inserts or updates the key, marks it most recently used, and if the number of keys exceeds capacity evicts the least recently used key. Both operations must run in O(1) average time. Output only the code in one ${F}javascript block, no explanation.`, score: (t) => { const c = firstCode(t); return runChecks(globalCtx(c, 'LRUCache'), lruChecks, c) }, ref: `${F}javascript\nclass LRUCache{constructor(c){this.c=c;this.m=new Map()}get(k){if(!this.m.has(k))return -1;const v=this.m.get(k);this.m.delete(k);this.m.set(k,v);return v}put(k,v){if(this.m.has(k))this.m.delete(k);this.m.set(k,v);if(this.m.size>this.c)this.m.delete(this.m.keys().next().value)}}\n${F}`, wrong: `${F}javascript\nclass LRUCache{constructor(c){this.m={}}get(k){return this.m[k]||-1}put(k,v){this.m[k]=v}}\n${F}` },
  { id: 'A3-evaluate', kind: 'shot', prompt: `Write a JavaScript function evaluate(expr) that evaluates an arithmetic expression string and returns a number. Support decimal numbers, + - * /, parentheses, unary minus (for example '-3' and '2*-3'), and arbitrary whitespace. Use normal operator precedence and left-to-right associativity. Do not use eval, Function or any other dynamic code execution. For any invalid expression (empty, unbalanced parentheses, two operators in a row other than unary minus, a trailing operator, two numbers in a row, unknown characters) throw an Error. Output only the code in one ${F}javascript block, no explanation.`, score: (t) => { const c = firstCode(t); return runChecks(globalCtx(c, 'evaluate'), evalChecks, c) }, ref: `${F}javascript\nfunction evaluate(s){let i=0;const ws=()=>{while(s[i]===' ')i++};function num(){ws();let st=i;while(/[0-9.]/.test(s[i]||''))i++;if(st===i)throw new Error('num');return parseFloat(s.slice(st,i))}function factor(){ws();if(s[i]==='-'){i++;return -factor()}if(s[i]==='('){i++;const v=expr();ws();if(s[i]!==')')throw new Error(')');i++;return v}return num()}function term(){let v=factor();for(;;){ws();if(s[i]==='*'){i++;v*=factor()}else if(s[i]==='/'){i++;v/=factor()}else return v}}function expr(){let v=term();for(;;){ws();if(s[i]==='+'){i++;v+=term()}else if(s[i]==='-'){i++;v-=term()}else return v}}const v=expr();ws();if(i<s.length)throw new Error('junk');return v}\n${F}`, wrong: `${F}javascript\nfunction evaluate(s){return 0}\n${F}` },
  { id: 'B1-debug', kind: 'shot', prompt: `This JavaScript module has bugs. Users report: (1) page 2 of a list shows the wrong items, (2) negative amounts are displayed incorrectly, (3) grouping loses items. Find and fix all bugs without changing the behaviour of code that already works. Return the complete corrected file in one ${F}javascript block, no explanation.\n\n${F}javascript\n${inventorySrc}${F}`, score: (t) => { const c = firstCode(t); return runChecks(debugCtx(c), inventoryChecks, c) }, ref: `${F}javascript\n${inventoryRef}${F}`, wrong: `${F}javascript\n${inventorySrc}${F}` },
  { id: 'B2-multifile', kind: 'shot', prompt: `Below are three files of a small CommonJS project.\n\n### file: user.js\n${F}javascript\n${projFiles['user.js']}${F}\n\n### file: store.js\n${F}javascript\n${projFiles['store.js']}${F}\n\n### file: report.js\n${F}javascript\n${projFiles['report.js']}${F}\n\nMake these changes: (1) rename the user field \`email\` to \`emailAddress\` everywhere it is used (keep function names as they are); (2) createUser must throw an Error if the name is empty or only whitespace, or if the email address does not contain an '@'. Return the COMPLETE content of every file you change, each in its own ${F}javascript block preceded by a line of the form "### file: <name>". Do not return files that need no change. No other explanation.`, score: (t) => { const files = { ...projFiles, ...filesFrom(t) }; return runChecks(multiCtx(files), multiChecks, t) }, ref: Object.entries(projRef).map(([n, c]) => `### file: ${n}\n${F}javascript\n${c}${F}`).join('\n\n'), wrong: '' },
  { id: 'C-agent', kind: 'agent' },
]


// ---------- hard tasks + spec-based agent runner ----------
tasks.push(...require('./hard_tasks.cjs')({ F, firstCode, runChecks, vm, globalCtx }))

function makeProject2(spec) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'bench-proj-'))
  for (const [rel, content] of Object.entries(spec.files)) { const full = path.join(dir, rel); fs.mkdirSync(path.dirname(full), { recursive: true }); fs.writeFileSync(full, content) }
  return dir
}
function scoreProject2(dir, spec) {
  const hd = path.join(dir, 'test_hidden'); fs.mkdirSync(hd, { recursive: true })
  let pass = 0, fail = 0
  for (const [name, content] of Object.entries(spec.hidden)) {
    fs.writeFileSync(path.join(hd, name), content)
    const r = runNodeTests(dir, path.join('test_hidden', name)); pass += r.pass; fail += r.fail
  }
  return { score: pass + fail ? pass / (pass + fail) : 0, pass, fail }
}
function execTool2(dir, name, args) {
  const inside = (p) => { if (typeof p !== 'string') return null; const full = path.resolve(dir, p); return full.startsWith(dir) && !full.includes('test_hidden') ? full : null }
  try {
    if (name === 'list_files') { const p = inside(args?.path); if (!p) return { invalid: true, text: 'error: invalid path' }; return { text: fs.readdirSync(p).filter(n => n !== 'test_hidden').join('\n') } }
    if (name === 'read_file') { const p = inside(args?.path); if (!p) return { invalid: true, text: 'error: invalid path' }; return { text: fs.readFileSync(p, 'utf8') } }
    if (name === 'write_file') { const p = inside(args?.path); if (!p || typeof args?.content !== 'string') return { invalid: true, text: 'error: need a valid path and string content' }; fs.writeFileSync(p, args.content); return { text: 'wrote ' + args.path, wrote: true, tests: /^test[\\/]/.test(args.path) } }
    if (name === 'replace_in_file') {
      const p = inside(args?.path)
      if (!p || typeof args?.old_text !== 'string' || typeof args?.new_text !== 'string') return { invalid: true, text: 'error: need path, old_text and new_text as strings' }
      const cur = fs.readFileSync(p, 'utf8'); const parts = cur.split(args.old_text)
      if (parts.length === 1) return { failedEdit: true, text: 'error: old_text not found in ' + args.path }
      if (parts.length > 2) return { failedEdit: true, text: 'error: old_text appears ' + (parts.length - 1) + ' times; include more context so it is unique' }
      fs.writeFileSync(p, parts.join(args.new_text)); return { text: 'replaced in ' + args.path, wrote: true, tests: /^test[\\/]/.test(args.path) }
    }
    if (name === 'run_tests') { const r = runNodeTests(dir, null, 'spec'); return { text: r.out.slice(0, 3500), ranTests: true, testsOk: r.ok } }
    return { invalid: true, text: 'error: unknown tool ' + name }
  } catch (e) { return { invalid: true, text: 'error: ' + e.message.slice(0, 120) } }
}
async function agentRun2(model, think, temp, run, spec) {
  const dir = makeProject2(spec); const t0 = Date.now()
  const messages = [{ role: 'system', content: spec.system }, { role: 'user', content: spec.user }]
  const m = { steps: 0, toolCalls: 0, invalid: 0, failedEdits: 0, loops: false, wrote: 0, editedTests: 0, testRuns: 0, evalTokens: 0, genS: 0, promptTokens: 0, promptS: 0, loadS: 0, finalMessage: '', ended: 'max-steps' }
  const seen = {}
  try {
    for (let step = 0; step < spec.maxSteps; step++) {
      const res = await chat(model, { think, messages, tools: spec.tools, options: { ...opts(temp, run, think), num_predict: think ? (Number(process.env.BENCH_THINK_MAXTOK) || 6000) : 2500 } })
      const s = stats(res); m.steps++; m.evalTokens += s.evalTokens; m.genS += s.genS; m.promptTokens += s.promptTokens; m.promptS += s.promptS; m.loadS += s.loadS
      const msg = res.message || {}; const calls = msg.tool_calls || []
      messages.push({ role: 'assistant', content: msg.content || '', ...(calls.length ? { tool_calls: calls } : {}) })
      if (!calls.length) { m.finalMessage = (msg.content || '').slice(0, 140); m.ended = 'final-message'; break }
      let stop = false
      for (const c of calls) {
        const name = c.function?.name; let a = c.function?.arguments; if (typeof a === 'string') { try { a = JSON.parse(a) } catch { a = null } }
        m.toolCalls++
        const key = name + JSON.stringify(a); seen[key] = (seen[key] || 0) + 1
        if (seen[key] >= 3 && name !== 'run_tests') { m.loops = true; stop = true }
        const r = execTool2(dir, name, a)
        if (r.invalid) m.invalid++
        if (r.failedEdit) m.failedEdits++
        if (r.wrote) m.wrote++
        if (r.tests) m.editedTests++
        if (r.ranTests) m.testRuns++
        messages.push({ role: 'tool', tool_name: name, content: r.text })
      }
      if (stop) { m.ended = 'loop'; break }
    }
  } catch (e) { m.ended = 'error: ' + e.message.slice(0, 100) }
  const sc = scoreProject2(dir, spec)
  try { fs.rmSync(dir, { recursive: true, force: true }) } catch {}
  return { ...m, score: sc.score, passed: sc.pass, total: sc.pass + sc.fail, wallS: (Date.now() - t0) / 1000 }
}

async function agentRun(model, think, temp, run) {
  const dir = makeProject(); const t0 = Date.now()
  const messages = [
    { role: 'system', content: 'You are a coding agent working in a small Node.js project. Use the tools to inspect and fix the code so that all tests pass. Run the tests before and after you change anything. When the tests pass, reply with a short final message and make no tool call.' },
    { role: 'user', content: 'The cart totals are wrong. Fix the bugs in src/cart.js so the tests pass.' },
  ]
  const m = { steps: 0, toolCalls: 0, invalid: 0, loops: false, wrote: 0, testRuns: 0, evalTokens: 0, genS: 0, promptTokens: 0, promptS: 0, loadS: 0, finalMessage: '', ended: 'max-steps' }
  const seen = {}
  try {
    for (let step = 0; step < 10; step++) {
      const res = await chat(model, { think, messages, tools: agentTools, options: { ...opts(temp, run, think), num_predict: think ? 6000 : 1500 } })
      const s = stats(res); m.steps++; m.evalTokens += s.evalTokens; m.genS += s.genS; m.promptTokens += s.promptTokens; m.promptS += s.promptS; m.loadS += s.loadS
      const msg = res.message || {}
      const calls = msg.tool_calls || []
      messages.push({ role: 'assistant', content: msg.content || '', ...(calls.length ? { tool_calls: calls } : {}) })
      if (!calls.length) { m.finalMessage = (msg.content || '').slice(0, 140); m.ended = 'final-message'; break }
      let stop = false
      for (const c of calls) {
        const name = c.function?.name; let a = c.function?.arguments; if (typeof a === 'string') { try { a = JSON.parse(a) } catch { a = null } }
        m.toolCalls++
        const key = name + JSON.stringify(a); seen[key] = (seen[key] || 0) + 1
        if (seen[key] >= 3 && name !== 'run_tests') { m.loops = true; stop = true }
        const r = execTool(dir, name, a)
        if (r.invalid) m.invalid++
        if (r.wrote) m.wrote++
        if (r.ranTests) m.testRuns++
        messages.push({ role: 'tool', tool_name: name, content: r.text })
      }
      if (stop) { m.ended = 'loop'; break }
    }
  } catch (e) { m.ended = 'error: ' + e.message.slice(0, 100) }
  const sc = scoreProject(dir)
  try { fs.rmSync(dir, { recursive: true, force: true }) } catch {}
  return { ...m, score: sc.score, passed: sc.pass, total: sc.pass + sc.fail, wallS: (Date.now() - t0) / 1000 }
}

// ---------- self-test ----------
async function selftest() {
  let bad = 0
  for (const t of tasks.filter(x => x.kind === 'shot')) {
    const good = t.score(t.ref), wrong = t.score(t.wrong)
    const ok = good.score === 1 && wrong.score < 0.6
    if (!ok) bad++
    console.log(t.id.padEnd(14), 'reference', `${good.passed}/${good.total}`, '| wrong-answer', `${wrong.passed}/${wrong.total}`, ok ? 'OK' : 'PROBLEM', good.failed.length ? 'ref failures: ' + good.failed.join('; ') : '')
  }
  for (const t of tasks.filter(x => x.subtle)) {
    const sub = t.score(t.subtle); const ok = sub.score < 1
    if (!ok) bad++
    console.log((t.id + ' subtle').padEnd(14), 'subtly-wrong version', sub.passed + '/' + sub.total, ok ? 'OK (detected)' : 'PROBLEM (not detected)', sub.failed.slice(0, 2).join('; '))
  }
  for (const t of tasks.filter(x => x.kind === 'agent2')) {
    const d = makeProject2(t); const before = scoreProject2(d, t); const visBefore = runNodeTests(d, null)
    for (const [rel, content] of Object.entries(t.ref)) fs.writeFileSync(path.join(d, rel), content)
    const after = scoreProject2(d, t); const visAfter = runNodeTests(d, null)
    fs.rmSync(d, { recursive: true, force: true })
    const ok = before.score < 0.7 && !visBefore.ok && after.score === 1 && visAfter.ok
    if (!ok) bad++
    console.log(t.id.padEnd(14), 'buggy hidden', before.pass + '/' + (before.pass + before.fail), '| visible tests fail on buggy', !visBefore.ok, '| fixed hidden', after.pass + '/' + (after.pass + after.fail), '| visible pass on fixed', visAfter.ok, ok ? 'OK' : 'PROBLEM')
  }
  const dir = makeProject(); const before = scoreProject(dir)
  fs.writeFileSync(path.join(dir, 'src', 'cart.js'), cartRef); const after = scoreProject(dir)
  const vis = runNodeTests(dir, null)
  fs.rmSync(dir, { recursive: true, force: true })
  const aok = before.score < 0.6 && after.score === 1 && vis.ok
  if (!aok) bad++
  console.log('C-agent'.padEnd(14), 'buggy project', `${before.pass}/${before.pass + before.fail}`, '| reference fix', `${after.pass}/${after.pass + after.fail}`, '| visible tests pass on fix', vis.ok, aok ? 'OK' : 'PROBLEM')
  console.log(bad ? `SELFTEST FAILED (${bad})` : 'SELFTEST OK')
  process.exit(bad ? 1 : 0)
}

// ---------- main ----------
async function unloadAll() {
  try { const ps = await (await fetch(HOST + '/api/ps')).json(); for (const m of ps.models || []) await fetch(HOST + '/api/generate', { method: 'POST', body: JSON.stringify({ model: m.name, keep_alive: 0 }) }) } catch {}
}
async function main() {
  const models = (arg('models', '')).split(',').filter(Boolean)
  const thinkMode = arg('think', 'off'); const runs = +arg('runs', 1); const temp = +arg('temp', 0)
  const budgetMin = +arg('budget', 80); const out = arg('out', null)
  const only = arg('tasks', 'all'); const list = only === 'all' ? tasks : tasks.filter(t => only.split(',').some(p => t.id.startsWith(p)))
  const thinkList = thinkMode === 'both' ? [false, true] : [thinkMode === 'on']
  const write = (o) => { console.log(JSON.stringify(o)); if (out) fs.appendFileSync(out, JSON.stringify(o) + '\n') }
  for (const model of models) {
    for (const think of thinkList) {
      await unloadAll(); const start = Date.now(); let skipped = false
      for (let run = 1; run <= runs && !skipped; run++) {
        for (const t of list) {
          if ((Date.now() - start) / 60000 > budgetMin) { write({ model, think, task: t.id, run, skipped: 'time budget exceeded' }); continue }
          const t0 = Date.now()
          try {
            if (t.kind === 'shot') {
              const r = await singleShot(model, think, temp, run, t.prompt); const sc = t.score(r.text)
              write({ model, think, temp: think ? 1.0 : temp, run, task: t.id, score: +sc.score.toFixed(3), passed: sc.passed, total: sc.total, failed: sc.failed.slice(0, 4), truncated: r.truncated, evalTokens: r.evalTokens, thinkChars: r.thinkChars, tokPerS: r.genS ? +(r.evalTokens / r.genS).toFixed(1) : null, promptTokens: r.promptTokens, promptTokPerS: r.promptS ? +(r.promptTokens / r.promptS).toFixed(1) : null, loadS: +r.loadS.toFixed(1), wallS: +((Date.now() - t0) / 1000).toFixed(1) })
            } else if (t.kind === 'agent2') {
              const r = await agentRun2(model, think, temp, run, t)
              write({ model, think, temp: think ? 1.0 : temp, run, task: t.id, score: +r.score.toFixed(3), passed: r.passed, total: r.total, steps: r.steps, toolCalls: r.toolCalls, invalidCalls: r.invalid, failedEdits: r.failedEdits, editedTests: r.editedTests, loops: r.loops, wrote: r.wrote, testRuns: r.testRuns, ended: r.ended, evalTokens: r.evalTokens, tokPerS: r.genS ? +(r.evalTokens / r.genS).toFixed(1) : null, promptTokens: r.promptTokens, wallS: +r.wallS.toFixed(1), finalMessage: r.finalMessage })
            } else {
              const r = await agentRun(model, think, temp, run)
              write({ model, think, temp: think ? 1.0 : temp, run, task: t.id, score: +r.score.toFixed(3), passed: r.passed, total: r.total, steps: r.steps, toolCalls: r.toolCalls, invalidCalls: r.invalid, loops: r.loops, wrote: r.wrote, testRuns: r.testRuns, ended: r.ended, evalTokens: r.evalTokens, tokPerS: r.genS ? +(r.evalTokens / r.genS).toFixed(1) : null, promptTokens: r.promptTokens, wallS: +r.wallS.toFixed(1), finalMessage: r.finalMessage })
            }
          } catch (e) {
            const msg = String(e.message)
            if (think && /think/i.test(msg)) { write({ model, think, skipped: 'model does not support thinking' }); skipped = true; break }
            write({ model, think, run, task: t.id, error: msg.slice(0, 160), wallS: +((Date.now() - t0) / 1000).toFixed(1) })
          }
        }
      }
      try { await fetch(HOST + '/api/generate', { method: 'POST', body: JSON.stringify({ model, keep_alive: 0 }) }) } catch {}
    }
  }
  console.log('ALL DONE')
}
if (flag('selftest')) selftest(); else main()
