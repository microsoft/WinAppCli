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
- Release asset renaming, via `scripts/stage-release-assets.ps1 -Verify`. It asserts the build still
  produces exactly `winappcli_x64.msix`, `winappcli_arm64.msix`, `microsoft-winappcli.tgz` and
  unversioned `.nupkg` names — the names the WinGet manifest URLs and the install instructions
  hardcode. A rename miss here caused #568.
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
- **The release job steps themselves run.** 1ES release jobs cannot check out the repo, so their
  logic is inline in `release.yml`. The dry run reproduces the *contract* those steps must satisfy
  (asset names, doc porting from the staged layout), not the inline code itself.

### Security note

The credential stage hands the release PAT, `System.AccessToken`, and authorized Azure service
connections to whatever is checked out. A YAML `condition:` is **not** a security control, because
the branch being run can edit it.

Restrict this at the ADO level: add a **Branch control** check on each service connection used
here, limited to `refs/heads/main`. Run other branches with `RunCredentialChecks: false`, which
skips every credential-bearing step and leaves a pure build validation.

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
4. Add a **Branch control** check on each of those service connections, limited to
   `refs/heads/main`. See the security note above — without it, any branch that can queue this
   pipeline gets the release credentials.
5. Leave the schedule as defined in YAML. Make sure **"Override the YAML schedule"** is off in the
   pipeline's UI settings, or the weekly trigger will not fire.

`always: true` on the schedule is deliberate: without it a week with no commits produces no run,
and a quiet week is exactly when an external policy change slips in unnoticed.

### Triaging a failure

| Failing step | Usually means |
|---|---|
| Build CLI | An internal feed, package or SDK dependency changed, or a real code break. Compare against the last green CI run to tell them apart. |
| Replace Stubbed Files | The internal telemetry package or its feed permissions changed. **CI will not catch this** — only release and dry run run it. |
| Generate Release Notes | `GITHUB_TOKEN_2` or `GH_MODELS_TOKEN` expired or lost access. |
| Check release credentials → GitHub Models | The Models token or model is gone. **The release will not fail on this** — it silently ships a raw commit list instead of AI notes. |
| Rehearse release asset staging | Package naming changed, or an architecture stopped building. The WinGet submission and the documented download URLs will break. Update `stage-release-assets.ps1` **and** the inline renames in `release.yml`. |
| Rehearse MS Learn doc porting | A doc edit landed that the porting or validation script rejects, **or** a script it needs is missing from the `mslearn-source` copy list in `release.yml`. |
| Check release credentials | Read the PASS/WARN/FAIL summary at the end of the log. `FAIL` is definitive; `WARN` means the check could not determine an answer. |
| Verify symbol publishing identity | The federated credential or its Entra app registration changed. |
| Verify pinned gh CLI download | The `gh` release asset was retagged or the hash is stale. Update `GhCliUrl`/`GhCliSha256` in **both** `release.yml` and `dryrun.yml`. |
| Rehearse WinGet manifest generation | `wingetcreate` or the manifest schema changed. Advisory — it does not fail the run. |

### Running it on demand

Queue it manually from ADO. Four parameters let you narrow the run:

- `RunCredentialChecks` — **set this to false when running a non-`main` branch.** It skips every
  credential-bearing step, leaving a pure build validation that is safe to run from anywhere.
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
- `scripts/stage-release-assets.ps1` and `scripts/tests/stage-release-assets.Tests.ps1`

Both test suites run as part of `scripts/build-cli.ps1`.

> **Planned (phase 2):** a scheduled agent prompt that reads the weekly run and posts a summary to
> the team. It will be documented alongside this file once the pipeline has a few runs behind it.
