import * as fs from 'fs';
import * as fsp from 'fs/promises';
import * as path from 'path';

/**
 * Mirrors the C# DetectedProjectType enum from WinApp.Cli.
 */
export type DetectedProjectType = 'Tauri' | 'Electron' | 'Flutter' | '.NET' | 'Rust' | 'C++';

/**
 * Represents a project detected during directory scanning.
 * Mirrors the C# DetectedProject record from WinApp.Cli.
 */
export interface DetectedProject {
	type: DetectedProjectType;
	directory: string;
	displayPath: string;
	projectFileName: string;
}

/**
 * Returns a display string like ".NET project (./src/MyApp/MyApp.csproj)"
 */
export function getDisplayFilePath(project: DetectedProject): string {
	return project.displayPath === '.'
		? `./${project.projectFileName}`
		: `./${project.displayPath}/${project.projectFileName}`;
}

/**
 * Returns a human-readable label like ".NET project (./src/MyApp/MyApp.csproj)"
 */
export function getProjectLabel(project: DetectedProject): string {
	return `${project.type} project (${getDisplayFilePath(project)})`;
}

const SKIP_DIRS = new Set([
	'node_modules', '.git', 'bin', 'obj', 'debug', 'release',
	'.vs', '.vscode', '.idea', 'packages', 'dist', 'build', 'out',
	'target', '.winapp', 'artifacts', 'testresults',
	'__pycache__', '.gradle', '.dart_tool', '.pub-cache', '.nuget', '.cargo'
]);

/**
 * Detects a project at a single directory (does not recurse).
 * Mirrors ProjectDetectionService.DetectProject from WinApp.Cli.
 */
export function detectProjectAt(directory: string, searchRoot: string): DetectedProject | undefined {
	const displayPath = getRelativeDisplayPath(directory, searchRoot);

	// Tauri: check immediate subdirectories for tauri.conf.json
	const tauriConf = findTauriConfFile(directory);
	if (tauriConf) {
		return { type: 'Tauri', directory, displayPath, projectFileName: tauriConf };
	}

	// Electron: package.json with electron dependency
	if (isElectronProject(directory)) {
		return { type: 'Electron', directory, displayPath, projectFileName: 'package.json' };
	}

	// Flutter: pubspec.yaml
	if (fs.existsSync(path.join(directory, 'pubspec.yaml'))) {
		return { type: 'Flutter', directory, displayPath, projectFileName: 'pubspec.yaml' };
	}

	// .NET: *.csproj (only executable, non-test projects)
	const csprojName = findExecutableCsproj(directory);
	if (csprojName) {
		return { type: '.NET', directory, displayPath, projectFileName: csprojName };
	}

	// Rust: Cargo.toml
	if (fs.existsSync(path.join(directory, 'Cargo.toml'))) {
		return { type: 'Rust', directory, displayPath, projectFileName: 'Cargo.toml' };
	}

	// C++: CMakeLists.txt
	if (fs.existsSync(path.join(directory, 'CMakeLists.txt'))) {
		return { type: 'C++', directory, displayPath, projectFileName: 'CMakeLists.txt' };
	}

	return undefined;
}

/**
 * Performs a breadth-first search of the directory tree to find compatible projects.
 * Mirrors ProjectDetectionService.DetectProjectsAsync from WinApp.Cli.
 * Uses async I/O with periodic yielding to keep the UI responsive.
 */
export async function detectProjects(root: string, maxProjects: number = 10): Promise<DetectedProject[]> {
	const results: DetectedProject[] = [];
	const queue: string[] = [root];
	let iterations = 0;

	while (queue.length > 0 && results.length < maxProjects) {
		const current = queue.shift()!;
		const detected = detectProjectAt(current, root);
		if (detected) {
			results.push(detected);
			// Don't recurse into detected project directories
			continue;
		}

		// Enqueue child directories (skip known non-project dirs)
		try {
			const entries = await fsp.readdir(current, { withFileTypes: true });
			for (const entry of entries) {
				if (!entry.isDirectory() && !entry.isSymbolicLink()) { continue; }
				if (entry.name.startsWith('.') && entry.name !== '.') { continue; }
				if (SKIP_DIRS.has(entry.name.toLowerCase())) { continue; }
				const fullPath = path.join(current, entry.name);
				// Skip symlinks and junctions (reparse points)
				if (entry.isSymbolicLink()) { continue; }
				try {
					const stat = await fsp.stat(fullPath);
					if (!stat.isDirectory()) { continue; }
				} catch {
					continue;
				}
				queue.push(fullPath);
			}
		} catch {
			// Skip directories we can't read
		}

		// Yield to the event loop periodically to keep the UI responsive
		if (++iterations % 50 === 0) {
			await new Promise(resolve => setTimeout(resolve, 0));
		}
	}

	return results;
}

function getRelativeDisplayPath(directory: string, searchRoot: string): string {
	const relative = path.relative(searchRoot, directory);
	if (!relative || relative === '.') {
		return '.';
	}
	return relative.replace(/\\/g, '/');
}

function findTauriConfFile(directory: string): string | undefined {
	try {
		const entries = fs.readdirSync(directory, { withFileTypes: true });
		for (const entry of entries) {
			if (!entry.isDirectory()) { continue; }
			if (entry.name.startsWith('.')) { continue; }
			const subDir = path.join(directory, entry.name);
			try {
				const stat = fs.lstatSync(subDir);
				if (stat.isSymbolicLink()) { continue; }
			} catch {
				continue;
			}
			if (fs.existsSync(path.join(subDir, 'tauri.conf.json'))) {
				return `${entry.name}/tauri.conf.json`;
			}
		}
	} catch {
		// Skip if we can't read
	}
	return undefined;
}

function isElectronProject(directory: string): boolean {
	const packageJsonPath = path.join(directory, 'package.json');
	if (!fs.existsSync(packageJsonPath)) { return false; }
	try {
		const content = fs.readFileSync(packageJsonPath, 'utf-8');
		const pkg = JSON.parse(content);
		const deps = { ...pkg.dependencies, ...pkg.devDependencies };
		return 'electron' in deps;
	} catch {
		return false;
	}
}

function findExecutableCsproj(directory: string): string | undefined {
	try {
		const entries = fs.readdirSync(directory);
		for (const entry of entries) {
			if (!entry.endsWith('.csproj')) { continue; }
			const filePath = path.join(directory, entry);
			try {
				const content = fs.readFileSync(filePath, 'utf-8');
				if (isExecutableCsproj(content)) {
					return entry;
				}
			} catch {
				continue;
			}
		}
	} catch {
		// Skip if we can't read
	}
	return undefined;
}

/**
 * Parses csproj XML content to determine if it's an executable, non-test project.
 * Simplified heuristic inspired by the CLI's IsExecutableProject logic — uses regex
 * to match the first <OutputType> and <IsTestProject> elements. Does not handle
 * multiple/conditional PropertyGroups or values inside XML comments.
 */
function isExecutableCsproj(content: string): boolean {
	// Extract OutputType value from PropertyGroup elements
	const outputTypeMatch = content.match(/<OutputType>\s*(.*?)\s*<\/OutputType>/i);
	if (!outputTypeMatch) {
		return false;
	}
	const outputType = outputTypeMatch[1].toLowerCase();
	if (outputType !== 'exe' && outputType !== 'winexe') {
		return false;
	}

	// Check IsTestProject property
	const isTestMatch = content.match(/<IsTestProject>\s*(.*?)\s*<\/IsTestProject>/i);
	if (isTestMatch && isTestMatch[1].toLowerCase() === 'true') {
		return false;
	}

	return true;
}
