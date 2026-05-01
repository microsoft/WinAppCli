#!/usr/bin/env node

import { generateCppAddonFiles } from './cpp-addon-utils';
import { generateCsAddonFiles } from './cs-addon-utils';
import { addElectronDebugIdentity, clearElectronDebugIdentity } from './msix-utils';
import { getWinappCliPath, callWinappCli, callWinappCliCapture, WINAPP_CLI_CALLER_VALUE } from './winapp-cli-utils';
import { autoGenerateJsBindings, generateJsBindings, readJsBindingsConfig } from './js-bindings-utils';
import { getProjectRootDir } from './utils';
import { spawn } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import * as readline from 'readline';

// CLI name - change this to rebrand the tool
const CLI_NAME = 'winapp';

// Commands that should be handled by Node.js (everything else goes to winapp-cli)
const NODE_ONLY_COMMANDS = new Set(['node']);

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

    // Route everything else to winapp-cli
    await callWinappCli(args, { exitOnError: true });

    // Post-init: prompt user to generate JS/TS bindings if this is a Node.js project
    if (command === 'init') {
      await promptJsBindingsAfterInit({ verbose: args.includes('--verbose') });
    }

    // Post-restore: auto-generate JS bindings if already configured
    if (command === 'restore') {
      await autoGenerateJsBindings({ verbose: args.includes('--verbose') });
    }
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
const NODE_SUBCOMMANDS = ['create-addon', 'add-electron-debug-identity', 'clear-electron-debug-identity'];

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
  console.log('  node generate-bindings    Generate JS/TS bindings from WinRT metadata');
  console.log('  node add-electron-debug-identity  Add package identity to Electron debug process');
  console.log('  node clear-electron-debug-identity  Remove package identity from Electron debug process');
  console.log('');
  console.log('Examples:');
  console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
  console.log(`  ${CLI_NAME} node create-addon --template cs --name myAddon`);
  console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
  console.log(`  ${CLI_NAME} node clear-electron-debug-identity`);
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
    console.log('  create-addon                  Generate native addon files for Electron');
    console.log('  generate-bindings             Generate JS/TS bindings from WinRT metadata');
    console.log('  add-electron-debug-identity   Add package identity to Electron debug process');
    console.log('  clear-electron-debug-identity Remove package identity from Electron debug process');
    console.log('');
    console.log('Examples:');
    console.log(`  ${CLI_NAME} node create-addon --help`);
    console.log(`  ${CLI_NAME} node create-addon --name myAddon`);
    console.log(`  ${CLI_NAME} node create-addon --name myCsAddon --template cs`);
    console.log(`  ${CLI_NAME} node generate-bindings`);
    console.log(`  ${CLI_NAME} node add-electron-debug-identity`);
    console.log(`  ${CLI_NAME} node clear-electron-debug-identity`);
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
      '  --manifest <path>     Path to custom appxmanifest.xml (default: appxmanifest.xml in current directory)'
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

async function handleGenerateBindings(args: string[]): Promise<void> {
  const options = parseArgs(args, { verbose: false });

  if (options.help) {
    console.log(`Usage: ${CLI_NAME} node generate-bindings [options]`);
    console.log('');
    console.log('Generate JS/TS bindings from WinRT metadata');
    console.log('');
    console.log('Reads the jsBindings section from winapp.yaml and generates typed');
    console.log('bindings using winrt-meta. This runs automatically after restore/init');
    console.log('if configured, but can also be called manually.');
    console.log('');
    console.log('Options:');
    console.log('  --verbose             Enable verbose output (default: false)');
    console.log('  --help                Show this help');
    console.log('');
    console.log('winapp.yaml configuration:');
    console.log('  jsBindings:');
    console.log('    lang: js              # js | cjs | ts (default: js)');
    console.log('    output: generated-js  # output directory');
    console.log('    packages:             # NuGet packages with .winmd metadata');
    console.log('      - Microsoft.WindowsAppSDK.AI');
    console.log('    systemTypes:          # additional Windows SDK classes');
    console.log('      - namespace: Windows.Storage');
    console.log('        classes: StorageFile');
    console.log('');
    console.log('Prerequisites:');
    console.log('  - winrt-meta must be installed (npm install -D winrt-meta)');
    console.log('  - Packages must be restored first (winapp restore)');
    return;
  }

  try {
    const result = await generateJsBindings({
      verbose: options.verbose as boolean,
    });

    if (!result.generated) {
      console.error(`❌ ${result.skipReason}`);
      process.exit(1);
    }
  } catch (error) {
    logErrorAndExit(error);
  }
}

