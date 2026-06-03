#!/usr/bin/env node

import { generateCppAddonFiles } from './cpp-addon-utils';
import { generateCsAddonFiles } from './cs-addon-utils';
import { addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getWinappCliPath, callWinappCli, callWinappCliCapture, WINAPP_CLI_CALLER_VALUE } from './winapp-cli-utils';
import { askBindingsKind, parseSetupSdksArg } from './jsbindings/init-prompt';
import { hasJsBindings, ensureJsBindingsBlock } from './jsbindings/package-json-config';
import { runJsBindingsPipeline } from './jsbindings/orchestrator';
import { getLockfilePath, LOCKFILE_NAME } from './jsbindings/lockfile-reader';
import { readWinappYamlPackages } from './jsbindings/yaml-packages-hash';
import {
  resolveWorkspaceDir,
  resolveYamlPath,
  isVerbose,
  isQuiet,
  hasConfigOnly,
  hasNoInstall,
  stripWrapperOnlyFlags,
} from './cli-args';
import { assertSafeWorkspaceFile } from './jsbindings/path-safety';
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';

// CLI name - change this to rebrand the tool
const CLI_NAME = 'winapp';

// Commands that should be handled by Node.js (everything else goes to winapp-cli)
const NODE_ONLY_COMMANDS = new Set(['node']);

// Commands the npm wrapper intercepts to add pre-/post-native hooks
// (currently: JS bindings prompt + orchestration).
const INTERCEPTED_COMMANDS = new Set(['init', 'restore']);

// argv flags that mean "skip every interactive wrapper hook" (help / completions
// / version are routed straight to the native CLI without prompting).
const HELP_FLAGS = new Set(['--help', '-h', '-?', '/?']);

interface ParsedArgs {
  help?: boolean;
  name?: string;
  template?: string;
  verbose?: boolean;
  [key: string]: string | boolean | undefined;
}

interface PackageJson {
  name: string;
  version: string;
  description?: string;
}

/**
 * Main CLI entry point for winapp package
 */
export async function main(): Promise<void> {
  const args = process.argv.slice(2);

  if (args.length === 0) {
    await showCombinedHelp();
    process.exit(1);
  }

  const command = args[0];
  const commandArgs = args.slice(1);

  try {
    // Handle help/version specially to show combined info
    if (['help', '--help', '-h'].includes(command)) {
      await showCombinedHelp();
      return;
    }

    if (['version', '--version', '-v'].includes(command)) {
      await showVersion();
      return;
    }

    // Handle completion requests — augment native CLI completions with wrapper-only commands
    if (command === 'complete') {
      await handleComplete(commandArgs);
      return;
    }

    // Route Node.js-only commands to local handlers
    if (NODE_ONLY_COMMANDS.has(command)) {
      await handleNodeCommand(command, commandArgs);
      return;
    }

    // `init --help` falls through to native help, which has no knowledge of the
    // wrapper-only options we add (e.g. --no-install). Run native help, then
    // append a short addendum so the flag is discoverable.
    if (command === 'init' && commandArgs.some((a) => HELP_FLAGS.has(a))) {
      await callWinappCli(stripWrapperOnlyFlags(args), { exitOnError: true });
      printInitWrapperOnlyHelp();
      return;
    }

    // Intercept init/restore so we can run the JS bindings pre-/post-hooks
    // around the native command. Help / completion flags bypass the hook.
    //
    // Fast-path: `init --setup-sdks none` has no JS bindings to wire up
    // (the dynwinrt codegen needs SDK winmds to compile against), so we
    // pass it straight through to the native CLI. This preserves the
    // pre-wrapper UX exactly — no extra yaml read, no informational log
    // line, no behaviour change. Users who want to refresh existing JS
    // bindings should run `winapp restore` (which is still intercepted).
    if (INTERCEPTED_COMMANDS.has(command) && !commandArgs.some((a) => HELP_FLAGS.has(a))) {
      if (command === 'init') {
        if (parseSetupSdksArg(commandArgs) === 'none') {
          // Fast path: no JS bindings to wire up. Still strip wrapper-only
          // flags (e.g. --no-install) — the native CLI rejects them.
          await callWinappCli(stripWrapperOnlyFlags(args), { exitOnError: true });
          return;
        }
        await handleInit(commandArgs);
        return;
      }
      if (command === 'restore') {
        await handleRestore(commandArgs);
        return;
      }
    }

    // Route everything else to winapp-cli
    await callWinappCli(args, { exitOnError: true });
  } catch (error) {
    logErrorAndExit(error);
  }
}

