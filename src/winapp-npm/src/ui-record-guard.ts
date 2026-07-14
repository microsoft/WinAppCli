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
import type { CallWinappCliCaptureOptions } from './winapp-cli-utils';
import type { UiRecordOptions, WinappResult } from './winapp-commands';

export type { UiRecordOptions };

/**
 * Record a window or element region to an H.264 MP4.
 *
 * **`durationSec` is required and must be > 0.** Unbounded recording (durationSec == 0) is only
 * supported via the CLI with Ctrl+C or piped stdin. The npm wrapper has no mechanism to stop
 * an unbounded spawn, so passing durationSec == 0 or omitting it will throw a clear error.
 *
 * @throws {Error} if `options.durationSec` is not provided or is ≤ 0.
 */
export async function uiRecord(options: UiRecordOptions = {}): Promise<WinappResult> {
  if (!options.durationSec || options.durationSec <= 0) {
    throw new Error(
      'uiRecord requires a positive durationSec ' +
        '(unbounded recording is only supported via the CLI with Ctrl+C or piped stdin). ' +
        'Pass options.durationSec > 0.'
    );
  }

  // Build args mirroring the generated _uiRecordGenerated (kept in sync with the CLI schema).
  const args: string[] = ['ui', 'record'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.captureScreen) args.push('--capture-screen');
  // durationSec is always set and > 0 (guarded above)
  args.push('--duration-sec', options.durationSec.toString());
  if (options.fps !== undefined) args.push('--fps', options.fps.toString());
  if (options.json) args.push('--json');
  if (options.maxEdge !== undefined) args.push('--max-edge', options.maxEdge.toString());
  if (options.output) args.push('--output', options.output);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  if (options.quiet) args.push('--quiet');
  if (options.verbose) args.push('--verbose');

  const captureOpts: CallWinappCliCaptureOptions = options.cwd ? { cwd: options.cwd } : {};
  const result = await callWinappCliCapture(args, captureOpts);
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}
