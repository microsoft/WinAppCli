import * as vscode from 'vscode';
import * as path from 'path';
import { spawn } from 'child_process';
import { getWinappCliPath, WINAPP_CLI_CALLER_VALUE } from './winapp-cli-utils';
import { glob } from 'glob';

const WINAPP_DEBUG_TYPE = 'winapp';

/**
 * Maps debugger types to the VS Code extensions that provide them.
 */
const DEBUGGER_EXTENSION_MAP: Record<string, { id: string; name: string }> = {
	'coreclr': { id: 'ms-dotnettools.csharp', name: 'C# (ms-dotnettools.csharp)' },
	'cppvsdbg': { id: 'ms-vscode.cpptools', name: 'C/C++ (ms-vscode.cpptools)' },
};

/**
 * Check that the VS Code extension required for the given debugger type is installed.
 * If it is not installed, show a clear error message with an option to install it.
 * Returns true if the extension is present (or the debugger type has no known requirement),
 * false if the extension is missing.
 */
async function ensureDebuggerExtensionInstalled(debuggerType: string): Promise<boolean> {
	const requirement = DEBUGGER_EXTENSION_MAP[debuggerType];
	if (!requirement) {
		return true;
	}

	if (vscode.extensions.getExtension(requirement.id)) {
		return true;
	}

	const install = await vscode.window.showErrorMessage(
		`The "${debuggerType}" debugger requires the ${requirement.name} VS Code extension. ` +
		`Please install it and reload VS Code, then retry.`,
		'Install Extension'
	);

	if (install === 'Install Extension') {
		await vscode.commands.executeCommand('workbench.extensions.installExtension', requirement.id);
		vscode.window.showInformationMessage(
			`Installing ${requirement.name}. Please reload VS Code once the installation completes, then retry the debug session.`
		);
	}

	return false;
}

/**
 * Execute a winapp CLI command and show output in the terminal
 */
async function runWinappCommand(extensionPath: string, command: string, cwd: string, showTerminal: boolean = true): Promise<string> {
	const cliPath = getWinappCliPath(extensionPath);
	const terminal = vscode.window.createTerminal({
		name: 'WinApp CLI',
		cwd: cwd,
		shellPath: 'powershell.exe',
		env: { WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE }
	});

	if (showTerminal) {
		terminal.show();
	}

	terminal.sendText(`& "${cliPath}" ${command}`);
	return '';
}

/**
 * Get the current workspace folder path
 */
function getWorkspacePath(): string | undefined {
	const workspaceFolders = vscode.workspace.workspaceFolders;
	if (!workspaceFolders || workspaceFolders.length === 0) {
		vscode.window.showErrorMessage('No workspace folder open');
		return undefined;
	}
	return workspaceFolders[0].uri.fsPath;
}

/**
 * Prompt user to select a file
 */
async function selectFile(title: string, filters?: { [name: string]: string[] }): Promise<string | undefined> {
	const result = await vscode.window.showOpenDialog({
		canSelectFiles: true,
		canSelectFolders: false,
		canSelectMany: false,
		title: title,
		filters: filters
	});

	return result?.[0]?.fsPath;
}

/**
 * Prompt user to select a folder
 */
async function selectFolder(title: string): Promise<string | undefined> {
	const result = await vscode.window.showOpenDialog({
		canSelectFiles: false,
		canSelectFolders: true,
		canSelectMany: false,
		title: title
	});

	return result?.[0]?.fsPath;
}

class WinAppDebugConfigurationProvider implements vscode.DebugConfigurationProvider {
	private extensionPath: string;

	constructor(extensionPath: string) {
		this.extensionPath = extensionPath;
	}

	async resolveDebugConfiguration(
		folder: vscode.WorkspaceFolder | undefined,
		config: vscode.DebugConfiguration,
		_token?: vscode.CancellationToken
	): Promise<vscode.DebugConfiguration | undefined> {
		// If no configuration, create a default one
		if (!config.type && !config.request && !config.name) {
			config.type = WINAPP_DEBUG_TYPE;
			config.name = 'WinApp: Launch and Attach';
			config.request = 'launch';
		}

		return config;
	}

