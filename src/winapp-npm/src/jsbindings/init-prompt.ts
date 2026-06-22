// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

import * as fs from 'fs';
import * as path from 'path';
import * as readline from 'readline';

export type BindingsKind = 'yes' | 'no';

export interface BindingsPromptInputs {
  workspaceDir: string;
  /** Raw argv after the `init` command (excludes the `init` word). */
  argv: readonly string[];
  /** True for `init`; false for `restore`, which never prompts. */
  isInit: boolean;
  /** True when package.json already declares `winapp.jsBindings`. */
  existingJsBindings: boolean;
  /** True when SDK winmds exist, or codegen is deliberately deferred (`--config-only`). */
  sdksReady: boolean;
  /** True when init was explicitly asked to add JS bindings without prompting. */
  addJsBindings?: boolean;
  /** When true (e.g. --json), refuse to prompt and behave like non-TTY. */
  nonInteractive?: boolean;
}

export interface BindingsPromptOutcome {
  kind: BindingsKind;
  /** Reason the prompt was skipped; undefined when the prompt ran. */
  silentReason?: string;
  /** Set only for existing config: true resets, false preserves. */
  overwriteExistingConfig?: boolean;
}

const USE_DEFAULTS_FLAGS = new Set(['--use-defaults', '--no-prompt']);

export async function askBindingsKind(inputs: BindingsPromptInputs): Promise<BindingsPromptOutcome> {
  // Restore never re-prompts: respect whatever the workspace already declares.
  if (!inputs.isInit) {
    return {
      kind: inputs.existingJsBindings ? 'yes' : 'no',
      silentReason: inputs.existingJsBindings
        ? 'inferred Yes from existing package.json winapp.jsBindings.'
        : 'no existing winapp.jsBindings in package.json.',
      overwriteExistingConfig: inputs.existingJsBindings ? false : undefined,
    };
  }

  // Init re-runs mirror native overwrite prompts; default Yes preserves UX parity.
  if (inputs.existingJsBindings) {
    // Explicit --add-js-bindings means "I want JS bindings, non-interactively."
    // The config already exists, so preserve it — opt-in doesn't mean "reset".
    // Fast-path before any prompt path so CI/--add-js-bindings combinations
    // (which may have TTY but no --use-defaults) don't hang on confirmation.
    if (inputs.addJsBindings) {
      return { kind: 'yes', overwriteExistingConfig: false };
    }
    const useDefaults = inputs.argv.some((a) => USE_DEFAULTS_FLAGS.has(a));
    if (useDefaults) {
      return {
        kind: 'yes',
        silentReason: '--use-defaults — overwriting existing winapp.jsBindings with defaults.',
        overwriteExistingConfig: true,
      };
    }
    if (inputs.nonInteractive || !process.stdin.isTTY) {
      // Non-interactive runs preserve config unless --use-defaults opts into reset.
      return {
        kind: 'yes',
        silentReason: inputs.nonInteractive
          ? '--json — preserving existing winapp.jsBindings without prompting.'
          : 'non-TTY stdin — preserving existing winapp.jsBindings.',
        overwriteExistingConfig: false,
      };
    }
    const overwrite = await confirmationPrompt('package.json already has winapp.jsBindings. Overwrite?');
    return { kind: 'yes', overwriteExistingConfig: overwrite };
  }

  // Without package.json we have nowhere to write `winapp.jsBindings` or the runtime dep.
  if (!fs.existsSync(path.join(inputs.workspaceDir, 'package.json'))) {
    // Explicit opt-in must surface as a hard failure — CI scripts watching
    // exit codes should not interpret "couldn't even start" as success.
    // Prefix with ❌ so the printed message matches other hard-fail outputs
    // (logErrorAndExit forwards the message verbatim without adding a prefix).
    if (inputs.addJsBindings) {
      throw new Error('❌ Cannot generate JS bindings: no package.json in this workspace.');
    }
    // No opt-in + not an npm project → silently skip. The user didn't ask for
    // JS bindings; reminding them "this only applies to npm projects" is noise
    // when they're initializing a non-Node project (C++, Rust, Tauri, etc.).
    return { kind: 'no' };
  }

  // JS bindings need SDK winmds; if setup was skipped, defer the prompt until they exist.
  // When the user did not explicitly opt in via --add-js-bindings, skip silently — they
  // chose to skip SDK setup, so they have no signal they wanted JS bindings either.
  if (!inputs.sdksReady) {
    return {
      kind: 'no',
      silentReason: inputs.addJsBindings
        ? 'Windows SDKs were not set up during init, so --add-js-bindings could not run. ' +
          'Run `npx winapp restore` then `npx winapp node generate-bindings` to add them later.'
        : undefined,
    };
  }

  if (inputs.addJsBindings) {
    return { kind: 'yes', silentReason: '--add-js-bindings — opting in to JS bindings.' };
  }

  const useDefaults = inputs.argv.some((a) => USE_DEFAULTS_FLAGS.has(a));
  if (useDefaults) {
    return { kind: 'no', silentReason: '--use-defaults — skipping JS bindings.' };
  }

  if (inputs.nonInteractive || !process.stdin.isTTY || process.env.CI) {
    return {
      kind: 'no',
      silentReason: inputs.nonInteractive
        ? '--json — skipping JS bindings.'
        : !process.stdin.isTTY
          ? 'non-TTY stdin — skipping JS bindings.'
          : 'CI environment detected — skipping JS bindings.',
    };
  }

  const answer = await confirmationPrompt('Add JS bindings to call Windows App SDK APIs directly from JavaScript?');
  return { kind: answer ? 'yes' : 'no' };
}

