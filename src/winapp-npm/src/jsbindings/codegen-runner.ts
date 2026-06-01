// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Stage-then-swap keeps previous bindings intact on codegen failure.
// Output directories are wiped, so workspace containment and reparse checks are security-critical.
// spawn receives an args array (not a shell string) so paths with spaces or `&` survive unchanged.

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import * as crypto from 'crypto';
import { spawn } from 'child_process';
import { JS_BINDINGS_OUTPUT_DIR } from './package-json-config';
import { assertSafeWorkspaceOutputDir, isNetworkPath, hasReparsePointOnPath } from './path-safety';

// Authorises later runs to wipe the generated output dir.
export const MANAGED_MARKER_FILE_NAME = '.dynwinrt-managed';

const CODEGEN_PACKAGE_NAME = '@microsoft/dynwinrt-codegen';

/** One cherry-pick pass derived from `additionalWinmds[i]` with namespace+classes. */
export interface CodegenCherryPick {
  /** Omit to rely on dynwinrt-codegen auto-detect (Windows SDK Windows.winmd). */
  winmdPath?: string;
  namespace: string;
  classes: readonly string[];
}

export interface CodegenInputs {
  /** Winmds that generate bindings after policy filtering. */
  emitWinmds: readonly string[];
  /** Winmds loaded for type resolution only. */
  refWinmds: readonly string[];
  /** Per-class generation passes from cherry-picked additionalWinmds. */
  cherryPicks: readonly CodegenCherryPick[];
  workspaceDir: string;
  /** Sink for stdout/stderr lines from the codegen child. */
  log?: (line: string) => void;
  /** false buffers stdout until failure; true streams child output. */
  verbose?: boolean;
}

export interface CodegenSummary {
  classes: number;
  interfaces: number;
  enums: number;
}

export interface CodegenResult {
  outputDir: string;
  /** Aggregated counts parsed from codegen stdout. */
  summary: CodegenSummary;
}

/** Top-level entry point: stage → spawn passes → swap. */
export async function runCodegen(inputs: CodegenInputs): Promise<CodegenResult> {
  const log = inputs.log ?? ((line) => process.stdout.write(line + os.EOL));
  const verbose = inputs.verbose ?? false;

  const outputDir = resolveOutputDir(inputs.workspaceDir);
  fs.mkdirSync(path.dirname(outputDir), { recursive: true });

  const emit = dedupeCaseInsensitive(inputs.emitWinmds);
  // File in both sets wins as emit.
  const refSet = new Set(emit.map((f) => f.toLowerCase()));
  const refs = dedupeCaseInsensitive(inputs.refWinmds.filter((r) => !refSet.has(r.toLowerCase())));

  const { executable, prefixArgs } = resolveCodegenInvocation();
  if (verbose) {
    log(`Using codegen → ${executable} ${prefixArgs.join(' ')}`);
    log(`Codegen inputs: ${emit.length} emit + ${refs.length} ref winmd(s)`);
  }

  const summary: CodegenSummary = { classes: 0, interfaces: 0, enums: 0 };

  await runWithStaging(outputDir, async (stagingDir) => {
    if (emit.length > 0) {
      const args = buildBulkArgs(prefixArgs, emit, stagingDir, refs);
      const stdout = await spawnCodegen(executable, args, inputs.workspaceDir, log, verbose);
      accumulateSummary(summary, parseSummary(stdout));
    }
    for (const cp of inputs.cherryPicks) {
      if (!cp.namespace.trim() || cp.classes.length === 0) {
        continue;
      }
      const args = buildExtraTypeArgs(prefixArgs, emit, stagingDir, refs, cp);
      const stdout = await spawnCodegen(executable, args, inputs.workspaceDir, log, verbose);
      accumulateSummary(summary, parseSummary(stdout));
    }
  });

  return { outputDir, summary };
}

export function resolveOutputDir(workspaceDir: string): string {
  // Fixed output dir; wiped each run, so keep it inside the workspace and reparse-free.
  return assertSafeWorkspaceOutputDir(workspaceDir, JS_BINDINGS_OUTPUT_DIR, 'jsBindings output');
}

