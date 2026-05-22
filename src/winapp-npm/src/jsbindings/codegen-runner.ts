// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Spawns @microsoft/dynwinrt-codegen against discovered .winmd metadata,
// using a stage-then-swap pattern so a partial failure leaves the previous
// output intact.
//
// Ported from C# `DynWinrtCodegenService.cs`. Key invariants preserved:
//   * Resolve output dir with strict workspace containment + reparse-point
//     refusal — the directory is wiped before each run, so we must never
//     follow a junction that points outside the workspace.
//   * Refuse to wipe a non-empty output directory without our managed marker
//     (`.dynwinrt-managed`); the user may have aimed the path at real files.
//   * Stage in a sibling dir, then atomic-rename swap with backup/restore on
//     failure so a kill mid-rename can't leave the user with no bindings.
//   * Use ArgumentList-equivalent (spawn args array) to avoid shell quoting
//     pitfalls — paths with spaces or `&` must pass through unchanged.

import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import * as crypto from 'crypto';
import { spawn } from 'child_process';
import { JsBindingsConfig } from './package-json-config';
import { assertSafeWorkspaceOutputDir, isNetworkPath, hasReparsePointOnPath } from './path-safety';

// Marker written into the output dir after a successful run; its presence
// authorises the next run to wipe the dir.
export const MANAGED_MARKER_FILE_NAME = '.dynwinrt-managed';

const CODEGEN_PACKAGE_NAME = '@microsoft/dynwinrt-codegen';

/** One cherry-pick pass derived from `additionalWinmds[i]` with namespace+classes. */
export interface CodegenCherryPick {
  namespace: string;
  classes: readonly string[];
}

export interface CodegenInputs {
  config: JsBindingsConfig;
  /** Emit winmds (after winmd-policy filtering + bulk additionalWinmds entries). */
  emitWinmds: readonly string[];
  /** Ref-only winmds (load for type resolution, don't generate bindings). */
  refWinmds: readonly string[];
  /** Cherry-pick passes — each runs codegen once with `--namespace` + `--class-name` filters. */
  cherryPicks: readonly CodegenCherryPick[];
  workspaceDir: string;
  /** A logger sink for stdout/stderr lines from the codegen child. */
  log?: (line: string) => void;
  /**
   * When false (default), child stdout is buffered and only printed on failure;
   * stderr is always forwarded. When true, stream stdout/stderr line-by-line.
   */
  verbose?: boolean;
}

export interface CodegenSummary {
  classes: number;
  interfaces: number;
  enums: number;
}

export interface CodegenResult {
  outputDir: string;
  /** Aggregated counts parsed from codegen stdout. Zeros if not detected. */
  summary: CodegenSummary;
}

