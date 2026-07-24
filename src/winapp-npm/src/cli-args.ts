// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Minimal argv helpers for wrapper hooks; native CLI still receives the original argv.

import * as path from 'path';

/** Options that consume the next argv token as a value (space-separated form). */
const VALUE_TAKING_OPTIONS = new Set(['--config-dir', '--config', '--setup-sdks']);

const USE_DEFAULTS_FLAGS = new Set(['--use-defaults', '--no-prompt']);
const WRAPPER_ONLY_FLAGS = new Set(['--add-js-bindings']);

/** Resolve the workspace root used for package.json, .winapp, and bindings output. */
export function resolveWorkspaceDir(args: readonly string[]): string {
  const positional = firstPositional(args);
  return positional ? path.resolve(positional) : process.cwd();
}

/**
 * Return the first non-option positional argument, skipping any token that
 * is the value of a value-taking option (e.g. `--config-dir somedir` — `somedir`
 * is not a positional). Supports both `--opt value` and `--opt=value` forms.
 */
export function firstPositional(args: readonly string[]): string | undefined {
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg.startsWith('-')) {
      // `--opt=value` — entire token is consumed, never a value to skip.
      if (arg.includes('=')) continue;
      if (VALUE_TAKING_OPTIONS.has(arg)) {
        // Skip the next token (the option's value).
        i++;
      }
      continue;
    }
    return arg;
  }
  return undefined;
}

/** Detect `--verbose` / `-v` (anywhere in argv) for opting into noisy codegen logs. */
export function isVerbose(args: readonly string[]): boolean {
  return args.includes('--verbose') || args.includes('-v');
}

/** Detect `--quiet` / `-q` (anywhere in argv). Mirrors the native global option. */
export function isQuiet(args: readonly string[]): boolean {
  return args.includes('--quiet') || args.includes('-q');
}

/** Detect `--json` — wrapper hooks must suppress spinners/prompts under this flag. */
export function isJson(args: readonly string[]): boolean {
  return args.includes('--json');
}

/** Detect `--config-only` — init's "skip package installation" mode. */
export function hasConfigOnly(args: readonly string[]): boolean {
  return args.includes('--config-only');
}

/** Detect `--add-js-bindings` — explicit JS bindings opt-in for init automation. */
export function hasAddJsBindings(args: readonly string[]): boolean {
  return args.includes('--add-js-bindings');
}

/**
 * Resolve the boolean value of the native `--sparse` option the same way the native
 * System.CommandLine parser does, so wrapper routing matches native behavior:
 *   - `--sparse`                       -> true  (bare flag)
 *   - `--sparse true` / `--sparse false` -> the following token (space form)
 *   - `--sparse=true` / `--sparse:false` -> the inline value
 *   - absent                           -> false
 * A `--sparse` immediately followed by a non-boolean token (e.g. another option or a
 * path) is treated as a bare `true`, matching the native option's 0..1 arity.
 */
export function parseSparseFlag(args: readonly string[]): boolean {
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === '--sparse') {
      const next = args[i + 1];
      if (next !== undefined) {
        const lowered = next.toLowerCase();
        if (lowered === 'true') return true;
        if (lowered === 'false') return false;
      }
      return true;
    }
    if (a.startsWith('--sparse=') || a.startsWith('--sparse:')) {
      return a.substring('--sparse='.length).toLowerCase() !== 'false';
    }
  }
  return false;
}

/** Remove wrapper-only flags before forwarding to native. */
export function stripWrapperOnlyFlags(args: readonly string[]): string[] {
  return args.filter((a) => !WRAPPER_ONLY_FLAGS.has(a));
}

/** Detect `--use-defaults` / `--no-prompt`. */
export function hasUseDefaults(args: readonly string[]): boolean {
  return args.some((a) => USE_DEFAULTS_FLAGS.has(a));
}

/** Resolve the `winapp.yaml` path used for the lockfile staleness check. */
export function resolveYamlPath(args: readonly string[], defaultConfigDir: string = process.cwd()): string {
  const explicit = extractConfigDir(args);
  const configDir = explicit ? path.resolve(explicit) : path.resolve(defaultConfigDir);
  return path.join(configDir, 'winapp.yaml');
}

function extractConfigDir(args: readonly string[]): string | undefined {
  for (let i = 0; i < args.length; i++) {
    const a = args[i];
    if (a === '--config-dir' && i + 1 < args.length) {
      return args[i + 1];
    }
    if (a.startsWith('--config-dir=')) {
      return a.substring('--config-dir='.length);
    }
  }
  return undefined;
}
