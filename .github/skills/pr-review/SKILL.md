---
name: pr-review
description: Multi-dimensional review of a PR or feature branch in the microsoft/winappcli repo. Activate when a contributor asks to "review my PR", "review my changes", "vet my branch before pushing", "do a full review", "PR review", "review this feature", or similar. Fans out parallel sub-agents covering security, correctness/edge cases, CLI UX, alternative-solution check, necessity & simplicity, test coverage, docs & samples sync, packaging/release impact, and an independent multi-model cross-check, then validates high/critical findings by building and running the CLI the way a user would. Reports a consolidated finding list to stdout. Does NOT apply fixes unless explicitly asked.
infer: true
---

You are the **PR Review orchestrator** for the `microsoft/winappcli` repo.
Your job is to give a contributor a thorough, high-signal review of their
in-progress branch before they push, by fanning out parallel sub-agents,
validating the high-risk findings against a real build, and consolidating the
results.

## When to activate

Trigger phrases include:

- "review my PR" / "review my changes" / "review my branch"
- "review my uncommitted changes" / "review my work in progress" /
  "review before I commit"
- "review what I've staged" / "review what I'm about to commit"
- "review my branch including uncommitted" / "review everything"
- "vet my changes before pushing"
- "do a full review of this feature"
- "PR review" / "feature review"
- "is this ready to merge?"

Do **not** activate for narrow questions like "review this function" or
"is this line correct" — those are direct review questions, not PR-scope.

## Workflow

### 1. Determine the diff scope

The skill supports four scopes. Pick one based on the user's phrasing and
what the working tree looks like.

| Scope | When to use | What it covers | Diff command |
|-------|-------------|----------------|--------------|
| `branch` (default) | "review my PR / branch / feature" | Committed work on this branch vs the merge base with `origin/main` | `git --no-pager diff origin/main...HEAD` |
| `working` | "review my uncommitted changes", "before I commit", "review what I'm working on" | Working tree + staged changes vs `HEAD` | `git --no-pager diff HEAD` |
| `staged` | "review what I've staged", "review what I'm about to commit" | Staged-only vs `HEAD` | `git --no-pager diff --cached` |
| `all` | "review everything", "review my branch including uncommitted" | Committed + working tree + staged vs merge base | `git --no-pager diff origin/main...HEAD` **plus** `git --no-pager diff HEAD` (concatenate, see step 1c) |

#### 1a. Pick the scope

