// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';

import { shouldSkipBindingsAfterInit, makeIndentedLog } from '../src/jsbindings/cli-hooks';

// Shorthand: build a default `false` set, override with `overrides`.
const make = (overrides: Partial<Parameters<typeof shouldSkipBindingsAfterInit>[0]> = {}) => ({
  explicitWorkspace: false,
  useDefaults: false,
  packageJsonExistedBeforeInit: false,
  packageJsonExistsNow: false,
  ...overrides,
});

test('shouldSkipBindingsAfterInit trusts cwd when user passes an explicit workspace', () => {
  assert.equal(shouldSkipBindingsAfterInit(make({ explicitWorkspace: true })), false);
  assert.equal(shouldSkipBindingsAfterInit(make({ explicitWorkspace: true, packageJsonExistedBeforeInit: false })), false);
});

test('shouldSkipBindingsAfterInit trusts cwd when package.json already existed before init', () => {
  assert.equal(shouldSkipBindingsAfterInit(make({ packageJsonExistedBeforeInit: true })), false);
});

test('shouldSkipBindingsAfterInit trusts cwd in non-interactive flow if a package.json exists now', () => {
  assert.equal(
    shouldSkipBindingsAfterInit(make({ useDefaults: true, packageJsonExistsNow: true })),
    false
  );
});

test('shouldSkipBindingsAfterInit skips when there is no signal that cwd is the workspace', () => {
  assert.equal(shouldSkipBindingsAfterInit(make()), true);
});

test('shouldSkipBindingsAfterInit still skips --use-defaults with no package.json anywhere', () => {
  assert.equal(
    shouldSkipBindingsAfterInit(make({ useDefaults: true, packageJsonExistsNow: false })),
    true
  );
});

test('makeIndentedLog prefixes single-line messages with the given indent', () => {
  const captured: string[] = [];
  const original = console.log;
  console.log = (line: string) => captured.push(line);
  try {
    const log = makeIndentedLog('  ');
    log('hello');
    log('world');
  } finally {
    console.log = original;
  }
  assert.deepEqual(captured, ['  hello', '  world']);
});

test('makeIndentedLog prefixes every non-empty line in multi-line messages', () => {
  const captured: string[] = [];
  const original = console.log;
  console.log = (line: string) => captured.push(line);
  try {
    const log = makeIndentedLog('  ');
    log('first\nsecond\n\nfourth');
  } finally {
    console.log = original;
  }
  assert.deepEqual(captured, ['  first\n  second\n\n  fourth']);
});

test('makeIndentedLog passes empty messages through untouched', () => {
  const captured: string[] = [];
  const original = console.log;
  console.log = (line: string) => captured.push(line);
  try {
    const log = makeIndentedLog('  ');
    log('');
  } finally {
    console.log = original;
  }
  assert.deepEqual(captured, ['']);
});
