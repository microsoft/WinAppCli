/**
 * AUTO-GENERATED — DO NOT EDIT
 *
 * Regenerate with:  npm run generate-commands
 * Source schema version: 0.2.2
 *
 * Programmatic wrappers for all winapp CLI commands.
 * Each function builds the CLI arguments, invokes the native CLI,
 * and returns a typed result with captured stdout/stderr.
 */
import {
  callWinappCliCapture,
  CallWinappCliCaptureOptions,
  CallWinappCliCaptureResult,
} from './winapp-cli-utils';

// ---------------------------------------------------------------------------
// Shared / common types
// ---------------------------------------------------------------------------

/** IfExists values. */
export type IfExists = 'error' | 'overwrite' | 'skip';

/** SdkInstallMode values. */
export type SdkInstallMode = 'stable' | 'preview' | 'experimental' | 'none';

/** ManifestTemplates values. */
export type ManifestTemplates = 'packaged' | 'sparse';

/** Base options shared by most commands. */
export interface CommonOptions {
  /** Suppress progress messages. */
  quiet?: boolean;
  /** Enable verbose output. */
  verbose?: boolean;
  /** Working directory for the CLI process (defaults to process.cwd()). */
  cwd?: string;
}

/** Result returned by every command wrapper. */
export interface WinappResult {
  /** Process exit code (always 0 on success – non-zero throws). */
  exitCode: number;
  /** Captured standard output. */
  stdout: string;
  /** Captured standard error. */
  stderr: string;
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function pushCommon(args: string[], opts: CommonOptions): void {
  if (opts.quiet) args.push('--quiet');
  if (opts.verbose) args.push('--verbose');
}

function captureOpts(opts: CommonOptions): CallWinappCliCaptureOptions {
  return opts.cwd ? { cwd: opts.cwd } : {};
}

async function execCommand(args: string[], opts: CommonOptions): Promise<WinappResult> {
  pushCommon(args, opts);
  const result: CallWinappCliCaptureResult = await callWinappCliCapture(args, captureOpts(opts));
  return { exitCode: result.exitCode, stdout: result.stdout, stderr: result.stderr };
}

// ---------------------------------------------------------------------------
// cert generate
// ---------------------------------------------------------------------------

export interface CertGenerateOptions extends CommonOptions {
  /** Export a .cer file (public key only) alongside the .pfx */
  exportCer?: boolean;
  /** Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) */
  ifExists?: IfExists;
  /** Install the certificate to the local machine store after generation */
  install?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Path to Package.appxmanifest or appxmanifest.xml file to extract publisher information from */
  manifest?: string;
  /** Output path for the generated PFX file */
  output?: string;
  /** Password for the generated PFX file */
  password?: string;
  /** Publisher name for the generated certificate. If not specified, will be inferred from manifest. */
  publisher?: string;
  /** Number of days the certificate is valid */
  validDays?: number;
}

/**
 * Create a self-signed certificate for local testing only. Publisher must match the manifest (auto-inferred if --manifest provided or Package.appxmanifest is in working directory). Output: devcert.pfx (default password: 'password'). For production, obtain a certificate from a trusted CA. Use 'cert install' to trust on this machine.
 */
export async function certGenerate(options: CertGenerateOptions = {}): Promise<WinappResult> {
  const args: string[] = ['cert', 'generate'];
  if (options.exportCer) args.push('--export-cer');
  if (options.ifExists) args.push('--if-exists', options.ifExists);
  if (options.install) args.push('--install');
  if (options.json) args.push('--json');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.output) args.push('--output', options.output);
  if (options.password) args.push('--password', options.password);
  if (options.publisher) args.push('--publisher', options.publisher);
  if (options.validDays !== undefined) args.push('--valid-days', options.validDays.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// cert info
// ---------------------------------------------------------------------------

export interface CertInfoOptions extends CommonOptions {
  /** Path to the certificate file (PFX) */
  certPath: string;
  /** Format output as JSON */
  json?: boolean;
  /** Password for the PFX file */
  password?: string;
}

/**
 * Display certificate details (subject, thumbprint, expiry). Useful for verifying a certificate matches your manifest before signing.
 */
export async function certInfo(options: CertInfoOptions): Promise<WinappResult> {
  const args: string[] = ['cert', 'info'];
  args.push(options.certPath);
  if (options.json) args.push('--json');
  if (options.password) args.push('--password', options.password);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// cert install
// ---------------------------------------------------------------------------

export interface CertInstallOptions extends CommonOptions {
  /** Path to the certificate file (PFX or CER) */
  certPath: string;
  /** Force installation even if the certificate already exists */
  force?: boolean;
  /** Password for the PFX file */
  password?: string;
}

/**
 * Trust a certificate on this machine (requires admin). Run before installing MSIX packages signed with dev certificates. Example: winapp cert install ./devcert.pfx. Only needed once per certificate.
 */
export async function certInstall(options: CertInstallOptions): Promise<WinappResult> {
  const args: string[] = ['cert', 'install'];
  args.push(options.certPath);
  if (options.force) args.push('--force');
  if (options.password) args.push('--password', options.password);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// create-debug-identity
// ---------------------------------------------------------------------------

export interface CreateDebugIdentityOptions extends CommonOptions {
  /** Path to the .exe that will need to run with identity, or entrypoint script. */
  entrypoint?: string;
  /** Keep the package identity from the manifest as-is, without appending '.debug' to the package name and application ID. */
  keepIdentity?: boolean;
  /** Path to the Package.appxmanifest or appxmanifest.xml */
  manifest?: string;
  /** Do not install the package after creation. */
  noInstall?: boolean;
}

/**
 * Enable package identity for debugging without creating full MSIX. Required for testing Windows APIs (push notifications, share target, etc.) during development. Example: winapp create-debug-identity ./myapp.exe. Requires Package.appxmanifest in current directory or passed via --manifest. Re-run after changing the manifest or Assets/.
 */
export async function createDebugIdentity(options: CreateDebugIdentityOptions = {}): Promise<WinappResult> {
  const args: string[] = ['create-debug-identity'];
  if (options.entrypoint) args.push(options.entrypoint);
  if (options.keepIdentity) args.push('--keep-identity');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.noInstall) args.push('--no-install');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// create-external-catalog
// ---------------------------------------------------------------------------

export interface CreateExternalCatalogOptions extends CommonOptions {
  /** List of input folders with executable files to process (separated by semicolons) */
  inputFolder: string;
  /** Include flat hashes when generating the catalog */
  computeFlatHashes?: boolean;
  /** Behavior when output file already exists */
  ifExists?: IfExists;
  /** Output catalog file path. If not specified, the default CodeIntegrityExternal.cat name is used. */
  output?: string;
  /** Include files from subdirectories */
  recursive?: boolean;
  /** Include page hashes when generating the catalog */
  usePageHashes?: boolean;
}

/**
 * Generates a CodeIntegrityExternal.cat catalog file with hashes of executable files from specified directories. Used with the TrustedLaunch flag in MSIX sparse package manifests (AllowExternalContent) to allow execution of external files not included in the package.
 */
export async function createExternalCatalog(options: CreateExternalCatalogOptions): Promise<WinappResult> {
  const args: string[] = ['create-external-catalog'];
  args.push(options.inputFolder);
  if (options.computeFlatHashes) args.push('--compute-flat-hashes');
  if (options.ifExists) args.push('--if-exists', options.ifExists);
  if (options.output) args.push('--output', options.output);
  if (options.recursive) args.push('--recursive');
  if (options.usePageHashes) args.push('--use-page-hashes');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// get-winapp-path
// ---------------------------------------------------------------------------

export interface GetWinappPathOptions extends CommonOptions {
  /** Get the global .winapp directory instead of local */
  global?: boolean;
}

/**
 * Print the path to the .winapp directory. Use --global for the shared cache location, or omit for the project-local .winapp folder. Useful for build scripts that need to reference installed packages.
 */
export async function getWinappPath(options: GetWinappPathOptions = {}): Promise<WinappResult> {
  const args: string[] = ['get-winapp-path'];
  if (options.global) args.push('--global');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// init
// ---------------------------------------------------------------------------

export interface InitOptions extends CommonOptions {
  /** Base/root directory for the winapp workspace, for consumption or installation. */
  baseDirectory?: string;
  /** Directory to read/store configuration (default: current directory) */
  configDir?: string;
  /** Only handle configuration file operations (create if missing, validate if exists). Skip package installation and other workspace setup steps. */
  configOnly?: boolean;
  /** Don't use configuration file for version management */
  ignoreConfig?: boolean;
  /** Don't update .gitignore file */
  noGitignore?: boolean;
  /** SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) */
  setupSdks?: SdkInstallMode;
  /** Do not prompt, and use default of all prompts */
  useDefaults?: boolean;
}

/**
 * Start here for initializing a Windows app with required setup. Sets up everything needed for Windows app development: creates Package.appxmanifest with default assets, creates winapp.yaml for version management, and downloads Windows SDK and Windows App SDK packages and generates projections. Interactive by default (use --use-defaults to skip prompts). Use 'restore' instead if you cloned a repo that already has winapp.yaml. Use 'manifest generate' if you only need a manifest, or 'cert generate' if you need a development certificate for code signing.
 */
export async function init(options: InitOptions = {}): Promise<WinappResult> {
  const args: string[] = ['init'];
  if (options.baseDirectory) args.push(options.baseDirectory);
  if (options.configDir) args.push('--config-dir', options.configDir);
  if (options.configOnly) args.push('--config-only');
  if (options.ignoreConfig) args.push('--ignore-config');
  if (options.noGitignore) args.push('--no-gitignore');
  if (options.setupSdks) args.push('--setup-sdks', options.setupSdks);
  if (options.useDefaults) args.push('--use-defaults');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// manifest add-alias
// ---------------------------------------------------------------------------

export interface ManifestAddAliasOptions extends CommonOptions {
  /** Application Id to add the alias to (default: first Application element) */
  appId?: string;
  /** Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) */
  manifest?: string;
  /** Alias name (e.g. 'myapp.exe'). Default: inferred from the Executable attribute in the manifest. */
  name?: string;
}

/**
 * Add an execution alias (uap5:AppExecutionAlias) to a Package.appxmanifest. This allows launching the packaged app from the command line by typing the alias name. By default, the alias is inferred from the Executable attribute (e.g. $targetnametoken$.exe becomes $targetnametoken$.exe alias).
 */
export async function manifestAddAlias(options: ManifestAddAliasOptions = {}): Promise<WinappResult> {
  const args: string[] = ['manifest', 'add-alias'];
  if (options.appId) args.push('--app-id', options.appId);
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.name) args.push('--name', options.name);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// manifest generate
// ---------------------------------------------------------------------------

export interface ManifestGenerateOptions extends CommonOptions {
  /** Directory to generate manifest in */
  directory?: string;
  /** Human-readable app description shown during installation and in Windows Settings */
  description?: string;
  /** Path to the application's executable. Default: <package-name>.exe */
  executable?: string;
  /** Behavior when output file exists: 'error' (fail, default), 'skip' (keep existing), or 'overwrite' (replace) */
  ifExists?: IfExists;
  /** Path to logo image file */
  logoPath?: string;
  /** Package name (default: folder name) */
  packageName?: string;
  /** Publisher CN (default: CN=<current user>) */
  publisherName?: string;
  /** Manifest template type: 'packaged' (full MSIX app, default) or 'sparse' (desktop app with package identity for Windows APIs) */
  template?: ManifestTemplates;
  /** App version in Major.Minor.Build.Revision format (e.g., 1.0.0.0). */
  version?: string;
}

/**
 * Create Package.appxmanifest without full project setup. Use when you only need a manifest and image assets (no SDKs, no certificate). For full setup, use 'init' instead. Templates: 'packaged' (full MSIX), 'sparse' (desktop app needing Windows APIs).
 */
export async function manifestGenerate(options: ManifestGenerateOptions = {}): Promise<WinappResult> {
  const args: string[] = ['manifest', 'generate'];
  if (options.directory) args.push(options.directory);
  if (options.description) args.push('--description', options.description);
  if (options.executable) args.push('--executable', options.executable);
  if (options.ifExists) args.push('--if-exists', options.ifExists);
  if (options.logoPath) args.push('--logo-path', options.logoPath);
  if (options.packageName) args.push('--package-name', options.packageName);
  if (options.publisherName) args.push('--publisher-name', options.publisherName);
  if (options.template) args.push('--template', options.template);
  if (options.version) args.push('--version', options.version);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// manifest update-assets
// ---------------------------------------------------------------------------

export interface ManifestUpdateAssetsOptions extends CommonOptions {
  /** Path to source image file (SVG, PNG, ICO, JPG, BMP, GIF) */
  imagePath: string;
  /** Path to source image for light theme variants (SVG, PNG, ICO, JPG, BMP, GIF) */
  lightImage?: string;
  /** Path to Package.appxmanifest or appxmanifest.xml file (default: search current directory) */
  manifest?: string;
}

/**
 * Generate new assets for images referenced in a Package.appxmanifest from a single source image. Source image should be at least 400x400 pixels.
 */
export async function manifestUpdateAssets(options: ManifestUpdateAssetsOptions): Promise<WinappResult> {
  const args: string[] = ['manifest', 'update-assets'];
  args.push(options.imagePath);
  if (options.lightImage) args.push('--light-image', options.lightImage);
  if (options.manifest) args.push('--manifest', options.manifest);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// package
// ---------------------------------------------------------------------------

export interface PackageOptions extends CommonOptions {
  /** Input folder with package layout */
  inputFolder: string;
  /** Path to signing certificate (will auto-sign if provided) */
  cert?: string;
  /** Certificate password (default: password) */
  certPassword?: string;
  /** Path to the executable relative to the input folder. */
  executable?: string;
  /** Generate a new development certificate */
  generateCert?: boolean;
  /** Install certificate to machine */
  installCert?: boolean;
  /** Path to AppX manifest file (default: auto-detect from input folder or current directory) */
  manifest?: string;
  /** Package name (default: from manifest) */
  name?: string;
  /** Output msix file name for the generated package (defaults to <name>.msix) */
  output?: string;
  /** Publisher name for certificate generation */
  publisher?: string;
  /** Bundle Windows App SDK runtime for self-contained deployment */
  selfContained?: boolean;
  /** Skip PRI file generation */
  skipPri?: boolean;
}

/**
 * Create MSIX installer from your built app. Run after building your app. A manifest (Package.appxmanifest or appxmanifest.xml) is required for packaging - it must be in current working directory, passed as --manifest or be in the input folder. Use --cert devcert.pfx to sign for testing. Example: winapp package ./dist --manifest Package.appxmanifest --cert ./devcert.pfx
 */
export async function packageApp(options: PackageOptions): Promise<WinappResult> {
  const args: string[] = ['package'];
  args.push(options.inputFolder);
  if (options.cert) args.push('--cert', options.cert);
  if (options.certPassword) args.push('--cert-password', options.certPassword);
  if (options.executable) args.push('--executable', options.executable);
  if (options.generateCert) args.push('--generate-cert');
  if (options.installCert) args.push('--install-cert');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.name) args.push('--name', options.name);
  if (options.output) args.push('--output', options.output);
  if (options.publisher) args.push('--publisher', options.publisher);
  if (options.selfContained) args.push('--self-contained');
  if (options.skipPri) args.push('--skip-pri');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// restore
// ---------------------------------------------------------------------------

export interface RestoreOptions extends CommonOptions {
  /** Base/root directory for the winapp workspace */
  baseDirectory?: string;
  /** Directory to read configuration from (default: current directory) */
  configDir?: string;
}

/**
 * Use after cloning a repo or when .winapp/ folder is missing. Reinstalls SDK packages from existing winapp.yaml without changing versions. Requires winapp.yaml (created by 'init'). To check for newer SDK versions, use 'update' instead.
 */
export async function restore(options: RestoreOptions = {}): Promise<WinappResult> {
  const args: string[] = ['restore'];
  if (options.baseDirectory) args.push(options.baseDirectory);
  if (options.configDir) args.push('--config-dir', options.configDir);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// run
// ---------------------------------------------------------------------------

export interface RunOptions extends CommonOptions {
  /** Input folder containing the app to run */
  inputFolder: string;
  /** Command-line arguments to pass to the application */
  args?: string;
  /** Remove the existing package's application data (LocalState, settings, etc.) before re-deploying. By default, application data is preserved across re-deployments. */
  clean?: boolean;
  /** Capture OutputDebugString messages and first-chance exceptions from the launched application. Only one debugger can attach to a process at a time, so other debuggers (Visual Studio, VS Code) cannot be used simultaneously. Use --no-launch instead if you need to attach a different debugger. Cannot be combined with --no-launch or --json. */
  debugOutput?: boolean;
  /** Launch the application and return immediately without waiting for it to exit. Useful for CI/automation where you need to interact with the app after launch. Prints the PID to stdout (or in JSON with --json). */
  detach?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Path to the Package.appxmanifest (default: auto-detect from input folder or current directory) */
  manifest?: string;
  /** Only create the debug identity and register the package without launching the application */
  noLaunch?: boolean;
  /** Output directory for the loose layout package. If not specified, a directory named AppX inside the input-folder directory will be used. */
  outputAppxDirectory?: string;
  /** Download symbols from Microsoft Symbol Server for richer native crash analysis. Only used with --debug-output. First run downloads symbols and caches them locally; subsequent runs use the cache. */
  symbols?: boolean;
  /** Unregister the development package after the application exits. Only removes packages registered in development mode. */
  unregisterOnExit?: boolean;
  /** Launch the app using its execution alias instead of AUMID activation. The app runs in the current terminal with inherited stdin/stdout/stderr. Requires a uap5:ExecutionAlias in the manifest. Use "winapp manifest add-alias" to add an execution alias to the manifest. */
  withAlias?: boolean;
}

/**
 * Creates packaged layout, registers the Application, and launches the packaged application.
 */
export async function run(options: RunOptions): Promise<WinappResult> {
  const args: string[] = ['run'];
  args.push(options.inputFolder);
  if (options.args) args.push('--args', options.args);
  if (options.clean) args.push('--clean');
  if (options.debugOutput) args.push('--debug-output');
  if (options.detach) args.push('--detach');
  if (options.json) args.push('--json');
  if (options.manifest) args.push('--manifest', options.manifest);
  if (options.noLaunch) args.push('--no-launch');
  if (options.outputAppxDirectory) args.push('--output-appx-directory', options.outputAppxDirectory);
  if (options.symbols) args.push('--symbols');
  if (options.unregisterOnExit) args.push('--unregister-on-exit');
  if (options.withAlias) args.push('--with-alias');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// sign
// ---------------------------------------------------------------------------

export interface SignOptions extends CommonOptions {
  /** Path to the file/package to sign */
  filePath: string;
  /** Path to the certificate file (PFX format) */
  certPath: string;
  /** Certificate password */
  password?: string;
  /** Timestamp server URL */
  timestamp?: string;
}

/**
 * Code-sign an MSIX package or executable. Example: winapp sign ./app.msix ./devcert.pfx. Use --timestamp for production builds to remain valid after cert expires. The 'package' command can sign automatically with --cert.
 */
export async function sign(options: SignOptions): Promise<WinappResult> {
  const args: string[] = ['sign'];
  args.push(options.filePath);
  args.push(options.certPath);
  if (options.password) args.push('--password', options.password);
  if (options.timestamp) args.push('--timestamp', options.timestamp);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// store
// ---------------------------------------------------------------------------

export interface StoreOptions extends CommonOptions {
  /** Arguments to pass through to the Microsoft Store Developer CLI. */
  storeArgs?: string[];
}

/**
 * Run a Microsoft Store Developer CLI command. This command will download the Microsoft Store Developer CLI if not already downloaded. Learn more about the Microsoft Store Developer CLI here: https://aka.ms/msstoredevcli
 */
export async function store(options: StoreOptions = {}): Promise<WinappResult> {
  const args: string[] = ['store'];
  if (options.storeArgs) args.push(...options.storeArgs);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// tool
// ---------------------------------------------------------------------------

export interface ToolOptions extends CommonOptions {
  /** Arguments to pass to the SDK tool, e.g. ['makeappx', 'pack', '/d', './folder', '/p', './out.msix']. */
  toolArgs?: string[];
}

/**
 * Run Windows SDK tools directly (makeappx, signtool, makepri, etc.). Auto-downloads Build Tools if needed. For most tasks, prefer higher-level commands like 'package' or 'sign'. Example: winapp tool makeappx pack /d ./folder /p ./out.msix
 */
export async function tool(options: ToolOptions = {}): Promise<WinappResult> {
  const args: string[] = ['tool'];
  if (options.toolArgs && options.toolArgs.length > 0) {
    args.push('--', ...options.toolArgs);
  }
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui click
// ---------------------------------------------------------------------------

export interface UiClickOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Perform a double-click instead of a single click */
  double?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Perform a right-click instead of a left click */
  right?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Click an element by slug or text search using mouse simulation. Works on elements that don't support InvokePattern (e.g., column headers, list items). Use --double for double-click, --right for right-click.
 */
export async function uiClick(options: UiClickOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'click'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.double) args.push('--double');
  if (options.json) args.push('--json');
  if (options.right) args.push('--right');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui focus
// ---------------------------------------------------------------------------

export interface UiFocusOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Move keyboard focus to the specified element using UIA SetFocus.
 */
export async function uiFocus(options: UiFocusOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'focus'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui get-focused
// ---------------------------------------------------------------------------

export interface UiGetFocusedOptions extends CommonOptions {
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Show the element that currently has keyboard focus in the target app.
 */
export async function uiGetFocused(options: UiGetFocusedOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'get-focused'];
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui get-property
// ---------------------------------------------------------------------------

export interface UiGetPropertyOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Property name to read or filter on */
  property?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Read UIA property values from an element. Specify --property for a single property or omit for all.
 */
export async function uiGetProperty(options: UiGetPropertyOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'get-property'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.property) args.push('--property', options.property);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui get-value
// ---------------------------------------------------------------------------

export interface UiGetValueOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Read the current value from an element. Tries TextPattern (RichEditBox, Document), ValuePattern (TextBox, ComboBox, Slider), then Name (labels). Usage: winapp ui get-value <selector> -a <app>
 */
export async function uiGetValue(options: UiGetValueOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'get-value'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui inspect
// ---------------------------------------------------------------------------

export interface UiInspectOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Walk up the tree from the specified element to the root */
  ancestors?: boolean;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Tree inspection depth */
  depth?: number;
  /** Hide disabled elements from output */
  hideDisabled?: boolean;
  /** Hide offscreen elements from output */
  hideOffscreen?: boolean;
  /** Show only interactive/invokable elements (buttons, links, inputs, list items). Increases default depth to 8. */
  interactive?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * View the UI element tree with semantic slugs, element types, names, and bounds.
 */
export async function uiInspect(options: UiInspectOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'inspect'];
  if (options.selector) args.push(options.selector);
  if (options.ancestors) args.push('--ancestors');
  if (options.app) args.push('--app', options.app);
  if (options.depth !== undefined) args.push('--depth', options.depth.toString());
  if (options.hideDisabled) args.push('--hide-disabled');
  if (options.hideOffscreen) args.push('--hide-offscreen');
  if (options.interactive) args.push('--interactive');
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui invoke
// ---------------------------------------------------------------------------

export interface UiInvokeOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Activate an element by slug or text search. Tries InvokePattern, TogglePattern, SelectionItemPattern, and ExpandCollapsePattern in order.
 */
export async function uiInvoke(options: UiInvokeOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'invoke'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui list-windows
// ---------------------------------------------------------------------------

export interface UiListWindowsOptions extends CommonOptions {
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
}

/**
 * List all visible windows with their HWND, title, process, and size. Use -a to filter by app name. Use the HWND with -w to target a specific window.
 */
export async function uiListWindows(options: UiListWindowsOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'list-windows'];
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui screenshot
// ---------------------------------------------------------------------------

export interface UiScreenshotOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Capture from screen (includes popups/overlays) instead of window rendering. Brings window to foreground first. */
  captureScreen?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Save output to file path (e.g., screenshot) */
  output?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Capture the target window or element as a PNG image. When multiple windows exist (e.g., dialogs), captures each to a separate file. With --json, returns file path and dimensions. Use --capture-screen for popup overlays.
 */
export async function uiScreenshot(options: UiScreenshotOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'screenshot'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.captureScreen) args.push('--capture-screen');
  if (options.json) args.push('--json');
  if (options.output) args.push('--output', options.output);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui scroll
// ---------------------------------------------------------------------------

export interface UiScrollOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Scroll direction: up, down, left, right */
  direction?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Scroll to position: top, bottom */
  to?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Scroll a container element using ScrollPattern. Use --direction to scroll incrementally, or --to to jump to top/bottom.
 */
export async function uiScroll(options: UiScrollOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'scroll'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.direction) args.push('--direction', options.direction);
  if (options.json) args.push('--json');
  if (options.to) args.push('--to', options.to);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui scroll-into-view
// ---------------------------------------------------------------------------

export interface UiScrollIntoViewOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Scroll the specified element into the visible area using UIA ScrollItemPattern.
 */
export async function uiScrollIntoView(options: UiScrollIntoViewOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'scroll-into-view'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui search
// ---------------------------------------------------------------------------

export interface UiSearchOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Maximum search results */
  max?: number;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Search the element tree for elements matching a text query. Returns all matches with semantic slugs.
 */
export async function uiSearch(options: UiSearchOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'search'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.max !== undefined) args.push('--max', options.max.toString());
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui set-value
// ---------------------------------------------------------------------------

export interface UiSetValueOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Value to set (text for TextBox/ComboBox, number for Slider) */
  value?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Set a value on an element using UIA ValuePattern. Works for TextBox, ComboBox, Slider, and other editable controls. Usage: winapp ui set-value <selector> <value> -a <app>
 */
export async function uiSetValue(options: UiSetValueOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'set-value'];
  if (options.selector) args.push(options.selector);
  if (options.value) args.push(options.value);
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui status
// ---------------------------------------------------------------------------

export interface UiStatusOptions extends CommonOptions {
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Format output as JSON */
  json?: boolean;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Connect to a target app, auto-detect mode (UIA or DevTools), and display connection info.
 */
export async function uiStatus(options: UiStatusOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'status'];
  if (options.app) args.push('--app', options.app);
  if (options.json) args.push('--json');
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// ui wait-for
// ---------------------------------------------------------------------------

export interface UiWaitForOptions extends CommonOptions {
  /** Semantic slug (e.g., btn-minimize-d1a0) or text to search by name/automationId */
  selector?: string;
  /** Target app (process name, window title, or PID). Lists windows if ambiguous. */
  app?: string;
  /** Use substring matching for --value instead of exact match */
  contains?: boolean;
  /** Wait for element to disappear instead of appear */
  gone?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Property name to read or filter on */
  property?: string;
  /** Timeout in milliseconds */
  timeout?: number;
  /** Wait for element value to equal this string. Uses smart fallback (TextPattern → ValuePattern → Name). Combine with --property to check a specific property instead. */
  value?: string;
  /** Target window by HWND (stable handle from list output). Takes precedence over --app. */
  window?: number;
}

/**
 * Wait for an element to appear, disappear, or have a property reach a target value. Polls at 100ms intervals until condition met or timeout.
 */
export async function uiWaitFor(options: UiWaitForOptions = {}): Promise<WinappResult> {
  const args: string[] = ['ui', 'wait-for'];
  if (options.selector) args.push(options.selector);
  if (options.app) args.push('--app', options.app);
  if (options.contains) args.push('--contains');
  if (options.gone) args.push('--gone');
  if (options.json) args.push('--json');
  if (options.property) args.push('--property', options.property);
  if (options.timeout !== undefined) args.push('--timeout', options.timeout.toString());
  if (options.value) args.push('--value', options.value);
  if (options.window !== undefined) args.push('--window', options.window.toString());
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// unregister
// ---------------------------------------------------------------------------

export interface UnregisterOptions extends CommonOptions {
  /** Skip the install-location directory check and unregister even if the package was registered from a different project tree */
  force?: boolean;
  /** Format output as JSON */
  json?: boolean;
  /** Path to the Package.appxmanifest (default: auto-detect from current directory) */
  manifest?: string;
}

/**
 * Unregisters a sideloaded development package. Only removes packages registered in development mode (e.g., via 'winapp run' or 'create-debug-identity').
 */
export async function unregister(options: UnregisterOptions = {}): Promise<WinappResult> {
  const args: string[] = ['unregister'];
  if (options.force) args.push('--force');
  if (options.json) args.push('--json');
  if (options.manifest) args.push('--manifest', options.manifest);
  return execCommand(args, options);
}

// ---------------------------------------------------------------------------
// update
// ---------------------------------------------------------------------------

export interface UpdateOptions extends CommonOptions {
  /** SDK installation mode: 'stable' (default), 'preview', 'experimental', or 'none' (skip SDK installation) */
  setupSdks?: SdkInstallMode;
}

/**
 * Check for and install newer SDK versions. Updates winapp.yaml with latest versions and reinstalls packages. Requires existing winapp.yaml (created by 'init'). Use --setup-sdks preview for preview SDKs. To reinstall current versions without updating, use 'restore' instead.
 */
export async function update(options: UpdateOptions = {}): Promise<WinappResult> {
  const args: string[] = ['update'];
  if (options.setupSdks) args.push('--setup-sdks', options.setupSdks);
  return execCommand(args, options);
}
