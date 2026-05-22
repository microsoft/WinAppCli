// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Top-level glue for JS bindings generation. Called after native `winapp restore`
// has written the winmd lockfile and after we've established that the user
// wants JS bindings (`winapp.jsBindings` namespace present in package.json).
//
// Pipeline:
//   1. Read package.json → get jsBindings config (npm wrapper-owned).
//   2. Read .winapp/winmds.lock.json → get NuGet winmd inventory.
//   3. Resolve user-supplied additional winmds + refs (reparse / UNC safety).
//   4. Partition by package category (skip / refOnly / emit) with user overrides.
//   5. Run dynwinrt-codegen (bulk + per-extraType passes) into staged dir.
//   6. Ensure @microsoft/dynwinrt is in package.json dependencies + print PM hint.
//
// Returns a structured outcome (not exceptions for "no jsBindings configured")
// so the cli.ts caller can decide whether to print anything.

import * as path from 'path';
import { readJsBindingsConfig } from './package-json-config';
import { tryReadLockfile } from './lockfile-reader';
import { partitionPackageWinmds } from './winmd-policy';
import { resolveAdditionalWinmds } from './additional-winmds';
import { runCodegen } from './codegen-runner';
import { ensureRuntimeDependency, formatRuntimeDependencyHint, getDynWinrtVersionPin } from './runtime-dep-injector';
import { detectPackageManager } from './package-manager-detector';
import { startSpinner, Spinner } from './spinner';
import { computeYamlPackagesHash, readWinappYamlPackages } from './yaml-packages-hash';

export const RUNTIME_PACKAGE_NAME = '@microsoft/dynwinrt';

export type OrchestratorOutcome = 'noJsBindings' | 'completed' | 'lockfileMissing' | 'lockfileStale' | 'noWinmdsToEmit';

export interface OrchestratorResult {
  outcome: OrchestratorOutcome;
  /** Human-readable diagnostic. Always set. */
  message: string;
  /** Output dir written by codegen (only when outcome === 'completed'). */
  outputDir?: string;
}

export interface OrchestratorOptions {
  workspaceDir: string;
  /**
   * Explicit `winapp.yaml` path the native CLI used (resolved from `--config-dir`
   * by the caller via {@link resolveYamlPath}). Defaults to
   * `<workspaceDir>/winapp.yaml` for backward-compat; pass it explicitly
   * whenever the user supplied `--config-dir` so the staleness check
   * compares against the same file native hashed into the lockfile.
   */
  yamlPath?: string;
  /** Override for the npm wrapper's pinned dynwinrt version (used in tests). */
  versionOverride?: string;
  /** Sink for per-line progress (stdout/stderr from codegen). Defaults to console. */
  log?: (line: string) => void;
  /** Forward to codegen-runner. False (default) suppresses per-file noise. */
  verbose?: boolean;
  /**
   * Suppress all non-essential progress / hint output. Errors and warnings
   * still go through `log`; the spinner, `🔨` fallback, and runtime-dep hint
   * are skipped. Used by `--quiet` on the wrapper.
   */
  quiet?: boolean;
}

