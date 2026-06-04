// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Detects which package manager (npm / yarn / pnpm / bun) a workspace uses,
// so we can print the right install command after mutating package.json.
//
// Ported from C# `PackageManagerDetector.cs`. Priority:
//   1. Corepack `packageManager` field in package.json
//   2. Lockfile sniffing (pnpm-lock.yaml → pnpm, yarn.lock → yarn, etc.)
//   3. Fallback: npm
//
// Pure synchronous filesystem reads; no spawn.

import * as fs from 'fs';
import * as path from 'path';

export type PackageManagerName = 'npm' | 'yarn' | 'pnpm' | 'bun';

export interface DetectedPackageManager {
  name: PackageManagerName;
  installCommand: string;
}

/**
 * Build the argv (executable + args, no shell) for adding a single package at an
 * exact version with the given package manager. The version is pinned exactly
 * (no `^`/`~`) so the installed runtime always matches the codegen pin.
 *
 * `packageSpec` must already be `name@version`.
 */
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
 * Resolve the absolute path to a package manager's launcher by scanning
 * `process.env.PATH` (and `PATHEXT` on Windows), mirroring the OS executable
 * lookup. Returns `null` when the executable cannot be found (or `PATH` is
 * unset), so callers can degrade to a best-effort warning.
 *
 * SECURITY (why we don't just spawn a bare `npm`): on Windows the launchers are
 * `.cmd` shims, and `cmd.exe` (and `node-which`) resolve a bare command name
 * against the CURRENT DIRECTORY *before* `PATH`. If we spawned `npm` with
 * `cwd` set to an untrusted workspace, a malicious `npm.cmd` dropped in that
 * workspace would hijack execution (CWE-426 untrusted search path). We
 * deliberately scan ONLY `PATH` here — never the workspace / `process.cwd()` —
 * and hand the resulting ABSOLUTE path to the spawn, which makes `cmd.exe`
 * skip its current-directory lookup entirely.
 *
 * Note: `process.env.PATH` is read through Node's case-insensitive `process.env`
 * proxy on Windows, so it resolves a `Path`/`path` variable too.
 */
export function resolvePackageManagerPath(name: PackageManagerName): string | null {
  const rawPath = process.env.PATH;
  if (!rawPath) {
    return null;
  }
  const dirs = rawPath.split(path.delimiter).filter((d) => d.length > 0);

  // On Windows, try each PATHEXT extension (npm → npm.cmd / npm.exe). On other
  // platforms the launcher has no extension.
  const exts =
    process.platform === 'win32'
      ? (process.env.PATHEXT || '.COM;.EXE;.BAT;.CMD')
          .split(';')
          .map((e) => e.trim())
          .filter((e) => e.length > 0)
      : [''];

  for (const dir of dirs) {
    // SECURITY: skip non-absolute PATH entries (`.`, `tools`, …). A relative
    // entry would be joined to a relative candidate that `fs.statSync` resolves
    // against `process.cwd()`, and the resulting relative path handed to the
    // installer (which runs with `cwd: workspaceDir`) would resolve a
    // workspace-controlled shim — the very CWE-426 hijack this function exists
    // to prevent. Only absolute PATH directories are trusted.
    if (!path.isAbsolute(dir)) {
      continue;
    }
    for (const ext of exts) {
      const candidate = path.join(dir, `${name}${ext}`);
      try {
        if (!fs.statSync(candidate).isFile()) {
          continue;
        }
        // Collapse any symlink/junction to its real location so the spawned
        // absolute path can't be redirected back into an untrusted directory.
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
