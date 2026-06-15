// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Dependency-free grouped task renderer: one animating parent header + indented
// child sub-tasks, redrawn in place via `readline` cursor APIs.

import * as readline from 'readline';

const FRAMES = ['⠋', '⠙', '⠹', '⠸', '⠼', '⠴', '⠦', '⠧', '⠇', '⠏'];
const FRAME_INTERVAL_MS = 80;

type TaskState = 'running' | 'done' | 'failed' | 'stopped';

interface TaskNode {
  indent: number;
  startText: string;
  finalText?: string;
  state: TaskState;
}

/** Handle for a child sub-task. Shape matches {@link import('./spinner').Spinner}. */
export interface GroupedChildSpinner {
  succeed: (text?: string) => void;
  fail: (text?: string) => void;
  stop: () => void;
}

export interface GroupedSpinnerOptions {
  stream?: NodeJS.WriteStream;
  /** Sink used in non-TTY mode (one call per event). Defaults to writing to `stream`. */
  nonTtyLog?: (line: string) => void;
}

export interface GroupedSpinner {
  addChild: (text: string) => GroupedChildSpinner;
  succeed: (text?: string) => void;
  fail: (text?: string) => void;
  stop: () => void;
}

// Wall-clock-derived so every active task shares the same frame on each tick.
function currentFrame(): string {
  return FRAMES[Math.floor(Date.now() / FRAME_INTERVAL_MS) % FRAMES.length];
}

function markerForState(state: TaskState): string | null {
  switch (state) {
    case 'running':
      return currentFrame();
    case 'done':
      return '✅';
    case 'failed':
      return '❌';
    case 'stopped':
      return null;
  }
}

/**
 * Start a grouped spinner. ALWAYS call exactly one of `succeed()` / `fail()` /
 * `stop()` on the returned handle, preferably from a `finally` block.
 */
export function startGroupedSpinner(parentText: string, options: GroupedSpinnerOptions = {}): GroupedSpinner {
  const stream = options.stream ?? process.stdout;
  const nonTtyLog = options.nonTtyLog;
  const isTty = !!stream.isTTY;

  const tasks: TaskNode[] = [{ indent: 0, startText: parentText, state: 'running' }];
  let done = false;

  const emitStatic = (line: string): void => {
    if (nonTtyLog) {
      nonTtyLog(line);
    } else {
      stream.write(`${line}\n`);
    }
  };

  if (!isTty) {
    emitStatic(parentText);
    return buildNonTtyHandle(
      tasks,
      emitStatic,
      () => done,
      (v) => (done = v)
    );
  }

  // Hide cursor for the duration of the animation.
  stream.write('\x1b[?25l');
  let linesDrawn = 0;

  const draw = (): void => {
    if (linesDrawn > 0) {
      readline.moveCursor(stream, 0, -linesDrawn);
      readline.cursorTo(stream, 0);
      readline.clearScreenDown(stream);
    }

    const lines: string[] = [];
    for (const task of tasks) {
      const marker = markerForState(task.state);
      if (marker === null) continue;
      const indent = '  '.repeat(task.indent);
      const text = task.finalText ?? task.startText;
      lines.push(`${indent}${marker} ${text}`);
    }

    if (lines.length > 0) {
      stream.write(`${lines.join('\n')}\n`);
    }
    linesDrawn = lines.length;
  };

  draw();
  const intervalHandle = setInterval(draw, FRAME_INTERVAL_MS);

  // Ctrl+C: restore cursor and re-raise so exit code stays 130.
  const onSigint = (): void => {
    if (done) return;
    done = true;
    clearInterval(intervalHandle);
    stream.write('\x1b[?25h');
    process.removeListener('SIGINT', onSigint);
    process.kill(process.pid, 'SIGINT');
  };
  process.once('SIGINT', onSigint);

  const finish = (state: TaskState, text?: string): void => {
    if (done) return;
    done = true;
    tasks[0].state = state;
    if (text !== undefined) tasks[0].finalText = text;
    clearInterval(intervalHandle);
    draw();
    stream.write('\x1b[?25h');
    process.removeListener('SIGINT', onSigint);
  };

  const addChild = (text: string): GroupedChildSpinner => {
    const node: TaskNode = { indent: 1, startText: text, state: 'running' };
    tasks.push(node);
    const settle = (state: TaskState, finalText?: string): void => {
      if (node.state !== 'running') return;
      node.state = state;
      if (finalText !== undefined) node.finalText = finalText;
    };
    return {
      succeed: (t) => settle('done', t),
      fail: (t) => settle('failed', t),
      stop: () => settle('stopped'),
    };
  };

  return {
    addChild,
    succeed: (t) => finish('done', t),
    fail: (t) => finish('failed', t),
    stop: () => {
      if (done) return;
      done = true;
      clearInterval(intervalHandle);
      stream.write('\x1b[?25h');
      process.removeListener('SIGINT', onSigint);
    },
  };
}

// Non-TTY: one static line per child completion / group finish.
function buildNonTtyHandle(
  tasks: TaskNode[],
  emitStatic: (line: string) => void,
  isDone: () => boolean,
  setDone: (v: boolean) => void
): GroupedSpinner {
  const addChild = (text: string): GroupedChildSpinner => {
    const node: TaskNode = { indent: 1, startText: text, state: 'running' };
    tasks.push(node);
    return {
      succeed: (t) => {
        if (node.state !== 'running') return;
        node.state = 'done';
        emitStatic(`  ✅ ${t ?? text}`);
      },
      fail: (t) => {
        if (node.state !== 'running') return;
        node.state = 'failed';
        emitStatic(`  ❌ ${t ?? text}`);
      },
      stop: () => {
        if (node.state !== 'running') return;
        node.state = 'stopped';
      },
    };
  };

  const finish = (marker: string, text?: string): void => {
    if (isDone()) return;
    setDone(true);
    emitStatic(`${marker} ${text ?? tasks[0].startText}`);
  };

  return {
    addChild,
    succeed: (t) => finish('✅', t),
    fail: (t) => finish('❌', t),
    stop: () => {
      if (isDone()) return;
      setDone(true);
    },
  };
}
