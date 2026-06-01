// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import { ensureEsmPackageMarker } from '../src/jsbindings/codegen-runner';

function tmpDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'esm-marker-'));
}

function readPkg(dir: string): Record<string, unknown> {
  return JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'));
}

test('ensureEsmPackageMarker creates a type:module package.json when none exists', () => {
  const dir = tmpDir();
  try {
    ensureEsmPackageMarker(dir);
    assert.deepEqual(readPkg(dir), { type: 'module' });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ensureEsmPackageMarker preserves existing fields and sets type', () => {
  const dir = tmpDir();
  try {
    fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify({ name: 'x', version: '1.0.0' }));
    ensureEsmPackageMarker(dir);
    assert.deepEqual(readPkg(dir), { name: 'x', version: '1.0.0', type: 'module' });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ensureEsmPackageMarker leaves an already-correct package.json untouched', () => {
  const dir = tmpDir();
  try {
    const pkgPath = path.join(dir, 'package.json');
    fs.writeFileSync(pkgPath, JSON.stringify({ type: 'module', name: 'keep' }));
    const before = fs.readFileSync(pkgPath, 'utf8');
    ensureEsmPackageMarker(dir);
    assert.equal(fs.readFileSync(pkgPath, 'utf8'), before);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('ensureEsmPackageMarker overwrites a corrupt package.json', () => {
  const dir = tmpDir();
  try {
    fs.writeFileSync(path.join(dir, 'package.json'), 'not json {');
    ensureEsmPackageMarker(dir);
    assert.deepEqual(readPkg(dir), { type: 'module' });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
