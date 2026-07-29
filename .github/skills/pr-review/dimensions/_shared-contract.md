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

## The gut check (mandatory, applies to every finding you emit)

The Team Lead Test asks whether a finding is *noise*. This one asks the harder
question: **is it necessary, or merely true?**

You are a specialist, and a specialist told to find problems will find some.
Everything you are about to report is defensible. That is the trap — the author
reads nine defensible findings as nine required changes, and the PR grows to
satisfy the list. Real reviewers discard most of what they notice.

For each finding, ask:

> Would a busy maintainer, looking at a PR that is otherwise ready to ship,
> genuinely want this changed — or is this just a true statement about the code?

**Drop it** if:

- The code works and you are describing a tidier alternative.
- The fix adds more complexity than the problem costs users. Say YAGNI instead.
- It guards against something that cannot happen here — input the CLI controls,
  a state the caller guarantees, a config the repo does not support.
- It is a "for completeness" / "for consistency" item with no user-visible
  consequence: the symmetric flag nobody asked for, the parallel test for a case
  that cannot regress, the doc section restating the help text.
- You cannot finish the sentence *"a user doing X will hit Y."*

**Never drop** a security issue, data loss, a crash, wrong output, or a broken
install — this gate removes polish and speculation, not real defects.

Then, for the findings that survive, write the **smallest** recommendation that
resolves each one — not the most thorough or most general. Try the subtractive
fix first: delete the unused branch, drop the option, collapse the second code
path, reject the input instead of handling it, or document the limitation. If
your recommendation adds a new command / flag / option / service / interface /
abstraction (`Fix cost: large`), say in one clause why a smaller fix will not
work; if you cannot, propose the smaller fix instead. Never recommend
speculative generality — no "make it pluggable," no interface for a second
implementation that does not exist.

Two precise findings beat eight thorough ones.

## No quotas — a clean result is a valid result

No dimension is required to produce a finding. Zero findings is a legitimate,
valuable outcome that tells the developer this area is solid. **Never invent,
inflate, or lower the bar on a finding just to avoid an empty report** — a
manufactured finding fails the Team Lead Test by definition. When you have
nothing to flag, say so and record what you checked in `## What I checked`.

## Re-review rounds

If the orchestrator tells you the branch has already been reviewed once:

- **Raise the bar.** Emit only `critical` and `high` findings. Medium and low
  observations belong in `## What I checked` as a one-line note, not as findings
  the author feels obliged to fix.
- **Code added in response to an earlier review is fair game.** It is *not*
  settled design. If a previous recommendation produced an option, abstraction,
  or code path that is not earning its keep, say so — a `subtractive` finding
  recommending its removal is one of the most valuable things you can return.
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
