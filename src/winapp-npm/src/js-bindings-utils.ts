import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { execFileSync } from 'child_process';
import { getProjectRootDir } from './utils';

// ======================================================================
// Types
// ======================================================================

export interface JsBindingsPackageEntry {
  name: string;
  version?: string;
}

export interface JsBindingsConfig {
  lang: string;
  output: string;
  packages: JsBindingsPackageEntry[];
  systemTypes: SystemTypeEntry[];
}

export interface SystemTypeEntry {
  namespace: string;
  classes: string;
}

export interface GenerateJsBindingsOptions {
  /** Project root directory (default: auto-detected from cwd) */
  projectRoot?: string;
  /** Path to winapp.yaml (default: <projectRoot>/winapp.yaml) */
  configPath?: string;
  /** Enable verbose output */
  verbose?: boolean;
}

export interface GenerateJsBindingsResult {
  /** Whether bindings were generated */
  generated: boolean;
  /** Output directory (absolute path) */
  outputDir?: string;
  /** Number of generated files */
  fileCount?: number;
  /** Reason if not generated */
  skipReason?: string;
}

// ======================================================================
// Public API
// ======================================================================

/**
 * Generate JS/TS bindings from WinRT metadata based on winapp.yaml config.
 * This is the main entry point — call after `winapp restore` or standalone.
 */
export async function generateJsBindings(
  options: GenerateJsBindingsOptions = {}
): Promise<GenerateJsBindingsResult> {
  const projectRoot = options.projectRoot || getProjectRootDir();
  const configPath = options.configPath || findWinappYaml(projectRoot);

  if (!configPath) {
    return { generated: false, skipReason: 'winapp.yaml not found' };
  }

  const winrtMetaPath = findWinrtMeta(projectRoot);
  if (!winrtMetaPath) {
    return { generated: false, skipReason: 'winrt-meta not found in node_modules' };
  }

  let config = readJsBindingsConfig(configPath);
  if (!config) {
    // Auto-create jsBindings section with defaults
    appendDefaultJsBindingsConfig(configPath);
    config = readJsBindingsConfig(configPath);
    if (!config) {
      return { generated: false, skipReason: 'failed to initialize jsBindings config' };
    }
    console.log(`[js-bindings] added default jsBindings config to ${path.basename(configPath)}`);
  }

  // Read package versions from the packages section
  const packageVersions = readPackageVersions(configPath);

  const outputDir = path.resolve(projectRoot, config.output);
  const verbose = options.verbose || false;

  if (verbose) {
    console.log(`[js-bindings] config: lang=${config.lang}, output=${config.output}`);
    console.log(`[js-bindings] winrt-meta: ${winrtMetaPath}`);
  }

  // Resolve packages: use explicit list if provided, otherwise auto-discover from winapp.yaml
  let packages = config.packages;
  if (packages.length === 0) {
    packages = discoverPackages(packageVersions, verbose);
    if (packages.length === 0) {
      return { generated: false, skipReason: 'no packages with WinRT metadata found in SDK dependencies' };
    }
  }

  // Collect reference winmd paths from non-generated packages (for type resolution only)
  const refPaths = discoverRefPaths(packageVersions, packages, verbose);

  // Clean and recreate output directory
  if (fs.existsSync(outputDir)) {
    fs.rmSync(outputDir, { recursive: true });
  }
  fs.mkdirSync(outputDir, { recursive: true });

  let totalFiles = 0;

  // Generate bindings for each package
  for (const pkg of packages) {
    // Use explicit version from jsBindings config, or fall back to packages section
    if (pkg.version) {
      packageVersions.set(pkg.name.toLowerCase(), pkg.version);
    }
    const metadataDir = findWinmdMetadata(pkg.name, packageVersions, verbose);
    if (!metadataDir) {
      console.warn(`[js-bindings] warning: metadata not found for ${pkg.name}, skipping`);
      continue;
    }

    // Split winmd files: generate from non-excluded, use excluded as ref
    const allWinmd = fs.readdirSync(metadataDir).filter(f => f.toLowerCase().endsWith('.winmd'));
    const generateWinmd: string[] = [];
    const localRefWinmd: string[] = [];

    for (const f of allWinmd) {
      const lower = f.toLowerCase();
      if (EXCLUDED_WINMD_PREFIXES.some(prefix => lower.startsWith(prefix))) {
        localRefWinmd.push(path.join(metadataDir, f));
      } else {
        generateWinmd.push(path.join(metadataDir, f));
      }
    }

    if (generateWinmd.length === 0) {
      if (verbose) {
        console.log(`[js-bindings] skipping ${pkg.name}: all winmd files excluded`);
      }
      continue;
    }

    if (verbose) {
      console.log(`[js-bindings] generating from ${metadataDir} (${generateWinmd.length} winmd, ${localRefWinmd.length} ref)`);
    }

    const allRefs = [...refPaths, ...localRefWinmd];
    const args = [
      'generate', '--winmd', generateWinmd.join(';'),
      '--output', outputDir,
      '--lang', config.lang,
    ];
    if (allRefs.length > 0) {
      args.push('--ref', allRefs.join(';'));
    }
    callWinrtMeta(winrtMetaPath, args, verbose);
  }

  // Generate system types (individual classes from Windows SDK)
  for (const entry of config.systemTypes) {
    if (verbose) {
      console.log(`[js-bindings] generating ${entry.namespace}.${entry.classes}`);
    }

    callWinrtMeta(winrtMetaPath, [
      'generate',
      '--namespace', entry.namespace,
      '--class-name', entry.classes,
      '--output', outputDir,
      '--lang', config.lang,
    ], verbose);
  }

  // Count generated files
  if (fs.existsSync(outputDir)) {
    const files = fs.readdirSync(outputDir);
    const ext = config.lang === 'ts' ? '.ts' : '.js';
    totalFiles = files.filter(f => f.endsWith(ext)).length;
  }

  console.log(`[js-bindings] generated ${totalFiles} file(s) in ${config.output}`);
  return { generated: true, outputDir, fileCount: totalFiles };
}

