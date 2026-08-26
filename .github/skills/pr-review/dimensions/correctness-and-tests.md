# Does it work? (correctness + tests)

You own two questions for the `microsoft/winappcli` repo: **is this code
correct**, and **would we know if it broke?**

Owning both means *you* decide when a gap is worth a test. A bug you would not
bother testing is not a coverage finding — say so and move on.

Apply `_shared-contract.md`. Set `Domain: correctness`.

You already know how to spot null derefs, races, off-by-one, deadlocks from
`.Result`, and swallowed exceptions. That is not written down here. What follows
is what you cannot know without this repo.

## Repo gotchas

- **Logger calls below `Information` bypass injected writers.** Debug and Info go
  through static `AnsiConsole` and are invisible to test capture. A new test
  asserting on Debug/Info output via an injected `TextWriter` will pass or fail
  for the wrong reason.
- **`EnablePosixBundling=false`** in `WinAppParserConfiguration.Default`. New
  commands that rely on POSIX flag bundling (`-abc`) will misbehave.
- **Manifest discovery has a precedence order** (`Package.appxmanifest`, then
  `appxmanifest.xml`) owned by `ManifestHelper` /
  `MsixService.FindManifestInDirectory`. Code that re-implements discovery drifts
  from the NuGet targets' `WinAppManifestPath` contract.
- **`RequireExistingConfig` runs treat zero packages as a friendly no-op.** New
  paths must not assume at least one package is installed.
- **`UiSessionInfo.IsExplicitWindow`** controls whether `inspect` / `search` /
  `find` expand beyond the target window. New UI commands must respect it.
- **UI selectors are slugs or plain text.** An ambiguous match must throw with a
  slug list, never silently pick one.
- **Cleanup on the failure path.** Processes, file handles, COM objects, and
  registered appx packages must be released when a command fails — a leaked
  registration breaks the *next* run, which is how these bugs reach users.

## Regression

Say what changes for existing users and existing flows. A gate that was dead and
now fires is a behavior change: state whether it will start failing flows that
used to pass. Lead `What is wrong` with `Regression:`.

## Tests

- **Unit:** `src/winapp-CLI/WinApp.Cli.Tests/` — **MSTest** (`MSTest.Sdk`), with
  internals exposed via `InternalsVisibleTo`. Test methods run in **parallel**;
  anything mutating process or machine state needs `[DoNotParallelize]`.
- **Samples:** `samples/<name>/test.Tests.ps1` (Pester 5.x). Conventions and
  shared helpers are documented in `AGENTS.md` — do not restate them, just flag
  deviations. A new `samples/<name>/` also needs a matrix entry in
  `.github/workflows/test-samples.yml`.
- A new public command with **no test at all** is `high`. Past that, ask whether
  a test would have caught a real bug; if not, skip it.
- Tests that hit the network, install certs, or register packages **without
  cleanup** are `high` — they break CI for everyone.
