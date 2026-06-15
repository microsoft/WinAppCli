// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import { Writable } from 'node:stream';

import { startSpinner } from '../src/jsbindings/spinner';

/** Fake non-TTY stream so the spinner exercises the static-line path. */
function makeNonTtyStream(): NodeJS.WriteStream & { written: string } {
  const sink: string[] = [];
  const stream = new Writable({
    write(chunk, _enc, cb): void {
      sink.push(chunk.toString());
      cb();
    },
  }) as unknown as NodeJS.WriteStream & { written: string };
  Object.defineProperty(stream, 'isTTY', { value: false, configurable: true });
  Object.defineProperty(stream, 'written', {
    get: (): string => sink.join(''),
  });
  return stream;
}

test('startSpinner (non-TTY) writes a static line through the stream when no nonTtyLog is given', () => {
  const stream = makeNonTtyStream();
  const spinner = startSpinner('Doing thing...', { stream });
  assert.equal(stream.written, 'Doing thing...\n');
  spinner.succeed('Did thing.');
  assert.equal(stream.written, 'Doing thing...\n✅ Did thing.\n');
});

test('startSpinner (non-TTY) routes both progress and completion through nonTtyLog', () => {
  const stream = makeNonTtyStream();
  const lines: string[] = [];
  const spinner = startSpinner('Installing pkg...', {
    stream,
    prefix: '  ',
    nonTtyLog: (line) => lines.push(line),
  });
  assert.deepEqual(lines, ['Installing pkg...']);
  // Nothing written directly to the stream when nonTtyLog is provided.
  assert.equal(stream.written, '');
  spinner.succeed('Installed pkg.');
  assert.deepEqual(lines, ['Installing pkg...', '✅ Installed pkg.']);
});

test('startSpinner (non-TTY) emits ❌ on fail', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const spinner = startSpinner('Bumping...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });
  spinner.fail('Bump failed: oops');
  assert.deepEqual(lines, ['Bumping...', '❌ Bump failed: oops']);
});

test('startSpinner (non-TTY) succeed() / fail() are idempotent', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const spinner = startSpinner('Work...', { stream, nonTtyLog: (line) => lines.push(line) });
  spinner.succeed('Done.');
  spinner.succeed('Done again.');
  spinner.fail('Should not surface.');
  spinner.stop();
  assert.deepEqual(lines, ['Work...', '✅ Done.']);
});

test('startSpinner (non-TTY) falls back to the original text when succeed/fail are called without text', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const spinner = startSpinner('Generic step', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });
  spinner.succeed();
  assert.deepEqual(lines, ['Generic step', '✅ Generic step']);
});