async function handleNodeCommand(command: string, args: string[]): Promise<void> {
  switch (command) {
    case 'node':
      await handleNode(args);
      break;

    default:
      console.error(`Unknown Node.js command: ${command}`);
      process.exit(1);
  }
}

// Node.js wrapper-only commands that should appear in completions
const NODE_WRAPPER_COMMANDS = ['node'];
const NODE_SUBCOMMANDS = [
  'create-addon',
  'add-electron-debug-identity',
  'clear-electron-debug-identity',
  'generate-bindings',
];

/**
 * Handle completion requests by forwarding to the native CLI and augmenting
 * with wrapper-only commands (node, node subcommands).
 */
async function handleComplete(args: string[]): Promise<void> {
  // If --setup is requested, forward directly to native CLI
  const setupIdx = args.indexOf('--setup');
  if (setupIdx !== -1) {
    await callWinappCli(['complete', ...args], { exitOnError: true });
    return;
  }

  // Parse --commandline and --position from args (supports both --key value and --key=value syntax)
  let commandLine = '';
  let position = 0;
  for (let i = 0; i < args.length; i++) {
    if (args[i].startsWith('--commandline=')) {
      commandLine = args[i].slice('--commandline='.length);
    } else if (args[i] === '--commandline' && i + 1 < args.length) {
      commandLine = args[++i];
    } else if (args[i].startsWith('--position=')) {
      position = parseInt(args[i].slice('--position='.length), 10) || 0;
    } else if (args[i] === '--position' && i + 1 < args.length) {
      position = parseInt(args[++i], 10) || 0;
    }
  }

  // Get completions from native CLI
  let nativeCompletions: string[] = [];
  try {
    const result = await callWinappCliCapture(['complete', ...args]);
    nativeCompletions = result.stdout
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.length > 0);
  } catch {
    // Native CLI may not be available; continue with wrapper-only completions
  }

  // Determine context from the command line to decide whether to add wrapper commands
  const textBeforeCursor = commandLine.slice(0, position);
  const hasTrailingSpace = textBeforeCursor.endsWith(' ');
  const tokens = textBeforeCursor.trim().split(/\s+/);
  // tokens[0] is "winapp", tokens[1] is the first subcommand if present, etc.
  // tokenCount accounts for trailing space meaning the user is starting a new token
  const tokenCount = hasTrailingSpace ? tokens.length + 1 : tokens.length;

  if (tokenCount <= 2) {
    // User is completing a top-level command — add wrapper-only commands
    const partial = tokenCount === 2 && !hasTrailingSpace ? tokens[1] : '';
    for (const cmd of NODE_WRAPPER_COMMANDS) {
      if (cmd.startsWith(partial) && !nativeCompletions.includes(cmd)) {
        nativeCompletions.push(cmd);
      }
    }
  } else if (tokenCount <= 3 && tokens[1] === 'node') {
    // User is completing a node subcommand
    const partial = tokenCount === 3 && !hasTrailingSpace ? tokens[2] : '';
    for (const sub of NODE_SUBCOMMANDS) {
      if (sub.startsWith(partial)) {
        nativeCompletions.push(sub);
      }
    }
  }

  // Output all completions
  for (const completion of nativeCompletions) {
    console.log(completion);
  }
}

function getPackageJson(): PackageJson {
  const packageJsonPath = require.resolve('../package.json');
  return JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));
}

