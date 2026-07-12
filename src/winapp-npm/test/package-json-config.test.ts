// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
  ensureJsBindingsBlock,
  ensureJsBindingsImports,
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

test('ensureJsBindingsBlock adds the block when absent (does not touch imports)', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  const outcome = ensureJsBindingsBlock(dir, { quiet: true });
  assert.equal(outcome, 'added');

  const read = readJsBindingsConfig(dir);
  assert.equal(read.packageJsonExists, true);
  assert.deepEqual(read.jsBindings, defaultJsBindingsConfig());
  assert.equal(readRawPackageJson(dir).imports, undefined);
});

test('ensureJsBindingsBlock leaves an existing block unchanged without reset', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  ensureJsBindingsBlock(dir, { quiet: true });
  writeJsBindingsConfig(dir, {
    additionalWinmds: [],
    additionalRefs: ['Custom.winmd'],
  });

  const outcome = ensureJsBindingsBlock(dir, { quiet: true });
  assert.equal(outcome, 'unchanged');
  assert.deepEqual(readJsBindingsConfig(dir).jsBindings?.additionalRefs, ['Custom.winmd']);
});

test('ensureJsBindingsBlock with reset restores defaults over a customized block', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  writeJsBindingsConfig(dir, {
    additionalWinmds: [],
    additionalRefs: ['Custom.winmd'],
  });

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

test('ensureJsBindingsImports adds both aliases to a workspace without imports', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  const result = ensureJsBindingsImports(dir);
  assert.equal(result.outcome, 'added');
  assert.deepEqual(result.diverged, []);
  assert.deepEqual(readRawPackageJson(dir).imports, {
    '#winapp/bindings': {
      types: './.winapp/bindings/index.d.ts',
      import: './.winapp/bindings/index.mjs',
      require: './.winapp/bindings/index.js',
      default: './.winapp/bindings/index.js',
    },
    '#winapp/bindings/*': {
      types: './.winapp/bindings/*.d.ts',
      default: './.winapp/bindings/*.js',
    },
  });
});

test('ensureJsBindingsImports is a no-op when both aliases already match', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0' });
  ensureJsBindingsImports(dir);
  const result = ensureJsBindingsImports(dir);
  assert.equal(result.outcome, 'unchanged');
  assert.deepEqual(result.diverged, []);
});

test('ensureJsBindingsImports preserves unrelated aliases and reports divergent ones', () => {
  const customRoot = { default: './custom-bindings.js' };
  const dir = makeWorkspace({
    name: 'app',
    version: '1.0.0',
    imports: {
      '#existing': './src/existing.js',
      '#winapp/bindings': customRoot,
    },
  });

  const result = ensureJsBindingsImports(dir);
  // The subpath alias was added, so the overall outcome is 'added'.
  assert.equal(result.outcome, 'added');
  assert.deepEqual(result.diverged, ['#winapp/bindings']);

  const imports = readRawPackageJson(dir).imports as Record<string, unknown>;
  assert.equal(imports['#existing'], './src/existing.js');
  // Divergent alias is preserved as-is, not overwritten.
  assert.deepEqual(imports['#winapp/bindings'], customRoot);
  assert.deepEqual(imports['#winapp/bindings/*'], {
    types: './.winapp/bindings/*.d.ts',
    default: './.winapp/bindings/*.js',
  });
});

test('ensureJsBindingsImports reports divergence without changing package.json when both diverge', () => {
  const customRoot = { default: './custom-bindings.js' };
  const customSub = { default: './custom-bindings/*.js' };
  const dir = makeWorkspace({
    name: 'app',
    version: '1.0.0',
    imports: {
      '#winapp/bindings': customRoot,
      '#winapp/bindings/*': customSub,
    },
  });

  const result = ensureJsBindingsImports(dir);
  assert.equal(result.outcome, 'unchanged');
  assert.deepEqual(result.diverged, ['#winapp/bindings', '#winapp/bindings/*']);

  const imports = readRawPackageJson(dir).imports as Record<string, unknown>;
  assert.deepEqual(imports['#winapp/bindings'], customRoot);
  assert.deepEqual(imports['#winapp/bindings/*'], customSub);
});

test('ensureJsBindingsImports rejects a non-object imports field with an actionable hint', () => {
  const dir = makeWorkspace({ name: 'app', version: '1.0.0', imports: 'invalid' });
  assert.throws(
    () => ensureJsBindingsImports(dir),
    /package\.json "imports" must be an object.*Edit package\.json.*winapp init --add-js-bindings/s
  );
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
