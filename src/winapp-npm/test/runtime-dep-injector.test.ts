// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
  ensureRuntimeDependency,
  updateRuntimeDependency,
  formatRuntimeDependencyHint,
  getRuntimeDependencyVersion,
  getDevDependencyVersion,
  isRuntimeDependencyDeclared,
  isPackageDeclared,
} from '../src/jsbindings/runtime-dep-injector';

const PKG = '@microsoft/dynwinrt';

function makeWorkspace(packageJson?: Record<string, unknown>): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-runtimedep-'));
  if (packageJson) {
    fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify(packageJson, null, 2));
  }
  return dir;
}

function readDeps(dir: string): Record<string, unknown> {
  const raw = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'));
  return (raw.dependencies ?? {}) as Record<string, unknown>;
}

test('ensureRuntimeDependency rejects empty package name or version', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  assert.throws(() => ensureRuntimeDependency(dir, '   ', '1.0.0'), /packageName must not be empty/);
  assert.throws(() => ensureRuntimeDependency(dir, PKG, '  '), /version must not be empty/);
});

test('ensureRuntimeDependency reports noPackageJson when package.json is absent', () => {
  const dir = makeWorkspace();
  assert.deepEqual(ensureRuntimeDependency(dir, PKG, '1.0.0'), { outcome: 'noPackageJson' });
});

test('ensureRuntimeDependency adds a new dependency right after version', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0', scripts: {} });
  const result = ensureRuntimeDependency(dir, PKG, '0.2.0');
  assert.deepEqual(result, { outcome: 'added', pinnedVersion: '0.2.0' });

  const raw = JSON.parse(fs.readFileSync(path.join(dir, 'package.json'), 'utf8'));
  assert.equal(raw.dependencies[PKG], '0.2.0');
  // "dependencies" is inserted immediately after "version".
  const keys = Object.keys(raw);
  assert.equal(keys[keys.indexOf('version') + 1], 'dependencies');
});

test('ensureRuntimeDependency leaves a matching version untouched', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.2.0' } });
  assert.deepEqual(ensureRuntimeDependency(dir, PKG, '0.2.0'), { outcome: 'alreadyPresent' });
});

test('ensureRuntimeDependency surfaces versionMismatch without mutating package.json', () => {
  // ensureRuntimeDependency never silently overwrites a user-pinned version.
  // Callers (the orchestrator + init hook) auto-apply updateRuntimeDependency
  // because codegen <-> runtime ABI parity is non-negotiable.
  const dir = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.1.0' } });
  const result = ensureRuntimeDependency(dir, PKG, '0.2.0');
  assert.deepEqual(result, { outcome: 'versionMismatch', existingVersion: '0.1.0', pinnedVersion: '0.2.0' });
  // Critical: existing pin must remain on disk until the caller opts in.
  assert.equal(readDeps(dir)[PKG], '0.1.0');
});

test('updateRuntimeDependency overwrites the existing pin', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.1.0' } });
  updateRuntimeDependency(dir, PKG, '0.2.0');
  assert.equal(readDeps(dir)[PKG], '0.2.0');
});

test('updateRuntimeDependency rejects empty package name or version', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  assert.throws(() => updateRuntimeDependency(dir, '   ', '1.0.0'), /packageName must not be empty/);
  assert.throws(() => updateRuntimeDependency(dir, PKG, '  '), /version must not be empty/);
});

test('ensureRuntimeDependency does not auto-promote a devDependency', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0', devDependencies: { [PKG]: '0.2.0' } });
  assert.deepEqual(ensureRuntimeDependency(dir, PKG, '0.2.0'), { outcome: 'presentInDevDependencies' });
  // Production dependencies must remain untouched.
  assert.equal(PKG in readDeps(dir), false);
});

test('isRuntimeDependencyDeclared reflects production dependency presence only', () => {
  const prod = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.2.0' } });
  assert.equal(isRuntimeDependencyDeclared(prod, PKG), true);

  const devOnly = makeWorkspace({ name: 'app', version: '1.0.0', devDependencies: { [PKG]: '0.2.0' } });
  assert.equal(isRuntimeDependencyDeclared(devOnly, PKG), false);

  const none = makeWorkspace();
  assert.equal(isRuntimeDependencyDeclared(none, PKG), false);
});

test('getRuntimeDependencyVersion returns the production dependency version only', () => {
  const prod = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.2.0' } });
  assert.equal(getRuntimeDependencyVersion(prod, PKG), '0.2.0');

  const devOnly = makeWorkspace({ name: 'app', version: '1.0.0', devDependencies: { [PKG]: '0.2.0' } });
  assert.equal(getRuntimeDependencyVersion(devOnly, PKG), null);

  const none = makeWorkspace();
  assert.equal(getRuntimeDependencyVersion(none, PKG), null);
});

test('getDevDependencyVersion returns the devDependencies version only', () => {
  const devOnly = makeWorkspace({ name: 'app', version: '1.0.0', devDependencies: { [PKG]: '0.2.0' } });
  assert.equal(getDevDependencyVersion(devOnly, PKG), '0.2.0');

  const prod = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.2.0' } });
  assert.equal(getDevDependencyVersion(prod, PKG), null);

  const none = makeWorkspace();
  assert.equal(getDevDependencyVersion(none, PKG), null);
});

test('isPackageDeclared spans both dependencies and devDependencies', () => {
  const prod = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.2.0' } });
  assert.equal(isPackageDeclared(prod, PKG), true);

  const devOnly = makeWorkspace({ name: 'app', version: '1.0.0', devDependencies: { [PKG]: '0.2.0' } });
  assert.equal(isPackageDeclared(devOnly, PKG), true);

  const empty = makeWorkspace({ name: 'app', version: '1.0.0' });
  assert.equal(isPackageDeclared(empty, PKG), false);

  const none = makeWorkspace();
  assert.equal(isPackageDeclared(none, PKG), false);
});

test('formatRuntimeDependencyHint surfaces versionMismatch as a drift warning', () => {
  // Defensive: live orchestrator + init flows now auto-update on mismatch, so
  // this branch should be unreachable in practice. Keep the helper to handle
  // any future passive flow that needs a "run init to resync" hint.
  const hint = formatRuntimeDependencyHint('versionMismatch', PKG, '0.2.0', 'npm install');
  assert.equal(hint.needsInstall, false);
  assert.match(hint.message, /version drift detected/i);
  assert.match(hint.message, /winapp init/);
});
