// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Runs dynwinrt-codegen with stage/swap output safety and temp winmd/ref list files.

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
const RUNTIME_PACKAGE_NAME = '@microsoft/dynwinrt';
const GENERATE_REQUIRED_CODEGEN_CAPABILITIES = ['input.winmd-list', 'input.ref-list'] as const;
const RUNTIME_REQUIRED_CODEGEN_CAPABILITIES = ['runtime-dependency'] as const;

export interface RuntimeDependencySpec {
  packageName: string;
  version: string;
}

/** One cherry-pick pass derived from `additionalWinmds[i]` with namespace+classes. */
export interface CodegenCherryPick {
  /** Omit when the picked type should resolve from refs or codegen's fallback metadata. */
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

export interface CodegenResult {
  outputDir: string;
}

/** Top-level entry point: stage → spawn passes → swap. */
export async function runCodegen(inputs: CodegenInputs): Promise<CodegenResult> {
  const log = inputs.log ?? ((line) => process.stdout.write(line + os.EOL));
  const verbose = inputs.verbose ?? false;

  const outputDir = resolveOutputDir(inputs.workspaceDir);
  fs.mkdirSync(path.dirname(outputDir), { recursive: true });

  const emit = dedupeCaseInsensitive(inputs.emitWinmds);
  // File in both sets wins as emit.
  const emitSet = new Set(emit.map((f) => f.toLowerCase()));
  const refs = dedupeCaseInsensitive(inputs.refWinmds.filter((r) => !emitSet.has(r.toLowerCase())));

  const { executable, prefixArgs } = resolveCodegenInvocation(inputs.workspaceDir);
  await assertCodegenCapabilities(executable, prefixArgs, inputs.workspaceDir, GENERATE_REQUIRED_CODEGEN_CAPABILITIES);
  if (verbose) {
    log(`Using codegen → ${executable} ${prefixArgs.join(' ')}`);
    log(`Codegen inputs: ${emit.length} emit + ${refs.length} ref winmd(s)`);
  }

  // winmd/ref paths are passed via newline-separated list files rather than ';'-joined
  // argv: there can be 100+ scattered paths, well past the Windows command-line limit.
  // The temp dir holds those lists and is removed in `finally`, even on codegen failure.
  const listDir = fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-codegen-'));
  try {
    await runWithStaging(outputDir, async (stagingDir) => {
      let pass = 0;
      if (emit.length > 0) {
        const args = buildBulkArgs(prefixArgs, emit, stagingDir, refs, listDir, pass++);
        await spawnCodegen(executable, args, inputs.workspaceDir, log, verbose);
      }
      for (const cp of inputs.cherryPicks) {
        if (!cp.namespace.trim() || cp.classes.length === 0) {
          continue;
        }
        const args = buildCherryPickArgs(prefixArgs, emit, stagingDir, refs, cp, listDir, pass++);
        await spawnCodegen(executable, args, inputs.workspaceDir, log, verbose);
      }
    });
  } finally {
    try {
      fs.rmSync(listDir, { recursive: true, force: true });
    } catch {
      /* orphan temp list dir is harmless */
    }
  }

  return { outputDir };
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

/** Write a newline-separated list file of winmd paths; returns its path. */
export function writeListFile(listDir: string, name: string, paths: readonly string[]): string {
  const filePath = path.join(listDir, name);
  // Trailing newline keeps the file POSIX-friendly; codegen trims each line.
  fs.writeFileSync(filePath, paths.join('\n') + '\n', { encoding: 'utf8' });
  return filePath;
}

/**
 * Partition cherry-pick inputs into emit roots and type-resolution refs.
 */
export function computeCherryPickInputs(
  emitWinmds: readonly string[],
  refWinmds: readonly string[],
  cp: CodegenCherryPick
): { winmds: string[]; refs: string[] } {
  if (cp.winmdPath) {
    const pickPath = cp.winmdPath;
    return {
      winmds: dedupeCaseInsensitive([...emitWinmds, pickPath]),
      refs: dedupeCaseInsensitive(refWinmds.filter((r) => !samePathCaseInsensitive(r, pickPath))),
    };
  }
  // No root winmd: keep the bulk emit metadata available for type resolution only.
  return { winmds: [], refs: dedupeCaseInsensitive([...refWinmds, ...emitWinmds]) };
}

interface GenerateArgsOptions {
  winmdListPath?: string | null;
  refListPath?: string | null;
  namespace?: string;
  classes?: readonly string[];
  outputDir: string;
}

/** Assemble a `generate` argv from list-file paths and optional cherry-pick selectors. */
export function buildGenerateArgs(prefixArgs: readonly string[], opts: GenerateArgsOptions): string[] {
  const args: string[] = [...prefixArgs, 'generate'];
  if (opts.winmdListPath) {
    args.push('--winmd-list', opts.winmdListPath);
  }
  if (opts.namespace) {
    args.push('--namespace', opts.namespace);
  }
  if (opts.classes && opts.classes.length > 0) {
    args.push('--class-name', opts.classes.join(','));
  }
  args.push('--output', opts.outputDir);
  if (opts.refListPath) {
    args.push('--ref-list', opts.refListPath);
  }
  return args;
}

export function buildBulkArgs(
  prefixArgs: readonly string[],
  emitWinmds: readonly string[],
  outputDir: string,
  refWinmds: readonly string[],
  listDir: string,
  passId: number
): string[] {
  const winmdListPath = emitWinmds.length > 0 ? writeListFile(listDir, `emit-${passId}.txt`, emitWinmds) : null;
  const refListPath = refWinmds.length > 0 ? writeListFile(listDir, `ref-${passId}.txt`, refWinmds) : null;
  return buildGenerateArgs(prefixArgs, { winmdListPath, refListPath, outputDir });
}

export function buildCherryPickArgs(
  prefixArgs: readonly string[],
  emitWinmds: readonly string[],
  outputDir: string,
  refWinmds: readonly string[],
  extra: CodegenCherryPick,
  listDir: string,
  passId: number
): string[] {
  const { winmds, refs } = computeCherryPickInputs(emitWinmds, refWinmds, extra);
  const winmdListPath = winmds.length > 0 ? writeListFile(listDir, `emit-${passId}.txt`, winmds) : null;
  const refListPath = refs.length > 0 ? writeListFile(listDir, `ref-${passId}.txt`, refs) : null;
  return buildGenerateArgs(prefixArgs, {
    winmdListPath,
    refListPath,
    namespace: extra.namespace,
    classes: extra.classes,
    outputDir,
  });
}

async function spawnCodegen(
  executable: string,
  args: readonly string[],
  workspaceDir: string,
  log: (line: string) => void,
  verbose: boolean
): Promise<void> {
  try {
    const { stdout, stderr } = await spawnCodegenCapture(executable, args, workspaceDir);
    // Quiet success suppresses codegen's noisy per-file progress; use --verbose for details.
    if (verbose) {
      if (stdout) {
        log(stdout);
      }
      if (stderr) {
        log(stderr);
      }
    }
  } catch (err) {
    if (err instanceof CodegenExitError) {
      if (err.stdout) {
        log(err.stdout);
      }
      if (err.stderr) {
        log(err.stderr);
      }
      throw new Error(`${err.message}. See output above for details.`, { cause: err });
    }
    throw err;
  }
}

interface CodegenProcessOutput {
  stdout: string;
  stderr: string;
}

class CodegenExitError extends Error {
  constructor(
    public readonly code: number | null,
    public readonly stdout: string,
    public readonly stderr: string
  ) {
    super(`dynwinrt-codegen failed (exit ${code ?? 'null'})`);
  }
}

async function spawnCodegenCapture(
  executable: string,
  args: readonly string[],
  workspaceDir: string
): Promise<CodegenProcessOutput> {
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
        reject(new CodegenExitError(code, stdout, stderr));
        return;
      }
      resolve({ stdout, stderr });
    });
  });
}

