// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// JS-binding hooks layered around native init/restore.

import * as fs from 'fs';
import * as path from 'path';

import { CLI_NAME, parseArgs, logErrorAndExit } from '../cli-shared';
import { callWinappCli } from '../winapp-cli-utils';
import { getCodegenRuntimeDependency } from './codegen-runner';
import { askBindingsKind } from './init-prompt';
import { hasJsBindings, ensureJsBindingsBlock } from './package-json-config';
import { packageJsonExists } from './package-json-doc';
import { RUNTIME_PACKAGE_NAME, runJsBindingsPipeline } from './orchestrator';
import { lockfileExists } from './lockfile-reader';
import { readWinappYamlPackages } from './yaml-packages-hash';
import { detectPackageManager } from './package-manager-detector';
import { ensureRuntimeDependency, formatRuntimeDependencyHint } from './runtime-dep-injector';
import {
  resolveWorkspaceDir,
  resolveYamlPath,
  isVerbose,
  isQuiet,
  hasConfigOnly,
  hasAddJsBindings,
  hasUseDefaults,
  stripWrapperOnlyFlags,
  firstPositional,
} from '../cli-args';
import { assertSafeWorkspaceFile } from './path-safety';
import { evaluateGenerateBindingsPreflight } from './generate-bindings-preflight';

/** Passive `node generate-bindings`: read package config + lockfile, then run codegen. */
export async function handleGenerateBindings(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    verbose: false,
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node generate-bindings [options]`);
    console.log('');
    console.log('Regenerate JS/TypeScript bindings from package.json + cached winmds');
    console.log('');
    console.log('This command will:');
    console.log('  1. Read the `winapp.jsBindings` block from package.json');
    console.log('  2. Read the cached winmd inventory from .winapp/winmds.lock.json');
    console.log('  3. Run dynwinrt-codegen into the output directory');
    console.log('');
    console.log('It only reads `winapp.jsBindings` + the cached lockfile and emits bindings —');
    console.log('it does NOT modify package.json. Run `winapp init` to opt into JS bindings');
    console.log('(it adds the `winapp.jsBindings` block and the @microsoft/dynwinrt dependency).');
    console.log('It also does NOT re-run the native restore. If you have never run');
    console.log('`winapp restore` in this workspace (so there is no winmd lockfile yet)');
    console.log('or you changed `winapp.yaml` since the last restore, run `winapp restore`');
    console.log('first, then re-run this command.');
    console.log('');
    console.log('Options:');
    console.log('  --verbose             Enable verbose codegen output (default: false)');
    console.log('  --quiet, -q           Suppress progress and informational output');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node generate-bindings`);
    console.log(`  ${CLI_NAME} node generate-bindings --verbose`);
    return;
  }

  const workspaceDir = resolveWorkspaceDir(args);
  const quiet = isQuiet(args);

  // Preflight: package.json + `winapp.jsBindings` + cached restore lockfile.
  // (Schema mismatches surface later as `lockfileStale`.)
  const preflight = evaluateGenerateBindingsPreflight(workspaceDir);
  if (preflight.kind !== 'ok') {
    for (const line of preflight.messageLines) {
      console.error(line);
    }
    process.exit(1);
  }

  // Hand off to the shared pipeline (outcomes → ✅ / ❌ / ⚠️).
  await runJsBindingsOrchestrator(workspaceDir, isVerbose(args), quiet, resolveYamlPath(args, workspaceDir));
}

/** Append the wrapper-specific `init` notes the native `--help` doesn't know about. */
export function printInitWrapperOnlyHelp(): void {
  console.log('');
  console.log(`Options (added by the ${CLI_NAME} npm wrapper):`);
  console.log('  --add-js-bindings    Add winapp.jsBindings and generate JS/TypeScript bindings');
  console.log('                       (useful with --use-defaults or non-interactive init)');
}

