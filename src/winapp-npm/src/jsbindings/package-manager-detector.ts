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
