# winapp devtools

Home for the **DevTools** workstreams — the CLI-driven capabilities for inspecting and editing running
Windows app UIs. Each workstream has a spec under `specs/winapp-devtools-*.md`.

## What's here

| Area | Workstream | Status |
|---|---|---|
| `provenance/` + `census/` | **W4 — source mapping / provenance** ("which line of source produced this live element?") | analysis layer landed (pure `net10.0`, CI-runnable) |

See `provenance/` for the confidence-graded source-mapping logic and `census/README.md` for the Gate-1
resolution census. Everything here is pure `net10.0` (no WinUI / no `-windows` TFM) so it builds and
tests on a hosted runner without a desktop; the live-app collection half is a documented heavy gate.

## Build & test

```powershell
dotnet build src/winapp-devtools/devtools.slnx -c Release
dotnet test  src/winapp-devtools/devtools.slnx
```

The `devtools-provenance.yml` workflow builds Release, runs the unit tests + the standing Gate-1 check,
re-derives the published census rates, and enforces the public-repo scrub (`scripts/scrub-scan.ps1`).
