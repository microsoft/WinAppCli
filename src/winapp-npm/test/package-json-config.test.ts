// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
  ensureJsBindingsBlock,
  writeJsBindingsConfig,
  readJsBindingsConfig,
  defaultJsBindingsConfig,
} from '../src/jsbindings/package-json-config';

function makeWorkspace(packageJson?: Record<string, unknown>): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-pkgcfg-'));
  if (packageJson) {
    fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify(packageJson, null, 2));
  }
  return dir;
}

function readRawPackageJson(dir: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'));
}

test('ensureJsBindingsBlock adds the block when absent', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  const outcome = ensureJsBindingsBlock(dir, { quiet: true });
  assert.equal(outcome, 'added');

  const read = readJsBindingsConfig(dir);
  assert.equal(read.packageJsonExists, true);
  assert.deepEqual(read.jsBindings, defaultJsBindingsConfig());
});

test('ensureJsBindingsBlock leaves an existing block unchanged without reset', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  ensureJsBindingsBlock(dir, { quiet: true });
  // Customize the block, then re-run without reset.
  writeJsBindingsConfig(dir, { additionalWinmds: [], additionalRefs: ['Custom.winmd'] });

  const outcome = ensureJsBindingsBlock(dir, { quiet: true });
  assert.equal(outcome, 'unchanged');
  assert.deepEqual(readJsBindingsConfig(dir).jsBindings?.additionalRefs, ['Custom.winmd']);
});

test('ensureJsBindingsBlock with reset restores defaults over a customized block', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  writeJsBindingsConfig(dir, { additionalWinmds: [], additionalRefs: ['Custom.winmd'] });

  const outcome = ensureJsBindingsBlock(dir, { quiet: true, reset: true });
  assert.equal(outcome, 'reset');
  assert.deepEqual(readJsBindingsConfig(dir).jsBindings, defaultJsBindingsConfig());
});

test('ensureJsBindingsBlock preserves unrelated winapp namespace keys', () => {
  const dir = makeWorkspace({
    name: 'app',
    version: '1.0.0',
    winapp: { someOtherFeature: { enabled: true } },
  });
  ensureJsBindingsBlock(dir, { quiet: true });

  const raw = readRawPackageJson(dir);
  const winapp = raw.winapp as Record<string, unknown>;
  assert.deepEqual(winapp.someOtherFeature, { enabled: true });
  assert.ok(winapp.jsBindings, 'jsBindings block should be added alongside existing keys');
});

test('writeJsBindingsConfig throws when package.json is missing', () => {
  const dir = makeWorkspace(); // no package.json
  assert.throws(() => writeJsBindingsConfig(dir, defaultJsBindingsConfig()), /package\.json not found/);
});

test('readJsBindingsConfig reports missing package.json and absent block', () => {
  const emptyDir = makeWorkspace();
  assert.deepEqual(readJsBindingsConfig(emptyDir), { packageJsonExists: false, jsBindings: null });

  const noBlockDir = makeWorkspace({ name: 'app', version: '1.0.0' });
  assert.deepEqual(readJsBindingsConfig(noBlockDir), { packageJsonExists: true, jsBindings: null });
});