/** Throws when outputDir contains files we didn't generate. Empty / missing OK. */
export function validateOutputDirIsWipeable(outputDir: string, sep: string = path.sep): void {
  if (!fs.existsSync(outputDir)) {
    return;
  }

  const stat = fs.lstatSync(outputDir);
  if (stat.isSymbolicLink()) {
    throw new Error(
      `Refusing to wipe '${outputDir}': it is a reparse point (symlink or junction). ` +
        'The wipe could follow the link and delete files outside the workspace. ' +
        'Move the output to a regular directory and try again.'
    );
  }

  const entries = fs.readdirSync(outputDir);
  if (entries.length === 0) {
    return;
  }

  const marker = outputDir + sep + MANAGED_MARKER_FILE_NAME;
  if (!fs.existsSync(marker)) {
    throw new Error(
      `Refusing to wipe non-managed output directory '${outputDir}'. ` +
        `This directory contains files but does not have a '${MANAGED_MARKER_FILE_NAME}' marker, ` +
        'which indicates it was created or modified outside winapp. ' +
        'Move or delete its contents manually if you intended to reuse this path for JS bindings.'
    );
  }

  for (const name of entries) {
    const full = path.join(outputDir, name);
    const child = fs.lstatSync(full);
    if (child.isSymbolicLink()) {
      throw new Error(
        `Refusing to wipe '${outputDir}': child entry '${name}' is a reparse point. ` +
          'Delete it manually before re-running codegen.'
      );
    }
  }
}

function writeManagedMarker(outputDir: string): void {
  fs.mkdirSync(outputDir, { recursive: true });
  const markerPath = path.join(outputDir, MANAGED_MARKER_FILE_NAME);
  const lines = [
    '# Generated by winapp dynwinrt-codegen integration. Do not edit.',
    '# Presence of this file authorises winapp to wipe the directory on the next run.',
    `generated_at: ${new Date().toISOString()}`,
    '',
  ];
  fs.writeFileSync(markerPath, lines.join('\n'), { encoding: 'utf8' });
}

// Generated bindings are ESM. A sub-package.json `{ "type": "module" }` tells Node
// to treat them as such, avoiding the MODULE_TYPELESS_PACKAGE_JSON reparse warning
// (and the perf hit it implies) when they're require()'d. Idempotent: ensures the
// marker even when the pinned dynwinrt-codegen version doesn't emit one itself.
export function ensureEsmPackageMarker(outputDir: string): void {
  fs.mkdirSync(outputDir, { recursive: true });
  const pkgPath = path.join(outputDir, 'package.json');
  let pkg: Record<string, unknown> = {};
  if (fs.existsSync(pkgPath)) {
    try {
      const parsed = JSON.parse(fs.readFileSync(pkgPath, 'utf8'));
      if (parsed && typeof parsed === 'object') {
        pkg = parsed as Record<string, unknown>;
      }
    } catch {
      // Corrupt/non-JSON: overwrite with the minimal marker below.
    }
  }
  if (pkg.type === 'module') {
    return;
  }
  pkg.type = 'module';
  fs.writeFileSync(pkgPath, JSON.stringify(pkg, null, 2) + '\n', { encoding: 'utf8' });
}

/** Stage → backup-old → swap → drop-backup. Visible for tests. */
export async function runWithStaging(
  outputDir: string,
  generate: (stagingDir: string) => Promise<void>
): Promise<void> {
  const parent = path.dirname(outputDir);
  const baseName = path.basename(outputDir);
  const nonce = crypto.randomBytes(8).toString('hex');
  const stagingDir = path.join(parent, `${baseName}.staging.${nonce}`);
  let backupDir: string | null = null;
  let stagingActive = true;

  fs.mkdirSync(stagingDir, { recursive: true });
  try {
    await generate(stagingDir);

    ensureEsmPackageMarker(stagingDir);
    writeManagedMarker(stagingDir);

    validateOutputDirIsWipeable(outputDir);

    if (fs.existsSync(outputDir)) {
      const backupNonce = crypto.randomBytes(8).toString('hex');
      backupDir = path.join(parent, `${baseName}.backup.${backupNonce}`);
      fs.renameSync(outputDir, backupDir);
    }

    try {
      fs.renameSync(stagingDir, outputDir);
      // Don't let finally delete the now-renamed user output.
      stagingActive = false;
    } catch (swapErr) {
      // Restore previous output so the user isn't left empty.
      if (backupDir !== null && fs.existsSync(backupDir)) {
        try {
          fs.renameSync(backupDir, outputDir);
          backupDir = null;
        } catch (restoreErr) {
          // Keep the backup so the user can recover manually.
          const preserved = backupDir;
          backupDir = null;
          throw new Error(
            `Codegen failed AND the previous output could not be restored. ` +
              `Your previous bindings are preserved at: ${preserved}. ` +
              `Move them back manually if needed. Restore error: ${(restoreErr as Error).message}`,
            { cause: restoreErr }
          );
        }
      }
      throw swapErr;
    }
  } finally {
    if (stagingActive) {
      try {
        fs.rmSync(stagingDir, { recursive: true, force: true });
      } catch {
        /* orphan staging is harmless */
      }
    }
    if (backupDir !== null) {
      try {
        fs.rmSync(backupDir, { recursive: true, force: true });
      } catch {
        /* orphan backup is harmless */
      }
    }
  }
}

