# Shared output contract

Every dimension sub-agent must follow this output contract.

This is a **spec / design review**, not a code review. You are evaluating a
proposal *before* it is built. Your value comes from **independent research
against reality** — the actual `microsoft/winappcli` codebase, the Windows SDK
tools, Windows APIs, the build flow, and the wider ecosystem — **not** from
restating or trusting the spec's own claims. Verify load-bearing statements
yourself.

## Header line

Start with exactly one line:

```
# <dimension name>: <N> findings
```

Where `<dimension name>` is one of: `necessity-and-scope`,
`approach-and-alternatives`, `feasibility-vs-reality`,
`risks-unknowns-edge-cases`, `dx-and-user-impact`, `multi-model`.

## Bottom line

Immediately after the header, emit exactly one line:

```
Bottom line: <one-sentence assessment for this dimension>
```

This is required **even when you have zero findings** — a design review's job is
to reach a judgment, and "the approach is the simplest reasonable one; no better
alternative found" is a complete, valuable result. Do not manufacture a finding
just to fill space (see *No quotas* below).

## Per-finding block

Each finding is a level-2 heading anchored to the **part of the spec** it is
about, followed by labeled bullets:

```markdown
## <spec section / heading / quoted claim>
- **Severity**: critical | high | medium | low
- **Confidence**: high | medium | low
- **Domain**: <dimension name>
- **Finding**: <one-line statement of the concern>
- **Evidence**: <what your INDEPENDENT research found — cite real files as
  `path:line`, real tool/API behavior, repo patterns, or ecosystem facts.
  Do NOT cite the spec as evidence for itself.>
- **Recommendation**: <concrete next step — e.g. descope, stage, use existing
  helper X, prototype Y first, adopt alternative Z, answer question Q>
```

Notes:

- The anchor identifies the spec claim/section under review (e.g.
  `§Approach — "shell out to makeappx with --foo"`). Keep it short.
- **Evidence must come from reality, not the spec.** If you could not verify a
  claim, that is itself a finding (mark `Confidence: low` or `medium` and say
  what you could not confirm) — see `feasibility-vs-reality`.
- Emit discontiguous concerns as separate findings.

## Open questions

After the findings, include a section listing questions the spec does **not**
answer that must be resolved **before** implementation starts:

```markdown
## Open questions
- <a decision or unknown that blocks or materially shapes the build>
- <e.g., "Does Windows App SDK expose an API for X, or is a P/Invoke required?">
```

Omit the bullets if there are genuinely none. Do not pad.

## Trailing "what I checked" note

After the open questions, include:

```markdown
## What I checked
- <one bullet per area you independently researched, e.g. "Read
  MsixService.cs pack path to confirm makeappx invocation">
- <e.g., "Grepped Commands/ for an existing `store` subcommand">
- <e.g., "Checked Windows App SDK docs for a Share Target API">
```

This appears in the orchestrator's `Coverage notes` section so the reader can
see the depth of research behind the verdict — not just the verdict.

## The Team Lead Test (mandatory signal-to-noise gate)

Before emitting a finding, ask: *"Would a senior maintainer of this repo raise
this in a design review, or wave it off as noise?"* If you would wave it off,
do not emit it.

Specifically, **drop**:

- Bikeshedding on naming, wording, or formatting of the spec itself.
- Restatements of what the spec says without an independent judgment.
- Speculative hypotheticals not grounded in the actual code, APIs, or a real
  usage scenario.
- "Consider also supporting X" scope-creep suggestions (the point of this review
  is usually *less* speculative generality, not more).
- Concerns that only matter after a decision the spec explicitly defers.

**Keep**:

- The feature not fitting winapp's mission, or duplicating existing capability.
- Load-bearing assumptions that are false, unverified, or hand-wavy.
- A materially simpler / safer / more idiomatic approach that exists.
- Real risks: compat/migration breakage, missing edge cases, release blockers.
- CLI UX / API incoherence users will trip over, or breaking changes.
- Unanswered questions that genuinely block implementation.

## No quotas — a clean result is a valid result

There is **no expectation of finding problems.** If, after genuine independent
research, the design is sound on your dimension, say so in the `Bottom line`,
list what you checked, and emit zero findings. **Never manufacture a concern to
have something to report.** A confident, well-researched "proceed" is exactly as
valuable as a well-researched objection. Fabricated or padded findings actively
harm the review by burying the real signal.

## Severity guide

Severity here measures **how much this should affect the go / no-go decision**,
not code-level blast radius.

| Severity | Meaning |
|----------|---------|
| critical | Fundamental flaw: the feature shouldn't exist as scoped, a core assumption is false, or the approach cannot work. Blocks proceeding until resolved. |
| high     | Significant concern that should change the design *before* implementation (wrong approach, big risk, breaking change, materially better alternative). |
| medium   | Worth addressing but not a blocker; can be resolved during implementation, with a note. |
| low      | Minor suggestion; only emit if concrete and actionable. |

## Confidence guide

Confidence reflects how well your **independent research** grounds the finding.

- **high**: Verified directly against real code, tool/API behavior, or the build
  — you read it and cite it.
- **medium**: Partially verified; some inference from repo/ecosystem context.
- **low**: Plausible concern you could not fully confirm. Say what you could not
  verify. (An unverifiable-but-load-bearing spec assumption is worth surfacing at
  low/medium confidence rather than dropping.)
