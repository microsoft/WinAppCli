// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Tests for the hand-written uiRecord guard wrapper.
 * Verifies that uiRecord rejects missing/non-positive durationSec with a clear error,
 * and that buildUiRecordArgs + _uiRecordWithCapture work correctly on the success path.
 */

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { uiRecord, buildUiRecordArgs, _uiRecordWithCapture } from '../src/ui-record-guard';
import type { UiRecordOptions } from '../src/ui-record-guard';

// ---------------------------------------------------------------------------
// durationSec validation tests
// JS callers may omit durationSec at runtime despite the TypeScript type, so
// we use `as any` casts for the invalid-input tests.
// ---------------------------------------------------------------------------

test('uiRecord with no durationSec throws clear error', async () => {
  await assert.rejects(
    () => (uiRecord as (o: unknown) => Promise<unknown>)({ app: 'myapp' }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec: got "${err.message}"`
      );
      return true;
    }
  );
});

test('uiRecord with durationSec = 0 throws clear error', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: 0 }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec: got "${err.message}"`
      );
      return true;
    }
  );
});

test('uiRecord with negative durationSec throws clear error', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: -1 }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(
        err.message.includes('durationSec'),
        `error message should mention durationSec: got "${err.message}"`
      );
      return true;
    }
  );
});

test('uiRecord error message explains why and how to fix it', async () => {
  let caught: Error | undefined;
  try {
    await uiRecord({ durationSec: 0 });
  } catch (e) {
    caught = e as Error;
  }
  assert.ok(caught, 'should have thrown');
  const msg = caught.message;
  assert.ok(
    msg.includes('durationSec > 0') || msg.includes('positive durationSec') || msg.includes('[1, 86400]'),
    `error message should describe the fix: "${msg}"`
  );
});

// ---------------------------------------------------------------------------
// M7 — buildUiRecordArgs: correct CLI args for every option
// ---------------------------------------------------------------------------

test('buildUiRecordArgs: minimal options (only durationSec)', () => {
  const args = buildUiRecordArgs({ durationSec: 5 });
  assert.deepEqual(args, ['ui', 'record', '--duration-sec', '5']);
});

test('buildUiRecordArgs: all options produce correct arg list', () => {
  const opts: UiRecordOptions = {
    app: 'myapp',
    captureScreen: true,
    durationSec: 10,
    fps: 30,
    json: true,
    maxEdge: 1080,
    output: 'out.mp4',
    window: 12345,
    quiet: true,
    verbose: false, // falsy — must NOT appear
    selector: 'btn-ok-a1b2',
    cwd: 'C:\\Projects\\test',
  };
  const args = buildUiRecordArgs(opts);

  // Named options come before the selector
  const dIdx = args.indexOf('--duration-sec');
  const sIdx = args.indexOf('--');
  assert.ok(dIdx >= 0, '--duration-sec must be present');
  assert.ok(sIdx >= 0, '-- terminator must be present');
  assert.ok(dIdx < sIdx, 'named options must precede the -- terminator');
  assert.equal(args[sIdx + 1], 'btn-ok-a1b2', 'selector must follow the -- terminator');

  // Spot-check required flags
  assert.ok(args.includes('--app'), '--app must be present');
  assert.equal(args[args.indexOf('--app') + 1], 'myapp');
  assert.ok(args.includes('--capture-screen'), '--capture-screen must be present');
  assert.equal(args[args.indexOf('--duration-sec') + 1], '10');
  assert.ok(args.includes('--fps'));
  assert.equal(args[args.indexOf('--fps') + 1], '30');
  assert.ok(args.includes('--json'));
  assert.ok(args.includes('--max-edge'));
  assert.equal(args[args.indexOf('--max-edge') + 1], '1080');
  assert.ok(args.includes('--output'));
  assert.equal(args[args.indexOf('--output') + 1], 'out.mp4');
  assert.ok(args.includes('--window'));
  assert.equal(args[args.indexOf('--window') + 1], '12345');
  assert.ok(args.includes('--quiet'));
  assert.ok(!args.includes('--verbose'), '--verbose must not appear when verbose is false');
});

test('buildUiRecordArgs: option-shaped selector goes after -- terminator', () => {
  const args = buildUiRecordArgs({ durationSec: 5, selector: '--capture-screen' });
  const termIdx = args.indexOf('--');
  assert.ok(termIdx >= 0, '-- terminator must be present');
  assert.equal(args[termIdx + 1], '--capture-screen', 'option-shaped selector must follow --');
  // The -- terminator must come after all named options
  assert.ok(termIdx > 2, '-- must not be the very first arg after ui record');
});