async function showCombinedHelp(): Promise<void> {
  const packageJson = getPackageJson();

  console.log(`${packageJson.name} v${packageJson.version}`);
  console.log(packageJson.description);
  console.log('');

  // Try to get help from winapp-cli first
  try {
    const winappCliPath = getWinappCliPath();
    await new Promise<void>((resolve) => {
      const child = spawn(winappCliPath, ['--help'], {
        stdio: 'inherit',
        shell: false,
        env: {
          ...process.env,
          WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE,
        },
      });

      child.on('close', () => {
        resolve();
      });

      child.on('error', () => {
        // If winapp-cli is not available, continue without showing fallback help
        resolve();
      });
    });
  } catch {
    // Continue without showing fallback help if winapp-cli is not available
  }

  // Add Node.js-specific commands
  console.log('');
  console.log('Node.js Extensions:');
  console.log('  node <subcommand>         Node.js-specific commands');
  console.log('');
  console.log('Node.js Subcommands:');
  console.log('  node create-addon         Generate native addon files for Electron');
  console.log('  node add-electron-debug-identity  Add package identity to Electron debug process');
  console.log('  node clear-electron-debug-identity  Remove package identity from Electron debug process');
  console.log('  node generate-bindings    Regenerate JS/TypeScript bindings from package.json + cached winmds');
  console.log('');
  console.log('Examples:');
  console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
  console.log(`  ${CLI_NAME} node create-addon --template cs --name myAddon`);
  console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
  console.log(`  ${CLI_NAME} node clear-electron-debug-identity`);
  console.log(`  ${CLI_NAME} node generate-bindings`);
}

async function showVersion(): Promise<void> {
  const packageJson = getPackageJson();

  console.log(`${packageJson.description || 'Windows App Development CLI'}`);
  console.log('');
  console.log(`Node.js Package: ${packageJson.name} v${packageJson.version}`);

  // Try to get version from native CLI
  try {
    const winappCliPath = getWinappCliPath();

    if (!fs.existsSync(winappCliPath)) {
      console.log('Native CLI: Not available (executable not found)');
      return;
    }

    console.log('Native CLI:');

    await new Promise<void>((resolve) => {
      const child = spawn(winappCliPath, ['--version'], {
        stdio: 'inherit',
        shell: false,
        env: {
          ...process.env,
          WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE,
        },
      });

      child.on('close', (code) => {
        if (code !== 0) {
          console.log('  (version command failed)');
        }
        resolve();
      });

      child.on('error', () => {
        console.log('  Not available (execution failed)');
        resolve();
      });
    });
  } catch {
    console.log('Native CLI: Not available');
  }
}

