// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Minimal argv helpers for wrapper hooks; native CLI still receives the original argv.

import * as path from 'path';

/** Options that consume the next argv token as a value (space-separated form). */
const VALUE_TAKING_OPTIONS = new Set(['--config-dir', '--config', '--setup-sdks']);

/** `--use-defaults` / `-y` / `--yes`. */
const USE_DEFAULTS_FLAGS = new Set(['--use-defaults', '--no-prompt', '-y', '--yes']);

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

/** Detect `--config-only` — init's "skip package installation" mode. */
export function hasConfigOnly(args: readonly string[]): boolean {
  return args.includes('--config-only');
}

/** Detect `--no-install` — opt out of auto-installing the runtime dependency. */
export function hasNoInstall(args: readonly string[]): boolean {
  return args.includes('--no-install');
}

/**
 * Flags the npm wrapper handles itself and that the native CLI does NOT
 * recognize. They must be stripped from any argv forwarded to the native CLI,
 * or it errors out on the unknown option.
 */
export const WRAPPER_ONLY_FLAGS: ReadonlySet<string> = new Set(['--no-install']);

/** Remove wrapper-only flags (e.g. `--no-install`) before forwarding to native. */
export function stripWrapperOnlyFlags(args: readonly string[]): string[] {
  return args.filter((a) => !WRAPPER_ONLY_FLAGS.has(a));
}

/** Detect `--use-defaults` / `--no-prompt` / `-y` / `--yes`. */
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
