// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Reads and writes the `"winapp": { "jsBindings": {...} }` namespace inside
// the workspace's package.json.
//
// Why package.json instead of winapp.yaml?
//   * `winapp.yaml` is owned by the native CLI and only describes SDK
//     `packages:` pins. Layering JS-only configuration in there meant the
//     native CLI had to either parse and ignore a JS-only block or risk
//     mangling unknown keys on round-trip.
//   * package.json already exists in every npm/Node workspace and is the
//     canonical place for Node-tool configuration (eslint, jest, prettier,
//     tsup, ...). The `"winapp"` key follows the same convention.
//   * The native CLI now has zero awareness of JS bindings — every code path
//     (init, restore, package, ...) is identical regardless of whether the
//     user opted into JS bindings.

import { AdditionalWinmd } from './additional-winmds';
import { readPackageJsonDoc, mutatePackageJsonDoc, packageJsonExists } from './package-json-doc';

export interface JsBindingsConfig {
  // Output directory, relative to the workspace root.
  output: string;
  // Extra .winmd files to feed into the codegen. Each entry either bulk-emits
  // the whole winmd or cherry-picks individual classes from it.
  additionalWinmds: AdditionalWinmd[];
  // Extra .winmd files loaded for type resolution only (no emit).
  additionalRefs: string[];
}

export function defaultJsBindingsConfig(): JsBindingsConfig {
  return {
    output: 'bindings',
    additionalWinmds: [],
    additionalRefs: [],
  };
}

export interface ReadJsBindingsResult {
  /** True when package.json existed and parsed successfully. */
  packageJsonExists: boolean;
  /** Parsed jsBindings config, or null when the namespace isn't present. */
  jsBindings: JsBindingsConfig | null;
}

/**
 * Read package.json from the workspace and return any
 * `"winapp": { "jsBindings": {...} }` namespace it declares.
 *
 * Missing file (or unsafe workspace path) → `{ packageJsonExists: false, jsBindings: null }`.
 * Present file, no `winapp.jsBindings` → `{ packageJsonExists: true, jsBindings: null }`.
 * Malformed JSON propagates as an exception so callers can surface a clear
 * error rather than silently treating the workspace as un-configured.
 */
export function readJsBindingsConfig(workspaceDir: string): ReadJsBindingsResult {
  const doc = readPackageJsonDoc(workspaceDir);
  if (!doc) {
    return { packageJsonExists: false, jsBindings: null };
  }
  const ns = doc.parsed.winapp;
  const block =
    ns && typeof ns === 'object' && !Array.isArray(ns) ? (ns as Record<string, unknown>).jsBindings : undefined;
  if (!block || typeof block !== 'object') {
    return { packageJsonExists: true, jsBindings: null };
  }
  return { packageJsonExists: true, jsBindings: coerceConfig(block) };
}

/**
 * Convenience: returns true when package.json declares `winapp.jsBindings`.
 * Propagates JSON parse errors (does NOT swallow them) — a malformed
 * package.json should fail the command with the actual parse error rather
 * than silently skip codegen. Callers should `try` around this if they need
 * to handle malformed input gracefully.
 */
export function hasJsBindings(workspaceDir: string): boolean {
  return readJsBindingsConfig(workspaceDir).jsBindings !== null;
}

/**
 * Outcome of {@link ensureJsBindingsBlock}.
 *   * `added`     — namespace was missing; default block written.
 *   * `reset`     — namespace existed but caller asked to overwrite it with defaults.
 *   * `unchanged` — namespace existed and caller did not request a reset.
 */
export type EnsureJsBindingsOutcome = 'added' | 'reset' | 'unchanged';

export interface EnsureJsBindingsOptions {
  /**
   * When true, overwrite an existing `winapp.jsBindings` block with the
   * default config. Use this when the user explicitly opted in again
   * (e.g. re-running `winapp init` and answering Yes after previously
   * customizing the block) — we never silently overwrite otherwise.
   */
  reset?: boolean;
  /** Suppress the informational banner printed to stdout. */
  quiet?: boolean;
}

/**
 * Make sure the workspace's package.json declares the
 * `winapp.jsBindings` namespace, then return what we did.
 *
 * Shared by `winapp init` (after a "yes" answer) and
 * `winapp node generate-bindings` (so the command works without making
 * the user hand-edit JSON before invoking it). NOT called from
 * `winapp restore` — restore must remain a passive "respect existing
 * declarations" operation and never silently add config the user did
 * not request.
 *
 * Requires package.json to exist; callers should fail with a clear
 * "this is not an npm project" error first when it does not.
 */
