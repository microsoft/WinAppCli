# Pipelines

| Pipeline | File | Trigger | Purpose |
|----------|------|---------|---------|
| WinDevCLI - CI | [`ci.yml`](ci.yml) | `main`, `dev/**` | Prerelease build and tests on every push. |
| WinDevCLI - Release | [`release.yml`](release.yml) | `rel/v*` | The real release: signs, publishes, and ships. |
| WinDevCLI - Release NuGet | [`release-nuget.yml`](release-nuget.yml) | After `release.yml`'s `Release_GitHub` stage | Pushes the signed `.nupkg` to nuget.org. Separate because it cannot run under CFSClean. |
| WinDevCLI - Release Dry Run | [`dryrun.yml`](dryrun.yml) | Weekly (Mon 09:00 UTC) | Rehearses a release without publishing anything. |
| WinDevCLI - Fuzz | [`fuzz.yml`](fuzz.yml) | Manual | Submits the OneFuzz job. |

Shared step templates live in [`templates/`](templates):

- [`build-env.yaml`](templates/build-env.yaml) — agent setup (.NET, Node, internal NuGet/npm feeds and their auth). Shared by CI, release and the dry run, so an ES policy change to feeds or auth is a one-file edit.
- [`build.yaml`](templates/build.yaml) — the build, packaging, optional ESRP signing, and artifact publishing.
- [`release-assets.yaml`](templates/release-assets.yaml) — renames built packages to the unversioned asset names the release publishes. Shared by `release.yml`'s `Release_GitHub` **and** the dry run, so the rehearsal runs the real renaming code rather than a copy of it.

> **Why templates and not scripts for release jobs.** 1ES release jobs
> (`templateContext.type: releaseJob`) cannot check out the repo, so no `.ps1` from the working
> tree is on disk there. A YAML `- template:` reference is fine regardless, because it is expanded
> at **compile** time — the steps are inlined into the job before it runs. So release-job logic
> *can* be shared, as long as it is shared as YAML rather than as a script file.

---

## Release dry run

### Why it exists

ES and 1ES policies change continuously. Today we find out which ones affect us **while a release is
in flight**, which is the most expensive possible moment: the release stalls, someone edits the
pipeline under time pressure, and we ship late or ship twice.

The dry run moves that discovery to a Monday morning. It performs a full release-fidelity build and
verifies every credential and service connection a release depends on, so a policy or credential
problem shows up as a red weekly run days before anyone cuts `rel/v*`.

### It is not another CI run

`ci.yml` builds a **prerelease** with `-SkipMsix`, leaves telemetry stubbed, and never touches a
release credential. The dry run covers the gap:

| | CI | Dry run | Release |
|---|---|---|---|
| Build configuration | prerelease | **stable** | stable |
| Telemetry unstubbed (`Unstub.ps1`) | no | **yes** | yes |
| MSIX / NuGet / npm packages | partial | **yes** | yes |
| Unit + script test suites | yes | yes | yes |
| Release notes generation | no | **yes, as a hard gate** | yes, `continueOnError` |
| MS Learn doc porting + validation | no | **yes** | yes |
| Release asset renaming verified | no | **yes** | yes (implicitly) |
| Credential / service connection checks | no | **yes** | n/a |
| ESRP signing | no | no | yes |
| Publishing (GitHub, npm, NuGet, WinGet, symbols) | no | no | yes |

### Why a separate pipeline instead of a dry-run switch on the release?

The obvious alternative is to add a `dryRun` option to `release.yml` and schedule that. It was
considered and rejected. Four reasons, roughly in order of how badly each fails:

1. **A schedule cannot pass parameters.** Azure DevOps cron triggers always compile with the YAML
   *default* values of `parameters:` — and so do branch triggers. So a `dryRun` parameter cannot
   distinguish "weekly rehearsal" from "real release": defaulting it to `false` makes the weekly
   run a **real release off `main`**, and defaulting it to `true` makes every `rel/v*` release
   silently publish nothing. The mode would have to be inferred from `Build.SourceBranch`, which
   is a weaker guarantee for a far higher stake.
2. **The publish stages are not all gated.** `Release_WinGet` (`release.yml:415`) and
   `Release_MSLearn` (`release.yml:499`) have no parameter gate at all — they run on
   `dependsOn: [Build, Release_GitHub]`. A dry-run flag that only skipped `GitHubRelease@1` would
   still submit to WinGet and open a docs PR. `EsrpRelease@10` is additionally `condition: always()`
   (`release.yml:396`).
