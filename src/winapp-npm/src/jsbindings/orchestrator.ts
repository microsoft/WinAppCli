// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Runs after native restore writes the lockfile and package.json opts into JS bindings.
// Returns structured outcomes so callers can decide what to print.

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
  /** Output dir written by codegen when completed. */
  outputDir?: string;
}

export interface OrchestratorOptions {
  workspaceDir: string;
  /** Path native CLI hashed; keeps stale-lockfile checks aligned with --config-dir. */
  yamlPath?: string;
  /** Override for the npm wrapper's pinned dynwinrt version, used in tests. */
  versionOverride?: string;
  /** Sink for codegen progress lines; defaults to console. */
  log?: (line: string) => void;
  /** Forward to codegen-runner; false suppresses per-file noise. */
  verbose?: boolean;
  /** Suppress progress and hints; warnings still go through `log`. */
  quiet?: boolean;
}

export async function runJsBindingsPipeline(options: OrchestratorOptions): Promise<OrchestratorResult> {
  const log = options.log ?? ((line) => console.log(line));
  const workspaceDir = path.resolve(options.workspaceDir);

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

  // If SDK pins changed after restore, codegen would emit against stale winmd inventory.
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

  // User-supplied winmd paths go through UNC/reparse safety checks before codegen sees them.
  const userEmit = resolveAdditionalWinmds(config.additionalWinmds, workspaceDir, 'additionalWinmds');
  const userRefs = resolveAdditionalWinmds(
    (config.additionalRefs ?? []).map((p) => ({ winmdPath: p })),
    workspaceDir,
    'additionalRefs'
  );
  for (const w of [...userEmit.warnings, ...userRefs.warnings]) {
    log(w);
  }

  // Cherry-pick entries load as refs; only listed classes are emitted.
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

  // Built-in NuGet policy has no user overrides; additionalWinmds are explicit overrides.
  const partition = partitionPackageWinmds(lockfile.packages);

  const emitWinmds = [...partition.emit, ...bulkAdditional];
  // Cherry-pick winmds are refs because only their requested classes are emitted.
  const refWinmds = [...partition.refOnly, ...userRefs.resolved.map((r) => r.winmdPath), ...cherryPickRefs];

  if (emitWinmds.length === 0 && cherryPicks.length === 0) {
    return {
      outcome: 'noWinmdsToEmit',
      message:
        'No winmds matched the emit policy and no cherry-pick entries are configured — nothing to generate. ' +
        'Install more NuGet packages in `winapp.yaml`, or add `additionalWinmds` in `package.json` `winapp.jsBindings`.',
    };
  }

  // Spinner covers quiet child output; skip it for verbose output or injected log sinks.
  // Tests inject logs, so avoid interleaving ANSI spinner output with assertions.
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

  // Runtime dep injection is best-effort; codegen output is still useful if it fails.
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
      // Warnings always surface, even in --quiet.
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
