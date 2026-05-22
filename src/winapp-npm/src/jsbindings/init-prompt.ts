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
}

export interface BindingsPromptOutcome {
  kind: BindingsKind;
  /** Reason the prompt was skipped; undefined when the prompt ran. */
  silentReason?: string;
  /** Set only for existing config: true resets, false preserves. */
  overwriteExistingConfig?: boolean;
}

const USE_DEFAULTS_FLAGS = new Set(['--use-defaults', '--no-prompt', '-y', '--yes']);

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
    const isDotNet = detectDotNetProject(inputs.workspaceDir);
    if (isDotNet) {
      return {
        kind: 'yes',
        silentReason: '.NET project detected — preserving existing winapp.jsBindings without prompting.',
        overwriteExistingConfig: false,
      };
    }
    const useDefaults = inputs.argv.some((a) => USE_DEFAULTS_FLAGS.has(a));
    if (useDefaults) {
      return {
        kind: 'yes',
        silentReason: '--use-defaults — overwriting existing winapp.jsBindings with defaults.',
        overwriteExistingConfig: true,
      };
    }
    if (!process.stdin.isTTY) {
      // Non-interactive runs preserve existing config unless --use-defaults opts into reset.
      return {
        kind: 'yes',
        silentReason: 'non-TTY stdin — preserving existing winapp.jsBindings.',
        overwriteExistingConfig: false,
      };
    }
    const overwrite = await confirmationPrompt('package.json already has winapp.jsBindings. Overwrite?');
    return { kind: 'yes', overwriteExistingConfig: overwrite };
  }

  const isDotNet = detectDotNetProject(inputs.workspaceDir);
  if (isDotNet) {
    // dynwinrt bindings target Node/Electron; .NET already gets WinRT via CsWinRT.
    return {
      kind: 'no',
      silentReason:
        '.NET project detected — JS bindings target Node/Electron via dynwinrt; .NET projects already get WinRT via CsWinRT.',
    };
  }

  // Without package.json we have nowhere to write `winapp.jsBindings` or the runtime dep.
  if (!fs.existsSync(path.join(inputs.workspaceDir, 'package.json'))) {
    return {
      kind: 'no',
      silentReason: 'no package.json in this workspace — JS bindings only apply to npm/Node projects.',
    };
  }

  const useDefaults = inputs.argv.some((a) => USE_DEFAULTS_FLAGS.has(a));
  if (useDefaults) {
    return { kind: 'yes', silentReason: '--use-defaults — opting in to JS bindings.' };
  }

  if (!process.stdin.isTTY) {
    return { kind: 'yes', silentReason: 'non-TTY stdin — defaulting to Yes.' };
  }

  const answer = await confirmationPrompt('Add JS/TypeScript bindings to this project?');
  return { kind: answer ? 'yes' : 'no' };
}

function detectDotNetProject(workspaceDir: string): boolean {
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(workspaceDir, { withFileTypes: true });
  } catch {
    return false;
  }
  for (const e of entries) {
    if (!e.isFile()) {
      continue;
    }
    const ext = path.extname(e.name).toLowerCase();
    if (ext === '.csproj' || ext === '.fsproj' || ext === '.vbproj') {
      return true;
    }
  }
  return false;
}

// cli.ts uses this to fast-path `init --setup-sdks none`; native validates values.
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
    // Keep retrying to match Spectre's refusal of unrecognized answers.
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
