// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.
//
// Shared constants/utilities for the CLI dispatcher and JS-binding hooks.

// CLI name - change this to rebrand the tool
export const CLI_NAME = 'winapp';

export interface ParsedArgs {
  help?: boolean;
  name?: string;
  template?: string;
  verbose?: boolean;
  [key: string]: string | boolean | undefined;
}

export function parseArgs(args: string[], defaults: ParsedArgs = {}): ParsedArgs {
  const result: ParsedArgs = { ...defaults };

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];

    if (arg === '--help' || arg === '-h') {
      result.help = true;
    } else if (arg.startsWith('--')) {
      const key = arg.slice(2);
      const nextArg = args[i + 1];

      if (nextArg && !nextArg.startsWith('--')) {
        // Value argument
        result[key] = nextArg;
        i++; // Skip next arg
      } else {
        // Boolean flag
        result[key] = true;
      }
    }
  }

  return result;
}

export function logErrorAndExit(error: unknown): never {
  if (error instanceof Error && error.message.includes('winapp-cli exited with code')) {
    process.exit(1);
  }

  if (error instanceof Error && error.message) {
    console.error(error.message);
  } else {
    console.error(error);
  }

  process.exit(1);
}
