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
winapp gallery search "tabbed document interface"

# Take the most relevant id and print the full XAML + C#
winapp gallery get gallery-tabview
```

### Discover what core platform patterns exist
```powershell
# Group output by source — core platform patterns appear first
winapp gallery list

# Then drill into one
winapp gallery get jumplist-recent-files
```

### Refresh the cache after upstream changes
```powershell
# Delete the 7-day cache so the next call re-fetches from GitHub
winapp gallery refresh

# Verify by running any read command — it'll print fresh data
winapp gallery search "settings card"
```

### Compose with agent workflows
```powershell
# get returns markdown — paste straight into a chat reply or save to a file
winapp gallery get toolkit-settingscard-settings-page-example > settings-card-sample.md

# Chain search → get to deliver one snippet from a vague description
winapp gallery search "show a list with custom item layout" --max 3
```

## Key concepts
- **Three sources**: `gallery-*` (WinUI 3 Gallery), `toolkit-*` (Community Toolkit), and bare slugs (curated core platform patterns). The id prefix tells you where the snippet came from.
- **BM25 ranking with synonym expansion**: the engine knows that *"flexbox"* maps to `StackPanel`/`WrapPanel`, *"tabbed document interface"* maps to `TabView`, *"hamburger menu"* maps to `NavigationView`, etc. Phrase queries work — you don't need to know the control name.
- **Cache lifetime is 7 days**: cached at `%USERPROFILE%\.winapp\cache\gallery\{gallery,toolkit}\`. The tool re-fetches automatically after that, or on demand via `winapp gallery refresh`. Honors `WINAPP_CLI_CACHE_DIRECTORY` like every other winapp cache.
- **Markdown output is the contract**: `search`, `get`, and `list` all print plain ASCII / markdown to stdout. The first-run welcome notice that other `winapp` commands print is **suppressed** for `gallery`, so it's safe to pipe to a file or another tool.
- **Offline by default after first run**: an embedded JSON snapshot ships inside the exe and is used if the GitHub fetch fails — your agent loop never breaks because of a network blip.
- **Exit code 1 on missing id**: `winapp gallery get bogus-id` prints `Pattern 'bogus-id' not found.` to stdout and exits non-zero, so scripts can react.

## Usage

### Search
```powershell
# Free-text query, default 5 results
winapp gallery search "settings card"

# Wider net
winapp gallery search "tabbed document interface" --max 10

# Natural-language descriptions work
winapp gallery search "i need a stack box like flexbox on the web"
winapp gallery search "show recently opened files in the taskbar right-click menu"
```

### Get
```powershell
# Pull full XAML + C# for one pattern
winapp gallery get gallery-tabview
winapp gallery get toolkit-settingscard-settings-page-example
winapp gallery get jumplist-recent-files

# Save to disk for editing
winapp gallery get gallery-navigationview > navview.md
```

### List
```powershell
# All patterns, grouped by source — useful for discovery
winapp gallery list
```

### Refresh
```powershell
# Delete cache; next read re-fetches from GitHub
winapp gallery refresh
```

## Tips
- Start with `winapp gallery search "<plain English of what you want>"` — synonym expansion is doing real work; you usually don't need the right keyword.
- The `id` you see in `search` output is the only argument `get` accepts — copy it verbatim.
- If you need fresh data right now (e.g. a control was added upstream this morning), run `winapp gallery refresh` once and the next read repopulates the cache.
- Output is markdown — pipe to a file (`> sample.md`) or paste straight into an agent reply.
- For agent loops that need a known-good cache state across runs, point `WINAPP_CLI_CACHE_DIRECTORY` at a per-session directory; everything (gallery + other winapp caches) lands under it.

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
| `Pattern 'X' not found.` (exit 1) | The id isn't in the current cache | Run `winapp gallery list` to see valid ids, or `winapp gallery refresh` if you suspect the cache is stale |
| Empty results for a known control | Cache schema bumped or fetch failed silently | `winapp gallery refresh`, then re-run; the next call repopulates from GitHub or falls back to the embedded snapshot |
| Stale results | Cache is up to 7 days old | `winapp gallery refresh` |
| `search` returns the same hits regardless of phrasing | BM25 saturated on a common term in your query | Add more specific words (`"tabbed document interface"` not `"tabs"`) or raise `--max` to see lower-ranked candidates |
