// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { resolveAdditionalWinmds, isCherryPick } from '../src/jsbindings/additional-winmds';

function makeWorkspace(): string {
  // Use realpath.native so 8.3 short-name TEMP (e.g. RUNNER~1 on CI) matches what
  // additional-winmds resolves internally via path.resolve.
  return fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-addwinmds-')));
}

function writeWinmd(dir: string, relPath: string): string {
  const full = path.join(dir, relPath);
  fs.mkdirSync(path.dirname(full), { recursive: true });
  fs.writeFileSync(full, ''); // contents irrelevant — resolver only checks existence
  return full;
}

test('resolveAdditionalWinmds accepts relative paths under the workspace', () => {
  const ws = makeWorkspace();
  const abs = writeWinmd(ws, 'libs/Foo.winmd');
  const result = resolveAdditionalWinmds([{ winmdPath: 'libs/Foo.winmd' }], ws, 'additionalWinmds');
  assert.deepEqual(result.warnings, []);
  assert.equal(result.resolved.length, 1);
  assert.equal(result.resolved[0].winmdPath, abs);
});

test('resolveAdditionalWinmds accepts absolute paths outside the workspace', () => {
  const ws = makeWorkspace();
  const outside = makeWorkspace();
  const abs = writeWinmd(outside, 'External.winmd');
  const result = resolveAdditionalWinmds([{ winmdPath: abs }], ws, 'additionalWinmds');
  assert.deepEqual(result.warnings, []);
  assert.equal(result.resolved[0].winmdPath, abs);
});

test('resolveAdditionalWinmds rejects raw UNC paths', () => {
  const ws = makeWorkspace();
  const result = resolveAdditionalWinmds([{ winmdPath: '\\\\attacker\\share\\bad.winmd' }], ws, 'additionalWinmds');
  assert.equal(result.resolved.length, 0);
  assert.equal(result.warnings.length, 1);
  assert.match(result.warnings[0], /network\/UNC paths are not allowed/);
});

test('resolveAdditionalWinmds warns when a referenced file does not exist', () => {
  const ws = makeWorkspace();
  const result = resolveAdditionalWinmds([{ winmdPath: 'libs/Missing.winmd' }], ws, 'additionalWinmds');
  assert.equal(result.resolved.length, 0);
  assert.equal(result.warnings.length, 1);
  assert.match(result.warnings[0], /entry not found/);
});

test('resolveAdditionalWinmds rejects directory paths with a clear warning', () => {
  const ws = makeWorkspace();
  fs.mkdirSync(path.join(ws, 'libs'), { recursive: true });
  const result = resolveAdditionalWinmds([{ winmdPath: 'libs' }], ws, 'additionalWinmds');
  assert.equal(result.resolved.length, 0);
  assert.equal(result.warnings.length, 1);
  assert.match(result.warnings[0], /not a regular file/);
});

test('resolveAdditionalWinmds deduplicates the same fullPath+namespace pair and merges classes', () => {
  const ws = makeWorkspace();
  writeWinmd(ws, 'libs/Foo.winmd');
  const result = resolveAdditionalWinmds(
    [
      { winmdPath: 'libs/Foo.winmd', namespace: 'Vendor.Foo', classes: ['A', 'B'] },
      { winmdPath: 'libs/foo.winmd', namespace: 'Vendor.Foo', classes: ['B', 'C'] },
    ],
    ws,
    'additionalWinmds'
  );
  assert.deepEqual(result.warnings, []);
  assert.equal(result.resolved.length, 1);
  assert.deepEqual(result.resolved[0].classes, ['A', 'B', 'C']);
});

test('resolveAdditionalWinmds merges path-less cherry-pick entries by namespace', () => {
  const ws = makeWorkspace();
  const result = resolveAdditionalWinmds(
    [
      { namespace: 'Vendor.Bar', classes: ['X', 'Y'] },
      { namespace: 'Vendor.Bar', classes: ['Y', 'Z'] },
    ],
    ws,
    'additionalWinmds'
  );
  assert.deepEqual(result.warnings, []);
  assert.equal(result.resolved.length, 1);
  assert.equal(result.resolved[0].winmdPath, undefined);
  assert.deepEqual(result.resolved[0].classes, ['X', 'Y', 'Z']);
  assert.equal(isCherryPick(result.resolved[0]), true);
});

test('resolveAdditionalWinmds skips entries with no path and no namespace+classes', () => {
  const ws = makeWorkspace();
  const result = resolveAdditionalWinmds([{}, { namespace: 'Vendor.Foo' }, { classes: ['A'] }], ws, 'additionalWinmds');
  assert.equal(result.resolved.length, 0);
  assert.equal(result.warnings.length, 3);
  for (const w of result.warnings) {
    assert.match(w, /no winmdPath and no namespace\+classes/);
  }
});

test('resolveAdditionalWinmds silently skips falsy / null entries in the array', () => {
  const ws = makeWorkspace();
  writeWinmd(ws, 'libs/Foo.winmd');
  const result = resolveAdditionalWinmds(
    // Cast away strict type to mimic JSON that bypassed validation.
    [null as unknown as { winmdPath: string }, { winmdPath: 'libs/Foo.winmd' }],
    ws,
    'additionalWinmds'
  );
  assert.deepEqual(result.warnings, []);
  assert.equal(result.resolved.length, 1);
});

test('resolveAdditionalWinmds returns empty result for undefined / empty inputs', () => {
  const ws = makeWorkspace();
  assert.deepEqual(resolveAdditionalWinmds(undefined, ws, 'x'), { resolved: [], warnings: [] });
  assert.deepEqual(resolveAdditionalWinmds([], ws, 'x'), { resolved: [], warnings: [] });
});

test('isCherryPick narrows only entries with both namespace and at least one class', () => {
  assert.equal(isCherryPick({ winmdPath: 'a.winmd' }), false);
  assert.equal(isCherryPick({ namespace: 'NS' }), false);
  assert.equal(isCherryPick({ namespace: 'NS', classes: [] }), false);
  assert.equal(isCherryPick({ namespace: 'NS', classes: ['A'] }), true);
});
