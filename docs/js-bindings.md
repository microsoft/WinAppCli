# JS / TypeScript bindings for WinRT (`jsBindings` feature)

`winapp` can generate typed JavaScript + TypeScript wrappers for Windows Runtime APIs as part of the standard `init` / `restore` flow, or layered onto an existing workspace via the `node jsbindings add` sub-command. The generator runs on top of [dynwinrt](https://github.com/microsoft/dynwinrt) — a runtime FFI bridge that calls WinRT methods via `.winmd` metadata, so the produced bindings are **typed at compile time** but call WinRT **dynamically at runtime** (no native build step required from your project).

This document covers the user-facing CLI (both `init --js-bindings*` and `node jsbindings add`), the `winapp.yaml` schema, recipes for the common scenarios, and a brief description of what happens under the hood. It reflects the current state of the feature including the v2.0 codegen-owned input refactor.

> **Availability** — the `--js-bindings*` flags and the `node jsbindings add` sub-command are gated behind invocation via the `@microsoft/winappcli` npm package (i.e. `npx winapp …`). Running `winapp` from a winget / standalone install will reject these surfaces with a clear error message, because the JS-binding generator (`@microsoft/dynwinrt-codegen`) and the runtime (`@microsoft/dynwinrt`) ship as npm dependencies.

---

## Quick start

The fastest path to "I want to call the WinAppSDK AI APIs from my Node app":

```bash
npm i -D @microsoft/winappcli
npx winapp init --use-defaults --js-bindings-ai
npm install              # picks up the @microsoft/dynwinrt runtime dep that init injected
```

That gives you `bindings/winrt/*.js` + `*.d.ts` for the WinAppSDK AI surface, ready to import:

```ts
import { LanguageModel } from './bindings/winrt/Microsoft.Windows.AI.Generative.LanguageModel';
const model = await LanguageModel.createAsync();
```

Already have a `winapp.yaml` and just want to add bindings on top?

```bash
npx winapp node jsbindings add --ai
```

Same end-state, but layered onto an existing workspace — `packages:` is left untouched and only the `jsBindings:` block is added.

---

## Common workflows

> The yaml snippets below show only the fields each workflow touches. For the complete `jsBindings:` schema (every field, default values, type, composition rules), see [`winapp.yaml` — `jsBindings:` block](#winappyaml--jsbindings-block).

### 1. Generate bindings for the WinAppSDK AI APIs

```bash
npx winapp init --use-defaults --js-bindings-ai
```

The `ai` preset narrows binding generation to the `Microsoft.WindowsAppSDK.AI` NuGet package. All other installed packages are still restored for the C# / native build, just not turned into JS bindings.

### 2. Generate bindings for the full WinAppSDK surface

```bash
npx winapp init --js-bindings
```

Without a preset, every installed package's `.winmd` files participate in binding generation (plus any winmds added via `additionalWinmds:`). Convenient for exploration; for a shipping app prefer the `--js-bindings-ai` preset or a hand-curated `packages:` list.

> XAML namespaces (`Microsoft.UI.Xaml.*`, `Windows.UI.Xaml.*`) are out of scope for dynwinrt — the codegen itself classifies them automatically as resolution-only refs, so no JS gets emitted for them regardless of which packages are in scope.

### 3. Slice generation by NuGet package

When the `ai` preset is too narrow but you also don't want bindings for every installed package, edit `winapp.yaml` and list the NuGet package IDs you actually want bindings for:

```yaml
# winapp.yaml
jsBindings:
  output: bindings/winrt
  packages:
    - Microsoft.WindowsAppSDK.AI            # AI APIs
    - Microsoft.WindowsAppSDK                # full WinAppSDK on top
```

Each entry must match a NuGet package ID present under your top-level `packages:` block. Empty / omitted means "all installed packages participate" (the v2 default).

> v2.0 removed the older namespace-prefix slicing (`includeNamespacePrefixes:` / `excludeNamespacePrefixes:`). Slicing now happens at the package level — coarser, but matches how WinRT metadata is actually shipped.

### 4. Add your own / a vendor `.winmd`

```yaml
# winapp.yaml
jsBindings:
  output: bindings/winrt
  additionalWinmds:
    - vendor/MyCompany.Foo.winmd          # relative to workspace root
    - C:\shared\OtherSdk.winmd            # absolute also works
```

`additionalWinmds:` files are appended to the codegen input alongside the package-discovered winmds. Use this when you want bindings emitted for the entire vendor file.

### 5. Cherry-pick a few classes from a giant vendor SDK

```yaml
jsBindings:
  output: bindings/winrt
  additionalRefs:                         # load for resolution only — NO bulk emit
    - vendor/BigVendor.SDK.winmd
  extraTypes:                             # explicitly list classes to emit
    - namespace: BigVendor.Camera
      classes:
        - Lens
        - Sensor
```

This is the right pattern when the vendor ships a 200 MB winmd and you only want two classes. The codegen loads the metadata for type resolution but only emits bindings for `Lens` and `Sensor`. The same pattern works for cherry-picking from system `Windows.*` winmds, which the codegen always treats as refs.

> If the same path appears in both `additionalWinmds:` and `additionalRefs:`, `additionalWinmds:` wins (emission is the stronger intent).

### 6. Override the output directory

```bash
npx winapp init --js-bindings --js-bindings-output src/generated/winrt
```

Or via `winapp.yaml`:

```yaml
jsBindings:
  output: src/generated/winrt
```

For `node jsbindings add`, use `--output` (the sub-command name already scopes it):

```bash
npx winapp node jsbindings add --ai --output src/generated/winrt
```

### 7. Re-init: opt into jsBindings on an existing workspace

If you ran `init` without bindings and later want them, prefer the layered `node jsbindings add` flow — it touches **only** the `jsBindings:` block and runs codegen against your already-restored packages, skipping the SDK installation steps entirely:

```bash
npx winapp node jsbindings add --ai          # add the AI preset
npx winapp node jsbindings add               # add the full surface (no preset)
```

If a `jsBindings:` block already exists, the command refuses by default to avoid clobbering hand edits. Pass `--force` to overwrite without prompting:

```bash
npx winapp node jsbindings add --ai --force
```

Re-running `winapp init --js-bindings` on an existing workspace is also supported (older flow), but it will go through the full restore pipeline; `node jsbindings add` is the recommended way to add bindings to an already-initialized project.

---

## CLI reference

### `winapp init` — parent flag

| Flag | Type | Description |
|------|------|-------------|
| `--js-bindings` | bool | Enable jsBindings codegen as part of init/restore. Adds a `jsBindings:` block to `winapp.yaml`. Required to activate any of the sub-options below — except the alias flags, which imply it. |

### `winapp init` — sub-options (effective only when `--js-bindings` is active)

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--js-bindings-output PATH` | string | `bindings/winrt` | Override the output directory (relative to workspace root). |
| `--js-bindings-lang js` | string | `js` | Target language. `js` emits both `.js` + `.d.ts`. Reserved for forward-compat (`py` exists in `dynwinrt-codegen` but is not yet wired through here). |

### `winapp init` — preset alias flags (auto-generated, one per preset)

Each entry in [the preset table](#presets) gets a corresponding bool flag. Alias flags **imply `--js-bindings`** — you don't need to type the parent flag.

| Flag | Effect |
|------|--------|
| `--js-bindings-ai` | Enable jsBindings + write the `ai` preset's package IDs to `packages:` |

> Today only the `ai` preset ships. The CLI auto-registers one alias flag per entry in `JsBindingsPresets.KnownPresets`, so adding a future preset is a one-line change with no CLI plumbing.

### `winapp node jsbindings add` — sub-command

Layered onto an already-initialized workspace. Requires an existing `winapp.yaml`; never installs SDK packages or rewrites the top-level `packages:` block. The job is to add (or replace, with `--force`) the `jsBindings:` block and run codegen against the workspace's restored packages.

| Flag | Type | Default | Description |
|------|------|---------|-------------|
| `--config-dir PATH` | path | current dir | Directory containing `winapp.yaml`. |
| `--output PATH` | string | `bindings/winrt` | Output directory for generated `.js` + `.d.ts`. Persisted to `jsBindings.output`. |
| `--force` | bool | `false` | Replace an existing `jsBindings:` block without prompting. |
| `--ai` | bool | `false` | Generate bindings for the `ai` preset only (writes its package IDs to `packages:`). One auto-registered flag per entry in `JsBindingsPresets.KnownPresets`. |

The first positional argument is the workspace base directory (defaults to the current directory).

---

## `winapp.yaml` — `jsBindings:` block

Full schema with every field shown explicitly:

```yaml
packages:
  - name: Microsoft.WindowsAppSDK
    version: 1.8.39
  - name: Microsoft.WindowsAppSDK.AI
    version: 0.4.250712-experimental2

jsBindings:
  # Target language — currently only 'js' (emits both .js and .d.ts).
  # 'py' is supported in the underlying codegen but not yet exposed here.
  lang: js

  # Output directory for generated .js + .d.ts (relative to workspace root).
  output: bindings/winrt

  # NuGet package IDs to scope binding generation to. When non-empty, only
  # .winmd files from these packages flow into the codegen (everything else
  # under the top-level `packages:` block is still installed for the C# /
  # native build, just not turned into JS bindings). Each entry must match
  # a package ID present in the top-level `packages:` block.
  # When empty / omitted, every installed package participates.
  packages:
    - Microsoft.WindowsAppSDK.AI

  # Extra .winmd files to feed into the codegen alongside package-discovered
  # ones. Each entry is bulk-emitted (gets full bindings).
  # Paths: relative to workspace root, OR absolute. Missing files = warning.
  additionalWinmds:
    - vendor/MyCompany.Foo.winmd
    - C:\shared\OtherSdk.winmd

  # Like additionalWinmds, but LOAD-ONLY: the metadata is available for
  # resolution (and for extraTypes lookups below) but no bulk emit happens.
  # Pair with extraTypes to cherry-pick from large vendor SDKs.
  additionalRefs:
    - vendor/BigVendor.SDK.winmd

  # Per-class explicit picks. Searches across all loaded winmds (package +
  # additionalWinmds + additionalRefs + system Windows.*). Useful for grabbing
  # one or two classes from a winmd you don't want fully emitted.
  extraTypes:
    - namespace: BigVendor.Camera
      classes:
        - Lens
        - Sensor

  # ── Per-package classification overrides (v2.3+) ──────────────────────
  # Layered on top of the built-in default policy (WinUI = skip,
  # InteractiveExperiences = ref-only). Useful when MS introduces a new XAML
  # package or you want to force-emit a normally-denylisted one.

  # Force-skip: drop entirely, no .js emit, not loaded as ref either.
  skipPackages:
    - Some.New.WinUI.Package

  # Force-ref-only: load for type resolution (--ref channel) but no .js emit.
  refOnlyPackages:
    - Vendor.PrimitiveTypes

  # Force-emit: overrides default skip / ref-only / user skip / user ref-only.
  # Use to opt back in to a denylisted package for experimentation.
  emitPackages:
    - Microsoft.WindowsAppSDK.WinUI
```

### Field defaults at a glance

| Field | Default | Type |
|-------|---------|------|
| `lang` | `js` | string |
| `output` | `bindings/winrt` | string |
| `packages` | `[]` (= all installed packages) | list of NuGet IDs |
| `additionalWinmds` | `[]` | list of paths |
| `additionalRefs` | `[]` | list of paths |
| `extraTypes` | `[]` | list of `{namespace, classes[]}` |
| `skipPackages` | `[]` | list of NuGet IDs |
| `refOnlyPackages` | `[]` | list of NuGet IDs |
| `emitPackages` | `[]` | list of NuGet IDs |

### Composition rules (when multiple lists overlap)

The codegen applies these rules in order:

1. **Package scope** — if `packages:` is non-empty, only winmds inside those NuGet packages are taken from the package set; otherwise every installed package's winmds are taken. (Top-level `packages:` is the source of truth for what's installed; `jsBindings.packages` only filters which subset participates in JS-binding generation.)
2. **Per-package classification** — each in-scope package is classified into `emit` / `refOnly` / `skip` using the precedence:<br>**user `emitPackages` ⟶ default-skip ∪ user `skipPackages` ⟶ default-ref-only ∪ user `refOnlyPackages` ⟶ emit**.<br>Skip drops the winmd; ref-only routes it through `--ref`; emit produces JS bindings.
3. `additionalWinmds:` and `additionalRefs:` paths are appended to the codegen input. If a file is in both lists, `additionalWinmds:` wins.
4. **Auto-classification by codegen** — `Windows.*` system winmds (and any other namespace the codegen treats as a foundation namespace) are loaded as resolution-only refs even when you list them under `additionalWinmds:`. They will not produce JS files in bulk mode; use `extraTypes:` to pull individual classes out.
5. `extraTypes:` runs as a separate pass after the bulk pass — it can pull classes out of any loaded winmd (refs included).

---

## Presets

Presets are named bundles of NuGet **package IDs** that get written into the `jsBindings.packages:` list. Today only one preset ships — `ai` — because that's the use case this feature was built for: a one-flag path to the WinAppSDK AI APIs. For anything else, edit `winapp.yaml` directly (see [workflow #3](#3-slice-generation-by-nuget-package)).

| Preset | `init` flag | `node jsbindings add` flag | Package IDs | Notes |
|--------|------------|------------------------|-------------|-------|
| `ai` | `--js-bindings-ai` | `--ai` | `Microsoft.WindowsAppSDK.AI` | Single-package preset; the codegen handles foundation namespaces (`Microsoft.Foundation`, `Windows.*`) automatically as refs. |

To add a new preset, edit `JsBindingsPresets.KnownPresets` in `WinApp.Cli/Services/JsBindingsPresets.cs`. Both the `init` alias flag and the matching `node jsbindings add` flag are auto-registered from this dictionary at startup — no other code changes required.

---

## Runtime dependency injection

When `init --js-bindings*` (or `node jsbindings add`) runs for the first time on a workspace, the CLI:

1. Detects your project's package manager from the `packageManager:` field in `package.json`, then falls back to lockfile sniffing (`pnpm-lock.yaml` → pnpm, `yarn.lock` → yarn, `bun.lockb` → bun, `package-lock.json` → npm). Defaults to **npm** if no signal exists.
2. Adds `@microsoft/dynwinrt` to your `package.json` `dependencies` (production dep, NOT devDep) — your generated bindings `import` from it at module load, so it must ship in your installed app.
3. Prints a PM-aware install hint (`npm install` / `pnpm install` / `yarn install` / `bun install`) so you know what to run next.

Supported package managers: **npm, pnpm, yarn, bun**.

> Why production not devDep? `@microsoft/dynwinrt` provides the runtime FFI bridge — without it, your generated `bindings/winrt/*.js` files fail to load at runtime. It's not a build-only tool.

---

## How it works under the hood

```
   ┌─────────────────────┐
   │  winapp.yaml        │ — packages: + jsBindings: blocks
   └──────────┬──────────┘
              │ (init / restore / node jsbindings add)
              ▼
   ┌──────────────────────────────────────────┐
   │  WorkspaceSetupService                   │
   │  • restore NuGet packages (init/restore) │
   │  • discover .winmd files in installed    │
   │    packages, scoped by                   │
   │    jsBindings.packages if set            │
   │  • resolve additionalWinmds /            │
   │    additionalRefs paths                  │
   └──────────┬───────────────────────────────┘
              │
              ▼
   ┌──────────────────────────────────────────┐
   │  DynWinrtCodegenService                  │
   │  • partition winmds: emit / ref-only /   │
   │    skip (per JsBindingsPresets policy)   │
   │  • safety-check output dir               │
   │    (.dynwinrt-managed marker)            │
   │  • spawn @microsoft/dynwinrt-codegen     │
   │    --winmd "p1;p2;..." --ref "r1;..."    │
   │  • write .dynwinrt-managed marker        │
   └──────────┬───────────────────────────────┘
              │
              ▼
   ┌──────────────────────────────────────────┐
   │  @microsoft/dynwinrt-codegen             │
   │  • loads emit winmds + ref winmds        │
   │  • auto-classifies Windows.* as          │
   │    resolution-only refs                  │
   │  • generates .js + .d.ts                 │ → bindings/winrt/<Namespace>.<Class>.{js,d.ts}
   └──────────────────────────────────────────┘
              │ (at app runtime)
              ▼
   ┌──────────────────────────────────────────┐
   │  @microsoft/dynwinrt                     │ (production dep injected into your package.json)
   │  • libffi-backed dynamic invocation      │
   │  • COM marshaling, async, delegates      │
   └──────────────────────────────────────────┘
```

### Per-package winmd categorization

Some WinAppSDK packages ship `.winmd` files that dynwinrt cannot drive at runtime (XAML composables, UI Composition, DispatcherQueue). To keep the generated tree usable, winapp applies a **package-level policy** before handing winmds to the codegen:

| Package | Category | Why |
|---------|----------|-----|
| `Microsoft.WindowsAppSDK.WinUI` | **Skip** | Pure XAML composables — `Button`, `Page`, `Application` etc. dynwinrt has no way to host. |
| `Microsoft.WindowsAppSDK.InteractiveExperiences` | **Ref-only** | Ships `Microsoft.UI.WindowId`, `Microsoft.Graphics.PointInt32`, `Microsoft.UI.Color` and other primitive types widely referenced by Foundation/Storage/Notifications APIs — must stay loaded for type resolution, but its own runtime classes are XAML/Composition types winapp cannot drive. |
| Everything else | **Emit** | Bulk-generate JS bindings (codegen still auto-classifies `Windows.*` as refs internally). |

This split happens in `JsBindingsPresets.PartitionByPackageCategory`. Skipped winmds aren't passed to the codegen at all; ref-only winmds flow through the codegen `--ref` channel.

**Escape hatch**: if you need the contents of a Skip/Ref-only package (vendor fork, experimentation), list its winmd files explicitly under `jsBindings.additionalWinmds:` — those flow through the user-additional channel and bypass the policy above.

### The `.dynwinrt-managed` marker and `winmds.lock.json`

After a successful generation winapp writes `<output>/.dynwinrt-managed` into the output directory. Its presence is the **only** signal winapp uses to know that a non-empty output directory is safe to wipe before the next codegen run. **Never delete this file by hand**, and never put files you care about under the codegen output directory — the next `restore` / `node jsbindings add` will wipe anything other than the marker. (If the directory is non-empty and the marker is missing, winapp aborts rather than risk overwriting hand-written code.)

In addition, `winapp restore` writes `.winapp/winmds.lock.json` — a human-readable snapshot of every NuGet package the restore resolved, with its version, the per-package winmd discovery results, and the `JsBindingsPresets` category (`emit` / `refOnly` / `skip`). The lockfile is purely an **optimization + audit trail**:

- `winapp node jsbindings add` reads it to skip the NuGet `.nuspec` HTTP roundtrip + cache re-glob (typically reduces `node jsbindings add` from ~3s to ~200ms in offline / poor-network conditions).
- When the lockfile is missing or its schema doesn't match, `node jsbindings add` transparently falls back to live discovery — no functional dependency on the file.
- Useful to share when reporting bugs: it tells the maintainer exactly what got resolved without us needing to repro your NuGet feed setup.

**Staleness checks** (v2.3): the lockfile records a SHA-256 of the top-level `packages:` block, and `node jsbindings add` rejects the fast path when:

1. **yaml drift** — current `packages:` hash differs from the one recorded → user edited yaml since restore.
2. **stale paths** — any winmd path recorded in the lockfile no longer exists on disk → NuGet cache was cleared since restore.

In both cases `node jsbindings add` falls back to live discovery and tells the user to consider re-running `winapp restore` (which rewrites the lockfile). The fallback path also re-runs the per-package classification (`skipPackages` / `refOnlyPackages` / `emitPackages` overrides take effect immediately — no restore required).

**Write atomicity**: lockfile writes go through a per-call `.tmp.<guid>` sibling that's renamed into place on completion, so concurrent readers always see a consistent (old or new) file, never a torn write.

This contract is what lets the codegen own all metadata-classification logic (refs vs bulk, `Windows.*` defaults, etc.) without winapp having to maintain a parallel C# implementation of the same rules.

---

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| `Error: --js-bindings requires the @microsoft/winappcli npm package` | You ran `winapp` from a winget / standalone install. JS-binding codegen ships as an npm transitive dep — install via `npm i -D @microsoft/winappcli` and call as `npx winapp …`. |
| `bindings/winrt/` is empty after restore | Most likely your `packages:` slice is too narrow, or matches no installed package. Check the debug log (`-v debug`) for the `winmd partition: emit=… ref-only=… skipped=…` line to see what got passed to the codegen. |
| Cannot find a class you expect | The codegen auto-classifies `Windows.*` (and similar foundation namespaces) as refs and does not bulk-emit them. Use `extraTypes:` to pull individual classes out: `{ namespace: 'Windows.Foundation', classes: ['Uri'] }`. |
| `winapp` refuses to write into the output directory | The output directory is non-empty and lacks a `.dynwinrt-managed` marker — winapp won't wipe it because it might contain hand-written code. Either point `output:` somewhere else, or delete the directory yourself if you're sure. |
| Imports from `@microsoft/dynwinrt` fail at app runtime | Make sure you ran your package manager's install command after `init` / `node jsbindings add` (so the auto-injected production dep actually downloads). The CLI prints the right command for your PM in the output. |
| Vendor winmd not found | `additionalWinmds:` / `additionalRefs:` paths are workspace-relative or absolute. Missing files print a warning and are skipped (so a stale entry doesn't break a working restore) — re-check the path. |
| `--js-bindings-output / --js-bindings-lang have no effect without --js-bindings; ignoring.` | You passed a sub-option without `--js-bindings`. Either add `--js-bindings`, or use a `--js-bindings-{preset}` alias flag (which implies it). |
| `node jsbindings add` errors with "jsBindings: already present" | Pass `--force` to replace the existing block without prompting; without it the command refuses to clobber hand-edited config. |

---

## Changelog (feature evolution)

The feature has shipped in incremental waves; user-visible additions:

| Version | Headline addition |
|---------|-------------------|
| **v1.0** | Manifest-driven codegen; `init --js-bindings` parent flag; `bindings.manifest.json` written under `.winapp/codegen/`. |
| **v1.1** | XAML namespaces (`Microsoft.UI.Xaml`, `Windows.UI.Xaml`) excluded by default — out of scope for dynwinrt. |
| **v1.2** | `@microsoft/dynwinrt` auto-injected as a production dep in user `package.json`; PM-aware install hint (npm / pnpm / yarn / bun). |
| **v1.4** | `--js-bindings-output` / `--js-bindings-lang` CLI flags; `additionalWinmds:` and `includeNamespacePrefixes:` yaml fields; presets (ai / webview / widgets / appnotifications); re-init UX. |
| **v1.5** | `additionalRefs:` yaml field — load winmds for resolution only, pair with `extraTypes:` to cherry-pick classes from large vendor SDKs without bulk-emitting. |
| **v1.6** | `--js-bindings-{preset}` shorthand alias flags; imply `--js-bindings`; auto-generated from the `KnownPresets` dictionary so adding a preset auto-exposes a flag. **Removed** the now-redundant `--js-bindings-only` flag — the alias flags fully supersede it. |
| **v1.7** | Trimmed shipped presets down to **`ai` only** (the actual goal of this feature: easy on-ramp to WinAppSDK AI APIs). The `webview` / `widgets` / `appnotifications` presets were removed. The dictionary + auto-alias machinery is preserved so a future curated AI sub-slice can be added with one line. (At the time, users wanting those namespaces were directed to write `includeNamespacePrefixes:` directly — that field has since been removed in v2.0; use `jsBindings.packages:` to slice by NuGet package, or `additionalWinmds:` to hand-pick winmd files.) |
| **v1.8** | New `winapp node jsbindings add` sub-command — layered, non-destructive way to add bindings to an existing workspace without going through the full restore pipeline. Auto-registers one `--<preset>` flag per entry in `KnownPresets` (e.g. `--ai`). `--force` to replace an existing block, `--output PATH` to override the output dir. |
| **v2.0** | Codegen-owned input refactor. Replaced the JSON manifest (`.winapp/codegen/bindings.manifest.json`) with direct command-line passing of winmd paths to the codegen (`--winmd "p1;p2;..."` / `--ref "r1;r2;..."`). Removed `excludeNamespacePrefixes:` / `includeNamespacePrefixes:` / `importName:` from `winapp.yaml` — `Windows.*` / XAML classification now happens entirely inside the codegen, and slicing happens at the **NuGet package** level via the new `packages:` field instead of namespace prefixes. The `ai` preset now expands to package IDs (`Microsoft.WindowsAppSDK.AI`) rather than namespace prefixes. A `.dynwinrt-managed` marker file inside the output dir gates safe re-wipes. |
| **v2.1** | Per-package winmd categorization (emit / ref-only / skip) added to `JsBindingsPresets`. The `Microsoft.WindowsAppSDK.WinUI` package is now dropped entirely from JS bindings (pure XAML, unusable at dynwinrt runtime); `Microsoft.WindowsAppSDK.InteractiveExperiences` flows through `--ref` only (its primitive types stay available for type resolution but no bindings are emitted for the XAML/Composition runtime classes it ships). |
| **v2.2** | `.winapp/winmds.lock.json` audit + cache artifact. `winapp restore` records every resolved (package, version, category, winmd paths) tuple to a versioned JSON lockfile; `winapp node jsbindings add` reads it first for a no-HTTP-no-glob fast path. Transparent fallback to live discovery when the file is missing or schema-mismatched, so older workspaces keep working unchanged. |
| **v2.3** | Lockfile gets staleness detection (SHA-256 of yaml `packages:` block + winmd path existence check) and atomic write (tmp + rename) — drift between `restore` and `node jsbindings add` no longer silently uses stale data. New yaml fields `skipPackages` / `refOnlyPackages` / `emitPackages` let users override the built-in per-package classification (`Microsoft.WindowsAppSDK.WinUI` = skip, `.InteractiveExperiences` = ref-only). `node jsbindings add --force` now patches the existing `jsBindings:` block instead of overwriting it from scratch — user-edited `extraTypes:` / `additionalWinmds:` / etc. survive. Changing the `output:` directory wipes the old managed bindings (if `.dynwinrt-managed` marker present). |

---

## See also

- [`@microsoft/dynwinrt`](https://github.com/microsoft/dynwinrt) — the runtime FFI bridge
- [`@microsoft/dynwinrt-codegen`](https://github.com/microsoft/dynwinrt) — the code-generation tool (lives in the same repo as `dynwinrt`)
- `winapp.yaml` schema reference (top-level): `packages:`, `jsBindings:`
