#!/usr/bin/env node
/**
 * generate-commands.mjs
 *
 * Auto-generates `src/winapp-commands.ts` from the live CLI schema.
 *
 * Usage:
 *   node scripts/generate-commands.mjs              # uses built CLI binary
 *   node scripts/generate-commands.mjs --check       # exit 1 if file would change
 *   node scripts/generate-commands.mjs --schema path  # use a specific schema JSON file
 */
import { execSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';

// ---------------------------------------------------------------------------
// CLI arg parsing
// ---------------------------------------------------------------------------
const args = process.argv.slice(2);
const checkOnly = args.includes('--check');
const schemaIdx = args.indexOf('--schema');
const schemaOverride = schemaIdx !== -1 ? args[schemaIdx + 1] : null;

const SCRIPT_DIR = import.meta.dirname;
const NPM_ROOT = resolve(SCRIPT_DIR, '..');
const OUTPUT = resolve(NPM_ROOT, 'src/winapp-commands.ts');

// ---------------------------------------------------------------------------
// Schema loading (same candidates as winapp-cli-utils.ts)
// ---------------------------------------------------------------------------
function loadSchema() {
  if (schemaOverride) {
    return JSON.parse(readFileSync(resolve(schemaOverride), 'utf8'));
  }

  const arch = process.arch === 'arm64' ? 'win-arm64' : 'win-x64';
  const candidates = [
    join(NPM_ROOT, `bin/${arch}/winapp.exe`),
    // Repo-root artifacts (built by scripts/build-cli.ps1)
    resolve(NPM_ROOT, `../../artifacts/cli/${arch}/winapp.exe`),
  ];
  const cliPath = candidates.find((p) => existsSync(p));

  if (cliPath) {
    const raw = execSync(`"${cliPath}" --cli-schema`, { encoding: 'utf8' });
    return JSON.parse(raw);
  }

  // Fallback: checked-in schema
  const fallback = resolve(NPM_ROOT, '../../docs/cli-schema.json');
  if (existsSync(fallback)) {
    return JSON.parse(readFileSync(fallback, 'utf8'));
  }

  throw new Error(
    'Cannot locate winapp CLI binary or docs/cli-schema.json.\n' +
      'Build the CLI first (scripts/build-cli.ps1) or ensure docs/cli-schema.json exists.'
  );
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** `--kebab-case` → `camelCase` */
function kebabToCamel(s) {
  return s.replace(/^-+/, '').replace(/-([a-z])/g, (_, c) => c.toUpperCase());
}

/** `kebab-case` → `PascalCase` */
function kebabToPascal(s) {
  const cc = kebabToCamel(s);
  return cc.charAt(0).toUpperCase() + cc.slice(1);
}

/** Clean up CLI description for JSDoc (single line, no trailing period). */
function cleanDesc(desc) {
  if (!desc) return '';
  return desc.replace(/\r?\n/g, ' ').replace(/\s+/g, ' ').trim();
}

const COMMON_OPTIONS = new Set(['--quiet', '--verbose', '--help']);
const TS_RESERVED = new Set(['package', 'default', 'export', 'import', 'class', 'function', 'return', 'delete', 'new']);

/**
 * Nullable enum types — strip `System.Nullable<...>` wrapper.
 */
const NULLABLE_ENUM_RE = /^System\.Nullable<(.+)>$/;

/** Collect all union types actually used across all commands. */
const usedUnions = new Map(); // tsName → { values, tsName }

/**
 * Derive a TypeScript union type name from the .NET valueType string.
 * e.g. "WinApp.Cli.Models.IfExists" → "IfExists"
 *      "System.Nullable<WinApp.Cli.Models.IfExists>" → "IfExists"
 */
function deriveUnionName(valueType) {
  let key = valueType;
  const nullableMatch = key.match(NULLABLE_ENUM_RE);
  if (nullableMatch) key = nullableMatch[1];
  // Extract short class name (last segment after '.')
  const lastDot = key.lastIndexOf('.');
  return lastDot >= 0 ? key.slice(lastDot + 1) : key;
}

/** Map a .NET value type to a TypeScript type string. */
function tsType(valueType, helpName) {
  if (!valueType) return 'string';
  if (valueType.includes('Boolean')) return 'boolean';
  if (valueType.includes('Int32') || valueType.includes('Int64') || valueType.includes('Double')) return 'number';

  // If helpName has pipe-separated values, it's an enum — derive a named union type
  if (helpName && helpName.includes('|')) {
    const vals = helpName.split('|').map((v) => v.trim()).filter(Boolean);
    if (vals.length >= 2) {
      const tsName = deriveUnionName(valueType);
      usedUnions.set(tsName, { tsName, values: vals });
      return tsName;
    }
  }

  return 'string';
}

/** Whether a boolean option is a flag (arity max 0 or 1 with default false). */
function isBoolFlag(opt) {
  return tsType(opt.valueType, opt.helpName) === 'boolean';
}

/**
 * Commands that act as pass-throughs (arbitrary extra args forwarded to
 * an underlying tool). We add a `string[]` property for the extra args.
 */
const PASSTHROUGH_COMMANDS = {
  tool: { propName: 'toolArgs', description: "Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix'].", separator: ' -- ' },
  store: { propName: 'storeArgs', description: 'Arguments to pass through to the Microsoft Store Developer CLI.', separator: '' },
};

// ---------------------------------------------------------------------------
// Flatten schema into leaf commands
// ---------------------------------------------------------------------------
function flattenCommands(node, parentPath = []) {
  const results = [];
  const subs = node.subcommands || {};

  for (const [name, cmd] of Object.entries(subs)) {
    if (cmd.hidden) continue;
    const cmdPath = [...parentPath, name];

    if (cmd.subcommands && Object.keys(cmd.subcommands).length > 0) {
      results.push(...flattenCommands(cmd, cmdPath));
    } else {
      results.push({ path: cmdPath, cmd });
    }
  }
  return results;
}

// ---------------------------------------------------------------------------
// Generate TS source
// ---------------------------------------------------------------------------
function generate(schema) {
  const commands = flattenCommands(schema);
  const lines = [];
  const L = (s = '') => lines.push(s);

  // -- header
  L('/**');
  L(' * AUTO-GENERATED — DO NOT EDIT');
  L(' *');
  L(' * Regenerate with:  npm run generate-commands');
  L(` * Source schema version: ${schema.version || 'unknown'}`);
  L(' *');
  L(' * Programmatic wrappers for all winapp CLI commands.');
  L(' * Each function builds the CLI arguments, invokes the native CLI,');
  L(' * and returns a typed result with captured stdout/stderr.');
  L(' */');
  L("import {");
  L("  callWinappCliCapture,");
  L("  CallWinappCliCaptureOptions,");
  L("  CallWinappCliCaptureResult,");
  L("} from './winapp-cli-utils';");
  L();

  // -- pre-scan all commands to collect used union types
  for (const { cmd } of commands) {
    for (const optDef of Object.values(cmd.options || {})) {
      tsType(optDef.valueType, optDef.helpName);
    }
  }

  // -- shared types
  L('// ---------------------------------------------------------------------------');
  L('// Shared / common types');
  L('// ---------------------------------------------------------------------------');
  L();

  // Emit discovered union types
  for (const [name, { values }] of usedUnions) {
    L(`/** ${name} values. */`);
    L(`export type ${name} = ${values.map((v) => `'${v}'`).join(' | ')};`);
    L();
  }

  L('/** Base options shared by most commands. */');
  L('export interface CommonOptions {');
  L('  /** Suppress progress messages. */');
  L('  quiet?: boolean;');
  L('  /** Enable verbose output. */');
  L('  verbose?: boolean;');
  L('  /** Working directory for the CLI process (defaults to process.cwd()). */');
  L('  cwd?: string;');
  L('}');
  L();
  L('/** Result returned by every command wrapper. */');
  L('export interface WinappResult {');
  L('  /** Process exit code (always 0 on success – non-zero throws). */');
  L('  exitCode: number;');
  L('  /** Captured standard output. */');
  L('  stdout: string;');
  L('  /** Captured standard error. */');
  L('  stderr: string;');
  L('}');
  L();

  // -- helpers
  L('// ---------------------------------------------------------------------------');
  L('// Helpers');
  L('// ---------------------------------------------------------------------------');
  L();
  L('function pushCommon(args: string[], opts: CommonOptions): void {');
  L("  if (opts.quiet) args.push('--quiet');");
  L("  if (opts.verbose) args.push('--verbose');");
  L('}');
  L();
  L('function captureOpts(opts: CommonOptions): CallWinappCliCaptureOptions {');
  L('  return opts.cwd ? { cwd: opts.cwd } : {};');
  L('}');
  L();
  L('async function execCommand(args: string[], opts: CommonOptions): Promise<WinappResult> {');
  L('  pushCommon(args, opts);');
  L('  const result: CallWinappCliCaptureResult = await callWinappCliCapture(args, captureOpts(opts));');
  L('  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };');
  L('}');

  // -- per-command wrappers
  for (const { path: cmdPath, cmd } of commands) {
    const cmdPathStr = cmdPath.join(' ');
    const fnName = getFunctionName(cmdPath);
    const ifaceName = kebabToPascal(cmdPath.join('-')) + 'Options';

    // Check for passthrough command
    const passthrough = PASSTHROUGH_COMMANDS[cmdPath.join(' ')] || null;

    // Collect non-common options
    const opts = [];
    for (const [optName, optDef] of Object.entries(cmd.options || {})) {
      if (COMMON_OPTIONS.has(optName)) continue;
      // Skip if all aliases are common
      if (optDef.aliases?.every((a) => COMMON_OPTIONS.has(a))) continue;
      opts.push({ cliName: optName, def: optDef, propName: kebabToCamel(optName) });
    }

    // Collect arguments (positional)
    const positionalArgs = [];
    for (const [argName, argDef] of Object.entries(cmd.arguments || {})) {
      positionalArgs.push({ cliName: argName, def: argDef, propName: kebabToCamel(argName) });
    }
    // Sort by order
    positionalArgs.sort((a, b) => (a.def.order ?? 0) - (b.def.order ?? 0));

    // Determine which positional args are required (arity minimum >= 1)
    const hasRequiredArgs = positionalArgs.some((a) => a.def.arity?.minimum >= 1);

    L();
    L('// ---------------------------------------------------------------------------');
    L(`// ${cmdPathStr}`);
    L('// ---------------------------------------------------------------------------');
    L();

    // --- Options interface ---
    L(`export interface ${ifaceName} extends CommonOptions {`);
    // positional args first
    for (const arg of positionalArgs) {
      const required = arg.def.arity?.minimum >= 1;
      L(`  /** ${cleanDesc(arg.def.description)} */`);
      L(`  ${arg.propName}${required ? '' : '?'}: ${tsType(arg.def.valueType)};`);
    }
    // then named options
    for (const opt of opts) {
      const tp = tsType(opt.def.valueType, opt.def.helpName);
      L(`  /** ${cleanDesc(opt.def.description)} */`);
      L(`  ${opt.propName}?: ${tp};`);
    }
    // passthrough args property
    if (passthrough) {
      L(`  /** ${passthrough.description} */`);
      L(`  ${passthrough.propName}?: string[];`);
    }
    L('}');
    L();

    // --- Wrapper function ---
    const defaultArg = hasRequiredArgs ? '' : ' = {}';
    L('/**');
    L(` * ${cleanDesc(cmd.description)}`);
    L(' */');
    L(`export async function ${fnName}(options: ${ifaceName}${defaultArg}): Promise<WinappResult> {`);

    // Build args array
    L(`  const args: string[] = [${cmdPath.map((p) => `'${p}'`).join(', ')}];`);

    // Positional args
    for (const arg of positionalArgs) {
      const required = arg.def.arity?.minimum >= 1;
      if (required) {
        L(`  args.push(options.${arg.propName});`);
      } else {
        L(`  if (options.${arg.propName}) args.push(options.${arg.propName});`);
      }
    }

    // Named options
    for (const opt of opts) {
      if (isBoolFlag(opt.def)) {
        L(`  if (options.${opt.propName}) args.push('${opt.cliName}');`);
      } else if (tsType(opt.def.valueType) === 'number') {
        L(`  if (options.${opt.propName} !== undefined) args.push('${opt.cliName}', options.${opt.propName}.toString());`);
      } else {
        L(`  if (options.${opt.propName}) args.push('${opt.cliName}', options.${opt.propName});`);
      }
    }

    // Passthrough args
    if (passthrough) {
      if (passthrough.separator === ' -- ') {
        L(`  if (options.${passthrough.propName} && options.${passthrough.propName}.length > 0) {`);
        L(`    args.push('--', ...options.${passthrough.propName});`);
        L('  }');
      } else {
        L(`  if (options.${passthrough.propName}) args.push(...options.${passthrough.propName});`);
      }
    }

    L('  return execCommand(args, options);');
    L('}');
  }

  return lines.join('\n') + '\n';
}

// ---------------------------------------------------------------------------
// Function naming: special cases then fallback to camelCase of path
// ---------------------------------------------------------------------------
const FN_NAME_OVERRIDES = {
  'package': 'packageApp', // `package` is a TS reserved-ish word
};

function getFunctionName(cmdPath) {
  const key = cmdPath.join(' ');
  if (FN_NAME_OVERRIDES[key]) return FN_NAME_OVERRIDES[key];

  // e.g. ['cert', 'generate'] → 'certGenerate'
  const name = cmdPath.map((p, i) => (i === 0 ? kebabToCamel(p) : kebabToPascal(p))).join('');
  return TS_RESERVED.has(name) ? name + 'Command' : name;
}

// ---------------------------------------------------------------------------
// Main
// ---------------------------------------------------------------------------
const schema = loadSchema();
const source = generate(schema);

if (checkOnly) {
  if (!existsSync(OUTPUT)) {
    console.error(`[generate-commands] ${OUTPUT} does not exist. Run without --check to generate it.`);
    process.exit(1);
  }
  const existing = readFileSync(OUTPUT, 'utf8');
  if (existing !== source) {
    console.error(
      '[generate-commands] src/winapp-commands.ts is out of date.\n' +
        'Run `npm run generate-commands` to regenerate it.'
    );
    process.exit(1);
  }
  console.log('[generate-commands] src/winapp-commands.ts is up to date.');
} else {
  writeFileSync(OUTPUT, source, 'utf8');
  console.log(`[generate-commands] Generated ${OUTPUT}`);
}
