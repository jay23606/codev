# Local model benchmark

A small, self-contained benchmark for comparing Ollama models on coding work. It produced [`comparison.html`](../comparison.html). Every answer is checked automatically against hidden tests, so no one has to judge output by eye.

## Tasks

| Group | Task | What it checks |
|---|---|---|
| Routine | Merge intervals, LRU cache, expression evaluator | Algorithms with edge cases (9, 7 and 19 hidden checks) |
| Routine | Fix 3 bugs | Three planted bugs among five working functions (13 checks) |
| Routine | Multi-file change | Rename a field across three files and add validation (10 checks) |
| Routine | Agent loop | A tool-using agent fixes a small failing project (7 hidden tests) |
| Hard | Filter compiler | A search-filter parser with precedence, quoting and 16 invalid-input cases (51 checks) |
| Hard | Refactor | Split an 80-line pricing function into helpers without changing behaviour; compared with the original on 12 edge cases and 200 random orders (22 checks) |
| Hard | Hard agent | An agent fixes a four-module library with seven bugs, only two of which show up as failing tests; the comments describe the intended behaviour (29 hidden tests) |

The agent tasks give the model `list_files`, `read_file`, `write_file`, `run_tests` (and, for the hard one, `replace_in_file`) inside a temporary project. The hidden tests are never shown to the model and are run only after it finishes.

## Running it

Needs Node 20 or newer and a running Ollama with the models pulled. Nothing to install.

```bash
# 1. Check the tasks themselves (no model needed): every task must score 100% on a
#    known-correct answer and low on a known-wrong one
node bench/run.cjs --selftest

# 2. Run models: thinking off, one run each, deterministic
node bench/run.cjs --models qwen3.6:35b-a3b,qwen3-coder:30b --think off --runs 1 --temp 0 --out results.jsonl

# 3. Only the hard tasks, thinking on, three runs
node bench/run.cjs --models qwen3.6:35b-a3b --think on --tasks H --runs 3 --out results.jsonl

# 4. Build the comparison page
node bench/report.cjs results.jsonl comparison.html --hardware "a laptop with 32 GB RAM, no GPU"
```

Run one model at a time and avoid other heavy work while it runs, or the timings will be unreliable. `run.cjs` appends one JSON line per finished task and unloads models between runs so each starts cold. See the comments at the top of each script for every option.

## Shadow-snapshot sizing prototype

To measure the cost of copying a large tracked-plus-untracked source tree before a command, run `pwsh -NoProfile -File bench/shadow-snapshot.ps1`. The script creates an isolated temporary Git repository with 10,000 source files, binary assets, untracked files, and ignored build output; copies the tracked and non-ignored files to a temporary snapshot; verifies every copied file by SHA-256; reports copy time and disk use; then removes only its uniquely named temporary directories. It does not touch a real project. Parameters at the top of the script let you scale the fixture.

## Results in this folder

`results/2026-09-28.jsonl` is the raw data behind the published page, `results/2026-09-28-big-budget.jsonl` is the follow-up run with a larger thinking budget, and `results/2026-09-28-verdict.html` is the written conclusion shown at the top of the page. To rebuild the page:

```bash
node bench/report.cjs bench/results/2026-09-28.jsonl comparison.html \
  --verdict bench/results/2026-09-28-verdict.html \
  --hardware "an AMD Ryzen AI 9 HX 370 laptop, 62 GB RAM, no discrete GPU (Ollama ran on the CPU)"
```

## Things to know before trusting a number

- **Small sample.** Nine JavaScript tasks. The results show whether a model handles routine work and tool use, not how it does on large codebases, other languages or long contexts.
- **Runs vary.** The same model varied by up to 33 points between runs of one task. Use several runs before concluding anything, and treat gaps of a few points as noise.
- **Thinking mode is limited by a token cap.** Thinking-on runs use a fixed budget per reply (8,000 tokens by default); a model that is still thinking when it hits the cap writes no answer. The cap can be raised with `BENCH_THINK_MAXTOK` and `BENCH_CTX`.
- **Each model runs with its own default sampling settings.** Unless overridden, the settings in the model's Ollama build apply (for example the official Qwen3.6 build sets `presence_penalty 1.5`, `top_k 20` and `top_p 0.95`, while a build imported from Hugging Face may set none). Only temperature and seed are set by the harness. When comparing two builds of the same model, pass identical settings to both with `BENCH_EXTRA_OPTIONS`, or the comparison measures the settings as much as the model.
- **Speeds belong to one machine.** They depend on the hardware; a GPU or different memory bandwidth can change which models are fastest.
- **The floor is not 0%.** Code that changes nothing scores 52% on the hard agent task, and an always-false answer about 25% on the filter task.
- **A model's "done" message is not evidence.** In these runs every model that fixed only some of the bugs still reported success; only the hidden tests know.
