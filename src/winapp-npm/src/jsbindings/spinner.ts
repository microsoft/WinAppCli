// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Tiny TTY spinner for long-running operations (codegen). Implemented inline
// — no external dependency — using ANSI cursor escapes the rest of the
// wrapper already relies on (`\x1b[2K`, `\x1b[?25l/h`).
//
// Behaviour matrix:
//   * TTY              → braille frames at 80ms, line cleared on stop().
//   * non-TTY (CI etc) → single static line written on start, stop() is a no-op.
//   * SIGINT (Ctrl+C)  → cursor restored before re-raising the signal so the
//                        terminal isn't left with a hidden caret.

const FRAMES = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
const FRAME_INTERVAL_MS = 80;

export interface Spinner {
  /** Clears the spinner line (TTY) or no-op (non-TTY). Idempotent. */
  stop: () => void;
}

export interface SpinnerOptions {
  /** Defaults to process.stdout. */
  stream?: NodeJS.WriteStream;
}

/**
 * Starts a TTY spinner showing the given text. Returns a handle whose `stop()`
 * wipes the line and restores the cursor. Always pair start/stop in a `try/finally`.
 *
 * On non-TTY streams (pipes, files, CI), writes a single static line so the user
 * still sees what's happening — no spinner animation, no line wipe.
 */
export function startSpinner(text: string, options: SpinnerOptions = {}): Spinner {
  const stream = options.stream ?? process.stdout;

  if (!stream.isTTY) {
    stream.write(`${text}\n`);
    return { stop: () => {} };
  }

  let frame = 0;
  let stopped = false;

  // Hide cursor for the duration of the spinner.
  stream.write('\x1b[?25l');

  const render = (): void => {
    // \r → carriage return; \x1b[2K → clear entire line. Together they
    // overwrite whatever we last drew without leaving stray trailing chars.
    stream.write(`\r\x1b[2K${FRAMES[frame % FRAMES.length]} ${text}`);
    frame++;
  };

  render();
  const handle = setInterval(render, FRAME_INTERVAL_MS);

  // Best-effort cleanup on Ctrl+C: clear the line + restore cursor, then
  // re-raise SIGINT so the process exits with the conventional 130 code.
  const onSigint = (): void => {
    if (stopped) return;
    stopped = true;
    clearInterval(handle);
    stream.write('\r\x1b[2K\x1b[?25h');
    process.removeListener('SIGINT', onSigint);
    process.kill(process.pid, 'SIGINT');
  };
  process.once('SIGINT', onSigint);

  return {
    stop: () => {
      if (stopped) return;
      stopped = true;
      clearInterval(handle);
      // Clear current line + show cursor again.
      stream.write('\r\x1b[2K\x1b[?25h');
      process.removeListener('SIGINT', onSigint);
    },
  };
}
