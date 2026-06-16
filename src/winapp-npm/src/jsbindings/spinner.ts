// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Dependency-free single-task spinner. Animates `⠋⠙⠹⠸…` in place on TTY and
// degrades to plain log lines on non-TTY streams.

const FRAMES = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
const FRAME_INTERVAL_MS = 80;

export interface Spinner {
  succeed: (text?: string) => void;
  fail: (text?: string) => void;
  stop: () => void;
}

export interface SpinnerOptions {
  stream?: NodeJS.WriteStream;
  /** Prefix prepended to every spinner / completion line (e.g. indent for child tasks). */
  prefix?: string;
  /** Sink for the static line in non-TTY mode. Defaults to `stream.write(...)`. */
  nonTtyLog?: (line: string) => void;
}

/**
 * Start a spinner. ALWAYS call exactly one of `succeed()` / `fail()` / `stop()`,
 * preferably from a `finally` block.
 */
export function startSpinner(text: string, options: SpinnerOptions = {}): Spinner {
  const stream = options.stream ?? process.stdout;
  const prefix = options.prefix ?? '';
  const nonTtyLog = options.nonTtyLog;

  if (!stream.isTTY) {
    if (nonTtyLog) {
      nonTtyLog(`${text}`);
    } else {
      stream.write(`${prefix}${text}\n`);
    }
    let done = false;
    const finishStatic = (marker: string, finalText: string | undefined): void => {
      if (done) return;
      done = true;
      const t = finalText ?? text;
      if (nonTtyLog) {
        nonTtyLog(`${marker} ${t}`);
      } else {
        stream.write(`${prefix}${marker} ${t}\n`);
      }
    };
    return {
      succeed: (t) => finishStatic('✅', t),
      fail: (t) => finishStatic('❌', t),
      stop: () => {
        done = true;
      },
    };
  }

  let frame = 0;
  let done = false;

  stream.write('\x1b[?25l');

  // Write the full line once, then on each frame only overwrite the spinner character.
  const spinnerCol = prefix.length; // column where the spinner character sits
  const render = (): void => {
    const ch = FRAMES[frame % FRAMES.length];
    // Move cursor to the spinner column and overwrite just that character
    stream.write(`\r\x1b[${spinnerCol + 1}G${ch}`);
    frame++;
  };

  // Initial full-line write
  stream.write(`\r\x1b[2K${prefix}${FRAMES[0]} ${text}`);
  frame = 1;
  const handle = setInterval(render, FRAME_INTERVAL_MS);

  const teardown = (): void => {
    clearInterval(handle);
    stream.write('\x1b[?25h');
    process.removeListener('SIGINT', onSigint);
  };

  const finishLive = (marker: string, finalText: string | undefined): void => {
    if (done) return;
    done = true;
    teardown();
    stream.write(`\r\x1b[2K${prefix}${marker} ${finalText ?? text}\n`);
  };

  // Ctrl+C: restore terminal and re-raise so exit code stays 130.
  function onSigint(): void {
    if (done) return;
    done = true;
    clearInterval(handle);
    stream.write('\r\x1b[2K\x1b[?25h');
    process.removeListener('SIGINT', onSigint);
    process.kill(process.pid, 'SIGINT');
  }
  process.once('SIGINT', onSigint);

  return {
    succeed: (t) => finishLive('✅', t),
    fail: (t) => finishLive('❌', t),
    stop: () => {
      if (done) return;
      done = true;
      teardown();
      stream.write('\r\x1b[2K');
    },
  };
}
