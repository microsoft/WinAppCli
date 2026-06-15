// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
  isNetworkPath,
  hasReparsePointOnPath,
  assertSafeWorkspaceFile,
  assertSafeWorkspaceOutputDir,
} from '../src/jsbindings/path-safety';

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
  // Drive-root boundaries must stay rooted (`C:\`) instead of becoming drive-relative (`C:`).
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

// assertSafeWorkspaceFile — rejects UNC, reparse-redirected, or
// outside-workspace targets before any write to package.json / winapp.yaml /
// winmds.lock.json so a hostile symlink can't redirect the write.

function makeRealTmpDir(prefix: string): string {
  // realpath.native handles 8.3 short-name TEMP dirs on CI (RUNNER~1).
  return fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), prefix)));
}

test('assertSafeWorkspaceFile accepts a file directly inside the workspace', () => {
  const ws = makeRealTmpDir('winapp-ps-asf-ok-');
  try {
    const target = path.join(ws, 'package.json');
    assertSafeWorkspaceFile(ws, target, 'package.json');
  } finally {
    fs.rmSync(ws, { recursive: true, force: true });
  }
});

test('assertSafeWorkspaceFile rejects a UNC workspace path', () => {
  assert.throws(
    () => assertSafeWorkspaceFile('\\\\server\\share\\proj', '\\\\server\\share\\proj\\package.json', 'package.json'),
    /UNC \/ network path/
  );
});

test('assertSafeWorkspaceFile rejects a target outside the workspace', () => {
  const ws = makeRealTmpDir('winapp-ps-asf-out-');
  const outside = makeRealTmpDir('winapp-ps-asf-other-');
  try {
    assert.throws(
      () => assertSafeWorkspaceFile(ws, path.join(outside, 'package.json'), 'package.json'),
      /reparse point|outside the workspace/
    );
  } finally {
    fs.rmSync(ws, { recursive: true, force: true });
    fs.rmSync(outside, { recursive: true, force: true });
  }
});

// assertSafeWorkspaceOutputDir — used before wiping `.winapp/bindings/`.
// Reject empty, UNC, escape, workspace-equal, and reparse-backed dirs so the
// wipe cannot follow a hostile redirect outside the workspace.

test('assertSafeWorkspaceOutputDir rejects an empty output dir', () => {
  const ws = makeRealTmpDir('winapp-ps-aso-empty-');
  try {
    assert.throws(() => assertSafeWorkspaceOutputDir(ws, '', 'bindings'), /must not be empty/);
    assert.throws(() => assertSafeWorkspaceOutputDir(ws, '   ', 'bindings'), /must not be empty/);
  } finally {
    fs.rmSync(ws, { recursive: true, force: true });
  }
});

test('assertSafeWorkspaceOutputDir rejects an output dir that escapes the workspace', () => {
  const ws = makeRealTmpDir('winapp-ps-aso-out-');
  const outside = makeRealTmpDir('winapp-ps-aso-other-');
  try {
    assert.throws(() => assertSafeWorkspaceOutputDir(ws, outside, 'bindings'), /outside the workspace/);
  } finally {
    fs.rmSync(ws, { recursive: true, force: true });
    fs.rmSync(outside, { recursive: true, force: true });
  }
});

test('assertSafeWorkspaceOutputDir rejects a workspace-equal output (must be a strict descendant)', () => {
  const ws = makeRealTmpDir('winapp-ps-aso-equal-');
  try {
    // Root as output dir would let the wipe delete the workspace itself.
    assert.throws(() => assertSafeWorkspaceOutputDir(ws, ws, 'bindings'), /outside the workspace/);
  } finally {
    fs.rmSync(ws, { recursive: true, force: true });
  }
});

test('assertSafeWorkspaceOutputDir accepts a relative descendant path', () => {
  const ws = makeRealTmpDir('winapp-ps-aso-rel-');
  try {
    const resolved = assertSafeWorkspaceOutputDir(ws, '.winapp/bindings', 'bindings');
    assert.equal(resolved, path.resolve(ws, '.winapp', 'bindings'));
  } finally {
    fs.rmSync(ws, { recursive: true, force: true });
  }
});

test('assertSafeWorkspaceOutputDir rejects a UNC workspace path', () => {
  assert.throws(
    () => assertSafeWorkspaceOutputDir('\\\\server\\share\\proj', 'bindings', 'bindings'),
    /UNC \/ network path/
  );
});
