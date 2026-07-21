// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { isDeepStrictEqual } from 'node:util';

import { AdditionalWinmd } from './additional-winmds';
import { readPackageJsonDoc, mutatePackageJsonDoc, packageJsonExists } from './package-json-doc';

// Fixed codegen output dir, mirroring C++ `.winapp/include` and auto-gitignored by init.
export const JS_BINDINGS_OUTPUT_DIR = '.winapp/bindings';
export const JS_BINDINGS_IMPORT = '#winapp/bindings';
export const JS_BINDINGS_SUBPATH_IMPORT = '#winapp/bindings/*';

// `package.json#imports` targets. Kept relative to package.json (co-located with
// `.winapp/bindings` by construction: both anchor on the same workspace dir),
// derived from `JS_BINDINGS_OUTPUT_DIR` so the paths can't drift.
const JS_BINDINGS_OUTPUT_REL = `./${JS_BINDINGS_OUTPUT_DIR}`;

const JS_BINDINGS_IMPORT_TARGET = {
  types: `${JS_BINDINGS_OUTPUT_REL}/index.d.ts`,
  import: `${JS_BINDINGS_OUTPUT_REL}/index.mjs`,
  require: `${JS_BINDINGS_OUTPUT_REL}/index.js`,
  default: `${JS_BINDINGS_OUTPUT_REL}/index.js`,
};

const JS_BINDINGS_SUBPATH_IMPORT_TARGET = {
  types: `${JS_BINDINGS_OUTPUT_REL}/*.d.ts`,
  default: `${JS_BINDINGS_OUTPUT_REL}/*.js`,
};

export interface JsBindingsConfig {
  // Extra .winmds to bulk-emit or cherry-pick.
  additionalWinmds: AdditionalWinmd[];
  // Extra .winmd files loaded for type resolution only.
  additionalRefs: string[];
}

export function defaultJsBindingsConfig(): JsBindingsConfig {
  return {
    additionalWinmds: [],
    additionalRefs: [],
  };
}

export interface ReadJsBindingsResult {
  /** True when package.json existed and parsed successfully. */
  packageJsonExists: boolean;
  /** Parsed config, or null when `winapp.jsBindings` isn't present. */
  jsBindings: JsBindingsConfig | null;
}

/** Read package.json and return `winapp.jsBindings` when present. */
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

/** Propagates malformed package.json errors instead of silently skipping codegen. */
export function hasJsBindings(workspaceDir: string): boolean {
  return readJsBindingsConfig(workspaceDir).jsBindings !== null;
}

export type EnsureJsBindingsOutcome = 'added' | 'reset' | 'unchanged';

export interface EnsureJsBindingsOptions {
  /** Only reset existing user config after explicit opt-in. */
  reset?: boolean;
  /** Suppress the informational banner printed to stdout. */
  quiet?: boolean;
  /** Override the log sink (e.g. to indent under a parent task header). */
  log?: (line: string) => void;
}

/** Ensure package.json declares `winapp.jsBindings`; explicit opt-in only, never restore. */
export function ensureJsBindingsBlock(
  workspaceDir: string,
  opts: EnsureJsBindingsOptions = {}
): EnsureJsBindingsOutcome {
  const log = opts.log ?? ((line: string) => console.log(line));
  const current = readJsBindingsConfig(workspaceDir);
  if (!current.jsBindings) {
    writeJsBindingsConfig(workspaceDir, defaultJsBindingsConfig());
    if (!opts.quiet) {
      log('💡 Added "winapp.jsBindings" to package.json. Edit `additionalWinmds` or `additionalRefs` to customize.');
    }
    return 'added';
  }
  if (opts.reset) {
    writeJsBindingsConfig(workspaceDir, defaultJsBindingsConfig());
    if (!opts.quiet) {
      log('💡 Reset "winapp.jsBindings" in package.json to defaults.');
    }
    return 'reset';
  }
  return 'unchanged';
}

/** Write/update `winapp.jsBindings`; shared helper preserves layout and atomic writes. */
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

export type EnsureJsBindingsImportsOutcome = 'added' | 'unchanged';

