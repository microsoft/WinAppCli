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

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

import { JsBindingsExtraType } from './additional-winmds';

export interface JsBindingsConfig {
  // Target language. Currently 'js' (default) or 'py'.
  lang: string;
  // Output directory, relative to the workspace root.
  output: string;
  // NuGet package IDs to scope binding generation to (empty = all in scope).
  packages: string[];
  // Individual classes to generate alongside the bulk pass.
  extraTypes: JsBindingsExtraType[];
  // Extra .winmd files to emit bindings for.
  additionalWinmds: string[];
  // Extra .winmd files loaded for type resolution only.
  additionalRefs: string[];
  // NuGet package IDs to drop entirely.
  skipPackages: string[];
  // NuGet package IDs to load as --ref only.
  refOnlyPackages: string[];
  // NuGet package IDs to force-emit, overriding skip / ref-only.
  emitPackages: string[];
}

export function defaultJsBindingsConfig(): JsBindingsConfig {
  return {
    lang: 'js',
    output: 'bindings',
    packages: [],
    extraTypes: [],
    additionalWinmds: [],
    additionalRefs: [],
    skipPackages: [],
    refOnlyPackages: [],
    emitPackages: [],
  };
}

export interface ReadJsBindingsResult {
  /** True when package.json existed and parsed successfully. */
  packageJsonExists: boolean;
  /** Parsed jsBindings config, or null when the namespace isn't present. */
  jsBindings: JsBindingsConfig | null;
}

const PACKAGE_JSON = 'package.json';

/**
 * Read package.json from the workspace and return any
 * `"winapp": { "jsBindings": {...} }` namespace it declares.
 *
 * Missing file → `{ packageJsonExists: false, jsBindings: null }`.
 * Present file, no `winapp.jsBindings` → `{ packageJsonExists: true, jsBindings: null }`.
 * Malformed JSON propagates as an exception so callers can surface a clear
 * error rather than silently treating the workspace as un-configured.
 */
export function readJsBindingsConfig(workspaceDir: string): ReadJsBindingsResult {
  const filePath = path.join(workspaceDir, PACKAGE_JSON);
  if (!fs.existsSync(filePath)) {
    return { packageJsonExists: false, jsBindings: null };
  }
  const raw = fs.readFileSync(filePath, 'utf8');
  const parsed = JSON.parse(raw);
  const ns = parsed && typeof parsed === 'object' ? parsed.winapp : undefined;
  const block = ns && typeof ns === 'object' ? ns.jsBindings : undefined;
  if (!block || typeof block !== 'object') {
    return { packageJsonExists: true, jsBindings: null };
  }
  return { packageJsonExists: true, jsBindings: coerceConfig(block) };
}

/** Convenience: returns true when package.json declares `winapp.jsBindings`. */
export function hasJsBindings(workspaceDir: string): boolean {
  try {
    return readJsBindingsConfig(workspaceDir).jsBindings !== null;
  } catch {
    return false;
  }
}

/**
 * Write (or update) the `"winapp": { "jsBindings": {...} }` namespace in
 * package.json.
 *
 * Behaviour:
 *   * Preserves the existing 2-space indent + trailing newline. We do not
 *     pull in `prettier` for this single edit — JSON.stringify gives us a
 *     stable canonical layout and `package.json` is the only file we own.
 *   * Atomic: writes to a sibling temp file, fsyncs, then renames over the
 *     real file so a half-written package.json is never visible.
 *   * Inserts the `"winapp"` key at the end of the top-level object when it
 *     does not yet exist — npm tooling does not care about key order, and
 *     stable insertion keeps round-trips clean.
 *   * Throws when package.json is missing or malformed; callers should
 *     ensure the file exists (e.g. by suggesting `npm init -y`) before
 *     writing.
 */
