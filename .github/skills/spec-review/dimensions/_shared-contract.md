# Shared output contract

Every dimension sub-agent must follow this output contract.

This is a **spec / design review**, not a code review. You are evaluating a
proposal *before* it is built. Your value comes from **independent research
against reality** — the actual `microsoft/winappcli` codebase, the Windows SDK
tools, Windows APIs, the build flow, and the wider ecosystem — **not** from
restating or trusting the spec's own claims. Verify load-bearing statements
yourself, and for anything *mechanical* **prefer a cheap experiment over a
code-read**: invoke the real tool and inspect its output, build a throwaway
project in a temp directory, or test a command's real behavior. You do not
implement the feature or touch the repo/spec — but running cheap, scoped
experiments in temp directories to confirm how things actually work is expected.

## Internal output

Sub-agent output is synthesis input. Metadata may stay internal, but the
orchestrator must rewrite surviving findings for a reader with no prior context.

Start with `# <dimension name>: <N> findings`, then
`Bottom line: <one-sentence assessment>`. Use this block for each finding:

```markdown
## <plain finding title>
- **Severity**: critical | high | medium | low
- **Confidence**: high | medium | low
- **Domain**: <dimension name>
- **Spec location**: <section or quoted claim>
- **What is wrong**: <the design defect>
- **Show me**: <smallest independent example; prefer input -> actual -> expected>
- **Why it matters**: <concrete user, delivery, or maintenance consequence>
- **Smallest fix**: <least-complex design change>
```

`Show me` must come from independent research, not the spec asserting itself.
Prefer an experiment you ran; otherwise cite authoritative docs or real
`path:line` evidence. The hierarchy is **experiment > authoritative vendor docs
> code-read > spec assertion (never sufficient alone)**. Keep experiments cheap,
scoped, and outside the repo tree. Define unavoidable jargon at first use. If a
claim is load-bearing but still unproven, show what is unknown and put the exact
closing experiment under `## Proofs required`; otherwise lower confidence or drop
an issue that cannot be demonstrated.

After findings, add only these internal sections when they have content:

```markdown
## Open decisions
- <decision that blocks or materially shapes implementation>

## Proofs required
- <unclosed assumption> — <specific experiment that would close it>

## What I checked
- <repo area, vendor source, or experiment independently examined>
```

## Junior-reader and signal-to-noise gate

Before emitting a finding, ask:

> Could a junior developer with no prior conversation understand what is wrong,
> see the evidence, understand the consequence, and apply the smallest fix after
> one read?

Also ask whether a senior maintainer would raise it in a design review or wave it
off as noise. If either test fails, rewrite it concretely or drop it.

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
- Load-bearing assumptions that are refuted, unproven, or hand-wavy.
- A materially simpler / safer / more idiomatic approach that exists.
- Real risks: compat/migration breakage, missing edge cases, release blockers.
- CLI UX / API incoherence users will trip over, or breaking changes.
- Unanswered questions that genuinely block implementation.

## Compatibility boundary

Backward compatibility starts at the latest supported published release, not an
earlier commit, review round, current PR implementation, or unreleased release
work. Before reporting a compatibility problem or proposing an alias, fallback,
migration path, legacy branch, or compatibility abstraction, identify:

1. The supported published version containing the behavior.
2. The public contract or persisted user data involved.
3. A real external consumer that would break.

If any is missing, prefer a clean replacement and do not emit a compatibility
finding. A preview contract counts only when the project publicly committed to
support it.

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

Confidence reflects how well your **independent research** grounds the finding,
following the evidence hierarchy (**experiment > authoritative docs > code-read >
spec assertion**).

- **high**: Proven empirically (you ran an experiment and observed the result) or
  verified directly against real code / authoritative vendor docs — you can cite
  exactly what you saw.
- **medium**: Partially verified; a code-read or docs plus some inference from
  repo/ecosystem context, without an experiment to close it.
- **low**: Plausible concern you could not fully confirm. Say what you could not
  verify and what experiment would close it. (An unverifiable-but-load-bearing
  spec assumption is worth surfacing at low/medium confidence rather than
  dropping.)
