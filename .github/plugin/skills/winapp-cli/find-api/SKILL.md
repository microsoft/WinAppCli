---
name: winapp-find-api
description: Search and inspect the Windows/WinRT API surface (types, members, enums, namespaces) available to a project, resolved from its referenced .winmd/.dll metadata. Use when an AI agent or developer needs to discover an API, list a type's properties/events/methods, validate that a property exists before writing XAML/code, enumerate an enum's values, or explore the namespaces and packages a project can call. Works with WinUI 3, WinRT/UWP, and any project with .winmd/.dll references.
version: 0.5.1
---
## When to use
- Discovering which Windows/WinRT type or member does what you need ("what's the acrylic brush type?", "which control is a NavigationView?")
- Listing a type's properties, events, and methods (with XML-doc descriptions and inherited members) before writing XAML or code against it
- Validating that a property exists on a type — catching typos and wrong-type mistakes before they become CS0117/XAML binding errors
- Enumerating an enum's values (e.g. `Symbol`, `Visibility`)
- Exploring the namespaces and packages a project can call into
- AI agents grounding code generation in the *actual* API surface a project references, instead of guessing

## Prerequisites
- Run from (or point `--project-dir` at) a project that has been **restored** — the index is built from `project.assets.json` and the restored NuGet/SDK packages. If the project has never been restored, run `winapp restore` (or `dotnet restore`) first.
- The first query for a project builds the index automatically (this can take a few seconds for a large SDK like WindowsAppSDK); subsequent queries are served from the warm cache. The index refreshes automatically when the project is re-restored.
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
- **Project resolution.** With one indexed project, queries "just work". With several, pass `--project <Name>` (matches the `.csproj`/`.vcxproj` name) or `--project-dir <path>` to disambiguate.
- **Exit codes for scripting.** `search` with no hits, `check-property` on a missing property, and `enums` on a non-enum all exit non-zero — gate code generation and CI checks on them.
- **Ambiguity detection.** When a short type name resolves to multiple namespaces (a CS0104 risk), search surfaces every candidate with its fully-qualified name so you can pick the right one.
- **Inherited members.** `members` includes inherited properties/events/methods and marks their declaring type, so you see the full usable surface of a control.

## Troubleshooting
- **"No indexed API metadata was found for this project."** The project has not been restored (no `project.assets.json`), or you're not in the project directory. Run `winapp restore`, then retry — or pass `--project-dir <path>`.
- **"Multiple projects are indexed — use --project to choose one."** Several projects share the cache. Pass `--project <Name>` or `--project-dir <path>`.
- **"Project '<name>' is not indexed."** The name passed to `--project` doesn't match a cached project. Run `winapp find-api projects` to see the indexed names, or `winapp find-api refresh` in that project's directory.
- **A type/member you expect is missing.** The owning package may not be restored, or the index is stale. Re-restore the project (auto-refreshes) or run `winapp find-api refresh` to force a rebuild.
- **First query is slow.** That's the one-time index build for the project's packages; subsequent queries are fast against the warm cache.


## Command Reference

### `winapp find-api members`

List the properties, events, and methods of a type (with XML-doc descriptions and inherited members), resolved from the project's indexed API metadata.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<type>` | No | The type to inspect. Accepts a short name (NavigationView) or a fully-qualified name (Microsoft.UI.Xaml.Controls.NavigationView). |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |

### `winapp find-api check-property`

Validate that a property exists on a type before you write XAML/code against it. On a miss, suggests similar properties on the type, attached-property forms, and other types that declare the property. Exits non-zero when the property does not exist.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<type>` | No | The type to check. |
| `<property>` | No | The property name to validate on the type. |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |

### `winapp find-api types`

List the types declared in a namespace (class/struct/enum/interface/delegate) with their base types.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<namespace>` | No | The namespace to list, e.g. Microsoft.UI.Xaml.Controls. |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |

### `winapp find-api enums`

List the values of an enum type. Exits non-zero when the type exists but is not an enum.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<type>` | No | The enum type to list, e.g. Symbol or Microsoft.UI.Xaml.Controls.Symbol. |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |

### `winapp find-api namespaces`

List the namespaces available to the project across its indexed API metadata, optionally filtered by prefix.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--filter` | Only list namespaces starting with this prefix, e.g. --filter Microsoft.UI. | (none) |
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |

### `winapp find-api packages`

List the NuGet/SDK packages whose API metadata is indexed for a project, with per-package type and member counts.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |

### `winapp find-api stats`

Show aggregate statistics for a project's API index: package, namespace, type, member, and .winmd file counts.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |

### `winapp find-api projects`

List every project that currently has an API index in the shared cache, with the number of packages indexed for each.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |

### `winapp find-api refresh`

Rebuild the API metadata index for a project from its restored packages. Runs automatically when a project is restored; run it manually to force a re-index or to index a project for the first time.

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--json` | Format output as JSON | (none) |
| `--project` | Project name to disambiguate when several projects are indexed (matches the .csproj/.vcxproj name). | (none) |
| `--project-dir` | Project directory to query (defaults to the current directory). Used to locate the indexed project. | (none) |
| `--scan` | Recursively discover and index every project under the directory instead of just the top-level project(s). | (none) |
