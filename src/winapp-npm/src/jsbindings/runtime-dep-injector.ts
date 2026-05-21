// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Mutates the user's package.json to declare `@microsoft/dynwinrt` as a
// production dependency. Returns a structured outcome so the caller can
// print the right hint.
//
// Ported from C# `UserPackageJsonService.cs`. Key invariants:
//   * Refuse to write through reparse-point ancestors (symlinks / junctions)
//     — same protection the native side enforced via PathSafety.
//   * Preserve unrelated keys exactly. Insert "dependencies" right after
//     "version" when creating the block from scratch.
//   * Atomic write via sibling tmp + fs.renameSync (Windows: same-volume rename
//     is atomic; fall back to copy+unlink if the rename fails).
//   * Don't auto-promote a dev→prod dep — the user pinned it under dev for a
//     reason; report `PresentInDevDependencies` instead.

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { hasReparsePointOnPath } from './path-safety';

export type RuntimeDependencyOutcome = 'added' | 'alreadyPresent' | 'presentInDevDependencies' | 'noPackageJson';

export interface EnsureRuntimeDependencyResult {
  outcome: RuntimeDependencyOutcome;
  /** When `outcome === 'added'`, the value that was written. */
  pinnedVersion?: string;
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

  const packageJsonPath = path.join(workspaceDir, 'package.json');

  // Refuse to follow reparse points / UNC ancestors BEFORE probing existence.
  if (hasReparsePointOnPath(packageJsonPath, workspaceDir)) {
    throw new Error(
      `Refusing to rewrite '${packageJsonPath}': the file or one of its ` +
        'ancestors is a symbolic link / reparse point. Resolve the link ' +
        'and re-run, or add the runtime dependency manually.'
    );
  }

  if (!fs.existsSync(packageJsonPath)) {
    return { outcome: 'noPackageJson' };
  }

  let original: string;
  try {
    original = fs.readFileSync(packageJsonPath, 'utf8');
  } catch (err) {
    throw new Error(`Failed to read ${packageJsonPath}: ${(err as Error).message}`, { cause: err });
  }

  let root: unknown;
  try {
    root = JSON.parse(original);
  } catch (err) {
    throw new Error(`Failed to parse ${packageJsonPath}: ${(err as Error).message}`, { cause: err });
  }

  if (!root || typeof root !== 'object' || Array.isArray(root)) {
    throw new Error(`${packageJsonPath} root is not a JSON object.`);
  }

  const obj = root as Record<string, unknown>;
  const deps = obj.dependencies;
  if (deps && typeof deps === 'object' && !Array.isArray(deps)) {
    if (packageName in (deps as Record<string, unknown>)) {
      return { outcome: 'alreadyPresent' };
    }
  }

  const devDeps = obj.devDependencies;
  if (devDeps && typeof devDeps === 'object' && !Array.isArray(devDeps)) {
    if (packageName in (devDeps as Record<string, unknown>)) {
      return { outcome: 'presentInDevDependencies' };
    }
  }

  // Add to dependencies; insert the block right after "version" when creating it.
  const rebuilt = insertOrUpdateDependency(obj, packageName, version);

  // npm/yarn/pnpm conventionally use 2-space indent + trailing newline.
  const serialized = JSON.stringify(rebuilt, null, 2);
  const final = original.endsWith('\n') && !serialized.endsWith('\n') ? serialized + '\n' : serialized;

