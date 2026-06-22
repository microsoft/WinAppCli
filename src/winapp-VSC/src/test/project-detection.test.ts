import * as assert from 'assert';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
	detectProjectAt,
	detectProjects,
	getProjectLabel,
	getDisplayFilePath,
	DetectedProject
} from '../project-detection';

/**
 * Creates a temporary directory for test fixtures.
 */
function createTempDir(): string {
	return fs.mkdtempSync(path.join(os.tmpdir(), 'winapp-test-'));
}

/**
 * Recursively removes a directory.
 */
function removeTempDir(dir: string): void {
	fs.rmSync(dir, { recursive: true, force: true });
}

/**
 * Creates a file with the given content, creating parent directories as needed.
 */
function createFile(filePath: string, content: string = ''): void {
	fs.mkdirSync(path.dirname(filePath), { recursive: true });
	fs.writeFileSync(filePath, content);
}

describe('project-detection', () => {
	let tempDir: string;

	beforeEach(() => {
		tempDir = createTempDir();
	});

	afterEach(() => {
		removeTempDir(tempDir);
	});

	describe('detectProjectAt', () => {
		it('detects a .NET executable project', () => {
			createFile(path.join(tempDir, 'MyApp.csproj'), `
				<Project Sdk="Microsoft.NET.Sdk">
					<PropertyGroup>
						<OutputType>Exe</OutputType>
						<TargetFramework>net8.0</TargetFramework>
					</PropertyGroup>
				</Project>
			`);

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, '.NET');
			assert.strictEqual(result.projectFileName, 'MyApp.csproj');
		});

		it('detects a WinExe .NET project', () => {
			createFile(path.join(tempDir, 'WpfApp.csproj'), `
				<Project Sdk="Microsoft.NET.Sdk">
					<PropertyGroup>
						<OutputType>WinExe</OutputType>
					</PropertyGroup>
				</Project>
			`);

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, '.NET');
		});

		it('does not detect a .NET test project', () => {
			createFile(path.join(tempDir, 'MyApp.Tests.csproj'), `
				<Project Sdk="Microsoft.NET.Sdk">
					<PropertyGroup>
						<OutputType>Exe</OutputType>
						<IsTestProject>true</IsTestProject>
					</PropertyGroup>
				</Project>
			`);

			const result = detectProjectAt(tempDir, tempDir);
			assert.strictEqual(result, undefined);
		});

		it('does not detect a .NET library project', () => {
			createFile(path.join(tempDir, 'MyLib.csproj'), `
				<Project Sdk="Microsoft.NET.Sdk">
					<PropertyGroup>
						<TargetFramework>net8.0</TargetFramework>
					</PropertyGroup>
				</Project>
			`);

			const result = detectProjectAt(tempDir, tempDir);
			assert.strictEqual(result, undefined);
		});

		it('detects an Electron project', () => {
			createFile(path.join(tempDir, 'package.json'), JSON.stringify({
				name: 'my-electron-app',
				dependencies: { electron: '^28.0.0' }
			}));

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, 'Electron');
			assert.strictEqual(result.projectFileName, 'package.json');
		});

		it('does not detect a non-Electron package.json', () => {
			createFile(path.join(tempDir, 'package.json'), JSON.stringify({
				name: 'my-lib',
				dependencies: { express: '^4.0.0' }
			}));

			const result = detectProjectAt(tempDir, tempDir);
			assert.strictEqual(result, undefined);
		});

		it('detects a Flutter project', () => {
			createFile(path.join(tempDir, 'pubspec.yaml'), 'name: my_app\n');

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, 'Flutter');
			assert.strictEqual(result.projectFileName, 'pubspec.yaml');
		});

		it('detects a Rust project', () => {
			createFile(path.join(tempDir, 'Cargo.toml'), '[package]\nname = "my_app"\n');

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, 'Rust');
			assert.strictEqual(result.projectFileName, 'Cargo.toml');
		});

		it('detects a C++ project', () => {
			createFile(path.join(tempDir, 'CMakeLists.txt'), 'cmake_minimum_required(VERSION 3.20)\n');

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, 'C++');
			assert.strictEqual(result.projectFileName, 'CMakeLists.txt');
		});

		it('detects a Tauri project', () => {
			createFile(path.join(tempDir, 'src-tauri', 'tauri.conf.json'), '{}');

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, 'Tauri');
			assert.strictEqual(result.projectFileName, 'src-tauri/tauri.conf.json');
		});

		it('returns undefined for empty directory', () => {
			const result = detectProjectAt(tempDir, tempDir);
			assert.strictEqual(result, undefined);
		});

		it('sets displayPath to "." for root directory', () => {
			createFile(path.join(tempDir, 'Cargo.toml'), '[package]\n');

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.displayPath, '.');
		});

		it('sets relative displayPath for nested directory', () => {
			const nested = path.join(tempDir, 'apps', 'my-app');
			createFile(path.join(nested, 'Cargo.toml'), '[package]\n');

			const result = detectProjectAt(nested, tempDir);
			assert.ok(result);
			assert.strictEqual(result.displayPath, 'apps/my-app');
		});

		it('prioritizes Tauri over Electron when both markers present', () => {
			createFile(path.join(tempDir, 'package.json'), JSON.stringify({
				dependencies: { electron: '^28.0.0' }
			}));
			createFile(path.join(tempDir, 'src-tauri', 'tauri.conf.json'), '{}');

			const result = detectProjectAt(tempDir, tempDir);
			assert.ok(result);
			assert.strictEqual(result.type, 'Tauri');
		});
	});

	describe('detectProjects', () => {
		it('finds multiple projects in different subdirectories', async () => {
			createFile(path.join(tempDir, 'app1', 'Cargo.toml'), '[package]\n');
			createFile(path.join(tempDir, 'app2', 'CMakeLists.txt'), 'cmake_minimum_required(VERSION 3.20)\n');
			createFile(path.join(tempDir, 'app3', 'pubspec.yaml'), 'name: app3\n');

			const results = await detectProjects(tempDir);
			assert.strictEqual(results.length, 3);
			const types = results.map(r => r.type).sort();
			assert.deepStrictEqual(types, ['C++', 'Flutter', 'Rust']);
		});

		it('respects maxProjects limit', async () => {
			for (let i = 0; i < 5; i++) {
				createFile(path.join(tempDir, `app${i}`, 'Cargo.toml'), '[package]\n');
			}

			const results = await detectProjects(tempDir, 3);
			assert.strictEqual(results.length, 3);
		});

		it('skips node_modules directory', async () => {
			createFile(path.join(tempDir, 'node_modules', 'some-pkg', 'Cargo.toml'), '[package]\n');
			createFile(path.join(tempDir, 'src', 'Cargo.toml'), '[package]\n');

			const results = await detectProjects(tempDir);
			assert.strictEqual(results.length, 1);
			assert.strictEqual(results[0].type, 'Rust');
			assert.ok(results[0].directory.includes('src'));
		});

		it('skips other ignored directories', async () => {
			const ignoredDirs = ['bin', 'obj', 'target', 'dist', 'build', '.git'];
			for (const dir of ignoredDirs) {
				createFile(path.join(tempDir, dir, 'Cargo.toml'), '[package]\n');
			}
			createFile(path.join(tempDir, 'src', 'Cargo.toml'), '[package]\n');

			const results = await detectProjects(tempDir);
			assert.strictEqual(results.length, 1);
			assert.ok(results[0].directory.includes('src'));
		});

		it('skips hidden directories', async () => {
			createFile(path.join(tempDir, '.hidden', 'Cargo.toml'), '[package]\n');
			createFile(path.join(tempDir, 'visible', 'Cargo.toml'), '[package]\n');

			const results = await detectProjects(tempDir);
			assert.strictEqual(results.length, 1);
			assert.ok(results[0].directory.includes('visible'));
		});

		it('does not recurse into detected project directories', async () => {
			// Parent is a Rust project
			createFile(path.join(tempDir, 'app', 'Cargo.toml'), '[package]\n');
			// Nested CMake inside the Rust project should not be found
			createFile(path.join(tempDir, 'app', 'subdir', 'CMakeLists.txt'), 'cmake_minimum_required(VERSION 3.20)\n');

			const results = await detectProjects(tempDir);
			assert.strictEqual(results.length, 1);
			assert.strictEqual(results[0].type, 'Rust');
		});

		it('detects project at root when present', async () => {
			createFile(path.join(tempDir, 'Cargo.toml'), '[package]\n');
			createFile(path.join(tempDir, 'subdir', 'CMakeLists.txt'), 'cmake_minimum_required(VERSION 3.20)\n');

			const results = await detectProjects(tempDir);
			// Root project found, so we don't recurse
			assert.strictEqual(results.length, 1);
			assert.strictEqual(results[0].type, 'Rust');
			assert.strictEqual(results[0].displayPath, '.');
		});

		it('returns empty array when no projects exist', async () => {
			createFile(path.join(tempDir, 'readme.md'), '# Hello\n');

			const results = await detectProjects(tempDir);
			assert.strictEqual(results.length, 0);
		});
	});

	describe('getDisplayFilePath', () => {
		it('formats root project path correctly', () => {
			const project: DetectedProject = {
				type: 'Rust',
				directory: '/some/path',
				displayPath: '.',
				projectFileName: 'Cargo.toml'
			};
			assert.strictEqual(getDisplayFilePath(project), './Cargo.toml');
		});

		it('formats nested project path correctly', () => {
			const project: DetectedProject = {
				type: '.NET',
				directory: '/some/path/apps/myapp',
				displayPath: 'apps/myapp',
				projectFileName: 'MyApp.csproj'
			};
			assert.strictEqual(getDisplayFilePath(project), './apps/myapp/MyApp.csproj');
		});
	});

	describe('getProjectLabel', () => {
		it('formats label correctly', () => {
			const project: DetectedProject = {
				type: '.NET',
				directory: '/some/path',
				displayPath: 'src/app',
				projectFileName: 'App.csproj'
			};
			assert.strictEqual(getProjectLabel(project), '.NET project (./src/app/App.csproj)');
		});
	});
});
