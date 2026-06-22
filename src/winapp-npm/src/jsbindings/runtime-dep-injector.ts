// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Declares `@microsoft/dynwinrt` as a production dependency.

import * as os from 'os';
import { readPackageJsonDoc, mutatePackageJsonDoc } from './package-json-doc';

export type RuntimeDependencyOutcome =
  | 'added'
  | 'alreadyPresent'
  | 'presentInDevDependencies'
  | 'noPackageJson'
  | 'versionMismatch';

export interface EnsureRuntimeDependencyResult {
  outcome: RuntimeDependencyOutcome;
  /** When `outcome === 'added'`, the value that was written. */
  pinnedVersion?: string;
  /** When `outcome === 'versionMismatch'`, the version currently pinned in `dependencies`. */
  existingVersion?: string;
}

export function ensureRuntimeDependency(
  workspaceDir: string,
  packageName: string,
  version: string
): EnsureRuntimeDependencyResult {
  if (!packageName.trim()) {
    throw new Error('packageName must not be empty');
  }
  if (!version.trim()) {
    throw new Error('version must not be empty');
  }

  // readPackageJsonDoc enforces the path-safety guard and returns null when package.json is missing.
  const doc = readPackageJsonDoc(workspaceDir);
  if (!doc) {
    return { outcome: 'noPackageJson' };
  }

  const obj = doc.parsed;
  const deps = obj.dependencies;
  if (deps && typeof deps === 'object' && !Array.isArray(deps)) {
    const depsRec = deps as Record<string, unknown>;
    const existing = depsRec[packageName];
    if (typeof existing === 'string' && existing === version) {
      return { outcome: 'alreadyPresent' };
    }
    if (typeof existing === 'string') {
      // Defer the write — let the caller decide whether to overwrite via updateRuntimeDependency.
      return { outcome: 'versionMismatch', existingVersion: existing, pinnedVersion: version };
    }
  }

  const devDeps = obj.devDependencies;
  if (devDeps && typeof devDeps === 'object' && !Array.isArray(devDeps)) {
    if (packageName in (devDeps as Record<string, unknown>)) {
      return { outcome: 'presentInDevDependencies' };
    }
  }

  // Shared helper re-reads + rechecks safety to close TOCTOU/reparse races before atomic write.
  mutatePackageJsonDoc(workspaceDir, (parsed) => insertOrUpdateDependency(parsed, packageName, version));

  return { outcome: 'added', pinnedVersion: version };
}

/** Force-write `packageName@version` into `dependencies`, replacing any prior value. */
export function updateRuntimeDependency(workspaceDir: string, packageName: string, version: string): void {
  if (!packageName.trim()) {
    throw new Error('packageName must not be empty');
  }
  if (!version.trim()) {
    throw new Error('version must not be empty');
  }
  mutatePackageJsonDoc(workspaceDir, (parsed) => insertOrUpdateDependency(parsed, packageName, version));
}

/**
 * Read-only prod dependency check for passive flows, which warn but never mutate.
 * Returns false when package.json is absent or the dep is dev-only.
 */
export function isRuntimeDependencyDeclared(workspaceDir: string, packageName: string): boolean {
  return getRuntimeDependencyVersion(workspaceDir, packageName) !== null;
}

/** Read the declared production dependency version without mutating package.json. */
export function getRuntimeDependencyVersion(workspaceDir: string, packageName: string): string | null {
  const doc = readPackageJsonDoc(workspaceDir);
  if (!doc) {
    return null;
  }
  const deps = doc.parsed.dependencies;
  if (!deps || typeof deps !== 'object' || Array.isArray(deps)) {
    return null;
  }
  const version = (deps as Record<string, unknown>)[packageName];
  return typeof version === 'string' && version.trim() ? version : null;
}

/** Read the declared devDependency version without mutating package.json. */
export function getDevDependencyVersion(workspaceDir: string, packageName: string): string | null {
  const doc = readPackageJsonDoc(workspaceDir);
  if (!doc) {
    return null;
  }
  const devDeps = doc.parsed.devDependencies;
  if (!devDeps || typeof devDeps !== 'object' || Array.isArray(devDeps)) {
    return null;
  }
  const version = (devDeps as Record<string, unknown>)[packageName];
  return typeof version === 'string' && version.trim() ? version : null;
}

/** True when the package is declared in either dependencies or devDependencies. */
export function isPackageDeclared(workspaceDir: string, packageName: string): boolean {
  return (
    getRuntimeDependencyVersion(workspaceDir, packageName) !== null ||
    getDevDependencyVersion(workspaceDir, packageName) !== null
  );
}

function insertOrUpdateDependency(
  obj: Record<string, unknown>,
  packageName: string,
  version: string
): Record<string, unknown> {
  const existingDeps = obj.dependencies;
  if (existingDeps && typeof existingDeps === 'object' && !Array.isArray(existingDeps)) {
    const deps = { ...(existingDeps as Record<string, unknown>) };
    deps[packageName] = version;
    return { ...obj, dependencies: deps };
  }

  // No dependencies block — rebuild the object inserting "dependencies"
  // right after "version" (conventional layout).
  const newDeps: Record<string, unknown> = { [packageName]: version };
  const rebuilt: Record<string, unknown> = {};
  let inserted = false;
  for (const [key, value] of Object.entries(obj)) {
    rebuilt[key] = value;
    if (!inserted && key === 'version') {
      rebuilt.dependencies = newDeps;
      inserted = true;
    }
  }
  if (!inserted) {
    rebuilt.dependencies = newDeps;
  }
  return rebuilt;
}

// Hint formatting helper — keeps cli-side glue free of switch statements.
export interface RuntimeDependencyHint {
  /** ANSI-friendly message to print. */
  message: string;
  /** True when the user should run `<pm> install` to install a newly added dep locally. */
  needsInstall: boolean;
}

export function formatRuntimeDependencyHint(
  outcome: RuntimeDependencyOutcome,
  packageName: string,
  pinnedVersion: string | undefined,
  installCommand: string
): RuntimeDependencyHint {
  const eol = os.EOL;
  switch (outcome) {
    case 'added':
      return {
        message: `✅ Added ${packageName}@${pinnedVersion} to your package.json dependencies. Run \`${installCommand}\` to install it locally.`,
        needsInstall: true,
      };
    case 'alreadyPresent':
      return {
        message: `✅ ${packageName} already declared in package.json dependencies — leaving it alone.`,
        needsInstall: false,
      };
    case 'presentInDevDependencies':
      return {
        message: `💡 ${packageName} is in devDependencies — generated bindings need it as a production dep. Move it manually.`,
        needsInstall: false,
      };
    case 'noPackageJson':
      return {
        message: `⚠ No package.json found in workspace. Generated bindings will fail to resolve ${packageName} at runtime.${eol}    Run \`npm init -y\` first, then re-run \`winapp restore\` to add the dependency.`,
        needsInstall: false,
      };
    case 'versionMismatch':
      return {
        message: `⚠ ${packageName} version drift detected between package.json and the installed dynwinrt-codegen. Run \`winapp init\` to resync.`,
        needsInstall: false,
      };
    default: {
      const _exhaustive: never = outcome;
      return { message: `Unknown outcome: ${String(_exhaustive)}`, needsInstall: false };
    }
  }
}
