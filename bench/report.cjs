// Builds a self-contained comparison page from the JSON lines that run.cjs writes.
// usage: node bench/report.cjs <results.jsonl> <out.html> [--verdict file.html] [--hardware "text"] [--exclude regex] [--host url] [--date YYYY-MM-DD]
//   --verdict   an HTML fragment shown at the top of the page (the written conclusions)
//   --hardware  a sentence describing the machine, shown under the title (for example "an M2 laptop, 24 GB RAM")
//   --exclude   a regular expression; results for matching model names are left out
//   Model size and quantization come from the local Ollama at the time the page is generated; they are left out for models that are not installed.
// When the same task was run again with the same model, thinking mode, run number and temperature, the later line replaces the earlier one.
const fs = require('node:fs')
const { execSync } = require('node:child_process')
const positional = []; const options = {}
{ const args = process.argv.slice(2); for (let i = 0; i < args.length; i++) { if (args[i].startsWith('--')) options[args[i].slice(2)] = args[++i]; else positional.push(args[i]) } }
const [resultsPath, outPath] = positional
const verdictPath = options.verdict
if (!resultsPath || !outPath) { console.error('usage: node bench/report.cjs <results.jsonl> <out.html> [--verdict file] [--hardware text] [--exclude regex] [--host url] [--date YYYY-MM-DD]'); process.exit(1) }
const HOST = options.host || process.env.BENCH_OLLAMA_URL || 'http://127.0.0.1:11434'
const esc = (s) => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]))

const rows = fs.readFileSync(resultsPath, 'utf8').split('\n').filter(Boolean).map(l => { try { return JSON.parse(l) } catch { return null } }).filter(Boolean)
const phases = rows.filter(r => r.phase).map(r => r.phase)
const finished = phases.includes('ALL DONE')
const EXCLUDE = options.exclude ? new RegExp(options.exclude, 'i') : /$^/
// when a task was re-run with the same settings and run number (for example after raising a token cap), the latest row wins
const latest = new Map()
for (const r of rows.filter(r => r.task && r.score !== undefined && !EXCLUDE.test(r.model))) latest.set([r.model, r.think ? 1 : 0, r.task, r.run, r.temp].join("|"), r)
const results = [...latest.values()]
const problems = rows.filter(r => (r.error || r.skipped) && !EXCLUDE.test(r.model || ''))

const TASKS = [
  ['A1-merge', 'Merge intervals', 'Algorithm: merge overlapping and touching intervals (9 hidden checks)'],
  ['A2-lru', 'LRU cache', 'Algorithm: O(1) LRU cache class (7 hidden checks)'],
  ['A3-evaluate', 'Expression evaluator', 'Algorithm: arithmetic parser with precedence, unary minus, errors, no eval (19 hidden checks)'],
  ['B1-debug', 'Fix 3 bugs', 'Debugging: three planted bugs among five working functions (13 checks)'],
  ['B2-multifile', 'Multi-file change', 'Rename a field across 3 files and add validation (10 checks)'],
  ['C-agent', 'Agent loop', 'Tool-using agent fixes a failing project; scored on 7 hidden tests'],
  ['H1-filter', 'Filter compiler', 'Hard algorithm: a search-filter parser with precedence, quoting, dotted paths and 16 invalid-input cases (51 hidden checks)'],
  ['H2-refactor', 'Refactor', 'Hard: refactor an 80-line pricing function into helpers without changing behaviour; compared with the original on 12 edge cases and 200 random orders (22 checks)'],
  ['H3-agent', 'Hard agent', 'Hard: agent fixes a four-module billing library with 7 bugs, only 2 of which show up as failing tests; scored on 29 hidden tests (unchanged code scores 52%)'],
]
const ROUTINE = TASKS.slice(0, 6).map(t => t[0]), HARD = TASKS.slice(6).map(t => t[0])
const median = (a) => { const s = a.filter(x => x != null && !Number.isNaN(x)).sort((x, y) => x - y); if (!s.length) return null; const m = s.length >> 1; return s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2 }
const mean = (a) => a.length ? a.reduce((x, y) => x + y, 0) / a.length : null

let tags = {}
try { for (const m of JSON.parse(execSync('curl -s --max-time 5 ' + HOST + '/api/tags', { encoding: 'utf8' })).models) tags[m.name] = m } catch {}
let ollamaVersion = ''; try { ollamaVersion = execSync('ollama --version', { encoding: 'utf8' }).replace(/\s+/g, ' ').trim() } catch {}
const ARCH = { 'qwen3-coder:30b': 'MoE, ~3B active', 'qwen3-coder-next:q2_k_l': 'MoE, ~3B active', 'qwen3.6:35b-a3b': 'MoE, ~3B active', 'qwen3.8:27b': 'Dense' }