// ======================================================================
// Post-init: interactive JS/TS bindings prompt
// ======================================================================

function askQuestion(rl: readline.Interface, question: string): Promise<string> {
  return new Promise((resolve) => {
    rl.question(question, (answer) => resolve(answer.trim()));
  });
}

async function promptJsBindingsAfterInit(options: { verbose?: boolean }): Promise<void> {
  const projectRoot = getProjectRootDir();

  // Only prompt for Node.js projects
  if (!fs.existsSync(path.join(projectRoot, 'package.json'))) {
    return;
  }

  // Skip if jsBindings already configured
  const yamlPath = findWinappYamlPath(projectRoot);
  if (yamlPath && readJsBindingsConfig(yamlPath)) {
    await autoGenerateJsBindings(options);
    return;
  }

  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout,
  });

  try {
    console.log('');
    const generate = await askQuestion(
      rl,
      'Would you like to generate JavaScript/TypeScript bindings for WinRT APIs? [Y/n] '
    );

    if (generate.toLowerCase() === 'n' || generate.toLowerCase() === 'no') {
      return;
    }

    console.log('');
    console.log('Select binding language:');
    console.log('  1. JavaScript (js)');
    console.log('  2. TypeScript (ts)');
    const langChoice = await askQuestion(rl, 'Enter choice [1/2]: ');

    let lang: string;
    let output: string;
    if (langChoice === '2' || langChoice.toLowerCase() === 'ts') {
      lang = 'ts';
      output = 'generated';
    } else {
      lang = 'js';
      output = 'generated-js';
    }

    // Write jsBindings config to winapp.yaml
    if (yamlPath) {
      let content = fs.readFileSync(yamlPath, 'utf8');
      if (!content.endsWith('\n')) {
        content += '\n';
      }
      content += `\njsBindings:\n  lang: ${lang}\n  output: ${output}\n`;
      fs.writeFileSync(yamlPath, content, 'utf8');
      console.log(`\nAdded jsBindings config to ${path.basename(yamlPath)} (lang: ${lang}, output: ${output})`);
    }

    // Install dynwinrt-js runtime dependency
    console.log('\nInstalling dynwinrt-js runtime dependency...');
    try {
      const { execSync } = require('child_process');
      execSync('npm install dynwinrt-js', { cwd: projectRoot, stdio: 'inherit' });
    } catch {
      console.warn('Warning: failed to install dynwinrt-js. Install it manually: npm install dynwinrt-js');
    }

    // Generate bindings
    const result = await generateJsBindings({ verbose: options.verbose });

    if (result.generated) {
      console.log('');
      console.log('To use the generated bindings in your code:');
      console.log(`  const { ClassName } = require('./${output}/NamespaceName');`);
      console.log('');
      console.log('To regenerate bindings manually:');
      console.log(`  npx ${CLI_NAME} node generate-bindings`);
      console.log('');
      console.log(`Bindings will also regenerate automatically on '${CLI_NAME} restore'.`);
    } else {
      console.log(`\nNo bindings generated: ${result.skipReason}`);
      console.log(`You can configure packages in winapp.yaml and run '${CLI_NAME} node generate-bindings' later.`);
    }
  } finally {
    rl.close();
  }
}

function findWinappYamlPath(projectRoot: string): string | null {
  const candidates = [
    path.join(projectRoot, 'winapp.yaml'),
    path.join(projectRoot, '.winapp', 'winapp.yaml'),
  ];
  for (const p of candidates) {
    if (fs.existsSync(p)) return p;
  }
  return null;
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
