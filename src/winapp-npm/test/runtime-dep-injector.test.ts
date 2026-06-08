// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
  ensureRuntimeDependency,
  getRuntimeDependencyVersion,
  isRuntimeDependencyDeclared,
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

test('ensureRuntimeDependency overwrites a stale pinned version', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0', dependencies: { [PKG]: '0.1.0' } });
  const result = ensureRuntimeDependency(dir, PKG, '0.2.0');
  assert.deepEqual(result, { outcome: 'added', pinnedVersion: '0.2.0' });
  assert.equal(readDeps(dir)[PKG], '0.2.0');
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
