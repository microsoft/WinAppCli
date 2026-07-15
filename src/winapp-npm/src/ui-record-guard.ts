// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Hand-written guard wrapper for uiRecord.
 *
 * winapp-commands.ts is AUTO-GENERATED. The raw generated delegate for `ui record`
 * is intentionally NOT exported (underscore-prefixed, module-internal) so it cannot
 * bypass this guard. This module is the only public entry point for recording.
 *
 * The guard validates that `durationSec` is provided and positive before calling the
 * CLI, because unbounded recording (durationSec == 0) is only supportable via the CLI
 * with Ctrl+C or piped stdin — the npm wrapper has no mechanism to stop an unbounded
 * spawn (no AbortSignal, no stdin pass-through).
 *
 * This file must NOT be edited by the code generator; it is hand-maintained.
 */

import { callWinappCliCapture } from './winapp-cli-utils';
import type { CallWinappCliCaptureOptions, CallWinappCliCaptureResult } from './winapp-cli-utils';
import type { UiRecordOptions as GeneratedUiRecordOptions, WinappResult } from './winapp-commands';

/**
 * Stricter version of `UiRecordOptions` where `durationSec` is **required** (not optional).
 * This type is the public surface of `uiRecord`; the generated type has it optional.
 * Survives regeneration because it is defined here in the hand-written guard module.
 */
export type UiRecordOptions = Omit<GeneratedUiRecordOptions, 'durationSec'> & { durationSec: number };

/**
 * Builds the CLI argument list for `ui record` from a validated options object.
 * Named options are placed before the positional selector, and the selector is
 * placed after a `--` terminator so option-shaped selectors (e.g. `--capture-screen`)
 * are never misinterpreted as CLI flags.
 *
 * Exported for unit testing — do not use externally.
 * @internal
 */
export function buildUiRecordArgs(options: UiRecordOptions): string[] {
  const args: string[] = ['ui', 'record'];
  if (options.app) args.push('--app', options.app);
  if (options.captureScreen) args.push('--capture-screen');
  // durationSec is always set and > 0 (guarded by callers)
  args.push('--duration-sec', options.durationSec.toString());
  if (options.fps !== undefined) args.push('--fps', options.fps.toString());
  if (options.json) args.push('--json');
  if (options.maxEdge !== undefined) args.push('--max-edge', options.maxEdge.toString());
  if (options.output) args.push('--output', options.output);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  if (options.quiet) args.push('--quiet');
  if (options.verbose) args.push('--verbose');
  // Place the positional selector AFTER '--' so a selector like '--capture-screen' is
  // not misinterpreted as a CLI flag.
  if (options.selector) {
    args.push('--', options.selector);
  }
  return args;
}

/**
 * Record a window or element region to an H.264 MP4.
 *
 * **`durationSec` is required and must be > 0.** Unbounded recording (durationSec == 0) is only
 * supported via the CLI with Ctrl+C or piped stdin. The npm wrapper has no mechanism to stop
 * an unbounded spawn, so passing durationSec == 0 or omitting it will throw a clear error.
 *
 * @throws {Error} if `options.durationSec` is not provided or is ≤ 0.
 */
export async function uiRecord(options: UiRecordOptions): Promise<WinappResult> {
  // Runtime guard for JS callers who may pass undefined despite the TypeScript type.
  // durationSec must be a finite integer in [1, 86400]: reject NaN, ±Infinity, non-integers,
  // values < 1, and values > 86400 (CLI upper bound). durationSec == 0 (unbounded) is only
  // supported via the CLI with Ctrl+C or piped stdin — the npm wrapper has no way to stop it.
  if (
    typeof options.durationSec !== 'number' ||
    !Number.isFinite(options.durationSec) ||
    !Number.isInteger(options.durationSec) ||
    options.durationSec < 1 ||
    options.durationSec > 86400
  ) {
    throw new Error(
      `uiRecord: durationSec must be a finite integer in [1, 86400]. Got: ${options.durationSec}. ` +
        'Unbounded recording (durationSec == 0) is only supported via the CLI with Ctrl+C or piped stdin. ' +
        'Pass options.durationSec > 0.'
    );
  }

  const args = buildUiRecordArgs(options);
  const captureOpts: CallWinappCliCaptureOptions = options.cwd ? { cwd: options.cwd } : {};
  const result = await callWinappCliCapture(args, captureOpts);
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}

/**
 * Internal implementation that accepts an injectable capture function — used by tests
 * to verify the full success path without spawning a real process.
 * @internal
 */
export async function _uiRecordWithCapture(
  options: UiRecordOptions,
  capture: (args: string[], opts: CallWinappCliCaptureOptions) => Promise<CallWinappCliCaptureResult>
): Promise<WinappResult> {
  if (
    typeof options.durationSec !== 'number' ||
    !Number.isFinite(options.durationSec) ||
    !Number.isInteger(options.durationSec) ||
    options.durationSec < 1 ||
    options.durationSec > 86400
  ) {
    throw new Error(
      `uiRecord: durationSec must be a finite integer in [1, 86400]. Got: ${options.durationSec}. ` +
        'Unbounded recording (durationSec == 0) is only supported via the CLI with Ctrl+C or piped stdin. ' +
        'Pass options.durationSec > 0.'
    );
  }
  const args = buildUiRecordArgs(options);
  const captureOpts: CallWinappCliCaptureOptions = options.cwd ? { cwd: options.cwd } : {};
  const result = await capture(args, captureOpts);
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}
