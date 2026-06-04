// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import { test } from 'node:test';
import * as assert from 'node:assert/strict';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';

import {
  buildAddExactCommand,
  detectPackageManager,
  resolvePackageManagerPath,
} from '../src/jsbindings/package-manager-detector';

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

const isWin = process.platform === 'win32';
// On Windows the launcher is a PATHEXT-extension file (npm.cmd); elsewhere it's bare.
const launcherExt = isWin ? '.cmd' : '';

function withEnv(overrides: Record<string, string | undefined>, fn: () => void): void {
  const saved: Record<string, string | undefined> = {};
  for (const k of Object.keys(overrides)) {
    saved[k] = process.env[k];
    if (overrides[k] === undefined) {
      delete process.env[k];
    } else {
      process.env[k] = overrides[k];
    }
  }
  try {
    fn();
  } finally {
    for (const [k, v] of Object.entries(saved)) {
      if (v === undefined) {
        delete process.env[k];
      } else {
        process.env[k] = v;
      }
    }
  }
}

test('resolvePackageManagerPath returns the absolute path of a launcher found on PATH', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-which-'));
  try {
    const launcher = path.join(dir, `npm${launcherExt}`);
    fs.writeFileSync(launcher, '');
    withEnv({ PATH: dir, PATHEXT: '.COM;.EXE;.BAT;.CMD' }, () => {
      const resolved = resolvePackageManagerPath('npm');
      // On Windows the returned extension casing follows PATHEXT (.CMD) while the
      // file is npm.cmd; both refer to the same file on a case-insensitive FS.
      assert.equal(resolved?.toLowerCase(), launcher.toLowerCase());
    });
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

test('resolvePackageManagerPath returns null when PATH is unset', () => {
  withEnv({ PATH: undefined, Path: undefined }, () => {
    assert.equal(resolvePackageManagerPath('npm'), null);
  });
});

test('resolvePackageManagerPath returns null when the launcher is not on PATH', () => {
  const emptyDir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-which-empty-'));
  try {
    withEnv({ PATH: emptyDir, PATHEXT: '.COM;.EXE;.BAT;.CMD' }, () => {
      assert.equal(resolvePackageManagerPath('pnpm'), null);
    });
  } finally {
    fs.rmSync(emptyDir, { recursive: true, force: true });
  }
});

test('resolvePackageManagerPath ignores a launcher in the current directory (shim-hijack defense)', () => {
  // A malicious `npm.cmd` dropped in the workspace/cwd must NOT be resolved:
  // only PATH is scanned, never process.cwd().
  const cwdDir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-which-cwd-'));
  const emptyPathDir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-which-pathonly-'));
  const savedCwd = process.cwd();
  try {
    fs.writeFileSync(path.join(cwdDir, `npm${launcherExt}`), '');
    process.chdir(cwdDir);
    withEnv({ PATH: emptyPathDir, PATHEXT: '.COM;.EXE;.BAT;.CMD' }, () => {
      // PATH has no npm launcher; the one in cwd must be ignored.
      assert.equal(resolvePackageManagerPath('npm'), null);
    });
  } finally {
    process.chdir(savedCwd);
    fs.rmSync(cwdDir, { recursive: true, force: true });
    fs.rmSync(emptyPathDir, { recursive: true, force: true });
  }
});

test('resolvePackageManagerPath skips relative PATH entries (workspace-shim defense)', () => {
  // A relative PATH entry (".", "tools", …) would join to a relative candidate
  // that fs.statSync resolves against process.cwd(); the resulting relative
  // path, handed to the installer running with cwd=workspaceDir, would resolve
  // a workspace-controlled shim (CWE-426). Only absolute PATH dirs are trusted.
  const cwdDir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-which-rel-'));
  const savedCwd = process.cwd();
  try {
    fs.writeFileSync(path.join(cwdDir, `npm${launcherExt}`), '');
    process.chdir(cwdDir);
    withEnv({ PATH: '.', PATHEXT: '.COM;.EXE;.BAT;.CMD' }, () => {
      assert.equal(resolvePackageManagerPath('npm'), null);
    });
  } finally {
    process.chdir(savedCwd);
    fs.rmSync(cwdDir, { recursive: true, force: true });
  }
});
