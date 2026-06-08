// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// JS-binding hooks layered around native init/restore.

import * as fs from 'fs';
import * as path from 'path';

import { CLI_NAME, parseArgs, logErrorAndExit } from '../cli-shared';
import { callWinappCli } from '../winapp-cli-utils';
import { askBindingsKind } from './init-prompt';
import { hasJsBindings, ensureJsBindingsBlock } from './package-json-config';
import { runJsBindingsPipeline } from './orchestrator';
import { getLockfilePath } from './lockfile-reader';
import { readWinappYamlPackages } from './yaml-packages-hash';
import {
  resolveWorkspaceDir,
  resolveYamlPath,
  isVerbose,
  isQuiet,
  hasConfigOnly,
  hasNoInstall,
  stripWrapperOnlyFlags,
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

/** Append the wrapper-only `init` options the native `--help` doesn't know about. */
export function printInitWrapperOnlyHelp(): void {
  console.log('');
  console.log(`Options (added by the ${CLI_NAME} npm wrapper):`);
  console.log('  --no-install          Skip auto-installing the @microsoft/dynwinrt runtime');
  console.log('                        dependency into node_modules after generating JS bindings');
  console.log('                        (the dependency is still added to package.json).');
}

/** `init` hook: run native init, then optionally add and generate JS bindings. */
export async function handleInit(args: string[]): Promise<void> {
  const workspaceDir = resolveWorkspaceDir(args);
  const quiet = isQuiet(args);
  const configOnly = hasConfigOnly(args);
  const noInstall = hasNoInstall(args);

  // Re-running on a configured workspace: infer the choice, don't re-prompt.
  const existingJsBindings = hasJsBindings(workspaceDir);

  // Native init runs first (its prompts finish, and we can gate on SDK setup).
  // Don't change child cwd: native resolves paths against its own cwd.
  const nativeArgs = stripWrapperOnlyFlags(args);
  await callWinappCli(['init', ...nativeArgs], { exitOnError: true });

  // Lockfile (from SDK winmd discovery) = winmds to bind against; --config-only defers.
  const lockfilePresent = fs.existsSync(getLockfilePath(workspaceDir));

  let outcome;
  try {
    outcome = await askBindingsKind({
      workspaceDir,
      argv: args,
      isInit: true,
      existingJsBindings,
      sdksReady: lockfilePresent || configOnly,
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

  // Onboarding flow: only command that writes+installs the runtime dep (unless --no-install).
  // Resolve yaml against workspaceDir (native remaps --config-dir) to avoid false stale-lockfile.
  await runJsBindingsOrchestrator(
    workspaceDir,
    isVerbose(args),
    quiet,
    resolveYamlPath(args, workspaceDir),
    !noInstall,
    true
  );
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
  if (!fs.existsSync(getLockfilePath(workspaceDir))) {
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