// group by config = model + thinking
const configs = new Map()
for (const r of results) {
  const key = r.model + '|' + (r.think ? 'on' : 'off')
  if (!configs.has(key)) configs.set(key, { model: r.model, think: !!r.think, byTask: {}, entries: [] })
  const c = configs.get(key); c.entries.push(r); (c.byTask[r.task] ||= []).push(r)
}
const idx = (ids) => ids.map(id => TASKS.findIndex(t => t[0] === id))
const groupMean = (means, ids) => { const v = idx(ids).map(i => means[i]).filter(x => x != null); return v.length ? mean(v) : null }
const stats = [...configs.values()].map(c => {
  const taskMeans = TASKS.map(([id]) => c.byTask[id] ? mean(c.byTask[id].map(r => r.score)) : null)
  const done = taskMeans.filter(x => x != null)
  const perfect = taskMeans.filter(x => x === 1).length
  // the first batch only: temperature 0 for thinking-off runs, the published thinking settings (temperature 1.0) for thinking-on runs; the later repeat batch also numbers its runs from 1
  const run1 = c.entries.filter(r => r.run === 1 && (r.think || r.temp === 0))
  const wall = (ids) => { const e = run1.filter(r => ids.includes(r.task)); return e.length ? e.reduce((s2, r) => s2 + (r.wallS || 0), 0) : null }
  const count = (ids) => run1.filter(r => ids.includes(r.task)).length
  const shots = c.entries.filter(r => r.promptTokPerS != null)
  const agentTasks = ['C-agent', 'H3-agent']
  return {
    ...c, taskMeans, tasksDone: done.length, perfect,
    routine: groupMean(taskMeans, ROUTINE), hard: groupMean(taskMeans, HARD),
    genTokS: median(c.entries.map(r => r.tokPerS)), promptTokS: median(shots.map(r => r.promptTokPerS)),
    loadS: (run1.find(r => r.loadS > 0) || {}).loadS ?? null,
    wallRoutine: wall(ROUTINE), routineCount: count(ROUTINE), wallHard: wall(HARD), hardCount: count(HARD),
    runs: Math.max(...c.entries.map(r => r.run || 1)),
    agents: agentTasks.map(id => ({ id, rs: c.byTask[id] || [] })).filter(a => a.rs.length),
    thinkChars: median(c.entries.map(r => r.thinkChars)),
  }
})
stats.sort((x, y) => (y.hard ?? -1) - (x.hard ?? -1) || (y.routine ?? -1) - (x.routine ?? -1) || (x.wallHard ?? 1e9) - (y.wallHard ?? 1e9))

const fmt = (x, d = 1) => x == null ? '<span class="na">n/a</span>' : Number(x).toFixed(d)
const mins = (sec, partial) => sec == null ? '<span class="na">not run</span>' : (sec >= 90 ? (sec / 60).toFixed(1) + ' min' : sec.toFixed(0) + ' s') + (partial ? ' <small>(partial)</small>' : '')
const cell = (x, note) => {
  if (x == null) return '<td class="na">not run</td>'
  const hue = Math.round(x * 120)
  return '<td class="score" style="--h:' + hue + '">' + (x * 100).toFixed(0) + '%' + (note ? '<small>' + esc(note) + '</small>' : '') + '</td>'
}
const name = (x) => esc(x.model) + (x.think ? ' <span class="pill">thinking on</span>' : '')
const meta = (x) => { const t = tags[x.model]; const d = t && t.details; return t ? (t.size / 1e9).toFixed(0) + ' GB · ' + esc((d && d.parameter_size) || '') + ' ' + esc((d && d.quantization_level) || '') + ' · ' + esc(ARCH[x.model] || '') : esc(ARCH[x.model] || '') }
const partialNote = (x, ids) => idx(ids).filter(i => x.taskMeans[i] != null).length < ids.length ? 'partial' : ''

