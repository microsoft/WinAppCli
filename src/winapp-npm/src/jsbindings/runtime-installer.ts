// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Installs `@microsoft/dynwinrt` during init so generated bindings run immediately.
// Best-effort: codegen and package.json already succeeded, so install failures warn.

import { spawnSync, SpawnSyncOptions } from 'child_process';
import { PackageManagerName, buildAddExactCommand, resolvePackageManagerPath } from './package-manager-detector';

export interface RuntimeInstallResult {
  ok: boolean;
  /** Human-readable command that was attempted, e.g. `npm install pkg@1 --save-exact`. */
  command: string;
  /** Set when ok is false. */
  error?: string;
}

/**
 * Build `cmd.exe /d /s /c` for an absolute `.cmd` shim.
 * Outer quotes survive `/s` stripping so spaced paths still resolve (cross-spawn pattern).
 */
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
 * Install exact-pinned runtime into `workspaceDir`, synchronously for exit-code mapping.
 * Security: resolve an absolute PATH-only launcher; on Windows, run `.cmd` via cmd.exe
 * per post-CVE-2024-27980 guidance, avoiding EINVAL and CWD-first hijack.
 */
export function installRuntimeDependency(
  workspaceDir: string,
  packageName: string,
  version: string,
  pmName: PackageManagerName
): RuntimeInstallResult {
  const spec = `${packageName}@${version}`;
  const { exe, args } = buildAddExactCommand(pmName, spec);
  const command = `${exe} ${args.join(' ')}`;

  const exePath = resolvePackageManagerPath(pmName);
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
      ? spawnSync('cmd.exe', ['/d', '/s', '/c', buildWindowsCmdLine(exePath, args)], {
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
