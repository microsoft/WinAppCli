// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
  packageJsonExists,
  readPackageJsonDoc,
  mutatePackageJsonDoc,
  atomicWriteFile,
} from '../src/jsbindings/package-json-doc';

function makeWorkspace(): string {
  return fs.realpathSync.native(fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-pkgdoc-')));
}

function writePkg(dir: string, raw: string): string {
  const p = path.join(dir, 'package.json');
  fs.writeFileSync(p, raw, 'utf8');
  return p;
}

test('packageJsonExists returns false for missing or unsafe paths', () => {
  const ws = makeWorkspace();
  assert.equal(packageJsonExists(ws), false);
  writePkg(ws, '{}');
  assert.equal(packageJsonExists(ws), true);
});

test('readPackageJsonDoc returns null when the file is missing', () => {
  const ws = makeWorkspace();
  assert.equal(readPackageJsonDoc(ws), null);
});

test('readPackageJsonDoc parses an object root and detects LF + trailing newline', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{\n  "name": "x",\n  "version": "1.0.0"\n}\n');
  const doc = readPackageJsonDoc(ws)!;
  assert.equal(doc.parsed.name, 'x');
  assert.equal(doc.eol, '\n');
  assert.equal(doc.trailingNewline, true);
});

test('readPackageJsonDoc detects CRLF when the file uses Windows line endings', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{\r\n  "name": "x"\r\n}\r\n');
  const doc = readPackageJsonDoc(ws)!;
  assert.equal(doc.eol, '\r\n');
  assert.equal(doc.trailingNewline, true);
});

test('readPackageJsonDoc detects when the file has no trailing newline', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{"name":"x"}');
  const doc = readPackageJsonDoc(ws)!;
  assert.equal(doc.trailingNewline, false);
});

test('readPackageJsonDoc throws on invalid JSON', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{ not valid json');
  assert.throws(() => readPackageJsonDoc(ws), /Failed to parse/);
});

test('readPackageJsonDoc throws when the top-level value is an array', () => {
  const ws = makeWorkspace();
  writePkg(ws, '[1, 2, 3]');
  assert.throws(() => readPackageJsonDoc(ws), /top-level value must be an object/);
});

test('readPackageJsonDoc throws when the top-level value is a scalar', () => {
  const ws = makeWorkspace();
  writePkg(ws, '"just a string"');
  assert.throws(() => readPackageJsonDoc(ws), /top-level value must be an object/);
});

test('readPackageJsonDoc throws when the top-level value is null', () => {
  const ws = makeWorkspace();
  writePkg(ws, 'null');
  assert.throws(() => readPackageJsonDoc(ws), /top-level value must be an object/);
});

test('mutatePackageJsonDoc applies in-place edits and preserves LF + trailing newline', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{\n  "name": "x"\n}\n');
  mutatePackageJsonDoc(ws, (pkg) => {
    pkg.version = '2.0.0';
  });
  const raw = fs.readFileSync(path.join(ws, 'package.json'), 'utf8');
  assert.equal(raw.endsWith('\n'), true);
  assert.equal(raw.includes('\r\n'), false);
  assert.equal(JSON.parse(raw).version, '2.0.0');
});

test('mutatePackageJsonDoc preserves CRLF on write after mutation', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{\r\n  "name": "x"\r\n}\r\n');
  mutatePackageJsonDoc(ws, (pkg) => {
    pkg.version = '2.0.0';
  });
  const raw = fs.readFileSync(path.join(ws, 'package.json'), 'utf8');
  // Every line break must be CRLF, including the final trailing newline.
  assert.equal(/\n(?!\r)/.test(raw.replace(/\r\n/g, '')), false);
  assert.equal(raw.endsWith('\r\n'), true);
});

test('mutatePackageJsonDoc accepts a replacement object returned by the mutator', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{"name":"old"}');
  mutatePackageJsonDoc(ws, () => ({ name: 'new', version: '1.0.0' }));
  const parsed = JSON.parse(fs.readFileSync(path.join(ws, 'package.json'), 'utf8'));
  assert.deepEqual(parsed, { name: 'new', version: '1.0.0' });
});

test('mutatePackageJsonDoc omits a trailing newline that was not in the original', () => {
  const ws = makeWorkspace();
  writePkg(ws, '{"name":"x"}');
  mutatePackageJsonDoc(ws, (pkg) => {
    pkg.version = '1.0.0';
  });
  const raw = fs.readFileSync(path.join(ws, 'package.json'), 'utf8');
  assert.equal(raw.endsWith('\n'), false);
});

test('mutatePackageJsonDoc throws when package.json is missing', () => {
  const ws = makeWorkspace();
  assert.throws(() => mutatePackageJsonDoc(ws, () => undefined), /package\.json not found/);
});

test('atomicWriteFile cleans up the sibling temp file on success', () => {
  const ws = makeWorkspace();
  const target = path.join(ws, 'package.json');
  atomicWriteFile(target, '{"name":"x"}\n');
  assert.equal(fs.readFileSync(target, 'utf8'), '{"name":"x"}\n');
  const leftovers = fs.readdirSync(ws).filter((n) => n.startsWith('.package.json.') && n.endsWith('.tmp'));
  assert.deepEqual(leftovers, []);
});

test('atomicWriteFile overwrites an existing file atomically', () => {
  const ws = makeWorkspace();
  const target = path.join(ws, 'package.json');
  fs.writeFileSync(target, '{"name":"old"}');
  atomicWriteFile(target, '{"name":"new"}\n');
  assert.equal(fs.readFileSync(target, 'utf8'), '{"name":"new"}\n');
});
