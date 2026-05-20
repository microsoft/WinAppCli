# JS / TypeScript bindings for WinRT (`jsBindings` feature)

`winapp` can generate typed JavaScript + TypeScript wrappers for Windows Runtime APIs as part of the standard `init` / `restore` flow. The generator runs on top of [dynwinrt](https://github.com/microsoft/dynwinrt) — a runtime FFI bridge that calls WinRT methods via `.winmd` metadata, so the produced bindings are **typed at compile time** but call WinRT **dynamically at runtime** (no native build step required from your project).

This document covers the user-facing CLI flow, the `winapp.yaml` schema, recipes for common scenarios, and a brief description of what happens under the hood.

> **Availability** — JS/TS bindings are gated behind invocation via the `@microsoft/winappcli` npm package (i.e. `npx winapp …`). The interactive bindings prompt on `winapp init` only appears when invoked through the npm shim, because the binding generator (`@microsoft/dynwinrt-codegen`) and the runtime (`@microsoft/dynwinrt`) ship as npm dependencies. The standalone winget / installer build does not surface the prompt.

---

## Quick start

The fastest path to "I want to call WinAppSDK / Windows Runtime APIs from my Node app":

```bash
npm i -D @microsoft/winappcli
npx winapp init --use-defaults     # auto-picks "Both" (C++ projections + JS bindings)
npm install                        # picks up the @microsoft/dynwinrt runtime dep that init injected
```

That gives you `bindings/winrt/*.js` + `*.d.ts` for the full Windows App SDK surface, ready to import:

```ts
import { LanguageModel } from './bindings/winrt/Microsoft.Windows.AI.Generative.LanguageModel';
const model = await LanguageModel.createAsync();
```

Want the interactive prompt instead? Omit `--use-defaults`:

```bash
npx winapp init
# > Bindings to generate:
#     C++ projections
#     JS/TS bindings
#   ❯ Both                         (default)
```

Already have a `winapp.yaml` and just want to add bindings on top? Edit the yaml to add an empty `jsBindings: {}` block (or a scoped one — see [workflow #3](#3-slice-generation-by-nuget-package)) and run `npx winapp restore`:

```yaml
# winapp.yaml
jsBindings: {}                     # full Windows App SDK surface
```

```bash
npx winapp restore
```

---

## Common workflows

> The yaml snippets below show only the fields each workflow touches. For the complete `jsBindings:` schema (every field, default values, type, composition rules), see [`winapp.yaml` — `jsBindings:` block](#winappyaml--jsbindings-block).

### 1. Generate bindings for the full WinAppSDK surface

```yaml
# winapp.yaml
jsBindings: {}
```

The empty block accepts the defaults: `lang: js`, `output: bindings/winrt`, and `packages: []` which means **every installed package's `.winmd` files participate**. Convenient for exploration; for a shipping app you may want to narrow `packages:` to just the APIs you actually call.

> XAML namespaces (`Microsoft.UI.Xaml.*`, `Windows.UI.Xaml.*`) are out of scope for dynwinrt — the codegen itself classifies them automatically as resolution-only refs, so no JS gets emitted for them regardless of which packages are in scope.

### 2. Skip C++ projections (JS-only project)

If your app is pure Node/Electron and you don't need cppwinrt headers, the bindings prompt's **JS/TS bindings** option (or `cppProjections: false` in `winapp.yaml`) skips the ~130 MB / ~20 s cppwinrt step entirely:

```yaml
# winapp.yaml
cppProjections: false
jsBindings: {}
```

```bash
npx winapp restore
```

### 3. Slice generation by NuGet package

When you don't want bindings for every installed package, list the NuGet package IDs you actually want bindings for:

```yaml
# winapp.yaml
jsBindings:
  output: bindings/winrt
  packages:
    - Microsoft.WindowsAppSDK.AI            # AI APIs only
    - Microsoft.WindowsAppSDK                # full WinAppSDK on top
```

Each entry must match a NuGet package ID present under your top-level `packages:` block. Empty / omitted means "all installed packages participate" (the default).

> Earlier versions supported namespace-prefix slicing (`includeNamespacePrefixes:` / `excludeNamespacePrefixes:`). Slicing now happens at the package level — coarser, but matches how WinRT metadata is actually shipped.

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

```yaml
jsBindings:
  output: src/generated/winrt
```

### 7. Re-run codegen after editing `winapp.yaml`

Any time you edit the `jsBindings:` block (add a package, swap to a different scope, add an `extraTypes:` entry), re-run:

```bash
npx winapp restore
```

`restore` reads the existing yaml without modifying it, re-discovers winmds, and re-runs codegen — the output directory is replaced atomically (stage-then-swap; previous bindings are preserved on codegen failure).

---

## `winapp.yaml` — `jsBindings:` block

Full schema with every field shown explicitly:

```yaml
packages:
  - name: Microsoft.WindowsAppSDK
    version: 1.8.39
  - name: Microsoft.WindowsAppSDK.AI
    version: 0.4.250712-experimental2

# Skip cppwinrt headers/libs/runtimes/projection generation. Defaults to true
# (C++ projections enabled). Set to false for pure Node/Electron projects that
# only consume JS bindings.
cppProjections: false

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

  # ── Per-package classification overrides ──────────────────────────────
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
| `cppProjections` (top-level) | `true` | bool |
| `jsBindings.lang` | `js` | string |
| `jsBindings.output` | `bindings/winrt` | string |
| `jsBindings.packages` | `[]` (= all installed packages) | list of NuGet IDs |
| `jsBindings.additionalWinmds` | `[]` | list of paths |
| `jsBindings.additionalRefs` | `[]` | list of paths |
| `jsBindings.extraTypes` | `[]` | list of `{namespace, classes[]}` |
| `jsBindings.skipPackages` | `[]` | list of NuGet IDs |
| `jsBindings.refOnlyPackages` | `[]` | list of NuGet IDs |
| `jsBindings.emitPackages` | `[]` | list of NuGet IDs |

### Composition rules (when multiple lists overlap)

The codegen applies these rules in order:

1. **Package scope** — if `packages:` is non-empty, only winmds inside those NuGet packages are taken from the package set; otherwise every installed package's winmds are taken. (Top-level `packages:` is the source of truth for what's installed; `jsBindings.packages` only filters which subset participates in JS-binding generation.)
2. **Per-package classification** — each in-scope package is classified into `emit` / `refOnly` / `skip` using the precedence:<br>**user `emitPackages` ⟶ default-skip ∪ user `skipPackages` ⟶ default-ref-only ∪ user `refOnlyPackages` ⟶ emit**.<br>Skip drops the winmd; ref-only routes it through `--ref`; emit produces JS bindings.
3. `additionalWinmds:` and `additionalRefs:` paths are appended to the codegen input. If a file is in both lists, `additionalWinmds:` wins.
4. **Auto-classification by codegen** — `Windows.*` system winmds (and any other namespace the codegen treats as a foundation namespace) are loaded as resolution-only refs even when you list them under `additionalWinmds:`. They will not produce JS files in bulk mode; use `extraTypes:` to pull individual classes out.
5. `extraTypes:` runs as a separate pass after the bulk pass — it can pull classes out of any loaded winmd (refs included).

---

## Runtime dependency injection

When `init` (or `restore`) runs the JS-bindings step on a workspace, the CLI:

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
              │ (init / restore)
              ▼
   ┌──────────────────────────────────────────┐
   │  WorkspaceSetupService                   │
   │  • restore NuGet packages                │
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

After a successful generation winapp writes `<output>/.dynwinrt-managed` into the output directory. Its presence is the **only** signal winapp uses to know that a non-empty output directory is safe to wipe before the next codegen run. **Never delete this file by hand**, and never put files you care about under the codegen output directory — the next `restore` will wipe anything other than the marker. (If the directory is non-empty and the marker is missing, winapp aborts rather than risk overwriting hand-written code.)

In addition, `winapp restore` writes `.winapp/winmds.lock.json` — a human-readable snapshot of every NuGet package the restore resolved, with its version, the per-package winmd discovery results, and the `JsBindingsPresets` category (`emit` / `refOnly` / `skip`). The lockfile is purely a diagnostic artifact:

- Useful to share when reporting bugs: it tells the maintainer exactly what got resolved without us needing to repro your NuGet feed setup.
- Records a SHA-256 of the top-level `packages:` block so you can spot yaml drift between restore runs.

**Write atomicity**: lockfile writes go through a per-call `.tmp.<guid>` sibling that's renamed into place on completion, so concurrent readers always see a consistent (old or new) file, never a torn write.

---

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| `winapp init` doesn't show a bindings prompt | You ran the standalone `winapp` (winget / installer). JS bindings ship as an npm-only feature. Install via `npm i -D @microsoft/winappcli` and call as `npx winapp init` to get the prompt. |
| `bindings/winrt/` is empty after restore | Most likely your `packages:` slice is too narrow, or matches no installed package. Check the debug log (`-v debug`) for the `winmd partition: emit=… ref-only=… skipped=…` line to see what got passed to the codegen. |
| Cannot find a class you expect | The codegen auto-classifies `Windows.*` (and similar foundation namespaces) as refs and does not bulk-emit them. Use `extraTypes:` to pull individual classes out: `{ namespace: 'Windows.Foundation', classes: ['Uri'] }`. |
| `winapp` refuses to write into the output directory | The output directory is non-empty and lacks a `.dynwinrt-managed` marker — winapp won't wipe it because it might contain hand-written code. Either point `output:` somewhere else, or delete the directory yourself if you're sure. |
| Imports from `@microsoft/dynwinrt` fail at app runtime | Make sure you ran your package manager's install command after `init` / `restore` (so the auto-injected production dep actually downloads). The CLI prints the right command for your PM in the output. |
| Vendor winmd not found | `additionalWinmds:` / `additionalRefs:` paths are workspace-relative or absolute. Missing files print a warning and are skipped (so a stale entry doesn't break a working restore) — re-check the path. |
| Want bindings but already ran `init` without them | Edit `winapp.yaml`, add `jsBindings: {}` (and optionally `cppProjections: false` if you don't want C++ projections), then run `npx winapp restore`. |

---

## See also

- [`@microsoft/dynwinrt`](https://github.com/microsoft/dynwinrt) — the runtime FFI bridge
- [`@microsoft/dynwinrt-codegen`](https://github.com/microsoft/dynwinrt) — the code-generation tool (lives in the same repo as `dynwinrt`)
- `winapp.yaml` schema reference (top-level): `packages:`, `cppProjections:`, `jsBindings:`
