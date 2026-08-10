---
name: winapp-find-api
description: Search and inspect the Windows/WinRT API surface (types, members, enums, namespaces) available to a project, resolved from its referenced .winmd/.dll metadata — or, outside any project, from the machine-wide Windows SDK. Use when an AI agent or developer needs to discover an API, list a type's properties/events/methods, validate that a property exists before writing XAML/code, enumerate an enum's values, or explore the namespaces and packages a project can call. Works with WinUI 3, WinRT/UWP, and any project with .winmd/.dll references. Distinct from 'winapp find-ui', which returns working WinUI control samples.
---
## When to use
- Discovering which Windows/WinRT type or member does what you need ("what's the acrylic brush type?", "which control is a NavigationView?")
- Listing a type's properties, events, and methods (with XML-doc descriptions and inherited members) before writing XAML or code against it
- Validating that a property exists on a type — catching typos and wrong-type mistakes before they become CS0117/XAML binding errors
- Enumerating an enum's values (e.g. `Symbol`, `Visibility`)
- Exploring the namespaces and packages a project can call into
- AI agents grounding code generation in the *actual* API surface a project references, instead of guessing

## Prerequisites
- **Querying a project:** run from (or point `--project-dir` at) a project that has been **restored** — the index is built from `project.assets.json` and the restored NuGet/SDK packages. If the project has never been restored, run `winapp restore` (or `dotnet restore`) first.
- **Querying with no project:** nothing is required. From a directory with no project, `find-api` answers from the machine-wide **SDK scope** (Windows SDK + Windows App SDK), so an agent can explore the API surface *before* scaffolding an app. No network access is needed in either case.
- The first query builds the index automatically (this can take a few seconds for a large SDK like WindowsAppSDK); subsequent queries are served from the warm cache. The project index refreshes automatically when the project is re-restored.
- No setup is needed beyond a restored project — the index lives under the global `.winapp` cache (`cache/find-api/`) and is shared across projects.

## Common patterns

### Search for an API
```powershell
# Bare form is a search — matched lexically against type and member names
winapp find-api "acrylic brush"
winapp find-api NavigationView
winapp find-api "list view" --max 10
```

### Inspect a type's members
```powershell
# Short name or fully-qualified name both work
winapp find-api members NavigationView
winapp find-api members Microsoft.UI.Xaml.Controls.NavigationView
```

### Validate a property before you write it
```powershell
# Exits non-zero when the property does not exist — safe to gate codegen on
winapp find-api check-property Button Background
winapp find-api check-property TextBlock Text
```

### List enum values
```powershell
winapp find-api enums Symbol
winapp find-api enums Microsoft.UI.Xaml.Visibility
```

### Explore namespaces, types, and packages
```powershell
winapp find-api namespaces --filter Microsoft.UI.Xaml
winapp find-api types Microsoft.UI.Xaml.Controls
winapp find-api packages
winapp find-api stats
```

### Manage the index
```powershell
# List every indexed project in the shared cache
winapp find-api projects

# Force a re-index (usually automatic after restore); --scan indexes every project under the dir
winapp find-api refresh
winapp find-api refresh --scan
```

### Explore the SDK with no project
```powershell
# From a directory with no project, results come from the machine-wide Windows SDK
# scope (reported as scope: sdk) — useful before an app has been scaffolded
winapp find-api "acrylic brush"
winapp find-api members Button --project sdk

# Rebuild the SDK scope after installing a new Windows SDK
winapp find-api refresh --project sdk
```

### Script against it with --json
```powershell
# Every verb supports --json for a clean, machine-readable payload on stdout
winapp find-api NavigationView --json
winapp find-api check-property Button Backgruond --json   # exits 1, JSON reports found:false
```