// Used to fast-path `init --setup-sdks none`; native validates values.
export function parseSetupSdksArg(argv: readonly string[]): string | undefined {
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a === '--setup-sdks' && i + 1 < argv.length) {
      return argv[i + 1].trim().toLowerCase();
    }
    if (a.startsWith('--setup-sdks=')) {
      return a.substring('--setup-sdks='.length).trim().toLowerCase();
    }
  }
  return undefined;
}

/** Mirrors the native CLI confirmation prompt UX. */
async function confirmationPrompt(title: string, defaultYes: boolean = true): Promise<boolean> {
  const useColor = !!process.stdout.isTTY && !process.env.NO_COLOR;
  // Match Spectre.Console's ConfirmationPrompt palette for native/npm parity.
  const blue = (s: string) => (useColor ? `\x1b[34m${s}\x1b[39m` : s);
  const green = (s: string) => (useColor ? `\x1b[32m${s}\x1b[39m` : s);
  const underline = (s: string) => (useColor ? `\x1b[4m${s}\x1b[24m` : s);

  const choices = blue('[y/n]');
  const defaultHint = green(`(${defaultYes ? 'y' : 'n'})`);
  const livePrompt = `${title} ${choices} ${defaultHint}: `;

  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  try {
    // Retry to match Spectre's refusal of unrecognized answers.
    for (;;) {
      const raw = await question(rl, livePrompt);
      const trimmed = (raw ?? '').trim().toLowerCase();

      let result: boolean | null = null;
      if (trimmed === '') {
        result = defaultYes;
      } else if (trimmed === 'y' || trimmed === 'yes' || trimmed === 'true') {
        result = true;
      } else if (trimmed === 'n' || trimmed === 'no' || trimmed === 'false') {
        result = false;
      }

      if (result === null) {
        continue;
      }

      if (useColor) {
        // Rewrite the live prompt with the underlined answer.
        process.stdout.write('\x1b[1A\x1b[2K\r');
        process.stdout.write(`${title}: ${underline(result ? 'Yes' : 'No')}\n`);
      }

      return result;
    }
  } finally {
    rl.close();
  }
}

function question(rl: readline.Interface, prompt: string): Promise<string> {
  return new Promise((resolve) => {
    rl.question(prompt, (answer) => resolve(answer));
  });
}
