## When to use

Use this skill when building a **WinUI 3** UI and you need to discover which
control fits an intent and get a real, working code example — without leaving the
CLI or guessing at control names and APIs.

`winapp find-ui` searches the **WinUI 3 Gallery**, the **Windows Community
Toolkit**, and the **microsoft-ui-reactor ReactorGallery** (plus a few curated
core patterns) and returns a working code snippet plus where it came from.

- **WinUI-only.** The corpus is WinUI 3 Gallery + Windows Community Toolkit +
  Reactor. It does **not** cover WPF, WinForms, or other UI frameworks.
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
winapp find-ui "flex layout" --source reactor

# Return more candidates
winapp find-ui "navigation" --max 6

# Browse everything (heavy — prefer search)
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
- **First run needs network.** The corpus is fetched from GitHub on first use and
  cached per-user under `<global .winapp>/cache/find-ui`. Subsequent runs are
  served from the cache (refreshed at most every 7 days, or on demand with
  `--refresh`). If the very first run is offline you'll get a clear "connect and
  run once" message.
- **Scenario ids** are stable within a cached corpus and look like
  `gallery-<control>-<n>` / `toolkit-<control>-<n>` / `reactor-<control>-<n>`; the
  `<source>-` prefix disambiguates controls that exist in more than one gallery
  (e.g. `ColorPicker`).
- **Exit codes** are script-friendly: `0` on a hit, `1` on no match / error.
- Keep queries **focused** (one feature per query) — the lexical ranker rewards
  specific phrasing. Batch multiple focused queries rather than one broad one.
