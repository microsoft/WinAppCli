// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Best-effort package dependency installer for init.

import { spawnSync, SpawnSyncOptions } from 'child_process';
import {
  PackageManagerName,
  PackageDependencyTarget,
  buildAddExactCommand,
  resolvePackageManagerPath,
} from './package-manager-detector';

export interface PackageInstallResult {
  ok: boolean;
  /** Human-readable command that was attempted, e.g. `npm install pkg@1 --save-exact`. */
  command: string;
  /** Set when ok is false. */
  error?: string;
}

/** Visible for unit tests. */
export function buildWindowsCmdLine(exePath: string, args: string[]): string {
  const inner = `"${exePath}" ${args.map(quoteForCmd).join(' ')}`;
  return `"${inner}"`;
}

/** Quote controlled cmd args; guards whitespace/metachars, not a general shell escaper. */
function quoteForCmd(arg: string): string {
  if (!/[\s"^&|<>()%!]/.test(arg)) {
    return arg;
  }
  // Double embedded quotes per cmd.exe rules, then wrap the whole thing.
  return `"${arg.replace(/"/g, '""')}"`;
}

/**
 * Install a package via the user's package manager. `version` empty → installs
 * `latest`. `target` selects `dependencies` vs `devDependencies`.
 */
export function installPackageDependency(
  workspaceDir: string,
  packageName: string,
  version: string,
  pmName: PackageManagerName,
  target: PackageDependencyTarget = 'dependencies'
): PackageInstallResult {
  const spec = version.trim() ? `${packageName}@${version}` : packageName;
  const { exe, args } = buildAddExactCommand(pmName, spec, target);
  const command = `${exe} ${args.join(' ')}`;

  const exePath = resolvePackageManagerPath(pmName, workspaceDir);
  if (!exePath) {
    return { ok: false, command, error: `${pmName} was not found on PATH` };
  }

  const spawnOptions: SpawnSyncOptions = {
    cwd: workspaceDir,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
    encoding: 'utf8',
  };

  const result =
    process.platform === 'win32'
      ? spawnSync('C:\\Windows\\System32\\cmd.exe', ['/d', '/s', '/c', buildWindowsCmdLine(exePath, args)], {
          ...spawnOptions,
          shell: false,
          windowsVerbatimArguments: true,
        })
      : spawnSync(exePath, args, { ...spawnOptions, shell: false });

  if (result.error) {
    const code = (result.error as NodeJS.ErrnoException).code;
    const reason = code === 'ENOENT' ? `${pmName} was not found on PATH` : result.error.message;
    return { ok: false, command, error: reason };
  }

  if (result.status !== 0) {
    const stderr = (result.stderr ?? '').toString().trim();
    const tail = stderr ? stderr.split(/\r?\n/).slice(-3).join(' ') : `exit ${result.status ?? 'null'}`;
    return { ok: false, command, error: tail };
  }

  return { ok: true, command };
}

/** @deprecated Use {@link installPackageDependency} instead. */
export const installRuntimeDependency = installPackageDependency;

/** @deprecated Use {@link PackageInstallResult} instead. */
export type RuntimeInstallResult = PackageInstallResult;

/** Run the package manager's plain `install` (no args) to honor the lockfile. */
export function runPackageManagerInstall(workspaceDir: string, pmName: PackageManagerName): PackageInstallResult {
  const args = ['install'];
  const command = `${pmName} ${args.join(' ')}`;

  const exePath = resolvePackageManagerPath(pmName, workspaceDir);
  if (!exePath) {
    return { ok: false, command, error: `${pmName} was not found on PATH` };
  }

  const spawnOptions: SpawnSyncOptions = {
    cwd: workspaceDir,
    stdio: ['ignore', 'pipe', 'pipe'],
    windowsHide: true,
    encoding: 'utf8',
  };

  const result =
    process.platform === 'win32'
      ? spawnSync('C:\\Windows\\System32\\cmd.exe', ['/d', '/s', '/c', buildWindowsCmdLine(exePath, args)], {
          ...spawnOptions,
          shell: false,
          windowsVerbatimArguments: true,
        })
      : spawnSync(exePath, args, { ...spawnOptions, shell: false });

  if (result.error) {
    const code = (result.error as NodeJS.ErrnoException).code;
    const reason = code === 'ENOENT' ? `${pmName} was not found on PATH` : result.error.message;
    return { ok: false, command, error: reason };
  }

  if (result.status !== 0) {
    const stderr = (result.stderr ?? '').toString().trim();
    const tail = stderr ? stderr.split(/\r?\n/).slice(-3).join(' ') : `exit ${result.status ?? 'null'}`;
    return { ok: false, command, error: tail };
  }

  return { ok: true, command };
}