const summaryRows = stats.map(x => '<tr>' +
  '<td class="model">' + name(x) + '<small>' + meta(x) + '</small></td>' +
  cell(x.routine, x.routine != null ? partialNote(x, ROUTINE) : '') +
  cell(x.hard, x.hard != null ? partialNote(x, HARD) : '') +
  '<td>' + x.perfect + '/' + TASKS.length + '</td>' +
  '<td>' + fmt(x.genTokS) + '</td><td>' + fmt(x.promptTokS, 0) + '</td>' +
  '<td>' + mins(x.wallRoutine, x.routineCount && x.routineCount < ROUTINE.length) + '</td>' +
  '<td>' + mins(x.wallHard, x.hardCount && x.hardCount < HARD.length) + '</td>' +
  '<td>' + fmt(x.loadS, 0) + (x.loadS != null ? ' s' : '') + '</td></tr>').join('\n')

const matrixRows = stats.map(x => '<tr><td class="model">' + name(x) + '</td>' + TASKS.map(([id]) => {
  const rs = x.byTask[id]; if (!rs) return cell(null)
  const sc = rs.map(r => r.score); const m = mean(sc)
  const note = rs.length > 1 ? rs.length + ' runs: ' + Math.min(...sc).toFixed(2) + '–' + Math.max(...sc).toFixed(2) : (id.includes('agent') ? '' : rs[0].passed + '/' + rs[0].total)
  return cell(m, note)
}).join('') + '</tr>').join('\n')

const speedRows = stats.map(x => '<tr><td class="model">' + name(x) + '</td><td>' + fmt(x.genTokS) + '</td><td>' + fmt(x.promptTokS, 0) + '</td><td>' + fmt(x.loadS, 0) + (x.loadS != null ? ' s' : '') + '</td><td>' + (x.think && x.thinkChars ? Math.round(x.thinkChars / 4) + ' tokens (approx.)' : '<span class="na">—</span>') + '</td></tr>').join('\n')

const agentRows = stats.flatMap(x => x.agents.map(a => {
  const rs = a.rs
  return '<tr><td class="model">' + name(x) + '</td><td>' + esc((TASKS.find(t => t[0] === a.id) || [])[1] || a.id) + '</td><td>' + esc(rs.map(r => r.passed + '/' + r.total).join(', ')) + '</td><td>' + fmt(mean(rs.map(r => r.steps))) + '</td><td>' + rs.reduce((n, r) => n + (r.invalidCalls || 0), 0) + '</td><td>' + rs.reduce((n, r) => n + (r.failedEdits || 0), 0) + '</td><td>' + rs.filter(r => r.loops).length + '</td><td>' + esc(rs.map(r => r.ended).join(', ')) + '</td><td>' + mins(median(rs.map(r => r.wallS))) + '</td></tr>'
})).join('\n')

const verdict = verdictPath && fs.existsSync(verdictPath) ? fs.readFileSync(verdictPath, 'utf8') : ''
const now = options.date || new Date().toISOString().slice(0, 10)
const status = finished ? 'Complete run' : 'PARTIAL run (still in progress)'
const taskList = TASKS.map(([, n, d]) => `<li><b>${esc(n)}</b> — ${esc(d)}</li>`).join('')
const problemList = problems.length ? `<h2>Problems during the run</h2><ul>${problems.map(p => `<li>${esc(p.model)} ${p.think ? '(thinking on) ' : ''}${esc(p.task || '')}: ${esc(p.error || p.skipped)}</li>`).join('')}</ul>` : ''

