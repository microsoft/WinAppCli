// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Hand-written guard wrapper for uiRecord.
 *
 * winapp-commands.ts is AUTO-GENERATED and re-generates `_uiRecordGenerated` (not `uiRecord`).
 * This module is the public face: it validates that `durationSec` is provided and positive
 * before delegating to the generated function, because unbounded recording (durationSec == 0)
 * is only supportable via the CLI with Ctrl+C or piped stdin — the npm wrapper has no mechanism
 * to stop an unbounded spawn (no AbortSignal, no stdin pass-through).
 *
 * This file must NOT be edited by the code generator; it is hand-maintained.
 */

import { _uiRecordGenerated, UiRecordOptions, WinappResult } from './winapp-commands';

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
  return _uiRecordGenerated(options);
}
