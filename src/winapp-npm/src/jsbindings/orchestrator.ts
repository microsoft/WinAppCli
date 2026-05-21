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
import { readJsBindingsConfig, JsBindingsConfig } from './package-json-config';
import { tryReadLockfile } from './lockfile-reader';
import { partitionByPackageCategory } from './winmd-policy';
import { resolveAdditionalWinmds } from './additional-winmds';
import { runCodegen } from './codegen-runner';
import { ensureRuntimeDependency, formatRuntimeDependencyHint, getDynWinrtVersionPin } from './runtime-dep-injector';
import { detectPackageManager } from './package-manager-detector';
import { startSpinner, Spinner } from './spinner';

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
  /** Override for the npm wrapper's pinned dynwinrt version (used in tests). */
  versionOverride?: string;
  /** Sink for per-line progress (stdout/stderr from codegen). Defaults to console. */
  log?: (line: string) => void;
  /** Forward to codegen-runner. False (default) suppresses per-file noise. */
  verbose?: boolean;
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

  // 2. Read lockfile.
  const winappDir = path.join(workspaceDir, '.winapp');
  const lockResult = tryReadLockfile(winappDir);
  if (!lockResult.lockfile) {
    return {
      outcome: lockResult.reason?.includes('schema mismatch') ? 'lockfileStale' : 'lockfileMissing',
      message:
        lockResult.reason ?? `No ${path.join(winappDir, 'winmds.lock.json')} found. Run \`winapp restore\` first.`,
    };
  }
  const lockfile = lockResult.lockfile;

  // 3. Resolve user-supplied additional winmds (each independently).
  const userEmit = resolveAdditionalWinmds(config.additionalWinmds, workspaceDir, 'additionalWinmds');
  const userRefs = resolveAdditionalWinmds(config.additionalRefs, workspaceDir, 'additionalRefs');
  for (const w of [...userEmit.warnings, ...userRefs.warnings]) {
    log(w);
  }

  // 4. Partition NuGet winmds by category. Per-package overrides from config.
  const flatWinmds: string[] = [];
  for (const pkg of lockfile.packages) {
    for (const w of pkg.winmds) {
      flatWinmds.push(w);
    }
  }
  const partition = partitionByPackageCategory(flatWinmds, {
    overrides: {
      skip: config.skipPackages,
      refOnly: config.refOnlyPackages,
      emit: config.emitPackages,
    },
    nugetCacheRoot: lockfile.nugetCacheDir,
    emitScope: config.packages.length > 0 ? config.packages : undefined,
  });

  // 5. Compose final emit + ref sets.
  const emitWinmds = [...partition.emit, ...userEmit.resolved];
  const refWinmds = [...partition.refOnly, ...userRefs.resolved];

  if (emitWinmds.length === 0 && countValidExtraTypes(config) === 0) {
    return {
      outcome: 'noWinmdsToEmit',
      message:
        'No winmds matched the emit policy and no extraTypes are configured — nothing to generate. ' +
        'Add packages: entries (or wider scope) or extraTypes: in package.json `winapp.jsBindings`.',
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
  const useSpinner = !options.log && !options.verbose;
  let spinner: Spinner | null = null;
  if (useSpinner) {
    spinner = startSpinner(progressText);
  } else {
    log(`🔨 ${progressText}`);
  }

  let codegenResult;
  try {
    codegenResult = await runCodegen({
      config,
      emitWinmds,
      refWinmds,
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
      log(hint.message);
    } catch (err) {
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

function countValidExtraTypes(config: JsBindingsConfig): number {
  let count = 0;
  for (const et of config.extraTypes) {
    if (et.namespace && et.namespace.trim() && et.classes && et.classes.length > 0) {
      count++;
    }
  }
  return count;
}

function safeGetVersionPin(log: (line: string) => void): string | null {
  try {
    return getDynWinrtVersionPin();
  } catch (err) {
    log(`⚠️ Could not resolve pinned ${RUNTIME_PACKAGE_NAME} version: ${(err as Error).message}`);
    return null;
  }
}
