// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'node:fs';
import * as os from 'node:os';
import * as path from 'node:path';

import {
  writeListFile,
  computeCherryPickInputs,
  buildGenerateArgs,
  buildBulkArgs,
  buildCherryPickArgs,
  parseCapabilitiesOutput,
  parseRuntimeDependencySpec,
} from '../src/jsbindings/codegen-runner';

function tmpDir(): string {
  return fs.mkdtempSync(path.join(os.tmpdir(), 'codegen-runner-test-'));
}

test('writeListFile writes newline-separated paths with trailing newline', () => {
  const dir = tmpDir();
  try {
    const p = writeListFile(dir, 'emit.txt', ['a.winmd', 'b.winmd']);
    assert.equal(p, path.join(dir, 'emit.txt'));
    assert.equal(fs.readFileSync(p, 'utf8'), 'a.winmd\nb.winmd\n');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('computeCherryPickInputs with explicit winmdPath adds it to emit and drops it from refs', () => {
  const out = computeCherryPickInputs(['e1.winmd', 'e2.winmd'], ['r1.winmd', 'pick.winmd'], {
    winmdPath: 'pick.winmd',
    namespace: 'Foo.Bar',
    classes: ['Baz'],
  });
  assert.deepEqual(out.winmds.sort(), ['e1.winmd', 'e2.winmd', 'pick.winmd'].sort());
  assert.deepEqual(out.refs, ['r1.winmd']);
});

test('computeCherryPickInputs without winmdPath emits nothing and pushes emit winmds into refs', () => {
  const out = computeCherryPickInputs(['e1.winmd', 'e2.winmd'], ['r1.winmd'], {
    namespace: 'Foo.Bar',
    classes: ['Baz'],
  });
  assert.deepEqual(out.winmds, []);
  assert.deepEqual(out.refs.sort(), ['e1.winmd', 'e2.winmd', 'r1.winmd'].sort());
});

test('buildGenerateArgs emits flags only when inputs are present', () => {
  const bulk = buildGenerateArgs(['prefix.js'], {
    winmdListPath: 'emit.txt',
    refListPath: 'ref.txt',
    outputDir: 'out',
  });
  assert.deepEqual(bulk, [
    'prefix.js',
    'generate',
    '--winmd-list',
    'emit.txt',
    '--output',
    'out',
    '--ref-list',
    'ref.txt',
  ]);

  const minimal = buildGenerateArgs([], { outputDir: 'out' });
  assert.deepEqual(minimal, ['generate', '--output', 'out']);
  assert.ok(!minimal.includes('--winmd-list'));
  assert.ok(!minimal.includes('--ref-list'));
});

test('buildGenerateArgs includes cherry-pick selectors when provided', () => {
  const args = buildGenerateArgs([], {
    winmdListPath: 'emit.txt',
    namespace: 'Foo.Bar',
    classes: ['Baz', 'Qux'],
    outputDir: 'out',
  });
  assert.ok(args.includes('--namespace'));
  assert.equal(args[args.indexOf('--namespace') + 1], 'Foo.Bar');
  assert.equal(args[args.indexOf('--class-name') + 1], 'Baz,Qux');
});

test('buildBulkArgs writes list files and references them', () => {
  const dir = tmpDir();
  try {
    const args = buildBulkArgs([], ['e1.winmd'], 'out', ['r1.winmd'], dir, 0);
    const emitPath = args[args.indexOf('--winmd-list') + 1];
    const refPath = args[args.indexOf('--ref-list') + 1];
    assert.equal(fs.readFileSync(emitPath, 'utf8'), 'e1.winmd\n');
    assert.equal(fs.readFileSync(refPath, 'utf8'), 'r1.winmd\n');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('buildBulkArgs omits --ref-list when there are no refs', () => {
  const dir = tmpDir();
  try {
    const args = buildBulkArgs([], ['e1.winmd'], 'out', [], dir, 1);
    assert.ok(args.includes('--winmd-list'));
    assert.ok(!args.includes('--ref-list'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('buildCherryPickArgs without winmdPath omits --winmd-list and moves emit winmds to refs', () => {
  const dir = tmpDir();
  try {
    const args = buildCherryPickArgs([], ['e1.winmd'], 'out', ['r1.winmd'], { namespace: 'Foo', classes: ['Bar'] }, dir, 2);
    assert.ok(!args.includes('--winmd-list'));
    const refPath = args[args.indexOf('--ref-list') + 1];
    // emit winmds were pushed into the ref list for type resolution.
    assert.equal(fs.readFileSync(refPath, 'utf8'), 'r1.winmd\ne1.winmd\n');
    assert.equal(args[args.indexOf('--namespace') + 1], 'Foo');
    assert.equal(args[args.indexOf('--class-name') + 1], 'Bar');
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('buildCherryPickArgs with winmdPath writes an emit list including the picked winmd', () => {
  const dir = tmpDir();
  try {
    const args = buildCherryPickArgs(
      [],
      ['e1.winmd'],
      'out',
      ['r1.winmd'],
      { winmdPath: 'pick.winmd', namespace: 'Foo', classes: ['Bar'] },
      dir,
      3
    );
    const emitPath = args[args.indexOf('--winmd-list') + 1];
    const emitContent = fs.readFileSync(emitPath, 'utf8');
    assert.ok(emitContent.includes('e1.winmd'));
    assert.ok(emitContent.includes('pick.winmd'));
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('parseCapabilitiesOutput trims empty lines and comments', () => {
  const caps = parseCapabilitiesOutput('\n# comment\ninput.winmd-list\r\n input.ref-list \n');
  assert.deepEqual([...caps].sort(), ['input.ref-list', 'input.winmd-list']);
});

test('parseRuntimeDependencySpec parses scoped package specs', () => {
  assert.deepEqual(parseRuntimeDependencySpec('@microsoft/dynwinrt@0.1.0-preview.5\n'), {
    packageName: '@microsoft/dynwinrt',
    version: '0.1.0-preview.5',
  });
});

test('parseRuntimeDependencySpec rejects unexpected packages', () => {
  assert.throws(
    () => parseRuntimeDependencySpec('@example/dynwinrt@1.0.0'),
    /expected '@microsoft\/dynwinrt'/
  );
});
