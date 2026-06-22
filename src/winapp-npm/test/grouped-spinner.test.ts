// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import { Writable } from 'node:stream';

import { startGroupedSpinner } from '../src/jsbindings/grouped-spinner';

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

test('startGroupedSpinner (non-TTY) emits parent header immediately and final ✅ on succeed', () => {
  const stream = makeNonTtyStream();
  const lines: string[] = [];
  const group = startGroupedSpinner('Setting up JS bindings...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });
  assert.deepEqual(lines, ['Setting up JS bindings...']);
  group.succeed('JS bindings setup completed successfully');
  assert.deepEqual(lines, ['Setting up JS bindings...', '✅ JS bindings setup completed successfully']);
});

test('startGroupedSpinner (non-TTY) emits ❌ on group fail', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const group = startGroupedSpinner('Setting up JS bindings...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });
  group.fail('JS bindings setup failed: boom');
  assert.deepEqual(lines, ['Setting up JS bindings...', '❌ JS bindings setup failed: boom']);
});

test('startGroupedSpinner (non-TTY) emits one indented ✅ per child succeed', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const group = startGroupedSpinner('Setting up JS bindings...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });

  const child1 = group.addChild('Resetting "winapp.jsBindings"...');
  child1.succeed('Reset "winapp.jsBindings" in package.json to defaults.');

  const child2 = group.addChild('Generating bindings...');
  child2.succeed('Generated JS bindings → C:\\out');

  group.succeed('JS bindings setup completed successfully');

  assert.deepEqual(lines, [
    'Setting up JS bindings...',
    '  ✅ Reset "winapp.jsBindings" in package.json to defaults.',
    '  ✅ Generated JS bindings → C:\\out',
    '✅ JS bindings setup completed successfully',
  ]);
});

test('startGroupedSpinner (non-TTY) emits ❌ on child fail', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const group = startGroupedSpinner('Setting up JS bindings...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });

  const child = group.addChild('Installing pkg...');
  child.fail('Could not auto-install pkg: ENOENT');

  group.fail('JS bindings setup failed');

  assert.deepEqual(lines, [
    'Setting up JS bindings...',
    '  ❌ Could not auto-install pkg: ENOENT',
    '❌ JS bindings setup failed',
  ]);
});

test('startGroupedSpinner (non-TTY) succeed/fail/stop are idempotent', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const group = startGroupedSpinner('Setup...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });
  group.succeed('Done.');
  group.succeed('Done again.');
  group.fail('Should not surface.');
  group.stop();
  assert.deepEqual(lines, ['Setup...', '✅ Done.']);
});

test('startGroupedSpinner (non-TTY) child completion after settle is a no-op', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const group = startGroupedSpinner('Setup...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });
  const child = group.addChild('Step 1...');
  child.succeed('Step 1 done.');
  // Second call should be ignored to avoid double-logging in CI streams.
  child.succeed('Should not surface.');
  child.fail('Should not surface either.');
  group.succeed('All done.');
  assert.deepEqual(lines, ['Setup...', '  ✅ Step 1 done.', '✅ All done.']);
});

test('startGroupedSpinner (non-TTY) falls back to original text when succeed/fail are called without text', () => {
  const lines: string[] = [];
  const stream = makeNonTtyStream();
  const group = startGroupedSpinner('Setup...', {
    stream,
    nonTtyLog: (line) => lines.push(line),
  });
  const child = group.addChild('Working...');
  child.succeed();
  group.succeed();
  assert.deepEqual(lines, ['Setup...', '  ✅ Working...', '✅ Setup...']);
});
