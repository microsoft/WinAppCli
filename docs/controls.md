<!-- mslearn: true -->
<!-- ms.topic: reference -->
<!-- description: Reference for the winapp CLI controls command. Search the WinUI 3 Gallery, Windows Community Toolkit, and curated core platform patterns for grounded XAML and C# samples from the terminal. -->
# Controls Search

Search the **WinUI 3 Gallery**, **Windows Community Toolkit**, and a curated
catalog of **core platform patterns** for grounded XAML and C# samples — without
opening a browser, cloning a sample repo, or reading hundreds of XAML files.

Designed for AI agents and developers who are authoring WinUI 3 apps and need
the canonical sample for a control or pattern *right now*.

## Overview

`winapp controls` indexes three sources and serves them through a BM25-ranked
search engine with synonym expansion:

| Source | Where it comes from | When to use it |
|---|---|---|
| **WinUI 3 Gallery** | [microsoft/WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery) (`main`) | Per-control samples for stock WinUI 3 controls (`TabView`, `NavigationView`, `InfoBar`, …). |
| **Windows Community Toolkit** | [CommunityToolkit/Windows](https://github.com/CommunityToolkit/Windows) (`main`) | Toolkit controls and behaviours (`SettingsCard`, `WrapPanel`, `TabbedCommandBar`, …). |
| **Core platform patterns** | Hand-curated catalog baked into the exe | Foundational scenarios where pulling a Gallery sample is overkill (jump lists, share contracts, system tray, file pickers, drag-and-drop). |

Data is fetched from GitHub on first use and cached for **7 days** under
`%USERPROFILE%\.winapp\cache\controls\` (override with `WINAPP_CLI_CACHE_DIRECTORY`).
An embedded snapshot ships with the exe so the tool works fully offline if the
fetch fails.

## Quick Start

```powershell
# Free-text search across all three sources
winapp controls search "tabbed document interface"

# Pull the full XAML + C# for one of the matches
winapp controls get gallery-tabview

# Browse everything that's available, grouped by source
winapp controls list

# Force a re-fetch from GitHub (ignore the 7-day cache)
winapp controls refresh
```

## Commands

### search

Free-text query, ranked by BM25 with synonym expansion (e.g. *"flexbox"* →
`StackPanel`/`WrapPanel`, *"tabbed document interface"* → `TabView`).

```bash
winapp controls search <query> [--max <N>]
```

**Arguments:**

- `query` — Free-text natural-language description of what you're trying to
  build.

**Options:**

- `--max <N>` — Maximum number of results to return (default: `5`).
- `--source <gallery|toolkit|core>` — Constrain results to one source (default: all sources).

**Output:**

Each result is one entry of:

```text
  <id>
    <title>: <short description>
```

Followed by a hint pointing at `winapp controls get <id>` for the full sample.

**Examples:**

```powershell
winapp controls search "settings card"
winapp controls search "show recently opened files in the taskbar"
winapp controls search "tabbed document interface" --max 10
```

### get

Print the full markdown card for a single pattern: title, description, XAML,
C# code-behind, and (where applicable) inline pitfall notes.

```bash
winapp controls get <id>
```

**Arguments:**

- `id` — Pattern id from `winapp controls search` or `winapp controls list`
  (e.g. `gallery-tabview`, `toolkit-settingscard-settings-page-example`,
  `system-tray-minimize`).

**Exit codes:**

- `0` — pattern printed
- `1` — id not found

**Output is markdown.** Pipe to a file or to your editor of choice:

```powershell
winapp controls get gallery-tabview > tabview-sample.md
```

### list

Dump every available pattern, grouped by source. Useful when you want to see
the exact ids accepted by `get` or to scan what's available.

```bash
winapp controls list [--source <gallery|toolkit|core>]
```

**Options:**

- `--source <gallery|toolkit|core>` — Constrain output to one source (default: all sources).

### refresh

Delete the cached WinUI Gallery and Community Toolkit snapshots so the next
`search`/`get`/`list` call re-fetches the latest from GitHub. Use this after a
new control lands upstream that you know isn't in your local cache yet (or when
you've explicitly bumped past the 7-day TTL).

```bash
winapp controls refresh
```

## Output format

Designed for **markdown-friendly stdout**:

- `search` and `list` print plain ASCII so output composes cleanly inside agent
  conversations.
- `get` prints a markdown card with fenced ```xml and ```csharp code blocks —
  ready to drop into a chat reply, a doc, or an issue.
- The first-run notice that `winapp` normally writes to stdout is **suppressed**
  for `controls` commands so you can safely pipe the output without contamination.

If you need a script-readable form, structured `--json` output is on the roadmap
(see `docs/fragments/skills/winapp-cli/controls.md` *Roadmap*).

## Cache

| Path | Contents |
|---|---|
| `%USERPROFILE%\.winapp\cache\controls\winui-gallery\` | WinUI 3 Gallery snapshot |
| `%USERPROFILE%\.winapp\cache\controls\toolkit\` | Community Toolkit snapshot |

Each cache directory holds `scenarios.json`, `tags.json`, `last-updated.txt`,
and `schema-version.txt`. Cache is valid for 7 days from `last-updated.txt`.
Setting `WINAPP_CLI_CACHE_DIRECTORY` redirects this (along with every other
winapp cache) to the directory of your choice.

## Source attribution

WinUI Gallery is © Microsoft and licensed under the MIT License. Windows
Community Toolkit is © Microsoft / .NET Foundation contributors, also MIT.
Core platform patterns shipped in this tool are authored by the winapp team.
Each result keeps an id prefix (`gallery-…`, `toolkit-…`, or a bare slug for
core patterns) so the source is always identifiable.

## Related

- [docs/usage.md#controls](usage.md) — short reference inside the main usage doc
- [`winapp ui`](ui-automation.md) — once you've copied a sample into your app,
  use UI Automation to drive it from the command line for testing.