3. **1ES classification is per pipeline registration, not per run.** `Azure/azure-sdk-for-net`'s
   `1es-redirect.yml` warns that *"Even conditional usage of CFSClean causes the pipeline to be
   classified as always CFSClean in the backend."* This repo already hit the same wall: #750 split
   nuget.org into its own pipeline precisely because isolation policy could not be scoped to one
   stage. Conditionally choosing Official vs Unofficial inside one registration invites the same
   class of problem, with the production release on the losing side.
4. **`release-nuget.yml` triggers off this pipeline's `Release_GitHub` stage** for `rel/v*`
   branches. Sharing a definition adds a new way to reach the nuget.org publish path. Today the
   dry run is simply invisible to that trigger.

There is also a security argument: two registrations let the dry run's service connections be
Branch-controlled to `main` while the release's stay on `rel/v*`. One registration must permit both.

**What is shared anyway.** The objection to a separate pipeline is duplication, and that is
addressed directly rather than structurally: `build-env.yaml`, `build.yaml` and
`release-assets.yaml` are consumed by both, so the build, the packaging and the asset renaming are
literally the same code. What remains separate is the part that *should* be — publishing versus
verifying.

### What it validates

**Build fidelity**

- The full stable build for x64 and ARM64, including every test suite `build-cli.ps1` runs.
- `Unstub.ps1`, which rewrites the csproj to pull the internal `Microsoft.Telemetry.Inbox.Managed`
  package. Nothing else exercises this, and it depends on the internal feed staying reachable
  under CFSClean.
- Package restore under the same `networkIsolationPolicy: Permissive,CFSClean` the release uses.
- MSIX, NuGet and npm packaging.

**Release-only steps that normally run once per release**

- `generate-release-notes.ps1`, against the real GitHub and GitHub Models APIs. The release runs
  this with `continueOnError: true`, so a dead token silently produces an empty changelog; here it
  is a hard failure and the output is asserted to be non-empty.
