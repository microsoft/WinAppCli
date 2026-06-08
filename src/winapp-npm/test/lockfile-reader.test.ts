// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import { tryReadLockfile, getLockfilePath, LOCKFILE_SCHEMA_VERSION } from '../src/jsbindings/lockfile-reader';

function withWorkspace(fn: (dir: string) => void): void {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-lock-'));
  try {
    fn(dir);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
}

function writeLockfile(workspaceDir: string, body: unknown): void {
  const filePath = getLockfilePath(workspaceDir);
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  fs.writeFileSync(filePath, typeof body === 'string' ? body : JSON.stringify(body));
}

test('tryReadLockfile returns null with no reason when the lockfile is absent', () => {
  withWorkspace((dir) => {
    const result = tryReadLockfile(dir);
    assert.equal(result.lockfile, null);
    assert.equal(result.reason, undefined);
  });
});

test('tryReadLockfile parses a valid schema-3 lockfile', () => {
  withWorkspace((dir) => {
    const cacheDir = path.join(dir, 'cache');
    fs.mkdirSync(cacheDir, { recursive: true });
    const winmd = path.join(cacheDir, 'Some.winmd');
    fs.writeFileSync(winmd, '');
    writeLockfile(dir, {
      schema: LOCKFILE_SCHEMA_VERSION,
      nuget_cache_dir: cacheDir,
      yaml_packages_hash: 'abc123',
      packages: [{ name: 'Pkg', version: '1.0', winmds: [winmd] }],
    });

    const { lockfile, reason } = tryReadLockfile(dir);
    assert.equal(reason, undefined);
    assert.ok(lockfile);
    assert.equal(lockfile!.schemaVersion, LOCKFILE_SCHEMA_VERSION);
    assert.equal(lockfile!.yamlPackagesHash, 'abc123');
    assert.deepEqual(lockfile!.packages, [{ name: 'Pkg', version: '1.0', winmds: [winmd] }]);
  });
});

test('tryReadLockfile rejects a schema mismatch', () => {
  withWorkspace((dir) => {
    writeLockfile(dir, { schema: 999, nuget_cache_dir: dir, packages: [] });
    const { lockfile, reason } = tryReadLockfile(dir);
    assert.equal(lockfile, null);
    assert.match(reason ?? '', /schema mismatch/i);
  });
});

test('tryReadLockfile rejects a lockfile missing nuget_cache_dir', () => {
  withWorkspace((dir) => {
    writeLockfile(dir, { schema: LOCKFILE_SCHEMA_VERSION, packages: [] });
    const { lockfile, reason } = tryReadLockfile(dir);
    assert.equal(lockfile, null);
    assert.match(reason ?? '', /nuget_cache_dir/i);
  });
});

test('tryReadLockfile rejects invalid JSON', () => {
  withWorkspace((dir) => {
    writeLockfile(dir, '{ not json');
    const { lockfile, reason } = tryReadLockfile(dir);
    assert.equal(lockfile, null);
    assert.match(reason ?? '', /not valid JSON/i);
  });
});

test('tryReadLockfile refuses winmd paths outside the recorded nuget cache', () => {
  withWorkspace((dir) => {
    const cacheDir = path.join(dir, 'cache');
    fs.mkdirSync(cacheDir, { recursive: true });
    writeLockfile(dir, {
      schema: LOCKFILE_SCHEMA_VERSION,
      nuget_cache_dir: cacheDir,
      packages: [{ name: 'Pkg', version: '1.0', winmds: ['C:\\Windows\\evil.winmd'] }],
    });
    const { lockfile, reason } = tryReadLockfile(dir);
    assert.equal(lockfile, null);
    assert.match(reason ?? '', /outside the recorded/i);
  });
});

test('tryReadLockfile refuses missing winmd paths inside the recorded nuget cache', () => {
  withWorkspace((dir) => {
    const cacheDir = path.join(dir, 'cache');
    fs.mkdirSync(cacheDir, { recursive: true });
    const missingWinmd = path.join(cacheDir, 'Missing.winmd');
    writeLockfile(dir, {
      schema: LOCKFILE_SCHEMA_VERSION,
      nuget_cache_dir: cacheDir,
      packages: [{ name: 'Pkg', version: '1.0', winmds: [missingWinmd] }],
    });

    const { lockfile, reason } = tryReadLockfile(dir);
    assert.equal(lockfile, null);
    assert.match(reason ?? '', /missing/i);
  });
});

test('tryReadLockfile refuses winmd paths that resolve to directories', () => {
  withWorkspace((dir) => {
    const cacheDir = path.join(dir, 'cache');
    const directoryWinmd = path.join(cacheDir, 'Directory.winmd');
    fs.mkdirSync(directoryWinmd, { recursive: true });
    writeLockfile(dir, {
      schema: LOCKFILE_SCHEMA_VERSION,
      nuget_cache_dir: cacheDir,
      packages: [{ name: 'Pkg', version: '1.0', winmds: [directoryWinmd] }],
    });

    const { lockfile, reason } = tryReadLockfile(dir);
    assert.equal(lockfile, null);
    assert.match(reason ?? '', /not files/i);
  });
});
