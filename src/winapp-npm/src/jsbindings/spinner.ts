// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Tiny dependency-free spinner for codegen progress.

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

/** Start a spinner; pair `stop()` in `finally`. Non-TTY streams get one static line. */
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
    // Carriage return + clear-line overwrites the previous frame without leftovers.
    stream.write(`\r\x1b[2K${FRAMES[frame % FRAMES.length]} ${text}`);
    frame++;
  };

  render();
  const handle = setInterval(render, FRAME_INTERVAL_MS);

  // Ctrl+C cleanup: restore the terminal, then re-raise for conventional exit 130.
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
