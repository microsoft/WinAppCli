// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Orchestrates package config, lockfile policy, codegen, and runtime dependency hints.

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
  updateRuntimeDependency,
} from './runtime-dep-injector';
import { detectPackageManager } from './package-manager-detector';
import { installPackageDependency } from './runtime-installer';
import { startSpinner, Spinner } from './spinner';
import { GroupedSpinner, GroupedChildSpinner } from './grouped-spinner';
import { computeYamlPackagesHash, readWinappYamlPackages } from './yaml-packages-hash';

export const RUNTIME_PACKAGE_NAME = '@microsoft/dynwinrt';

export type OrchestratorOutcome = 'noJsBindings' | 'completed' | 'lockfileMissing' | 'lockfileStale' | 'noWinmdsToEmit';

export interface OrchestratorResult {
  outcome: OrchestratorOutcome;
  /** Human-readable diagnostic. Always set. */
  message: string;
  /** Output dir written by codegen when completed. */
  outputDir?: string;
  /** True when an in-place spinner has already shown the success line; wrappers should skip the duplicate. */
  uiAlreadyAnnounced?: boolean;
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
  /** Indent prepended to every spinner line emitted by this pipeline. */
  progressPrefix?: string;
  /** When set, per-step progress renders as children of this grouped spinner. */
  group?: GroupedSpinner;
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

  const progressText =
    `Generating JS bindings from ${emitWinmds.length} winmd${emitWinmds.length === 1 ? '' : 's'}` +
    (refWinmds.length > 0 ? ` (+${refWinmds.length} ref)` : '') +
    `...`;
  const useSpinner = !options.verbose && !options.quiet;
  let spinner: Spinner | GroupedChildSpinner | null = null;
  if (useSpinner) {
    spinner = options.group
      ? options.group.addChild(progressText)
      : startSpinner(progressText, {
          prefix: options.progressPrefix ?? '',
          nonTtyLog: log,
        });
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
  } catch (err) {
    spinner?.fail(`Generating JS bindings failed: ${(err as Error).message}`);
    throw err;
  }
  const completionMessage = formatCompletedMessage(codegenResult.outputDir);
  spinner?.succeed(completionMessage);

  // Runtime dep policy: init declares/optionally installs (best-effort); passive
  // restore/generate-bindings never mutate package.json and only warn if missing.
  if (options.manageRuntimeDep) {
    const pinnedVersion = options.versionOverride ?? (await safeGetRuntimeVersion(workspaceDir, log));
    if (pinnedVersion) {
      try {
        let ensureResult = ensureRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, pinnedVersion);

        // ABI lockstep: auto-update on mismatch. When an install will follow
        // immediately, defer the UI and merge it with the install spinner.
        let bumpedFrom: string | null = null;
        if (ensureResult.outcome === 'versionMismatch') {
          const existing = ensureResult.existingVersion ?? '';
          const willInstall = options.installRuntimeDep;
          if (willInstall) {
            try {
              updateRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, pinnedVersion);
            } catch (err) {
              log(`⚠️ Failed to update ${RUNTIME_PACKAGE_NAME} version pin: ${(err as Error).message}`);
              throw err;
            }
            bumpedFrom = existing;
          } else {
            const bumpProgress = `Bumping ${RUNTIME_PACKAGE_NAME} ${existing} → ${pinnedVersion} (ABI lockstep)...`;
            const bumpSpinner: Spinner | GroupedChildSpinner | null = useSpinner
              ? options.group
                ? options.group.addChild(bumpProgress)
                : startSpinner(bumpProgress, {
                    prefix: options.progressPrefix ?? '',
                    nonTtyLog: log,
                  })
              : null;
            try {
              updateRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, pinnedVersion);
            } catch (err) {
              bumpSpinner?.fail(`Bumping ${RUNTIME_PACKAGE_NAME} failed: ${(err as Error).message}`);
              throw err;
            }
            const bumpMessage =
              `Bumped ${RUNTIME_PACKAGE_NAME} ${existing} → ${pinnedVersion} ` +
              'to match the installed @microsoft/dynwinrt-codegen (ABI lockstep).';
            if (bumpSpinner) {
              bumpSpinner.succeed(bumpMessage);
            } else if (!options.quiet) {
              log(`⬆️  ${bumpMessage}`);
            }
          }
          ensureResult = { outcome: 'added', pinnedVersion };
        }

        const pm = detectPackageManager(workspaceDir);

        // Install only when the dep is in `dependencies`; noPackageJson/dev-only fall through to hints.
        const dependencyDeclared = ensureResult.outcome === 'added' || ensureResult.outcome === 'alreadyPresent';
        if (options.installRuntimeDep && dependencyDeclared) {
          const installVersion = ensureResult.pinnedVersion ?? pinnedVersion;
          const installProgress = bumpedFrom
            ? `Updating ${RUNTIME_PACKAGE_NAME} ${bumpedFrom} → ${installVersion} with ${pm.name} (ABI lockstep)...`
            : `Installing ${RUNTIME_PACKAGE_NAME}@${installVersion} with ${pm.name}...`;
          const installSpinner: Spinner | GroupedChildSpinner | null = useSpinner
            ? options.group
              ? options.group.addChild(installProgress)
              : startSpinner(installProgress, {
                  prefix: options.progressPrefix ?? '',
                  nonTtyLog: log,
                })
            : null;
          if (!installSpinner && !options.quiet) {
            log(`📦 ${installProgress}`);
          }
          const install = installPackageDependency(workspaceDir, RUNTIME_PACKAGE_NAME, installVersion, pm.name);
          if (install.ok) {
            const installedMessage = bumpedFrom
              ? `Updated ${RUNTIME_PACKAGE_NAME} ${bumpedFrom} → ${installVersion} with ${pm.name} (ABI lockstep).`
              : `Installed ${RUNTIME_PACKAGE_NAME}@${installVersion}.`;
            if (installSpinner) {
              installSpinner.succeed(installedMessage);
            } else if (!options.quiet) {
              log(`✅ ${installedMessage}`);
            }
          } else {
            // Best-effort: warn so users can install manually.
            const failureMessage = bumpedFrom
              ? `Updated package.json to ${RUNTIME_PACKAGE_NAME}@${installVersion} (ABI lockstep) ` +
                `but could not auto-install: ${install.error}. ` +
                `Run \`${pm.installCommand}\` to install it locally.`
              : `Could not auto-install ${RUNTIME_PACKAGE_NAME}@${installVersion}: ${install.error}. ` +
                `Run \`${pm.installCommand}\` to install it locally.`;
            if (installSpinner) {
              installSpinner.fail(failureMessage);
            } else {
              log(`⚠️ ${failureMessage}`);
            }
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
    uiAlreadyAnnounced: useSpinner,
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