export function buildBulkArgs(
  prefixArgs: readonly string[],
  emitWinmds: readonly string[],
  outputDir: string,
  refWinmds: readonly string[]
): string[] {
  const args: string[] = [
    ...prefixArgs,
    'generate',
    '--winmd',
    emitWinmds.join(';'),
    '--output',
    outputDir,
    '--lang',
    'js',
  ];
  if (refWinmds.length > 0) {
    args.push('--ref', refWinmds.join(';'));
  }
  return args;
}

export function buildExtraTypeArgs(
  prefixArgs: readonly string[],
  emitWinmds: readonly string[],
  outputDir: string,
  refWinmds: readonly string[],
  extra: CodegenCherryPick
): string[] {
  const args: string[] = [...prefixArgs, 'generate'];
  // refWinmds and emitWinmds are disjoint (runCodegen drops emit from refs).
  const refSet = new Set<string>(refWinmds);

  if (extra.winmdPath) {
    // Explicit winmd: emit the cherry-picked class from it, alongside the bulk
    // emit set so types declared across those winmds resolve.
    const emitSet = new Set<string>(emitWinmds);
    emitSet.add(extra.winmdPath);
    args.push('--winmd', Array.from(emitSet).join(';'));
    refSet.delete(extra.winmdPath);
  } else {
    // Path-less cherry-pick: the user is targeting a class in the SDK's
    // auto-detected Windows.winmd. Passing --winmd here would DISABLE that
    // auto-detection and hide the Windows.* types the class needs. Omit it and
    // expose the NuGet emit winmds via --ref so cross-references still resolve.
    for (const w of emitWinmds) {
      refSet.add(w);
    }
  }

  args.push(
    '--namespace',
    extra.namespace,
    '--class-name',
    extra.classes.join(','),
    '--output',
    outputDir,
    '--lang',
    'js'
  );

  const refs = Array.from(refSet);
  if (refs.length > 0) {
    args.push('--ref', refs.join(';'));
  }
  return args;
}

async function spawnCodegen(
  executable: string,
  args: readonly string[],
  workspaceDir: string,
  log: (line: string) => void,
  verbose: boolean
): Promise<string> {
  return new Promise((resolve, reject) => {
    const child = spawn(executable, args as string[], {
      stdio: ['ignore', 'pipe', 'pipe'],
      cwd: workspaceDir,
      shell: false,
      windowsHide: true,
    });

    const stdoutChunks: Buffer[] = [];
    const stderrChunks: Buffer[] = [];
    child.stdout?.on('data', (c: Buffer) => stdoutChunks.push(c));
    child.stderr?.on('data', (c: Buffer) => stderrChunks.push(c));

    child.on('error', (err) =>
      reject(new Error(`Failed to launch dynwinrt-codegen at '${executable}': ${err.message}`))
    );

    child.on('close', (code) => {
      const stdout = Buffer.concat(stdoutChunks).toString('utf8').trimEnd();
      const stderr = Buffer.concat(stderrChunks).toString('utf8').trimEnd();
      if (code !== 0) {
        if (stdout) {
          log(stdout);
        }
        if (stderr) {
          log(stderr);
        }
        reject(new Error(`dynwinrt-codegen failed (exit ${code ?? 'null'}). See output above for details.`));
        return;
      }
      // Quiet success suppresses codegen's noisy per-file progress; use --verbose for details.
      if (verbose) {
        if (stdout) {
          log(stdout);
        }
        if (stderr) {
          log(stderr);
        }
      }
      resolve(stdout);
    });
  });
}

const SUMMARY_REGEX =
  /Done\.\s+(\d+)\s+class\(es\)\s+\+\s+(\d+)\s+interface\(s\)\s+\+\s+(\d+)\s+enum\(s\)\s+generated/i;

/** Parse the trailing "Done. N class(es) + M interface(s) + K enum(s)" line. */
export function parseSummary(stdout: string): CodegenSummary {
  const summary: CodegenSummary = { classes: 0, interfaces: 0, enums: 0 };
  if (!stdout) {
    return summary;
  }
  // Take the last summary if a multi-pass output reaches this function.
  const re = new RegExp(SUMMARY_REGEX, 'gi');
  let last: RegExpExecArray | null = null;
  for (let match = re.exec(stdout); match !== null; match = re.exec(stdout)) {
    last = match;
  }
  if (last) {
    summary.classes = Number(last[1]);
    summary.interfaces = Number(last[2]);
    summary.enums = Number(last[3]);
  }
  return summary;
}

