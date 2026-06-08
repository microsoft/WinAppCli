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
import { getCodegenRuntimeDependency, runCodegen } from './codegen-runner';
import {
  ensureRuntimeDependency,
  formatRuntimeDependencyHint,
  getRuntimeDependencyVersion,
} from './runtime-dep-injector';
import { detectPackageManager } from './package-manager-detector';
import { installRuntimeDependency } from './runtime-installer';
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
  /** Override for the codegen-declared dynwinrt version, used in tests. */
  versionOverride?: string;
  /** Sink for codegen progress lines; defaults to console. */
  log?: (line: string) => void;
  /** Forward to codegen-runner; false suppresses per-file noise. */
  verbose?: boolean;
  /** Suppress progress and hints; warnings still go through `log`. */
  quiet?: boolean;
  /**
   * Init-only: write `@microsoft/dynwinrt` to package.json.
   * Passive flows never mutate package.json; they only warn when the dep is missing.
   */
  manageRuntimeDep?: boolean;
  /** Init-only: install the runtime dep into node_modules; failures warn. */
  installRuntimeDep?: boolean;
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
    if (!currentPackages) {
      return {
        outcome: 'lockfileStale',
        message:
          'Lockfile records a `winapp.yaml` package hash but `winapp.yaml` could not be read. ' +
          'Restore the file (or remove `.winapp/winmds.lock.json` if intentional) before regenerating bindings.',
      };
    }
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
  const cherryPicks: { winmdPath?: string; namespace: string; classes: string[] }[] = [];
  for (const entry of userEmit.resolved) {
    if (entry.namespace && entry.classes && entry.classes.length > 0) {
      cherryPicks.push({
        winmdPath: entry.winmdPath,
        namespace: entry.namespace,
        classes: entry.classes,
      });
    } else if (entry.winmdPath) {
      bulkAdditional.push(entry.winmdPath);
    }
  }

  // Built-in NuGet policy has no user overrides; additionalWinmds are explicit overrides.
  const partition = partitionPackageWinmds(lockfile.packages);

  const emitWinmds = [...partition.emit, ...bulkAdditional];
  // Include every cherry-pick winmd (when path is given) in --ref so each pass
  // can resolve types declared in OTHER cherry-pick winmds.
  const cherryPickRefs = cherryPicks.map((cp) => cp.winmdPath).filter((p): p is string => !!p);
  const refWinmds = [
    ...partition.refOnly,
    ...userRefs.resolved.map((r) => r.winmdPath).filter((p): p is string => !!p),
    ...cherryPickRefs,
  ];

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

  // Runtime dep policy: init declares/optionally installs (best-effort); passive
  // restore/generate-bindings never mutate package.json and only warn if missing.
  if (options.manageRuntimeDep) {
    const pinnedVersion = options.versionOverride ?? (await safeGetRuntimeVersion(workspaceDir, log));
    if (pinnedVersion) {
      try {
        const ensureResult = ensureRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, pinnedVersion);
        const pm = detectPackageManager(workspaceDir);

        // Install only when the dep is in `dependencies`; noPackageJson/dev-only fall through to hints.
        const dependencyDeclared = ensureResult.outcome === 'added' || ensureResult.outcome === 'alreadyPresent';
        if (options.installRuntimeDep && dependencyDeclared) {
          const installVersion = ensureResult.pinnedVersion ?? pinnedVersion;
          if (!options.quiet) {
            log(`📦 Installing ${RUNTIME_PACKAGE_NAME}@${installVersion} with ${pm.name}...`);
          }
          const install = installRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, installVersion, pm.name);
          if (install.ok) {
            if (!options.quiet) {
              log(`✅ Installed ${RUNTIME_PACKAGE_NAME}@${installVersion}.`);
            }
          } else {
            // Best-effort: generated bindings remain useful; warn so users can install manually.
            log(
              `⚠️ Could not auto-install ${RUNTIME_PACKAGE_NAME}@${installVersion}: ${install.error}. ` +
                `Run \`${pm.installCommand}\` to install it locally.`
            );
          }
        } else {
          const hint = formatRuntimeDependencyHint(
            ensureResult.outcome,
            RUNTIME_PACKAGE_NAME,
            ensureResult.pinnedVersion,
            pm.installCommand
          );
          if (!options.quiet) {
            log(hint.message);
          }
        }
      } catch (err) {
        // Warnings always surface, even in --quiet.
        log(`⚠️ Failed to ensure runtime dependency: ${(err as Error).message}`);
      }
    }
  } else {
    // Passive flow: warn if the generated runtime import would be unresolved or stale; init owns writes.
    const declaredVersion = getRuntimeDependencyVersion(workspaceDir, RUNTIME_PACKAGE_NAME);
    if (!declaredVersion) {
      log(
        `⚠️ ${RUNTIME_PACKAGE_NAME} is not declared in package.json dependencies. ` +
          'Generated bindings import it at runtime — run `winapp init` to add it (or add it manually).'
      );
    } else {
      const expectedVersion = options.versionOverride ?? (await safeGetRuntimeVersion(workspaceDir, log));
      if (expectedVersion && declaredVersion !== expectedVersion) {
        log(
          `⚠️ ${RUNTIME_PACKAGE_NAME} is declared as ${declaredVersion}, ` +
            `but dynwinrt-codegen declares ${expectedVersion}. ` +
            'Run `winapp init` to update it (or update it manually).'
        );
      }
    }
  }

  return {
    outcome: 'completed',
    message: formatCompletedMessage(codegenResult.outputDir),
    outputDir: codegenResult.outputDir,
  };
}

function formatCompletedMessage(outputDir: string): string {
  return `Generated JS bindings → ${outputDir}`;
}

async function safeGetRuntimeVersion(workspaceDir: string, log: (line: string) => void): Promise<string | null> {
  try {
    return (await getCodegenRuntimeDependency(workspaceDir)).version;
  } catch (err) {
    log(`⚠️ Could not resolve ${RUNTIME_PACKAGE_NAME} version from dynwinrt-codegen: ${(err as Error).message}`);
    return null;
  }
}