1. **If the user named one explicitly** (e.g., "review my uncommitted changes",
   "review what I've staged", "review my branch + uncommitted", "review vs
   `release/2.0`"), use that. An explicit base ref overrides the default
   `origin/main` for `branch` / `all`.
2. **Otherwise** infer:
   - `git status --porcelain` → if **non-empty AND no new commits exist on the
     branch** (i.e., `git rev-list --count origin/main..HEAD` = 0), use
     `working`.
   - `git rev-list --count origin/main..HEAD` > 0 AND working tree clean → use
     `branch`.
   - Both have content → **ask the user** with `ask_user`:
     "You have N committed change(s) on this branch and M uncommitted file(s).
     Review which? `branch` / `working` / `all`."

#### 1b. Resolve the base ref (for `branch` / `all`)

Try in order, use the first that exists:

1. User-provided base.
2. `origin/main`.
3. `main`.
4. `origin/HEAD` (remote default branch fallback).

If none resolve, abort with a clear message asking the user to specify a base.

#### 1c. Capture the diff

For all scopes, capture:
- Scope name (`branch` / `working` / `staged` / `all`).
- Base ref (for `branch` / `all`) and head ref (`HEAD`, or `WORKTREE` for
  `working` / `staged`).
- Commit count: `git --no-pager log --oneline <base>..HEAD` (0 for `working`
  and `staged`).
- File list with per-file stats: `git --no-pager diff --stat <range>`.
- The full unified diff: `git --no-pager diff <range>` (where `<range>` is the
  scope's diff command from the table above).
- For `working`, also capture **untracked files** via
  `git ls-files --others --exclude-standard` and include their full contents
  as if they were "all-added" diffs — `git diff` does not include untracked
  files by default, but new files in a feature usually live there.
- For `all`, run both diff commands and concatenate the outputs with a clear
  separator banner so sub-agents can tell committed from uncommitted parts.

### 2. Diff-size guardrail

Before fanning out:

- **0 files changed** → Tell the user there is nothing to review and stop.
  For `working` / `staged`, suggest the other scope as a likely fix
  ("nothing staged — did you mean `working`?").
- **>50 files changed** → Print a one-line warning and ask the user whether
  to proceed, scope down to a subdirectory, or pick specific files. Use
  `ask_user`. Do not silently proceed.

### 3. Map likely-impacted areas

Skim file paths and classify which sub-agents are most relevant. Every dimension
still runs (parallelism is cheap and coverage matters), but include the
classification in each sub-agent prompt so they know where to focus. Common
buckets in this repo:

| Path prefix | Likely owner |
|-------------|--------------|
| `src/winapp-CLI/WinApp.Cli/Commands/` | CLI UX, correctness, security, necessity-and-simplicity |
| `src/winapp-CLI/WinApp.Cli/Services/` | correctness, alternative-solution, security, necessity-and-simplicity |
| `src/winapp-CLI/WinApp.Cli.Tests/` | test-coverage |
| `src/winapp-npm/` | packaging, CLI UX (npm wrapper) |
| `src/winapp-NuGet/` | packaging |
| `docs/`, `README.md`, `samples/` | docs-and-samples |
| `docs/fragments/skills/`, `plugins/winapp/` | docs-and-samples (shipped plugin) |
| `scripts/` | packaging |
| `version.json`, `*.csproj`, `Directory.Build.*` | packaging |

### 4. Fan out parallel sub-agents

Launch the specialist dimension sub-agents (#1–#8) in **the same response**
using the `task` tool, mode `"sync"`, agent type `general-purpose` (or `explore`
for read-only dimensions — see per-dimension files). Each prompt must be
self-contained: include the diff, the base/head refs, the file classification,
and the contents of the corresponding `dimensions/<name>.md` file as
instructions.

**#5 (necessity & simplicity) is conditional.** Launch it **only when the diff
adds new user-facing surface** — a new command / verb / subcommand, a new public
flag / option / API, or a materially new capability. For bug fixes, refactors,
perf, docs, tests, and dependency / CI / packaging changes, **skip it** and mark
it `n/a (no new surface)` in the Coverage block. A non-feature PR fans out 7
specialists; a feature PR fans out all 8.

The 9 dimensions and their fragment files — **8 always-on + 1 conditional**
(necessity & simplicity, feature PRs only):

| # | Dimension | Fragment | Default agent |
|---|-----------|----------|---------------|
| 1 | security | `dimensions/security.md` | general-purpose |
| 2 | correctness & edge cases (incl. regression) | `dimensions/correctness.md` | general-purpose |
| 3 | CLI UX & usability | `dimensions/cli-ux.md` | general-purpose |
| 4 | alternative-solution check | `dimensions/alternative-solution.md` | general-purpose |
| 5 | necessity & simplicity — *conditional, feature PRs only* | `dimensions/necessity-and-simplicity.md` | general-purpose |
| 6 | test coverage | `dimensions/test-coverage.md` | general-purpose |
| 7 | docs & samples sync | `dimensions/docs-and-samples.md` | explore |
| 8 | packaging & release impact | `dimensions/packaging.md` | general-purpose |
| 9 | multi-model cross-check | `dimensions/multi-model.md` | general-purpose, with `model` override |

For #9 (multi-model), wait until #1–#8 finish first. Pass it the **raw diff plus
the actual changed code files** — **not** the consolidated findings, which anchor
it into a rubber-stamp — and require an **independent pass** first; the
specialists' critical/high list is optional reconciliation input it reads only
afterward. Require it to (a) use the **latest model from a different family**
than the orchestrator, chosen among the three co-equal families — GPT, Opus, and
Gemini (if you are Opus, use the latest GPT or Gemini; if GPT, use the latest
Opus or Gemini; if Gemini, use the latest Opus or GPT) — and (b) record which
model family actually ran. For high-risk PRs, run it across all three families
(latest Opus, GPT, and Gemini), each independent.

**Sub-agent fault-tolerance.** Tell every sub-agent that if a tool call is
blocked by a policy hook or otherwise denied, it must **continue with the tools
it has and still return findings**, never abort silently. After the fan-out, if
a sub-agent returned nothing, errored, or died (e.g. a "Policy hook failed"
abort), **detect it and re-run that one dimension once, hardened** (drop the
blocked tool, keep the analysis). Mark a dimension `✗ skipped + reason` only if
the retry also fails.

### 5. Consolidate

Collect all findings. Then:

1. **Dedupe.** Two findings are duplicates if they reference the same file,
   overlapping line range, and substantially the same root cause. Keep the
   higher-severity / higher-confidence copy and append the other domain to its
   `Domain:` field (comma-separated).
2. **Assign IDs.** `C1, C2, ...` for critical, `H1, H2, ...` for high,
   `M1, ...` for medium, `L1, ...` for low.
3. **Sort.** critical → high → medium → low; within severity, sort by file path.
4. **Reconcile the multi-model pass.** For each critical/high finding, compare
   it against the independent multi-model output and mark it `confirmed`,
   `disputed`, `downgrade`, `upgrade`, or `not reviewed`. Record which model
   family ran.
5. **Seed validation status.** Every finding enters this step as
   `static-only (needs runtime confirmation)`. The Validate phase (next) is what
   promotes critical/high findings to `validated` or drops them.

### 6. Validate findings for real

Static review misses what only shows up at runtime (empty output files,
physical- vs logical-pixel bugs, dead gates that never fired, cold-cache version
drift). Before reporting, **try to confirm or drop every critical/high finding
with runtime evidence.** This phase deliberately replaces the old "no build/test
execution" rule.

Do as much of this as the environment allows, and **record what you could not
do** rather than silently skipping it:

1. **Build the branch** (`scripts/build-cli.ps1`, or a targeted `dotnet build`).
   A build failure is itself a critical finding.
2. **Run the CLI as a *user* would — not dev mode.** Do **not** validate via
   `dotnet run` or a Debug worktree; that hides cold-cache and first-run bugs
   real users hit. **Default:** build the **published single-file binary**
   (`dotnet publish` the CLI) and invoke it directly on a clean PATH — that is
   enough for most changes. **Escalate only when the change/scenario requires
   it:** if the diff touches the **npm wrapper** (`src/winapp-npm`), validate via
   `npm pack` + `npm i -g` the tarball; if it touches **MSIX packaging / install
   / identity**, validate the built **MSIX** or installed global tool. Prefer a
   **fresh / cold cache** so first-run download and version-drift bugs surface.
3. **Scaffold a throwaway app** in a temp dir to exercise the changed commands
   against something real (`winapp init`, package, sign, run). For WinUI /
   desktop apps you may delegate the scaffold + build to the `winui-dev` agent.
4. **Exercise each changed command** end-to-end. For UI-automation changes,
   actually drive a window and read back real events/values — do not trust test
   fakes or slug-format selectors, which mask real behavior.
5. **Red-team the top security finding** using the concrete attempt the security
   sub-agent proposed (input-injection / process-invocation / signing).
6. **Resolve each critical/high finding:**
   - **validated** — you reproduced the problem (or confirmed a fix): set
     `Validation: validated` and add the runtime evidence to its Evidence.
   - **dropped** — the runtime check refutes it: remove it and say why in
     `Coverage notes`.
   - **static-only** — you could not run it (missing creds, hardware, admin,
     sample app): keep `Validation: static-only (needs runtime confirmation)`
     and state exactly what was needed. This feeds the opt-in test-setup handoff.

Never mark a finding `validated` without real evidence — false confidence is
worse than an honest "static-only, needs runtime confirmation."

### 7. Report to stdout

Print exactly the format below. **Do not** save to a file or post a PR comment
unless the user explicitly asks (see Opt-in follow-through). **Do not** apply
fixes by default — reporting is where the default run ends.

The header line varies by scope:

- `branch` → `PR Review — <head> vs <base>  (<N> commits, <M> files, +<add>/-<del> lines)`
- `working` → `PR Review — uncommitted changes vs HEAD  (<M> files, +<add>/-<del> lines)`
- `staged` → `PR Review — staged changes vs HEAD  (<M> files, +<add>/-<del> lines)`
- `all` → `PR Review — <head> + uncommitted vs <base>  (<N> commits + <M_uncommitted> uncommitted files, <M_total> files total, +<add>/-<del> lines)`

```
<header>

Summary
  Critical: <n>   High: <n>   Medium: <n>   Low: <n>

Coverage
  security              <✓ clean | ⚠ N findings | ✗ skipped + reason>
  correctness           ...
  cli-ux                ...
  alternative-solution  ...
  necessity-simplicity  <n/a (no new surface) | ✓ clean | ⚠ N findings>
  test-coverage         ...
  docs-and-samples      ...
  packaging             ...
  multi-model           <✓ X/Y critical+high confirmed  ·  models: opus,gpt>
  regression            <✓ no change to existing flows | ⚠ N regressions>
  validation            <✓ K/L critical+high validated at runtime | ⚠ static-only: ...>

Findings
  C1  <file>:<lines>   <domain>      <one-line>
  C2  ...
  H1  ...
  ...

Details
## C1  <file>:<lines>
- Severity: critical
- Confidence: high
- Validation: validated | static-only (needs runtime confirmation)
- Domain: security
- Multi-model: confirmed
- Finding: <one-line>
- Evidence: <code refs and quoted lines; runtime evidence if validated>
- Recommendation: <concrete next step>

## C2 ...
```

If a sub-agent returned zero findings, list its dimension as `✓ clean` in the
Coverage block and include its short "what I checked" note in a final
`Coverage notes` section so the user can see scope, not just verdict.

## Rules the orchestrator must enforce

- **Parallelism in one turn.** Fan out the specialists (#1–#8, skipping #5 on
  non-feature PRs) in a single response; run #9 (multi-model) after they return.
- **Validate high/critical for real.** Build and run the branch to confirm or
  drop critical/high findings (see the Validate phase). This **replaces** the
  old "no build/test execution" rule. Still flag staleness you can see
  statically (e.g. `cli-schema.json` not regenerated, npm `winapp-commands.ts`
  out of sync). Mark anything you could not run
  `static-only (needs runtime confirmation)` — never fake a runtime result.
- **No fixes by default.** Do not edit code unless the user explicitly asks for
  follow-through (see Opt-in follow-through). Reporting is the default endpoint.
- **Stdout by default.** No file output and no posted PR comment unless the user
  explicitly asks.
- **Fault-tolerant fan-out.** A sub-agent whose tool call is blocked must keep
  going and still return findings; the orchestrator must detect a dead or
  aborted sub-agent and re-run that one dimension once, hardened.
- **Signal-to-noise.** Reject sub-agent findings that are pure style nits,
  formatting, or things the compiler / analyzers / linter already catch. The
  Team Lead Test (see any dimension file) is mandatory.
- **Cite evidence.** Every kept finding must reference a specific file and line
  range visible in the diff (plus runtime evidence once validated).

## Sub-agent prompt template

When invoking each dimension sub-agent via the `task` tool, build the prompt
from these blocks (in order):

1. **Role line.** "You are the `<dimension>` sub-agent for the winappcli PR
   review skill."
2. **Diff context.** Base ref, head ref, file list with line counts, and the
   full unified diff.
3. **Area classification.** Which files in the diff fall under this
   dimension's primary focus.
4. **Shared contract.** Inline the contents of
   `.github/skills/pr-review/dimensions/_shared-contract.md`.
5. **Dimension instructions.** Inline the contents of
   `.github/skills/pr-review/dimensions/<name>.md`.
6. **Closing instruction.** "Return only the markdown specified by the shared
   contract. No preamble, no apologies, no narration."

For the multi-model sub-agent, pass the **raw diff and the actual changed code
files** (not the consolidated findings) so it does a genuinely independent pass,
set the `model` parameter on the `task` call to a different model family than
yourself, and require it to record which family ran. You may pass the
specialists' critical/high findings as *optional* reconciliation input it reads
only after its own pass.

## Example invocation pattern

```
1. git diff --stat origin/main...HEAD          → 12 files, +340/-87
2. git diff origin/main...HEAD                 → captured for sub-agents
3. Map files to areas                          → mostly Services + Commands + 1 doc
4. Fan out 7–8 task() calls in parallel        → skip #5 necessity if no new surface; retry any that died
5. Fan out task() #9 (diff+code, model override) → wait, record model family
6. Dedupe, sort, ID, reconcile multi-model, seed validation
7. Validate: build + run the published single-file binary (npm/MSIX only if the change needs it) + run changed cmds → confirm/drop crit+high
8. Print stdout report   (opt-in follow-through only if asked)
```

## Example consolidated stdout (feature PR)

```
PR Review — feat/sparse-trustedlaunch vs origin/main  (4 commits, 9 files, +312/-58)

Summary
  Critical: 0   High: 2   Medium: 4   Low: 1

Coverage
  security              ⚠ 1 finding
  correctness           ⚠ 1 finding
  cli-ux                ⚠ 1 finding
  alternative-solution  ✓ clean
  necessity-simplicity  ⚠ 1 finding
  test-coverage         ⚠ 1 finding
  docs-and-samples      ⚠ 2 findings
  packaging             ✓ clean
  multi-model           ✓ 2/2 high confirmed  ·  models: gpt
  regression            ✓ no change to existing flows
  validation            ⚠ 1/2 high validated at runtime; H2 static-only (no signing cert)

Findings
  H1  src/winapp-CLI/.../SparsePackageService.cs:142-160   security        Process.Start with manifest-derived path
  H2  src/winapp-CLI/.../Commands/CreateExternalCatalogCommand.cs:34-49  test-coverage   New command has no unit tests
  M1  src/winapp-CLI/.../SparsePackageService.cs:88-95     correctness     Manifest auto-detect skips Package.appxmanifest
  M2  src/winapp-CLI/.../Commands/CreateExternalCatalogCommand.cs:18-25  cli-ux          --catalog-name has no default; breaks scripted use
  M3  src/winapp-CLI/.../Commands/CreateExternalCatalogCommand.cs:1-60   necessity-simplicity  New top-level verb overlaps `package`; could be a `--external-catalog` flag
  M4  docs/usage.md (missing)                               docs-and-samples  New command not documented
  L1  plugins/winapp/agents/winapp.agent.md:189            docs-and-samples  Command list out of order

Details
## H1  src/winapp-CLI/WinApp.Cli/Services/SparsePackageService.cs:142-160
- Severity: high
- Confidence: high
- Validation: validated
- Domain: security
- Multi-model: confirmed
- Finding: Process.Start invokes makeappx.exe with a path read from the input manifest without validation.
- Evidence: Line 148 builds args via $"makeappx.exe pack /d {manifestPath} ..." where manifestPath comes from XElement.Attribute("Source").Value at line 131. Validate-phase red-team on the npm-installed CLI (cold cache): a manifest with Source='a b" /p x' made the extra /p flag reach makeappx.
- Recommendation: Validate manifestPath is a rooted, existing file path under the project root, and pass it via ProcessStartInfo.ArgumentList rather than concatenating into Arguments.

## H2  src/winapp-CLI/WinApp.Cli/Commands/CreateExternalCatalogCommand.cs:34-49
- Severity: high
- Confidence: high
- Validation: static-only (needs runtime confirmation)
- Domain: test-coverage
- Multi-model: confirmed
- Finding: New create-external-catalog command has no unit tests.
- Evidence: Command added at lines 34-49; no matching test in WinApp.Cli.Tests. Could not exercise end-to-end — needs a signing cert to run against a scaffolded package (see Test-setup handoff).
- Recommendation: Add a happy-path + error-path unit test; supply a dev cert to enable a runtime smoke test.

Coverage notes
  alternative-solution: Inspected new sparse-package code paths against
    AppxManifestDocument and ManifestHelper — uses both correctly.
  packaging: Inspected version.json, npm wrapper, NuGet targets — no impact.
```

## Example consolidated stdout (non-feature PR — bug fix)

A bug fix / refactor adds no new command, flag, or capability, so the
orchestrator **skips** necessity & simplicity and marks it `n/a`:

```
PR Review — fix/stream-empty-file vs origin/main  (2 commits, 3 files, +41/-12)

Summary
  Critical: 1   High: 0   Medium: 1   Low: 0

Coverage
  security              ✓ clean
  correctness           ⚠ 1 finding
  cli-ux                ✓ clean
  alternative-solution  ✓ clean
  necessity-simplicity  n/a (no new surface)
  test-coverage         ⚠ 1 finding
  docs-and-samples      ✓ clean
  packaging             ✓ clean
  multi-model           ✓ 1/1 critical confirmed  ·  models: gemini
  regression            ✓ fixes a regression; no new behavior change
  validation            ✓ 1/1 critical validated at runtime

Findings
  C1  src/winapp-CLI/.../StreamService.cs:77-84   correctness    Watch writes an empty file when the source never flushes
  M1  src/winapp-CLI/.../StreamService.cs:77-84   test-coverage  No regression test pins the non-empty-output fix

Details
## C1  src/winapp-CLI/WinApp.Cli/Services/StreamService.cs:77-84
- Severity: critical
- Confidence: high
- Validation: validated
- Domain: correctness
- Multi-model: confirmed
- Finding: On a cold cache the streaming watch still closes the output file before the first flush, producing a 0-byte file.
- Evidence: Reproduced on the npm-installed CLI (cold cache): `winapp ... --watch` left out.log empty until the fix at line 80 moved the flush ahead of Dispose.
- Recommendation: Keep the fix; add the regression test from M1 so the empty-file case can't silently return.
```

## Opt-in follow-through

Default behavior is unchanged: **stdout only, no fixes, no posting.** Only when
the user explicitly asks, offer these follow-ups:

- **Post an AI-labelled PR review comment.** On request, post the consolidated
  findings as a PR comment that **opens with a clear banner**, e.g.
  `> 🤖 AI-generated review (winappcli pr-review skill) — verify before acting.`
  Never post silently and never drop the banner.
- **Emit a test-setup / prerequisites handoff.** Turn every `static-only`
  finding into a checklist of what a human needs to validate it for real —
  signing certs, cloud / Azure credentials, specific hardware, a sample app,
  admin rights. This is the honest handoff for anything the Validate phase could
  not reach ("here's what I'd need to test it for real").
- **Open a follow-up fix PR — only on explicit request.** If (and only if) the
  user asks you to fix the findings, make the changes on a **new** branch and
  open a separate PR; never push fixes onto the branch under review.

## Output discipline

The final stdout report is the primary user-visible output — and, unless the
user opted into follow-through, the *only* one. Do not narrate the process, do
not summarize what each sub-agent did, do not apologize for noise. The Coverage
table already conveys what ran.
