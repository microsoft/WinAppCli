import * as esbuild from 'esbuild';

const production = process.argv.includes('--production');
const watch = process.argv.includes('--watch');

/** @type {import('esbuild').BuildOptions} */
const buildOptions = {
	entryPoints: ['src/extension.ts'],
	bundle: true,
	outfile: 'dist/extension.js',
	external: ['vscode'],
	format: 'cjs',
	platform: 'node',
	target: 'ES2022',
	sourcemap: !production,
	minify: production,
	sourcesContent: false,
};

/**
 * Plugin that logs build start/end so the VS Code problem matcher
 * can detect when the background watch task is ready.
 */
const watchPlugin = {
	name: 'watch-plugin',
	setup(build) {
		build.onStart(() => {
			console.log('[esbuild] watching for changes...');
		});
		build.onEnd((result) => {
			if (result.errors.length === 0) {
				console.log('[esbuild] build finished');
			}
		});
	},
};

async function main() {
	if (watch) {
		const ctx = await esbuild.context({ ...buildOptions, plugins: [watchPlugin] });
		await ctx.watch();
	} else {
		await esbuild.build(buildOptions);
		console.log('[esbuild] build complete');
	}
}

main().catch((e) => {
	console.error(e);
	process.exit(1);
});