function accumulateSummary(target: CodegenSummary, add: CodegenSummary): void {
  target.classes += add.classes;
  target.interfaces += add.interfaces;
  target.enums += add.enums;
}

interface CodegenInvocation {
  executable: string;
  prefixArgs: string[];
}

// Preferred resolution order:
//   1. Node module resolution from the wrapper directory for npm, pnpm, yarn classic, and PnP.
//   2. Physical node_modules walking for bundled or patched layouts where require.resolve is stubbed.
export function resolveCodegenInvocation(): CodegenInvocation {
  const wrapperDir = tryGetWrapperDir();
  const arch = resolveArchSubdir();

  const pkgDirs = resolveCodegenPackageDirs(wrapperDir);
  let lastChecked: string | null = null;
  for (const pkgDir of pkgDirs) {
    // Refuse any pkgDir under UNC or with a reparse-point ancestor — a hostile
    // npm install layout (junction'd node_modules) could redirect us to a
    // victim binary.
    if (isNetworkPath(pkgDir) || hasReparsePointOnPath(pkgDir, path.parse(pkgDir).root || pkgDir)) {
      continue;
    }
    // Prefer the pre-built .exe; cli.js is a defensive fallback.
    const exePath = path.join(pkgDir, 'bin', arch, 'dynwinrt-codegen.exe');
    if (fs.existsSync(exePath)) {
      return { executable: exePath, prefixArgs: [] };
    }

    // Prefer process.execPath for cli.js so a poisoned PATH cannot substitute node.exe.
    const cliJs = path.join(pkgDir, 'cli.js');
    if (fs.existsSync(cliJs)) {
      const nodePath = resolveTrustedNodeInterpreter();
      if (!nodePath) {
        throw new Error(
          `The codegen at '${cliJs}' requires a native Node.js executable (node.exe) on PATH. ` +
            'Install Node 18+ (winget install OpenJS.NodeJS) ' +
            `or reinstall ${CODEGEN_PACKAGE_NAME} so the pre-built .exe is available.`
        );
      }
      return { executable: nodePath, prefixArgs: [cliJs] };
    }
    lastChecked = pkgDir;
  }

  const wrapperHint = wrapperDir
    ? ` at '${wrapperDir}'`
    : ' (winapp install directory could not be determined; try reinstalling @microsoft/winappcli)';
  const partialHint = lastChecked
    ? `Found ${CODEGEN_PACKAGE_NAME} at '${lastChecked}' but no executable inside ` +
      `(expected 'bin/${arch}/dynwinrt-codegen.exe' or 'cli.js'). ` +
      'The npm package may be corrupt; reinstall it.\n\n'
    : `Searched ${CODEGEN_PACKAGE_NAME} from the wrapper install${wrapperHint} — ` +
      `no ${CODEGEN_PACKAGE_NAME} resolvable via Node module resolution.\n\n`;

  throw new Error(
    partialHint +
      'To enable JS bindings, install via your package manager:\n' +
      '  npm i -D @microsoft/winappcli\n' +
      `(bundles ${CODEGEN_PACKAGE_NAME} as a transitive dependency.)\n\n` +
      'pnpm and yarn (classic / Berry / PnP) are supported via Node module resolution.\n\n' +
      'See https://github.com/microsoft/WinAppCli#electron--nodejs for setup details.'
  );
}

/**
 * Yield package dirs via Node resolution, then physical node_modules fallback.
 * The iterator stops once the caller fully validates the first usable package.
 */
function* resolveCodegenPackageDirs(wrapperDir: string | null): Generator<string> {
  const seen = new Set<string>();

  const yieldUnique = function* (dir: string | null): Generator<string> {
    if (!dir) return;
    const key = dir.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    yield dir;
  };

  // Node's resolver handles hoisted, isolated, and PnP package layouts.
  yield* yieldUnique(resolveViaRequireResolve(wrapperDir));

  // Physical walk preserves the legacy fallback for patched/bundled installs.
  for (let probe: string | null = wrapperDir; probe; probe = parentOrNull(probe)) {
    const pkgDir = path.join(probe, 'node_modules', '@microsoft', 'dynwinrt-codegen');
    if (fs.existsSync(pkgDir)) {
      yield* yieldUnique(pkgDir);
    }
  }
}

