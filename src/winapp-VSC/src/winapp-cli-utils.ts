import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

export const WINAPP_CLI_CALLER_VALUE = 'vscode-extension';

/**
 * Get the path to the bundled winapp CLI executable.
 * Looks in the extension's bin/ directory first (by architecture),
 * then falls back to development paths and the system PATH.
 */
export function getWinappCliPath(extensionPath: string): string {
	const arch = os.arch() === 'arm64' ? 'win-arm64' : 'win-x64';

	const onDiskPaths = [
		// Bundled in extension (production)
		path.join(extensionPath, 'bin', arch, 'winapp.exe'),
		// Development builds (when running from source)
		path.join(extensionPath, '..', 'winapp-CLI', 'WinApp.Cli', 'bin', 'Debug', 'net10.0-windows', arch, 'winapp.exe'),
		path.join(extensionPath, '..', 'winapp-CLI', 'WinApp.Cli', 'bin', 'Release', 'net10.0-windows', arch, 'winapp.exe'),
	];

	// Return the first on-disk path that exists, otherwise fall back to 'winapp' on the system PATH
	return onDiskPaths.find((p) => fs.existsSync(p)) || 'winapp';
}
