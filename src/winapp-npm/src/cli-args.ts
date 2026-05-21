// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Small helpers for argv parsing shared across the npm wrapper's intercepted
// commands (`init`, `restore`, `node generate-bindings`). Keep these in sync
// with the native CLI option / argument shapes in src/winapp-CLI/.../Commands/.
//
// We deliberately do NOT use a full argument-parser library: the wrapper only
// needs to look at a handful of options that affect *where* it should read or
// write workspace files, and we want to forward the user's literal argv to
// the native CLI unchanged.

import * as path from 'path';

/** Options that consume the next argv token as a value (space-separated form). */
const VALUE_TAKING_OPTIONS = new Set(['--config-dir', '--config', '--setup-sdks']);

/** `--use-defaults` / `-y` / `--yes`. */
const USE_DEFAULTS_FLAGS = new Set(['--use-defaults', '--no-prompt', '-y', '--yes']);

/**
 * Resolve the effective workspace directory for the wrapper's local file
 * operations (package.json, .winapp/, bindings/ output). Mirrors how the
 * native CLI resolves `BaseDirectoryArgument` — first non-flag positional
 * argument wins; otherwise the current working directory.
 *
 * `--config-dir` is intentionally NOT consulted: it only changes where
 * winapp.yaml is read/written, not the workspace root.
 */
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

/** Detect `--config-only` — init's "skip package installation" mode. */
export function hasConfigOnly(args: readonly string[]): boolean {
  return args.includes('--config-only');
}

/** Detect `--use-defaults` / `--no-prompt` / `-y` / `--yes`. */
export function hasUseDefaults(args: readonly string[]): boolean {
  return args.some((a) => USE_DEFAULTS_FLAGS.has(a));
}

/**
 * Resolve the effective `winapp.yaml` path the native CLI will read, so the
 * orchestrator's staleness check (`yaml_packages_hash`) compares against the
 * SAME file native used. Mirrors `InitCommand`/`RestoreCommand` semantics:
 *
 *   --config-dir <DIR> / --config-dir=<DIR>  → <DIR>/winapp.yaml
 *   (no --config-dir)                        → <process.cwd()>/winapp.yaml
 *
 * Note: `--config-dir` defaults to **current directory**, NOT to the
 * `base-directory` positional. So a positional base-dir alone does NOT
 * change where the yaml is read from — only `--config-dir` does. Don't
 * derive the yaml location from `workspaceDir`.
 */
export function resolveYamlPath(args: readonly string[]): string {
  const explicit = extractConfigDir(args);
  const configDir = explicit ? path.resolve(explicit) : process.cwd();
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
