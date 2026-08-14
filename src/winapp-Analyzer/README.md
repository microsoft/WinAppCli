# Microsoft.WindowsAppSDK.Analyzers — WinUI 3 / Windows App SDK Roslyn Analyzer

A Roslyn analyzer that catches common WinUI 3 / Windows App SDK pitfalls at
build time — UWP→WinUI 3 compatibility issues, runtime traps, MVVM
regressions, and interop bugs. Every diagnostic ships at `Warning` severity
(no rule is `Error`) and includes a `helpLinkUri`.


## Layout

```
src/tools/winui-analyzer/
├── Microsoft.WindowsAppSDK.Analyzers/         # the analyzer assembly (netstandard2.0)
│   ├── DiagnosticIds.cs / DiagnosticCategories.cs / HelpLinks.cs
│   ├── ProjectContext.cs                      # UWP-vs-greenfield project gate
│   ├── Allowlists.cs                          # declarative per-rule carve-outs
│   ├── ApiMappings.g.cs / FeatureMappings.g.cs # data-driven from Microsoft Learn
│   └── Rules/                                 # 9 DiagnosticAnalyzers
├── Microsoft.WindowsAppSDK.Analyzers.Tests/   # xUnit test project (net10.0)
├── docs/ROADMAP.md                            # what's planned next
├── RULES.md                                   # full rule catalog + ID methodology
├── CHANGELOG.md                               # analyzer-scoped changelog
├── Directory.Build.props                      # scoped — TWaE only inside this subtree
├── global.json                                # pins .NET 10 SDK
└── Microsoft.WindowsAppSDK.Analyzers.slnx     # solution file
```

The `Directory.Build.props` and `global.json` are intentionally scoped to this
subtree (not at repo root) so `TreatWarningsAsErrors=true` doesn't break
unrelated C# projects (`winui-search`, `winmd-cli`).

## Rule categories

Rules use a 4-digit categorized ID scheme (`WUIcXxx` where `c` is the
category). IDs are immutable — once assigned, never reused, even if the rule
is removed. See [`RULES.md`](RULES.md) for the full per-rule catalog and the
migration table from the older `WUIxxx` 3-digit scheme.

| Category | Range | What it covers |
|---|---|---|
| UWP → WinUI 3 API compatibility | `WUI0xxx` | `Window.Current`, `CoreDispatcher`, `GetForCurrentView`, `using Windows.UI.Xaml` |
| Migration-table data-driven | `WUI1xxx` | UWP API has WinAppSDK equivalent / no equivalent / feature-area hint (driven by `ApiMappings.g.cs` + `FeatureMappings.g.cs`) |
| Runtime / layout / XAML pitfalls | `WUI2xxx` | Raw `TabView` content, nested `x:Bind` without fallback, `x:Bind` without `Mode`, null `Converter`, missing `AutomationId`, attached-property syntax |
| MVVM patterns | `WUI3xxx` | Old `[ObservableProperty]` field syntax |
| Interop | `WUI4xxx` | `WebView2` not initialized, removed ONNX Runtime GenAI APIs |

## Building & testing

Requires the .NET 10 SDK (a `global.json` in this directory pins to 10.0.x).

```powershell
# From this directory (src/tools/winui-analyzer/)
dotnet build Microsoft.WindowsAppSDK.Analyzers.slnx -c Release
dotnet test  Microsoft.WindowsAppSDK.Analyzers.slnx -c Release

# Or, from the repo root
dotnet build src/tools/winui-analyzer/Microsoft.WindowsAppSDK.Analyzers.slnx -c Release
```

The build emits `Microsoft.WindowsAppSDK.Analyzers.dll` under
`Microsoft.WindowsAppSDK.Analyzers/bin/Release/netstandard2.0/`. The `.targets`
file lives next to the source — both files must be copied to the
`winui-dev-workflow` skill's `analyzer/` payload after every change so the
skill stays self-contained.

For a one-shot rebuild + payload refresh, use the repo-root helper:

```powershell
# Builds analyzer + winmd-cli + winui-search and refreshes the analyzer skill
# payload in one step. Use this whenever you change analyzer source so the
# pr-validation provenance check doesn't fail your PR.
./scripts/build-tools.ps1
```

## Distribution

Today the analyzer ships as a **prebuilt `Microsoft.WindowsAppSDK.Analyzers.dll`
committed under `plugins/winui/skills/winui-dev-workflow/analyzer/`**. Two CI
guardrails keep source ↔ binary honest:

* **`analyzer-provenance`** — every PR rebuilds the DLL and SHA-256 compares
  it against the committed copy (256-byte size-delta tolerance for
  deterministic-build drift across SDK versions).
* **`analyzer-targets-sync`** — the source-tree `.targets` file and the
  skill-payload `.targets` file must be byte-identical.

If you change source, run `scripts/build-tools.ps1` from the repo root before
opening the PR — otherwise both checks will fail.

The longer-term plan is to publish this as a NuGet package
(`Microsoft.WindowsAppSDK.Analyzers`); the csproj is already wired for it
(`PackageId`, `PackageVersion`, `<Description>`, `<PackageTags>`). Flip
`GeneratePackageOnBuild=true` when ready. NuGet publication is tracked
separately as `tool-analyzer-nuget` in the launch tracker — independent of
this repo's public launch.

## Status

**Preview / `0.1.0-alpha`.** Rule IDs are immutable, but the rule set itself
will grow. Every rule has a `helpLinkUri` pointing at relevant Microsoft Learn
documentation. Ships at `Warning` severity (never `Error`) so adding a rule
can never break someone's build by default — they have to opt into
`TreatWarningsAsErrors` for the analyzer's diagnostics.

## Contributing

* Add new rules under `Microsoft.WindowsAppSDK.Analyzers/Rules/`. Reserve a
  fresh ID in `DiagnosticIds.cs` (don't reuse retired ones), wire a
  `helpLinkUri` into `HelpLinks.cs`, and add positive / negative / FP-guard
  tests under `Microsoft.WindowsAppSDK.Analyzers.Tests/Rules/`. Update
  `RULES.md` and `CHANGELOG.md`.
* Bump `<PackageVersion>` in the csproj only when publishing (see distribution
  section above).
* Don't put `Directory.Build.props` at the repo root — it would force
  `TreatWarningsAsErrors` onto `winui-search` / `winmd-cli` which aren't
  ready for it.