/** Top-level entry point: stage → spawn passes → swap. */
export async function runCodegen(inputs: CodegenInputs): Promise<CodegenResult> {
  const log = inputs.log ?? ((line) => process.stdout.write(line + os.EOL));
  const verbose = inputs.verbose ?? false;

  const outputDir = resolveOutputDir(inputs.workspaceDir, inputs.config.output);
  fs.mkdirSync(path.dirname(outputDir), { recursive: true });

  const emit = dedupeCaseInsensitive(inputs.emitWinmds);
  // Drop refs that are already in emit (file in both wins as emit).
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

// ---- output dir resolution + safety ---------------------------------------

export function resolveOutputDir(workspaceDir: string, output: string): string {
  // Single source of truth for "this directory will be wiped before each
  // codegen run" safety policy: must be UNC-free, strictly inside the
  // workspace, and reparse-point-free along the entire path. Mirrors
  // PathSafety guards on the native side.
  const out = output && output.trim() ? output : 'bindings';
  return assertSafeWorkspaceOutputDir(workspaceDir, out, 'jsBindings.output');
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

// ---- staging + swap --------------------------------------------------------

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

    writeManagedMarker(stagingDir);

    validateOutputDirIsWipeable(outputDir);

    if (fs.existsSync(outputDir)) {
      const backupNonce = crypto.randomBytes(8).toString('hex');
      backupDir = path.join(parent, `${baseName}.backup.${backupNonce}`);
      fs.renameSync(outputDir, backupDir);
    }

    try {
      fs.renameSync(stagingDir, outputDir);
      // Don't let the finally block target the now-renamed staging dir
      // (which IS the user's new output).
      stagingActive = false;
    } catch (swapErr) {
      // Restore previous output so the user isn't left empty.
      if (backupDir !== null && fs.existsSync(backupDir)) {
        try {
          fs.renameSync(backupDir, outputDir);
          backupDir = null;
        } catch (restoreErr) {
          // Preserve the backup on disk and surface the path so the user
          // can recover manually. Null the local so finally won't delete it.
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

// ---- argv builders ---------------------------------------------------------

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
  if (emitWinmds.length > 0) {
    args.push('--winmd', emitWinmds.join(';'));
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
  if (refWinmds.length > 0) {
    args.push('--ref', refWinmds.join(';'));
  }
  return args;
}

// ---- spawn -----------------------------------------------------------------

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
        // On failure, always surface both streams so the user can diagnose.
        if (stdout) {
          log(stdout);
        }
        if (stderr) {
          log(stderr);
        }
        reject(new Error(`dynwinrt-codegen failed (exit ${code ?? 'null'}). See output above for details.`));
        return;
      }
      // Success: in quiet mode, swallow both streams. dynwinrt-codegen emits
      // per-file "Generated …" lines plus a "Discovered N namespace(s)" dump
      // (some via stderr as progress) that drown out the orchestrator's own
      // single-line success summary. Users who need the detail can pass
      // `--verbose` / `-v` (handleInit / handleRestore in cli.ts).
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

// ---- summary parsing -------------------------------------------------------

const SUMMARY_REGEX =
  /Done\.\s+(\d+)\s+class\(es\)\s+\+\s+(\d+)\s+interface\(s\)\s+\+\s+(\d+)\s+enum\(s\)\s+generated/i;

/** Parse the trailing "Done. N class(es) + M interface(s) + K enum(s)" line. */
export function parseSummary(stdout: string): CodegenSummary {
  const summary: CodegenSummary = { classes: 0, interfaces: 0, enums: 0 };
  if (!stdout) {
    return summary;
  }
  // Codegen may emit one summary per pass; take the last one in case of
  // multi-pass output reaching this function (defensive — currently each
  // spawn is its own pass).
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

// ---- executable resolution -------------------------------------------------

interface CodegenInvocation {
  executable: string;
  prefixArgs: string[];
}

// Locate dynwinrt-codegen. Preferred resolution order:
//   1. `require.resolve('@microsoft/dynwinrt-codegen/package.json')` anchored
//      at the wrapper directory — this is the canonical Node module-resolver,
//      so it works with hoisted node_modules (npm / yarn-classic),
//      pnpm-default's symlinked layout, and yarn-Berry PnP.
//   2. Physical node_modules walk — defensive fallback for the rare case
//      where the wrapper is loaded via something that breaks
//      `require.resolve` (e.g., custom bundler with frozen paths).
//
// Workspace-local installs are still preferred (a wrapper installed under
// the user's workspace co-locates the codegen there), and we only trust
// `cli.js` at a real on-disk path that we can lstat — so PnP's virtual
// `.zip!/` paths are converted to an unzipped on-disk location by Node
// itself before we read them.
export function resolveCodegenInvocation(): CodegenInvocation {
  const wrapperDir = tryGetWrapperDir();
  const arch = resolveArchSubdir();

  const pkgDirs = resolveCodegenPackageDirs(wrapperDir);
  let lastChecked: string | null = null;
  for (const pkgDir of pkgDirs) {
    // Priority 1: pre-built .exe (no Node startup needed).
    const exePath = path.join(pkgDir, 'bin', arch, 'dynwinrt-codegen.exe');
    if (fs.existsSync(exePath)) {
      return { executable: exePath, prefixArgs: [] };
    }

    // Priority 2: cli.js via node — defensive fallback. Prefer the current
    // wrapper's own interpreter (`process.execPath`) over PATH lookup so a
    // poisoned PATH (UNC entry, reparse junction, attacker-controlled dir)
    // can't substitute a hostile node.exe for cli.js execution. We still
    // walk PATH as a last resort for the unusual case where the wrapper is
    // launched from a non-node interpreter (e.g. an `.exe` shim).
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
 * Build the list of candidate `@microsoft/dynwinrt-codegen` package
 * directories. Iterates lazily so we stop as soon as the first match has
 * been fully validated by the caller.
 *
 * Order:
 *   * Anchored `require.resolve` from the wrapper dir. Honors all linkers
 *     (hoisted, isolated, PnP) because it goes through Node's own resolver.
 *   * Physical `node_modules/@microsoft/dynwinrt-codegen` walk from the
 *     wrapper dir upward — same as the legacy behaviour, kept as a safety
 *     net for bundled / patched layouts where `require.resolve` is stubbed.
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

  // Strategy 1: Node module resolution (PnP / pnpm / npm / yarn-classic).
  yield* yieldUnique(resolveViaRequireResolve(wrapperDir));

  // Strategy 2: physical node_modules walk from the wrapper dir upward.
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
  // Always include the wrapper module's own directory so global installs
  // (`npm i -g @microsoft/winappcli`) still resolve the bundled codegen.
  searchPaths.push(__dirname);

  try {
    const pkgJson = require.resolve(`${CODEGEN_PACKAGE_NAME}/package.json`, { paths: searchPaths });
    // pkgJson should be a real on-disk file path. PnP can return virtual
    // paths inside `.zip!/`; reject those by requiring the parent dir to
    // exist on disk.
    const pkgDir = path.dirname(pkgJson);
    if (fs.existsSync(pkgDir)) {
      return pkgDir;
    }
  } catch {
    // Not resolvable from any anchor — fall through to the physical walk.
  }
  return null;
}

function parentOrNull(dir: string): string | null {
  const parent = path.dirname(dir);
  return parent === dir ? null : parent;
}

// Locate the winapp-npm install directory by walking from __dirname (dist/jsbindings/
// in prod, src/jsbindings/ in test/dev) up until we find a package.json named
// @microsoft/winappcli.
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

// Locate a trusted node.exe to run `cli.js`. Priority:
//   1. `process.execPath` — the interpreter currently executing this wrapper.
//      That's the same node we just used to load this very module, so its
//      provenance is implicitly trusted (the npm package manager picked it).
//      We still verify it is a `.exe` / `.com` and that no segment of the
//      path is a reparse point / UNC — defends against `npm` being launched
//      via a junction into a hostile share.
//   2. PATH walk — fallback for the rare case where the wrapper is bundled
//      into an `.exe` shim and `process.execPath` doesn't point at a Node
//      interpreter. Each PATH candidate must pass the same safety gate.
//
// Rejects `.bat` / `.cmd` because those dispatch through `cmd.exe` and would
// re-parse user-derived args.
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
  // UNC / network paths: reject. Workspace-style reparse-point walk needs a
  // boundary; for arbitrary system paths (`C:\Program Files\nodejs\…`) we
  // anchor on the candidate's drive root so the entire path is scanned for
  // reparse junctions.
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

// Walk PATH looking for node.exe / node.com. Rejects relative PATH entries,
// drops CWD-equivalent entries, refuses UNC / reparse-backed candidates,
// and only accepts native .exe/.com (no .bat/.cmd).
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