async function handleNode(args: string[]): Promise<void> {
  // Handle help flags
  if (args.length === 0 || ['--help', '-h', 'help'].includes(args[0])) {
    console.log(`Usage: ${CLI_NAME} node <subcommand> [options]`);
    console.log('');
    console.log('Node.js-specific commands');
    console.log('');
    console.log('Subcommands:');
    console.log('  create-addon                   Generate native addon files for Electron');
    console.log('  add-electron-debug-identity    Add package identity to Electron debug process');
    console.log('  clear-electron-debug-identity  Remove package identity from Electron debug process');
    console.log('  generate-bindings              Regenerate JS/TypeScript bindings (no NuGet/cppwinrt restore)');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon --help`);
    console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
    console.log(`  ${CLI_NAME} node create-addon --name myCsAddon --template cs`);
    console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
    console.log(`  ${CLI_NAME} node clear-electron-debug-identity`);
    console.log(`  ${CLI_NAME} node generate-bindings`);
    console.log('');
    console.log(`Use "${CLI_NAME} node <subcommand> --help" for detailed help on each subcommand.`);
    return;
  }

  const subcommand = args[0];
  const subcommandArgs = args.slice(1);

  switch (subcommand) {
    case 'create-addon':
      await handleCreateAddon(subcommandArgs);
      break;

    case 'add-electron-debug-identity':
      await handleAddonElectronDebugIdentity(subcommandArgs);
      break;

    case 'clear-electron-debug-identity':
      await handleClearElectronDebugIdentity(subcommandArgs);
      break;

    case 'generate-bindings':
      await handleGenerateBindings(subcommandArgs);
      break;

    default:
      console.error(`❌ Unknown node subcommand: ${subcommand}`);
      console.error(`Run "${CLI_NAME} node" for available subcommands.`);
      process.exit(1);
  }
}

async function handleCreateAddon(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    name: undefined, // Will be set based on template
    template: 'cpp',
    verbose: false,
  });

  // Set default name based on template
  if (!options.name) {
    options.name = options.template === 'cs' ? 'csAddon' : 'nativeWindowsAddon';
  }

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node create-addon [options]`);
    console.log('');
    console.log('Generate addon files for Electron project');
    console.log('');
    console.log('Options:');
    console.log('  --name <name>         Addon name (default depends on template)');
    console.log('  --template <type>     Addon template: cpp, cs (default: cpp)');
    console.log('  --verbose             Enable verbose output (default: false)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Templates:');
    console.log('  cpp                   C++ native addon (node-gyp)');
    console.log('  cs                    C# addon (node-api-dotnet)');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon`);
    console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
    console.log(`  ${CLI_NAME} node create-addon --template cs --name MyCsAddon`);
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing package.json)');
    return;
  }

  // Validate template
  if (!['cpp', 'cs'].includes(options.template as string)) {
    console.error(`❌ Invalid template: ${options.template}. Valid options: cpp, cs`);
    process.exit(1);
  }

  try {
    let result;

    if (options.template === 'cs') {
      // Use C# addon generator
      result = await generateCsAddonFiles({
        name: options.name as string,
        verbose: options.verbose as boolean,
      });

      console.log(`New addon at: ${result.addonPath}`);

      const restoreArgs = ['restore'];
      if (options.verbose) {
        restoreArgs.push('--verbose');
      }

      await callWinappCli(restoreArgs, { exitOnError: true });

      console.log('');

      if (result.needsTerminalRestart) {
        printTerminalRestartInstructions();
      }

      console.log(`Next steps:`);
      console.log(`  1. npm run build-${result.addonName}`);
      console.log(`  2. See ${result.addonName}/README.md for usage examples`);
    } else {
      // Use C++ addon generator
      result = await generateCppAddonFiles({
        name: options.name as string,
        verbose: options.verbose as boolean,
      });

      console.log(`New addon at: ${result.addonPath}`);
      console.log('');

      if (result.needsTerminalRestart) {
        printTerminalRestartInstructions();
      }

      console.log(`Next steps:`);
      console.log(`  1. npm run build-${result.addonName}`);
      console.log(`  2. In your source, import the addon with:`);
      console.log(
        `     "const ${result.addonName} = require('./${result.addonName}/build/Release/${result.addonName}.node')";`
      );
    }
  } catch (error) {
    logErrorAndExit(error);
  }
}

function printTerminalRestartInstructions(): void {
  console.log(
    '⚠️ IMPORTANT: You need to restart your terminal/command prompt for newly installed tools to be available in your PATH.'
  );

  // Simple check: This variable usually only exists if running inside PowerShell
  if (process.env.PSModulePath) {
    console.log('💡 To refresh current session, copy and run this line:');
    console.log(
      '   \x1b[36m$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")\x1b[0m'
    );
  }
  console.log('');
}

async function handleAddonElectronDebugIdentity(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    verbose: false,
    'no-install': false,
    'keep-identity': false,
    manifest: undefined,
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node add-electron-debug-identity [options]`);
    console.log('');
    console.log('Add package identity to Electron debug process');
    console.log('');
    console.log('This command will:');
    console.log('  1. Create a backup of node_modules/electron/dist/electron.exe');
    console.log(
      '  2. Generate a sparse MSIX manifest in .winapp/debug folder, and assets in node_modules/electron/dist/ folder'
    );
    console.log('  3. Add package identity to the Electron executable');
    console.log('  4. Register the sparse package with external location');
    console.log('');
    console.log('Options:');
    console.log(
      '  --manifest <path>     Path to custom Package.appxmanifest or appxmanifest.xml (default: auto-detected in current directory)'
    );
    console.log('  --no-install          Do not install the package after creation (will require manual registration)');
    console.log('  --keep-identity       Keep the manifest identity as-is, without appending .debug suffix');
    console.log('  --verbose             Enable verbose output (default: false)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing node_modules/electron)');
    return;
  }

  try {
    await addElectronDebugIdentity({
      verbose: options.verbose as boolean,
      noInstall: options['no-install'] as boolean,
      keepIdentity: options['keep-identity'] as boolean,
      manifest: options.manifest as string | undefined,
    });

    console.log(`✅ Electron debug identity setup completed successfully!`);
  } catch (error) {
    logErrorAndExit(error);
  }
}

