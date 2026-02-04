import * as vscode from 'vscode';
import { exec } from 'child_process';
import { promisify } from 'util';

const execAsync = promisify(exec);

const WINAPP_DEBUG_TYPE = 'winapp';

// Path to the winapp CLI executable - update this to point to the installed location
const WINAPP_CLI_PATH = 'winapp';

/**
 * Execute a winapp CLI command and show output in the terminal
 */
async function runWinappCommand(command: string, cwd: string, showTerminal: boolean = true): Promise<string> {
	const terminal = vscode.window.createTerminal({
		name: 'WinApp CLI',
		cwd: cwd
	});

	if (showTerminal) {
		terminal.show();
	}

	terminal.sendText(`${WINAPP_CLI_PATH} ${command}`);
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

		       try {
			       // Build the command with mapped arguments
			       const cmdParts: string[] = ['D:\\WinAppCli\\src\\winapp-CLI\\WinApp.Cli\\bin\\Debug\\net10.0-windows\\win-arm64\\winapp.exe', 'run'];

			       if (config.manifest) {
				       cmdParts.push('--manifest', `"${config.manifest}"`);
			       }

				   // Determine the debugger type based on config or default to coreclr
				   const debuggerType = config.debuggerType || 'coreclr';

				   if (debuggerType === 'node') {
						if (!config.args) {
							config.args = '';
						}
						config.args = '--inspect' + (config.port ? `=${config.port}` : '') + ' ' + config.args;
				   }

			       if (config.args) {
				       cmdParts.push('--args', `"${config.args}"`);
			       }

			       const command = cmdParts.join(' ');

			       // Run "winapp run" which returns the process ID
			       const processId = await vscode.window.withProgress({
				       location: vscode.ProgressLocation.Notification,
				       title: 'Launching package...',
				       cancellable: false
			       }, async (progress) => {
				       progress.report({ message: 'Running winapp run...' });

					   let cwd = folder.uri.fsPath;
					   if (config.workingDirectory) {
						   cwd = config.workingDirectory;
					   }

				       const { stdout, stderr } = await execAsync(command, { cwd });

				       if (stderr) {
					       console.warn('winapp run stderr:', stderr);
				       }

				       const pid = parseProcessId(stdout);
				       if (!pid) {
					       throw new Error(`Could not parse process ID from winapp run output: ${stdout}`);
				       }

				       return pid;
			       });

				   // define debugConfiguration using vscode.DebugConfiguration type
				   var debugConfiguration = {
					   type: debuggerType,
					   name: config.name || 'Attach to WinApp Package',
					   request: 'attach'
					} as vscode.DebugConfiguration;

					// if debuggerType is 'node', use port from config or default to 9229
					if (debuggerType === 'node') {
						debugConfiguration.port = config.port || 9229;
					}else{
						// for other debugger types, set processId in config
						debugConfiguration.processId = processId;
					}
			       // Start the child debug session and return undefined so VS Code doesn't try to start a debug adapter for 'winapp'
			       await vscode.debug.startDebugging(folder, debugConfiguration);
			       return undefined;
		       } catch (error) {
			       const message = error instanceof Error ? error.message : String(error);
			       vscode.window.showErrorMessage(`Failed to launch and attach: ${message}`);
			       return undefined;
		       }
	       }
}