test('buildUiRecordArgs: no selector produces no -- terminator', () => {
  const args = buildUiRecordArgs({ durationSec: 5, app: 'myapp' });
  assert.ok(!args.includes('--'), 'no selector means no -- terminator');
});

// ---------------------------------------------------------------------------
// M7 — _uiRecordWithCapture: full success-path test (mocked capture function)
// ---------------------------------------------------------------------------

test('_uiRecordWithCapture: calls capture with correct args and returns result', async () => {
  const capturedArgs: string[][] = [];
  const capturedOpts: unknown[] = [];
  const fakeResult = { exitCode: 0, stdout: '{"frames":30}', stderr: '' };

  async function mockCapture(args: string[], opts: unknown) {
    capturedArgs.push(args);
    capturedOpts.push(opts);
    return fakeResult;
  }

  const opts: UiRecordOptions = {
    durationSec: 5,
    app: 'myapp',
    fps: 15,
    output: 'rec.mp4',
    cwd: 'C:\\work',
  };

  const result = await _uiRecordWithCapture(opts, mockCapture as Parameters<typeof _uiRecordWithCapture>[1]);

  // Exactly one CLI invocation
  assert.equal(capturedArgs.length, 1, 'capture must be called exactly once');

  // Args must start with ui record
  assert.equal(capturedArgs[0][0], 'ui');
  assert.equal(capturedArgs[0][1], 'record');

  // --duration-sec is present with value 5
  const dIdx = capturedArgs[0].indexOf('--duration-sec');
  assert.ok(dIdx >= 0, '--duration-sec must be in args');
  assert.equal(capturedArgs[0][dIdx + 1], '5');

  // cwd is forwarded
  assert.deepEqual(capturedOpts[0], { cwd: 'C:\\work' });

  // Result is correctly mapped
  assert.equal(result.exitCode, 0);
  assert.equal(result.stdout, '{"frames":30}');
  assert.equal(result.stderr, '');
});

test('_uiRecordWithCapture: no cwd → empty capture options', async () => {
  const capturedOpts: unknown[] = [];
  async function mockCapture(_args: string[], opts: unknown) {
    capturedOpts.push(opts);
    return { exitCode: 0, stdout: '', stderr: '' };
  }

  await _uiRecordWithCapture({ durationSec: 3 }, mockCapture as Parameters<typeof _uiRecordWithCapture>[1]);
  assert.deepEqual(capturedOpts[0], {}, 'empty cwd must produce empty capture options object');
});

// ---------------------------------------------------------------------------
// L2 — option-shaped selectors: confirmed by buildUiRecordArgs tests above
// Extra edge-case test
// ---------------------------------------------------------------------------

test('buildUiRecordArgs: leading-dash selector does not appear before --', () => {
  const args = buildUiRecordArgs({ durationSec: 5, selector: '--json' });
  const termIdx = args.indexOf('--');
  assert.ok(termIdx >= 0, '-- must be present');
  // '--json' must ONLY appear after '--', not as a named option before it
  const jsonBeforeTerm = args.slice(0, termIdx).includes('--json');
  assert.ok(!jsonBeforeTerm, '--json selector must not appear as a flag before the -- terminator');
  assert.equal(args[termIdx + 1], '--json', 'selector must be the arg after --');
});

// ---------------------------------------------------------------------------
// L3 (round-7) — guard rejects NaN, Infinity, fractional, and out-of-range durations
// ---------------------------------------------------------------------------

test('uiRecord with NaN durationSec throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: NaN }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with Infinity durationSec throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: Infinity }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with -Infinity durationSec throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: -Infinity }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with fractional durationSec (1.5) throws', async () => {
  await assert.rejects(
    () => uiRecord({ durationSec: 1.5 }),
    (err: unknown) => {
      assert.ok(err instanceof Error, 'should throw an Error');
      assert.ok(err.message.includes('durationSec'), `message must mention durationSec: "${err.message}"`);
      return true;
    }
  );
});

test('uiRecord with durationSec = 1 (minimum valid) proceeds to capture', async () => {
  // durationSec = 1 is the minimum valid value — guard must NOT throw.
  let captureCalledWith: string[][] = [];
  async function mockCapture(args: string[]) {
    captureCalledWith.push(args);
    return { exitCode: 0, stdout: '', stderr: '' };
  }
  await _uiRecordWithCapture(
    { durationSec: 1 },
    mockCapture as Parameters<typeof _uiRecordWithCapture>[1]
  );
  assert.equal(captureCalledWith.length, 1, 'capture must be called exactly once for valid durationSec=1');
});

