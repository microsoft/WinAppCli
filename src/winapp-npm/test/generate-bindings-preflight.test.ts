// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { evaluateGenerateBindingsPreflight } from '../src/jsbindings/generate-bindings-preflight';
import { getLockfilePath } from '../src/jsbindings/lockfile-reader';

function makeWorkspace(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-preflight-'));
}

function writePackageJson(dir: string, obj: Record<string, unknown>): void {
  fs.writeFileSync(path.join(dir, 'package.json'), JSON.stringify(obj, null, 2));
}

function writeLockfile(dir: string): void {
  const lockfilePath = getLockfilePath(dir);
  fs.mkdirSync(path.dirname(lockfilePath), { recursive: true });
  fs.writeFileSync(lockfilePath, JSON.stringify({ packages: [] }));
}

test('preflight reports noPackageJson when package.json is missing', () => {
  const dir = makeWorkspace();
  const result = evaluateGenerateBindingsPreflight(dir);
  assert.equal(result.kind, 'noPackageJson');
  assert.ok(result.messageLines.some((l) => l.includes('No package.json')));
});

test('preflight reports noJsBindings when the winapp.jsBindings namespace is absent', () => {
  const dir = makeWorkspace();
  writePackageJson(dir, { name: 'app', version: '1.0.0' });
  const result = evaluateGenerateBindingsPreflight(dir);
  assert.equal(result.kind, 'noJsBindings');
  assert.ok(result.messageLines.some((l) => l.includes('winapp.jsBindings')));
});

test('preflight reports noLockfile when the restore lockfile is absent', () => {
  const dir = makeWorkspace();
  writePackageJson(dir, { name: 'app', version: '1.0.0', winapp: { jsBindings: {} } });
  const result = evaluateGenerateBindingsPreflight(dir);
  assert.equal(result.kind, 'noLockfile');
  assert.ok(result.messageLines.some((l) => l.includes('winmds.lock.json')));
});

test('preflight returns ok when package.json, jsBindings, and lockfile are present', () => {
  const dir = makeWorkspace();
  writePackageJson(dir, { name: 'app', version: '1.0.0', winapp: { jsBindings: {} } });
  writeLockfile(dir);
  const result = evaluateGenerateBindingsPreflight(dir);
  assert.equal(result.kind, 'ok');
  assert.deepEqual(result.messageLines, []);
});
