# Shared output contract

Every dimension sub-agent must follow this output contract.

## Header line

Start with exactly one line:

```
# <dimension name>: <N> findings
```

Where `<dimension name>` is one of: `security`, `correctness`, `cli-ux`,
`alternative-solution`, `necessity-and-simplicity`, `test-coverage`,
`docs-and-samples`, `packaging`, `multi-model`.

## Per-finding block

Each finding is a level-2 heading followed by labeled bullets:

```markdown
## <relative file path>:<start_line>-<end_line>
- **Severity**: critical | high | medium | low
- **Confidence**: high | medium | low
- **Validation**: static-only (needs runtime confirmation) | validated
- **Domain**: <dimension name>
- **Finding**: <one-line statement of what is wrong>
- **Evidence**: <specific code evidence — quote 1-3 lines, cite line refs in the diff>
- **Fix cost**: subtractive | small | medium | large
- **Recommendation**: <concrete actionable next step>
```

Notes:

- File paths are relative to the repo root (no leading `./`).
- Line numbers refer to the **post-change** file (the right side of the diff).
  For `working` / `staged` / `all` scopes this means the working-tree or staged
  state, not a committed version.
- For findings that span discontiguous regions, emit them as separate findings.
- **Validation** starts as `static-only (needs runtime confirmation)` for every
  finding you emit — you are reading the diff, not running it. The orchestrator
  flips it to `validated` in the Validate phase when a runtime check confirms
  the finding, and drops the finding if a runtime check refutes it.
- **Fix cost** describes what your *recommendation* would add to the codebase,
  not how bad the bug is:

  | Fix cost | Meaning |
  |----------|---------|
  | subtractive | Resolved by removing or narrowing code, deleting an option, collapsing two paths into one, or documenting a limitation. Adds nothing. |
  | small | Under ~20 lines inside existing files. No new file, type, or public surface. |
  | medium | A new test file, a new method group, or a new internal type. |
  | large | A new command / flag / option / service / interface / abstraction, or any new public surface. |

## Trailing "what I checked" note

After the findings (or in place of them when there are zero), include:

```markdown
## What I checked
- <one bullet per area inspected, e.g., "All new methods in MsixService.cs">
- <e.g., "Process.Start call sites added in CertCommand.cs">
- <e.g., "appxmanifest.xml writes via XDocument vs regex">
```

This appears in the orchestrator's `Coverage notes` section so the developer
can see scope, not just verdict.

## The Team Lead Test (mandatory signal-to-noise gate)

Before emitting a finding, ask: *"Would a senior maintainer of this repo keep
this comment in a PR review, or delete it as noise?"* If you would delete it,
do not emit it.

Specifically, **drop**:

- Style, formatting, brace placement, naming preferences (analyzers cover these).
- Suggestions to "consider adding a comment" without a substantive reason.
- Speculative hypotheticals not grounded in the diff.
- Restatements of what the code does.
- Anything the C# compiler, `EnforceCodeStyleInBuild`, or repo analyzers
  already flag (this repo treats warnings as errors in Release).

**Keep**:

- Bugs, logic errors, race conditions, missed edge cases.
- Security issues (never suppressed, even at low confidence).
- API/UX inconsistencies users will notice.
- Coverage gaps with concrete impact.
- Doc/sample/packaging drift caused by this change.

## The Complexity Budget Test (mandatory, applies to your recommendation)

The Team Lead Test governs whether a *finding* is worth reporting. This test
governs the *fix you propose*. A review that only ever says "add X" turns every
round into net growth; over several rounds that is how a focused feature becomes
an over-engineered one. Your recommendation is part of the design, so it carries
the same burden the diff does.

Before writing `Recommendation`, do all three:

1. **Propose the smallest fix that actually resolves the finding.** Not the most
   thorough, not the most general, not the one that also handles the adjacent
   case you imagined. The smallest one.
2. **Try the subtractive fix first.** Many findings are better resolved by
   removing than by adding — delete the unused branch, drop the option nobody
   asked for, collapse the second code path, reject the input instead of
   handling it, or document the limitation in `docs/usage.md` and move on. If a
   subtractive fix exists, lead with it, even if an additive fix is "better."
3. **Justify any `large` fix cost explicitly.** If your recommendation adds a
   new command / flag / option / service / interface / abstraction, the
   `Recommendation` must say in one clause why a `subtractive` or `small` fix
   does not work. A `large` recommendation with no such justification fails this
   test — downgrade it to the smaller fix or drop the finding.

Never recommend speculative generality: no "make this pluggable," no "extract an
interface for future providers," no config knob without a user who asked for it.
"YAGNI" is a valid and complete recommendation.

## No quotas — a clean result is a valid result

No dimension is required to produce a finding. Zero findings is a legitimate,
valuable outcome that tells the developer this area is solid. **Never invent,
inflate, or lower the bar on a finding just to avoid an empty report** — a
manufactured finding fails the Team Lead Test by definition. When you have
nothing to flag, say so and record what you checked in `## What I checked`.

## Re-review rounds (round 2 and later)

The orchestrator will tell you when the branch has already been through one or
more review rounds, and may give you the previous round's findings. On a
re-review:

- **Raise the bar.** Emit only `critical` and `high` findings. Medium and low
  observations belong in `## What I checked` as a one-line note, not as findings
  the author feels obliged to fix.
- **Code added in response to a previous review is fair game.** It is *not*
  settled design. If a prior round's recommendation produced a new option,
  abstraction, or code path that is not earning its keep, say so — a
  `subtractive` finding recommending its removal is one of the most valuable
  things you can return.
- **Do not restate findings the author already addressed.** Confirm the fix and
  move on.

## Severity guide

| Severity | Meaning |
|----------|---------|
| critical | Will break users, corrupt data, leak secrets, or block release. Must fix before merge. |
| high     | Real bug, real security/UX issue, or real coverage gap. Should fix before merge. |
| medium   | Worth fixing but not a blocker; may be deferred with a note. |
| low      | Minor improvement; only emit if the improvement is concrete and actionable. |

## Confidence guide

- **high**: Full chain visible in the diff (cause + effect both present).
- **medium**: One half visible; the other half inferred from repo context you read.
- **low**: Pattern resembles a known issue but key elements not verifiable.

Security findings are **never** suppressed by low confidence — emit them anyway.
