// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { isNetworkPath, hasReparsePointOnPath } from '../src/jsbindings/path-safety';

test('isNetworkPath returns false for empty and local drive-letter paths', () => {
  assert.equal(isNetworkPath(''), false);
  assert.equal(isNetworkPath('C:\\Users\\me\\proj'), false);
  assert.equal(isNetworkPath('relative\\path'), false);
});

test('isNetworkPath flags plain UNC paths', () => {
  assert.equal(isNetworkPath('\\\\server\\share'), true);
  assert.equal(isNetworkPath('\\\\server\\share\\sub'), true);
});

test('isNetworkPath normalizes forward slashes before classifying', () => {
  assert.equal(isNetworkPath('//server/share'), true);
});

test('isNetworkPath treats local DOS device paths as non-network', () => {
  assert.equal(isNetworkPath('\\\\?\\C:\\foo'), false);
  assert.equal(isNetworkPath('\\\\.\\C:\\foo'), false);
});

test('isNetworkPath flags DOS-device UNC paths (\\\\?\\UNC\\ and \\\\.\\UNC\\)', () => {
  assert.equal(isNetworkPath('\\\\?\\UNC\\server\\share'), true);
  assert.equal(isNetworkPath('\\\\.\\UNC\\server\\share'), true);
  // Case-insensitive on the UNC token.
  assert.equal(isNetworkPath('\\\\?\\unc\\server\\share'), true);
});

test('hasReparsePointOnPath accepts an absolute path contained under its drive root (regression: C:\\ boundary)', () => {
  // Before the drive-root normalization fix, a drive-root boundary collapsed
  // to a bare `C:` whose `path.resolve()` yields the per-drive CWD, so a
  // legitimate same-drive absolute path was wrongly reported as "outside
  // boundary". The temp dir is a normal directory under the drive root.
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-ps-'));
  try {
    const root = path.parse(dir).root; // e.g. "C:\\" or "/"
    assert.equal(hasReparsePointOnPath(dir, root), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('hasReparsePointOnPath flags a target outside the boundary', () => {
  const a = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-ps-a-'));
  const b = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-ps-b-'));
  try {
    // b is a sibling of a, not contained under it.
    assert.equal(hasReparsePointOnPath(b, a), true);
  } finally {
    fs.rmSync(a, { recursive: true, force: true });
    fs.rmSync(b, { recursive: true, force: true });
  }
});

test('hasReparsePointOnPath treats the boundary itself as contained', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-ps-same-'));
  try {
    assert.equal(hasReparsePointOnPath(dir, dir), false);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