  atomicWriteFile(packageJsonPath, final);
  return { outcome: 'added', pinnedVersion: version };
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

function atomicWriteFile(filePath: string, content: string): void {
  const dir = path.dirname(filePath);
  const tmpName = `${path.basename(filePath)}.tmp.${process.pid}.${Date.now()}.${Math.random().toString(36).slice(2)}`;
  const tmpPath = path.join(dir, tmpName);
  let staged = false;
  try {
    fs.writeFileSync(tmpPath, content, { encoding: 'utf8' });
    staged = true;
    // Windows: same-volume rename is atomic. fs.renameSync overwrites the target.
    fs.renameSync(tmpPath, filePath);
    staged = false;
  } catch (err) {
    // Fallback for the rare cross-volume case (or AV / sharing-violation
    // races): copy+unlink. Not atomic, but better than leaving the file
    // mid-write.
    if (staged) {
      try {
        fs.copyFileSync(tmpPath, filePath);
        try {
          fs.unlinkSync(tmpPath);
        } catch {
          /* leaked tmp is harmless */
        }
        return;
      } catch (fallbackErr) {
        try {
          fs.unlinkSync(tmpPath);
        } catch {
          /* ignore */
        }
        throw new Error(
          `Failed to write ${filePath}: ${(fallbackErr as Error).message} (after rename error: ${(err as Error).message})`,
          { cause: fallbackErr }
        );
      }
    }
    throw new Error(`Failed to write ${filePath}: ${(err as Error).message}`, { cause: err });
  }
}

// Resolves the pinned `@microsoft/dynwinrt` version by reading the winapp-npm
// package's own dependencies (the wrapper bundles dynwinrt-codegen, and
// dynwinrt shares the same pin). Mirrors the C# `NpmWrapperVersionProvider`.
export function getDynWinrtVersionPin(): string {
  // The dist/jsbindings/runtime-dep-injector.js → resolve back to package.json.
  // __dirname is dist/jsbindings/ in prod, src/jsbindings/ in test/dev.
  // Walk up looking for the winapp-npm package.json.
  const start = __dirname;
  let dir = start;
  const root = path.parse(dir).root;
  for (;;) {
    const candidate = path.join(dir, 'package.json');
    if (fs.existsSync(candidate)) {
      try {
        const parsed = JSON.parse(fs.readFileSync(candidate, 'utf8')) as Record<string, unknown>;
        if (parsed.name === '@microsoft/winappcli') {
          const deps = parsed.dependencies;
          if (deps && typeof deps === 'object') {
            const v = (deps as Record<string, unknown>)['@microsoft/dynwinrt-codegen'];
            if (typeof v === 'string' && v.trim()) {
              return v;
            }
          }
          throw new Error(
            `${candidate} is the @microsoft/winappcli package.json but has no @microsoft/dynwinrt-codegen pin.`
          );
        }
      } catch (err) {
        // Wrong package.json; keep walking.
        if (err instanceof Error && err.message.includes('@microsoft/dynwinrt-codegen pin')) {
          throw err;
        }
      }
    }
    if (dir === root) {
      break;
    }
    const parent = path.dirname(dir);
    if (parent === dir) {
      break;
    }
    dir = parent;
  }
  throw new Error(
    `Could not locate the @microsoft/winappcli package.json near ${start}. ` +
      'This typically means the npm wrapper is running outside its install layout.'
  );
}

// Hint formatting helper — keeps cli-side glue free of switch statements.
export interface RuntimeDependencyHint {
  /** ANSI-friendly message to print. */
  message: string;
  /** True when the user should run `<pm> install` to materialize a new dep. */
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
        message: `✅ Added ${packageName}@${pinnedVersion} to your package.json dependencies. Run \`${installCommand}\` to materialize it.`,
        needsInstall: true,
      };
    case 'alreadyPresent':
      return {
        message: `✅ ${packageName} already declared in package.json dependencies — leaving it alone.`,
        needsInstall: false,
      };
    case 'presentInDevDependencies':
      return {
        message: `ℹ️  ${packageName} is in devDependencies — generated bindings need it as a production dep. Move it manually.`,
        needsInstall: false,
      };
    case 'noPackageJson':
      return {
        message: `⚠️ No package.json found in workspace. Generated bindings will fail to resolve ${packageName} at runtime.${eol}    Run \`npm init -y\` first, then re-run \`winapp restore\` to add the dependency.`,
        needsInstall: false,
      };
    default: {
      const _exhaustive: never = outcome;
      return { message: `Unknown outcome: ${String(_exhaustive)}`, needsInstall: false };
    }
  }
}