export interface EnsureJsBindingsImportsResult {
  outcome: EnsureJsBindingsImportsOutcome;
  /** Alias names that already exist in `imports` but point at a different target than
   *  the winapp defaults. Preserved as-is; the caller decides whether to warn. */
  diverged: readonly string[];
}

/** Add `#winapp/bindings` + `#winapp/bindings/*` to `package.json` `imports`. Existing
 *  aliases with a different target are preserved and reported via `diverged`. */
export function ensureJsBindingsImports(workspaceDir: string): EnsureJsBindingsImportsResult {
  const doc = readPackageJsonDoc(workspaceDir);
  if (!doc) {
    throw new Error(
      `package.json not found in ${workspaceDir}. ` + 'Run `npm init -y` (or equivalent) before mutating package.json.'
    );
  }
  const applied = withJsBindingsImports(doc.parsed.imports);
  if (!applied.changed) {
    // Nothing to add — skip the write so we don't renormalize formatting or
    // trigger file watchers just to report divergence.
    return { outcome: 'unchanged', diverged: applied.diverged };
  }
  mutatePackageJsonDoc(workspaceDir, (parsed) => {
    parsed.imports = withJsBindingsImports(parsed.imports).imports;
  });
  return { outcome: 'added', diverged: applied.diverged };
}

/** Render the JSON-serializable config shape embedded in package.json. */
export function renderJsBindingsConfig(config: JsBindingsConfig): unknown {
  return serializeConfig(config);
}

function coerceConfig(raw: unknown): JsBindingsConfig {
  const defaults = defaultJsBindingsConfig();
  if (!raw || typeof raw !== 'object') {
    return defaults;
  }
  const r = raw as Record<string, unknown>;

  return {
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
    const ns = typeof r.namespace === 'string' ? r.namespace.trim() : '';
    const classes = coerceStringArray(r.classes);
    // Drop entries that have nothing to emit: no path AND no cherry-pick target.
    if (!winmdPath && (!ns || classes.length === 0)) {
      continue;
    }
    const entry: AdditionalWinmd = {};
    if (winmdPath) {
      entry.winmdPath = winmdPath;
    }
    if (ns && classes.length > 0) {
      entry.namespace = ns;
      entry.classes = classes;
    }
    out.push(entry);
  }
  return out;
}

/** Stable key order; empty arrays remain explicit defaults in package.json. */
function serializeConfig(config: JsBindingsConfig): Record<string, unknown> {
  return {
    additionalWinmds: config.additionalWinmds.map((w) => {
      const entry: Record<string, unknown> = {};
      if (w.winmdPath) {
        entry.winmdPath = w.winmdPath;
      }
      if (w.namespace && w.classes && w.classes.length > 0) {
        entry.namespace = w.namespace;
        entry.classes = [...w.classes];
      }
      return entry;
    }),
    additionalRefs: [...config.additionalRefs],
  };
}

function withJsBindingsImports(existing: unknown): {
  imports: Record<string, unknown>;
  changed: boolean;
  diverged: string[];
} {
  if (existing !== undefined && (!existing || typeof existing !== 'object' || Array.isArray(existing))) {
    throw new Error(
      'package.json "imports" must be an object before adding JS bindings aliases. ' +
        'Edit package.json so "imports" is an object (or remove the field), then rerun ' +
        '`npx winapp init --add-js-bindings`.'
    );
  }

  const imports: Record<string, unknown> = { ...((existing as Record<string, unknown> | undefined) ?? {}) };
  const diverged: string[] = [];
  let changed = false;

  const entries: ReadonlyArray<readonly [string, Record<string, string>]> = [
    [JS_BINDINGS_IMPORT, JS_BINDINGS_IMPORT_TARGET],
    [JS_BINDINGS_SUBPATH_IMPORT, JS_BINDINGS_SUBPATH_IMPORT_TARGET],
  ];
  for (const [key, target] of entries) {
    const current = imports[key];
    if (current === undefined) {
      imports[key] = target;
      changed = true;
    } else if (!isDeepStrictEqual(current, target)) {
      // Preserve user-customized aliases; caller warns via `diverged`.
      diverged.push(key);
    }
  }
  return { imports, changed, diverged };
}

export { PACKAGE_JSON_FILENAME } from './package-json-doc';
