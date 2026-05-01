# Developer skills

Skills in this directory are for **contributors working on the `microsoft/winappcli`
repository itself**. They are read by Copilot CLI (and other agents) to perform
repo-specific developer tasks like reviewing a PR before push.

> **Not the same as `.github/plugin/skills/`.** That directory contains the
> *shipped* `winapp` Copilot plugin skills, which help end users of the `winapp`
> CLI tool. Those are auto-generated from `docs/fragments/skills/` via
> `scripts/generate-llm-docs.ps1`. Skills under `.github/skills/` are
> hand-written and not shipped to end users.

## Available skills

| Skill | Purpose |
|-------|---------|
| [`pr-review/`](pr-review/SKILL.md) | Multi-dimensional review of a PR / feature branch diff (security, correctness, CLI UX, alternative solutions, tests, docs/samples, packaging, multi-model cross-check). Reports findings to stdout; does not apply fixes. |

## Conventions

- Each skill is a directory containing a `SKILL.md` (the entry point the
  orchestrating agent reads) and any supporting prompt fragments.
- Skills do not run scripts. The orchestrating agent uses its own tools
  (`task`, `grep`, `view`, `powershell` for git, etc.) following the
  instructions in `SKILL.md`.
- Prompt fragments meant to be passed verbatim to sub-agents live under a
  `dimensions/` (or similarly named) subfolder.
- Output goes to stdout unless the user explicitly asks for a file.
