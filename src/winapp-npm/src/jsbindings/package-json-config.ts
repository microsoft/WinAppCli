// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { AdditionalWinmd } from './additional-winmds';
import { readPackageJsonDoc, mutatePackageJsonDoc, packageJsonExists } from './package-json-doc';

export interface JsBindingsConfig {
  // Output directory, relative to the workspace root.
  output: string;
  // Extra .winmd files to feed into codegen, either bulk-emitted or cherry-picked.
  additionalWinmds: AdditionalWinmd[];
  // Extra .winmd files loaded for type resolution only.
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
}

/** Ensure package.json declares `winapp.jsBindings`; explicit opt-in only, never restore. */
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
 * Write or update the `winapp.jsBindings` namespace in package.json.
 * `mutatePackageJsonDoc` preserves JSON layout and performs the atomic write.
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

/** Stable key order; empty arrays remain explicit defaults in package.json. */
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

export { PACKAGE_JSON_FILENAME } from './package-json-doc';