const html = `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Local coding model comparison</title>
<style>
:root { --bg:#fbfaf8; --fg:#1f1d1a; --muted:#6b665e; --line:#e2ded6; --card:#ffffff; --accent:#c4552d; --s:58%; --l:80%; }
@media (prefers-color-scheme: dark) { :root { --bg:#161513; --fg:#ece8e1; --muted:#9a948a; --line:#332f2a; --card:#1e1c19; --accent:#e07a52; --l:32%; } }
* { box-sizing:border-box; }
body { margin:0; background:var(--bg); color:var(--fg); font:15px/1.5 system-ui,-apple-system,Segoe UI,sans-serif; }
main { max-width:1080px; margin:0 auto; padding:32px 16px 64px; }
h1 { margin:0 0 4px; font-size:1.7rem; } h2 { margin:36px 0 10px; font-size:1.2rem; }
p, li { max-width:75ch; } .muted { color:var(--muted); } .banner { background:#fff3cd; color:#5c4400; padding:8px 12px; border-radius:8px; margin:12px 0; }
.wrap { overflow-x:auto; border:1px solid var(--line); border-radius:10px; background:var(--card); }
table { border-collapse:collapse; width:100%; min-width:760px; }
th, td { padding:9px 12px; text-align:right; border-bottom:1px solid var(--line); white-space:nowrap; }
th { font-size:.78rem; text-transform:uppercase; letter-spacing:.04em; color:var(--muted); font-weight:600; }
th:first-child, td.model { text-align:left; } tr:last-child td { border-bottom:0; }
td.model { font-weight:600; white-space:normal; } td.model small { display:block; font-weight:400; color:var(--muted); font-size:.78rem; }
td.score { background:hsl(var(--h) var(--s) var(--l)); font-weight:600; } td.score small { display:block; font-weight:400; font-size:.72rem; opacity:.8; }
@media (prefers-color-scheme: dark) { td.score { color:#fff; } }
.na { color:var(--muted); font-weight:400; } .pill { display:inline-block; background:var(--accent); color:#fff; border-radius:99px; padding:0 8px; font-size:.7rem; font-weight:600; vertical-align:middle; }
.verdict { background:var(--card); border:1px solid var(--line); border-left:4px solid var(--accent); border-radius:8px; padding:4px 16px 8px; margin:16px 0; }
ul { padding-left:1.2em; }
</style>
</head>
<body>
<main>
<h1>Local coding model comparison</h1>
<p class="muted">${esc(status)} · generated ${esc(now)}${options.hardware ? ' · run on ' + esc(options.hardware) : ''}${ollamaVersion ? ' · ' + esc(ollamaVersion) : ''}</p>
${finished ? '' : '<div class="banner">This run was not finished when the page was generated. Missing cells are shown as “not run”.</div>'}
${verdict ? `<div class="verdict">${verdict}</div>` : ''}

<h2>Overall</h2>
<p class="muted">Each score is the share of hidden checks a model passed, averaged over tasks with equal weight (and over repeat runs where there are any). <b>Routine</b> is six everyday tasks; <b>Hard</b> is three harder ones. Speeds are medians; times are the wall time of the first run. Rows are ordered by hard score.</p>
<div class="wrap"><table>
<thead><tr><th>Model</th><th>Routine score</th><th>Hard score</th><th>Perfect tasks (of 9)</th><th>Generate tok/s</th><th>Read prompt tok/s</th><th>Time, 6 routine</th><th>Time, 3 hard</th><th>Cold load</th></tr></thead>
<tbody>${summaryRows}</tbody></table></div>

<h2>Score by task</h2>
<div class="wrap"><table>
<thead><tr><th>Model</th>${TASKS.map(([, n]) => `<th>${esc(n)}</th>`).join('')}</tr></thead>
<tbody>${matrixRows}</tbody></table></div>

<p class="muted">For scale: a model that changes nothing on the hard agent task scores 52%; an answer that always returns false scores about 25% on the filter task; a refactor that returns an empty object scores about 5%.</p>

<h2>Speed</h2>
<div class="wrap"><table>
<thead><tr><th>Model</th><th>Generate tok/s</th><th>Read prompt tok/s</th><th>Cold load</th><th>Typical thinking length</th></tr></thead>
<tbody>${speedRows}</tbody></table></div>

<h2>Agent loop details</h2>
<div class="wrap"><table>
<thead><tr><th>Model</th><th>Task</th><th>Hidden tests passed</th><th>Avg steps</th><th>Invalid tool calls</th><th>Failed edits</th><th>Loops</th><th>How it ended</th><th>Median time</th></tr></thead>
<tbody>${agentRows}</tbody></table></div>

${problemList}

<h2>What was tested</h2>
<ul>${taskList}</ul>
<p>Every answer was scored automatically against hidden checks; the checks were first verified against a known-correct solution (100%) and a deliberately wrong one (low). Each model was cold-loaded, with a 16K-token context. Runs used temperature 0 (repeat runs used 0.3), thinking off unless marked, and a token cap per answer.</p>

<h2>Limits</h2>
<ul>
<li>Six small JavaScript tasks are a narrow sample. They show whether a model handles routine coding and tool use, not how it performs on large or unfamiliar codebases, other languages, or long contexts.</li>
<li>Most models got one run; only the fast models got repeats, so small score differences (a few points) can be luck.</li>
<li>Speeds are for this machine, on the CPU, and will differ elsewhere. A GPU or different memory bandwidth changes the ranking of dense versus mixture-of-experts models a lot.</li>
<li>Thinking-mode runs used a fixed token cap; a model cut off while thinking may be scored lower than its true ability.</li>
</ul>
</main>
</body>
</html>
`
fs.writeFileSync(outPath, html)
console.log('wrote', outPath, '| configs:', stats.length, '| finished:', finished)