export function activate(context: vscode.ExtensionContext) {
	const provider = new WinAppDebugConfigurationProvider();

	context.subscriptions.push(
		vscode.debug.registerDebugConfigurationProvider(WINAPP_DEBUG_TYPE, provider)
	);

	// Register winapp.init command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.init', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const sdkMode = await vscode.window.showQuickPick(
				['stable', 'preview', 'experimental', 'none'],
				{ placeHolder: 'Select SDK installation mode' }
			);

			let command = 'init --use-defaults';
			if (sdkMode && sdkMode !== 'stable') {
				command += ` --setup-sdks ${sdkMode}`;
			}

			await runWinappCommand(command, workspacePath);
		})
	);

	// Register winapp.restore command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.restore', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			await runWinappCommand('restore', workspacePath);
		})
	);

	// Register winapp.update command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.update', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const sdkMode = await vscode.window.showQuickPick(
				['stable', 'preview', 'experimental'],
				{ placeHolder: 'Select SDK installation mode (optional)' }
			);

			let command = 'update';
			if (sdkMode && sdkMode !== 'stable') {
				command += ` --setup-sdks ${sdkMode}`;
			}

			await runWinappCommand(command, workspacePath);
		})
	);

	// Register winapp.pack command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.pack', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const inputFolder = await selectFolder('Select input folder to package');
			if (!inputFolder) return;

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

			await runWinappCommand(command, workspacePath);
		})
	);

	// Register winapp.run command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.run', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			await runWinappCommand('run', workspacePath);
		})
	);

	// Register winapp.createDebugIdentity command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.createDebugIdentity', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const entrypoint = await selectFile('Select executable or script', {
				'Executables': ['exe'],
				'Scripts': ['py', 'js'],
				'All files': ['*']
			});

			let command = 'create-debug-identity';
			if (entrypoint) {
				command += ` "${entrypoint}"`;
			}

			await runWinappCommand(command, workspacePath);
		})
	);

	// Register winapp.manifestGenerate command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.manifestGenerate', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const template = await vscode.window.showQuickPick(
				['packaged', 'sparse', 'hostedapp'],
				{ placeHolder: 'Select manifest template type' }
			);

			let command = 'manifest generate';
			if (template) {
				command += ` --template ${template}`;
			}

			await runWinappCommand(command, workspacePath);
		})
	);

	// Register winapp.manifestUpdateAssets command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.manifestUpdateAssets', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const imagePath = await selectFile('Select source image for assets', {
				'Images': ['png', 'jpg', 'jpeg', 'gif', 'bmp']
			});

			if (!imagePath) {
				vscode.window.showErrorMessage('An image file is required');
				return;
			}

			await runWinappCommand(`manifest update-assets "${imagePath}"`, workspacePath);
		})
	);

	// Register winapp.certGenerate command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.certGenerate', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const install = await vscode.window.showQuickPick(
				['Yes', 'No'],
				{ placeHolder: 'Install certificate after generation?' }
			);

			let command = 'cert generate';
			if (install === 'Yes') {
				command += ' --install';
			}

			await runWinappCommand(command, workspacePath);
		})
	);

	// Register winapp.certInstall command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.certInstall', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const certPath = await selectFile('Select certificate to install', {
				'Certificates': ['pfx', 'cer']
			});

			if (!certPath) {
				vscode.window.showErrorMessage('A certificate file is required');
				return;
			}

			await runWinappCommand(`cert install "${certPath}"`, workspacePath);
		})
	);

	// Register winapp.sign command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.sign', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

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

			await runWinappCommand(`sign "${filePath}" --cert "${certPath}"`, workspacePath);
		})
	);

	// Register winapp.tool command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.tool', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const toolName = await vscode.window.showQuickPick(
				['makeappx', 'signtool', 'mt', 'makepri'],
				{ placeHolder: 'Select Windows SDK tool' }
			);

			if (!toolName) return;

			const args = await vscode.window.showInputBox({
				prompt: `Enter arguments for ${toolName}`,
				placeHolder: 'e.g., --help'
			});

			let command = `tool ${toolName}`;
			if (args) {
				command += ` ${args}`;
			}

			await runWinappCommand(command, workspacePath);
		})
	);

	// Register winapp.getWinappPath command
	context.subscriptions.push(
		vscode.commands.registerCommand('winapp.getWinappPath', async () => {
			const workspacePath = getWorkspacePath();
			if (!workspacePath) return;

			const global = await vscode.window.showQuickPick(
				['Local (.winapp in workspace)', 'Global (shared cache)'],
				{ placeHolder: 'Which path to retrieve?' }
			);

			let command = 'get-winapp-path';
			if (global === 'Global (shared cache)') {
				command += ' --global';
			}

			await runWinappCommand(command, workspacePath);
		})
	);
}

/**
 * Parse the process ID from the winapp run output.
 * Expects the output to contain the process ID (e.g., just the number or in a known format).
 */
function parseProcessId(output: string): number | undefined {
	const trimmed = output.trim();

	// Try to parse the output directly as a number
	const directParse = parseInt(trimmed, 10);
	if (!isNaN(directParse) && directParse > 0) {
		return directParse;
	}

	// Try to find a process ID in common formats like "PID: 1234" or "Process ID: 1234"
	const patterns = [
		/(?:pid|process\s*id)\s*[=:]\s*(\d+)/i,
		/^(\d+)$/m,
		/started\s+.*?(?:pid|process)\s*[=:]\s*(\d+)/i,
	];

	for (const pattern of patterns) {
		const match = trimmed.match(pattern);
		if (match) {
			const pid = parseInt(match[1], 10);
			if (!isNaN(pid) && pid > 0) {
				return pid;
			}
		}
	}

	return undefined;
}

export function deactivate() {}