/**
 * Auto-generate JS bindings if configured.
 * Silently skips if not configured or winrt-meta not installed.
 * Used as post-restore/init hook.
 */
export async function autoGenerateJsBindings(
  options: { verbose?: boolean } = {}
): Promise<void> {
  try {
    const result = await generateJsBindings(options);
    if (!result.generated && options.verbose) {
      console.log(`[js-bindings] skipped: ${result.skipReason}`);
    }
  } catch (error) {
    // Don't fail restore/init if binding generation fails
    console.warn(`[js-bindings] warning: ${error instanceof Error ? error.message : error}`);
  }
}

// ======================================================================
// Config parsing
// ======================================================================

/**
 * Append default jsBindings section to winapp.yaml.
 */
function appendDefaultJsBindingsConfig(yamlPath: string): void {
  const projectDir = path.dirname(yamlPath);
  const isTypeScript = fs.existsSync(path.join(projectDir, 'tsconfig.json'));
  const lang = isTypeScript ? 'ts' : 'js';
  const output = isTypeScript ? 'generated' : 'generated-js';
  const defaultConfig = `
jsBindings:
  lang: ${lang}
  output: ${output}
`;
  let content = fs.readFileSync(yamlPath, 'utf8');
  if (!content.endsWith('\n')) {
    content += '\n';
  }
  fs.writeFileSync(yamlPath, content + defaultConfig, 'utf8');
}

/**
 * Find winapp.yaml in the project.
 * Checks both `winapp.yaml` and `.winapp/winapp.yaml`.
 */
function findWinappYaml(projectRoot: string): string | null {
  const candidates = [
    path.join(projectRoot, 'winapp.yaml'),
    path.join(projectRoot, '.winapp', 'winapp.yaml'),
  ];
  for (const p of candidates) {
    if (fs.existsSync(p)) return p;
  }
  return null;
}