export function parseCapabilitiesOutput(stdout: string): Set<string> {
  return new Set(
    stdout
      .split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line.length > 0 && !line.startsWith('#'))
  );
}

export function parseRuntimeDependencySpec(stdout: string): RuntimeDependencySpec {
  const spec = stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .find((line) => line.length > 0);
  if (!spec) {
    throw new Error('dynwinrt-codegen runtime-dependency returned no output.');
  }

  const sep = spec.lastIndexOf('@');
  if (sep <= 0 || sep === spec.length - 1) {
    throw new Error(`Invalid dynwinrt-codegen runtime dependency spec: '${spec}'.`);
  }

  const packageName = spec.slice(0, sep);
  const version = spec.slice(sep + 1);
  if (packageName !== RUNTIME_PACKAGE_NAME) {
    throw new Error(
      `dynwinrt-codegen returned runtime dependency '${packageName}', expected '${RUNTIME_PACKAGE_NAME}'.`
    );
  }
  return { packageName, version };
}

async function runCodegenQuery(
  executable: string,
  prefixArgs: readonly string[],
  workspaceDir: string,
  command: string
): Promise<string> {
  try {
    const { stdout } = await spawnCodegenCapture(executable, [...prefixArgs, command], workspaceDir);
    return stdout;
  } catch (err) {
    if (err instanceof CodegenExitError) {
      const details = [err.stderr, err.stdout].filter(Boolean).join(os.EOL);
      throw new Error(
        `dynwinrt-codegen ${command} failed (exit ${err.code ?? 'null'}).` + (details ? `${os.EOL}${details}` : ''),
        { cause: err }
      );
    }
    throw err;
  }
}

