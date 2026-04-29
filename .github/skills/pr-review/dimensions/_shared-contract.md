# Shared output contract

Every dimension sub-agent must follow this output contract.

## Header line

Start with exactly one line:

```
# <dimension name>: <N> findings
```

Where `<dimension name>` is one of: `security`, `correctness`, `cli-ux`,
`alternative-solution`, `test-coverage`, `docs-and-samples`, `packaging`,
`multi-model`.

## Per-finding block

Each finding is a level-2 heading followed by labeled bullets:

```markdown
## <relative file path>:<start_line>-<end_line>
- **Severity**: critical | high | medium | low
- **Confidence**: high | medium | low
- **Domain**: <dimension name>
- **Finding**: <one-line statement of what is wrong>
- **Evidence**: <specific code evidence — quote 1-3 lines, cite line refs in the diff>
- **Recommendation**: <concrete actionable next step>
```

Notes:

- File paths are relative to the repo root (no leading `./`).
- Line numbers refer to the **post-change** file (the right side of the diff).
  For `working` / `staged` / `all` scopes this means the working-tree or staged
  state, not a committed version.
- For findings that span discontiguous regions, emit them as separate findings.

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