## Key concepts
- **Bare form = search.** `winapp find-api "<query>"` searches; the sub-verbs (`members`, `check-property`, `types`, `enums`, `namespaces`, `packages`, `stats`, `projects`, `refresh`) drill into specifics.
- **Lexical, not semantic.** Search matches type and member *names* (and signatures), ranked by a scoring heuristic. It does not do embeddings/semantic matching — phrase queries the way the API is named.
- **Automatic indexing.** The index builds on first query and refreshes when `project.assets.json` changes, so it stays in sync with restores. Use `refresh` only to force a rebuild or index a project for the first time without querying.
- **Project resolution and scopes.** Every answer names its scope (`scope` in `--json`, a note in text). A project in the current directory (or `--project` / `--project-dir`) gives `scope: project`, covering the Windows SDK, Windows App SDK, *and* the project's NuGet packages. A directory with **no** project gives `scope: sdk` — the machine-wide Windows SDK + Windows App SDK only, which excludes third-party NuGet packages. A projectless query is *never* answered from some other indexed project, so results don't depend on unrelated global state. Use `--project sdk` to pick the SDK scope explicitly from inside a project.
- **Exit codes for scripting.** `search` with no hits, `check-property` on a missing property, and `enums` on a non-enum all exit non-zero — gate code generation and CI checks on them.
- **Ambiguity detection.** When a short type name resolves to multiple namespaces (a CS0104 risk), search surfaces every candidate with its fully-qualified name so you can pick the right one.
- **Inherited members.** `members` includes inherited properties/events/methods and marks their declaring type, so you see the full usable surface of a control.

## Troubleshooting
- **"No indexed API metadata was found for this project."** You are standing in a real project that hasn't been indexed — usually because it has not been restored (no `project.assets.json`). Run `winapp restore`, then retry. `find-api` deliberately does *not* silently narrow to the SDK scope here, because that would hide the project's own NuGet packages and make its types look nonexistent.
- **Results say `scope: sdk` but you expected project APIs.** There is no project in the current directory, so the machine-wide SDK scope answered. `cd` into the project (or pass `--project-dir <path>`); third-party NuGet packages such as the Community Toolkit only exist in the `project` scope.
- **"No project was found here and no Windows SDK metadata is available on this machine."** Neither a project nor an installed Windows SDK / Windows App SDK was found. Run from a project directory, or install the SDK.
- **"Project '<name>' is not indexed."** The name passed to `--project` doesn't match a cached project. Run `winapp find-api projects` to see the indexed names, or `winapp find-api refresh` in that project's directory.
- **A type/member you expect is missing.** The owning package may not be restored, or the index is stale. Re-restore the project (auto-refreshes) or run `winapp find-api refresh` to force a rebuild. After installing a *new Windows SDK*, rebuild the SDK scope with `winapp find-api refresh --project sdk`.
- **First query is slow.** That's the one-time index build for the project's packages; subsequent queries are fast against the warm cache.

## Related skills
- **`winapp-find-ui`** — when you need a *working WinUI control sample* (XAML + C#) rather than the raw API surface. Use `find-api` to confirm a type/member exists and inspect its shape; use `find-ui` to get example usage.
- **`winapp-ui-automation`** (`winapp ui`) — inspects a *running app's* UI tree; `find-api` inspects the *static API surface* a project references.

## CLI reference
- `winapp find-api "<query>" [--max N]` — lexical search across types and members (bare form). Exits non-zero on no hits.
- `winapp find-api members <type>` — properties, events, and methods (incl. inherited) of a type.
- `winapp find-api check-property <type> <property>` — validate a property exists; exits non-zero on a miss.
- `winapp find-api types <namespace>` — types declared in a namespace, with base types.
- `winapp find-api enums <type>` — enum values; exits non-zero when the type is not an enum.
- `winapp find-api namespaces [--filter <prefix>]` — available namespaces.
- `winapp find-api packages` — indexed NuGet/SDK packages with per-package counts.
- `winapp find-api stats` — aggregate index statistics for the project.
- `winapp find-api projects` — every project indexed in the shared cache.
- `winapp find-api refresh [--scan]` — force a re-index; `--scan` walks all projects under the directory.

Common options (all verbs): `--json` for machine-readable output (payloads include a `scope` field: `project` or `sdk`), `--project <Name>` / `--project-dir <path>` to select a project, `--project sdk` to query the machine-wide Windows SDK scope.