export function ensureJsBindingsBlock(
  workspaceDir: string,
  opts: EnsureJsBindingsOptions = {}
): EnsureJsBindingsOutcome {
  const current = readJsBindingsConfig(workspaceDir);
  if (!current.jsBindings) {
    writeJsBindingsConfig(workspaceDir, defaultJsBindingsConfig());
    if (!opts.quiet) {
      console.log(
        'ℹ️  Added "winapp.jsBindings" to package.json. ' + 'Edit it to customize package scope, extraTypes, etc.'
      );
    }
    return 'added';
  }
  if (opts.reset) {
    writeJsBindingsConfig(workspaceDir, defaultJsBindingsConfig());
    if (!opts.quiet) {
      console.log('ℹ️  Reset "winapp.jsBindings" in package.json to defaults.');
    }
    return 'reset';
  }
  return 'unchanged';
}

/**
 * Write (or update) the `"winapp": { "jsBindings": {...} }` namespace in
 * package.json.
 *
 * Behaviour:
 *   * Preserves the existing 2-space indent + trailing newline (via
 *     `mutatePackageJsonDoc`). We do not pull in `prettier` for this single
 *     edit — JSON.stringify gives us a stable canonical layout and
 *     `package.json` is the only file we own.
 *   * Atomic: writes to a sibling temp file, fsyncs, then renames over the
 *     real file so a half-written package.json is never visible.
 *   * Inserts the `"winapp"` key at the end of the top-level object when it
 *     does not yet exist — npm tooling does not care about key order, and
 *     stable insertion keeps round-trips clean.
 *   * Throws when package.json is missing or malformed; callers should
 *     ensure the file exists (e.g. by suggesting `npm init -y`) before
 *     writing.
 *   * Throws when the workspace path is UNC or has a reparse-point ancestor.
 */
export function writeJsBindingsConfig(workspaceDir: string, config: JsBindingsConfig): void {
  if (!packageJsonExists(workspaceDir)) {
    throw new Error(
      `package.json not found in ${workspaceDir}. ` +
        'Run `npm init -y` (or equivalent) before adding JS bindings configuration.'
    );
  }
  mutatePackageJsonDoc(workspaceDir, (parsed) => {
    const existingNs = parsed.winapp;
    const ns =
      existingNs && typeof existingNs === 'object' && !Array.isArray(existingNs)
        ? (existingNs as Record<string, unknown>)
        : {};
    parsed.winapp = { ...ns, jsBindings: serializeConfig(config) };
  });
}

/**
 * Hook for tests / future helpers: render the config block as it would be
 * embedded in package.json. Returns the JSON-serializable shape — callers
 * typically don't need this directly, but the orchestrator tests use it to
 * assert round-trip behaviour without re-implementing the schema.
 */
export function renderJsBindingsConfig(config: JsBindingsConfig): unknown {
  return serializeConfig(config);
}

// ---------------------------------------------------------------------------
// Internals
// ---------------------------------------------------------------------------

function coerceConfig(raw: unknown): JsBindingsConfig {
  const defaults = defaultJsBindingsConfig();
  if (!raw || typeof raw !== 'object') {
    return defaults;
  }
  const r = raw as Record<string, unknown>;

  return {
    output: typeof r.output === 'string' && r.output.trim() ? r.output.trim() : defaults.output,
    additionalWinmds: coerceAdditionalWinmds(r.additionalWinmds),
    additionalRefs: coerceStringArray(r.additionalRefs),
  };
}

function coerceStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const out: string[] = [];
  for (const v of value) {
    if (typeof v === 'string') {
      const trimmed = v.trim();
      if (trimmed) {
        out.push(trimmed);
      }
    }
  }
  return out;
}

function coerceAdditionalWinmds(value: unknown): AdditionalWinmd[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const out: AdditionalWinmd[] = [];
  for (const v of value) {
    if (!v || typeof v !== 'object') {
      continue;
    }
    const r = v as Record<string, unknown>;
    const winmdPath = typeof r.winmdPath === 'string' ? r.winmdPath.trim() : '';
    if (!winmdPath) {
      continue;
    }
    const ns = typeof r.namespace === 'string' ? r.namespace.trim() : '';
    const classes = coerceStringArray(r.classes);
    const entry: AdditionalWinmd = { winmdPath };
    if (ns && classes.length > 0) {
      entry.namespace = ns;
      entry.classes = classes;
    }
    out.push(entry);
  }
  return out;
}

/**
 * Serialize a JsBindingsConfig in a stable, schema-faithful shape:
 *   * keys are emitted in a fixed order so diffs stay clean across edits;
 *   * empty arrays are kept (they're documentation: "yes I considered this,
 *     and meant the empty default") rather than stripped.
 */
function serializeConfig(config: JsBindingsConfig): Record<string, unknown> {
  return {
    output: config.output,
    additionalWinmds: config.additionalWinmds.map((w) => {
      const entry: Record<string, unknown> = { winmdPath: w.winmdPath };
      if (w.namespace && w.classes && w.classes.length > 0) {
        entry.namespace = w.namespace;
        entry.classes = [...w.classes];
      }
      return entry;
    }),
    additionalRefs: [...config.additionalRefs],
  };
}

// Re-exported so callers don't have to know whether the implementation lives
// in this module or elsewhere.
export { PACKAGE_JSON_FILENAME } from './package-json-doc';
