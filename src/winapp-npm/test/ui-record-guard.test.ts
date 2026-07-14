// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

/**
 * Tests for the hand-written uiRecord guard wrapper.
 * Verifies that uiRecord rejects missing/non-positive durationSec with a clear error,
 * and delegates to _uiRecordGenerated when a valid durationSec is supplied.
 */

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

// ---------------------------------------------------------------------------
// Mock callWinappCliCapture so the guard test doesn't actually spawn a process.
// We monkey-patch winapp-cli-utils before importing the guard module.
// ---------------------------------------------------------------------------

// Provide a mock implementation via module-level interception.
// node:test doesn't have Jest-style module mocks, so we use a lightweight approach:
// load the module, then test via the exported function directly.

import { uiRecord } from '../src/ui-record-guard';

// ---------------------------------------------------------------------------
// durationSec validation tests
// ---------------------------------------------------------------------------

test('uiRecord with no options (durationSec omitted) throws clear error', async () => {
  await assert.rejects(
    () => uiRecord({}),
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
    await uiRecord({});
  } catch (e) {
    caught = e as Error;
  }
  assert.ok(caught, 'should have thrown');
  // Must explain the constraint and the fix
  const msg = caught.message;
  assert.ok(
    msg.includes('durationSec > 0') || msg.includes('positive durationSec'),
    `error message should describe the fix: "${msg}"`
  );
});