export async function runJsBindingsPipeline(options: OrchestratorOptions): Promise<OrchestratorResult> {
  const log = options.log ?? ((line) => console.log(line));
  const workspaceDir = path.resolve(options.workspaceDir);

  // 1. Read package.json for the `winapp.jsBindings` namespace.
  const pkgResult = readJsBindingsConfig(workspaceDir);
  if (!pkgResult.packageJsonExists) {
    return {
      outcome: 'noJsBindings',
      message: `No package.json found in ${workspaceDir} — skipping JS bindings.`,
    };
  }
  if (!pkgResult.jsBindings) {
    return {
      outcome: 'noJsBindings',
      message: 'No "winapp.jsBindings" namespace in package.json — skipping JS bindings.',
    };
  }
  const config = pkgResult.jsBindings;

  // 2. Read lockfile (workspace-scoped: lives at <workspace>/.winapp/winmds.lock.json).
  const lockResult = tryReadLockfile(workspaceDir);
  if (!lockResult.lockfile) {
    return {
      outcome: lockResult.reason?.includes('schema mismatch') ? 'lockfileStale' : 'lockfileMissing',
      message:
        lockResult.reason ??
        `No ${path.join(workspaceDir, '.winapp', 'winmds.lock.json')} found. ` +
          'This file is written by `winapp restore`. Run `winapp restore` once ' +
          '(or re-run it after upgrading from an older winapp version) to build the ' +
          'winmd inventory, then retry.',
    };
  }
  const lockfile = lockResult.lockfile;

  // 2a. Compare the lockfile's recorded `yaml_packages_hash` against a fresh
  //     hash of `winapp.yaml`. If the user edited the SDK pins without
  //     re-running `winapp restore`, the lockfile's winmd inventory is for
  //     the OLD packages — emitting JS bindings now would generate against
  //     stale types. Surface as `lockfileStale` so the cli.ts caller prints
  //     the actionable `winapp restore` hint.
  if (lockfile.yamlPackagesHash) {
    const currentPackages = readWinappYamlPackages(workspaceDir, options.yamlPath);
    if (currentPackages) {
      const currentHash = computeYamlPackagesHash(currentPackages);
      if (currentHash !== lockfile.yamlPackagesHash) {
        return {
          outcome: 'lockfileStale',
          message:
            `winapp.yaml \`packages:\` has changed since the last \`winapp restore\` ` +
            `(lockfile hash ${lockfile.yamlPackagesHash.slice(0, 12)}…, current ${currentHash.slice(0, 12)}…). ` +
            'Run `winapp restore` to refresh the winmd inventory before generating bindings.',
        };
      }
    }
  }

  // 3. Resolve user-supplied additional winmds + refs (path safety + dedupe).
  //    `additionalWinmds` entries can be bulk (winmdPath only) or cherry-pick
  //    (winmdPath + namespace + classes); we split them after resolution.
  const userEmit = resolveAdditionalWinmds(config.additionalWinmds, workspaceDir, 'additionalWinmds');
  const userRefs = resolveAdditionalWinmds(
    (config.additionalRefs ?? []).map((p) => ({ winmdPath: p })),
    workspaceDir,
    'additionalRefs'
  );
  for (const w of [...userEmit.warnings, ...userRefs.warnings]) {
    log(w);
  }

  // Split resolved additionalWinmds into bulk emit vs cherry-pick passes.
  // Cherry-pick entries are loaded as ref-only so codegen can resolve types;
  // only the listed classes are emitted.
  const bulkAdditional: string[] = [];
  const cherryPicks: { namespace: string; classes: string[] }[] = [];
  const cherryPickRefs: string[] = [];
  for (const entry of userEmit.resolved) {
    if (entry.namespace && entry.classes && entry.classes.length > 0) {
      cherryPicks.push({ namespace: entry.namespace, classes: entry.classes });
      cherryPickRefs.push(entry.winmdPath);
    } else {
      bulkAdditional.push(entry.winmdPath);
    }
  }

  // 4. Partition NuGet winmds by built-in package category (no user overrides).
  const partition = partitionPackageWinmds(lockfile.packages);

  // 5. Compose final emit + ref sets.
  const emitWinmds = [...partition.emit, ...bulkAdditional];
  const refWinmds = [...partition.refOnly, ...userRefs.resolved.map((r) => r.winmdPath), ...cherryPickRefs];

  if (emitWinmds.length === 0 && cherryPicks.length === 0) {
    return {
      outcome: 'noWinmdsToEmit',
      message:
        'No winmds matched the emit policy and no cherry-pick entries are configured — nothing to generate. ' +
        'Install more NuGet packages in `winapp.yaml`, or add `additionalWinmds` in `package.json` `winapp.jsBindings`.',
    };
  }

  // 6. Run codegen. Show a TTY spinner so the user sees progress during the
  //    ~30s where codegen-runner suppresses all child output (quiet mode).
  //    Spinner is suppressed in verbose mode (where codegen prints its own
  //    line-by-line output) and when the caller injected a custom log sink
  //    (e.g., tests — we mustn't interleave ANSI noise with assertion output).
  const progressText =
    `Generating JS bindings from ${emitWinmds.length} winmd${emitWinmds.length === 1 ? '' : 's'}` +
    (refWinmds.length > 0 ? ` (+${refWinmds.length} ref)` : '') +
    `...`;
  const useSpinner = !options.log && !options.verbose && !options.quiet;
  let spinner: Spinner | null = null;
  if (useSpinner) {
    spinner = startSpinner(progressText);
  } else if (!options.quiet) {
    log(`🔨 ${progressText}`);
  }

  let codegenResult;
  try {
    codegenResult = await runCodegen({
      config,
      emitWinmds,
      refWinmds,
      cherryPicks,
      workspaceDir,
      log,
      verbose: options.verbose,
    });
  } finally {
    spinner?.stop();
  }

  // 7. Ensure runtime dep + print PM hint.
  const pinnedVersion = options.versionOverride ?? safeGetVersionPin(log);
  if (pinnedVersion) {
    try {
      const ensureResult = ensureRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, pinnedVersion);
      const pm = detectPackageManager(workspaceDir);
      const hint = formatRuntimeDependencyHint(
        ensureResult.outcome,
        RUNTIME_PACKAGE_NAME,
        ensureResult.pinnedVersion,
        pm.installCommand
      );
      if (!options.quiet) {
        log(hint.message);
      }
    } catch (err) {
      // Warnings always surface, even in --quiet, so users still see real failures.
      log(`⚠️ Failed to ensure runtime dependency: ${(err as Error).message}`);
    }
  }

  return {
    outcome: 'completed',
    message: formatCompletedMessage(codegenResult.outputDir, codegenResult.summary),
    outputDir: codegenResult.outputDir,
  };
}

function formatCompletedMessage(
  outputDir: string,
  summary: { classes: number; interfaces: number; enums: number }
): string {
  const hasCounts = summary.classes > 0 || summary.interfaces > 0 || summary.enums > 0;
  if (!hasCounts) {
    return `Generated JS bindings → ${outputDir}`;
  }
  const parts: string[] = [];
  if (summary.classes > 0) parts.push(`${summary.classes} class${summary.classes === 1 ? '' : 'es'}`);
  if (summary.interfaces > 0) parts.push(`${summary.interfaces} interface${summary.interfaces === 1 ? '' : 's'}`);
  if (summary.enums > 0) parts.push(`${summary.enums} enum${summary.enums === 1 ? '' : 's'}`);
  return `Generated JS bindings → ${outputDir} (${parts.join(', ')})`;
}

function safeGetVersionPin(log: (line: string) => void): string | null {
  try {
    return getDynWinrtVersionPin();
  } catch (err) {
    log(`⚠️ Could not resolve pinned ${RUNTIME_PACKAGE_NAME} version: ${(err as Error).message}`);
    return null;
  }
}