/**
 * Parse the jsBindings section from winapp.yaml.
 * Uses simple line-by-line parsing (consistent with C# ConfigService pattern).
 * Returns null if no jsBindings section exists.
 */
export function readJsBindingsConfig(yamlPath: string): JsBindingsConfig | null {
  const content = fs.readFileSync(yamlPath, 'utf8');
  const lines = content.split('\n').map(l => l.replace(/\r$/, ''));

  // Find jsBindings section
  let inJsBindings = false;
  let inPackages = false;
  let inSystemTypes = false;
  let currentSystemType: Partial<SystemTypeEntry> | null = null;

  const config: JsBindingsConfig = {
    lang: 'js',
    output: 'generated-js',
    packages: [],
    systemTypes: [],
  };

  let found = false;
  let currentPackage: Partial<JsBindingsPackageEntry> | null = null;

  for (const line of lines) {
    const trimmed = line.trim();

    // Skip empty lines and comments
    if (!trimmed || trimmed.startsWith('#')) continue;

    // Detect top-level sections
    if (!line.startsWith(' ') && !line.startsWith('\t')) {
      if (trimmed === 'jsBindings:') {
        inJsBindings = true;
        inPackages = false;
        inSystemTypes = false;
        found = true;
        continue;
      } else if (inJsBindings) {
        // Hit another top-level section, stop
        break;
      }
      continue;
    }

    if (!inJsBindings) continue;

    // Inside jsBindings section
    if (trimmed === 'packages:') {
      inPackages = true;
      inSystemTypes = false;
      currentPackage = null;
      continue;
    }
    if (trimmed === 'systemTypes:') {
      // Flush pending package
      if (currentPackage?.name) {
        config.packages.push({ name: currentPackage.name, version: currentPackage.version });
      }
      currentPackage = null;
      inSystemTypes = true;
      inPackages = false;
      continue;
    }

    if (inPackages) {
      if (trimmed.startsWith('- ')) {
        // Flush previous package
        if (currentPackage?.name) {
          config.packages.push({ name: currentPackage.name, version: currentPackage.version });
        }

        const rest = trimmed.slice(2).trim();
        if (rest.startsWith('name:')) {
          // Structured format: `- name: Foo`
          currentPackage = { name: rest.replace('name:', '').trim().replace(/^['"]|['"]$/g, '') };
        } else {
          // Simple format: `- PackageName`
          const value = rest.replace(/^['"]|['"]$/g, '');
          if (value) config.packages.push({ name: value });
          currentPackage = null;
        }
        continue;
      }
      // Continuation line for structured package (e.g. `version: 1.2.3`)
      if (currentPackage && trimmed.startsWith('version:')) {
        currentPackage.version = trimmed.replace('version:', '').trim().replace(/^['"]|['"]$/g, '');
        continue;
      }
    }

    if (inSystemTypes) {
      if (trimmed.startsWith('- ')) {
        // New system type entry
        if (currentSystemType?.namespace && currentSystemType?.classes) {
          config.systemTypes.push(currentSystemType as SystemTypeEntry);
        }
        currentSystemType = {};
        const kv = trimmed.slice(2).trim();
        parseKeyValue(kv, currentSystemType);
        continue;
      }
      if (currentSystemType && trimmed.includes(':')) {
        parseKeyValue(trimmed, currentSystemType);
        continue;
      }
    }

    // Simple key-value at jsBindings level
    if (!inPackages && !inSystemTypes && trimmed.includes(':')) {
      const [key, ...rest] = trimmed.split(':');
      const value = rest.join(':').trim().replace(/^['"]|['"]$/g, '');
      const k = key.trim();
      if (k === 'lang') config.lang = value;
      else if (k === 'output') config.output = value;
    }
  }

  // Flush last pending entries
  if (currentPackage?.name) {
    config.packages.push({ name: currentPackage.name, version: currentPackage.version });
  }
  if (currentSystemType?.namespace && currentSystemType?.classes) {
    config.systemTypes.push(currentSystemType as SystemTypeEntry);
  }

  return found ? config : null;
}

function parseKeyValue(str: string, target: Partial<SystemTypeEntry>): void {
  const colonIdx = str.indexOf(':');
  if (colonIdx === -1) return;
  const key = str.slice(0, colonIdx).trim();
  const value = str.slice(colonIdx + 1).trim().replace(/^['"]|['"]$/g, '');
  if (key === 'namespace') target.namespace = value;
  else if (key === 'classes') target.classes = value;
}

/**
 * Read the packages section from winapp.yaml to get version pins.
 * Returns a map of package name (lowercase) -> version.
 */
function readPackageVersions(yamlPath: string): Map<string, string> {
  const content = fs.readFileSync(yamlPath, 'utf8');
  const lines = content.split('\n').map(l => l.replace(/\r$/, ''));
  const versions = new Map<string, string>();

  let inPackages = false;
  let currentName = '';

  for (const line of lines) {
    const trimmed = line.trim();
    if (!trimmed || trimmed.startsWith('#')) continue;

    if (trimmed === 'packages:') {
      inPackages = true;
      continue;
    }
    // Another top-level section ends packages
    if (!line.startsWith(' ') && !line.startsWith('\t') && trimmed !== 'packages:') {
      if (inPackages) inPackages = false;
      continue;
    }

    if (!inPackages) continue;

    if (trimmed.startsWith('- name:')) {
      currentName = trimmed.replace('- name:', '').trim().replace(/^['"]|['"]$/g, '');
    } else if (trimmed.startsWith('name:')) {
      currentName = trimmed.replace('name:', '').trim().replace(/^['"]|['"]$/g, '');
    } else if (trimmed.startsWith('version:') && currentName) {
      const version = trimmed.replace('version:', '').trim().replace(/^['"]|['"]$/g, '');
      versions.set(currentName.toLowerCase(), version);
      currentName = '';
    }
  }

  return versions;
}

// ======================================================================
// WinMD discovery
// ======================================================================

/**
 * NuGet packages to exclude from auto-discovery (UI frameworks not usable from Electron/Node.js).
 */
const EXCLUDED_PACKAGES = new Set([
  'microsoft.windowsappsdk.winui',
  'microsoft.windowsappsdk.widgets',
]);

/**
 * WinMD file prefixes to exclude from generation (UI types not usable from Electron/Node.js).
 * These are used as --ref for type resolution instead.
 */
const EXCLUDED_WINMD_PREFIXES = [
  'microsoft.ui.',
  'microsoft.web.webview2.',
  'microsoft.windows.widgets.',
];

/**
 * Auto-discover packages with WinRT metadata (.winmd) from winapp.yaml packages
 * and their NuGet dependencies. Excludes UI-only packages not usable from Electron.
 */
function discoverPackages(
  packageVersions: Map<string, string>,
  verbose: boolean
): JsBindingsPackageEntry[] {
  const nugetCache = getNugetCachePath();
  const discovered: JsBindingsPackageEntry[] = [];
  const seen = new Set<string>();

  for (const [name, version] of packageVersions) {
    // Check the root package itself for .winmd files
    checkAndAddPackage(name, version, nugetCache, discovered, seen, verbose);

    // Check its NuGet dependencies for .winmd files
    const deps = readNuspecDeps(nugetCache, name, version);
    for (const dep of deps) {
      checkAndAddPackage(dep.name, dep.version, nugetCache, discovered, seen, verbose);
    }
  }

  return discovered;
}

function checkAndAddPackage(
  name: string,
  version: string | undefined,
  nugetCache: string,
  discovered: JsBindingsPackageEntry[],
  seen: Set<string>,
  verbose: boolean
): void {
  const lower = name.toLowerCase();
  if (seen.has(lower) || EXCLUDED_PACKAGES.has(lower)) return;
  seen.add(lower);

  if (!version) return;

  const pkgDir = path.join(nugetCache, lower, version);
  if (!fs.existsSync(pkgDir)) return;

  // Check known .winmd locations
  const metadataDir = path.join(pkgDir, 'metadata');
  const libDir = path.join(pkgDir, 'lib', 'uap10.0');

  if ((fs.existsSync(metadataDir) && hasWinmdFiles(metadataDir)) ||
      (fs.existsSync(libDir) && hasWinmdFiles(libDir))) {
    if (verbose) {
      console.log(`[js-bindings] auto-discovered: ${name} v${version}`);
    }
    discovered.push({ name, version });
  }
}

/**
 * Collect .winmd file paths from excluded packages (and all their transitive deps)
 * to use as --ref for type resolution.
 */
function discoverRefPaths(
  packageVersions: Map<string, string>,
  generatedPackages: JsBindingsPackageEntry[],
  verbose: boolean
): string[] {
  const nugetCache = getNugetCachePath();
  const refs: string[] = [];
  const generatedSet = new Set(generatedPackages.map(p => p.name.toLowerCase()));
  const seen = new Set<string>();

  for (const [name, version] of packageVersions) {
    collectRefFromPackage(name, version, nugetCache, refs, generatedSet, seen, verbose);

    const deps = readNuspecDeps(nugetCache, name, version);
    for (const dep of deps) {
      collectRefFromPackage(dep.name, dep.version, nugetCache, refs, generatedSet, seen, verbose);
    }
  }

  return refs;
}

function collectRefFromPackage(
  name: string,
  version: string | undefined,
  nugetCache: string,
  refs: string[],
  generatedSet: Set<string>,
  seen: Set<string>,
  verbose: boolean
): void {
  const lower = name.toLowerCase();
  // Skip packages we're generating bindings for, and skip already-processed
  if (generatedSet.has(lower) || seen.has(lower) || !version) return;
  seen.add(lower);

  const pkgDir = path.join(nugetCache, lower, version);
  if (!fs.existsSync(pkgDir)) return;

  // Collect individual .winmd files from known locations
  for (const subDir of ['metadata', path.join('lib', 'uap10.0')]) {
    const dir = path.join(pkgDir, subDir);
    if (!fs.existsSync(dir)) continue;
    const winmdFiles = fs.readdirSync(dir).filter(f => f.toLowerCase().endsWith('.winmd'));
    for (const f of winmdFiles) {
      refs.push(path.join(dir, f));
    }
    if (winmdFiles.length > 0 && verbose) {
      console.log(`[js-bindings] ref: ${name} v${version} (${winmdFiles.length} winmd files)`);
    }
  }

  // Also check subdirectories (e.g. metadata/10.0.18362.0/)
  for (const subDir of ['metadata']) {
    const dir = path.join(pkgDir, subDir);
    if (!fs.existsSync(dir)) continue;
    try {
      const entries = fs.readdirSync(dir, { withFileTypes: true });
      for (const entry of entries) {
        if (entry.isDirectory()) {
          const subPath = path.join(dir, entry.name);
          const winmdFiles = fs.readdirSync(subPath).filter(f => f.toLowerCase().endsWith('.winmd'));
          for (const f of winmdFiles) {
            refs.push(path.join(subPath, f));
          }
        }
      }
    } catch {
      // ignore
    }
  }
}

/**
 * Read direct dependencies from a NuGet package's nuspec file.
 */
function readNuspecDeps(
  nugetCache: string,
  pkgName: string,
  pkgVersion: string,
): JsBindingsPackageEntry[] {
  const nuspecPath = path.join(nugetCache, pkgName, pkgVersion,
    `${pkgName}.${pkgVersion}.nuspec`);
  const nuspecPathAlt = path.join(nugetCache, pkgName, pkgVersion,
    `${pkgName}.nuspec`);

  const filePath = fs.existsSync(nuspecPath) ? nuspecPath :
    fs.existsSync(nuspecPathAlt) ? nuspecPathAlt : null;

  if (!filePath) return [];

  const content = fs.readFileSync(filePath, 'utf8');
  const deps: JsBindingsPackageEntry[] = [];

  const depRegex = /<dependency\s+id="([^"]+)"\s+version="([^"]+)"\s*\/>/g;
  let match;
  while ((match = depRegex.exec(content)) !== null) {
    deps.push({
      name: match[1],
      version: match[2].replace(/[\[\]()]/g, ''),
    });
  }

  return deps;
}

function getNugetCachePath(): string {
  return process.env.NUGET_PACKAGES || path.join(os.homedir(), '.nuget', 'packages');
}

/**
 * Find .winmd metadata directory for a NuGet package.
 * Searches NuGet cache with multiple directory structure patterns.
 */
function findWinmdMetadata(
  packageName: string,
  packageVersions: Map<string, string>,
  verbose: boolean
): string | null {
  const nugetCache = getNugetCachePath();

  const pkgLower = packageName.toLowerCase();

  // Try exact version from winapp.yaml first
  const pinnedVersion = packageVersions.get(pkgLower);

  const tryVersion = (version: string): string | null => {
    const pkgDir = path.join(nugetCache, pkgLower, version);
    if (!fs.existsSync(pkgDir)) return null;

    // Pattern 1: metadata/ subdirectory (e.g. Microsoft.WindowsAppSDK.AI)
    const metadataDir = path.join(pkgDir, 'metadata');
    if (fs.existsSync(metadataDir) && hasWinmdFiles(metadataDir)) {
      return metadataDir;
    }

    // Pattern 2: lib/uap10.0/ (e.g. full WinAppSDK package)
    const libDir = path.join(pkgDir, 'lib', 'uap10.0');
    if (fs.existsSync(libDir) && hasWinmdFiles(libDir)) {
      return libDir;
    }

    return null;
  };

  // Try pinned version
  if (pinnedVersion) {
    const result = tryVersion(pinnedVersion);
    if (result) return result;
  }

  // Scan for latest version in NuGet cache
  const pkgBaseDir = path.join(nugetCache, pkgLower);
  if (!fs.existsSync(pkgBaseDir)) {
    if (verbose) {
      console.log(`[js-bindings] package ${packageName} not found in NuGet cache at ${pkgBaseDir}`);
    }
    return null;
  }

  const versions = fs.readdirSync(pkgBaseDir)
    .filter(d => fs.statSync(path.join(pkgBaseDir, d)).isDirectory())
    .sort()
    .reverse(); // newest first

  for (const version of versions) {
    const result = tryVersion(version);
    if (result) {
      if (verbose) {
        console.log(`[js-bindings] found ${packageName} v${version}`);
      }
      return result;
    }
  }

  return null;
}

function hasWinmdFiles(dir: string): boolean {
  try {
    return fs.readdirSync(dir).some(f => f.toLowerCase().endsWith('.winmd'));
  } catch {
    return false;
  }
}

// ======================================================================
// winrt-meta invocation
// ======================================================================

/**
 * Find winrt-meta CLI path from winappcli's own dependencies.
 */
function findWinrtMeta(_projectRoot: string): string | null {
  try {
    return require.resolve('winrt-meta/cli');
  } catch {
    return null;
  }
}

/**
 * Call winrt-meta generate with given arguments.
 */
function callWinrtMeta(cliPath: string, args: string[], verbose: boolean): void {
  const stdio: import('child_process').StdioOptions = verbose
    ? 'inherit'
    : ['inherit', 'pipe', 'inherit'];
  try {
    execFileSync(process.execPath, [cliPath, ...args], {
      stdio,
      env: process.env,
    });
  } catch (error) {
    const msg = error instanceof Error ? error.message : String(error);
    throw new Error(`winrt-meta failed: ${msg}`);
  }
}
