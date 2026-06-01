// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Materializes the `@microsoft/dynwinrt` runtime dependency into node_modules by
// invoking the workspace's package manager. Used by the `init` onboarding flow so
// freshly generated bindings are runnable without a manual second step.
//
// Best-effort by design: codegen has already succeeded and written the dependency
// into package.json, so an install failure (offline, private registry, missing
// package manager) degrades to a warning rather than failing the command.

import { spawnSync } from 'child_process';
import { PackageManagerName, buildAddExactCommand } from './package-manager-detector';

export interface RuntimeInstallResult {
  ok: boolean;
  /** Human-readable command that was attempted, e.g. `npm install pkg@1 --save-exact`. */
  command: string;
  /** Set when ok is false. */
  error?: string;
}

/**
 * Spawn `<pm> add <packageName>@<version>` (exact-pinned) in `workspaceDir`.
 *
 * Runs synchronously so the caller can map the exit code directly. The package
 * manager is launched through a shell: on Windows the binaries are `.cmd` shims,
 * and since the CVE-2024-27980 fix Node refuses to spawn `.cmd`/`.bat` files with
 * `shell: false` (it fails with EINVAL). The full command is passed as a single
 * string (no separate args array) so the shell resolves the shim and we avoid the
 * DEP0190 arg-escaping deprecation warning. The args are constants we control, so
 * there is no injection surface.
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

  const result = spawnSync(command, {
    cwd: workspaceDir,
    stdio: ['ignore', 'pipe', 'pipe'],
    shell: process.platform === 'win32' ? process.env.ComSpec || 'cmd.exe' : true,
    windowsHide: true,
    encoding: 'utf8',
  });

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