- `port-mslearn-docs.ps1` and `validate-mslearn-docs.ps1`. Doc porting has blocked releases before
  (#685, #777) because it only ever runs at release time against a week of accumulated doc edits.
  The dry run runs them **from a staged copy that mirrors release.yml's `mslearn-source` file
  list**, not from the checkout, so a script added as a dependency but forgotten in that copy list
  fails here rather than during the release.
- Release asset renaming, by running **the same `release-assets.yaml` template the release runs**,
  then asserting the result with `scripts/verify-release-assets.ps1`. The names are load-bearing:
  the WinGet submission is handed a fixed-length, fixed-order URL list built from them, and the
  documented download links hardcode them. A rename miss caused #568.
- Archiving the CLI binaries into `winappcli-x64.zip` and `winappcli-arm64.zip`, asserting both
  architectures produced a `winapp.exe`. These are release assets too, and the WinGet manifest
  hardcodes their URLs.
- The .NET 6 runtime install the ESRP task depends on. Installing a runtime signs nothing, so the
  dry run does it to catch a policy or download failure affecting it.

**Credentials and service connections** (`scripts/check-release-credentials.ps1`, read-only)

- The GitHub PAT: that it authenticates, its scopes, its **expiry** (warns 21 days out), and its
  remaining rate limit.
- Push permission on the winget-pkgs fork and the windows-dev-docs-pr fork, read from the API.
  Fork values are also format-checked even when the token is dead, so a bad variable group value
  is not masked by an unrelated failure.
- The **GitHub Models token**, via a one-token inference call against the same model
  `generate-release-notes.ps1` uses. This matters more than it looks: when Models is unreachable
  the generator silently falls back to a raw commit list, and the release runs it with
  `continueOnError`, so nothing else in the system would ever tell you.
- Existence and readiness of `github-service-connection`, the ESRP signing connection, the symbol
  publishing connection, and `NuGet-WinAppCLI`. **This proves the connection exists and is
  authorized for the pipeline — it does not authenticate the credential stored inside it.**
- The symbol publishing federated identity, by acquiring the same
  `api://30471ccf-0966-45b9-a979-065dbedb24c1` token the release acquires — and then not using it.
  This one *is* a real credential test.
- The pinned `gh` CLI download and its SHA-256, which both release stages depend on.
- WinGet manifest generation via `wingetcreate`, run against the **last published release** with no
  `--submit`, so the tool, the installer downloads and the manifest schema are all exercised.
- Optionally (`RunEsrpCertificateCheck`, off by default) ESRP Key Vault certificate expiry.

### What it deliberately does not do

- **No ESRP signing.** Artifacts are unsigned; the dry run produces nothing shippable.
- **No GitHub release.** Creating and deleting one would notify every watcher for no reason.
- **No fork mutations.** The forks are never synced, cloned or pushed to. Only the token's scopes
  and its push permission are read over the API.
- **No publishing at all** — no symbol upload, no nuget.org push, no npm publish, no WinGet
  submission.
- **No sample/guide tests.** Those run in GitHub Actions (`.github/workflows/test-samples.yml`),
  where the toolchain downloads they need (Flutter, Rust, Inno Setup, PSGallery) are reachable.
  CFSClean sinkholes those sources, so running them here would fail for the wrong reason.

### Limitations worth knowing

Because it produces no shippable output, the dry run extends
`1ES.Unofficial.PipelineTemplate.yml`. Unofficial enforces a **weaker SDL subset** than the Official
template the release uses. PoliCheck runs in both, but TSA upload, CodeQL TSA integration, SBOM
enforcement and signing validation are Official-only.

**A policy change confined to those Official-only gates will not be caught here.** The dry run
covers build, dependency, credential and release-step failures — the categories that have actually
blocked our releases — not the full compliance chain.

Other things a green run does **not** prove:

- **ESRP signing works.** Nothing here signs. The optional certificate probe
  (`RunEsrpCertificateCheck`) only reads Key Vault metadata, and it is off by default because
  `$(SigningServiceName)` may be an ESRP-typed endpoint that `AzurePowerShell@5` cannot consume.
  Enable it once a manual run confirms the task binds, and it becomes a real gate.
- **`github-service-connection` and `NuGet-WinAppCLI` credentials are valid.** Those two are only
  checked for existence and readiness; nothing authenticates the secret inside them without
  publishing, which is out of scope by design.
- **Every release job step runs.** Asset renaming is genuinely shared (`templates/release-assets.yaml`),
  so that one *is* the real code. But the publish steps themselves — `GitHubRelease@1`,
  `EsrpRelease@10`, `wingetcreate --submit`, `PublishSymbols@2` — are never executed. For those the
  dry run reproduces the *contract* they depend on (asset names exist and are correctly named, docs
  port cleanly from the staged layout, credentials authenticate), not the publish call itself.

### Security note

The credential stage hands the release PAT, `System.AccessToken`, and authorized Azure service
connections to whatever is checked out. A YAML `condition:` is **not** a security control, because
the branch being run can edit it.

> [!WARNING]
> These service connections are **shared with the real release**, which runs from `rel/v*`.
> A Branch control check limited to `refs/heads/main` would therefore **block real releases.**
> Any branch filter you add must include **both** `refs/heads/main` (for the dry run) and
> `refs/heads/rel/v*` (for the release).

Two ways to restrict this properly, best first:

1. **Provision dry-run-only identities.** A separate GitHub PAT with read-only scopes and a
   separate read-only Azure identity, exposed as their own variable group. Then the dry run never
   holds credentials that can publish anything, and branch filters stay independent.
2. **Share the connections but widen the filter.** Add a Branch control check allowing
   `refs/heads/main` and `refs/heads/rel/v*`, and nothing else. Weaker, because the dry run still
   holds publish-capable credentials.

For any other branch, run with `RunCredentialChecks: false`. Every secret-bearing step in the
pipeline — the credential checks, release-notes generation, the symbol-identity probe, the ESRP
probe, and the token used by the WinGet rehearsal — hangs off that single parameter, so turning it
off yields a genuinely credential-free build validation.

### One-time ADO setup

The pipeline is not self-provisioning. To stand it up:

1. Create a new pipeline in the `Windows Developer CLI` folder pointing at `.pipelines/dryrun.yml`.
   Name it **WinDevCLI - Release Dry Run**.
2. Link the same variable group(s) the release pipeline uses. The dry run reads:
   `TelemetryProviderGuid`, `GITHUB_TOKEN_2`, `GH_MODELS_TOKEN`, `WingetPkgsFork`,
   `MSLearnDocsFork`, `SigningServiceName`, `SigningAKVName`, `SigningAuthCertName`,
   `SigningSignCertName`, `SymbolPublishingServiceConnection`.
3. Authorize the pipeline to use the service connections it references. Referencing them through
   runtime variables means ADO cannot grant access automatically.
4. Restrict those connections — see the security note above. **Do not limit them to
   `refs/heads/main`:** they are shared with the release, which runs from `rel/v*`, so a
   main-only filter would block real releases. Either provision dry-run-only read-only
   identities, or allow both `refs/heads/main` and `refs/heads/rel/v*`.
5. Leave the schedule as defined in YAML. Make sure **"Override the YAML schedule"** is off in the
   pipeline's UI settings, or the weekly trigger will not fire.

`always: true` on the schedule is deliberate: without it a week with no commits produces no run,
and a quiet week is exactly when an external policy change slips in unnoticed.

#### Testing it before merging to `main`

You do not have to merge first. When creating the pipeline, the **"Existing Azure Pipelines YAML
file"** dialog has a **branch selector above the file picker** — switch it to the feature branch
and the file becomes selectable. If the wizard skips that step, create the pipeline and then set
**Settings → "Default branch for manual and scheduled builds"** to the feature branch.

Manually queue it from there to shake the run out. Two caveats:

- The **schedule will not fire** until the YAML is on a branch matching the schedule's
  `branches: include` list (`main`). Manual queueing is how you test before then.
- Point that default-branch setting back at `main` once merged, or the weekly run keeps building
  a stale branch.

### Triaging a failure

| Failing step | Usually means |
|---|---|
| Build CLI | An internal feed, package or SDK dependency changed, or a real code break. Compare against the last green CI run to tell them apart. |
| Replace Stubbed Files | The internal telemetry package or its feed permissions changed. **CI will not catch this** — only release and dry run run it. |
| Generate Release Notes | `GITHUB_TOKEN_2` or `GH_MODELS_TOKEN` expired or lost access. |
| Check release credentials → GitHub Models | The Models token or model is gone. **The release will not fail on this** — it silently ships a raw commit list instead of AI notes. |
| Rehearse release asset renaming / Verify release asset names | Package naming changed, or an architecture stopped building. The WinGet submission and the documented download URLs will break. Fix `templates/release-assets.yaml` — it is the single copy the real release uses too. |
| Rehearse MS Learn doc porting | A doc edit landed that the porting or validation script rejects, **or** a script it needs is missing from the `mslearn-source` copy list in `release.yml`. |
| Check release credentials | Read the PASS/WARN/FAIL summary at the end of the log. `FAIL` is definitive; `WARN` means the check could not determine an answer. |
| Verify symbol publishing identity | The federated credential or its Entra app registration changed. |
| Verify pinned gh CLI download | The `gh` release asset was retagged or the hash is stale. Update `GhCliUrl`/`GhCliSha256` in **both** `release.yml` and `dryrun.yml`. |
| Rehearse WinGet manifest generation | `wingetcreate` or the manifest schema changed. Advisory — it does not fail the run. |

### Running it on demand

Queue it manually from ADO. Four parameters let you narrow the run:

- `RunCredentialChecks` — **set this to false when running any branch other than `main`.** It gates
  every secret-bearing step in the pipeline, leaving a pure build validation that is safe to run
  from anywhere. Note this also skips release-notes generation, which needs the GitHub tokens.
- `RunWinGetManifestCheck` — skip to avoid the installer downloads.
- `RunEsrpCertificateCheck` — enable to probe ESRP Key Vault certificate expiry.
- `TreatWarningsAsErrors` — fail on warnings too, e.g. a token nearing expiry.

The credential script also runs locally:

```powershell
$env:GH_TOKEN = 'ghp_...'
$env:GH_MODELS_TOKEN = 'ghp_...'
.\scripts\check-release-credentials.ps1 `
    -WingetPkgsFork '<owner>/winget-pkgs' `
    -MSLearnDocsFork '<owner>/windows-dev-docs-pr'
```

Service connection checks are skipped outside Azure Pipelines. Add `-SkipModelsCheck` to run fully
offline.

### Related

- `scripts/check-release-credentials.ps1` and `scripts/tests/check-release-credentials.Tests.ps1`
- `scripts/verify-release-assets.ps1` and `scripts/tests/verify-release-assets.Tests.ps1`
- `.pipelines/templates/release-assets.yaml` — the renaming itself, shared with the real release

Both test suites run as part of `scripts/build-cli.ps1`.

Note that the renaming logic has **no** unit test, deliberately. It lives in exactly one place that
the real release runs, so there is no second copy to drift; the weekly dry run executes it against
real build output and then verifies the result, which is stronger evidence than a unit test over a
duplicate would be.

> **Planned (phase 2):** a scheduled agent prompt that reads the weekly run and posts a summary to
> the team. It will be documented alongside this file once the pipeline has a few runs behind it.