async function assertCodegenCapabilities(
  executable: string,
  prefixArgs: readonly string[],
  workspaceDir: string,
  requiredCapabilities: readonly string[]
): Promise<void> {
  let stdout: string;
  try {
    stdout = await runCodegenQuery(executable, prefixArgs, workspaceDir, 'capabilities');
  } catch (err) {
    throw new Error(
      `Installed ${CODEGEN_PACKAGE_NAME} does not support required capability negotiation. ` +
        `Upgrade ${CODEGEN_PACKAGE_NAME} (or reinstall @microsoft/winappcli). ` +
        `Details: ${(err as Error).message}`,
      { cause: err }
    );
  }

  const capabilities = parseCapabilitiesOutput(stdout);
  const missing = requiredCapabilities.filter((capability) => !capabilities.has(capability));
  if (missing.length > 0) {
    throw new Error(
      `Installed ${CODEGEN_PACKAGE_NAME} is missing required capabilities: ${missing.join(', ')}. ` +
        `Upgrade ${CODEGEN_PACKAGE_NAME} (or reinstall @microsoft/winappcli).`
    );
  }
}

export async function getCodegenRuntimeDependency(workspaceDir: string): Promise<RuntimeDependencySpec> {
  const { executable, prefixArgs } = resolveCodegenInvocation(workspaceDir);
  await assertCodegenCapabilities(executable, prefixArgs, workspaceDir, RUNTIME_REQUIRED_CODEGEN_CAPABILITIES);
  const stdout = await runCodegenQuery(executable, prefixArgs, workspaceDir, 'runtime-dependency');
  return parseRuntimeDependencySpec(stdout);
}

export function getCodegenPackageVersion(workspaceDir: string): string | null {
  const { packageDir } = resolveCodegenInvocation(workspaceDir);
  const packageJsonPath = path.join(packageDir, 'package.json');
  const parsed = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8')) as { version?: unknown };
  return typeof parsed.version === 'string' ? parsed.version : null;
}

export function supportsPackageImports(version: string | null): boolean {
  if (!version) {
    return false;
  }

  const match = /^(\d+)\.(\d+)\.(\d+)(?:-([^+]+))?(?:\+.*)?$/.exec(version);
  if (!match) {
    return false;
  }

  const current = match.slice(1, 4).map(Number);
  const minimum = [0, 1, 0];
  for (let i = 0; i < minimum.length; i++) {
    if (current[i] !== minimum[i]) {
      return current[i] > minimum[i];
    }
  }

  const prerelease = match[4];
  if (!prerelease) {
    return true;
  }

  // Only `preview.N` is recognized today because that's the channel dynwinrt-codegen
  // ships on. Dual CJS/ESM output — required by the `#winapp/bindings` imports map —
  // landed in preview.8. If codegen ever switches to `rc.N`/`beta.N`/etc., extend
  // this to accept those tags too (any recognized prerelease with N ≥ 8 → true).
  const preview = /^preview\.(\d+)$/.exec(prerelease);
  return preview !== null && Number(preview[1]) >= 8;
}

interface CodegenInvocation {
  executable: string;
  prefixArgs: string[];
  packageDir: string;
}

