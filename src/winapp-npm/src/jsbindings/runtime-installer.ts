// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Installs the `@microsoft/dynwinrt` runtime dependency into node_modules by
// invoking the workspace's package manager. Used by the `init` onboarding flow so
// freshly generated bindings are runnable without a manual second step.
//
// Best-effort by design: codegen has already succeeded and written the dependency
// into package.json, so an install failure (offline, private registry, missing
// package manager) degrades to a warning rather than failing the command.

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
 * Build the `cmd.exe /d /s /c` command line for launching an absolute-path `.cmd`
 * shim. The ENTIRE command (quoted-exe + args) is wrapped in an extra outer pair
 * of quotes: with `/s`, cmd.exe strips the first and last quote of the string,
 * leaving the inner `"C:\...\npm.cmd" <args>` intact so a path containing spaces
 * still resolves. This mirrors the cross-spawn / @npmcli/promise-spawn pattern.
 */
/** Visible for unit tests. */
export function buildWindowsCmdLine(exePath: string, args: string[]): string {
  const inner = `"${exePath}" ${args.map(quoteForCmd).join(' ')}`;
  return `"${inner}"`;
}

/**
 * Quote a single argument for a `cmd.exe /c "<cmdline>"` string. Our args are
 * controlled constants (`install`, `<name>@<version>`, `--save-exact`), so this
 * only needs to guard the rare case of a version/name containing whitespace or
 * cmd metacharacters — it is not a general-purpose shell escaper.
 */
function quoteForCmd(arg: string): string {
  if (!/[\s"^&|<>()%!]/.test(arg)) {
    return arg;
  }
  // Double embedded quotes per cmd.exe rules, then wrap the whole thing.
  return `"${arg.replace(/"/g, '""')}"`;
}

/**
 * Install `<pm> add <packageName>@<version>` (exact-pinned) into `workspaceDir`.
 *
 * Runs synchronously so the caller can map the exit code directly. SECURITY: we
 * resolve the package manager to an ABSOLUTE path from `PATH` first
 * (`resolvePackageManagerPath`), never spawning a bare command name. On Windows
 * the launchers are `.cmd` shims; spawning `cmd.exe` with the absolute `.cmd`
 * path (rather than `{ shell: true }` + a bare name) is the pattern recommended
 * by the Node docs post-CVE-2024-27980 — it avoids the EINVAL that `.cmd` +
 * `shell: false` would raise, and because the path is absolute, `cmd.exe` does
 * NOT perform its current-directory-first lookup, so a malicious `npm.cmd` in
 * `workspaceDir` cannot hijack execution.
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