async function handleClearElectronDebugIdentity(args: string[]): Promise<void> {
  const options = parseArgs(args, {
    verbose: false,
  });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node clear-electron-debug-identity [options]`);
    console.log('');
    console.log('Remove package identity from Electron debug process');
    console.log('');
    console.log('This command will:');
    console.log('  1. Restore electron.exe from the backup created by add-electron-debug-identity');
    console.log('  2. Remove the backup files');
    console.log('');
    console.log('Options:');
    console.log('  --verbose             Enable verbose output (default: false)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('Note: This command must be run from the root of an Electron project');
    console.log('      (directory containing node_modules/electron)');
    return;
  }

  try {
    const result = await clearElectronDebugIdentity({
      verbose: options.verbose as boolean,
    });

    if (result.restoredFromBackup) {
      console.log(`✅ Electron debug identity cleared successfully!`);
    } else {
      console.log(`ℹ️  No backup found - electron.exe may already be clean.`);
    }
  } catch (error) {
    logErrorAndExit(error);
  }
}

/**
 * `node generate-bindings`: regenerate JS/TypeScript bindings without re-running
 * the heavy native restore (no NuGet download, no cppwinrt headers, no manifest /
 * cert work). Re-reads `winapp.jsBindings` from package.json and the cached
 * `.winapp/winmds.lock.json` written by the last `winapp restore`, then runs
 * dynwinrt-codegen. Intended for fast iteration after editing the `winapp.jsBindings`
 * block (packages scope, extraTypes, skip/refOnly/emit overrides).
 *
 * Passive by design: it only *reads* these two inputs and emits bindings — it
 * never writes package.json. Adding the `winapp.jsBindings` block and the
 * `@microsoft/dynwinrt` dependency is `winapp init`'s job; this command fails
 * fast when the block is absent.
 *
 * Pre-checks fail fast with an actionable hint when a prerequisite is missing.
 */
async function handleGenerateBindings(args: string[]): Promise<void> {
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

  // 1. Must be an npm/Node project — winapp.jsBindings lives in package.json.
  const pkgJsonPath = path.join(workspaceDir, 'package.json');
  assertSafeWorkspaceFile(workspaceDir, pkgJsonPath, 'package.json');
  if (!fs.existsSync(pkgJsonPath)) {
    console.error('❌ No package.json found in this directory.');
    console.error('   This command only applies to npm/Node projects.');
    console.error('   Run `npm init -y` first, then re-run this command.');
    process.exit(1);
  }

  // 2. The `winapp.jsBindings` namespace must already exist. This command is a
  //    passive regenerator: it only reads `winapp.jsBindings` + the cached
  //    lockfile and emits bindings — it never writes declarations. Adding the
  //    block (and the runtime dependency) is `winapp init`'s job, so fail fast
  //    with an actionable hint instead of silently creating it here.
  if (!hasJsBindings(workspaceDir)) {
    console.error('❌ No "winapp.jsBindings" namespace in package.json.');
    console.error('   Run `winapp init` to opt into JS bindings (it adds the block and the');
    console.error('   @microsoft/dynwinrt dependency), then re-run this command to regenerate.');
    process.exit(1);
  }

  // 3. Lockfile from a prior `winapp restore` must be present. (Schema-mismatch
  //    cases get the orchestrator's more detailed message via `lockfileStale`.)
  const lockfilePath = getLockfilePath(workspaceDir);
  assertSafeWorkspaceFile(workspaceDir, lockfilePath, LOCKFILE_NAME);
  if (!fs.existsSync(lockfilePath)) {
    console.error(`❌ No .winapp/${LOCKFILE_NAME} found.`);
    console.error('   This file is written by `winapp restore`. If you cloned a fresh repo,');
    console.error('   or upgraded from an older winapp that did not write this lockfile,');
    console.error('   run `winapp restore` once to build the winmd inventory, then re-run this command.');
    process.exit(1);
  }

  // 4. Hand off to the shared pipeline. Outcomes are translated to ✅ / ❌ /⚠️
  //    by runJsBindingsOrchestrator.
  await runJsBindingsOrchestrator(workspaceDir, isVerbose(args), quiet, resolveYamlPath(args));
}

/**
 * Print the npm-wrapper-only options for `init` that the native `--help` output
 * does not know about. Appended after native help so users can discover them.
 */
function printInitWrapperOnlyHelp(): void {
  console.log('');
  console.log(`Options (added by the ${CLI_NAME} npm wrapper):`);
  console.log('  --no-install          Skip auto-installing the @microsoft/dynwinrt runtime');
  console.log('                        dependency into node_modules after generating JS bindings');
  console.log('                        (the dependency is still added to package.json).');
}

/**
 * `init` intercept: ask the JS bindings prompt, run native init, then (when
 * the user wants JS bindings) write the `"winapp.jsBindings"` namespace to
 * package.json, re-run restore so the lockfile is fresh, and orchestrate
 * dynwinrt-codegen.
 *
 * The native CLI itself has no awareness of JS bindings — every flag, every
 * code path is identical regardless of the user's choice here.
 */
async function handleInit(args: string[]): Promise<void> {
  const workspaceDir = resolveWorkspaceDir(args);
  const quiet = isQuiet(args);
  const configOnly = hasConfigOnly(args);
  const noInstall = hasNoInstall(args);

  // Re-running init on a configured workspace? Infer the choice rather than
  // re-prompt so we never silently drop the user's prior customizations.
  const existingJsBindings = hasJsBindings(workspaceDir);

  // Native init runs FIRST so all of its own prompts (package name, publisher,
  // version, SDK setup, …) complete before we ask about JS bindings. Asking
  // last also lets us gate the question on whether SDK setup actually happened.
  // Native init runs with the user's literal argv (no flag injection), minus
  // wrapper-only flags the native CLI doesn't recognize (`--no-install`). We
  // deliberately do NOT change the child's cwd: native `init` resolves
  // `base-directory` / `--config-dir` against its own cwd, so changing it would
  // double-resolve any relative path (`winapp init subdir` → spawn
  // cwd=/foo/subdir + arg `subdir` → native lands in /foo/subdir/subdir).
  // Wrapper-side `workspaceDir` is an absolute path we only use for our OWN
  // bookkeeping (package.json read/write, codegen output, prompts).
  const nativeArgs = stripWrapperOnlyFlags(args);
  await callWinappCli(['init', ...nativeArgs], { exitOnError: true });

  // SDK setup writes .winapp/winmds.lock.json during winmd discovery (Step 5),
  // so its presence tells us JS bindings have winmds to generate against. When
  // the user declines SDK setup there's no lockfile — and nothing to bind to.
  // `--config-only` skips package install (no lockfile) but defers codegen on
  // purpose, so treat it as "ready" for the opt-in decision below.
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

  // User opted out — nothing more to do.
  if (outcome.kind === 'no') {
    return;
  }

  // Persist the default jsBindings block to package.json so subsequent
  // `winapp restore` / `winapp node generate-bindings` runs pick it up. Skip
  // when package.json is missing so we don't fail an init that already
  // succeeded — surface a clear hint.
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

  // --config-only skips package installation in the native CLI, so no lockfile
  // gets written and the orchestrator would fail with `lockfileMissing`. Honor
  // the user's intent and stop here — they can run `winapp restore` later.
  if (configOnly) {
    if (!quiet) {
      console.log(
        'ℹ️  --config-only requested; JS bindings codegen deferred. ' +
          'Run `npx winapp restore` (or `npx winapp node generate-bindings` after a restore) to generate.'
      );
    }
    return;
  }

  // Native `winapp init` already invoked WorkspaceSetupService, which wrote
  // .winapp/winmds.lock.json as part of Step 5 (winmd discovery). The previous
  // implementation re-ran `winapp restore` here defensively, but that doubled
  // the cost of init and ignored `--config-only`. Hand straight off to the
  // orchestrator instead.
  //
  // Guard: an init re-run can infer Yes from existing config even though SDK
  // setup was skipped this time (no lockfile). Generating against a missing
  // winmd inventory would fail; inform and defer instead of erroring out.
  if (!lockfilePresent) {
    if (!quiet) {
      console.log(
        'ℹ️  Windows SDKs were not set up, so JS bindings were not generated. ' +
          'Run `npx winapp restore` then `npx winapp node generate-bindings` to generate them.'
      );
    }
    return;
  }

  // init is the onboarding flow: it is the only command that *writes* the
  // runtime dependency to package.json (manageRuntimeDep) and, unless the user
  // opted out with --no-install, also installs it into node_modules.
  //
  // Native init remaps --config-dir to the selected init directory when not
  // explicit, so resolve the yaml against workspaceDir (not cwd) — otherwise
  // `winapp init <base-dir>` would hash the wrong file and report a false
  // stale-lockfile failure.
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
async function handleRestore(args: string[]): Promise<void> {
  const workspaceDir = resolveWorkspaceDir(args);
  const quiet = isQuiet(args);

  // See handleInit: do NOT set child cwd. Native `restore` resolves
  // `base-directory` / `--config-dir` relative to its own cwd; forwarding
  // a relative positional from a re-rooted shell would double-resolve.
  await callWinappCli(['restore', ...args], { exitOnError: true });

  if (!hasJsBindings(workspaceDir)) {
    return;
  }

  // Native restore is a no-op success when there's no winapp.yaml or it has no
  // packages — in those cases it writes no lockfile. Don't regress that contract
  // into a hard failure: with no lockfile there's simply nothing to generate, so
  // skip with an informational note instead of erroring out (the orchestrator
  // would otherwise return `lockfileMissing` → exit 1).
  if (!fs.existsSync(getLockfilePath(workspaceDir))) {
    if (!quiet) {
      console.log(
        'ℹ️  No winmd inventory found (winapp.yaml has no packages yet), so JS bindings ' +
          'were not generated. Add packages to `winapp.yaml` and re-run `npx winapp restore`.'
      );
    }
    return;
  }

  // A *stale* lockfile from a previous restore can linger after the user removes
  // winapp.yaml or empties its `packages:` block. Native restore treats that as a
  // no-op success (it writes no fresh lockfile), but the orchestrator would then
  // report the lingering lockfile as stale → exit 1. Preserve the no-op contract:
  // when there are no packages to restore, there are no bindings to generate.
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

function logErrorAndExit(error: unknown): never {
  if (error instanceof Error && error.message.includes('winapp-cli exited with code')) {
    process.exit(1);
  }

  if (error instanceof Error && error.message) {
    console.error(error.message);
  } else {
    console.error(error);
  }

  process.exit(1);
}

function parseArgs(args: string[], defaults: ParsedArgs = {}): ParsedArgs {
  const result: ParsedArgs = { ...defaults };

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];

    if (arg === '--help' || arg === '-h') {
      result.help = true;
    } else if (arg.startsWith('--')) {
      const key = arg.slice(2);
      const nextArg = args[i + 1];

      if (nextArg && !nextArg.startsWith('--')) {
        // Value argument
        result[key] = nextArg;
        i++; // Skip next arg
      } else {
        // Boolean flag
        result[key] = true;
      }
    }
  }

  return result;
}

// Run if called directly
if (require.main === module) {
  main();
}
