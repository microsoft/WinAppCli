# CLI UX

Apply `_shared-contract.md`. Set `Domain: cli-ux`.

You own the surface users type. Judge new commands and options against **this
repo's established conventions**, not general CLI taste — a new command that is
merely different from what you would have designed is not a finding.

## Conventions a new command must match

- **Naming.** kebab-case (`--use-defaults`), `--no-<flag>` for negation. `-a` and
  `-w` are reserved for app / window targeting — do not let a new command take
  them for anything else. Preserve an alias only when it passes the shared
  compatibility gate; an alias introduced only during development is not
  precedent.
- **Defaults over required options.** `--cert-password` defaults to `password`
  for dev certs; `--manifest` auto-detects. A new **required** option that could
  have had a default breaks scripted use — that is `high`.
- **Log-level semantics.** Flag any new command that violates these:

  | Flag | Minimum level |
  |---|---|
  | *(default)* | Information |
  | `--verbose` | Debug |
  | `--quiet` | Warning |
  | `--json` | None — machine-readable only, no log lines in the stream |

- **Non-interactive support.** Every new prompt must be skippable via
  `--use-defaults` with a sensible default, or it blocks CI.
- **Exit codes.** Non-zero on user-actionable failure. A silent `return` that
  leaves exit code 0 after an error is a real bug, not polish.
- **Placement.** New subcommands slot under the right parent (`ui`, `manifest`,
  `cert`, `node`, `tool`, `store`) and appear in `--help`.
- **Schema round-trip.** New options must serialize through `--cli-schema`.
  Custom argument types without a binder will not — flag those.
- **`run` `--property` / `-p` arity.** Must stay `ArgumentArity.ZeroOrMore`. With
  `OneOrMore`, a valueless `-p` produces a plain-text parser error that breaks
  `--json` output.

## Not findings

Renaming suggestions without a concrete UX impact, color and emoji choices,
alignment polish, or anything a user would discover from `--help`.