/** Resolve via Node first (npm/pnpm/yarn/PnP), then physical node_modules for patched layouts. */
export function resolveCodegenInvocation(workspaceDir?: string): CodegenInvocation {
  const wrapperDir = tryGetWrapperDir();

  const pkgDirs = resolveCodegenPackageDirs(workspaceDir ?? null, wrapperDir);
  let lastChecked: string | null = null;
  for (const pkgDir of pkgDirs) {
    // Refuse UNC/reparse-backed pkgDirs; junctioned node_modules could redirect to a victim binary.
    if (isNetworkPath(pkgDir) || hasReparsePointOnPath(pkgDir, path.parse(pkgDir).root || pkgDir)) {
      continue;
    }
    // Invoke the package CLI, not the internal exe, so npm-package-owned behavior
    // (output package.json markers, runtime version reporting) stays inside
    // @microsoft/dynwinrt-codegen instead of being duplicated here.
    const cliJs = path.join(pkgDir, 'cli.js');
    if (fs.existsSync(cliJs)) {
      if (hasReparsePointOnPath(cliJs, path.parse(cliJs).root || pkgDir)) {
        continue;
      }
      const nodePath = resolveTrustedNodeInterpreter();
      if (!nodePath) {
        throw new Error(
          `The codegen at '${cliJs}' requires a native Node.js executable (node.exe) on PATH. ` +
            'Install Node 18+ (winget install OpenJS.NodeJS) ' +
            `or reinstall ${CODEGEN_PACKAGE_NAME}.`
        );
      }
      return { executable: nodePath, prefixArgs: [cliJs], packageDir: pkgDir };
    }
    lastChecked = pkgDir;
  }

  const wrapperHint = wrapperDir
    ? ` at '${wrapperDir}'`
    : ' (winapp install directory could not be determined; try reinstalling @microsoft/winappcli)';
  const partialHint = lastChecked
    ? `Found ${CODEGEN_PACKAGE_NAME} at '${lastChecked}' but no cli.js entry point. ` +
      'The npm package may be corrupt; reinstall it.\n\n'
    : `Searched ${CODEGEN_PACKAGE_NAME} from the wrapper install${wrapperHint} — ` +
      `no ${CODEGEN_PACKAGE_NAME} resolvable via Node module resolution.\n\n`;

  throw new Error(
    partialHint +
      'To enable JS bindings, install codegen via your package manager:\n' +
      `  npm i -D @microsoft/dynwinrt-codegen\n` +
      `(or re-run \`npx winapp init . --add-js-bindings\`, which installs it automatically).\n\n` +
      'pnpm and yarn (classic / Berry / PnP) are supported via Node module resolution.\n\n' +
      'See https://github.com/microsoft/WinAppCli#electron--nodejs for setup details.'
  );
}

/** Yield package dirs via Node resolution, then physical node_modules fallback. */
function* resolveCodegenPackageDirs(workspaceDir: string | null, wrapperDir: string | null): Generator<string> {
  const seen = new Set<string>();

  const yieldUnique = function* (dir: string | null): Generator<string> {
    if (!dir) return;
    const key = dir.toLowerCase();
    if (seen.has(key)) return;
    seen.add(key);
    yield dir;
  };

  // Node's resolver handles hoisted, isolated, and PnP package layouts. Workspace
  // wins so global/npx installs find user-project devDeps before falling back.
  yield* yieldUnique(resolveViaRequireResolve(workspaceDir, wrapperDir));

  // Physical walk preserves the legacy fallback for patched/bundled installs.
  for (const start of [workspaceDir, wrapperDir]) {
    for (let probe: string | null = start; probe; probe = parentOrNull(probe)) {
      const pkgDir = path.join(probe, 'node_modules', '@microsoft', 'dynwinrt-codegen');
      if (fs.existsSync(pkgDir)) {
        yield* yieldUnique(pkgDir);
      }
    }
  }
}

function resolveViaRequireResolve(workspaceDir: string | null, wrapperDir: string | null): string | null {
  const searchPaths: string[] = [];
  // Workspace first: user-project devDep should win over a wrapper-bundled copy.
  if (workspaceDir) searchPaths.push(workspaceDir);
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

// Walk up from dist/src jsbindings to the @microsoft/winappcli package root.
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

// Trust npm-selected process.execPath first; still require native .exe/.com and no UNC/reparse.
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
  // Anchor on the drive root so system paths (not just workspace paths) are scanned for junctions.
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
  const driveRoot = path.parse(resolved).root;
  if (driveRoot && hasReparsePointOnPath(resolved, driveRoot)) {
    return false;
  }
  return true;
}

// PATH fallback rejects relative/CWD/UNC/reparse-backed candidates and .bat/.cmd shims,
// preventing attacker-controlled PATH entries from running cli.js.
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

function samePathCaseInsensitive(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase();
}
