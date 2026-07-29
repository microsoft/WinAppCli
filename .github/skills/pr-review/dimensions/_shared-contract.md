# Shared output contract

## Output

Start with one line: `# <dimension>: <N> findings`

Then one block per finding:

```markdown
## <path>:<start_line>-<end_line>
- **Severity**: critical | high | medium | low
- **Confidence**: high | medium | low
- **Validation**: static-only (needs runtime confirmation)
- **Domain**: <dimension>
- **Finding**: <what is wrong, one line>
- **Evidence**: <quote 1-3 lines from the diff, cite line refs>
- **Recommendation**: <smallest concrete fix>
```

Paths are repo-relative; line numbers refer to the post-change file. Always emit
`Validation: static-only` — the orchestrator promotes findings to `validated`
after actually running the code.

End with a `## What I checked` section, one bullet per area inspected. This is
how the developer sees scope, not just verdict.

## The bar

Before emitting anything, ask:

> Would a busy maintainer, looking at a PR that is otherwise ready to ship,
> genuinely want this changed — or is this merely a true statement about the
> code?

Emit it only if you can finish the sentence **"a user doing X will hit Y."**

Drop it if:

- The code works and you are describing a tidier alternative.
- The fix adds more complexity than the problem costs users.
- It guards against something that cannot happen here — input the CLI controls,
  a state the caller guarantees, a config the repo does not support.
- It is a "for completeness" / "for consistency" item with no user-visible
  effect.
- The compiler, the analyzers, or `EnforceCodeStyleInBuild` already catch it.
  Style, naming, and formatting are never findings.

**Never drop** a security issue, data loss, a crash, wrong output, or a broken
install. This bar removes polish and speculation, not defects.

**There is no quota, and zero findings is a good result.** Two precise findings
beat eight thorough ones. Never invent one to avoid an empty report.

## Recommendations

Write the **smallest** fix that resolves the finding, not the most thorough.

Try the subtractive fix first: delete the branch, drop the option, reject the
input, collapse the second code path, or document the limitation. If your fix
adds a new command / flag / service / abstraction, say in one clause why a
smaller one will not do — if you cannot, propose the smaller one instead. Never
recommend speculative generality.

## Severity

| | |
|---|---|
| critical | Breaks users, corrupts data, leaks secrets, or blocks release |
| high | Real bug, real security or UX issue, real coverage gap |
| medium | Worth fixing, not a blocker |
| low | Only if the improvement is concrete and actionable |

Confidence is **high** when cause and effect are both visible in the diff,
**medium** when one half is inferred from repo context you read, **low** when the
pattern matches a known issue but key elements are unverifiable. Security
findings are never suppressed for low confidence.

## Re-reviews

If told this branch was already reviewed: emit critical and high only, and treat
code added in response to earlier review comments as **re-openable — not settled
design**.
