// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Detects npm/yarn/pnpm/bun so hints match the workspace.
// Ported from C# `PackageManagerDetector.cs`: Corepack field, lockfile sniffing, then npm.
// Pure synchronous filesystem reads; no spawn.

import * as fs from 'fs';
import * as path from 'path';

export type PackageManagerName = 'npm' | 'yarn' | 'pnpm' | 'bun';

export interface DetectedPackageManager {
  name: PackageManagerName;
  installCommand: string;
}

/** Build argv (no shell) for adding exact-pinned `name@version` so runtime matches codegen. */
export function buildAddExactCommand(name: PackageManagerName, packageSpec: string): { exe: string; args: string[] } {
  switch (name) {
    case 'npm':
      return { exe: 'npm', args: ['install', packageSpec, '--save-exact'] };
    case 'pnpm':
      return { exe: 'pnpm', args: ['add', packageSpec, '--save-exact'] };
    case 'yarn':
      return { exe: 'yarn', args: ['add', packageSpec, '--exact'] };
    case 'bun':
      return { exe: 'bun', args: ['add', packageSpec, '--exact'] };
  }
}

/**
 * Resolve a package manager launcher from PATH/PATHEXT, returning null if absent.
 * Security: never spawn bare `npm`; Windows `.cmd` lookup checks CWD before PATH,
 * so we pass an absolute PATH-only launcher to avoid workspace hijack (CWE-426).
 * Node's Windows env proxy also covers `Path`/`path` casing.
 */
export function resolvePackageManagerPath(name: PackageManagerName): string | null {
  const rawPath = process.env.PATH;
  if (!rawPath) {
    return null;
  }
  const dirs = rawPath.split(path.delimiter).filter((d) => d.length > 0);

  // Windows tries PATHEXT variants (npm.cmd/npm.exe); other platforms use no extension.
  const exts =
    process.platform === 'win32'
      ? (process.env.PATHEXT || '.COM;.EXE;.BAT;.CMD')
          .split(';')
          .map((e) => e.trim())
          .filter((e) => e.length > 0)
      : [''];

  for (const dir of dirs) {
    // Skip relative PATH entries: they resolve through CWD/workspace and enable CWE-426 hijack.
    if (!path.isAbsolute(dir)) {
      continue;
    }
    for (const ext of exts) {
      const candidate = path.join(dir, `${name}${ext}`);
      try {
        if (!fs.statSync(candidate).isFile()) {
          continue;
        }
        // Collapse symlinks/junctions so the spawned absolute path can't redirect to untrusted code.
        const real = fs.realpathSync.native(candidate);
        if (!path.isAbsolute(real)) {
          continue;
        }
        return real;
      } catch {
        // Not present / not accessible — keep scanning.
      }
    }
  }
  return null;
}

export function detectPackageManager(workspaceDir: string): DetectedPackageManager {
  // Priority 1: Corepack packageManager field.
  const pkgJson = path.join(workspaceDir, 'package.json');
  if (fs.existsSync(pkgJson)) {
    const fromCorepack = tryReadCorepackField(pkgJson);
    if (fromCorepack) {
      return fromCorepack;
    }
  }

  // Priority 2: lockfile sniffing. pnpm/yarn/bun first because
  // package-lock.json is sometimes auto-created by tools in non-npm workspaces.
  if (fs.existsSync(path.join(workspaceDir, 'pnpm-lock.yaml'))) {
    return { name: 'pnpm', installCommand: 'pnpm install' };
  }
  if (fs.existsSync(path.join(workspaceDir, 'yarn.lock'))) {
    return { name: 'yarn', installCommand: 'yarn install' };
  }
  if (fs.existsSync(path.join(workspaceDir, 'bun.lockb')) || fs.existsSync(path.join(workspaceDir, 'bun.lock'))) {
    return { name: 'bun', installCommand: 'bun install' };
  }
  if (
    fs.existsSync(path.join(workspaceDir, 'package-lock.json')) ||
    fs.existsSync(path.join(workspaceDir, 'npm-shrinkwrap.json'))
  ) {
    return { name: 'npm', installCommand: 'npm install' };
  }

  // Fallback.
  return { name: 'npm', installCommand: 'npm install' };
}

function tryReadCorepackField(packageJsonPath: string): DetectedPackageManager | null {
  let raw: string;
  try {
    raw = fs.readFileSync(packageJsonPath, 'utf8');
  } catch {
    return null;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(raw);
  } catch {
    return null;
  }

  if (!parsed || typeof parsed !== 'object') {
    return null;
  }
  const pm = (parsed as Record<string, unknown>).packageManager;
  if (typeof pm !== 'string' || !pm.trim()) {
    return null;
  }

  // Format: "<name>@<version>" with optional "+sha" suffix.
  const atIndex = pm.indexOf('@');
  const name = atIndex >= 0 ? pm.substring(0, atIndex) : pm;
  switch (name.trim().toLowerCase()) {
    case 'npm':
      return { name: 'npm', installCommand: 'npm install' };
    case 'yarn':
      return { name: 'yarn', installCommand: 'yarn install' };
    case 'pnpm':
      return { name: 'pnpm', installCommand: 'pnpm install' };
    case 'bun':
      return { name: 'bun', installCommand: 'bun install' };
    default:
      // Unknown PM declaration; fall through to lockfile sniffing.
      return null;
  }
}
