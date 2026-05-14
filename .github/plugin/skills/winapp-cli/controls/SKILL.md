---
name: winapp-controls
description: Search the WinUI 3 Gallery, Windows Community Toolkit, and curated core platform patterns for grounded XAML and C# code samples. Use when an AI agent or developer is authoring a WinUI 3 app and needs the canonical sample for a control or scenario (TabView, NavigationView, SettingsCard, jump lists, share contract, file picker, etc.) without leaving the terminal. Returns markdown-formatted code blocks ready to drop into an app.
version: 0.3.1
---
## When to use
- An agent or developer needs the canonical XAML/C# sample for a WinUI 3 control or platform pattern
- Authoring a new WinUI 3 page and want to reach for the right control without opening a browser
- Looking up Community Toolkit controls (`SettingsCard`, `WrapPanel`, `TabbedCommandBar`, …) by what they do, not by name
- Discovering core platform patterns (jump lists, share contract, system tray, file picker, drag-drop) without diving into win32 docs
- Producing a grounded code suggestion that won't hallucinate API surface — every snippet returned came verbatim from a maintained Microsoft sample repo

## Prerequisites
- Network access on first use (fetches snapshots from `microsoft/WinUI-Gallery` and `CommunityToolkit/Windows` on GitHub)
- After the first run, the tool works fully offline for 7 days from cache; an embedded snapshot is also baked into the exe as a hard fallback

## Common patterns

### Find a control, then pull the full sample
```powershell
# Free-text query → list of ranked candidate ids
winapp controls search "tabbed document interface"

# Take the most relevant id and print the full XAML + C#
winapp controls get gallery-tabview
```

### Discover what core platform patterns exist
```powershell
# Group output by source — core platform patterns appear first
winapp controls list

# Then drill into one
winapp controls get jumplist-recent-files
```

### Refresh the cache after upstream changes
```powershell
# Delete the 7-day cache so the next call re-fetches from GitHub
winapp controls refresh

# Verify by running any read command — it'll print fresh data
winapp controls search "settings card"
```

### Compose with agent workflows
```powershell
# get returns markdown — paste straight into a chat reply or save to a file
winapp controls get toolkit-settingscard-settings-page-example > settings-card-sample.md

# Chain search → get to deliver one snippet from a vague description
winapp controls search "show a list with custom item layout" --max 3
```

## Key concepts
- **Three sources**: `gallery-*` (WinUI 3 Gallery), `toolkit-*` (Community Toolkit), and bare slugs (curated core platform patterns). The id prefix tells you where the snippet came from.
- **BM25 ranking with synonym expansion**: the engine knows that *"flexbox"* maps to `StackPanel`/`WrapPanel`, *"tabbed document interface"* maps to `TabView`, *"hamburger menu"* maps to `NavigationView`, etc. Phrase queries work — you don't need to know the control name.
- **Cache lifetime is 7 days**: cached at `%USERPROFILE%\.winapp\cache\controls\{winui-gallery,toolkit}\`. The tool re-fetches automatically after that, or on demand via `winapp controls refresh`. Honors `WINAPP_CLI_CACHE_DIRECTORY` like every other winapp cache.
- **Markdown output is the contract**: `search`, `get`, and `list` all print plain ASCII / markdown to stdout. The first-run welcome notice that other `winapp` commands print is **suppressed** for `controls`, so it's safe to pipe to a file or another tool.
- **Offline by default after first run**: an embedded JSON snapshot ships inside the exe and is used if the GitHub fetch fails — your agent loop never breaks because of a network blip.
- **Exit code 1 on missing id**: `winapp controls get bogus-id` prints `Pattern 'bogus-id' not found.` to stdout and exits non-zero, so scripts can react.

## Usage

### Search
```powershell
# Free-text query, default 5 results
winapp controls search "settings card"

# Wider net
winapp controls search "tabbed document interface" --max 10

# Natural-language descriptions work
winapp controls search "i need a stack box like flexbox on the web"
winapp controls search "show recently opened files in the taskbar right-click menu"
```

### Get
```powershell
# Pull full XAML + C# for one pattern
winapp controls get gallery-tabview
winapp controls get toolkit-settingscard-settings-page-example
winapp controls get jumplist-recent-files

# Save to disk for editing
winapp controls get gallery-navigationview > navview.md
```

### List
```powershell
# All patterns, grouped by source — useful for discovery
winapp controls list
```

### Refresh
```powershell
# Delete cache; next read re-fetches from GitHub
winapp controls refresh
```

## Tips
- Start with `winapp controls search "<plain English of what you want>"` — synonym expansion is doing real work; you usually don't need the right keyword.
- The `id` you see in `search` output is the only argument `get` accepts — copy it verbatim.
- If you need fresh data right now (e.g. a control was added upstream this morning), run `winapp controls refresh` once and the next read repopulates the cache.
- Output is markdown — pipe to a file (`> sample.md`) or paste straight into an agent reply.
- For agent loops that need a known-good cache state across runs, point `WINAPP_CLI_CACHE_DIRECTORY` at a per-session directory; everything (controls + other winapp caches) lands under it.

## Related skills
- `winapp-setup` — initialize a project with the Windows App SDK before you start dropping samples into it
- `winapp-frameworks` — language-specific guidance for consuming WinUI 3 from Electron/Flutter/.NET/C++/Rust/Tauri
- `winapp-package` — once your sample app works, package it as MSIX

## Roadmap
- `--json` opt-in output for scripts and agents that want structured results
- `--refresh` flag on `search`/`get`/`list` so the cache can be invalidated inline without a separate `refresh` call
- Optional `--source <gallery|toolkit|core>` filter to constrain results to one source

## Troubleshooting
| Error | Cause | Solution |
|---|---|---|
| `Pattern 'X' not found.` (exit 1) | The id isn't in the current cache | Run `winapp controls list` to see valid ids, or `winapp controls refresh` if you suspect the cache is stale |
| Empty results for a known control | Cache schema bumped or fetch failed silently | `winapp controls refresh`, then re-run; the next call repopulates from GitHub or falls back to the embedded snapshot |
| Stale results | Cache is up to 7 days old | `winapp controls refresh` |
| `search` returns the same hits regardless of phrasing | BM25 saturated on a common term in your query | Add more specific words (`"tabbed document interface"` not `"tabs"`) or raise `--max` to see lower-ranked candidates |


## Command Reference

### `winapp controls search`

Search WinUI 3 Gallery, Community Toolkit, and core platform patterns for controls that match a free-text query.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<query>` | Yes | Free-text query (e.g. "tabbed document interface", "share contract", "settings card"). |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--max` | Maximum number of matches to return. | `5` |

### `winapp controls get`

Print the full XAML, C#, and pitfall notes for a single control pattern, identified by the id returned from `winapp controls search`.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<id>` | Yes | Pattern id from `winapp controls search` (e.g. gallery-tabview, toolkit-segmented, jumplist-recent-files). |

### `winapp controls list`

List every available control pattern grouped by source (core platform patterns, WinUI Gallery, Community Toolkit). Useful for discovery and to see exact ids accepted by `winapp controls get`.

### `winapp controls refresh`

Delete the cached WinUI Gallery and Community Toolkit dataset so the next `winapp controls search/get/list` re-fetches the latest snapshot from GitHub.