/** `init` hook: run native init, then optionally add and generate JS bindings. */
export async function handleInit(args: string[]): Promise<void> {
  const workspaceDir = resolveWorkspaceDir(args);
  const quiet = isQuiet(args);
  const configOnly = hasConfigOnly(args);
  const addJsBindings = hasAddJsBindings(args);
  const explicitWorkspace = firstPositional(args) !== undefined;
  const useDefaults = hasUseDefaults(args);
  const packageJsonExistedBeforeInit = packageJsonExists(workspaceDir);

  // Re-running on a configured workspace: infer the choice, don't re-prompt.
  const existingJsBindings = hasJsBindings(workspaceDir);

  // Native init runs first (its prompts finish, and we can gate on SDK setup).
  // Don't change child cwd: native resolves paths against its own cwd.
  await callWinappCli(['init', ...stripWrapperOnlyFlags(args)], { exitOnError: true });

  if (!explicitWorkspace && !useDefaults) {
    if (!quiet) {
      console.log(
        'ℹ️  JS bindings setup skipped because init may have selected a project directory. ' +
          'Run `npx winapp restore` from that project to generate bindings.'
      );
    }
    return;
  }

  if (!explicitWorkspace && !packageJsonExistedBeforeInit && !packageJsonExists(workspaceDir)) {
    if (!quiet) {
      console.log(
        'ℹ️  JS bindings setup skipped because init may have selected a project directory. ' +
          'Run `npx winapp restore` from that project to generate bindings.'
      );
    }
    return;
  }

  // Lockfile (from SDK winmd discovery) = winmds to bind against; --config-only defers.
  const lockfilePresent = lockfileExists(workspaceDir);

  let outcome;
  try {
    outcome = await askBindingsKind({
      workspaceDir,
      argv: args,
      isInit: true,
      existingJsBindings,
      sdksReady: lockfilePresent || configOnly,
      addJsBindings,
    });
  } catch (err) {
    logErrorAndExit(err);
  }

  if (outcome.silentReason && !quiet) {
    console.log(`ℹ️  ${outcome.silentReason}`);
  }

  if (outcome.kind === 'no') {
    return;
  }

  // Persist default block for later restore/generate-bindings; skip+hint if no package.json.
  try {
    const pkgJsonPath = path.join(workspaceDir, 'package.json');
    assertSafeWorkspaceFile(workspaceDir, pkgJsonPath, 'package.json');
    if (!fs.existsSync(pkgJsonPath)) {
      if (!quiet) {
        console.warn(
          '⚠️  package.json not found in this workspace. ' +
            'Run `npm init -y` (or equivalent) and then `npx winapp node generate-bindings` to enable JS bindings.'
        );
      }
      return;
    }
    ensureJsBindingsBlock(workspaceDir, {
      reset: outcome.overwriteExistingConfig === true,
      quiet,
    });
  } catch (err) {
    console.error(`Failed to update package.json: ${(err as Error).message}`);
    process.exit(1);
  }

  // --config-only wrote no lockfile; codegen would fail. Defer to a later restore.
  if (configOnly) {
    await ensureRuntimeDependencyForInit(workspaceDir, quiet);
    if (!quiet) {
      console.log(
        'ℹ️  --config-only requested; JS bindings codegen deferred. ' +
          'Run `npx winapp restore` (or `npx winapp node generate-bindings` after a restore) to generate.'
      );
    }
    return;
  }

  // Lockfile already written by native init → straight to orchestrator.
  // Guard: a re-run may infer Yes from old config though SDK setup was skipped (no lockfile).
  if (!lockfilePresent) {
    if (!quiet) {
      console.log(
        'ℹ️  Windows SDKs were not set up, so JS bindings were not generated. ' +
          'Run `npx winapp restore` then `npx winapp node generate-bindings` to generate them.'
      );
    }
    return;
  }

  // Onboarding flow: writes and installs the runtime dependency.
  // Resolve yaml against workspaceDir (native remaps --config-dir) to avoid false stale-lockfile.
  await runJsBindingsOrchestrator(
    workspaceDir,
    isVerbose(args),
    quiet,
    resolveYamlPath(args, workspaceDir),
    true,
    true
  );
}

async function ensureRuntimeDependencyForInit(workspaceDir: string, quiet: boolean): Promise<void> {
  try {
    const { version } = await getCodegenRuntimeDependency(workspaceDir);
    const result = ensureRuntimeDependency(workspaceDir, RUNTIME_PACKAGE_NAME, version);
    if (!quiet) {
      const pm = detectPackageManager(workspaceDir);
      const hint = formatRuntimeDependencyHint(result.outcome, RUNTIME_PACKAGE_NAME, result.pinnedVersion, pm.installCommand);
      console.log(hint.message);
    }
  } catch (err) {
    console.warn(`⚠️ Failed to ensure runtime dependency: ${(err as Error).message}`);
  }
}

/**
 * `restore` intercept: run native restore unconditionally, then orchestrate
 * dynwinrt-codegen iff package.json declares `winapp.jsBindings`.
 */
export async function handleRestore(args: string[]): Promise<void> {
  const workspaceDir = resolveWorkspaceDir(args);
  const quiet = isQuiet(args);

  // See handleInit: do NOT set child cwd (avoids double-resolving a relative arg).
  await callWinappCli(['restore', ...args], { exitOnError: true });

  if (!hasJsBindings(workspaceDir)) {
    return;
  }

  // No lockfile = native restore no-op (empty winapp.yaml); nothing to generate, skip.
  if (!lockfileExists(workspaceDir)) {
    if (!quiet) {
      console.log(
        'ℹ️  No winmd inventory found (winapp.yaml has no packages yet), so JS bindings ' +
          'were not generated. Add packages to `winapp.yaml` and re-run `npx winapp restore`.'
      );
    }
    return;
  }

  // Stale lockfile can linger after packages removed; no packages = nothing to generate.
  const restoreYamlPath = resolveYamlPath(args);
  const yamlPackages = readWinappYamlPackages(workspaceDir, restoreYamlPath);
  if (!yamlPackages || yamlPackages.length === 0) {
    if (!quiet) {
      console.log(
        'ℹ️  winapp.yaml has no packages, so JS bindings were not generated. ' +
          'Add packages to `winapp.yaml` and re-run `npx winapp restore`.'
      );
    }
    return;
  }

  await runJsBindingsOrchestrator(workspaceDir, isVerbose(args), quiet, restoreYamlPath);
}

/** Runs the JS bindings pipeline and translates outcomes into exit codes. */
async function runJsBindingsOrchestrator(
  workspaceDir: string,
  verbose: boolean = false,
  quiet: boolean = false,
  yamlPath?: string,
  installRuntimeDep: boolean = false,
  manageRuntimeDep: boolean = false
): Promise<void> {
  try {
    const result = await runJsBindingsPipeline({
      workspaceDir,
      verbose,
      quiet,
      yamlPath,
      installRuntimeDep,
      manageRuntimeDep,
    });
    switch (result.outcome) {
      case 'completed':
        if (!quiet) {
          console.log(`✅ ${result.message}`);
        }
        return;
      case 'noJsBindings':
        // Silent — caller already vetted that jsBindings is configured.
        return;
      case 'lockfileMissing':
      case 'lockfileStale':
        console.error(`❌ ${result.message}`);
        process.exit(1);
        break;
      case 'noWinmdsToEmit':
        // Warning surfaces even with --quiet so users see actionable signals.
        console.warn(`⚠️ ${result.message}`);
        return;
    }
  } catch (err) {
    logErrorAndExit(err);
  }
}