	async resolveDebugConfigurationWithSubstitutedVariables(
		folder: vscode.WorkspaceFolder | undefined,
		config: vscode.DebugConfiguration,
		_token?: vscode.CancellationToken
	): Promise<vscode.DebugConfiguration | undefined> {
		if (!folder) {
			vscode.window.showErrorMessage('No workspace folder open');
			return undefined;
		}

		return config;
	}
}

class WinAppDebugAdapterFactory implements vscode.DebugAdapterDescriptorFactory {
	private extensionPath: string;

	constructor(extensionPath: string) {
		this.extensionPath = extensionPath;
	}

	async createDebugAdapterDescriptor(
		session: vscode.DebugSession,
		_executable: vscode.DebugAdapterExecutable | undefined
	): Promise<vscode.DebugAdapterDescriptor> {
		const config = session.configuration;
		const folder = session.workspaceFolder;

		if (!folder) {
			throw new Error('No workspace folder open');
		}

		try {
			// The run command requires an input-folder positional argument.
			// If not set in launch.json, search for folders containing .exe
			// files and let the user pick one.
			let inputFolder: string | undefined = config.inputFolder;
			if (!inputFolder) {
				const exeMatches = await glob('**/*.exe', {
					cwd: folder.uri.fsPath,
					absolute: true,
					nocase: true,
					ignore: ['**/node_modules/**', '**/.git/**', '**/AppX/**', '**/.winapp/**', '**/obj/**', '**/.vs/**', '**/packages/**']
				});

				// Collect unique parent directories that contain .exe files
				const dirSet = new Set<string>();
				for (const exe of exeMatches) {
					dirSet.add(path.dirname(exe));
				}

				if (dirSet.size === 0) {
					throw new Error('No folders containing .exe files found in the workspace. Build your project first, or set "inputFolder" in launch.json.');
				}

				const dirs = [...dirSet].sort();
				if (dirs.length === 1) {
					inputFolder = dirs[0];
				} else {
					const items = dirs.map(d => ({
						label: path.relative(folder.uri.fsPath, d),
						description: d,
						fsPath: d
					}));
					const picked = await vscode.window.showQuickPick(items, {
						placeHolder: 'Select the build output folder containing your app'
					});
					if (!picked) {
						throw new Error('No build output folder selected, cancelling debug session.');
					}
					inputFolder = picked.fsPath;
				}
			}

			const cliPath = getWinappCliPath(this.extensionPath);
			const spawnArgs = ['run', inputFolder];

			// Optional explicit manifest path; when omitted the CLI
			// auto-detects from the input folder or current directory.
			if (config.manifest) {
				spawnArgs.push('--manifest', config.manifest);
			}

			if (config.outputAppxDirectory) {
				spawnArgs.push('--output-appx-directory', config.outputAppxDirectory);
			}

			// Determine the debugger type based on config or default to coreclr
			const debuggerType = config.debuggerType || 'coreclr';

			// Verify the required VS Code extension for this debugger type is installed
			// before starting the app, so we don't launch the process only to fail on attach.
			if (!await ensureDebuggerExtensionInstalled(debuggerType)) {
				return new vscode.DebugAdapterInlineImplementation(new NoOpDebugAdapter());
			}

			let args = config.args || '';
			if (debuggerType === 'node') {
				args = '--inspect' + (config.port ? `=${config.port}` : '') + ' ' + args;
			}

			if (args.trim()) {
				spawnArgs.push('--args', args.trim());
			}

			spawnArgs.push('--json');

			// Spawn winapp run --json. The process stays alive while the app runs,
			// so we stream stdout to parse the JSON with the PID before waiting for exit.
			const { processId, runProcess } = await vscode.window.withProgress({
				location: vscode.ProgressLocation.Notification,
				title: 'Launching package...',
				cancellable: false
			}, async (progress) => {
				progress.report({ message: 'Running winapp run...' });

				let cwd = folder.uri.fsPath;
				if (config.workingDirectory) {
					cwd = config.workingDirectory;
				}

				return new Promise<{ processId: number; runProcess: ReturnType<typeof spawn> }>((resolve, reject) => {
					const child = spawn(cliPath, spawnArgs, {
						cwd,
						env: { ...process.env, WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE },
						shell: false
					});

					let stdout = '';
					let stderr = '';
					let resolved = false;

					child.stdout!.on('data', (data: Buffer) => {
						stdout += data.toString();
						if (resolved) { return; }

						const pid = parseProcessIdFromJson(stdout);
						if (pid) {
							resolved = true;
							resolve({ processId: pid, runProcess: child });
						}
					});

					child.stderr!.on('data', (data: Buffer) => {
						stderr += data.toString();
						console.warn('winapp run stderr:', data.toString());
					});

					child.on('error', (err) => {
						if (!resolved) {
							reject(new Error(`Failed to start winapp run: ${err.message}`));
						}
					});

					child.on('close', (code) => {
						if (!resolved) {
							if (code !== 0) {
								reject(new Error(`winapp run exited with code ${code}. stderr: ${stderr}\nstdout: ${stdout}`));
							} else {
								reject(new Error(`winapp run exited before returning a process ID. stdout: ${stdout}`));
							}
						}
					});
				});
			});

			// Build the attach debug configuration
			const debugConfiguration: vscode.DebugConfiguration = {
				type: debuggerType,
				name: config.name || 'Attach to WinApp Package',
				request: 'attach'
			};

			if (debuggerType === 'node') {
				debugConfiguration.port = config.port || 9229;
			} else {
				debugConfiguration.processId = processId;
			}

			// Start the real debug session as a child of the winapp session
			await vscode.debug.startDebugging(folder, debugConfiguration, { parentSession: session });

			// When the child debug session ends, kill the winapp run process and stop the parent session
			const parentSession = session;
			const disposable = vscode.debug.onDidTerminateDebugSession((ended) => {
				if (ended.parentSession === parentSession) {
					disposable.dispose();
					runProcess.kill();
					vscode.debug.stopDebugging(parentSession);
				}
			});

			// When the winapp run process exits (app closed), stop the debug session
			runProcess.on('close', () => {
				vscode.debug.stopDebugging(parentSession);
			});

			// Return an inline no-op adapter — the real debugging happens in the child session above
			return new vscode.DebugAdapterInlineImplementation(new NoOpDebugAdapter());
		} catch (error) {
			const message = error instanceof Error ? error.message : String(error);
			vscode.window.showErrorMessage(`Failed to launch and attach: ${message}`);
			throw error;
		}
	}
}