export function writeJsBindingsConfig(workspaceDir: string, config: JsBindingsConfig): void {
  const filePath = path.join(workspaceDir, PACKAGE_JSON);
  if (!fs.existsSync(filePath)) {
    throw new Error(
      `package.json not found in ${workspaceDir}. ` +
        'Run `npm init -y` (or equivalent) before adding JS bindings configuration.'
    );
  }
  const raw = fs.readFileSync(filePath, 'utf8');
  const parsed = JSON.parse(raw);
  if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
    throw new Error(`Unexpected JSON shape in ${filePath}: top-level value must be an object.`);
  }

  const existingNs = parsed.winapp && typeof parsed.winapp === 'object' ? parsed.winapp : {};
  parsed.winapp = { ...existingNs, jsBindings: serializeConfig(config) };

  const eol = detectEol(raw);
  const trailing = raw.endsWith('\n') ? eol : '';
  const serialized = JSON.stringify(parsed, null, 2).replace(/\n/g, eol) + trailing;

  atomicWriteFileSync(filePath, serialized);
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
    lang: typeof r.lang === 'string' && r.lang.trim() ? r.lang.trim() : defaults.lang,
    output: typeof r.output === 'string' && r.output.trim() ? r.output.trim() : defaults.output,
    packages: coerceStringArray(r.packages),
    extraTypes: coerceExtraTypes(r.extraTypes),
    additionalWinmds: coerceStringArray(r.additionalWinmds),
    additionalRefs: coerceStringArray(r.additionalRefs),
    skipPackages: coerceStringArray(r.skipPackages),
    refOnlyPackages: coerceStringArray(r.refOnlyPackages),
    emitPackages: coerceStringArray(r.emitPackages),
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

function coerceExtraTypes(value: unknown): JsBindingsExtraType[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const out: JsBindingsExtraType[] = [];
  for (const v of value) {
    if (!v || typeof v !== 'object') {
      continue;
    }
    const r = v as Record<string, unknown>;
    const ns = typeof r.namespace === 'string' ? r.namespace.trim() : '';
    const classes = coerceStringArray(r.classes);
    if (ns) {
      out.push({ namespace: ns, classes });
    }
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
    lang: config.lang,
    output: config.output,
    packages: [...config.packages],
    extraTypes: config.extraTypes.map((et) => ({
      namespace: et.namespace,
      classes: [...et.classes],
    })),
    additionalWinmds: [...config.additionalWinmds],
    additionalRefs: [...config.additionalRefs],
    skipPackages: [...config.skipPackages],
    refOnlyPackages: [...config.refOnlyPackages],
    emitPackages: [...config.emitPackages],
  };
}

function detectEol(content: string): string {
  // Match the file's predominant line ending so we don't accidentally
  // rewrite CRLF → LF (or vice versa) on Windows checkouts.
  return content.includes('\r\n') ? '\r\n' : '\n';
}

function atomicWriteFileSync(filePath: string, content: string): void {
  const dir = path.dirname(filePath);
  const tmp = path.join(dir, `.${path.basename(filePath)}.${process.pid}.${Date.now()}.tmp`);
  let cleanup = true;
  try {
    const fd = fs.openSync(tmp, 'w');
    try {
      fs.writeFileSync(fd, content);
      try {
        fs.fsyncSync(fd);
      } catch {
        // fsync isn't supported on every platform (e.g. some FUSE mounts on
        // CI); the rename itself is enough for atomicity on POSIX and NTFS.
      }
    } finally {
      fs.closeSync(fd);
    }
    fs.renameSync(tmp, filePath);
    cleanup = false;
  } finally {
    if (cleanup) {
      try {
        fs.unlinkSync(tmp);
      } catch {
        // best-effort temp cleanup
      }
    }
  }
}

// Re-exported so callers don't have to know whether the implementation lives
// in this module or elsewhere.
export const PACKAGE_JSON_FILENAME = PACKAGE_JSON;
// Hint: os.EOL is intentionally unused — we prefer the file's existing EOL.
void os;
