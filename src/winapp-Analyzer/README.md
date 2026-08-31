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

Part of the `winappCli` repo. Build and test via the solution:

```powershell
# From the repo root
dotnet build src/winapp-CLI/winapp.sln -c Release
dotnet test  src/winapp-Analyzer/Microsoft.WindowsAppSDK.Analyzers.Tests/Microsoft.WindowsAppSDK.Analyzers.Tests.csproj -c Release
```

The build emits `Microsoft.WindowsAppSDK.Analyzers.dll` under
`src/winapp-Analyzer/Microsoft.WindowsAppSDK.Analyzers/bin/Release/netstandard2.0/`.
The analyzer references the oldest supported Roslyn (`Microsoft.CodeAnalysis.CSharp`
4.8.0, = .NET SDK 8.0.100) so older compilers still load it.

## Distribution

The analyzer is delivered two ways from a single build (see the design in the
`winappCli` repo and `scripts/package-nuget.ps1`):

* **Standalone NuGet package** — packed as
  **`Microsoft.Windows.SDK.BuildTools.WinUIAnalyzer`** (note: the package ID
  differs from the assembly name, which stays `Microsoft.WindowsAppSDK.Analyzers`).
  `dotnet build`, Visual Studio, and CI pick it up through the normal
  `analyzers/dotnet/cs` convention. Reference it directly to get the diagnostics
  without the CLI.
* **Embedded in the `winapp` CLI** — `winapp run` extracts and injects the same
  analyzer DLL for WinUI project-mode builds, so you get the diagnostics even
  without a `PackageReference`. A build-time provenance gate asserts the embedded
  DLL is byte-identical to the one packed into the NuGet package.

Both artifacts share the CLI version and are signed and pushed to nuget.org by the
repo's `rel/v*` release pipeline.

## Status

**Preview / `0.x`.** Rule IDs are immutable, but the rule set itself will grow.
Every rule has a `helpLinkUri` into the rule catalog (`RULES.md`). Rules ship at
`Warning` severity (never `Error`) so adding a rule can never break someone's build
by default. When `winapp run` injects the analyzer it also exempts the `WUIxxxx`
IDs from `TreatWarningsAsErrors`, so an automatic injection can never turn a
passing build red (a user can still opt an individual rule into an error via their
own `WarningsAsErrors`).

## Contributing

* Add new rules under `Microsoft.WindowsAppSDK.Analyzers/Rules/`. Reserve a
  fresh ID in `DiagnosticIds.cs` (don't reuse retired ones), wire a
  `helpLinkUri` into `HelpLinks.cs`, and add positive / negative / FP-guard
  tests under `Microsoft.WindowsAppSDK.Analyzers.Tests/Rules/`. Update
  `RULES.md`, `CHANGELOG.md`, and the rule list in `AnalyzerReleases.Unshipped.md`.
  If `winapp run`'s injected `WarningsNotAsErrors` list should cover the new ID,
  add it in `src/winapp-CLI/WinApp.Cli/Services/AnalyzerInjectionService.cs`.