function resolveViaRequireResolve(wrapperDir: string | null): string | null {
  const searchPaths: string[] = [];
  if (wrapperDir) searchPaths.push(wrapperDir);
  // Global installs still need to resolve the bundled codegen.
  searchPaths.push(__dirname);

  try {
    const pkgJson = require.resolve(`${CODEGEN_PACKAGE_NAME}/package.json`, { paths: searchPaths });
    // Reject PnP virtual `.zip!/` paths by requiring a real parent directory.
    const pkgDir = path.dirname(pkgJson);
    if (fs.existsSync(pkgDir)) {
      return pkgDir;
    }
  } catch {
    // require.resolve throws on no-match; treat as "not installed".
  }
  return null;
}

function parentOrNull(dir: string): string | null {
  const parent = path.dirname(dir);
  return parent === dir ? null : parent;
}

// Walk up from dist/src jsbindings until package.json names @microsoft/winappcli.
function tryGetWrapperDir(): string | null {
  let dir = __dirname;
  const root = path.parse(dir).root;
  for (;;) {
    const candidate = path.join(dir, 'package.json');
    if (fs.existsSync(candidate)) {
      try {
        const parsed = JSON.parse(fs.readFileSync(candidate, 'utf8')) as Record<string, unknown>;
        if (parsed.name === '@microsoft/winappcli') {
          return dir;
        }
      } catch {
        /* keep walking */
      }
    }
    if (dir === root) {
      return null;
    }
    const parent = path.dirname(dir);
    if (parent === dir) {
      return null;
    }
    dir = parent;
  }
}

function resolveArchSubdir(): string {
  return os.arch() === 'arm64' ? 'arm64' : 'x64';
}

// Trust process.execPath first because npm selected it to load this wrapper.
// It still must be a native .exe/.com with no UNC or reparse-point ancestor.
// PATH fallback is safety-gated and excludes .bat/.cmd re-parsing.
function resolveTrustedNodeInterpreter(): string | null {
  const execPath = process.execPath;
  if (execPath && isAcceptableNodeExe(execPath)) {
    return execPath;
  }
  return resolveNativeNodeOnPath();
}

function isAcceptableNodeExe(candidate: string): boolean {
  if (!candidate) {
    return false;
  }
  // Anchor on the drive root so arbitrary system paths are scanned for junctions.
  // This covers paths like `C:\Program Files\nodejs\node.exe`, not just workspace paths.
  if (isNetworkPath(candidate)) {
    return false;
  }
  const ext = path.extname(candidate).toLowerCase();
  if (ext !== '.exe' && ext !== '.com') {
    return false;
  }
  let resolved: string;
  try {
    resolved = path.resolve(candidate);
  } catch {
    return false;
  }
  if (!fs.existsSync(resolved)) {
    return false;
  }
  const driveRoot = path.parse(resolved).root.replace(/[\\/]+$/, '');
  if (driveRoot && hasReparsePointOnPath(resolved, driveRoot)) {
    return false;
  }
  return true;
}

// PATH fallback rejects relative/CWD/UNC/reparse-backed candidates and .bat/.cmd shims.
// That prevents an attacker-controlled PATH entry from running cli.js.
function resolveNativeNodeOnPath(): string | null {
  const command = 'node';
  const pathEnv = process.env.PATH ?? '';
  const dirs = pathEnv.split(path.delimiter).filter((d) => d.length > 0);
  const cwdFull = (() => {
    try {
      return path.resolve(process.cwd());
    } catch {
      return null;
    }
  })();

  for (const dirRaw of dirs) {
    const dir = dirRaw.replace(/^"|"$/g, '').trim();
    if (!dir || dir === '.' || !path.isAbsolute(dir)) {
      continue;
    }
    if (isNetworkPath(dir)) {
      continue;
    }
    let resolvedDir: string;
    try {
      resolvedDir = path.resolve(dir);
    } catch {
      continue;
    }
    if (cwdFull && resolvedDir.toLowerCase() === cwdFull.toLowerCase()) {
      continue;
    }
    for (const ext of ['.exe', '.com']) {
      const candidate = path.join(resolvedDir, command + ext);
      if (fs.existsSync(candidate) && isAcceptableNodeExe(candidate)) {
        return candidate;
      }
    }
    const bare = path.join(resolvedDir, command);
    if (fs.existsSync(bare) && isAcceptableNodeExe(bare)) {
      return bare;
    }
  }
  return null;
}

function dedupeCaseInsensitive(items: readonly string[]): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const item of items) {
    const k = item.toLowerCase();
    if (!seen.has(k)) {
      seen.add(k);
      out.push(item);
    }
  }
  return out;
}
