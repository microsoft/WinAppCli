// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { buildAddExactCommand, detectPackageManager } from '../src/jsbindings/package-manager-detector';

test('buildAddExactCommand pins exact versions per package manager', () => {
  assert.deepEqual(buildAddExactCommand('npm', 'pkg@1.2.3'), {
    exe: 'npm',
    args: ['install', 'pkg@1.2.3', '--save-exact'],
  });
  assert.deepEqual(buildAddExactCommand('pnpm', 'pkg@1.2.3'), {
    exe: 'pnpm',
    args: ['add', 'pkg@1.2.3', '--save-exact'],
  });
  assert.deepEqual(buildAddExactCommand('yarn', 'pkg@1.2.3'), {
    exe: 'yarn',
    args: ['add', 'pkg@1.2.3', '--exact'],
  });
  assert.deepEqual(buildAddExactCommand('bun', 'pkg@1.2.3'), {
    exe: 'bun',
    args: ['add', 'pkg@1.2.3', '--exact'],
  });
});

function withTempWorkspace(files: Record<string, string>, fn: (dir: string) => void): void {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-pm-'));
  try {
    for (const [name, contents] of Object.entries(files)) {
      fs.writeFileSync(path.join(dir, name), contents);
    }
    fn(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

test('detectPackageManager prefers the corepack packageManager field', () => {
  withTempWorkspace(
    {
      'package.json': JSON.stringify({ packageManager: 'pnpm@9.1.0+sha512.abc' }),
      // A conflicting lockfile must lose to the explicit corepack declaration.
      'yarn.lock': '',
    },
    (dir) => {
      assert.equal(detectPackageManager(dir).name, 'pnpm');
    }
  );
});

test('detectPackageManager sniffs lockfiles when no corepack field is present', () => {
  withTempWorkspace({ 'pnpm-lock.yaml': '' }, (dir) => assert.equal(detectPackageManager(dir).name, 'pnpm'));
  withTempWorkspace({ 'yarn.lock': '' }, (dir) => assert.equal(detectPackageManager(dir).name, 'yarn'));
  withTempWorkspace({ 'bun.lockb': '' }, (dir) => assert.equal(detectPackageManager(dir).name, 'bun'));
  withTempWorkspace({ 'package-lock.json': '' }, (dir) => assert.equal(detectPackageManager(dir).name, 'npm'));
});

test('detectPackageManager falls back to npm for an empty workspace', () => {
  withTempWorkspace({}, (dir) => {
    const detected = detectPackageManager(dir);
    assert.equal(detected.name, 'npm');
    assert.equal(detected.installCommand, 'npm install');
  });
});

test('detectPackageManager ignores an unparsable package.json and uses lockfiles', () => {
  withTempWorkspace({ 'package.json': '{ not valid json', 'yarn.lock': '' }, (dir) => {
    assert.equal(detectPackageManager(dir).name, 'yarn');
  });
});