/**
 * A minimal no-op debug adapter. The winapp debug type doesn't need a real adapter
 * since we delegate to a child debug session (coreclr/node).
 */
class NoOpDebugAdapter implements vscode.DebugAdapter {
	private sendMessageEmitter = new vscode.EventEmitter<vscode.DebugProtocolMessage>();
	readonly onDidSendMessage: vscode.Event<vscode.DebugProtocolMessage> = this.sendMessageEmitter.event;

	handleMessage(message: vscode.DebugProtocolMessage): void {
		// Respond to the initialize request so VS Code doesn't hang
		const msg = message as any;
		if (msg.type === 'request' && msg.command === 'initialize') {
			this.sendMessageEmitter.fire({
				type: 'response',
				request_seq: msg.seq,
				success: true,
				command: msg.command,
				seq: 0
			} as any);
		} else if (msg.type === 'request' && msg.command === 'disconnect') {
			this.sendMessageEmitter.fire({
				type: 'response',
				request_seq: msg.seq,
				success: true,
				command: msg.command,
				seq: 0
			} as any);
		}
	}

	dispose(): void {
		this.sendMessageEmitter.dispose();
	}
}

export function activate(context: vscode.ExtensionContext) {
	const extensionPath = context.extensionPath;
	const provider = new WinAppDebugConfigurationProvider(extensionPath);

	context.subscriptions.push(
		vscode.debug.registerDebugConfigurationProvider(WINAPP_DEBUG_TYPE, provider)
	);

	const factory = new WinAppDebugAdapterFactory(extensionPath);
	context.subscriptions.push(
		vscode.debug.registerDebugAdapterDescriptorFactory(WINAPP_DEBUG_TYPE, factory)
	);

	// Register winapp.init command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.init', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const sdkMode = await vscode.window.showQuickPick(
				['stable', 'preview', 'experimental', 'none'],
				{ placeHolder: 'Select SDK installation mode' }
			);

			let command = 'init --use-defaults';
			if (sdkMode && sdkMode !== 'stable') {
				command += ` --setup-sdks ${sdkMode}`;
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.restore command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.restore', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			await runWinappCommand(extensionPath, 'restore', workspacePath);
		})
	);

	// Register winapp.update command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.update', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const sdkMode = await vscode.window.showQuickPick(
				['stable', 'preview', 'experimental'],
				{ placeHolder: 'Select SDK installation mode (optional)' }
			);

			let command = 'update';
			if (sdkMode && sdkMode !== 'stable') {
				command += ` --setup-sdks ${sdkMode}`;
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.pack command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.pack', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const inputFolder = await selectFolder('Select input folder to package');
			if (!inputFolder) {
				return;
			}

			const generateCert = await vscode.window.showQuickPick(
				['Yes', 'No'],
				{ placeHolder: 'Generate and install a development certificate?' }
			);

			const selfContained = await vscode.window.showQuickPick(
				['Yes', 'No'],
				{ placeHolder: 'Bundle Windows App SDK runtime (self-contained)?' }
			);

			let command = `pack "${inputFolder}"`;
			if (generateCert === 'Yes') {
				command += ' --generate-cert --install-cert';
			}
			if (selfContained === 'Yes') {
				command += ' --self-contained';
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.run command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.run', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const inputFolder = await selectFolder('Select input folder containing the app to run');
			if (!inputFolder) {
				return;
			}

			await runWinappCommand(extensionPath, `run "${inputFolder}"`, workspacePath);
		})
	);

	// Register winapp.createDebugIdentity command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.createDebugIdentity', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}
			const entrypoint = await selectFile('Select executable', {
				'Executables': ['exe'],
				'All files': ['*']
			});

			let command = 'create-debug-identity';
			if (entrypoint) {
				command += ` "${entrypoint}"`;
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.manifestGenerate command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.manifestGenerate', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const template = await vscode.window.showQuickPick(
				['packaged', 'sparse'],
				{ placeHolder: 'Select manifest template type' }
			);

			let command = 'manifest generate';
			if (template) {
				command += ` --template ${template}`;
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.manifestUpdateAssets command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.manifestUpdateAssets', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const imagePath = await selectFile('Select source image for assets', {
				'Images': ['png', 'jpg', 'jpeg', 'gif', 'bmp']
			});

			if (!imagePath) {
				vscode.window.showErrorMessage('An image file is required');
				return;
			}

			await runWinappCommand(extensionPath, `manifest update-assets "${imagePath}"`, workspacePath);
		})
	);

	// Register winapp.certGenerate command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.certGenerate', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const install = await vscode.window.showQuickPick(
				['Yes', 'No'],
				{ placeHolder: 'Install certificate after generation?' }
			);

			let command = 'cert generate';
			if (install === 'Yes') {
				command += ' --install';
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.certInstall command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.certInstall', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const certPath = await selectFile('Select certificate to install', {
				'Certificates': ['pfx', 'cer']
			});

			if (!certPath) {
				vscode.window.showErrorMessage('A certificate file is required');
				return;
			}

			await runWinappCommand(extensionPath, `cert install "${certPath}"`, workspacePath);
		})
	);

	// Register winapp.sign command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.sign', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const filePath = await selectFile('Select file to sign', {
				'MSIX Packages': ['msix', 'appx'],
				'Executables': ['exe', 'dll'],
				'All files': ['*']
			});

			if (!filePath) {
				vscode.window.showErrorMessage('A file to sign is required');
				return;
			}

			const certPath = await selectFile('Select signing certificate', {
				'Certificates': ['pfx']
			});

			if (!certPath) {
				vscode.window.showErrorMessage('A certificate file is required');
				return;
			}

			await runWinappCommand(extensionPath, `sign "${filePath}" --cert "${certPath}"`, workspacePath);
		})
	);

	// Register winapp.tool command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.tool', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const toolSelection = await vscode.window.showQuickPick(
				['makeappx', 'signtool', 'mt', 'makepri', 'other'],
				{ placeHolder: 'Select Windows SDK tool' }
			);

			if (!toolSelection) {
				return;
			}

			let toolName: string;
			if (toolSelection === 'other') {
				const customTool = await vscode.window.showInputBox({
					prompt: 'Enter the Windows SDK tool name',
					placeHolder: 'e.g., custom-tool'
				});

				if (!customTool) {
					return;
				}
				toolName = customTool;
			} else {
				toolName = toolSelection;
			}

			const args = await vscode.window.showInputBox({
				prompt: `Enter arguments for ${toolName}`,
				placeHolder: 'e.g., --help'
			});

			let command = `tool ${toolName}`;
			if (args) {
				command += ` ${args}`;
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.getWinappPath command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.getWinappPath', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			const global = await vscode.window.showQuickPick(
				['Local (.winapp in workspace)', 'Global (shared cache)'],
				{ placeHolder: 'Which path to retrieve?' }
			);

			let command = 'get-winapp-path';
			if (global === 'Global (shared cache)') {
				command += ' --global';
			}

			await runWinappCommand(extensionPath, command, workspacePath);
		})
	);

	// Register winapp.manifestAddAlias command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.manifestAddAlias', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			await runWinappCommand(extensionPath, 'manifest add-alias', workspacePath);
		})
	);

	// Register winapp.unregister command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.unregister', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) {
				return;
			}

			await runWinappCommand(extensionPath, 'unregister', workspacePath);
		})
	);

	// Register winapp.certInfo command
	// This command only inspects a certificate file and does not require a workspace.
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.certInfo', async () => {
			const certPath = await selectFile('Select certificate file', {
				'Certificates': ['pfx', 'cer']
			});

			if (!certPath) {
				vscode.window.showErrorMessage('A certificate file is required');
				return;
			}

			const password = await vscode.window.showInputBox({
				prompt: 'Enter certificate password (leave empty for default)',
				password: true
			});

			// Use spawn with an args array (shell: false) to avoid exposing
			// the password in terminal history and to prevent argument injection.
			const cliPath = getWinappCliPath(extensionPath);
			const args = ['cert', 'info', certPath];
			if (password) {
				args.push('--password', password);
			}

			// Use the certificate's parent directory as cwd since no workspace is required.
			const cwd = path.dirname(certPath);

			const outputChannel = vscode.window.createOutputChannel('WinApp Cert Info');
			outputChannel.show();
			outputChannel.appendLine(`Running: winapp cert info "${certPath}"`);

			await new Promise<void>((resolve) => {
				const child = spawn(cliPath, args, {
					cwd,
					env: { ...process.env, WINAPP_CLI_CALLER: WINAPP_CLI_CALLER_VALUE },
					shell: false
				});

				child.stdout!.on('data', (data: Buffer) => {
					outputChannel.append(data.toString());
				});

				child.stderr!.on('data', (data: Buffer) => {
					outputChannel.append(data.toString());
				});

				child.on('error', (err) => {
					vscode.window.showErrorMessage(`Failed to run cert info: ${err.message}`);
					resolve();
				});

				child.on('close', (code) => {
					if (code !== 0) {
						outputChannel.appendLine(`\nCommand exited with code ${code}`);
						vscode.window.showErrorMessage('Certificate info command failed. See output for details.');
					}
					resolve();
				});
			});
		})
	);
}

/**
 * Parse the process ID from the winapp run --json output.
 * Expects a JSON object with a processId (or pid) field.
 */
function parseProcessIdFromJson(output: string): number | undefined {
	try {
		const json = JSON.parse(output.trim());
		const pid = json.processId ?? json.pid ?? json.ProcessId ?? json.PID;
		if (typeof pid === 'number' && pid > 0) {
			return pid;
		}
	} catch {
		// JSON not complete yet or invalid
	}
	return undefined;
}

export function deactivate() {
}
