// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Decides whether the user wants JS/TypeScript bindings in addition to the
// C++ projections that the native CLI always sets up.
//
// Decision tree:
//   * `winapp restore` (not `init`) → infer from package.json; never prompt.
//   * Existing `"winapp.jsBindings"` namespace in package.json → infer Yes
//     (we don't second-guess the user's prior choice).
//   * .NET project (any *.csproj / *.fsproj / *.vbproj in cwd) → silent No;
//     dynwinrt bindings don't target .NET (CsWinRT already provides
//     projections).
//   * `--use-defaults` / `-y` / `--yes` → silent Yes (npm user opted in).
//   * Non-TTY stdin → silent Yes (scripted npm invocation, same default).
//   * Otherwise → prompt `Add JS/TypeScript bindings? [Y/n]`.
//
// Note: `--setup-sdks none` is fast-pathed before this function is called
// (cli.ts forwards straight to the native CLI), so we never see it here.

import * as fs from 'fs';
import * as path from 'path';
import * as readline from 'readline';

export type BindingsKind = 'yes' | 'no';

export interface BindingsPromptInputs {
  workspaceDir: string;
  /** Raw argv after the `init` command (excludes the `init` word). */
  argv: readonly string[];
  /** True for `init`. False for `restore` (which never prompts). */
  isInit: boolean;
  /** True when package.json already declares `winapp.jsBindings`. */
  existingJsBindings: boolean;
}

export interface BindingsPromptOutcome {
  kind: BindingsKind;
  /** Reason the prompt was skipped (silent decision). undefined when the prompt actually ran. */
  silentReason?: string;
  /**
   * Set when `existingJsBindings` was true at prompt time. true = user (or
   * silent path) elected to overwrite the existing config with fresh defaults;
   * false = preserve the user's existing config as-is. undefined when no
   * existing config existed.
   */
  overwriteExistingConfig?: boolean;
}

const USE_DEFAULTS_FLAGS = new Set(['--use-defaults', '-y', '--yes']);

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

  // Existing jsBindings + init re-run → ask whether to overwrite, mirroring
  // the native CLI's `winapp.yaml exists with pinned versions. Overwrite?` and
  // `<manifest> already exists. Overwrite?` prompts. Default Yes matches
  // those native prompts; users can answer N to preserve customizations.
  if (inputs.existingJsBindings) {
    const isDotNet = detectDotNetProject(inputs.workspaceDir);
    if (isDotNet) {
      // Edge: someone added winapp.jsBindings to a .NET project and is now
      // re-running init. Honor .NET classification and silent-preserve.
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
      // Scripted invocation — preserve existing config (safer default for
      // non-interactive runs; --use-defaults is the explicit opt-in to reset).
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
    return {
      kind: 'no',
      silentReason:
        '.NET project detected — JS bindings target Node/Electron via dynwinrt; .NET projects already get WinRT via CsWinRT.',
    };
  }

  // JS bindings only apply to Node/Electron projects, which always have a
  // package.json. Skip silently when one isn't present so we don't ask a
  // question whose answer can't be honored (we'd need somewhere to write
  // `winapp.jsBindings` and to inject the runtime dep).
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

// Look for `--setup-sdks <mode>` or `--setup-sdks=<mode>` in the argv.
// Mirrors the native option exactly; we don't validate the value beyond the
// "none" check (native will reject invalid values).
//
// Exported so cli.ts can fast-path `init --setup-sdks none` straight to the
// native CLI without invoking the bindings prompt (parity with the
// pre-wrapper UX where --setup-sdks none was a no-op for JS bindings).
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

/**
 * Mirrors Spectre.Console's `ConfirmationPrompt` rendering used by the native
 * CLI: live prompt shows `{title} [y/n] (y):` with the hint in dim grey,
 * and after the user answers the line is rewritten as `{title}: <Yes|No>`
 * with the answer underlined. Keeps init UX consistent across native and
 * npm-wrapper prompts.
 */
async function confirmationPrompt(title: string, defaultYes: boolean = true): Promise<boolean> {
  const useColor = !!process.stdout.isTTY && !process.env.NO_COLOR;
  // Match Spectre.Console's default ConfirmationPrompt palette:
  //   * Choices `[y/n]` → blue (ChoicesStyle default)
  //   * Default value `(y)` → green (DefaultValueStyle default)
  //   * Post-answer value → underline (matches our C# rewrite path)
  const blue = (s: string) => (useColor ? `\x1b[34m${s}\x1b[39m` : s);
  const green = (s: string) => (useColor ? `\x1b[32m${s}\x1b[39m` : s);
  const underline = (s: string) => (useColor ? `\x1b[4m${s}\x1b[24m` : s);

  const choices = blue('[y/n]');
  const defaultHint = green(`(${defaultYes ? 'y' : 'n'})`);
  const livePrompt = `${title} ${choices} ${defaultHint}: `;

  const rl = readline.createInterface({ input: process.stdin, output: process.stdout });
  try {
    // Loop until we get a recognized answer (or an empty answer, which uses
    // the default). Matches Spectre's behavior of refusing garbage input.
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
        // Invalid — re-prompt (Spectre prints validation error; we keep it terse).
        continue;
      }

      if (useColor) {
        // Move cursor up one line (over the line we just wrote), clear it,
        // then rewrite the prompt with the underlined answer.
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
