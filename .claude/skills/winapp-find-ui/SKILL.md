---
name: winapp-find-ui
description: Search WinUI 3 controls and samples for a working code example. Use when building a WinUI 3 UI and you need to discover which control fits an intent (e.g. 'tabbed layout', 'a card with an image and title', 'swipeable list rows') and get a real code example from the WinUI Gallery or the Windows Community Toolkit (Gallery/Toolkit return XAML and/or C#). The microsoft-ui-reactor ReactorGallery is an opt-in source (C#-only declarative WinUI) searched only via --source reactor. WinUI-only — not WPF/WinForms. Distinct from 'winapp ui', which automates a running app's UI.
version: 0.5.1
---
## When to use

Use this skill when building a **WinUI 3** UI and you need to discover which
control fits an intent and get a real, working code example — without leaving the
CLI or guessing at control names and APIs.

`winapp find-ui` searches the **WinUI 3 Gallery** and the **Windows Community
Toolkit** (plus a few curated **core** patterns) and returns a working code
snippet plus where it came from. A third source, the **microsoft-ui-reactor
ReactorGallery**, is **opt-in**: it is excluded from a normal search and is only
searched when you pass `--source reactor`.

- **WinUI-only.** The corpus is WinUI 3 Gallery + Windows Community Toolkit (+
  Reactor when opted in). It does **not** cover WPF, WinForms, or other UI
  frameworks.
- **Reactor is opt-in and for Reactor projects only.** Reactor is a C#-only
  declarative/MVU framework — its samples can't paste into a standard `dotnet new
  winui` XAML + code-behind app, so a default search deliberately omits it. Only
  reach for `--source reactor` when you're actually building a Reactor app.
- **Result shape varies by source.** Gallery and Toolkit scenarios return XAML,
  C#, or both (one-sided samples are kept); Reactor scenarios are C#-only
  declarative WinUI (no XAML).
- Distinct from `winapp ui search`, which searches a *running app's* UI tree via
  UI Automation — unrelated to control/sample discovery.

**Front-load lookups, then code.** Search for each feature you need up front, pick
the right control and scenario id, fetch its full code with `--id`, then write your
XAML — don't interleave search-and-code.

## Workflow

```bash
# 1. Search compactly to find the control + its scenario ids (WinUI-only)
winapp find-ui "tabbed layout"

# 2. Fetch the full XAML + C# (and prerequisite notes) for the best match
winapp find-ui --id gallery-tabview-1

# 3. Batch: fetch several scenarios at once
winapp find-ui --id gallery-tabview-1 --id toolkit-tabbedcommandbar-1
```

## Examples

```bash
# Natural-intent search
winapp find-ui "a card with an image and title"
winapp find-ui "swipeable list rows"

# Restrict to one source
winapp find-ui "settings card" --source toolkit
winapp find-ui "color picker" --source gallery

# Reactor is opt-in: excluded from a normal search, only searched with --source reactor
# (use this only for a Reactor/MVU project — its C#-only samples don't fit standard XAML apps)
winapp find-ui "flex layout" --source reactor

# Return more candidates
winapp find-ui "navigation" --max 6

# Browse everything (heavy — prefer search; excludes opt-in Reactor)
winapp find-ui --list

# Force a corpus refresh from GitHub
winapp find-ui "info bar" --refresh
```

## Agent-friendly output

Add `--json` for a structured, grounded result an agent can consume in one shot:

```bash
winapp find-ui "color picker" --json
```

- **Search** → `{ query, matchCount, matches: [ { source, control, score, description?, scenarios: [ { id, header } ] } ] }` — compact; use the `id` values to fetch code.
- **`--id`** → `{ results: [ { id, found, content } ] }` — `content` is the code markdown block (XAML and/or C# for Gallery/Toolkit; C# only for Reactor).
- **`--list`** → `{ count, items: [ { id, header } ] }`.
- On error, `--json` emits `{ "error": "..." }` on stdout with a non-zero exit code.

## Notes & tips

- **One mode at a time.** A search query, `--id`, and `--list` are mutually
  exclusive — combining them is rejected. `--source` applies to search only.
- **Reactor is opt-in.** A normal search and `--list` cover Gallery + Toolkit +
  core only. Pass `--source reactor` to search Reactor (reactor-only results);
  a `reactor-<control>-<n>` `--id` still fetches even without the flag. Skipping
  Reactor by default keeps its C#-only samples from outranking usable controls in
  a standard XAML app.
- **First run needs network.** The corpus is fetched from GitHub on first use and
  cached per-user under `<global .winapp>/cache/find-ui`. Subsequent runs are
  served from the cache (refreshed at most every 7 days, or on demand with
  `--refresh`). If the very first run is offline you'll get a clear "connect and
  run once" message.
- **Scenario ids** are stable within a cached corpus. Gallery/Toolkit/Reactor ids
  look like `gallery-<control>-<n>` / `toolkit-<control>-<n>` /
  `reactor-<control>-<n>`; the `<source>-` prefix disambiguates controls that
  exist in more than one gallery (e.g. `ColorPicker`). Curated **core** patterns
  use a plain descriptive id with **no** `<source>-<control>-<n>` shape (e.g.
  `file-picker-desktop`, `live-charts`); fetch them the same way with `--id`, and
  browse them with `--source core`.
- **Exit codes** are script-friendly: `0` on a hit, `1` on no match / error.
- Keep queries **focused** (one feature per query) — the lexical ranker rewards
  specific phrasing. Batch multiple focused queries rather than one broad one.


## Command Reference

### `winapp find-ui`

Search WinUI controls and samples for a working code example. WinUI-only: covers the WinUI 3 Gallery and the Windows Community Toolkit by default (plus the microsoft-ui-reactor ReactorGallery as an opt-in source via --source reactor); not WPF/WinForms. The corpus is fetched from GitHub on first use and cached per-user, so the first run requires network access.

#### Arguments
<!-- auto-generated from cli-schema.json -->
| Argument | Required | Description |
|----------|----------|-------------|
| `<query>` | No | What you're looking for, e.g. "tabbed layout" or "color picker". Matched lexically against WinUI control names, sample headers, and tags. |

#### Options
<!-- auto-generated from cli-schema.json -->
| Option | Description | Default |
|--------|-------------|---------|
| `--id` | Fetch the code (Gallery/Toolkit return XAML and/or C#; Reactor is C#-only) plus prerequisite notes for one or more scenario ids from a prior search (e.g. gallery-tabview-1). | (none) |
| `--json` | Format output as JSON | (none) |
| `--list` | List every discoverable control/sample id instead of searching. Covers Gallery, Toolkit, and core; the opt-in Reactor source is excluded (search it with --source reactor). | (none) |
| `--max` | Maximum number of matched controls to return. | `3` |
| `--refresh` | Bypass the local cache and re-fetch the WinUI corpus from GitHub. | (none) |
| `--source` | Restrict results to a single source: gallery (WinUI 3 Gallery), toolkit (Windows Community Toolkit), reactor (microsoft-ui-reactor, C#-only declarative WinUI), or core (curated patterns). Reactor is opt-in — it is excluded from a normal search, so pass --source reactor to search it (only do this for a Reactor/MVU project; its C#-only samples don't paste into a standard XAML app). | (none) |
