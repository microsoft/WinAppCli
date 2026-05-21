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

export interface DetectedPackageManager {
  name: 'npm' | 'yarn' | 'pnpm' | 'bun';
  installCommand: string;
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
